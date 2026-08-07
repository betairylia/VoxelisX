using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    /// <summary>
    /// One directed half of a raw brick-overlap candidate, with both bodies replaced by
    /// their GUID rank. Ranks are the positions of the step's body GUIDs in GUID-sorted
    /// order, so sorting by rank equals sorting by GUID while keeping the sort keys as
    /// small ints and giving the parallel pipeline a radix digit (the source rank).
    /// </summary>
    internal struct DirectedBrickOverlapRecord
    {
        public int SrcRank;
        public int3 SrcBrick;
        public int TgtRank;
        public int3 TgtBrick;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompareInt3(int3 a, int3 b)
        {
            if (a.x != b.x) return a.x < b.x ? -1 : 1;
            if (a.y != b.y) return a.y < b.y ? -1 : 1;
            if (a.z != b.z) return a.z < b.z ? -1 : 1;
            return 0;
        }

        /// <summary> Order inside one source-rank bucket: (SrcBrick, TgtRank, TgtBrick). </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompareWithinBucket(in DirectedBrickOverlapRecord a, in DirectedBrickOverlapRecord b)
        {
            int c = CompareInt3(a.SrcBrick, b.SrcBrick);
            if (c != 0) return c;
            if (a.TgtRank != b.TgtRank) return a.TgtRank < b.TgtRank ? -1 : 1;
            return CompareInt3(a.TgtBrick, b.TgtBrick);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompareFull(in DirectedBrickOverlapRecord a, in DirectedBrickOverlapRecord b)
        {
            if (a.SrcRank != b.SrcRank) return a.SrcRank < b.SrcRank ? -1 : 1;
            return CompareWithinBucket(a, b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Equals(in DirectedBrickOverlapRecord a, in DirectedBrickOverlapRecord b)
        {
            return a.SrcRank == b.SrcRank && a.TgtRank == b.TgtRank
                && math.all(a.SrcBrick == b.SrcBrick) && math.all(a.TgtBrick == b.TgtBrick);
        }

        /// <summary>
        /// True when the directed record's source key is strictly smaller than its target
        /// key, which selects exactly one of the two directions of an undirected pair.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCanonical(in DirectedBrickOverlapRecord r)
        {
            if (r.SrcRank != r.TgtRank) return r.SrcRank < r.TgtRank;
            // Physics never pairs a body with itself; kept for a well-defined total order.
            return CompareInt3(r.SrcBrick, r.TgtBrick) < 0;
        }
    }

    internal struct DirectedRecordFullComparer : IComparer<DirectedBrickOverlapRecord>
    {
        public int Compare(DirectedBrickOverlapRecord x, DirectedBrickOverlapRecord y)
            => DirectedBrickOverlapRecord.CompareFull(x, y);
    }

    internal struct DirectedRecordBucketComparer : IComparer<DirectedBrickOverlapRecord>
    {
        public int Compare(DirectedBrickOverlapRecord x, DirectedBrickOverlapRecord y)
            => DirectedBrickOverlapRecord.CompareWithinBucket(x, y);
    }

    /// <summary>
    /// Sorts the step's bodies by GUID and produces the body-index↔rank mappings.
    /// Ranks are order-isomorphic to GUIDs, so all rank comparisons downstream reproduce
    /// the canonical GUID ordering.
    /// </summary>
    [BurstCompile]
    internal struct BuildBodyRankJob : IJob
    {
        struct GuidAndBody
        {
            public Guid128 Guid;
            public int BodyIndex;
        }

        struct GuidAndBodyComparer : IComparer<GuidAndBody>
        {
            public int Compare(GuidAndBody x, GuidAndBody y)
            {
                int c = x.Guid.CompareTo(y.Guid);
                if (c != 0) return c;
                return x.BodyIndex.CompareTo(y.BodyIndex);
            }
        }

        [ReadOnly] public NativeArray<Guid128> BodyIndexToGuid;
        [NativeDisableParallelForRestriction] public NativeArray<int> RankOfBody;
        [NativeDisableParallelForRestriction] public NativeArray<Guid128> RankToGuid;

        public void Execute()
        {
            int numBodies = BodyIndexToGuid.Length;
            var sorted = new NativeArray<GuidAndBody>(numBodies, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < numBodies; i++)
            {
                sorted[i] = new GuidAndBody { Guid = BodyIndexToGuid[i], BodyIndex = i };
            }
            sorted.Sort(new GuidAndBodyComparer());
            for (int rank = 0; rank < numBodies; rank++)
            {
                RankOfBody[sorted[rank].BodyIndex] = rank;
                RankToGuid[rank] = sorted[rank].Guid;
            }
            sorted.Dispose();
        }
    }

    /// <summary>
    /// Counts the candidates in every stream work item. The combined index space covers
    /// the dynamic stream first, then the static-static stream.
    /// </summary>
    [BurstCompile]
    internal struct CountStreamItemsJob : IJobParallelFor
    {
        [ReadOnly] public NativeStream.Reader DynamicReader;
        [ReadOnly] public NativeStream.Reader StaticReader;
        public int DynamicForEachCount;
        [WriteOnly] public NativeArray<int> Counts;

        public void Execute(int index)
        {
            if (index < DynamicForEachCount)
            {
                Counts[index] = DynamicReader.BeginForEachIndex(index);
            }
            else
            {
                Counts[index] = StaticReader.BeginForEachIndex(index - DynamicForEachCount);
            }
        }
    }

    /// <summary> Exclusive prefix sum over the per-work-item candidate counts. </summary>
    [BurstCompile]
    internal struct ComputeStreamOffsetsJob : IJob
    {
        [ReadOnly] public NativeArray<int> Counts;
        [WriteOnly] public NativeArray<int> Offsets;

        public void Execute()
        {
            int sum = 0;
            for (int i = 0; i < Counts.Length; i++)
            {
                Offsets[i] = sum;
                sum += Counts[i];
            }
        }
    }

    /// <summary>
    /// Flattens both candidate streams into one array of directed records, mapping body
    /// indices to GUID ranks and doubling every candidate into A→B and B→A.
    /// </summary>
    [BurstCompile]
    internal struct FlattenCandidatesJob : IJobParallelFor
    {
        [ReadOnly] public NativeStream.Reader DynamicReader;
        [ReadOnly] public NativeStream.Reader StaticReader;
        public int DynamicForEachCount;
        public int NumBodies;
        [ReadOnly] public NativeArray<int> Offsets;
        [ReadOnly] public NativeArray<int> RankOfBody;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> Directed;

        public void Execute(int index)
        {
            NativeStream.Reader reader;
            int localIndex;
            if (index < DynamicForEachCount)
            {
                reader = DynamicReader;
                localIndex = index;
            }
            else
            {
                reader = StaticReader;
                localIndex = index - DynamicForEachCount;
            }

            int count = reader.BeginForEachIndex(localIndex);
            int baseOffset = Offsets[index];
            for (int k = 0; k < count; k++)
            {
                var candidate = reader.Read<VoxelBrickOverlapCandidate>();

                int bodyA = candidate.BodyIndexA;
                int bodyB = candidate.BodyIndexB;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if ((uint)bodyA >= (uint)NumBodies || (uint)bodyB >= (uint)NumBodies)
                {
                    throw new InvalidOperationException(
                        "Brick-overlap candidate has an out-of-range body index.");
                }
#else
                // A producer bug must not corrupt memory in release builds.
                bodyA = math.clamp(bodyA, 0, NumBodies - 1);
                bodyB = math.clamp(bodyB, 0, NumBodies - 1);
#endif
                int rankA = RankOfBody[bodyA];
                int rankB = RankOfBody[bodyB];

                int writeIndex = 2 * (baseOffset + k);
                Directed[writeIndex] = new DirectedBrickOverlapRecord
                {
                    SrcRank = rankA,
                    SrcBrick = candidate.BrickCoordsInA,
                    TgtRank = rankB,
                    TgtBrick = candidate.BrickCoordsInB
                };
                Directed[writeIndex + 1] = new DirectedBrickOverlapRecord
                {
                    SrcRank = rankB,
                    SrcBrick = candidate.BrickCoordsInB,
                    TgtRank = rankA,
                    TgtBrick = candidate.BrickCoordsInA
                };
            }
        }
    }

    /// <summary>
    /// Histogram of directed records per source rank. Mirrors the physics scheduler's
    /// RadixSortHistogramJob (worker-partitioned, atomic adds).
    /// </summary>
    [BurstCompile]
    internal struct RankHistogramJob : IJobParallelFor
    {
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> Directed;
        [NativeDisableParallelForRestriction]
        public NativeArray<int> Histogram;
        public int NumWorkers;

        public unsafe void Execute(int workerIndex)
        {
            int perWorker = (int)math.ceil(Directed.Length / (float)NumWorkers);
            int start = workerIndex * perWorker;
            if (start > Directed.Length - 1)
            {
                return;
            }
            int end = math.min(start + perWorker, Directed.Length);

            var histogramPtr = (int*)Histogram.GetUnsafePtr();
            for (int i = start; i < end; i++)
            {
                var counter = new UnsafeAtomicCounter32(histogramPtr + Directed[i].SrcRank);
                counter.Add(1);
            }
        }
    }

    /// <summary>
    /// In-place exclusive prefix sum over the rank histogram. Mirrors the physics
    /// scheduler's RadixSortPrefixSumJob.
    /// </summary>
    [BurstCompile]
    internal struct BucketPrefixSumJob : IJob
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<int> Histogram;

        public void Execute()
        {
            int last = Histogram[0];
            Histogram[0] = 0;
            for (int i = 1; i < Histogram.Length; i++)
            {
                int current = Histogram[i];
                Histogram[i] = Histogram[i - 1] + last;
                last = current;
            }
        }
    }

    /// <summary>
    /// Scatters directed records into contiguous per-rank buckets with atomic cursors.
    /// Mirrors the physics scheduler's RadixSortGatherJob. After this job, Histogram[b]
    /// holds the end index of bucket b.
    /// </summary>
    [BurstCompile]
    internal struct ScatterRecordsJob : IJobParallelFor
    {
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> Input;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> Output;
        [NativeDisableParallelForRestriction]
        public NativeArray<int> BucketCursors;
        public int NumWorkers;

        public unsafe void Execute(int workerIndex)
        {
            int perWorker = (int)math.ceil(Input.Length / (float)NumWorkers);
            int start = workerIndex * perWorker;
            if (start > Input.Length - 1)
            {
                return;
            }
            int end = math.min(start + perWorker, Input.Length);

            var cursorPtr = (int*)BucketCursors.GetUnsafePtr();
            for (int i = start; i < end; i++)
            {
                var record = Input[i];
                var counter = new UnsafeAtomicCounter32(cursorPtr + record.SrcRank);
                int writeIndex = counter.Add(1);
                Output[writeIndex] = record;
            }
        }
    }

    /// <summary>
    /// Sorts every rank bucket by (SrcBrick, TgtRank, TgtBrick). Mirrors the physics
    /// scheduler's SortSubArraysJob. The scatter order inside a bucket is timing
    /// dependent, but the sort key covers the whole record and duplicates are
    /// bit-identical, so the sorted array is deterministic.
    /// </summary>
    [BurstCompile]
    internal struct SortBucketsJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> InOutArray;
        // BucketEnds[b] = end of bucket b = start of bucket b + 1 (see ScatterRecordsJob).
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> BucketEnds;

        public void Execute(int bucket)
        {
            int start = bucket == 0 ? 0 : BucketEnds[bucket - 1];
            if (start >= InOutArray.Length)
            {
                return;
            }
            int length = BucketEnds[bucket] - start;

            if (length > 2)
            {
                var slice = new NativeSlice<DirectedBrickOverlapRecord>(InOutArray, start, length);
                NativeSortExtension.Sort(slice, new DirectedRecordBucketComparer());
            }
            else if (length == 2)
            {
                if (DirectedBrickOverlapRecord.CompareWithinBucket(InOutArray[start], InOutArray[start + 1]) > 0)
                {
                    var tmp = InOutArray[start + 1];
                    InOutArray[start + 1] = InOutArray[start];
                    InOutArray[start] = tmp;
                }
            }
        }
    }

    /// <summary>
    /// Per-bucket counting scan: unique directed records (= neighbor entries), distinct
    /// source bricks (= range entries) and canonical pairs. Duplicates of a directed
    /// record always share the source body, so deduplication is bucket-local.
    /// </summary>
    [BurstCompile]
    internal struct CountUniquePerBucketJob : IJobParallelFor
    {
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> Sorted;
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> BucketEnds;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> NeighborCounts;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> SourceCounts;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> PairCounts;

        public void Execute(int bucket)
        {
            int start = bucket == 0 ? 0 : BucketEnds[bucket - 1];
            int end = BucketEnds[bucket];

            int neighborCount = 0;
            int sourceCount = 0;
            int pairCount = 0;

            DirectedBrickOverlapRecord prev = default;
            bool hasPrev = false;
            for (int i = start; i < end; i++)
            {
                var record = Sorted[i];
                if (hasPrev && DirectedBrickOverlapRecord.Equals(record, prev))
                {
                    continue;
                }
                if (!hasPrev || math.any(record.SrcBrick != prev.SrcBrick))
                {
                    sourceCount++;
                }
                neighborCount++;
                if (DirectedBrickOverlapRecord.IsCanonical(record))
                {
                    pairCount++;
                }
                prev = record;
                hasPrev = true;
            }

            NeighborCounts[bucket] = neighborCount;
            SourceCounts[bucket] = sourceCount;
            PairCounts[bucket] = pairCount;
        }
    }

    /// <summary>
    /// Per-bucket emission scan into the pre-sized output arrays: neighbor keys, source
    /// ranges, canonical pairs and the source→range hash lookup. Every bucket writes to
    /// disjoint precomputed ranges, so the only shared write path is the hash map's
    /// parallel writer.
    /// </summary>
    [BurstCompile]
    internal struct EmitPerBucketJob : IJobParallelFor
    {
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<DirectedBrickOverlapRecord> Sorted;
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> BucketEnds;
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> NeighborOffsets;
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> SourceOffsets;
        [ReadOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> PairOffsets;
        [ReadOnly] public NativeArray<Guid128> RankToGuid;

        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<BrickOverlapKey> Neighbors;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<BrickOverlapSourceRange> Ranges;
        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<BrickOverlapPair> Pairs;
        public NativeParallelHashMap<BrickOverlapKey, int>.ParallelWriter RangeLookup;

        public void Execute(int bucket)
        {
            int start = bucket == 0 ? 0 : BucketEnds[bucket - 1];
            int end = BucketEnds[bucket];
            if (start >= end)
            {
                return;
            }

            int neighborCursor = NeighborOffsets[bucket];
            int sourceCursor = SourceOffsets[bucket];
            int pairCursor = PairOffsets[bucket];

            Guid128 sourceGuid = RankToGuid[bucket];

            DirectedBrickOverlapRecord prev = default;
            bool hasPrev = false;
            int3 runBrick = default;
            int runStart = 0;
            int runCount = 0;

            for (int i = start; i < end; i++)
            {
                var record = Sorted[i];
                if (hasPrev && DirectedBrickOverlapRecord.Equals(record, prev))
                {
                    continue;
                }

                if (!hasPrev || math.any(record.SrcBrick != runBrick))
                {
                    if (hasPrev)
                    {
                        var closedSource = new BrickOverlapKey { EntityId = sourceGuid, BrickCoord = runBrick };
                        Ranges[sourceCursor] = new BrickOverlapSourceRange
                        {
                            Source = closedSource,
                            Start = runStart,
                            Count = runCount
                        };
                        RangeLookup.TryAdd(closedSource, sourceCursor);
                        sourceCursor++;
                    }
                    runBrick = record.SrcBrick;
                    runStart = neighborCursor;
                    runCount = 0;
                }

                Neighbors[neighborCursor] = new BrickOverlapKey
                {
                    EntityId = RankToGuid[record.TgtRank],
                    BrickCoord = record.TgtBrick
                };
                neighborCursor++;
                runCount++;

                if (DirectedBrickOverlapRecord.IsCanonical(record))
                {
                    Pairs[pairCursor] = new BrickOverlapPair
                    {
                        A = new BrickOverlapKey { EntityId = sourceGuid, BrickCoord = record.SrcBrick },
                        B = new BrickOverlapKey { EntityId = RankToGuid[record.TgtRank], BrickCoord = record.TgtBrick }
                    };
                    pairCursor++;
                }

                prev = record;
                hasPrev = true;
            }

            var lastSource = new BrickOverlapKey { EntityId = sourceGuid, BrickCoord = runBrick };
            Ranges[sourceCursor] = new BrickOverlapSourceRange
            {
                Source = lastSource,
                Start = runStart,
                Count = runCount
            };
            RangeLookup.TryAdd(lastSource, sourceCursor);
        }
    }

    /// <summary>
    /// Small-input fallback: flatten, sort, deduplicate and emit in one Burst job.
    /// Produces bit-identical output to the parallel pipeline.
    /// </summary>
    [BurstCompile]
    internal struct SerialBuildJob : IJob
    {
        [ReadOnly] public NativeStream.Reader DynamicReader;
        [ReadOnly] public NativeStream.Reader StaticReader;
        public int DynamicForEachCount;
        public int StaticForEachCount;
        public int NumBodies;
        [ReadOnly] public NativeArray<int> RankOfBody;
        [ReadOnly] public NativeArray<Guid128> RankToGuid;

        public NativeList<DirectedBrickOverlapRecord> Scratch;
        public NativeList<BrickOverlapKey> Neighbors;
        public NativeList<BrickOverlapSourceRange> Ranges;
        public NativeList<BrickOverlapPair> Pairs;
        public NativeParallelHashMap<BrickOverlapKey, int> RangeLookup;

        public void Execute()
        {
            Scratch.Clear();
            FlattenStream(DynamicReader, DynamicForEachCount);
            FlattenStream(StaticReader, StaticForEachCount);

            Scratch.Sort(new DirectedRecordFullComparer());

            DirectedBrickOverlapRecord prev = default;
            bool hasPrev = false;
            BrickOverlapKey runSource = default;
            int runStart = 0;
            int runCount = 0;

            for (int i = 0; i < Scratch.Length; i++)
            {
                var record = Scratch[i];
                if (hasPrev && DirectedBrickOverlapRecord.Equals(record, prev))
                {
                    continue;
                }

                if (!hasPrev || record.SrcRank != prev.SrcRank || math.any(record.SrcBrick != prev.SrcBrick))
                {
                    if (hasPrev)
                    {
                        Ranges.Add(new BrickOverlapSourceRange { Source = runSource, Start = runStart, Count = runCount });
                        RangeLookup.TryAdd(runSource, Ranges.Length - 1);
                    }
                    runSource = new BrickOverlapKey { EntityId = RankToGuid[record.SrcRank], BrickCoord = record.SrcBrick };
                    runStart = Neighbors.Length;
                    runCount = 0;
                }

                Neighbors.Add(new BrickOverlapKey
                {
                    EntityId = RankToGuid[record.TgtRank],
                    BrickCoord = record.TgtBrick
                });
                runCount++;

                if (DirectedBrickOverlapRecord.IsCanonical(record))
                {
                    Pairs.Add(new BrickOverlapPair
                    {
                        A = new BrickOverlapKey { EntityId = RankToGuid[record.SrcRank], BrickCoord = record.SrcBrick },
                        B = new BrickOverlapKey { EntityId = RankToGuid[record.TgtRank], BrickCoord = record.TgtBrick }
                    });
                }

                prev = record;
                hasPrev = true;
            }

            if (hasPrev)
            {
                Ranges.Add(new BrickOverlapSourceRange { Source = runSource, Start = runStart, Count = runCount });
                RangeLookup.TryAdd(runSource, Ranges.Length - 1);
            }
        }

        void FlattenStream(NativeStream.Reader reader, int forEachCount)
        {
            for (int index = 0; index < forEachCount; index++)
            {
                int count = reader.BeginForEachIndex(index);
                for (int k = 0; k < count; k++)
                {
                    var candidate = reader.Read<VoxelBrickOverlapCandidate>();

                    int bodyA = candidate.BodyIndexA;
                    int bodyB = candidate.BodyIndexB;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if ((uint)bodyA >= (uint)NumBodies || (uint)bodyB >= (uint)NumBodies)
                    {
                        throw new InvalidOperationException(
                            "Brick-overlap candidate has an out-of-range body index.");
                    }
#else
                    bodyA = math.clamp(bodyA, 0, NumBodies - 1);
                    bodyB = math.clamp(bodyB, 0, NumBodies - 1);
#endif
                    int rankA = RankOfBody[bodyA];
                    int rankB = RankOfBody[bodyB];

                    Scratch.Add(new DirectedBrickOverlapRecord
                    {
                        SrcRank = rankA,
                        SrcBrick = candidate.BrickCoordsInA,
                        TgtRank = rankB,
                        TgtBrick = candidate.BrickCoordsInB
                    });
                    Scratch.Add(new DirectedBrickOverlapRecord
                    {
                        SrcRank = rankB,
                        SrcBrick = candidate.BrickCoordsInB,
                        TgtRank = rankA,
                        TgtBrick = candidate.BrickCoordsInA
                    });
                }
            }
        }
    }
}
