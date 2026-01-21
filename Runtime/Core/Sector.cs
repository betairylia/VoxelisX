using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis
{
    /// <summary>
    /// Information about a brick update for renderer synchronization.
    /// Tracks whether a brick was added, removed, or modified.
    /// </summary>
    public struct BrickUpdateInfo
    {
        /// <summary>
        /// The type of update that occurred to a brick.
        /// </summary>
        public enum Type
        {
            /// <summary>No update pending.</summary>
            Idle = 0,
            /// <summary>Brick was newly added.</summary>
            Added,
            /// <summary>Brick was removed.</summary>
            Removed,
            /// <summary>Brick contents were modified.</summary>
            Modified,
        }

        /// <summary>
        /// The type of update for this brick.
        /// </summary>
        public Type type;

        /// <summary>
        /// The relative brick index within the sector's voxel data.
        /// </summary>
        public short brickIdx;

        /// <summary>
        /// The absolute brick index in the sector's 3D grid.
        /// </summary>
        public short brickIdxAbsolute;
    }

    /// <summary>
    /// Represents a 3D sector of voxel data organized into bricks.
    /// A sector contains a 16x16x16 grid of bricks, where each brick is 8x8x8 blocks.
    /// Total sector size is 128x128x128 blocks.
    /// </summary>
    /// <remarks>
    /// The sector uses a sparse voxel octree-like structure where empty bricks are not allocated.
    /// Only bricks containing non-empty blocks consume memory. This allows for efficient storage
    /// of large voxel worlds with sparse geometry.
    /// </remarks>
    public unsafe struct Sector
    {
        // Brick-level constants (isometric: uniform across all axes)
        /// <summary>Bit shift for brick size (brick is 8 blocks per axis).</summary>
        public const int SHIFT_IN_BLOCKS = 3;
        /// <summary>Size of a brick in blocks (8 blocks per axis).</summary>
        public const int SIZE_IN_BLOCKS = (1 << SHIFT_IN_BLOCKS);
        /// <summary>Bitmask for extracting block position within brick (0-7).</summary>
        public const int BRICK_MASK = SIZE_IN_BLOCKS - 1;
        /// <summary>Squared size for indexing calculations (8 * 8 = 64).</summary>
        public const int SIZE_IN_BLOCKS_SQUARED = SIZE_IN_BLOCKS * SIZE_IN_BLOCKS;
        /// <summary>Total number of blocks in a single brick (8x8x8 = 512).</summary>
        public const int BLOCKS_IN_BRICK = SIZE_IN_BLOCKS * SIZE_IN_BLOCKS * SIZE_IN_BLOCKS;

        // Sector-level constants (isometric: uniform across all axes)
        /// <summary>Bit shift for sector size in bricks (16 bricks per axis).</summary>
        public const int SHIFT_IN_BRICKS = 4;
        /// <summary>Number of bricks in a sector per axis (16 bricks = 128 blocks).</summary>
        public const int SIZE_IN_BRICKS = (1 << SHIFT_IN_BRICKS);
        /// <summary>Bitmask for extracting brick position within sector (0-15).</summary>
        public const int SECTOR_MASK = SIZE_IN_BRICKS - 1;
        /// <summary>Squared size for indexing calculations (16 * 16 = 256).</summary>
        public const int SIZE_IN_BRICKS_SQUARED = SIZE_IN_BRICKS * SIZE_IN_BRICKS;

        /// <summary>Total number of bricks in a sector (16x16x16 = 4096).</summary>
        public const int BRICKS_IN_SECTOR = SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS;
        /// <summary>Size of a sector in blocks (128 blocks per axis).</summary>
        public const int SECTOR_SIZE_IN_BLOCKS = SIZE_IN_BRICKS << SHIFT_IN_BLOCKS;

        /// <summary>Special value indicating an empty/unallocated brick.</summary>
        public const short BRICKID_EMPTY = -1;

        /// <summary>
        /// Flattened array of all block data for non-empty bricks.
        /// Bricks are stored contiguously, with each brick containing BLOCKS_IN_BRICK elements.
        /// </summary>
        public UnsafeList<Block> voxels;
        
        /// <summary>
        /// Maps absolute brick indices (position in 3D grid) to relative brick indices (position in voxels array).
        /// Contains BRICKID_EMPTY for bricks that have not been allocated.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public short* brickIdx;
        
        /// <summary>
        /// Tracks the update state of each brick for renderer synchronization.
        /// Indexed by absolute brick ID.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public BrickUpdateInfo.Type* brickFlags;

        /// <summary>
        /// List containing absolute IDs of bricks that have been modified and need rendering updates.
        /// For renderer, updater, physics etc. use.
        /// </summary>
        public UnsafeList<short> updateRecord;

        // Dirty propagation buffers
        [NativeDisableUnsafePtrRestriction]
        public ushort* brickDirtyFlags;           // Write buffer: what changed THIS tick
        [NativeDisableUnsafePtrRestriction]
        public ushort* brickRequireUpdateFlags;   // Read buffer: what needs processing (from propagation)
        [NativeDisableUnsafePtrRestriction]
        public uint* brickDirtyDirectionMask;     // Bitmask indicating which of 26 neighbor directions need propagation
        public ushort sectorDirtyFlags;
        public ushort sectorRequireUpdateFlags;
        public uint sectorNeighborsToCreate;      // Bitmask indicating which of 26 neighbor sectors need to be created for propagation

        /// <summary>
        /// Returns true if there are pending brick updates for the renderer.
        /// </summary>
        public bool IsRendererDirty => !updateRecord.IsEmpty || (sectorDirtyFlags & (ushort)DirtyFlags.Reserved0) != 0;

        /// <summary>
        /// Returns true if the sector contains no allocated bricks.
        /// </summary>
        public bool IsRendererEmpty => RendererNonEmptyBrickCount == 0;

        /// <summary>
        /// Single-element array storing the count of allocated bricks.
        /// Using an array allows modification within burst-compiled jobs.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        private int* currentBrickId;
        
        // Lock for brick-thread-safe write
        private long _sectorAllocLock, _sectorSetDirtyLock;

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector for rendering.
        /// </summary>
        public int RendererNonEmptyBrickCount => currentBrickId[0];

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector.
        /// </summary>
        public int NonEmptyBrickCount => currentBrickId[0];

        /// <summary>
        /// Gets the approximate host memory usage of this sector in bytes.
        /// </summary>
        public int MemoryUsage => voxels.Capacity * UnsafeUtility.SizeOf(typeof(Block));

        [BurstCompile]
        struct InitBrickIdJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<short> id;
            public void Execute(int index)
            {
                id[index] = BRICKID_EMPTY;
            }
        }

        /// <summary>
        /// Creates a new sector with the specified allocator and initial brick capacity.
        /// </summary>
        /// <param name="allocator">The memory allocator to use for native collections.</param>
        /// <param name="initialBricks">Initial capacity for brick storage (default: 1).</param>
        /// <param name="options">Memory initialization options (default: ClearMemory).</param>
        /// <returns>A newly initialized sector.</returns>
        public static Sector New(
            Allocator allocator,
            int initialBricks = 1,
            NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            int totalBricks = BRICKS_IN_SECTOR;

            Sector s = new Sector()
            {
                voxels = new UnsafeList<Block>(initialBricks * BLOCKS_IN_BRICK, allocator),
                brickIdx = (short*)UnsafeUtility.Malloc(totalBricks * sizeof(short), UnsafeUtility.AlignOf<short>(), allocator),
                brickFlags = (BrickUpdateInfo.Type*)UnsafeUtility.Malloc(totalBricks * sizeof(BrickUpdateInfo.Type), UnsafeUtility.AlignOf<BrickUpdateInfo.Type>(), allocator),
                updateRecord = new UnsafeList<short>(10, allocator),
                currentBrickId = (int*)UnsafeUtility.Malloc(sizeof(int), UnsafeUtility.AlignOf<int>(), allocator),
                brickDirtyFlags = (ushort*)UnsafeUtility.Malloc(totalBricks * sizeof(ushort), UnsafeUtility.AlignOf<ushort>(), allocator),
                brickRequireUpdateFlags = (ushort*)UnsafeUtility.Malloc(totalBricks * sizeof(ushort), UnsafeUtility.AlignOf<ushort>(), allocator),
                brickDirtyDirectionMask = (uint*)UnsafeUtility.Malloc(totalBricks * sizeof(uint), UnsafeUtility.AlignOf<uint>(), allocator),
                NonEmptyBricks = new UnsafeList<short>(initialBricks, allocator),
                sectorDirtyFlags = 0,
                sectorRequireUpdateFlags = 0,
                sectorNeighborsToCreate = 0,
                _snapshot_enabled = false,
            };

            if (options == NativeArrayOptions.ClearMemory)
            {
                UnsafeUtility.MemClear(s.brickIdx, totalBricks * sizeof(short));
                UnsafeUtility.MemClear(s.brickFlags, totalBricks * sizeof(BrickUpdateInfo.Type));
                UnsafeUtility.MemClear(s.brickDirtyFlags, totalBricks * sizeof(ushort));
                UnsafeUtility.MemClear(s.brickRequireUpdateFlags, totalBricks * sizeof(ushort));
                UnsafeUtility.MemClear(s.brickDirtyDirectionMask, totalBricks * sizeof(uint));

                for (int i = 0; i < totalBricks; i++)
                    s.brickIdx[i] = BRICKID_EMPTY;
            }

            *(s.currentBrickId) = 0;
            return s;
        }

        /// <summary>
        /// Creates a shallow copy of a sector without copying the update record.
        /// </summary>
        /// <param name="from">The sector handle to clone from.</param>
        /// <param name="allocator">The memory allocator to use for the new sector.</param>
        /// <returns>A new sector containing the same voxel data but with an empty update record.</returns>
        public static Sector CloneNoRecord(
            SectorHandle from,
            Allocator allocator)
            => CloneNoRecord(*(from.Ptr), allocator);

        /// <summary>
        /// Creates a shallow copy of a sector without copying the update record.
        /// </summary>
        /// <param name="from">The sector to clone from.</param>
        /// <param name="allocator">The memory allocator to use for the new sector.</param>
        /// <returns>A new sector containing the same voxel data but with an empty update record.</returns>
        /// <remarks>
        /// This method copies the voxel data and brick indices but does not copy the update record,
        /// dirty flags, or require update flags. The new sector starts in a clean state.
        /// </remarks>
        public static Sector CloneNoRecord(
            Sector from,
            Allocator allocator)
        {
            Sector s = Sector.New(allocator, from.NonEmptyBrickCount, NativeArrayOptions.UninitializedMemory);

            s.voxels.AddRange(from.voxels);
            UnsafeUtility.MemCpy(s.brickIdx, from.brickIdx,
                SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS * sizeof(short));
            *(s.currentBrickId) = *(from.currentBrickId);

            return s;
        }
        
        /// <summary>
        /// Disposes all native collections used by this sector, releasing unmanaged memory.
        /// </summary>
        public void Dispose(Allocator allocator)
        {
            if (voxels.IsCreated) voxels.Dispose();
            if (brickIdx != null) UnsafeUtility.Free(brickIdx, allocator);
            if (brickFlags != null) UnsafeUtility.Free(brickFlags, allocator);
            if (updateRecord.IsCreated) updateRecord.Dispose();
            if (currentBrickId != null) UnsafeUtility.Free(currentBrickId, allocator);
            if (NonEmptyBricks.IsCreated) NonEmptyBricks.Dispose();
            if (brickDirtyFlags != null) UnsafeUtility.Free(brickDirtyFlags, allocator);
            if (brickRequireUpdateFlags != null) UnsafeUtility.Free(brickRequireUpdateFlags, allocator);
            if (brickDirtyDirectionMask != null) UnsafeUtility.Free(brickDirtyDirectionMask, allocator);
        }

        /// <summary>
        /// Converts 3D brick coordinates to a flat brick index within the sector.
        /// </summary>
        /// <param name="x">Brick X coordinate (0-15).</param>
        /// <param name="y">Brick Y coordinate (0-15).</param>
        /// <param name="z">Brick Z coordinate (0-15).</param>
        /// <returns>The flat absolute brick index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBrickIdx(int x, int y, int z)
        {
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(x < SIZE_IN_BRICKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(y < SIZE_IN_BRICKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(z < SIZE_IN_BRICKS);

            return (x + y * SIZE_IN_BRICKS + z * SIZE_IN_BRICKS_SQUARED);
        }

        /// <summary>
        /// Converts a flat absolute brick index back to 3D brick coordinates.
        /// </summary>
        /// <param name="bidAbsolute">The absolute brick index.</param>
        /// <returns>The 3D brick position within the sector.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 ToBrickPos(short bidAbsolute)
        {
            return new int3(
                bidAbsolute & SECTOR_MASK,
                (bidAbsolute >> SHIFT_IN_BRICKS) & SECTOR_MASK,
                (bidAbsolute >> (SHIFT_IN_BRICKS << 1)));  // >> (SHIFT_IN_BRICKS * 2)
        }

        /// <summary>
        /// Converts 3D block coordinates within a brick to a flat block index.
        /// </summary>
        /// <param name="x">Block X coordinate within brick (0-7).</param>
        /// <param name="y">Block Y coordinate within brick (0-7).</param>
        /// <param name="z">Block Z coordinate within brick (0-7).</param>
        /// <returns>The flat block index within the brick (0-511).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBlockIdx(int x, int y, int z)
        {
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(x < SIZE_IN_BLOCKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(y < SIZE_IN_BLOCKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(z < SIZE_IN_BLOCKS);

            return (x + y * SIZE_IN_BLOCKS + z * SIZE_IN_BLOCKS_SQUARED);
        }

        /// <summary>
        /// Precomputed lookup table for voxel position to propagation direction mask.
        /// Index: voxel flat index within brick (0-511), Value: bitmask of 26 neighbor directions.
        /// Initialized by PrecomputeVoxelPropagationMasks() on first use.
        /// </summary>
        private static uint[] s_voxelPropagationMasks = null;

        /// <summary>
        /// Precomputed lookup table for brick position to sector neighbor directions.
        /// Index: brick flat index (0-4095), Value: bitmask of 26 sector neighbor directions this brick touches.
        /// A brick at sector boundary will have bits set for which neighboring sectors it touches.
        /// Initialized by PrecomputeBrickSectorNeighborMasks() on first use.
        /// </summary>
        private static uint[] s_brickSectorNeighborMasks = null;

        /// <summary>
        /// Precomputes both voxel and brick propagation lookup tables.
        /// Must be called during initialization before any dirty propagation occurs.
        /// </summary>
        public static void PrecomputePropagationLookupTables()
        {
            PrecomputeVoxelPropagationMasks();
            PrecomputeBrickSectorNeighborMasks();
        }

        /// <summary>
        /// Precomputes the propagation direction masks for all 512 voxel positions within a brick.
        /// </summary>
        private static void PrecomputeVoxelPropagationMasks()
        {
            if (s_voxelPropagationMasks != null) return; // Already initialized

            s_voxelPropagationMasks = new uint[BLOCKS_IN_BRICK]; // 512 entries

            for (int z = 0; z < SIZE_IN_BLOCKS; z++)
            {
                for (int y = 0; y < SIZE_IN_BLOCKS; y++)
                {
                    for (int x = 0; x < SIZE_IN_BLOCKS; x++)
                    {
                        int voxelIdx = ToBlockIdx(x, y, z);
                        s_voxelPropagationMasks[voxelIdx] = ComputeBoundaryDirectionMask(x, y, z, SIZE_IN_BLOCKS);
                    }
                }
            }
        }

        /// <summary>
        /// Precomputes which sector neighbors each brick position touches (for bricks at sector boundaries).
        /// </summary>
        private static void PrecomputeBrickSectorNeighborMasks()
        {
            if (s_brickSectorNeighborMasks != null) return; // Already initialized

            s_brickSectorNeighborMasks = new uint[BRICKS_IN_SECTOR]; // 4096 entries

            for (int z = 0; z < SIZE_IN_BRICKS; z++)
            {
                for (int y = 0; y < SIZE_IN_BRICKS; y++)
                {
                    for (int x = 0; x < SIZE_IN_BRICKS; x++)
                    {
                        int brickIdx = ToBrickIdx(x, y, z);
                        s_brickSectorNeighborMasks[brickIdx] = ComputeBoundaryDirectionMask(x, y, z, SIZE_IN_BRICKS);
                    }
                }
            }
        }

        /// <summary>
        /// Unified method to compute which neighbor directions need propagation based on position within a volume.
        /// Works for both voxel-in-brick (sideLength=8) and brick-in-sector (sideLength=16) calculations.
        /// </summary>
        /// <param name="x">X coordinate within volume</param>
        /// <param name="y">Y coordinate within volume</param>
        /// <param name="z">Z coordinate within volume</param>
        /// <param name="sideLength">Side length of the volume (8 for brick, 16 for sector)</param>
        /// <returns>Bitmask where bit i indicates if direction i needs propagation</returns>
        private static uint ComputeBoundaryDirectionMask(int x, int y, int z, int sideLength)
        {
            int maxCoord = sideLength - 1;

            // Determine which boundaries this position is at
            bool atMinX = (x == 0);
            bool atMaxX = (x == maxCoord);
            bool atMinY = (y == 0);
            bool atMaxY = (y == maxCoord);
            bool atMinZ = (z == 0);
            bool atMaxZ = (z == maxCoord);

            uint mask = 0;

            // For each of the 26 neighbor directions, check if we're at the corresponding boundary
            for (int dir = 0; dir < 26; dir++)
            {
                int3 direction = NeighborhoodSettings.Directions[dir];
                bool atBoundary = true;

                // Check if we're at the required boundary for this direction
                if (direction.x < 0 && !atMinX) atBoundary = false;
                if (direction.x > 0 && !atMaxX) atBoundary = false;
                if (direction.y < 0 && !atMinY) atBoundary = false;
                if (direction.y > 0 && !atMaxY) atBoundary = false;
                if (direction.z < 0 && !atMinZ) atBoundary = false;
                if (direction.z > 0 && !atMaxZ) atBoundary = false;

                if (atBoundary)
                {
                    mask |= (1u << dir);
                }
            }

            return mask;
        }

        /// <summary>
        /// Gets the precomputed propagation direction mask for a voxel position within a brick.
        /// </summary>
        /// <param name="voxelIdxInBrick">Flat voxel index within brick (0-511)</param>
        /// <returns>Bitmask where bit i indicates if direction i needs propagation</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetVoxelPropagationMask(int voxelIdxInBrick)
        {
            return s_voxelPropagationMasks[voxelIdxInBrick];
        }

        /// <summary>
        /// Gets the precomputed sector neighbor mask for a brick position.
        /// </summary>
        /// <param name="brickIdx">Flat brick index within sector (0-4095)</param>
        /// <returns>Bitmask where bit i indicates if brick touches sector neighbor i</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetBrickSectorNeighborMask(int brickIdx)
        {
            return s_brickSectorNeighborMasks[brickIdx];
        }

        /// <summary>
        /// Gets a slice of the voxel array representing a specific brick.
        /// </summary>
        /// <param name="x">Brick X coordinate.</param>
        /// <param name="y">Brick Y coordinate.</param>
        /// <param name="z">Brick Z coordinate.</param>
        /// <returns>A NativeSlice containing the brick's blocks, or null if the brick is empty.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block* GetBrick(int x, int y, int z)
        {
            short bid = this.brickIdx[ToBrickIdx(x, y, z)];
            return GetBrick(bid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block* GetBrick(short bid)
        {
            if (bid == BRICKID_EMPTY)
            {
                return null;
            }
            return voxels.Ptr + (bid * BLOCKS_IN_BRICK);
        }

        /// <summary>
        /// Gets the block at the specified position within this sector.
        /// </summary>
        /// <param name="x">Block X coordinate within sector (0-127).</param>
        /// <param name="y">Block Y coordinate within sector (0-127).</param>
        /// <param name="z">Block Z coordinate within sector (0-127).</param>
        /// <returns>The block at the specified position, or Block.Empty if the brick is not allocated.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
        {
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = this.brickIdx[brick_sector_index_id];

            if (bid == BRICKID_EMPTY)
            {
                return Block.Empty;
            }

            return voxels[
                bid * BLOCKS_IN_BRICK
                + ToBlockIdx(
                    x & BRICK_MASK,
                    y & BRICK_MASK,
                    z & BRICK_MASK)
            ];
        }

        #region Snapshots

        /// <summary>
        /// Backbuffer version of voxels for read access during double-buffered updates (e.g., cellular automata).
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<Block> _snapshot_voxels;

        /// <summary>
        /// Backbuffer version of brickIdx for read access during double-buffered updates.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public short* _snapshot_brickIdx;

        private bool _snapshot_enabled;

        /// <summary>
        /// Activates snapshot mode for double-buffered sector updates.
        /// </summary>
        /// <param name="allocator">The allocator to use for snapshot buffers.</param>
        /// <remarks>
        /// In snapshot mode, any modifications to the sector are written to a backbuffer without affecting read operations.
        /// This enables reading from the previous state while writing to the current state, which is essential for
        /// cellular automata where a voxel's state depends on its neighbors in the previous time step.
        /// Call <see cref="ApplySnapshot"/> after all modifications are complete to swap the buffers.
        /// Multiple calls to ActivateSnapshot without ApplySnapshot will be ignored.
        /// </remarks>
        public void ActivateSnapshot(Allocator allocator = Allocator.Persistent)
        {
            // Don't activate multiple times
            if (_snapshot_enabled)
            {
                return;
            }

            if (!_snapshot_voxels.IsCreated)
            {
                _snapshot_voxels = new UnsafeList<Block>(voxels.Capacity, allocator);
            }

            if (_snapshot_brickIdx == null)
            {
                _snapshot_brickIdx = (short*)UnsafeUtility.Malloc(
                    sizeof(short) * BRICKS_IN_SECTOR,
                    UnsafeUtility.AlignOf<short>(),
                    allocator
                );
            }
            
            _snapshot_voxels.Clear();
            _snapshot_voxels.AddRange(voxels);
            UnsafeUtility.MemCpy(_snapshot_brickIdx, brickIdx, sizeof(short) * BRICKS_IN_SECTOR);

            _snapshot_enabled = true;
        }

        /// <summary>
        /// Applies the snapshot by swapping the backbuffer into the main buffer.
        /// </summary>
        /// <remarks>
        /// This method swaps the snapshot buffers with the main voxel and brick index buffers,
        /// completing the double-buffered update cycle. Only applies if snapshot mode was previously activated.
        /// After applying, snapshot mode is automatically deactivated.
        /// Swapping instead of copying avoids one copy operation, improving performance.
        /// </remarks>
        public void ApplySnapshot()
        {
            // Apply only if previously activated
            if (!_snapshot_enabled) return;

            // Swap voxels buffers
            var tempVoxels = voxels;
            voxels = _snapshot_voxels;
            _snapshot_voxels = tempVoxels;

            // Swap brickIdx pointers
            var tempBrickIdx = brickIdx;
            brickIdx = _snapshot_brickIdx;
            _snapshot_brickIdx = tempBrickIdx;

            _snapshot_enabled = false;
        }

        #endregion

        /// <summary>
        /// Sets the block at the specified position within this sector.
        /// Automatically allocates a new brick if needed.
        /// </summary>
        /// <param name="x">Block X coordinate within sector (0-127).</param>
        /// <param name="y">Block Y coordinate within sector (0-127).</param>
        /// <param name="z">Block Z coordinate within sector (0-127).</param>
        /// <param name="b">The block data to set.</param>
        /// <remarks>
        /// If the target brick is not allocated and the block is non-empty, a new brick will be created.
        /// Setting an empty block to an empty brick is a no-op to avoid unnecessary allocations.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block b)
        {
            short* targetBrickIdx = _snapshot_enabled ? _snapshot_brickIdx : brickIdx;
            ref UnsafeList<Block> targetVoxels = ref(_snapshot_enabled ? ref _snapshot_voxels : ref voxels);
            
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = targetBrickIdx[brick_sector_index_id];

            // Skip setting empty to empty bricks
            if (bid == BRICKID_EMPTY && b.isEmpty)
            {
                return;
            }

            // To put non-empty block to empty brick,
            // Create the brick first
            if (bid == BRICKID_EMPTY)
            {
                // Wait until lock release
                while(Interlocked.Read(ref _sectorAllocLock) != 0) {}
                Interlocked.Increment(ref _sectorAllocLock);
                
                targetBrickIdx[brick_sector_index_id] = (short)currentBrickId[0];
                bid = (short)currentBrickId[0];
                targetVoxels.AddReplicate(Block.Empty, BLOCKS_IN_BRICK);

                brickFlags[brick_sector_index_id] = BrickUpdateInfo.Type.Added;
                updateRecord.Add((short)brick_sector_index_id);

                currentBrickId[0]++;
                
                Interlocked.Decrement(ref _sectorAllocLock);
            }

            // Set the block in target brick
            int voxelIdxInBrick = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);
            int vid = bid * BLOCKS_IN_BRICK + voxelIdxInBrick;

            if (targetVoxels[vid] != b)
            {
                // Use precomputed lookup table for propagation direction mask
                uint directionMask = GetVoxelPropagationMask(voxelIdxInBrick);
                MarkBrickDirty(brick_sector_index_id, DirtyFlags.Reserved0, directionMask);
                targetVoxels[vid] = b;
            }
        }

        /// <summary>
        /// Reorders bricks in memory for better cache coherency.
        /// </summary>
        /// <remarks>
        /// Currently not implemented. In the future, this method could reorganize bricks in memory
        /// to improve spatial locality and cache performance during rendering and physics updates.
        /// </remarks>
        public void ReorderBricks()
        {
            // Not yet implemented
            return;
        }

        /// <summary>
        /// Cleans up dirty-related data in sector at the end of a tick.
        /// This is necessary since multiple systems may consume the dirty data for update.
        /// In-place consumption of dirtyness will only allow one system to use.
        /// </summary>
        /// <remarks>
        /// Resets all brick flags to idle and clears the update record. Should be called after
        /// all systems (rendering, physics, etc.) have processed the dirty bricks for this tick.
        /// </remarks>
        public void EndTick()
        {
            foreach (var record in updateRecord)
            {
                brickFlags[record] = BrickUpdateInfo.Type.Idle;
            }

            updateRecord.Clear();
        }

        /// <summary>
        /// Marks a brick as dirty with the specified flags.
        /// </summary>
        /// <param name="brickIdx">The absolute brick index to mark as dirty.</param>
        /// <param name="flags">The dirty flags to set.</param>
        /// <param name="directionMask">Bitmask indicating which of 26 neighbor directions need propagation (0xFFFFFFFF = all directions)</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkBrickDirty(int brickIdx, DirtyFlags flags, uint directionMask = 0xFFFFFFFF)
        {
            brickDirtyFlags[brickIdx] |= (ushort)flags;
            brickDirtyDirectionMask[brickIdx] |= directionMask;

            // Update sector-level tracking for which neighbor sectors need creation
            // This combines the voxel propagation direction with brick sector boundary info
            uint brickSectorNeighborMask = GetBrickSectorNeighborMask(brickIdx);
            uint crossSectorPropagation = directionMask & brickSectorNeighborMask;
            if (crossSectorPropagation != 0)
            {
                sectorNeighborsToCreate |= crossSectorPropagation;
            }

            if ((sectorDirtyFlags & (ushort)flags) == (ushort)flags) return;

            // Wait until lock release
            while(Interlocked.Read(ref _sectorSetDirtyLock) != 0) {}
            Interlocked.Increment(ref _sectorSetDirtyLock);

            sectorDirtyFlags |= (ushort)flags;

            Interlocked.Decrement(ref _sectorSetDirtyLock);
        }

        /// <summary>
        /// Marks a brick as requiring an update with the specified flags.
        /// </summary>
        /// <param name="brickIdx">The absolute brick index to mark for update.</param>
        /// <param name="flags">The update flags to set.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkBrickRequireUpdate(int brickIdx, DirtyFlags flags)
        {
            brickRequireUpdateFlags[brickIdx] |= (ushort)flags;
            sectorRequireUpdateFlags |= (ushort)flags;
        }

        /// <summary>
        /// Clears all dirty flags for all bricks in the sector.
        /// </summary>
        public void ClearAllDirtyFlags()
        {
            int totalBricks = SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS;
            UnsafeUtility.MemClear(brickDirtyFlags, totalBricks * sizeof(ushort));
            UnsafeUtility.MemClear(brickDirtyDirectionMask, totalBricks * sizeof(uint));
            sectorDirtyFlags = 0;
            sectorNeighborsToCreate = 0;
        }

        /// <summary>
        /// Clears specific require-update flags for all bricks in the sector.
        /// </summary>
        /// <param name="flags">The flags to clear.</param>
        public void ClearRequireUpdateFlags(DirtyFlags flags)
        {
            ushort clearMask = (ushort)~flags;
            int totalBricks = SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS;
            ushort aggregated = 0;

            for (int i = 0; i < totalBricks; i++)
            {
                brickRequireUpdateFlags[i] &= clearMask;
                aggregated |= brickRequireUpdateFlags[i];
            }

            sectorRequireUpdateFlags = aggregated;
        }

        /// <summary>
        /// Clears specific dirty flags for all bricks in the sector.
        /// </summary>
        /// <param name="flags">The flags to clear.</param>
        public void ClearDirtyFlags(DirtyFlags flags)
        {
            ushort clearMask = (ushort)~flags;
            int totalBricks = SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS;
            ushort aggregated = 0;
            uint neighborsToCreateAggregated = 0;

            for (int i = 0; i < totalBricks; i++)
            {
                brickDirtyFlags[i] &= clearMask;
                aggregated |= brickDirtyFlags[i];

                // Clear direction mask if no dirty flags remain for this brick
                if (brickDirtyFlags[i] == 0)
                {
                    brickDirtyDirectionMask[i] = 0;
                }
                else
                {
                    // Recalculate which sectors need creation based on remaining dirty bricks
                    uint brickSectorNeighborMask = GetBrickSectorNeighborMask(i);
                    uint crossSectorPropagation = brickDirtyDirectionMask[i] & brickSectorNeighborMask;
                    neighborsToCreateAggregated |= crossSectorPropagation;
                }
            }

            sectorDirtyFlags = aggregated;
            sectorNeighborsToCreate = neighborsToCreateAggregated;
        }

        /// <summary>
        /// Clears all require-update flags for all bricks in the sector.
        /// </summary>
        public void ClearAllRequireUpdateFlags()
        {
            int totalBricks = SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS;
            UnsafeUtility.MemClear(brickRequireUpdateFlags, totalBricks * sizeof(ushort));
            sectorRequireUpdateFlags = 0;
        }

        #region PHYSICS

        /// <summary>
        /// List of absolute indices of all non-empty bricks, used for physics and iteration optimization.
        /// </summary>
        [NativeDisableParallelForRestriction] public UnsafeList<short> NonEmptyBricks;

        /// <summary>
        /// Updates the NonEmptyBricks list to reflect current sector state.
        /// </summary>
        /// <remarks>
        /// Scans all bricks and rebuilds the list of non-empty brick indices.
        /// Should be called after bulk modifications to ensure the acceleration structure is up to date.
        /// </remarks>
        [BurstCompile]
        public void UpdateNonEmptyBricks()
        {
            NonEmptyBricks.Clear();
            for (short i = 0; i < SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS; i++)
            {
                if (brickIdx[i] != BRICKID_EMPTY)
                {
                    NonEmptyBricks.Add(i);
                }
            }
        }

        #endregion
    }
    
    /// <summary>
    /// Iterator structure for enumerating blocks within a sector along with their positions.
    /// </summary>
    public struct BrickIterator
    {
        /// <summary>
        /// The brick at the current iterator position.
        /// </summary>
        public short brickIdx;

        /// <summary>
        /// The 3D position of the brick (in blocks) within the sector.
        /// </summary>
        public int3 position;
    }
}
