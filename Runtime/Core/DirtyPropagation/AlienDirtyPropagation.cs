using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;
using Voxelis.Mathematics;

namespace Voxelis
{
    public enum AlienDirtyCandidateMode : byte
    {
        BlockEdit = 0,
        Motion = 1
    }

    public struct AlienDirtyPropagationSettings
    {
        public DirtyFlags FlagsToPropagate;
        public float DeltaTime;
        
        // TODO: FIXME: Below should be static/const or something
        public DirtyFlags AlienMotionDirtyMask;
        public int SpatialCellSize;
        public int DirtyHaloVoxels;
        public float MotionThreshold;

        public static AlienDirtyPropagationSettings Default => new AlienDirtyPropagationSettings
        {
            FlagsToPropagate = DirtyFlags.All,
            AlienMotionDirtyMask = DirtyFlags.GeneralAutomata,
            SpatialCellSize = 64,
            DirtyHaloVoxels = 1,
            MotionThreshold = 0f,
            DeltaTime = 1f
        };
    }

    public static partial class AlienDirtyPropagation
    {
        // Go to settings or somewhere else
        private const float MaxBoundEpsilon = 1e-4f;

        public static void Propagate(NativeArray<VoxelEntityData> entities, AlienDirtyPropagationSettings settings)
        {
            settings = NormalizeSettings(settings);
            if (entities.Length <= 1)
            {
                return;
            }

            if (settings.FlagsToPropagate == DirtyFlags.None && settings.AlienMotionDirtyMask == DirtyFlags.None)
            {
                return;
            }

            var entityViews = new NativeList<AlienDirtyEntityView>(entities.Length, Allocator.TempJob);
            var allSectors = new NativeList<AlienDirtySectorRecord>(Allocator.TempJob);
            var dirtySectorIndices = new NativeList<int>(Allocator.TempJob);
            var movingSectorIndices = new NativeList<int>(Allocator.TempJob);

            try
            {
                Profiler.BeginSample("BuildRecords");
                
                BuildRecords(entities, settings.FlagsToPropagate, ref entityViews, ref allSectors, ref dirtySectorIndices, ref movingSectorIndices);
                
                Profiler.EndSample();
                
                if (allSectors.Length == 0)
                {
                    return;
                }

                Profiler.BeginSample("BuildCandidates");
                NativeList<AlienDirtyCandidate> candidates = BuildCandidates(
                    entityViews.AsArray(),
                    allSectors.AsArray(),
                    dirtySectorIndices.AsArray(),
                    movingSectorIndices.AsArray(),
                    settings);
                Profiler.EndSample();
                try
                {
                    if (candidates.Length == 0)
                    {
                        return;
                    }
                    
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
                        {
                            return;
                        }

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
                if (movingSectorIndices.IsCreated) movingSectorIndices.Dispose();
                if (dirtySectorIndices.IsCreated) dirtySectorIndices.Dispose();
                if (allSectors.IsCreated) allSectors.Dispose();
                if (entityViews.IsCreated) entityViews.Dispose();
            }
        }

        // TODO: FIXME: Maybe remove or simplify or move to somewhere else, too bloaty
        private static AlienDirtyPropagationSettings NormalizeSettings(AlienDirtyPropagationSettings settings)
        {
            if (settings.FlagsToPropagate == DirtyFlags.None)
            {
                settings.FlagsToPropagate = DirtyFlags.All;
            }

            settings.FlagsToPropagate = (DirtyFlags)DirtyPropagationSettings.FilterCanPropagateToAlien((ushort)settings.FlagsToPropagate);

            if (settings.AlienMotionDirtyMask == DirtyFlags.None)
            {
                settings.AlienMotionDirtyMask = DirtyFlags.GeneralAutomata;
            }

            settings.AlienMotionDirtyMask = (DirtyFlags)DirtyPropagationSettings.FilterCanPropagateToAlien((ushort)settings.AlienMotionDirtyMask);

            if (settings.SpatialCellSize < Sector.SIZE_IN_BLOCKS)
            {
                settings.SpatialCellSize = Sector.SIZE_IN_BLOCKS;
            }

            if ((settings.SpatialCellSize & (settings.SpatialCellSize - 1)) != 0)
            {
                int cellSize = 1;
                while (cellSize < settings.SpatialCellSize)
                {
                    cellSize <<= 1;
                }

                settings.SpatialCellSize = cellSize;
            }

            if (settings.DirtyHaloVoxels < 0)
            {
                settings.DirtyHaloVoxels = 0;
            }

            if (settings.DeltaTime <= 0f)
            {
                settings.DeltaTime = 1f;
            }

            return settings;
        }

        // TODO: Maybe move to burst and check where can remove, current bottleneck
        private static void BuildRecords(
            NativeArray<VoxelEntityData> entities,
            DirtyFlags flagsToPropagate,
            ref NativeList<AlienDirtyEntityView> entityViews,
            ref NativeList<AlienDirtySectorRecord> allSectors,
            ref NativeList<int> dirtySectorIndices,
            ref NativeList<int> movingSectorIndices)
        {
            for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
            {
                VoxelEntityData entity = entities[entityIndex];
                float4x4 localToWorld = float4x4.TRS(entity.transform.pos, entity.transform.rot, 1f);
                float4x4 previousLocalToWorld = float4x4.TRS(entity.previousTransform.pos, entity.previousTransform.rot, 1f);

                entityViews.Add(new AlienDirtyEntityView
                {
                    EntityId = entityIndex,
                    LocalToWorld = entity.transform,
                    WorldToLocal = math.inverse(localToWorld),
                    PreviousLocalToWorld = entity.previousTransform,
                    PreviousWorldToLocal = math.inverse(previousLocalToWorld),
                    LinearVelocity = entity.linearVelocity,
                    AngularVelocity = entity.angularVelocity
                });

                bool entityMoving = math.lengthsq(entity.linearVelocity) > 0f || math.lengthsq(entity.angularVelocity) > 0f;

                foreach (var kvp in entity.sectors)
                {
                    int sectorIndex = allSectors.Length;
                    SectorHandle sectorHandle = kvp.Value;
                    int3 sectorPos = kvp.Key;

                    var record = new AlienDirtySectorRecord
                    {
                        EntityId = entityIndex,
                        SectorPos = sectorPos,
                        Sector = sectorHandle,
                        CurrentWorldAabb = ComputeSectorWorldAabb(sectorPos, entity.transform),
                        PreviousWorldAabb = ComputeSectorWorldAabb(sectorPos, entity.previousTransform)
                    };

                    allSectors.Add(record);

                    Sector sector = sectorHandle.Get();
                    if (sector.NonEmptyBrickCount == 0)
                    {
                        continue;
                    }

                    if ((sector.sectorDirtyFlags & (ushort)flagsToPropagate) != 0)
                    {
                        dirtySectorIndices.Add(sectorIndex);
                    }

                    if (entityMoving)
                    {
                        movingSectorIndices.Add(sectorIndex);
                    }
                }
            }
        }

        // TODO: Why use two streams then merge? The merge seems very heavy which is weird
        private static NativeList<AlienDirtyCandidate> BuildCandidates(
            NativeArray<AlienDirtyEntityView> entityViews,
            NativeArray<AlienDirtySectorRecord> allSectors,
            NativeArray<int> dirtySectorIndices,
            NativeArray<int> movingSectorIndices,
            AlienDirtyPropagationSettings settings)
        {
            NativeParallelMultiHashMap<int3, int> currentHash = default;
            NativeParallelMultiHashMap<int3, int> motionHash = default;
            var candidates = new NativeList<AlienDirtyCandidate>(Allocator.TempJob);

            try
            {
                NativeStream blockStream = default;
                NativeStream motionStream = default;

                try
                {
                    if (dirtySectorIndices.Length > 0)
                    {
                        currentHash = BuildSpatialHash(allSectors, settings.SpatialCellSize, 0, false);
                        blockStream = new NativeStream(dirtySectorIndices.Length, Allocator.TempJob);
                        var blockJob = new BuildBlockEditCandidatesJob
                        {
                            DirtySectorIndices = dirtySectorIndices,
                            AllSectors = allSectors,
                            Entities = entityViews,
                            SpatialHash = currentHash,
                            SpatialCellSize = settings.SpatialCellSize,
                            DirtyHaloVoxels = settings.DirtyHaloVoxels,
                            FlagsToPropagate = settings.FlagsToPropagate,
                            Candidates = blockStream.AsWriter()
                        };
                        blockJob.Schedule(dirtySectorIndices.Length, 1).Complete();
                    }

                    if (movingSectorIndices.Length > 0 && settings.AlienMotionDirtyMask != DirtyFlags.None)
                    {
                        motionHash = BuildSpatialHash(allSectors, settings.SpatialCellSize, settings.DirtyHaloVoxels, true);
                        motionStream = new NativeStream(movingSectorIndices.Length, Allocator.TempJob);
                        var motionJob = new BuildMotionCandidatesJob
                        {
                            MovingSectorIndices = movingSectorIndices,
                            AllSectors = allSectors,
                            Entities = entityViews,
                            MotionSpatialHash = motionHash,
                            SpatialCellSize = settings.SpatialCellSize,
                            DirtyHaloVoxels = settings.DirtyHaloVoxels,
                            MotionThreshold = settings.MotionThreshold,
                            DeltaTime = settings.DeltaTime,
                            AlienMotionDirtyMask = settings.AlienMotionDirtyMask,
                            Candidates = motionStream.AsWriter()
                        };
                        motionJob.Schedule(movingSectorIndices.Length, 1).Complete();
                    }

                    NativeArray<int> blockCounts = default;
                    NativeArray<int> motionCounts = default;
                    int blockCount = blockStream.IsCreated ? CountStream(blockStream, dirtySectorIndices.Length, out blockCounts) : 0;
                    int motionCount = motionStream.IsCreated ? CountStream(motionStream, movingSectorIndices.Length, out motionCounts) : 0;
                    int totalCount = blockCount + motionCount;

                    try
                    {
                        if (totalCount > 0)
                        {
                            candidates.ResizeUninitialized(totalCount);
                            int offset = 0;
                            if (blockCount > 0)
                            {
                                CopyStream(blockStream, blockCounts, candidates.AsArray(), offset);
                                offset += blockCount;
                            }

                            if (motionCount > 0)
                            {
                                CopyStream(motionStream, motionCounts, candidates.AsArray(), offset);
                            }
                        }
                    }
                    finally
                    {
                        if (motionCounts.IsCreated) motionCounts.Dispose();
                        if (blockCounts.IsCreated) blockCounts.Dispose();
                    }
                }
                finally
                {
                    if (motionStream.IsCreated) motionStream.Dispose();
                    if (blockStream.IsCreated) blockStream.Dispose();
                }
            }
            finally
            {
                if (motionHash.IsCreated) motionHash.Dispose();
                if (currentHash.IsCreated) currentHash.Dispose();
            }

            return candidates;
        }

        // TODO: Rethink motion and swept behavior
        private static NativeParallelMultiHashMap<int3, int> BuildSpatialHash(
            NativeArray<AlienDirtySectorRecord> sectors,
            int cellSize,
            int halo,
            bool swept)
        {
            int capacity = 0;
            for (int i = 0; i < sectors.Length; i++)
            {
                if (sectors[i].Sector.Get().NonEmptyBrickCount == 0)
                {
                    continue;
                }

                AABB aabb = swept
                    ? AABB.Union(sectors[i].PreviousWorldAabb, sectors[i].CurrentWorldAabb).Inflated(halo)
                    : sectors[i].CurrentWorldAabb;
                capacity += CountCells(aabb, cellSize);
            }

            var hash = new NativeParallelMultiHashMap<int3, int>(math.max(capacity, 1), Allocator.TempJob);
            for (int sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
            {
                if (sectors[sectorIndex].Sector.Get().NonEmptyBrickCount == 0)
                {
                    continue;
                }

                AABB aabb = swept
                    ? AABB.Union(sectors[sectorIndex].PreviousWorldAabb, sectors[sectorIndex].CurrentWorldAabb).Inflated(halo)
                    : sectors[sectorIndex].CurrentWorldAabb;

                int3 minCell = WorldToCell(aabb.Min, cellSize);
                int3 maxCell = WorldToCell(aabb.Max - new float3(MaxBoundEpsilon), cellSize);
                for (int z = minCell.z; z <= maxCell.z; z++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    hash.Add(new int3(x, y, z), sectorIndex);
                }
            }

            return hash;
        }

        // TODO: Get rid of this
        private static int CountStream(NativeStream stream, int foreachCount, out NativeArray<int> counts)
        {
            counts = new NativeArray<int>(foreachCount, Allocator.TempJob);
            var total = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                new CountCandidatesInStreamJob
                {
                    Candidates = stream.AsReader(),
                    Counts = counts,
                    Total = total
                }.Run();

                return total[0];
            }
            finally
            {
                total.Dispose();
            }
        }

        // TODO: And this
        private static void CopyStream(NativeStream stream, NativeArray<int> counts, NativeArray<AlienDirtyCandidate> output, int outputOffset)
        {
            var starts = new NativeArray<int>(counts.Length, Allocator.TempJob);
            try
            {
                int running = outputOffset;
                for (int i = 0; i < counts.Length; i++)
                {
                    starts[i] = running;
                    running += counts[i];
                }

                new CopyCandidatesFromStreamJob
                {
                    Candidates = stream.AsReader(),
                    Starts = starts,
                    Output = output
                }.Run();
            }
            finally
            {
                starts.Dispose();
            }
        }

        /* TODO: Dedup keeps first candidate's flags rather than OR-ing them (AlienDirtyPropagation.cs:557–582). Today every duplicate (target, source, sourceBrick, mode) carries identical Flags so this is fine — but the moment a future caller emits two block-edit candidates with the same key but different flag bits, bits will be dropped silently. Either OR them (previous.Flags |= candidate.Flags; uniqueCandidates[uniqueIndex - 1] = previous;) or document the invariant. 
         */
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
                    previous = candidate;
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
        private static int CountCells(AABB aabb, int cellSize)
        {
            int3 minCell = WorldToCell(aabb.Min, cellSize);
            int3 maxCell = WorldToCell(aabb.Max - new float3(MaxBoundEpsilon), cellSize);
            int3 size = maxCell - minCell + 1;
            return math.max(1, size.x * size.y * size.z);
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
