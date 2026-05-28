using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Voxelis.Mathematics;

namespace Voxelis
{
    public struct AlienDirtyEntityView
    {
        public int LocalIndex;
        public RigidTransform LocalToWorld;
        public float4x4 WorldToLocal;
        public RigidTransform PreviousLocalToWorld;
        public float4x4 PreviousWorldToLocal;
        public float3 LinearVelocity;
        public float3 AngularVelocity;
        public bool IsMoving;
    }

    public struct AlienDirtySectorRecord
    {
        public int EntityId;
        public int3 SectorPos;
        public SectorHandle Sector;
        public AABB CurrentWorldAabb;
        public AABB PreviousWorldAabb;
    }

    public struct ActiveSectorInfo
    {
        public int SectorIndex;
        public bool HasDirtyBricks;
        public bool EntityIsMoving;
    }

    public struct AlienDirtyCandidate : IComparable<AlienDirtyCandidate>
    {
        public int TargetSectorIndex;
        public int SourceSectorIndex;
        public short SourceBrickIdx;
        public DirtyFlags Flags;

        public int CompareTo(AlienDirtyCandidate other)
        {
            int cmp = TargetSectorIndex.CompareTo(other.TargetSectorIndex);
            if (cmp != 0) return cmp;
            cmp = SourceSectorIndex.CompareTo(other.SourceSectorIndex);
            if (cmp != 0) return cmp;
            cmp = SourceBrickIdx.CompareTo(other.SourceBrickIdx);
            if (cmp != 0) return cmp;
            return ((ushort)Flags).CompareTo((ushort)other.Flags);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SameDedupKey(AlienDirtyCandidate other)
        {
            return TargetSectorIndex == other.TargetSectorIndex
                   && SourceSectorIndex == other.SourceSectorIndex
                   && SourceBrickIdx == other.SourceBrickIdx;
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

    public static unsafe partial class AlienDirtyPropagation
    {
        [BurstCompile]
        public unsafe struct BuildRecordsJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] [ReadOnly] public VoxelEntityData* Entities;
            [ReadOnly] public int EntityCount;
            [ReadOnly] public DirtyFlags FlagsToPropagate;
            public NativeList<AlienDirtyEntityView> EntityViews;
            public NativeList<AlienDirtySectorRecord> AllSectors;
            public NativeList<ActiveSectorInfo> ActiveSectors;

            public void Execute()
            {
                for (int entityIndex = 0; entityIndex < EntityCount; entityIndex++)
                {
                    VoxelEntityData entity = Entities[entityIndex];
                    float4x4 localToWorld = float4x4.TRS(entity.transform.pos, entity.transform.rot, 1f);
                    float4x4 previousLocalToWorld =
                        float4x4.TRS(entity.previousTransform.pos, entity.previousTransform.rot, 1f);

                    bool entityMoving = math.lengthsq(entity.linearVelocity) > 0f ||
                                        math.lengthsq(entity.angularVelocity) > 0f;

                    EntityViews.Add(new AlienDirtyEntityView
                    {
                        LocalIndex = entityIndex,
                        LocalToWorld = entity.transform,
                        WorldToLocal = math.inverse(localToWorld),
                        PreviousLocalToWorld = entity.previousTransform,
                        PreviousWorldToLocal = math.inverse(previousLocalToWorld),
                        LinearVelocity = entity.linearVelocity,
                        AngularVelocity = entity.angularVelocity,
                        IsMoving = entityMoving,
                    });

                    foreach (var kvp in entity.sectors)
                    {
                        int sectorIndex = AllSectors.Length;
                        SectorHandle sectorHandle = kvp.Value;
                        int3 sectorPos = kvp.Key;

                        AllSectors.Add(new AlienDirtySectorRecord
                        {
                            EntityId = entityIndex,
                            SectorPos = sectorPos,
                            Sector = sectorHandle,
                            CurrentWorldAabb = ComputeSectorWorldAabb(sectorPos, entity.transform),
                            PreviousWorldAabb = ComputeSectorWorldAabb(sectorPos, entity.previousTransform)
                        });

                        Sector sector = sectorHandle.Get();
                        if (sector.NonEmptyBrickCount == 0)
                            continue;

                        bool hasDirty = (sector.sectorDirtyFlags & (ushort)FlagsToPropagate) != 0;
                        if (hasDirty || entityMoving)
                        {
                            ActiveSectors.Add(new ActiveSectorInfo
                            {
                                SectorIndex = sectorIndex,
                                HasDirtyBricks = hasDirty,
                                EntityIsMoving = entityMoving,
                            });
                        }
                    }
                }
            }
        }

        [BurstCompile]
        public unsafe struct BuildCandidatesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ActiveSectorInfo> ActiveSectors;
            [ReadOnly] public NativeArray<AlienDirtySectorRecord> AllSectors;
            [ReadOnly] public NativeArray<AlienDirtyEntityView> Entities;
            [ReadOnly] public NativeParallelMultiHashMap<int3, int> SpatialHash;
            [ReadOnly] public int SpatialCellSize;
            [ReadOnly] public int DirtyHaloVoxels;
            [ReadOnly] public DirtyFlags FlagsToPropagate;
            [ReadOnly] public DirtyFlags AlienMotionDirtyMask;
            public NativeStream.Writer Candidates;

            public void Execute(int activeIdx)
            {
                NativeStream.Writer writer = Candidates;
                writer.BeginForEachIndex(activeIdx);

                ActiveSectorInfo info = ActiveSectors[activeIdx];
                int sourceSectorIndex = info.SectorIndex;
                AlienDirtySectorRecord source = AllSectors[sourceSectorIndex];
                Sector sourceSector = source.Sector.Get();

                if (info.HasDirtyBricks)
                {
                    foreach (DirtyBrickInfo dirtyBrick in new SectorDirtyBrickEnumerator(sourceSector, FlagsToPropagate))
                    {
                        AABB brickAabb = ComputeBrickWorldAabb(source, Entities[source.EntityId],
                            dirtyBrick.BrickIdx, DirtyHaloVoxels, false);
                        QueryAndEmitCandidates(ref writer, sourceSectorIndex, source,
                            brickAabb, dirtyBrick.BrickIdx, dirtyBrick.Flags, false);
                    }
                }

                if (info.EntityIsMoving && AlienMotionDirtyMask != DirtyFlags.None)
                {
                    AlienDirtyEntityView sourceEntity = Entities[source.EntityId];

                    if (sourceSector.NonEmptyBrickCount > 0)
                    {
                        for (short brickSlot = 0; brickSlot < Sector.BRICKS_IN_SECTOR; brickSlot++)
                        {
                            if (sourceSector.brickIdx[brickSlot] == Sector.BRICKID_EMPTY)
                                continue;

                            AABB currentAabb = ComputeBrickWorldAabb(source, sourceEntity,
                                brickSlot, DirtyHaloVoxels, false);
                            QueryAndEmitCandidates(ref writer, sourceSectorIndex, source,
                                currentAabb, brickSlot, AlienMotionDirtyMask, true);

                            AABB previousAabb = ComputeBrickWorldAabb(source, sourceEntity,
                                brickSlot, DirtyHaloVoxels, true);
                            QueryAndEmitCandidates(ref writer, sourceSectorIndex, source,
                                previousAabb, brickSlot, AlienMotionDirtyMask, true);
                        }
                    }

                    EmitReverseCandidates(ref writer, sourceSectorIndex, source);
                }

                writer.EndForEachIndex();
            }

            private void QueryAndEmitCandidates(
                ref NativeStream.Writer writer,
                int sourceSectorIndex,
                AlienDirtySectorRecord source,
                AABB sourceWorldAabb,
                short sourceBrickIdx,
                DirtyFlags flags,
                bool checkRelativeMotion)
            {
                int3 minCell = AlienDirtyPropagation.WorldToCell(sourceWorldAabb.Min, SpatialCellSize);
                int3 maxCell = AlienDirtyPropagation.WorldToCell(
                    AlienDirtyPropagation.MaxExclusive(sourceWorldAabb.Max), SpatialCellSize);

                for (int z = minCell.z; z <= maxCell.z; z++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    int3 cell = new int3(x, y, z);
                    if (!SpatialHash.TryGetFirstValue(cell, out int targetSectorIndex, out var iterator))
                        continue;

                    do
                    {
                        AlienDirtySectorRecord target = AllSectors[targetSectorIndex];
                        if (target.EntityId == source.EntityId)
                            continue;

                        if (checkRelativeMotion &&
                            !HasRelativeMotion(Entities[source.EntityId], Entities[target.EntityId]))
                            continue;

                        if (!AABB.Overlaps(sourceWorldAabb, target.CurrentWorldAabb) &&
                            !AABB.Overlaps(sourceWorldAabb, target.PreviousWorldAabb))
                            continue;

                        writer.Write(new AlienDirtyCandidate
                        {
                            TargetSectorIndex = targetSectorIndex,
                            SourceSectorIndex = sourceSectorIndex,
                            SourceBrickIdx = sourceBrickIdx,
                            Flags = flags
                        });
                    } while (SpatialHash.TryGetNextValue(out targetSectorIndex, ref iterator));
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool HasRelativeMotion(AlienDirtyEntityView a, AlienDirtyEntityView b)
            {
                return math.lengthsq(a.LinearVelocity - b.LinearVelocity) > 0f ||
                       math.lengthsq(a.AngularVelocity - b.AngularVelocity) > 0f;
            }

            private void EmitReverseCandidates(
                ref NativeStream.Writer writer,
                int movingSectorIndex,
                AlienDirtySectorRecord moving)
            {
                AABB queryAabb = AABB.Union(moving.CurrentWorldAabb, moving.PreviousWorldAabb)
                    .Inflated(DirtyHaloVoxels);
                int3 minCell = AlienDirtyPropagation.WorldToCell(queryAabb.Min, SpatialCellSize);
                int3 maxCell = AlienDirtyPropagation.WorldToCell(
                    AlienDirtyPropagation.MaxExclusive(queryAabb.Max), SpatialCellSize);

                for (int z = minCell.z; z <= maxCell.z; z++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                for (int x = minCell.x; x <= maxCell.x; x++)
                {
                    int3 cell = new int3(x, y, z);
                    if (!SpatialHash.TryGetFirstValue(cell, out int otherSectorIndex, out var iterator))
                        continue;

                    do
                    {
                        AlienDirtySectorRecord other = AllSectors[otherSectorIndex];
                        if (other.EntityId == moving.EntityId)
                            continue;

                        if (!HasRelativeMotion(Entities[moving.EntityId], Entities[other.EntityId]))
                            continue;

                        if (other.Sector.Get().NonEmptyBrickCount == 0)
                            continue;

                        if (!AABB.Overlaps(queryAabb, other.CurrentWorldAabb) &&
                            !AABB.Overlaps(queryAabb, other.PreviousWorldAabb))
                            continue;

                        writer.Write(new AlienDirtyCandidate
                        {
                            TargetSectorIndex = movingSectorIndex,
                            SourceSectorIndex = otherSectorIndex,
                            SourceBrickIdx = -1,
                            Flags = AlienMotionDirtyMask
                        });
                    } while (SpatialHash.TryGetNextValue(out otherSectorIndex, ref iterator));
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static AABB ComputeBrickWorldAabb(AlienDirtySectorRecord sector, AlienDirtyEntityView entity,
                short brickIdx, int halo, bool previous)
            {
                int3 brickPos = Sector.ToBrickPos(brickIdx);
                float3 localMin = sector.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS
                                  + brickPos * Sector.SIZE_IN_BLOCKS - halo;
                float3 localMax = sector.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS
                                  + (brickPos + 1) * Sector.SIZE_IN_BLOCKS + halo;
                RigidTransform transform = previous ? entity.PreviousLocalToWorld : entity.LocalToWorld;
                return AABB.Transform(new AABB { Min = localMin, Max = localMax }, transform);
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
                    return;

                for (int i = range.Start; i < range.Start + range.Count; i++)
                {
                    AlienDirtyCandidate candidate = Candidates[i];
                    AlienDirtySectorRecord source = Sectors[candidate.SourceSectorIndex];

                    if (candidate.SourceBrickIdx >= 0)
                    {
                        MarkSpecificBrick(source, target, candidate.SourceBrickIdx, candidate.Flags);
                    }
                    else
                    {
                        MarkAllAllocatedBricks(source, target, candidate.Flags);
                    }
                }
            }

            private void MarkSpecificBrick(AlienDirtySectorRecord source, AlienDirtySectorRecord target,
                short sourceBrickIdx, DirtyFlags flags)
            {
                if (TryGetBrickTargetRange(source, target, sourceBrickIdx, DirtyHaloVoxels,
                        false, out int3 currentMin, out int3 currentMax))
                {
                    MarkAllocatedTargetBrickRange(target.Sector, currentMin, currentMax, flags);
                }

                AlienDirtyEntityView sourceEntity = Entities[source.EntityId];
                AlienDirtyEntityView targetEntity = Entities[target.EntityId];
                if (sourceEntity.IsMoving || targetEntity.IsMoving)
                {
                    if (TryGetBrickTargetRange(source, target, sourceBrickIdx, DirtyHaloVoxels,
                            true, out int3 prevMin, out int3 prevMax))
                    {
                        MarkAllocatedTargetBrickRange(target.Sector, prevMin, prevMax, flags);
                    }
                }
            }

            private void MarkAllAllocatedBricks(AlienDirtySectorRecord source, AlienDirtySectorRecord target,
                DirtyFlags flags)
            {
                Sector sourceSector = source.Sector.Get();
                for (short sourceBrick = 0; sourceBrick < Sector.BRICKS_IN_SECTOR; sourceBrick++)
                {
                    if (sourceSector.brickIdx[sourceBrick] == Sector.BRICKID_EMPTY)
                        continue;

                    if (TryGetBrickTargetRange(source, target, sourceBrick, DirtyHaloVoxels,
                            false, out int3 currentMin, out int3 currentMax))
                    {
                        MarkAllocatedTargetBrickRange(target.Sector, currentMin, currentMax, flags);
                    }

                    if (TryGetBrickTargetRange(source, target, sourceBrick, DirtyHaloVoxels,
                            true, out int3 prevMin, out int3 prevMax))
                    {
                        MarkAllocatedTargetBrickRange(target.Sector, prevMin, prevMax, flags);
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
                RigidTransform sourceTransform =
                    previous ? sourceEntity.PreviousLocalToWorld : sourceEntity.LocalToWorld;
                RigidTransform targetTransform =
                    previous ? targetEntity.PreviousLocalToWorld : targetEntity.LocalToWorld;
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
                maxBrick = (int3)math.floor(AlienDirtyPropagation.MaxExclusive(targetMax) / Sector.SIZE_IN_BLOCKS);

                if (math.any(maxBrick < 0) || math.any(minBrick >= Sector.SIZE_IN_BRICKS))
                    return false;

                minBrick = math.max(minBrick, int3.zero);
                maxBrick = math.min(maxBrick, new int3(Sector.SIZE_IN_BRICKS - 1));
                return math.all(minBrick <= maxBrick);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static float3 AbsMul(float3x3 matrix, float3 value)
            {
                return new float3(
                    math.abs(matrix.c0.x) * value.x + math.abs(matrix.c1.x) * value.y +
                    math.abs(matrix.c2.x) * value.z,
                    math.abs(matrix.c0.y) * value.x + math.abs(matrix.c1.y) * value.y +
                    math.abs(matrix.c2.y) * value.z,
                    math.abs(matrix.c0.z) * value.x + math.abs(matrix.c1.z) * value.y +
                    math.abs(matrix.c2.z) * value.z);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void MarkAllocatedTargetBrickRange(SectorHandle targetHandle, int3 minBrick, int3 maxBrick,
                DirtyFlags flags)
            {
                ref Sector target = ref targetHandle.Get();
                for (int z = minBrick.z; z <= maxBrick.z; z++)
                for (int y = minBrick.y; y <= maxBrick.y; y++)
                for (int x = minBrick.x; x <= maxBrick.x; x++)
                {
                    int brickIdx = Sector.ToBrickIdx(x, y, z);
                    if (target.brickIdx[brickIdx] == Sector.BRICKID_EMPTY)
                        continue;

                    target.brickRequireUpdateFlags[brickIdx] |= (ushort)flags;
                    target.sectorRequireUpdateFlags |= (ushort)flags;
                }
            }
        }
    }
}
