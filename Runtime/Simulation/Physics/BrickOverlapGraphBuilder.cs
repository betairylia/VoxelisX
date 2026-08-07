using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Profiling;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    /// <summary>
    /// Builds and double-buffer-publishes the post-physics brick-overlap graph from the raw
    /// candidate streams of one simulation step.
    ///
    /// Pipeline (all Burst jobs, mirrored from the physics scheduler's parallel radix sort):
    /// flatten+map+double → histogram by source rank → prefix sum → scatter → per-bucket sort
    /// → per-bucket count → per-bucket emit. Deduplication is bucket-local because duplicates
    /// of a directed record always share the source body. Small inputs (at most
    /// <see cref="serialBuildThreshold"/> directed records) run one serial job instead and
    /// produce identical output.
    ///
    /// Build must run while the step's bodyIndexToGuid mapping is still valid, after the
    /// step's FinalExecutionHandle completed and before the next simulation reset.
    /// </summary>
    public sealed class BrickOverlapGraphBuilder : IDisposable
    {
        /// <summary>
        /// Directed-record count (2 × raw candidates) at or below which the build runs as
        /// one serial job. Zero forces the parallel pipeline.
        /// </summary>
        public int serialBuildThreshold = 2048;

        sealed class GraphBuffer : IDisposable
        {
            public NativeList<BrickOverlapPair> Pairs;
            public NativeList<BrickOverlapKey> Neighbors;
            public NativeList<BrickOverlapSourceRange> Ranges;
            public NativeParallelHashMap<BrickOverlapKey, int> RangeLookup;

            public GraphBuffer()
            {
                Pairs = new NativeList<BrickOverlapPair>(64, Allocator.Persistent);
                Neighbors = new NativeList<BrickOverlapKey>(128, Allocator.Persistent);
                Ranges = new NativeList<BrickOverlapSourceRange>(64, Allocator.Persistent);
                RangeLookup = new NativeParallelHashMap<BrickOverlapKey, int>(128, Allocator.Persistent);
            }

            public void Clear()
            {
                Pairs.Clear();
                Neighbors.Clear();
                Ranges.Clear();
                RangeLookup.Clear();
            }

            public void Dispose()
            {
                if (Pairs.IsCreated) Pairs.Dispose();
                if (Neighbors.IsCreated) Neighbors.Dispose();
                if (Ranges.IsCreated) Ranges.Dispose();
                if (RangeLookup.IsCreated) RangeLookup.Dispose();
            }
        }

        readonly GraphBuffer[] m_Buffers;
        int m_Active;
        int m_Version;

        // Persistent, grow-only scratch reused across steps (no per-pair managed allocations
        // on warm runs).
        NativeList<int> m_StreamOffsets;
        NativeList<DirectedBrickOverlapRecord> m_Directed;
        NativeList<DirectedBrickOverlapRecord> m_Sorted;
        NativeList<int> m_Histogram;
        NativeList<int> m_NeighborCounts;
        NativeList<int> m_SourceCounts;
        NativeList<int> m_PairCounts;
        NativeList<int> m_RankOfBody;
        NativeList<Guid128> m_RankToGuid;

        // Stand-in for an uncreated candidate stream (for example the dynamic stream in a
        // world with zero dynamic bodies): jobs cannot be scheduled with a default
        // NativeStream.Reader field, so an always-created empty stream substitutes. Its
        // ForEachCount is never used; the real per-stream counts stay zero.
        NativeStream m_EmptyStream;

        /// <summary> Counters of the most recent build. </summary>
        public BrickOverlapGraphStats LastBuildStats { get; private set; }

        static readonly ProfilerMarker s_BuildMarker = new ProfilerMarker("BrickOverlapGraph.Build");
        static readonly ProfilerMarker s_SortMarker = new ProfilerMarker("BrickOverlapGraph.FlattenSortCount");
        static readonly ProfilerMarker s_EmitMarker = new ProfilerMarker("BrickOverlapGraph.Emit");

        public BrickOverlapGraphBuilder()
        {
            m_Buffers = new[] { new GraphBuffer(), new GraphBuffer() };
            m_StreamOffsets = new NativeList<int>(16, Allocator.Persistent);
            m_Directed = new NativeList<DirectedBrickOverlapRecord>(256, Allocator.Persistent);
            m_Sorted = new NativeList<DirectedBrickOverlapRecord>(256, Allocator.Persistent);
            m_Histogram = new NativeList<int>(64, Allocator.Persistent);
            m_NeighborCounts = new NativeList<int>(64, Allocator.Persistent);
            m_SourceCounts = new NativeList<int>(64, Allocator.Persistent);
            m_PairCounts = new NativeList<int>(64, Allocator.Persistent);
            m_RankOfBody = new NativeList<int>(64, Allocator.Persistent);
            m_RankToGuid = new NativeList<Guid128>(64, Allocator.Persistent);
            m_EmptyStream = new NativeStream(1, Allocator.Persistent);
        }

        /// <summary>
        /// Read-only view of the currently published graph. Default (empty, IsCreated false)
        /// before the first publish. Re-fetch each step; the view aliases double-buffered
        /// storage that is reused two publishes later.
        /// </summary>
        public BrickOverlapGraph Graph
        {
            get
            {
                if (m_Version == 0)
                {
                    return default;
                }
                GraphBuffer active = m_Buffers[m_Active];
                return new BrickOverlapGraph(
                    m_Version,
                    active.Pairs.AsArray(),
                    active.Neighbors.AsArray(),
                    active.Ranges.AsArray(),
                    active.RangeLookup.AsReadOnly());
            }
        }

        /// <summary>
        /// Builds the graph from this step's candidates, then publishes it by swapping the
        /// double buffer and advancing the version. An empty input publishes an empty graph
        /// (it replaces, not preserves, the previous one). Completes all internal jobs
        /// before returning.
        /// </summary>
        public void BuildAndPublish(VoxelBrickOverlapCandidates candidates, NativeArray<Guid128> bodyIndexToGuid)
        {
            using (s_BuildMarker.Auto())
            {
                long startTicks = Stopwatch.GetTimestamp();

                GraphBuffer target = m_Buffers[1 - m_Active];
                int numBodies = bodyIndexToGuid.IsCreated ? bodyIndexToGuid.Length : 0;
                int rawCandidates = candidates.Count();

                var stats = new BrickOverlapGraphStats
                {
                    RawCandidates = rawCandidates,
                    NumBodies = numBodies
                };

                if (rawCandidates == 0 || numBodies == 0)
                {
                    target.Clear();
                    Publish(stats, startTicks);
                    return;
                }

                NativeStream dynamicStream = candidates.DynamicStream;
                NativeStream staticStream = candidates.StaticStream;
                int dynamicForEach = dynamicStream.IsCreated ? dynamicStream.ForEachCount : 0;
                int staticForEach = staticStream.IsCreated ? staticStream.ForEachCount : 0;
                NativeStream.Reader dynamicReader = (dynamicStream.IsCreated ? dynamicStream : m_EmptyStream).AsReader();
                NativeStream.Reader staticReader = (staticStream.IsCreated ? staticStream : m_EmptyStream).AsReader();

                m_RankOfBody.ResizeUninitialized(numBodies);
                m_RankToGuid.ResizeUninitialized(numBodies);
                JobHandle rankHandle = new BuildBodyRankJob
                {
                    BodyIndexToGuid = bodyIndexToGuid,
                    RankOfBody = m_RankOfBody.AsArray(),
                    RankToGuid = m_RankToGuid.AsArray()
                }.Schedule();

                int totalDirected = rawCandidates * 2;

                if (totalDirected <= serialBuildThreshold)
                {
                    BuildSerial(target, rankHandle, dynamicReader, staticReader,
                        dynamicForEach, staticForEach, numBodies, totalDirected, ref stats);
                    Publish(stats, startTicks);
                    return;
                }

                BuildParallel(target, rankHandle, dynamicReader, staticReader,
                    dynamicForEach, staticForEach, numBodies, totalDirected, ref stats);
                Publish(stats, startTicks);
            }
        }

        void BuildSerial(GraphBuffer target, JobHandle rankHandle,
            NativeStream.Reader dynamicReader, NativeStream.Reader staticReader,
            int dynamicForEach, int staticForEach, int numBodies, int totalDirected,
            ref BrickOverlapGraphStats stats)
        {
            target.Clear();
            // At most one range entry per directed record; bounded by the serial threshold.
            EnsureHashCapacity(target, totalDirected);
            m_Directed.Clear();

            JobHandle serialHandle = new SerialBuildJob
            {
                DynamicReader = dynamicReader,
                StaticReader = staticReader,
                DynamicForEachCount = dynamicForEach,
                StaticForEachCount = staticForEach,
                NumBodies = numBodies,
                RankOfBody = m_RankOfBody.AsArray(),
                RankToGuid = m_RankToGuid.AsArray(),
                Scratch = m_Directed,
                Neighbors = target.Neighbors,
                Ranges = target.Ranges,
                Pairs = target.Pairs,
                RangeLookup = target.RangeLookup
            }.Schedule(rankHandle);

            using (s_SortMarker.Auto())
            {
                serialHandle.Complete();
            }

            stats.UsedSerialPath = true;
            stats.UniquePairs = target.Pairs.Length;
            stats.ActiveSourceBricks = target.Ranges.Length;
        }

        void BuildParallel(GraphBuffer target, JobHandle rankHandle,
            NativeStream.Reader dynamicReader, NativeStream.Reader staticReader,
            int dynamicForEach, int staticForEach, int numBodies, int totalDirected,
            ref BrickOverlapGraphStats stats)
        {
            int forEachTotal = dynamicForEach + staticForEach;

            m_StreamOffsets.ResizeUninitialized(forEachTotal);
            m_Directed.ResizeUninitialized(totalDirected);
            m_Sorted.ResizeUninitialized(totalDirected);
            m_Histogram.ResizeUninitialized(numBodies + 1);
            m_NeighborCounts.ResizeUninitialized(numBodies);
            m_SourceCounts.ResizeUninitialized(numBodies);
            m_PairCounts.ResizeUninitialized(numBodies);

            for (int i = 0; i < m_Histogram.Length; i++)
            {
                m_Histogram[i] = 0;
            }

            JobHandle offsetsHandle = new ComputeStreamOffsetsJob
            {
                DynamicReader = dynamicReader,
                StaticReader = staticReader,
                DynamicForEachCount = dynamicForEach,
                StaticForEachCount = staticForEach,
                Offsets = m_StreamOffsets.AsArray()
            }.Schedule();

            JobHandle flattenHandle = new FlattenCandidatesJob
            {
                DynamicReader = dynamicReader,
                StaticReader = staticReader,
                DynamicForEachCount = dynamicForEach,
                NumBodies = numBodies,
                Offsets = m_StreamOffsets.AsArray(),
                RankOfBody = m_RankOfBody.AsArray(),
                Directed = m_Directed.AsArray()
            }.Schedule(forEachTotal, 1, JobHandle.CombineDependencies(offsetsHandle, rankHandle));

            int numWorkers = math.max(1, JobsUtility.JobWorkerCount);

            JobHandle histogramHandle = new RankHistogramJob
            {
                Directed = m_Directed.AsArray(),
                Histogram = m_Histogram.AsArray(),
                NumWorkers = numWorkers
            }.Schedule(numWorkers, 1, flattenHandle);

            JobHandle prefixHandle = new BucketPrefixSumJob
            {
                Histogram = m_Histogram.AsArray()
            }.Schedule(histogramHandle);

            JobHandle scatterHandle = new ScatterRecordsJob
            {
                Input = m_Directed.AsArray(),
                Output = m_Sorted.AsArray(),
                BucketCursors = m_Histogram.AsArray(),
                NumWorkers = numWorkers
            }.Schedule(numWorkers, 1, prefixHandle);

            int bucketBatch = math.max(1, numBodies / 32);

            JobHandle sortHandle = new SortBucketsJob
            {
                InOutArray = m_Sorted.AsArray(),
                BucketEnds = m_Histogram.AsArray()
            }.Schedule(numBodies + 1, bucketBatch, scatterHandle);

            JobHandle countUniqueHandle = new CountUniquePerBucketJob
            {
                Sorted = m_Sorted.AsArray(),
                BucketEnds = m_Histogram.AsArray(),
                NeighborCounts = m_NeighborCounts.AsArray(),
                SourceCounts = m_SourceCounts.AsArray(),
                PairCounts = m_PairCounts.AsArray()
            }.Schedule(numBodies, bucketBatch, sortHandle);

            using (s_SortMarker.Auto())
            {
                countUniqueHandle.Complete();
            }

            // Turn per-bucket counts into per-bucket output offsets, size the target buffers,
            // then emit in parallel into disjoint ranges.
            int totalNeighbors = ExclusivePrefixInPlace(m_NeighborCounts);
            int totalSources = ExclusivePrefixInPlace(m_SourceCounts);
            int totalPairs = ExclusivePrefixInPlace(m_PairCounts);

            target.Pairs.ResizeUninitialized(totalPairs);
            target.Neighbors.ResizeUninitialized(totalNeighbors);
            target.Ranges.ResizeUninitialized(totalSources);
            target.RangeLookup.Clear();
            EnsureHashCapacity(target, totalSources);

            JobHandle emitHandle = new EmitPerBucketJob
            {
                Sorted = m_Sorted.AsArray(),
                BucketEnds = m_Histogram.AsArray(),
                NeighborOffsets = m_NeighborCounts.AsArray(),
                SourceOffsets = m_SourceCounts.AsArray(),
                PairOffsets = m_PairCounts.AsArray(),
                RankToGuid = m_RankToGuid.AsArray(),
                Neighbors = target.Neighbors.AsArray(),
                Ranges = target.Ranges.AsArray(),
                Pairs = target.Pairs.AsArray(),
                RangeLookup = target.RangeLookup.AsParallelWriter()
            }.Schedule(numBodies, bucketBatch, default);

            using (s_EmitMarker.Auto())
            {
                emitHandle.Complete();
            }

            stats.UsedSerialPath = false;
            stats.UniquePairs = totalPairs;
            stats.ActiveSourceBricks = totalSources;
        }

        void Publish(BrickOverlapGraphStats stats, long startTicks)
        {
            m_Active = 1 - m_Active;
            m_Version++;
            stats.BuildMilliseconds = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            LastBuildStats = stats;
        }

        static void EnsureHashCapacity(GraphBuffer target, int needed)
        {
            if (target.RangeLookup.Capacity < needed)
            {
                target.RangeLookup.Capacity = needed;
            }
        }

        static int ExclusivePrefixInPlace(NativeList<int> counts)
        {
            int sum = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                int current = counts[i];
                counts[i] = sum;
                sum += current;
            }
            return sum;
        }

        public void Dispose()
        {
            m_Buffers[0].Dispose();
            m_Buffers[1].Dispose();
            if (m_StreamOffsets.IsCreated) m_StreamOffsets.Dispose();
            if (m_Directed.IsCreated) m_Directed.Dispose();
            if (m_Sorted.IsCreated) m_Sorted.Dispose();
            if (m_Histogram.IsCreated) m_Histogram.Dispose();
            if (m_NeighborCounts.IsCreated) m_NeighborCounts.Dispose();
            if (m_SourceCounts.IsCreated) m_SourceCounts.Dispose();
            if (m_PairCounts.IsCreated) m_PairCounts.Dispose();
            if (m_RankOfBody.IsCreated) m_RankOfBody.Dispose();
            if (m_RankToGuid.IsCreated) m_RankToGuid.Dispose();
            if (m_EmptyStream.IsCreated) m_EmptyStream.Dispose();
        }
    }
}
