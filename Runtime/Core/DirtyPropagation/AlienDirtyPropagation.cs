using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;
using Voxelis.Mathematics;

namespace Voxelis
{
    public struct AlienDirtyPropagationSettings
    {
        public DirtyFlags FlagsToPropagate;
        public DirtyFlags AlienMotionDirtyMask;
        public int SpatialCellSize;
        public int DirtyHaloVoxels;

        public static AlienDirtyPropagationSettings Default => new AlienDirtyPropagationSettings
        {
            FlagsToPropagate = DirtyFlags.All,
            AlienMotionDirtyMask = DirtyFlags.GeneralAutomata,
            SpatialCellSize = 64,
            DirtyHaloVoxels = 1,
        };
    }

    public static partial class AlienDirtyPropagation
    {
        private const float MaxBoundEpsilon = 1e-4f;

        public static void Propagate(NativeArray<VoxelEntityData> entities, AlienDirtyPropagationSettings settings)
        {
            settings = NormalizeSettings(settings);
            if (entities.Length <= 1)
                return;

            if (settings.FlagsToPropagate == DirtyFlags.None && settings.AlienMotionDirtyMask == DirtyFlags.None)
                return;

            var entityViews = new NativeList<AlienDirtyEntityView>(entities.Length, Allocator.TempJob);
            var allSectors = new NativeList<AlienDirtySectorRecord>(Allocator.TempJob);
            var activeSectors = new NativeList<ActiveSectorInfo>(Allocator.TempJob);

            try
            {
                Profiler.BeginSample("BuildRecords");
                BuildRecords(entities, settings, ref entityViews, ref allSectors, ref activeSectors);
                Profiler.EndSample();

                if (allSectors.Length == 0 || activeSectors.Length == 0)
                    return;

                Profiler.BeginSample("BuildCandidates");
                NativeList<AlienDirtyCandidate> candidates = BuildCandidates(
                    entityViews.AsArray(),
                    allSectors.AsArray(),
                    activeSectors.AsArray(),
                    settings);
                Profiler.EndSample();

                try
                {
                    if (candidates.Length == 0)
                        return;

                    Profiler.BeginSample("Candidates Sort and Ranges");
                    candidates.Sort();

                    var uniqueCandidates = new NativeList<AlienDirtyCandidate>(candidates.Length, Allocator.TempJob);
                    var ranges = new NativeArray<AlienDirtyCandidateRange>(allSectors.Length, Allocator.TempJob);
                    var activeTargets = new NativeList<int>(Allocator.TempJob);

                    try
                    {
                        DeduplicateAndBuildRanges(candidates.AsArray(), ref uniqueCandidates, ranges, ref activeTargets);
                        Profiler.EndSample();

                        if (activeTargets.Length == 0)
                            return;

                        Profiler.BeginSample("Target-owned Mark Aliens");
                        var markJob = new MarkAlienRequireUpdatesJob
                        {
                            ActiveTargetSectors = activeTargets.AsArray(),
                            CandidateRanges = ranges,
                            Candidates = uniqueCandidates.AsArray(),
                            Sectors = allSectors.AsArray(),
                            Entities = entityViews.AsArray(),
                            DirtyHaloVoxels = settings.DirtyHaloVoxels
                        };

                        markJob.Schedule(activeTargets.Length, 1).Complete();
                    }
                    finally
                    {
                        Profiler.EndSample();
                        if (activeTargets.IsCreated) activeTargets.Dispose();
                        if (ranges.IsCreated) ranges.Dispose();
                        if (uniqueCandidates.IsCreated) uniqueCandidates.Dispose();
                    }
                }
                finally
                {
                    if (candidates.IsCreated) candidates.Dispose();
                }
            }
            finally
            {
                if (activeSectors.IsCreated) activeSectors.Dispose();
                if (allSectors.IsCreated) allSectors.Dispose();
                if (entityViews.IsCreated) entityViews.Dispose();
            }
        }

        private static AlienDirtyPropagationSettings NormalizeSettings(AlienDirtyPropagationSettings s)
        {
            s.FlagsToPropagate = (DirtyFlags)DirtyPropagationSettings.FilterCanPropagateToAlien(
                (ushort)(s.FlagsToPropagate == DirtyFlags.None ? DirtyFlags.All : s.FlagsToPropagate));
            s.AlienMotionDirtyMask = (DirtyFlags)DirtyPropagationSettings.FilterCanPropagateToAlien(
                (ushort)(s.AlienMotionDirtyMask == DirtyFlags.None
                    ? DirtyFlags.GeneralAutomata
                    : s.AlienMotionDirtyMask));
            s.SpatialCellSize = math.max(Sector.SIZE_IN_BLOCKS, math.ceilpow2(s.SpatialCellSize));
            s.DirtyHaloVoxels = math.max(0, s.DirtyHaloVoxels);
            return s;
        }

        private static unsafe void BuildRecords(
            NativeArray<VoxelEntityData> entities,
            AlienDirtyPropagationSettings settings,
            ref NativeList<AlienDirtyEntityView> entityViews,
            ref NativeList<AlienDirtySectorRecord> allSectors,
            ref NativeList<ActiveSectorInfo> activeSectors)
        {
            new BuildRecordsJob
            {
                Entities = (VoxelEntityData*)entities.GetUnsafeReadOnlyPtr(),
                EntityCount = entities.Length,
                FlagsToPropagate = settings.FlagsToPropagate,
                EntityViews = entityViews,
                AllSectors = allSectors,
                ActiveSectors = activeSectors
            }.Schedule().Complete();
        }

        private static NativeList<AlienDirtyCandidate> BuildCandidates(
            NativeArray<AlienDirtyEntityView> entityViews,
            NativeArray<AlienDirtySectorRecord> allSectors,
            NativeArray<ActiveSectorInfo> activeSectors,
            AlienDirtyPropagationSettings settings)
        {
            NativeParallelMultiHashMap<int3, int> spatialHash = default;
            var candidates = new NativeList<AlienDirtyCandidate>(Allocator.TempJob);

            try
            {
                spatialHash = BuildSpatialHash(allSectors, entityViews, settings.SpatialCellSize);

                NativeStream stream = default;
                try
                {
                    stream = new NativeStream(activeSectors.Length, Allocator.TempJob);
                    var job = new BuildCandidatesJob
                    {
                        ActiveSectors = activeSectors,
                        AllSectors = allSectors,
                        Entities = entityViews,
                        SpatialHash = spatialHash,
                        SpatialCellSize = settings.SpatialCellSize,
                        DirtyHaloVoxels = settings.DirtyHaloVoxels,
                        FlagsToPropagate = settings.FlagsToPropagate,
                        AlienMotionDirtyMask = settings.AlienMotionDirtyMask,
                        Candidates = stream.AsWriter()
                    };
                    job.Schedule(activeSectors.Length, 1).Complete();

                    FlattenStream(stream, activeSectors.Length, ref candidates);
                }
                finally
                {
                    if (stream.IsCreated) stream.Dispose();
                }
            }
            finally
            {
                if (spatialHash.IsCreated) spatialHash.Dispose();
            }

            return candidates;
        }

        private static NativeParallelMultiHashMap<int3, int> BuildSpatialHash(
            NativeArray<AlienDirtySectorRecord> sectors,
            NativeArray<AlienDirtyEntityView> entities,
            int cellSize)
        {
            int estimatedCapacity = math.max(sectors.Length * 8, 1);
            var hash = new NativeParallelMultiHashMap<int3, int>(estimatedCapacity, Allocator.TempJob);

            for (int sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
            {
                AlienDirtySectorRecord record = sectors[sectorIndex];
                if (record.Sector.Get().NonEmptyBrickCount == 0)
                    continue;

                InsertAabbIntoHash(ref hash, record.CurrentWorldAabb, cellSize, sectorIndex);

                if (entities[record.EntityId].IsMoving)
                {
                    InsertAabbIntoHash(ref hash, record.PreviousWorldAabb, cellSize, sectorIndex);
                }
            }

            return hash;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InsertAabbIntoHash(
            ref NativeParallelMultiHashMap<int3, int> hash,
            AABB aabb, int cellSize, int sectorIndex)
        {
            int3 minCell = WorldToCell(aabb.Min, cellSize);
            int3 maxCell = WorldToCell(aabb.Max - new float3(MaxBoundEpsilon), cellSize);
            for (int z = minCell.z; z <= maxCell.z; z++)
            for (int y = minCell.y; y <= maxCell.y; y++)
            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                hash.Add(new int3(x, y, z), sectorIndex);
            }
        }

        private static void FlattenStream(NativeStream stream, int foreachCount,
            ref NativeList<AlienDirtyCandidate> output)
        {
            var reader = stream.AsReader();
            int total = 0;
            for (int i = 0; i < foreachCount; i++)
            {
                total += reader.BeginForEachIndex(i);
            }

            if (total == 0)
                return;

            output.ResizeUninitialized(total);
            var array = output.AsArray();
            int writeIndex = 0;

            reader = stream.AsReader();
            for (int i = 0; i < foreachCount; i++)
            {
                int count = reader.BeginForEachIndex(i);
                for (int c = 0; c < count; c++)
                {
                    array[writeIndex++] = reader.Read<AlienDirtyCandidate>();
                }

                reader.EndForEachIndex();
            }
        }

        private static void DeduplicateAndBuildRanges(
            NativeArray<AlienDirtyCandidate> sortedCandidates,
            ref NativeList<AlienDirtyCandidate> uniqueCandidates,
            NativeArray<AlienDirtyCandidateRange> ranges,
            ref NativeList<int> activeTargets)
        {
            for (int i = 0; i < ranges.Length; i++)
            {
                ranges[i] = new AlienDirtyCandidateRange { Start = -1, Count = 0 };
            }

            AlienDirtyCandidate previous = default;
            bool hasPrevious = false;
            for (int i = 0; i < sortedCandidates.Length; i++)
            {
                AlienDirtyCandidate candidate = sortedCandidates[i];
                if (hasPrevious && candidate.SameDedupKey(previous))
                {
                    var last = uniqueCandidates[uniqueCandidates.Length - 1];
                    last.Flags |= candidate.Flags;
                    uniqueCandidates[uniqueCandidates.Length - 1] = last;
                    previous = last;
                    continue;
                }

                int uniqueIndex = uniqueCandidates.Length;
                uniqueCandidates.Add(candidate);
                AlienDirtyCandidateRange range = ranges[candidate.TargetSectorIndex];
                if (range.Start < 0)
                {
                    range.Start = uniqueIndex;
                    range.Count = 1;
                    activeTargets.Add(candidate.TargetSectorIndex);
                }
                else
                {
                    range.Count++;
                }

                ranges[candidate.TargetSectorIndex] = range;
                previous = candidate;
                hasPrevious = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static AABB ComputeSectorWorldAabb(int3 sectorPos, RigidTransform transform)
        {
            float3 localMin = sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
            float3 localMax = localMin + Sector.SECTOR_SIZE_IN_BLOCKS;
            return AABB.Transform(new AABB { Min = localMin, Max = localMax }, transform);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 MaxExclusive(float3 value)
        {
            return value - new float3(MaxBoundEpsilon);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int3 WorldToCell(float3 world, int cellSize)
        {
            return (int3)math.floor(world / cellSize);
        }
    }
}
