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
        /// solid block, the occupied positive 2x2x2 octet (bits 0-6) and whether all six face
        /// neighbors are solid (bit 7). The slot's aux bitmap is rebuilt in the same pass and marks
        /// sparse Corner/Edge physics-key voxels. Cross-sector topology is resolved through neighbor
        /// handles.
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
                    SectorSlotStorage* physSlot = sector.slots + (int)SectorSlotId.PhysicsInfo;
                    bool fullRebuild = !physSlot->IsCreated ||
                                       physSlot->stride != sizeof(PhysicsInfo) ||
                                       !physSlot->HasAux;
                    if (!fullRebuild && (sector.sectorRequireUpdateFlags & (ushort)dirtyMask) == 0)
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
                    if (physSlot->IsCreated && physSlot->stride != sizeof(PhysicsInfo))
                    {
                        // PhysicsInfo is derived from Block occupancy. A saved or hot-reloaded cache
                        // with an older layout must be replaced before typed writes begin.
                        physSlot->Dispose();
                        *physSlot = default;
                    }
                    sector.EnsureSlotAllocated<PhysicsInfo>(
                        SectorSlotId.PhysicsInfo, BrickBitmask.Bytes);

                    inputs.Add(new PhysicsSlotInput
                    {
                        Sector = kvp.Value,
                        Neighbors = neighbors,
                        FullRebuild = fullRebuild
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
            public bool FullRebuild;
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
                if (!physSlot->IsCreated || !physSlot->HasAux)
                {
                    return;
                }

                var helper = new SectorNeighborhoodReaderHelper(handle, input.Neighbors);

                foreach (SectorNonEmptyBrickEnumerator.BrickRef brickRef in sector.EnumerateNonEmptyBricks())
                {
                    int brickIdxAbs = brickRef.BrickAbs;
                    if (!input.FullRebuild &&
                        (sector.brickRequireUpdateFlags[brickIdxAbs] & dirtyMask) == 0)
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
                    var physicsKeyMask = (ulong*)physSlot->GetBrickAuxPtr(bid);
                    int3 brickBlockPos = Sector.ToBrickPos((short)brickIdxAbs) * Sector.SIZE_IN_BLOCKS;

                    ProcessBrick(ref helper, brick, physBrick, physicsKeyMask, brickBlockPos);
                }
            }

            private unsafe void ProcessBrick(
                ref SectorNeighborhoodReaderHelper helper,
                Block* brick,
                PhysicsInfo* physBrick,
                ulong* physicsKeyMask,
                int3 brickBlockPos)
            {
                for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                {
                    ulong physicsKeyWord = 0ul;
                    for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                    {
                        for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                        {
                            int voxelIdx = Sector.ToBlockIdx(x, y, z);

                            // Empty (air) blocks are not solid: clear so a freed block leaves no
                            // stale topology data.
                            if (brick[voxelIdx].isEmpty)
                            {
                                physBrick[voxelIdx] = default;
                                continue;
                            }

                            physBrick[voxelIdx] = ComputeBlockPhysicsInfo(
                                ref helper, brickBlockPos + new int3(x, y, z),
                                out bool isPhysicsKey);
                            if (isPhysicsKey)
                            {
                                physicsKeyWord |= 1ul << (voxelIdx & 63);
                            }
                        }
                    }
                    physicsKeyMask[z] = physicsKeyWord;
                }
            }

            /// <summary>
            /// Computes the seven positive-neighbor bits and the six-face interior bit. The
            /// Corner/Edge classification is returned separately for the aux physics-key bitmap.
            /// For now every non-air block is solid.
            /// </summary>
            private unsafe PhysicsInfo ComputeBlockPhysicsInfo(
                ref SectorNeighborhoodReaderHelper helper,
                int3 sectorBlockPos,
                out bool isPhysicsKey)
            {
                byte forwardOccupancy = 0;
                int surroundedAxes = 0;

                for (int axis = 0; axis < 3; axis++)
                {
                    int dirPos = axis * 2;       // +axis direction index (0,2,4)
                    int dirNeg = axis * 2 + 1;   // -axis direction index (1,3,5)

                    bool solidPos = helper.BlockTest(sectorBlockPos + NeighborhoodSettings.Directions[dirPos]);
                    bool solidNeg = helper.BlockTest(sectorBlockPos + NeighborhoodSettings.Directions[dirNeg]);

                    if (solidPos) { forwardOccupancy |= (byte)(1 << axis); }
                    if (solidPos && solidNeg) { surroundedAxes++; }
                }

                if (helper.BlockTest(sectorBlockPos + new int3(1, 1, 0))) { forwardOccupancy |= 1 << 3; }
                if (helper.BlockTest(sectorBlockPos + new int3(1, 0, 1))) { forwardOccupancy |= 1 << 4; }
                if (helper.BlockTest(sectorBlockPos + new int3(0, 1, 1))) { forwardOccupancy |= 1 << 5; }
                if (helper.BlockTest(sectorBlockPos + new int3(1, 1, 1))) { forwardOccupancy |= 1 << 6; }

                isPhysicsKey = surroundedAxes <= 1;
                if (surroundedAxes == 3) { forwardOccupancy |= 0x80; }
                return new PhysicsInfo { data = forwardOccupancy };
            }
        }
    }
}
