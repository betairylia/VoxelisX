using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxelis
{
    public partial struct VoxelBodyData : IDisposable
    {
        /// <summary>
        /// Recomputes the per-block <see cref="PhysicsInfo"/> slot for every sector that has pending
        /// require-update flags matching <paramref name="dirtyMask"/>. The slot encodes, for each
        /// solid block, which Von Neumann faces are exposed (low 6 bits) and how surrounded the block
        /// is (bits 6-7: 0 None / 1 Face / 2 Edge / 3 Corner), so collision detection can skip fully
        /// interior blocks. Cross-sector faces are resolved through the entity's neighbor handles.
        /// </summary>
        /// <remarks>
        /// Gating uses the require-update (read) buffers populated by dirty propagation, mirroring the
        /// sector renderer. The brick-level <c>GeometryWithLocalNeighbor</c> flag already covers bricks
        /// adjacent to a geometry change, so boundary blocks whose exposure flipped are re-evaluated too.
        /// </remarks>
        private unsafe void RefreshPhysicsSlot(
            LockableUnsafeHashMap<int3, SectorHandle> sectors,
            LockableUnsafeHashMap<int3, SectorNeighborHandles> sectorNeighbors,
            DirtyFlags dirtyMask = DirtyFlags.GeometryWithLocalNeighbor)
        {
            if (sectors.Count == 0)
            {
                return;
            }

            var inputs = new NativeList<PhysicsSlotInput>(sectors.Count, Allocator.TempJob);
            try
            {
                foreach (var kvp in sectors)
                {
                    ref Sector sector = ref kvp.Value.Get();
                    if ((sector.sectorRequireUpdateFlags & (ushort)dirtyMask) == 0)
                    {
                        continue;
                    }

                    // Neighbor handles are maintained in lock-step with the sectors map
                    // (VoxelEntityData.AddSectorAt/RemoveSectorAt); a missing entry would make
                    // cross-boundary reads dereference null, so skip defensively if absent.
                    if (!sectorNeighbors.TryGetValue(kvp.Key, out SectorNeighborHandles neighbors))
                    {
                        continue;
                    }

                    // The slot writes happen inside the parallel job; allocate the backing storage
                    // here on the main thread so the job only ever writes into existing memory.
                    sector.EnsureSlotAllocated<PhysicsInfo>(SectorSlotId.PhysicsInfo);

                    inputs.Add(new PhysicsSlotInput
                    {
                        Sector = kvp.Value,
                        Neighbors = neighbors
                    });
                }

                if (inputs.Length == 0)
                {
                    return;
                }

                var job = new ComputePhysicsSlotJob
                {
                    inputs = inputs.AsArray(),
                    dirtyMask = (ushort)dirtyMask
                };
                job.Schedule(inputs.Length, 1).Complete();
            }
            finally
            {
                if (inputs.IsCreated)
                {
                    inputs.Dispose();
                }
            }
        }

        private struct PhysicsSlotInput
        {
            public SectorHandle Sector;
            public SectorNeighborHandles Neighbors;
        }

        /// <summary>
        /// Burst job that fills the <see cref="PhysicsInfo"/> slot of one sector per index. Each index
        /// writes only into its own sector's slot storage (reads may cross into neighbor sectors), so
        /// running sectors in parallel is data-race free.
        /// </summary>
        [BurstCompile]
        private struct ComputePhysicsSlotJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<PhysicsSlotInput> inputs;
            public ushort dirtyMask;

            public unsafe void Execute(int index)
            {
                PhysicsSlotInput input = inputs[index];
                SectorHandle handle = input.Sector;
                ref Sector sector = ref handle.Get();

                SectorSlotStorage* physSlot = sector.slots + (int)SectorSlotId.PhysicsInfo;
                if (!physSlot->IsCreated)
                {
                    return;
                }

                var helper = new SectorNeighborhoodReaderHelper(handle, input.Neighbors);

                foreach (SectorNonEmptyBrickEnumerator.BrickRef brickRef in sector.EnumerateNonEmptyBricks())
                {
                    int brickIdxAbs = brickRef.BrickAbs;
                    if ((sector.brickRequireUpdateFlags[brickIdxAbs] & dirtyMask) == 0)
                    {
                        continue;
                    }

                    short bid = brickRef.Bid;

                    Block* brick = sector.GetBrick<Block>(SectorSlotId.Block, bid);
                    if (brick == null)
                    {
                        continue;
                    }

                    var physBrick = (PhysicsInfo*)physSlot->GetBrickPtr(bid);
                    int3 brickBlockPos = Sector.ToBrickPos((short)brickIdxAbs) * Sector.SIZE_IN_BLOCKS;

                    ProcessBrick(ref helper, brick, physBrick, brickBlockPos);
                }
            }

            private unsafe void ProcessBrick(
                ref SectorNeighborhoodReaderHelper helper,
                Block* brick,
                PhysicsInfo* physBrick,
                int3 brickBlockPos)
            {
                for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                {
                    for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                    {
                        for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                        {
                            int voxelIdx = Sector.ToBlockIdx(x, y, z);

                            // Empty (air) blocks are not solid: clear so a freed block leaves no
                            // stale exposure data. data == 0 means "interior / ignore" downstream.
                            if (brick[voxelIdx].isEmpty)
                            {
                                physBrick[voxelIdx] = default;
                                continue;
                            }

                            physBrick[voxelIdx] = ComputeBlockPhysicsInfo(
                                ref helper, brickBlockPos + new int3(x, y, z));
                        }
                    }
                }
            }

            /// <summary>
            /// Computes the <see cref="PhysicsInfo"/> for a single solid block from the solidity of its
            /// 6 Von Neumann neighbors. Bit i of the connectivity mask follows
            /// <see cref="NeighborhoodSettings.Directions"/> order (0:+X 1:-X 2:+Y 3:-Y 4:+Z 5:-Z) and
            /// is set when that neighbor is NOT solid (an exposed face). An axis counts as "surrounded"
            /// when both of its neighbors are solid; the physics-flag is <c>3 - surroundedAxes</c>.
            /// For now every non-air block (id != 0) is treated as solid.
            /// </summary>
            private unsafe PhysicsInfo ComputeBlockPhysicsInfo(
                ref SectorNeighborhoodReaderHelper helper,
                int3 sectorBlockPos)
            {
                byte faceMask = 0;
                int surroundedAxes = 0;

                for (int axis = 0; axis < 3; axis++)
                {
                    int dirPos = axis * 2;       // +axis direction index (0,2,4)
                    int dirNeg = axis * 2 + 1;   // -axis direction index (1,3,5)

                    bool solidPos = helper.BlockTest(sectorBlockPos + NeighborhoodSettings.Directions[dirPos]);
                    bool solidNeg = helper.BlockTest(sectorBlockPos + NeighborhoodSettings.Directions[dirNeg]);

                    if (!solidPos) { faceMask |= (byte)(1 << dirPos); }
                    if (!solidNeg) { faceMask |= (byte)(1 << dirNeg); }
                    if (solidPos && solidNeg) { surroundedAxes++; }
                }

                int physicsFlag = 3 - surroundedAxes;
                return new PhysicsInfo { data = (byte)((physicsFlag << 6) | faceMask) };
            }
        }
    }
}
