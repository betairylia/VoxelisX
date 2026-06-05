using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis
{
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
    public unsafe partial struct Sector
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

        /// <summary>Sentinel value for an unallocated brick slot in <see cref="brickIdx"/>.</summary>
        public const short BRICKID_EMPTY = SparseBrickIdTable.EMPTY;
        public const int MAX_SLOTS = 16;
        
        public SparseBrickIdTable brickMap;
        public short* brickIdx
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => brickMap.indices;
        }
        
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
        public bool IsRendererRequireUpdate => (sectorRequireUpdateFlags & (ushort)(DirtyFlags.GeometryWithLocalNeighbor | DirtyFlags.BlockBrickAdded | DirtyFlags.BlockBrickRemoved)) != 0;

        // Binary spin gates (0 = free, 1 = held) guarding the rare brick-/slot-allocation
        // and sector-flag-aggregation critical sections. Acquired/released only through
        // AcquireSpinGate/ReleaseSpinGate so the test-and-set stays a single atomic op.
        private long _sectorAllocLock, _sectorSetDirtyLock;
        private Allocator _allocator;

        /// <summary>
        /// Atomically acquires a binary spin gate. Burst-safe (Interlocked only, no managed
        /// SpinWait). Unlike the previous Read-then-Increment pair, CompareExchange performs
        /// the test-and-set as one atomic operation, so two threads can never both observe
        /// the gate as free and both "acquire" it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AcquireSpinGate(ref long gate)
        {
            while (Interlocked.CompareExchange(ref gate, 1L, 0L) != 0L) { }
        }

        /// <summary>Releases a binary spin gate acquired via <see cref="AcquireSpinGate"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReleaseSpinGate(ref long gate)
        {
            Interlocked.Exchange(ref gate, 0L);
        }

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector.
        /// </summary>
        public int NonEmptyBrickCount => brickMap.Count;

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector for rendering.
        /// </summary>
        public int RendererNonEmptyBrickCount => brickMap.Count;

        /// <summary>
        /// Returns true if the sector contains no allocated bricks.
        /// </summary>
        public bool IsRendererEmpty => brickMap.Count == 0;

        /// <summary>
        /// Gets the approximate host memory usage of this sector in bytes.
        /// </summary>
        public int MemoryUsage
        {
            get
            {
                int total = 0;

                for (int i = 0; i < MAX_SLOTS; i++)
                {
                    SectorSlotStorage slot = slots[i];
                    if (!slot.IsCreated) continue;
                    total += slot.data.Capacity;
                }

                return total;
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
            NativeArrayOptions options = NativeArrayOptions.ClearMemory,
            SparseBrickIdTable? copyFrom = null,
            bool createDefaultSlots = true)
        {
            int totalBricks = BRICKS_IN_SECTOR;

            Sector s = new Sector()
            {
                slots = (SectorSlotStorage*)UnsafeUtility.Malloc(MAX_SLOTS * sizeof(SectorSlotStorage), UnsafeUtility.AlignOf<SectorSlotStorage>(), allocator),
                _snapshot_slots = null,
                brickMap = (copyFrom == null ? SparseBrickIdTable.New(allocator) : copyFrom.Value.Clone(allocator)),
                brickDirtyFlags = (ushort*)UnsafeUtility.Malloc(totalBricks * sizeof(ushort), UnsafeUtility.AlignOf<ushort>(), allocator),
                brickRequireUpdateFlags = (ushort*)UnsafeUtility.Malloc(totalBricks * sizeof(ushort), UnsafeUtility.AlignOf<ushort>(), allocator),
                brickDirtyDirectionMask = (uint*)UnsafeUtility.Malloc(totalBricks * sizeof(uint), UnsafeUtility.AlignOf<uint>(), allocator),
                NonEmptyBricks = new UnsafeList<short>(initialBricks, allocator),
                sectorDirtyFlags = 0,
                sectorRequireUpdateFlags = 0,
                sectorNeighborsToCreate = 0,
                _snapshot_enabled = false,
                _allocator = allocator,
            };

            UnsafeUtility.MemClear(s.slots, MAX_SLOTS * sizeof(SectorSlotStorage));
            if (createDefaultSlots)
            {
                s.slots[(int)SectorSlotId.Block] = SectorSlotStorage.New(
                    UnsafeUtility.SizeOf<Block>(), initialBricks, allocator);
            }

            if (options == NativeArrayOptions.ClearMemory)
            {
                // brickMap.indices is already initialized to BRICKID_EMPTY by SparseBrickIdTable.New;
                // only the dirty buffers need clearing here.
                UnsafeUtility.MemClear(s.brickDirtyFlags, totalBricks * sizeof(ushort));
                UnsafeUtility.MemClear(s.brickRequireUpdateFlags, totalBricks * sizeof(ushort));
                UnsafeUtility.MemClear(s.brickDirtyDirectionMask, totalBricks * sizeof(uint));
            }

            return s;
        }

        /// <summary>
        /// Creates a copy of a sector without initialize the dirty/requireUpdate arrays.
        /// </summary>
        /// <param name="from">The sector handle to clone from.</param>
        /// <param name="allocator">The memory allocator to use for the new sector.</param>
        /// <returns>A new sector containing the same voxel data but with undefined dirty records.</returns>
        public static Sector CloneWithUndefinedDirtiness(
            SectorHandle from,
            Allocator allocator)
            => CloneWithUndefinedDirtiness(*(from.Ptr), allocator);

        /// <summary>
        /// Creates a copy of a sector without initialize the dirty/requireUpdate arrays.
        /// </summary>
        /// <param name="from">The sector to clone from.</param>
        /// <param name="allocator">The memory allocator to use for the new sector.</param>
        /// <returns>A new sector containing the same voxel data but with undefined dirty records.</returns>
        public static Sector CloneWithUndefinedDirtiness(
            Sector from,
            Allocator allocator)
        {
            // Pass copyFrom so the new brickMap is a faithful clone (indices, freelist, capacity).
            // The dirty buffers stay uninitialized intentionally per the method's contract.
            Sector s = Sector.New(
                allocator,
                from.NonEmptyBrickCount,
                NativeArrayOptions.UninitializedMemory,
                copyFrom: from.brickMap,
                createDefaultSlots: false);

            CopySlotsTo(from.slots, s.slots, allocator);

            return s;
        }
        
        /// <summary>
        /// Disposes all native collections used by this sector, releasing unmanaged memory.
        /// </summary>
        public void Dispose(Allocator allocator)
        {
            if(slots != null)
            {
                ResetSlotTable(ref slots);
                UnsafeUtility.Free(slots, allocator);
                slots = null;
            }

            if (_snapshot_slots != null)
            {
                ResetSlotTable(ref _snapshot_slots);
                UnsafeUtility.Free(_snapshot_slots, allocator);
                _snapshot_slots = null;
            }

            if (brickMap.IsCreated) brickMap.Dispose();
            if (_snapshot_brickMap.IsCreated) _snapshot_brickMap.Dispose();
            if (NonEmptyBricks.IsCreated) NonEmptyBricks.Dispose();
            if (brickDirtyFlags != null) UnsafeUtility.Free(brickDirtyFlags, allocator);
            if (brickRequireUpdateFlags != null) UnsafeUtility.Free(brickRequireUpdateFlags, allocator);
            if (brickDirtyDirectionMask != null) UnsafeUtility.Free(brickDirtyDirectionMask, allocator);

            brickDirtyFlags = null;
            brickRequireUpdateFlags = null;
            brickDirtyDirectionMask = null;
            _snapshot_enabled = false;
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
        /// Gets the precomputed propagation direction mask for a voxel position within a brick.
        /// </summary>
        /// <param name="voxelIdxInBrick">Flat voxel index within brick (0-511)</param>
        /// <returns>Bitmask where bit i indicates if direction i needs propagation</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetVoxelPropagationMask(int voxelIdxInBrick)
        {
            return NeighborhoodSettings.s_voxelPropagationMasks[voxelIdxInBrick];
        }

        /// <summary>
        /// Gets the precomputed sector neighbor mask for a brick position.
        /// </summary>
        /// <param name="brickIdx">Flat brick index within sector (0-4095)</param>
        /// <returns>Bitmask where bit i indicates if brick touches sector neighbor i</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetBrickSectorNeighborMask(int brickIdx)
        {
            return NeighborhoodSettings.s_brickSectorNeighborMasks[brickIdx];
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
            return GetBrick<Block>(SectorSlotId.Block, bid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetBrick<T>(SectorSlotId slotId, short bid) 
            where T: unmanaged, IEquatable<T>
        {
            if (bid == BRICKID_EMPTY || (int)slotId >= MAX_SLOTS)
            {
                return null;
            }
            
            SectorSlotStorage slot = slots[(int)slotId];
            return slot.IsCreated ? (T*)slot.GetBrickPtr(bid) : null;
        }

        #region Snapshots
        
        /// <summary>
        /// Backbuffer version of <see cref="brickMap"/> for read access during double-buffered updates.
        /// Allocated lazily on first <see cref="ActivateSnapshot"/> and reused thereafter.
        /// </summary>
        public SparseBrickIdTable _snapshot_brickMap;

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

            if (_snapshot_slots == null)
            {
                _snapshot_slots = (SectorSlotStorage*)UnsafeUtility.Malloc(MAX_SLOTS * sizeof(SectorSlotStorage),
                    UnsafeUtility.AlignOf<SectorSlotStorage>(), allocator);
                UnsafeUtility.MemClear(_snapshot_slots, MAX_SLOTS * sizeof(SectorSlotStorage));
            }

            if (!_snapshot_brickMap.IsCreated)
            {
                _snapshot_brickMap = SparseBrickIdTable.New(allocator);
            }

            CopySlotsTo(slots, _snapshot_slots, allocator);
            _snapshot_brickMap.CopyFrom(brickMap);

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

            // Swap slot buffers
            var tempSlots = slots;
            slots = _snapshot_slots;
            _snapshot_slots = tempSlots;

            // Swap brick maps (struct swap moves the underlying pointers wholesale)
            var tempBrickMap = brickMap;
            brickMap = _snapshot_brickMap;
            _snapshot_brickMap = tempBrickMap;

            _snapshot_enabled = false;
        }

        #endregion

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
                AcquireSpinGate(ref _sectorSetDirtyLock);
                sectorNeighborsToCreate |= crossSectorPropagation;
                ReleaseSpinGate(ref _sectorSetDirtyLock);
            }

            if ((sectorDirtyFlags & (ushort)flags) == (ushort)flags) return;

            AcquireSpinGate(ref _sectorSetDirtyLock);
            sectorDirtyFlags |= (ushort)flags;
            ReleaseSpinGate(ref _sectorSetDirtyLock);
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
            if (sectorDirtyFlags == 0 && sectorNeighborsToCreate == 0) { return; }
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
            if (sectorRequireUpdateFlags == 0) { return; }
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
