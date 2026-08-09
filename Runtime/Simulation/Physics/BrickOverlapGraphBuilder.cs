using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Physics;
using Unity.Profiling;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    /// <summary>
    /// Builds and double-buffer-publishes the post-physics brick-overlap graph from a raw
    /// candidate stream.
    ///
    /// Querying both endpoints or submitting duplicate source bricks can produce duplicate raw
    /// pairs. The sorted build removes them before publishing symmetric adjacency. Small inputs
    /// (at most <see cref="serialBuildThreshold"/> directed records) run one serial job. The
    /// parallel build path is not implemented.
    ///
    /// Build must run while the step's bodyIndexToGuid mapping is still valid, after the
    /// step's FinalExecutionHandle completed and before the next simulation reset.
    /// </summary>
    public sealed class BrickOverlapGraphBuilder : IDisposable
    {
        /// <summary>
        /// Directed-record count (2 × raw candidates) at or below which the build runs as
        /// one serial job. Inputs above this threshold reach the unimplemented parallel path.
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

        // Persistent, grow-only scratch reused across serial builds.
        NativeList<DirectedBrickOverlapRecord> m_Directed;
        NativeList<int> m_RankOfBody;
        NativeList<Guid128> m_RankToGuid;

        /// <summary> Counters of the most recent build. </summary>
        public BrickOverlapGraphStats LastBuildStats { get; private set; }

        static readonly ProfilerMarker s_BuildMarker = new ProfilerMarker("BrickOverlapGraph.Build");
        static readonly ProfilerMarker s_SortMarker = new ProfilerMarker("BrickOverlapGraph.FlattenSortCount");

        public BrickOverlapGraphBuilder()
        {
            m_Buffers = new[] { new GraphBuffer(), new GraphBuffer() };
            m_Directed = new NativeList<DirectedBrickOverlapRecord>(256, Allocator.Persistent);
            m_RankOfBody = new NativeList<int>(64, Allocator.Persistent);
            m_RankToGuid = new NativeList<Guid128>(64, Allocator.Persistent);
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
        public void BuildAndPublish(NativeStream candidates, NativeArray<Guid128> bodyIndexToGuid)
        {
            using (s_BuildMarker.Auto())
            {
                long startTicks = Stopwatch.GetTimestamp();

                GraphBuffer target = m_Buffers[1 - m_Active];
                int numBodies = bodyIndexToGuid.IsCreated ? bodyIndexToGuid.Length : 0;
                int rawCandidates = candidates.IsCreated ? candidates.Count() : 0;

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

                int candidateForEach = candidates.ForEachCount;
                NativeStream.Reader candidateReader = candidates.AsReader();

                m_RankOfBody.ResizeUninitialized(numBodies);
                m_RankToGuid.ResizeUninitialized(numBodies);
                JobHandle rankHandle = new BuildBodyRankJob
                {
                    BodyIndexToGuid = bodyIndexToGuid,
                    RankOfBody = m_RankOfBody.AsArray(),
                    RankToGuid = m_RankToGuid.AsArray()
                }.Schedule();

                int totalDirected = rawCandidates * 2;

                // TODO: Implement the parallel path.
                // if (totalDirected <= serialBuildThreshold)
                if (true)
                {
                    BuildSerial(target, rankHandle, candidateReader,
                        candidateForEach, numBodies, totalDirected, ref stats);
                    Publish(stats, startTicks);
                    return;
                }

                BuildParallel(target, rankHandle, candidateReader,
                    candidateForEach, numBodies, totalDirected, ref stats);
                Publish(stats, startTicks);
            }
        }

        void BuildSerial(GraphBuffer target, JobHandle rankHandle,
            NativeStream.Reader candidateReader,
            int candidateForEach, int numBodies, int totalDirected,
            ref BrickOverlapGraphStats stats)
        {
            target.Clear();
            // At most one range entry per directed record; bounded by the serial threshold.
            EnsureHashCapacity(target, totalDirected);
            m_Directed.Clear();

            JobHandle serialHandle = new SerialBuildJob
            {
                CandidateReader = candidateReader,
                CandidateForEachCount = candidateForEach,
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
            stats.PublishedPairs = target.Pairs.Length;
            stats.ActiveSourceBricks = target.Ranges.Length;
        }

        void BuildParallel(GraphBuffer target, JobHandle rankHandle,
            NativeStream.Reader candidateReader,
            int candidateForEach, int numBodies, int totalDirected,
            ref BrickOverlapGraphStats stats)
        {
            rankHandle.Complete();
            throw new NotImplementedException();
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

        public void Dispose()
        {
            m_Buffers[0].Dispose();
            m_Buffers[1].Dispose();
            if (m_Directed.IsCreated) m_Directed.Dispose();
            if (m_RankOfBody.IsCreated) m_RankOfBody.Dispose();
            if (m_RankToGuid.IsCreated) m_RankToGuid.Dispose();
        }
    }
}
