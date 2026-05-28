using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxelis.Mathematics;

namespace Voxelis
{
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

    public struct AlienDirtySectorRecord
    {
        public int EntityId;
        public int3 SectorPos;
        public SectorHandle Sector;
        public AABB CurrentWorldAabb;
        public AABB PreviousWorldAabb;
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

    // TODO: A bit bloaty
    public struct AlienDirtyCandidateRange
    {
        public int Start;
        public int Count;
    }

    // TODO: Should similar or merge to BrickIterator?
    public struct DirtyBrickInfo
    {
        public short BrickIdx;
        public DirtyFlags Flags;
    }

    public static unsafe partial class AlienDirtyPropagation
    {
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
                    AABB sourceWorldAabb = ComputeBrickWorldAabb(source, Entities[source.EntityId], dirtyBrick.BrickIdx,
                        DirtyHaloVoxels);
                    QueryAndWriteBlockCandidates(ref writer, sourceSectorIndex, source, sourceWorldAabb, dirtyBrick);
                }

                writer.EndForEachIndex();
            }

            private void QueryAndWriteBlockCandidates(
                ref NativeStream.Writer writer,
                int sourceSectorIndex,
                AlienDirtySectorRecord source,
                AABB sourceWorldAabb,
                DirtyBrickInfo dirtyBrick)
            {
                int3 minCell = AlienDirtyPropagation.WorldToCell(sourceWorldAabb.Min, SpatialCellSize);
                int3 maxCell =
                    AlienDirtyPropagation.WorldToCell(AlienDirtyPropagation.MaxExclusive(sourceWorldAabb.Max),
                        SpatialCellSize);

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

                        if (!AABB.Overlaps(sourceWorldAabb, target.CurrentWorldAabb))
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
                    } while (SpatialHash.TryGetNextValue(out targetSectorIndex, ref iterator));
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static AABB ComputeBrickWorldAabb(AlienDirtySectorRecord sector, AlienDirtyEntityView entity,
                short brickIdx, int halo)
            {
                int3 brickPos = Sector.ToBrickPos(brickIdx);
                float3 localMin = sector.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + brickPos * Sector.SIZE_IN_BLOCKS -
                                  halo;
                float3 localMax = sector.SectorPos * Sector.SECTOR_SIZE_IN_BLOCKS +
                                  (brickPos + 1) * Sector.SIZE_IN_BLOCKS + halo;
                return AABB.Transform(new AABB { Min = localMin, Max = localMax }, entity.LocalToWorld);
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

                AABB swept = AABB.Union(moving.PreviousWorldAabb, moving.CurrentWorldAabb).Inflated(DirtyHaloVoxels);
                int3 minCell = AlienDirtyPropagation.WorldToCell(swept.Min, SpatialCellSize);
                int3 maxCell =
                    AlienDirtyPropagation.WorldToCell(AlienDirtyPropagation.MaxExclusive(swept.Max), SpatialCellSize);

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

                        if (!AABB.Overlaps(swept, other.CurrentWorldAabb)
                            && !AABB.Overlaps(swept, other.PreviousWorldAabb))
                        {
                            continue;
                        }

                        if (!RelativeMotionExceedsThreshold(Entities[moving.EntityId], Entities[other.EntityId],
                                MotionThreshold, DeltaTime))
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
                    } while (MotionSpatialHash.TryGetNextValue(out otherSectorIndex, ref iterator));
                }

                writer.EndForEachIndex();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool HasAllocatedBricks(Sector sector)
            {
                return sector.NonEmptyBrickCount > 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool RelativeMotionExceedsThreshold(AlienDirtyEntityView a, AlienDirtyEntityView b,
                float threshold, float deltaTime)
            {
                float linear = math.length(a.LinearVelocity - b.LinearVelocity) * deltaTime;
                float influenceRadius = Sector.SECTOR_SIZE_IN_BLOCKS;
                // TODO: Tighten this with pair-local lever-arm displacement instead of waking on any angular motion.
                float angular = (math.length(a.AngularVelocity) + math.length(b.AngularVelocity)) * influenceRadius *
                                deltaTime;
                return linear + angular > threshold;
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
                        if (TryGetBrickTargetRange(source, target, candidate.SourceBrickIdx, DirtyHaloVoxels, false,
                                out int3 minBrick, out int3 maxBrick))
                        {
                            MarkAllocatedTargetBrickRange(target.Sector, minBrick, maxBrick, candidate.Flags);
                        }
                    }
                    else
                    {
                        Sector sourceSector = source.Sector.Get();
                        for (short sourceBrick = 0; sourceBrick < Sector.BRICKS_IN_SECTOR; sourceBrick++)
                        {
                            if (sourceSector.brickIdx[sourceBrick] == Sector.BRICKID_EMPTY)
                            {
                                continue;
                            }

                            bool hasCurrent = TryGetBrickTargetRange(source, target, sourceBrick, DirtyHaloVoxels,
                                false, out int3 currentMin, out int3 currentMax);
                            bool hasPrevious = TryGetBrickTargetRange(source, target, sourceBrick, DirtyHaloVoxels,
                                true, out int3 previousMin, out int3 previousMax);

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
                    {
                        continue;
                    }

                    target.brickRequireUpdateFlags[brickIdx] |= (ushort)flags;
                    target.sectorRequireUpdateFlags |= (ushort)flags;
                }
            }

        }
    }
}