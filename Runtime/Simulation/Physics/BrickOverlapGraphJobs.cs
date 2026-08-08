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
    /// small ints.
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
    /// Flattens, sorts, and emits the overlap graph in one Burst job.
    /// </summary>
    [BurstCompile]
    internal struct SerialBuildJob : IJob
    {
        public NativeStream.Reader DynamicReader;
        public NativeStream.Reader StaticReader;
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
