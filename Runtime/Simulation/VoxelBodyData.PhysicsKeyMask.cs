using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxelis
{
    public partial struct VoxelBodyData
    {
        // PhysicsInfo flag lives in data bits 6-7: 3 = Corner, 2 = Edge, 1 = Face, 0 = None.
        // A "physics-key" block is a Corner or Edge, i.e. flag >= this threshold.
        private const int PhysicsKeyMinFlag = 2;

        /// <summary>
        /// Rebuilds the per-brick "physics-key voxel" bitmask kept as the PhysicsInfo slot's aux
        /// buffer: bit set when the block is a Corner or Edge. Must run after
        /// <c>ComputePhysicsProperties</c>, which fills the PhysicsInfo data this reads. Only sectors
        /// whose PhysicsInfo slot exists are processed; gating otherwise matches the non-empty mask
        /// and <c>RefreshPhysicsSlot</c> (same require-update geometry flag).
        /// </summary>
        public unsafe void RefreshPhysicsKeyMask(
            LockableUnsafeHashMap<int3, SectorHandle> sectors,
            DirtyFlags dirtyMask = DirtyFlags.GeometryWithLocalNeighbor)
        {
            if (sectors.Count == 0) { return; }

            var inputs = new NativeList<PhysicsKeyInput>(sectors.Count, Allocator.TempJob);
            try
            {
                foreach (var kvp in sectors)
                {
                    ref Sector sector = ref kvp.Value.Get();

                    SectorSlotStorage* physSlot = sector.slots + (int)SectorSlotId.PhysicsInfo;
                    if (!physSlot->IsCreated) { continue; }

                    bool hadAux = physSlot->HasAux;
                    bool changed = (sector.sectorRequireUpdateFlags & (ushort)dirtyMask) != 0;
                    if (hadAux && !changed) { continue; }

                    // Allocate on the main thread so the job only ever writes into existing memory.
                    sector.EnsureAuxAllocated(SectorSlotId.PhysicsInfo, BrickBitmask.Bytes);

                    inputs.Add(new PhysicsKeyInput { Sector = kvp.Value, FullRebuild = !hadAux });
                }

                if (inputs.Length == 0) { return; }

                new MarkPhysicsKeyMaskJob
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

        private struct PhysicsKeyInput
        {
            public SectorHandle Sector;
            public bool FullRebuild;
        }

        [BurstCompile]
        private struct MarkPhysicsKeyMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<PhysicsKeyInput> inputs;
            public ushort dirtyMask;

            public unsafe void Execute(int index)
            {
                PhysicsKeyInput input = inputs[index];
                ref Sector sector = ref input.Sector.Get();

                SectorSlotStorage* physSlot = sector.slots + (int)SectorSlotId.PhysicsInfo;
                if (!physSlot->HasAux) { return; }

                for (int brickAbs = 0; brickAbs < Sector.BRICKS_IN_SECTOR; brickAbs++)
                {
                    if (!input.FullRebuild && (sector.brickRequireUpdateFlags[brickAbs] & dirtyMask) == 0)
                    {
                        continue;
                    }

                    short bid = sector.brickIdx[brickAbs];
                    if (bid == Sector.BRICKID_EMPTY) { continue; }

                    var physBrick = (PhysicsInfo*)physSlot->GetBrickPtr(bid);
                    ulong* mask = (ulong*)physSlot->GetBrickAuxPtr(bid);
                    BrickBitmask.Clear(mask);

                    for (int voxelIdx = 0; voxelIdx < Sector.BLOCKS_IN_BRICK; voxelIdx++)
                    {
                        if ((physBrick[voxelIdx].data >> 6) >= PhysicsKeyMinFlag)
                        {
                            BrickBitmask.SetBit(mask, voxelIdx);
                        }
                    }
                }
            }
        }
    }
}
