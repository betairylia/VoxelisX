using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxelis
{
    public unsafe partial struct VoxelEntityData
    {
        /// <summary>
        /// Rebuilds the per-brick "non-empty voxel" bitmask kept as the Block slot's aux buffer:
        /// bit set when the block at that voxel is not empty. One sector per job index; each sector
        /// writes only its own aux, so parallel execution is race free.
        ///
        /// Gating mirrors <c>RefreshPhysicsSlot</c>: a sector is (re)built when its aux is missing
        /// (first tick / freshly loaded) or when it carries the geometry require-update flag. A
        /// missing-aux sector rebuilds every non-empty brick; otherwise only the require-update
        /// bricks are re-marked, leaving untouched bricks' masks (carried through the snapshot clone)
        /// intact. Must run after snapshots are applied, so it reads settled Block data.
        /// </summary>
        public void RefreshNonEmptyMask(DirtyFlags dirtyMask = DirtyFlags.GeometryWithLocalNeighbor)
        {
            if (sectors.Count == 0) { return; }

            var inputs = new NativeList<MaskSectorInput>(sectors.Count, Allocator.TempJob);
            try
            {
                foreach (var kvp in sectors)
                {
                    ref Sector sector = ref kvp.Value.Get();
                    if (sector.NonEmptyBrickCount == 0) { continue; }

                    SectorSlotStorage* blockSlot = sector.slots + (int)SectorSlotId.Block;
                    bool hadAux = blockSlot->HasAux;
                    bool changed = (sector.sectorRequireUpdateFlags & (ushort)dirtyMask) != 0;
                    if (hadAux && !changed) { continue; }

                    // Allocate on the main thread so the job only ever writes into existing memory.
                    sector.EnsureAuxAllocated(SectorSlotId.Block, BrickBitmask.Bytes);

                    inputs.Add(new MaskSectorInput { Sector = kvp.Value, FullRebuild = !hadAux });
                }

                if (inputs.Length == 0) { return; }

                new MarkNonEmptyMaskJob
                {
                    inputs = inputs.AsArray(),
                    dirtyMask = (ushort)dirtyMask
                }.Schedule(inputs.Length, 1).Complete();
            }
            finally
            {
                if (inputs.IsCreated) { inputs.Dispose(); }
            }
        }

        private struct MaskSectorInput
        {
            public SectorHandle Sector;
            public bool FullRebuild;
        }

        [BurstCompile]
        private struct MarkNonEmptyMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<MaskSectorInput> inputs;
            public ushort dirtyMask;

            public void Execute(int index)
            {
                MaskSectorInput input = inputs[index];
                ref Sector sector = ref input.Sector.Get();

                SectorSlotStorage* blockSlot = sector.slots + (int)SectorSlotId.Block;
                if (!blockSlot->HasAux) { return; }

                for (int brickAbs = 0; brickAbs < Sector.BRICKS_IN_SECTOR; brickAbs++)
                {
                    if (!input.FullRebuild && (sector.brickRequireUpdateFlags[brickAbs] & dirtyMask) == 0)
                    {
                        continue;
                    }

                    short bid = sector.brickIdx[brickAbs];
                    if (bid == Sector.BRICKID_EMPTY) { continue; }

                    Block* brick = sector.GetBrick<Block>(SectorSlotId.Block, bid);
                    if (brick == null) { continue; }

                    ulong* mask = (ulong*)blockSlot->GetBrickAuxPtr(bid);
                    BrickBitmask.Clear(mask);

                    for (int voxelIdx = 0; voxelIdx < Sector.BLOCKS_IN_BRICK; voxelIdx++)
                    {
                        if (!brick[voxelIdx].isEmpty)
                        {
                            BrickBitmask.SetBit(mask, voxelIdx);
                        }
                    }
                }
            }
        }
    }
}
