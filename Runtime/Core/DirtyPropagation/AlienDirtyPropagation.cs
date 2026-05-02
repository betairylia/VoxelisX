using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
        public DirtyFlags AlienMotionDirtyMask;
        public int SpatialCellSize;
        public int DirtyHaloVoxels;
        public float MotionThreshold;
        public float DeltaTime;

        public static AlienDirtyPropagationSettings Default => new AlienDirtyPropagationSettings
        {
            FlagsToPropagate = DirtyFlags.All,
            AlienMotionDirtyMask = DirtyFlags.Reserved0,
            SpatialCellSize = 64,
            DirtyHaloVoxels = 1,
            MotionThreshold = 0f,
            DeltaTime = 1f
        };
    }

    public struct AlienDirtyEntityView
    {
        public int EntityId;
        public RigidTransform LocalToWorld;
        public float4x4 WorldToLocal;
        public RigidTransform PreviousLocalToWorld;
        public float4x4 PreviousWorldToLocal;
        public float3 LinearVelocity;
        public float3 AngularVelocity;
    }

    public struct AlienDirtyAabb
    {
        public float3 Min;
        public float3 Max;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AlienDirtyAabb Inflated(float amount)
        {
            return new AlienDirtyAabb { Min = Min - amount, Max = Max + amount };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AlienDirtyAabb Union(AlienDirtyAabb a, AlienDirtyAabb b)
        {
            return new AlienDirtyAabb { Min = math.min(a.Min, b.Min), Max = math.max(a.Max, b.Max) };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Overlaps(AlienDirtyAabb a, AlienDirtyAabb b)
        {
            return math.all(a.Min <= b.Max) && math.all(a.Max >= b.Min);
        }
    }

    public struct AlienDirtySectorRecord
    {
        public int EntityId;
        public int3 SectorPos;
        public SectorHandle Sector;
        public AlienDirtyAabb CurrentWorldAabb;
        public AlienDirtyAabb PreviousWorldAabb;
    }

    public struct AlienDirtyCandidate : IComparable<AlienDirtyCandidate>
    {
        public int TargetSectorIndex;
        public int SourceSectorIndex;
        public short SourceBrickIdx;
        public DirtyFlags Flags;
        public AlienDirtyCandidateMode Mode;

        public int CompareTo(AlienDirtyCandidate other)
        {
            int cmp = TargetSectorIndex.CompareTo(other.TargetSectorIndex);
            if (cmp != 0) return cmp;
            cmp = SourceSectorIndex.CompareTo(other.SourceSectorIndex);
            if (cmp != 0) return cmp;
            cmp = SourceBrickIdx.CompareTo(other.SourceBrickIdx);
            if (cmp != 0) return cmp;
            cmp = Mode.CompareTo(other.Mode);
            if (cmp != 0) return cmp;
            return ((ushort)Flags).CompareTo((ushort)other.Flags);
        }

        public bool SameDedupKey(AlienDirtyCandidate other)
        {
            return TargetSectorIndex == other.TargetSectorIndex
                   && SourceSectorIndex == other.SourceSectorIndex
                   && SourceBrickIdx == other.SourceBrickIdx
                   && Mode == other.Mode;
        }
    }

    public struct AlienDirtyCandidateRange
    {
        public int Start;
        public int Count;
    }

    public struct DirtyBrickInfo
    {
        public short BrickIdx;
        public DirtyFlags Flags;
    }

    public unsafe struct SectorDirtyBrickEnumerator
    {
        private Sector sector;
        private DirtyFlags mask;
        private int nextIndex;
        private DirtyBrickInfo current;

        public SectorDirtyBrickEnumerator(Sector sector, DirtyFlags mask)
        {
            this.sector = sector;
            this.mask = mask;
            nextIndex = 0;
            current = default;
        }

        public SectorDirtyBrickEnumerator GetEnumerator() => this;
        public DirtyBrickInfo Current => current;

        public bool MoveNext()
        {
            if ((sector.sectorDirtyFlags & (ushort)mask) == 0)
            {
                return false;
            }

            while (nextIndex < Sector.BRICKS_IN_SECTOR)
            {
                int brickIdx = nextIndex++;
                ushort flags = (ushort)(sector.brickDirtyFlags[brickIdx] & (ushort)mask);
                if (flags != 0 && sector.brickIdx[brickIdx] != Sector.BRICKID_EMPTY)
                {
                    current = new DirtyBrickInfo
                    {
                        BrickIdx = (short)brickIdx,
                        Flags = (DirtyFlags)flags
                    };
                    return true;
                }
            }

            return false;
        }
    }

    public static unsafe class AlienDirtyPropagation
    {
        public static void Propagate(NativeArray<VoxelEntityData> entities, AlienDirtyPropagationSettings settings)
        {
            settings = NormalizeSettings(settings);
            if (entities.Length <= 1)
            {
                RefreshAllocatedBrickLists(entities);
                return;
            }

            RefreshAllocatedBrickLists(entities);

            var entityViews = new NativeList<AlienDirtyEntityView>(entities.Length, Allocator.TempJob);
            var allSectors = new NativeList<AlienDirtySectorRecord>(Allocator.TempJob);
            var dirtySectorIndices = new NativeList<int>(Allocator.TempJob);
            var movingSectorIndices = new NativeList<int>(Allocator.TempJob);

            try
            {
                BuildRecords(entities, settings.FlagsToPropagate, ref entityViews, ref allSectors, ref dirtySectorIndices, ref movingSectorIndices);
                if (allSectors.Length == 0)
                {
                    return;
                }

                NativeList<AlienDirtyCandidate> candidates = BuildCandidates(
                    entityViews.AsArray(),
                    allSectors.AsArray(),
                    dirtySectorIndices.AsArray(),
                    movingSectorIndices.AsArray(),
                    settings);

                try
                {
                    if (candidates.Length == 0)
                    {
                        return;
                    }

                    candidates.Sort();

                    var uniqueCandidates = new NativeList<AlienDirtyCandidate>(candidates.Length, Allocator.TempJob);
                    var ranges = new NativeArray<AlienDirtyCandidateRange>(allSectors.Length, Allocator.TempJob);
                    var activeTargets = new NativeList<int>(Allocator.TempJob);

                    try
                    {
                        DeduplicateAndBuildRanges(candidates.AsArray(), ref uniqueCandidates, ranges, ref activeTargets);
                        if (activeTargets.Length == 0)
                        {
                            return;
                        }

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

        public static void Propagate(NativeList<VoxelEntityData> entities, AlienDirtyPropagationSettings settings)
        {
            Propagate(entities.AsArray(), settings);
        }

        private static AlienDirtyPropagationSettings NormalizeSettings(AlienDirtyPropagationSettings settings)
        {
            if (settings.FlagsToPropagate == DirtyFlags.None)
            {
                settings.FlagsToPropagate = DirtyFlags.All;
            }

            if (settings.AlienMotionDirtyMask == DirtyFlags.None)
            {
                settings.AlienMotionDirtyMask = DirtyFlags.Reserved0;
            }

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

        private static void RefreshAllocatedBrickLists(NativeArray<VoxelEntityData> entities)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                VoxelEntityData entity = entities[i];
                entity.RefreshAllocatedBrickLists();
            }
        }

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

                    if ((sectorHandle.Get().sectorDirtyFlags & (ushort)flagsToPropagate) != 0)
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

        private static NativeList<AlienDirtyCandidate> BuildCandidates(
            NativeArray<AlienDirtyEntityView> entityViews,
            NativeArray<AlienDirtySectorRecord> allSectors,
            NativeArray<int> dirtySectorIndices,
            NativeArray<int> movingSectorIndices,
            AlienDirtyPropagationSettings settings)
        {
            var currentHash = BuildSpatialHash(allSectors, settings.SpatialCellSize, settings.DirtyHaloVoxels, false);
            var motionHash = BuildSpatialHash(allSectors, settings.SpatialCellSize, settings.DirtyHaloVoxels, true);
            var candidates = new NativeList<AlienDirtyCandidate>(Allocator.TempJob);

            try
            {
                NativeStream blockStream = default;
                NativeStream motionStream = default;

                try
                {
                    if (dirtySectorIndices.Length > 0)
                    {
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

                    if (movingSectorIndices.Length > 0)
                    {
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

                    int blockCount = blockStream.IsCreated ? CountStream(blockStream, dirtySectorIndices.Length) : 0;
                    int motionCount = motionStream.IsCreated ? CountStream(motionStream, movingSectorIndices.Length) : 0;
                    int totalCount = blockCount + motionCount;

                    if (totalCount > 0)
                    {
                        candidates.ResizeUninitialized(totalCount);
                        int offset = 0;
                        if (blockCount > 0)
                        {
                            CopyStream(blockStream, dirtySectorIndices.Length, candidates.AsArray(), offset);
                            offset += blockCount;
                        }

                        if (motionCount > 0)
                        {
                            CopyStream(motionStream, movingSectorIndices.Length, candidates.AsArray(), offset);
                        }
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

        private static NativeParallelMultiHashMap<int3, int> BuildSpatialHash(
            NativeArray<AlienDirtySectorRecord> sectors,
            int cellSize,
            int halo,
            bool swept)
        {
            int capacity = 0;
            for (int i = 0; i < sectors.Length; i++)
            {
                AlienDirtyAabb aabb = swept
                    ? AlienDirtyAabb.Union(sectors[i].PreviousWorldAabb, sectors[i].CurrentWorldAabb).Inflated(halo)
                    : sectors[i].CurrentWorldAabb;
                capacity += CountCells(aabb, cellSize);
            }

            var hash = new NativeParallelMultiHashMap<int3, int>(math.max(capacity, 1), Allocator.TempJob);
            for (int sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
            {
                AlienDirtyAabb aabb = swept
                    ? AlienDirtyAabb.Union(sectors[sectorIndex].PreviousWorldAabb, sectors[sectorIndex].CurrentWorldAabb).Inflated(halo)
                    : sectors[sectorIndex].CurrentWorldAabb;

                int3 minCell = WorldToCell(aabb.Min, cellSize);
                int3 maxCell = WorldToCell(aabb.Max - new float3(1e-4f), cellSize);
                for (int z = minCell.z; z <= maxCell.z; z++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    hash.Add(new int3(x, y, z), sectorIndex);
                }
            }

            return hash;
        }

        private static int CountStream(NativeStream stream, int foreachCount)
        {
            var counts = new NativeArray<int>(foreachCount, Allocator.TempJob);
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
                counts.Dispose();
            }
        }

        private static void CopyStream(NativeStream stream, int foreachCount, NativeArray<AlienDirtyCandidate> output, int outputOffset)
        {
            var starts = new NativeArray<int>(foreachCount, Allocator.TempJob);
            var counts = new NativeArray<int>(foreachCount, Allocator.TempJob);
            var total = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                new CountCandidatesInStreamJob
                {
                    Candidates = stream.AsReader(),
                    Counts = counts,
                    Total = total
                }.Run();

                int running = outputOffset;
                for (int i = 0; i < foreachCount; i++)
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
                total.Dispose();
                counts.Dispose();
                starts.Dispose();
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
        private static AlienDirtyAabb ComputeSectorWorldAabb(int3 sectorPos, RigidTransform transform)
        {
            float3 localMin = sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
            float3 localMax = localMin + Sector.SECTOR_SIZE_IN_BLOCKS;
            float3 worldMin = new float3(float.PositiveInfinity);
            float3 worldMax = new float3(float.NegativeInfinity);

            for (int mask = 0; mask < 8; mask++)
            {
                float3 localCorner = new float3(
                    (mask & 1) == 0 ? localMin.x : localMax.x,
                    (mask & 2) == 0 ? localMin.y : localMax.y,
                    (mask & 4) == 0 ? localMin.z : localMax.z);
                float3 worldCorner = math.transform(transform, localCorner);
                worldMin = math.min(worldMin, worldCorner);
                worldMax = math.max(worldMax, worldCorner);
            }

            return new AlienDirtyAabb { Min = worldMin, Max = worldMax };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountCells(AlienDirtyAabb aabb, int cellSize)
        {
            int3 minCell = WorldToCell(aabb.Min, cellSize);
            int3 maxCell = WorldToCell(aabb.Max - new float3(1e-4f), cellSize);
            int3 size = maxCell - minCell + 1;
            return math.max(1, size.x * size.y * size.z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int3 WorldToCell(float3 world, int cellSize)
        {
            return (int3)math.floor(world / cellSize);
        }
    }

    [BurstCompile]
    public struct CountCandidatesInStreamJob : IJob
    {
        public NativeStream.Reader Candidates;
        public NativeArray<int> Counts;
        public NativeArray<int> Total;

        public void Execute()
        {
            int total = 0;
            for (int i = 0; i < Counts.Length; i++)
            {
                int count = Candidates.BeginForEachIndex(i);
                Counts[i] = count;
                total += count;
            }

            Total[0] = total;
        }
    }

    [BurstCompile]
    public struct CopyCandidatesFromStreamJob : IJob
    {
        public NativeStream.Reader Candidates;
        [ReadOnly] public NativeArray<int> Starts;
        public NativeArray<AlienDirtyCandidate> Output;

        public void Execute()
        {
            for (int i = 0; i < Starts.Length; i++)
            {
                int count = Candidates.BeginForEachIndex(i);
                int writeIndex = Starts[i];
                for (int c = 0; c < count; c++)
                {
                    Output[writeIndex++] = Candidates.Read<AlienDirtyCandidate>();
                }

                Candidates.EndForEachIndex();
            }
        }
    }

    [BurstCompile]
    public unsafe struct BuildBlockEditCandidatesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> DirtySectorIndices;
        [ReadOnly] public NativeArray<AlienDirtySectorRecord> AllSectors;
        [ReadOnly] public NativeArray<AlienDirtyEntityView> Entities;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> SpatialHash;
        [ReadOnly] public int SpatialCellSize;
        [ReadOnly] public int DirtyHaloVoxels;
        [ReadOnly] public DirtyFlags FlagsToPropagate;
        public NativeStream.Writer Candidates;

        public void Execute(int sourceSectorListIndex)
        {
            NativeStream.Writer writer = Candidates;
            writer.BeginForEachIndex(sourceSectorListIndex);

            int sourceSectorIndex = DirtySectorIndices[sourceSectorListIndex];
            AlienDirtySectorRecord source = AllSectors[sourceSectorIndex];
            Sector sourceSector = source.Sector.Get();

            foreach (DirtyBrickInfo dirtyBrick in new SectorDirtyBrickEnumerator(sourceSector, FlagsToPropagate))
            {
                AlienDirtyAabb sourceWorldAabb = BrickWorldAabb(source, Entities[source.EntityId], dirtyBrick.BrickIdx, DirtyHaloVoxels);
                QueryAndWriteBlockCandidates(ref writer, sourceSectorIndex, source, sourceWorldAabb, dirtyBrick);
            }

            writer.EndForEachIndex();
        }

        private void QueryAndWriteBlockCandidates(
            ref NativeStream.Writer writer,
            int sourceSectorIndex,
            AlienDirtySectorRecord source,
            AlienDirtyAabb sourceWorldAabb,
            DirtyBrickInfo dirtyBrick)
        {
            int3 minCell = WorldToCell(sourceWorldAabb.Min, SpatialCellSize);
            int3 maxCell = WorldToCell(sourceWorldAabb.Max - new float3(1e-4f), SpatialCellSize);

            for (int z = minCell.z; z <= maxCell.z; z++)
            for (int y = minCell.y; y <= maxCell.y; y++)
            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                int3 cell = new int3(x, y, z);
                if (!SpatialHash.TryGetFirstValue(cell, out int targetSectorIndex, out var iterator))
                {
                    continue;
                }

                do
                {
                    AlienDirtySectorRecord target = AllSectors[targetSectorIndex];
                    if (target.EntityId == source.EntityId)
                    {
                        continue;
                    }

                    if (!AlienDirtyAabb.Overlaps(sourceWorldAabb, target.CurrentWorldAabb))
                    {
                        continue;
                    }

                    writer.Write(new AlienDirtyCandidate
                    {
                        TargetSectorIndex = targetSectorIndex,
                        SourceSectorIndex = sourceSectorIndex,
                        SourceBrickIdx = dirtyBrick.BrickIdx,
                        Flags = dirtyBrick.Flags,
                        Mode = AlienDirtyCandidateMode.BlockEdit
                    });
                }
                while (SpatialHash.TryGetNextValue(out targetSectorIndex, ref iterator));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static AlienDirtyAabb BrickWorldAabb(AlienDirtySectorRecord sector, AlienDirtyEntityView entity, short brickIdx, int halo)
        {
            int3 brickPos = Sector.ToBrickPos(brickIdx);
            float3 localMin = sector.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + brickPos * Sector.SIZE_IN_BLOCKS - halo;
            float3 localMax = sector.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + (brickPos + 1) * Sector.SIZE_IN_BLOCKS + halo;
            float3 worldMin = new float3(float.PositiveInfinity);
            float3 worldMax = new float3(float.NegativeInfinity);

            for (int mask = 0; mask < 8; mask++)
            {
                float3 localCorner = new float3(
                    (mask & 1) == 0 ? localMin.x : localMax.x,
                    (mask & 2) == 0 ? localMin.y : localMax.y,
                    (mask & 4) == 0 ? localMin.z : localMax.z);
                float3 worldCorner = math.transform(entity.LocalToWorld, localCorner);
                worldMin = math.min(worldMin, worldCorner);
                worldMax = math.max(worldMax, worldCorner);
            }

            return new AlienDirtyAabb { Min = worldMin, Max = worldMax };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int3 WorldToCell(float3 world, int cellSize)
        {
            return (int3)math.floor(world / cellSize);
        }
    }

    [BurstCompile]
    public unsafe struct BuildMotionCandidatesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> MovingSectorIndices;
        [ReadOnly] public NativeArray<AlienDirtySectorRecord> AllSectors;
        [ReadOnly] public NativeArray<AlienDirtyEntityView> Entities;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> MotionSpatialHash;
        [ReadOnly] public int SpatialCellSize;
        [ReadOnly] public int DirtyHaloVoxels;
        [ReadOnly] public float MotionThreshold;
        [ReadOnly] public float DeltaTime;
        [ReadOnly] public DirtyFlags AlienMotionDirtyMask;
        public NativeStream.Writer Candidates;

        public void Execute(int movingSectorListIndex)
        {
            NativeStream.Writer writer = Candidates;
            writer.BeginForEachIndex(movingSectorListIndex);

            int movingSectorIndex = MovingSectorIndices[movingSectorListIndex];
            AlienDirtySectorRecord moving = AllSectors[movingSectorIndex];
            if (!HasAllocatedBricks(moving.Sector.Get()))
            {
                writer.EndForEachIndex();
                return;
            }

            AlienDirtyAabb swept = AlienDirtyAabb.Union(moving.PreviousWorldAabb, moving.CurrentWorldAabb).Inflated(DirtyHaloVoxels);
            int3 minCell = WorldToCell(swept.Min, SpatialCellSize);
            int3 maxCell = WorldToCell(swept.Max - new float3(1e-4f), SpatialCellSize);

            for (int z = minCell.z; z <= maxCell.z; z++)
            for (int y = minCell.y; y <= maxCell.y; y++)
            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                int3 cell = new int3(x, y, z);
                if (!MotionSpatialHash.TryGetFirstValue(cell, out int otherSectorIndex, out var iterator))
                {
                    continue;
                }

                do
                {
                    AlienDirtySectorRecord other = AllSectors[otherSectorIndex];
                    if (other.EntityId == moving.EntityId)
                    {
                        continue;
                    }

                    if (!HasAllocatedBricks(other.Sector.Get()))
                    {
                        continue;
                    }

                    if (!AlienDirtyAabb.Overlaps(swept, other.CurrentWorldAabb)
                        && !AlienDirtyAabb.Overlaps(swept, other.PreviousWorldAabb))
                    {
                        continue;
                    }

                    if (!RelativeMotionExceedsThreshold(Entities[moving.EntityId], Entities[other.EntityId], MotionThreshold, DeltaTime))
                    {
                        continue;
                    }

                    writer.Write(new AlienDirtyCandidate
                    {
                        TargetSectorIndex = otherSectorIndex,
                        SourceSectorIndex = movingSectorIndex,
                        SourceBrickIdx = -1,
                        Flags = AlienMotionDirtyMask,
                        Mode = AlienDirtyCandidateMode.Motion
                    });

                    writer.Write(new AlienDirtyCandidate
                    {
                        TargetSectorIndex = movingSectorIndex,
                        SourceSectorIndex = otherSectorIndex,
                        SourceBrickIdx = -1,
                        Flags = AlienMotionDirtyMask,
                        Mode = AlienDirtyCandidateMode.Motion
                    });
                }
                while (MotionSpatialHash.TryGetNextValue(out otherSectorIndex, ref iterator));
            }

            writer.EndForEachIndex();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasAllocatedBricks(Sector sector)
        {
            return sector.NonEmptyBricks.IsCreated && sector.NonEmptyBricks.Length > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RelativeMotionExceedsThreshold(AlienDirtyEntityView a, AlienDirtyEntityView b, float threshold, float deltaTime)
        {
            float linear = math.length(a.LinearVelocity - b.LinearVelocity) * deltaTime;
            float influenceRadius = Sector.SECTOR_SIZE_IN_BLOCKS;
            // TODO: Tighten this with pair-local lever-arm displacement instead of waking on any angular motion.
            float angular = (math.length(a.AngularVelocity) + math.length(b.AngularVelocity)) * influenceRadius * deltaTime;
            return linear + angular > threshold;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int3 WorldToCell(float3 world, int cellSize)
        {
            return (int3)math.floor(world / cellSize);
        }
    }

    [BurstCompile]
    public unsafe struct MarkAlienRequireUpdatesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> ActiveTargetSectors;
        [ReadOnly] public NativeArray<AlienDirtyCandidateRange> CandidateRanges;
        [ReadOnly] public NativeArray<AlienDirtyCandidate> Candidates;
        [ReadOnly] public NativeArray<AlienDirtySectorRecord> Sectors;
        [ReadOnly] public NativeArray<AlienDirtyEntityView> Entities;
        [ReadOnly] public int DirtyHaloVoxels;

        public void Execute(int activeTargetIndex)
        {
            int targetSectorIndex = ActiveTargetSectors[activeTargetIndex];
            AlienDirtySectorRecord target = Sectors[targetSectorIndex];
            AlienDirtyCandidateRange range = CandidateRanges[targetSectorIndex];
            if (range.Start < 0 || range.Count <= 0)
            {
                return;
            }

            for (int i = range.Start; i < range.Start + range.Count; i++)
            {
                AlienDirtyCandidate candidate = Candidates[i];
                AlienDirtySectorRecord source = Sectors[candidate.SourceSectorIndex];

                if (candidate.Mode == AlienDirtyCandidateMode.BlockEdit)
                {
                    if (TryGetBrickTargetRange(source, target, candidate.SourceBrickIdx, DirtyHaloVoxels, false, out int3 minBrick, out int3 maxBrick))
                    {
                        MarkAllocatedTargetBrickRange(target.Sector, minBrick, maxBrick, candidate.Flags);
                    }
                }
                else
                {
                    Sector sourceSector = source.Sector.Get();
                    for (int sourceBrickListIndex = 0; sourceBrickListIndex < sourceSector.NonEmptyBricks.Length; sourceBrickListIndex++)
                    {
                        short sourceBrick = sourceSector.NonEmptyBricks[sourceBrickListIndex];
                        bool hasCurrent = TryGetBrickTargetRange(source, target, sourceBrick, DirtyHaloVoxels, false, out int3 currentMin, out int3 currentMax);
                        bool hasPrevious = TryGetBrickTargetRange(source, target, sourceBrick, DirtyHaloVoxels, true, out int3 previousMin, out int3 previousMax);

                        if (hasCurrent)
                        {
                            MarkAllocatedTargetBrickRange(target.Sector, currentMin, currentMax, candidate.Flags);
                        }

                        if (hasPrevious)
                        {
                            MarkAllocatedTargetBrickRange(target.Sector, previousMin, previousMax, candidate.Flags);
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetBrickTargetRange(
            AlienDirtySectorRecord source,
            AlienDirtySectorRecord target,
            short sourceBrickIdx,
            int halo,
            bool previous,
            out int3 minBrick,
            out int3 maxBrick)
        {
            AlienDirtyEntityView sourceEntity = Entities[source.EntityId];
            AlienDirtyEntityView targetEntity = Entities[target.EntityId];
            RigidTransform sourceTransform = previous ? sourceEntity.PreviousLocalToWorld : sourceEntity.LocalToWorld;
            RigidTransform targetTransform = previous ? targetEntity.PreviousLocalToWorld : targetEntity.LocalToWorld;
            float4x4 targetWorldToLocal = previous ? targetEntity.PreviousWorldToLocal : targetEntity.WorldToLocal;

            int3 sourceBrickPos = Sector.ToBrickPos(sourceBrickIdx);
            float3 sourceLocalCenter = source.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS
                                       + sourceBrickPos * Sector.SIZE_IN_BLOCKS
                                       + new float3(Sector.SIZE_IN_BLOCKS * 0.5f);
            float sourceHalfExtent = Sector.SIZE_IN_BLOCKS * 0.5f + halo;

            float3 worldCenter = math.transform(sourceTransform, sourceLocalCenter);
            float3 targetLocalCenter = math.mul(targetWorldToLocal, new float4(worldCenter, 1f)).xyz;
            quaternion relativeRotation = math.mul(math.inverse(targetTransform.rot), sourceTransform.rot);
            float3 targetHalfExtent = AbsMul(new float3x3(relativeRotation), new float3(sourceHalfExtent));

            float3 targetSectorOrigin = target.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
            float3 targetMin = targetLocalCenter - targetHalfExtent - targetSectorOrigin;
            float3 targetMax = targetLocalCenter + targetHalfExtent - targetSectorOrigin;

            minBrick = (int3)math.floor(targetMin / Sector.SIZE_IN_BLOCKS);
            maxBrick = (int3)math.floor((targetMax - new float3(1e-4f)) / Sector.SIZE_IN_BLOCKS);

            if (math.any(maxBrick < 0) || math.any(minBrick >= Sector.SIZE_IN_BRICKS))
            {
                return false;
            }

            minBrick = math.max(minBrick, int3.zero);
            maxBrick = math.min(maxBrick, new int3(Sector.SIZE_IN_BRICKS - 1));
            return math.all(minBrick <= maxBrick);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float3 AbsMul(float3x3 matrix, float3 value)
        {
            return new float3(
                math.abs(matrix.c0.x) * value.x + math.abs(matrix.c1.x) * value.y + math.abs(matrix.c2.x) * value.z,
                math.abs(matrix.c0.y) * value.x + math.abs(matrix.c1.y) * value.y + math.abs(matrix.c2.y) * value.z,
                math.abs(matrix.c0.z) * value.x + math.abs(matrix.c1.z) * value.y + math.abs(matrix.c2.z) * value.z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkAllocatedTargetBrickRange(SectorHandle targetHandle, int3 minBrick, int3 maxBrick, DirtyFlags flags)
        {
            ref Sector target = ref targetHandle.Get();
            for (int z = minBrick.z; z <= maxBrick.z; z++)
            for (int y = minBrick.y; y <= maxBrick.y; y++)
            for (int x = minBrick.x; x <= maxBrick.x; x++)
            {
                int brickIdx = Sector.ToBrickIdx(x, y, z);
                if (target.brickIdx[brickIdx] == Sector.BRICKID_EMPTY)
                {
                    continue;
                }

                target.brickRequireUpdateFlags[brickIdx] |= (ushort)flags;
                target.sectorRequireUpdateFlags |= (ushort)flags;
            }
        }
    }
}
