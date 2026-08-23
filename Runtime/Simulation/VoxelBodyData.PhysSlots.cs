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
        /// solid block, the cells of the voxel-center cubical complex that are rooted at that block
        /// and are COLLISION-ACTIVE. The slot's aux bitmap is rebuilt in the same pass and marks the
        /// physics-key roots: a root is a key when it carries an active point or an active edge.
        /// Cross-sector topology is resolved through neighbor handles.
        /// </summary>
        /// <remarks>
        /// Gating uses the require-update (read) buffers populated by dirty propagation, mirroring the
        /// sector renderer. The brick-level <c>GeometryWithLocalNeighbor</c> flag already covers bricks
        /// adjacent to a geometry change, so boundary blocks whose exposure flipped are re-evaluated too.
        ///
        /// The key rule is deliberately ROOT-LOCAL. The previous rule ("a root is a key when one of
        /// its cells covers a sparse seed voxel") was a two-step dependency - root, covered voxel,
        /// that voxel's own neighbor - which pushed the read window two voxels past the brick. The
        /// per-voxel propagation table in <c>NeighborhoodSettings.s_voxelPropagationMasks</c> only
        /// reaches ONE voxel, so a change at the neighbor brick's local index 1 set no direction bit
        /// towards this brick and this brick never refreshed. That silently froze key bits at the
        /// brick seam, and the harmful direction (clearing a voxel so a key should appear) dropped a
        /// contact source. A root-local rule keeps the window inside the one-voxel reach the
        /// propagation table guarantees, so the hole cannot recur.
        ///
        /// Every output is derived from block occupancy alone, never from another brick's
        /// <see cref="PhysicsInfo"/> or key bits. That matters because those are rewritten in place:
        /// a brick that is not flagged this update still holds the previous update's ACTIVE bytes
        /// and key bits, so reading them as if they were raw occupancy would mix two representations.
        /// Occupancy is stable input for the whole refresh, so one parallel pass per brick is race
        /// free and deterministic.
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
        /// <remarks>
        /// Per brick the job first loads an occupancy window covering the brick plus a one-voxel halo
        /// on each side. The window is stored as one 10-bit row of X per (Y, Z) pair, so cell
        /// existence, activity and the key mask all become AND/OR/shift chains over eight voxels at a
        /// time. The window is filled from the Block slot's per-brick occupancy bitmask (rebuilt by
        /// <c>RefreshNonEmptyMask</c> immediately before this pass), which costs 27 brick lookups per
        /// brick instead of one lookup per neighbor test.
        /// </remarks>
        [BurstCompile]
        private struct ComputePhysicsSlotJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<PhysicsSlotInput> inputs;
            public ushort dirtyMask;

            // Window bounds in brick-local block coordinates. The low end reaches -1 because the cell
            // grown one voxel back is what a cell competes against for its negative directions. The
            // high end reaches +1 past the brick because a cube rooted at 7 spans the voxel at 8.
            //
            // ONE voxel each way is the whole reach, and that is load bearing: the per-voxel
            // propagation table only marks neighbor bricks within one voxel, so a window that reached
            // +2 would read voxels whose edits never flag this brick. Do not widen this without
            // widening s_voxelPropagationMasks to match.
            private const int WindowLow = -1;
            private const int WindowHigh = Sector.SIZE_IN_BLOCKS;
            private const int WindowSpan = WindowHigh - WindowLow + 1;
            private const int WindowRows = WindowSpan * WindowSpan;

            // Highest root coordinate.
            private const int RootHigh = Sector.SIZE_IN_BLOCKS - 1;

            // Bit b of a window row holds the occupancy of x = b - 1.
            private const int RowBitOrigin = -WindowLow;

            private const int NeighborBrickCount = 27;

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

                // Scratch for the whole sector; one brick is processed at a time.
                ulong** brickMasks = stackalloc ulong*[NeighborBrickCount];
                Block** brickBlocks = stackalloc Block*[NeighborBrickCount];
                uint* occupancyRows = stackalloc uint[WindowRows];
                uint* cellRows = stackalloc uint[PhysicsInfo.FeatureBitCount * WindowRows];
                uint* activeRows = stackalloc uint[PhysicsInfo.FeatureBitCount];

                foreach (SectorNonEmptyBrickEnumerator.BrickRef brickRef in sector.EnumerateNonEmptyBricks())
                {
                    int brickIdxAbs = brickRef.BrickAbs;
                    if (!input.FullRebuild &&
                        (sector.brickRequireUpdateFlags[brickIdxAbs] & dirtyMask) == 0)
                    {
                        continue;
                    }

                    short bid = brickRef.Bid;
                    var physBrick = (PhysicsInfo*)physSlot->GetBrickPtr(bid);
                    var physicsKeyMask = (ulong*)physSlot->GetBrickAuxPtr(bid);
                    if (physBrick == null || physicsKeyMask == null)
                    {
                        continue;
                    }

                    int3 brickBlockPos = Sector.ToBrickPos((short)brickIdxAbs) * Sector.SIZE_IN_BLOCKS;

                    LoadNeighborBricks(ref helper, brickBlockPos, brickMasks, brickBlocks);
                    LoadOccupancyWindow(brickMasks, brickBlocks, occupancyRows);
                    ComputeCellRows(occupancyRows, cellRows);
                    WriteBrick(cellRows, activeRows, physBrick, physicsKeyMask);
                }
            }

            /// <summary>Row index of one X row of the occupancy window.</summary>
            private static int RowIndex(int y, int z)
            {
                return (z - WindowLow) * WindowSpan + (y - WindowLow);
            }

            /// <summary>
            /// Caches the 3x3x3 bricks the window spans. Absent bricks and sectors stay null and read
            /// as empty. The occupancy bitmask is preferred; the raw blocks are the fallback for a
            /// brick whose Block slot carries no aux yet.
            /// </summary>
            private static unsafe void LoadNeighborBricks(
                ref SectorNeighborhoodReaderHelper helper,
                int3 brickBlockPos,
                ulong** brickMasks,
                Block** brickBlocks)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int3 blockPos = brickBlockPos +
                                            new int3(dx, dy, dz) * Sector.SIZE_IN_BLOCKS;
                            int slot = NeighborIndex(dx, dy, dz);
                            brickMasks[slot] = (ulong*)helper.GetBrickAuxPtrAtBlock(
                                SectorSlotId.Block, blockPos);
                            brickBlocks[slot] = helper.GetBrickPtrAtBlock<Block>(
                                SectorSlotId.Block, blockPos);
                        }
                    }
                }
            }

            private static int NeighborIndex(int dx, int dy, int dz)
            {
                return ((dz + 1) * 3 + (dy + 1)) * 3 + (dx + 1);
            }

            /// <summary>Occupancy of one in-brick X row, as eight bits with x at bit x.</summary>
            private static unsafe uint BrickRowBits(
                ulong** brickMasks, Block** brickBlocks, int slot, int y, int z)
            {
                ulong* mask = brickMasks[slot];
                if (mask != null)
                {
                    // One mask word covers one Z slice; within it a row starts at y * SIZE_IN_BLOCKS.
                    return (uint)((mask[z] >> (y << Sector.SHIFT_IN_BLOCKS)) & 0xFFul);
                }

                Block* blocks = brickBlocks[slot];
                if (blocks == null)
                {
                    return 0u;
                }

                int baseIdx = Sector.ToBlockIdx(0, y, z);
                uint bits = 0u;
                for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                {
                    bits |= blocks[baseIdx + x].isEmpty ? 0u : (1u << x);
                }
                return bits;
            }

            /// <summary>
            /// Fills the occupancy window. Each row splices the last bit of the -X brick, all eight
            /// bits of the centre brick and the first bit of the +X brick. The +X brick's remaining
            /// bits land above the window and are simply unused; they hold correct occupancy, so a
            /// shift that reaches them cannot read stale data.
            /// </summary>
            private static unsafe void LoadOccupancyWindow(
                ulong** brickMasks, Block** brickBlocks, uint* occupancyRows)
            {
                for (int z = WindowLow; z <= WindowHigh; z++)
                {
                    int brickZ = z >> Sector.SHIFT_IN_BLOCKS;
                    int localZ = z & Sector.BRICK_MASK;
                    for (int y = WindowLow; y <= WindowHigh; y++)
                    {
                        int brickY = y >> Sector.SHIFT_IN_BLOCKS;
                        int localY = y & Sector.BRICK_MASK;

                        int centre = NeighborIndex(0, brickY, brickZ);
                        uint low = BrickRowBits(brickMasks, brickBlocks, centre - 1, localY, localZ);
                        uint mid = BrickRowBits(brickMasks, brickBlocks, centre, localY, localZ);
                        uint high = BrickRowBits(brickMasks, brickBlocks, centre + 1, localY, localZ);

                        occupancyRows[RowIndex(y, z)] =
                            (low >> (Sector.SIZE_IN_BLOCKS - RowBitOrigin)) |
                            (mid << RowBitOrigin) |
                            (high << (Sector.SIZE_IN_BLOCKS + RowBitOrigin));
                    }
                }
            }

            /// <summary>
            /// Step 1: existence of every cell of the complex, indexed by axis mask (X=1, Y=2, Z=4).
            /// A cell exists when all of its corner voxels are occupied, so each mask is an AND of
            /// the rows and shifts its axes select. Rows are produced for every root a brick voxel
            /// competes against, which includes one row back on Y and Z.
            /// </summary>
            private static unsafe void ComputeCellRows(uint* occupancyRows, uint* cellRows)
            {
                for (int z = WindowLow; z <= RootHigh; z++)
                {
                    for (int y = WindowLow; y <= RootHigh; y++)
                    {
                        int row = RowIndex(y, z);
                        uint here = occupancyRows[row];
                        uint aheadY = occupancyRows[RowIndex(y + 1, z)];
                        uint aheadZ = occupancyRows[RowIndex(y, z + 1)];
                        uint aheadYZ = occupancyRows[RowIndex(y + 1, z + 1)];

                        uint spanX = here & (here >> 1);
                        uint spanXaheadY = aheadY & (aheadY >> 1);
                        uint spanXaheadZ = aheadZ & (aheadZ >> 1);
                        uint spanYZ = here & aheadY & aheadZ & aheadYZ;

                        cellRows[0 * WindowRows + row] = here;                   // point
                        cellRows[1 * WindowRows + row] = spanX;                  // X
                        cellRows[2 * WindowRows + row] = here & aheadY;          // Y
                        cellRows[3 * WindowRows + row] = spanX & spanXaheadY;    // XY
                        cellRows[4 * WindowRows + row] = here & aheadZ;          // Z
                        cellRows[5 * WindowRows + row] = spanX & spanXaheadZ;    // XZ
                        cellRows[6 * WindowRows + row] = spanYZ;                 // YZ
                        cellRows[7 * WindowRows + row] = spanYZ & (spanYZ >> 1); // XYZ
                    }
                }
            }

            /// <summary>
            /// Step 2: keeps only the cells that are collision-active. A cell that can grow along
            /// axis <c>a</c> gives every direction with a positive <c>+a</c> component to the grown
            /// cell rooted here, and every direction with a positive <c>-a</c> component to the grown
            /// cell rooted one voxel back. Both together leave only a zero-area slice, so the cell
            /// survives exactly when at least one of the two grown cells is missing.
            /// <para>
            /// This is the old containment rule with AND in place of OR: dedup dropped a cell when
            /// EITHER grown cell existed, activity drops it only when BOTH do. The cube has no axis
            /// to grow along, so it is always active; it is a volume cell and the surface path
            /// excludes it. Results are indexed by <see cref="PhysicsInfo"/> feature bit.
            /// </para>
            /// </summary>
            private static unsafe void ComputeActiveRows(
                uint* cellRows, int y, int z, uint* activeRows)
            {
                int row = RowIndex(y, z);
                int rowBackY = RowIndex(y - 1, z);
                int rowBackZ = RowIndex(y, z - 1);

                for (int axisMask = 0; axisMask < PhysicsInfo.FeatureBitCount; axisMask++)
                {
                    uint covered = 0u;
                    if ((axisMask & 1) == 0)
                    {
                        // One voxel back on X is one bit up in the row.
                        uint grownX = cellRows[(axisMask | 1) * WindowRows + row];
                        covered |= grownX & (grownX << 1);
                    }
                    if ((axisMask & 2) == 0)
                    {
                        covered |= cellRows[(axisMask | 2) * WindowRows + row] &
                                   cellRows[(axisMask | 2) * WindowRows + rowBackY];
                    }
                    if ((axisMask & 4) == 0)
                    {
                        covered |= cellRows[(axisMask | 4) * WindowRows + row] &
                                   cellRows[(axisMask | 4) * WindowRows + rowBackZ];
                    }

                    activeRows[PhysicsInfo.FeatureBitFromAxisMask(axisMask)] =
                        cellRows[axisMask * WindowRows + row] & ~covered;
                }
            }

            /// <summary>
            /// Step 3: a root is a key when it carries an active point or an active edge. Every
            /// permitted feature pair (vertex-vertex, vertex-edge, vertex-face, edge-edge) has a
            /// vertex or an edge on at least one side, so these roots are exactly the contact
            /// sources; a face-only root is always the target of a vertex.
            /// <para>
            /// Root-local by design. Deriving the key from cells covering a neighboring seed voxel
            /// would reach two voxels past the brick, which is one more than dirty propagation
            /// guarantees.
            /// </para>
            /// </summary>
            private static unsafe uint ComputeKeyRow(uint* activeRows)
            {
                return activeRows[PhysicsInfo.BitPoint] |
                       activeRows[PhysicsInfo.BitEdgeX] |
                       activeRows[PhysicsInfo.BitEdgeY] |
                       activeRows[PhysicsInfo.BitEdgeZ];
            }

            /// <summary>
            /// Transposes the per-row results into one byte per voxel and one mask word per Z slice.
            /// Air voxels fall out as zero because no cell exists at an empty root.
            /// </summary>
            private static unsafe void WriteBrick(
                uint* cellRows,
                uint* activeRows,
                PhysicsInfo* physBrick,
                ulong* physicsKeyMask)
            {
                for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                {
                    ulong keyWord = 0ul;
                    for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                    {
                        ComputeActiveRows(cellRows, y, z, activeRows);
                        uint keyRow = ComputeKeyRow(activeRows);

                        int baseIdx = Sector.ToBlockIdx(0, y, z);
                        for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                        {
                            int bit = x + RowBitOrigin;
                            uint data = 0u;
                            for (int feature = 0; feature < PhysicsInfo.FeatureBitCount; feature++)
                            {
                                data |= ((activeRows[feature] >> bit) & 1u) << feature;
                            }
                            physBrick[baseIdx + x] = new PhysicsInfo { data = (byte)data };
                        }

                        keyWord |= (ulong)((keyRow >> RowBitOrigin) & 0xFFu) <<
                                   (y << Sector.SHIFT_IN_BLOCKS);
                    }
                    physicsKeyMask[z] = keyWord;
                }
            }
        }
    }
}
