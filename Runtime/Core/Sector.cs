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
    public unsafe struct SectorSlotStorage
    {
        public UnsafeList<byte> data;
        public UnsafeList<byte> brickPresent;
        public int stride;
        public int presentCount;

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => data.IsCreated;
        }

        public int BrickCapacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => brickPresent.IsCreated ? brickPresent.Length : 0;
        }

        public static SectorSlotStorage New(int stride, int initialBrickCapacity, Allocator allocator)
        {
            int byteCapacity = math.max(1, initialBrickCapacity) * Sector.BLOCKS_IN_BRICK * stride;
            var storage = new SectorSlotStorage
            {
                data = new UnsafeList<byte>(byteCapacity, allocator),
                brickPresent = new UnsafeList<byte>(math.max(1, initialBrickCapacity), allocator),
                stride = stride,
                presentCount = 0,
            };

            if (initialBrickCapacity > 0)
            {
                storage.EnsureBrickCapacity(initialBrickCapacity);
            }

            return storage;
        }

        public SectorSlotStorage Clone(Allocator allocator)
        {
            var clone = New(stride, BrickCapacity, allocator);
            clone.presentCount = presentCount;

            if (brickPresent.Length > 0)
            {
                clone.brickPresent.Resize(brickPresent.Length, NativeArrayOptions.UninitializedMemory);
                UnsafeUtility.MemCpy(clone.brickPresent.Ptr, brickPresent.Ptr, brickPresent.Length);
            }

            if (data.Length > 0)
            {
                clone.data.Resize(data.Length, NativeArrayOptions.UninitializedMemory);
                UnsafeUtility.MemCpy(clone.data.Ptr, data.Ptr, data.Length);
            }

            return clone;
        }

        public void Dispose()
        {
            if (data.IsCreated) data.Dispose();
            if (brickPresent.IsCreated) brickPresent.Dispose();
            presentCount = 0;
            stride = 0;
        }

        public void EnsureBrickCapacity(int brickCapacity)
        {
            if (brickCapacity <= BrickCapacity) return;

            brickPresent.Resize(brickCapacity, NativeArrayOptions.ClearMemory);
            data.Resize(brickCapacity * Sector.BLOCKS_IN_BRICK * stride, NativeArrayOptions.ClearMemory);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasBrick(short bid)
        {
            return bid >= 0 && bid < brickPresent.Length && brickPresent[bid] != 0;
        }

        public void ClearBrick(short bid)
        {
            if (bid < 0) return;
            EnsureBrickCapacity(bid + 1);

            if (brickPresent[bid] != 0)
            {
                brickPresent[bid] = 0;
                presentCount--;
            }

            UnsafeUtility.MemClear(data.Ptr + bid * Sector.BLOCKS_IN_BRICK * stride, Sector.BLOCKS_IN_BRICK * stride);
        }

        public void EnsureBrickPresent(short bid)
        {
            EnsureBrickCapacity(bid + 1);
            if (brickPresent[bid] != 0) return;

            brickPresent[bid] = 1;
            presentCount++;
            UnsafeUtility.MemClear(data.Ptr + bid * Sector.BLOCKS_IN_BRICK * stride, Sector.BLOCKS_IN_BRICK * stride);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(short bid, int voxelIdxInBrick) where T : unmanaged
        {
            if (!HasBrick(bid))
            {
                return default;
            }

            int byteOffset = (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * stride;
            return UnsafeUtility.ReadArrayElement<T>(data.Ptr + byteOffset, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(short bid, int voxelIdxInBrick, T value) where T : unmanaged
        {
            int byteOffset = (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * stride;
            UnsafeUtility.WriteArrayElement(data.Ptr + byteOffset, 0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickPtr(short bid)
        {
            if (!HasBrick(bid))
            {
                return null;
            }

            return data.Ptr + bid * Sector.BLOCKS_IN_BRICK * stride;
        }
    }

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

        /// <summary>Sentinel value for an unallocated brick slot in <see cref="brickIdx"/>.</summary>
        public const short BRICKID_EMPTY = SparseBrickIdTable.EMPTY;
        public const int MAX_SLOTS = 16;

        /// <summary>
        /// Fixed table of enum-addressed slot storage. Slot data uses the shared <see cref="brickMap"/>
        /// namespace, but each slot tracks whether it has data for a shared brick ID.
        /// </summary>
        public UnsafeList<SectorSlotStorage> slots;

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
        public bool IsRendererRequireUpdate => (sectorRequireUpdateFlags & (ushort)(DirtyFlags.GeometryWithLocalNeighbor | DirtyFlags.BrickAdded | DirtyFlags.BrickRemoved)) != 0;

        // Lock for brick-thread-safe write
        private long _sectorAllocLock, _sectorSetDirtyLock;
        private Allocator _allocator;

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector.
        /// </summary>
        public int NonEmptyBrickCount => brickMap.Count;

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector for rendering.
        /// </summary>
        public int RendererNonEmptyBrickCount => CountSlotBricks(SectorSlotId.Block);

        /// <summary>
        /// Returns true if the sector contains no allocated bricks.
        /// </summary>
        public bool IsRendererEmpty => RendererNonEmptyBrickCount == 0;

        /// <summary>
        /// Gets the approximate host memory usage of this sector in bytes.
        /// </summary>
        public int MemoryUsage
        {
            get
            {
                int total = 0;
                if (!slots.IsCreated) return total;

                for (int i = 0; i < slots.Length; i++)
                {
                    SectorSlotStorage slot = slots[i];
                    if (!slot.IsCreated) continue;
                    total += slot.data.Capacity;
                    total += slot.brickPresent.Capacity;
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
            bool createBlockSlot = true)
        {
            int totalBricks = BRICKS_IN_SECTOR;

            Sector s = new Sector()
            {
                slots = new UnsafeList<SectorSlotStorage>(MAX_SLOTS, allocator),
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

            s.slots.Resize(MAX_SLOTS, NativeArrayOptions.ClearMemory);
            if (createBlockSlot)
            {
                s.CreateSlot(SectorSlotId.Block, UnsafeUtility.SizeOf<Block>(), allocator);
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
                createBlockSlot: false);

            s.CloneSlotsFrom(in from, allocator);

            return s;
        }
        
        /// <summary>
        /// Disposes all native collections used by this sector, releasing unmanaged memory.
        /// </summary>
        public void Dispose(Allocator allocator)
        {
            DisposeSlotTable(ref slots);
            DisposeSlotTable(ref _snapshot_slots);
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

        private static int SlotIndex(SectorSlotId slotId) => (byte)slotId;

        private static int DefaultStrideForSlot(SectorSlotId slotId)
        {
            switch (slotId)
            {
                case SectorSlotId.Block:
                    return UnsafeUtility.SizeOf<Block>();
                case SectorSlotId.Meta:
                    return UnsafeUtility.SizeOf<Meta>();
                default:
                    return 0;
            }
        }

        private static bool IsDefaultValue<T>(ref T value) where T : unmanaged
        {
            T defaultValue = default;
            return UnsafeUtility.MemCmp(
                UnsafeUtility.AddressOf(ref value),
                UnsafeUtility.AddressOf(ref defaultValue),
                UnsafeUtility.SizeOf<T>()) == 0;
        }

        private static bool ValuesEqual<T>(ref T a, ref T b) where T : unmanaged
        {
            return UnsafeUtility.MemCmp(
                UnsafeUtility.AddressOf(ref a),
                UnsafeUtility.AddressOf(ref b),
                UnsafeUtility.SizeOf<T>()) == 0;
        }

        private void CreateSlot(SectorSlotId slotId, int stride, Allocator allocator)
        {
            int slotIndex = SlotIndex(slotId);
            SectorSlotStorage* slot = slots.Ptr + slotIndex;
            if (slot->IsCreated) return;

            *slot = SectorSlotStorage.New(stride, brickMap.Capacity, allocator);
        }

        private void EnsureSlot(SectorSlotId slotId, int stride, Allocator allocator)
        {
            CreateSlot(slotId, stride, allocator);
            (slots.Ptr + SlotIndex(slotId))->EnsureBrickCapacity(brickMap.Capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SectorSlotStorage* GetSlotStorage(SectorSlotId slotId)
        {
            int slotIndex = SlotIndex(slotId);
            if (!slots.IsCreated || slotIndex < 0 || slotIndex >= slots.Length)
            {
                return null;
            }

            SectorSlotStorage* slot = slots.Ptr + slotIndex;
            return slot->IsCreated ? slot : null;
        }

        private void ExtendCreatedSlots(int brickCapacity)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                SectorSlotStorage* slot = slots.Ptr + i;
                if (slot->IsCreated)
                {
                    slot->EnsureBrickCapacity(brickCapacity);
                }
            }
        }

        private void ClearSlotBrick(short bid)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                SectorSlotStorage* slot = slots.Ptr + i;
                if (slot->IsCreated)
                {
                    slot->ClearBrick(bid);
                }
            }
        }

        private void CloneSlotsFrom(in Sector from, Allocator allocator)
        {
            DisposeSlotTable(ref slots);
            slots = new UnsafeList<SectorSlotStorage>(MAX_SLOTS, allocator);
            slots.Resize(MAX_SLOTS, NativeArrayOptions.ClearMemory);

            if (!from.slots.IsCreated) return;

            for (int i = 0; i < math.min(from.slots.Length, MAX_SLOTS); i++)
            {
                SectorSlotStorage source = from.slots[i];
                if (source.IsCreated)
                {
                    *(slots.Ptr + i) = source.Clone(allocator);
                }
            }
        }

        private static void DisposeSlotTable(ref UnsafeList<SectorSlotStorage> slotTable)
        {
            if (!slotTable.IsCreated) return;

            for (int i = 0; i < slotTable.Length; i++)
            {
                SectorSlotStorage slot = slotTable[i];
                if (slot.IsCreated)
                {
                    slot.Dispose();
                }
            }

            slotTable.Dispose();
        }

        public bool IsSlotCreated(SectorSlotId slotId)
        {
            return GetSlotStorage(slotId) != null;
        }

        public int CountSlotBricks(SectorSlotId slotId)
        {
            SectorSlotStorage* slot = GetSlotStorage(slotId);
            if (slot == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < BRICKS_IN_SECTOR; i++)
            {
                short bid = brickIdx[i];
                if (bid != BRICKID_EMPTY && slot->HasBrick(bid))
                {
                    count++;
                }
            }

            return count;
        }

        public bool HasSlotBrick(SectorSlotId slotId, short bid)
        {
            SectorSlotStorage* slot = GetSlotStorage(slotId);
            return slot != null && slot->HasBrick(bid);
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

            SectorSlotStorage* blockSlot = GetSlotStorage(SectorSlotId.Block);
            return blockSlot == null ? null : (Block*)blockSlot->GetBrickPtr(bid);
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
            => GetSlot<Block>(SectorSlotId.Block, x, y, z);

        public Meta GetMeta(int x, int y, int z)
            => GetSlot<Meta>(SectorSlotId.Meta, x, y, z);

        public T GetSlot<T>(SectorSlotId slotId, int x, int y, int z) where T : unmanaged
        {
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = this.brickIdx[brick_sector_index_id];

            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = GetSlotStorage(slotId);
            if (slot == null)
            {
                return default;
            }

            return slot->Get<T>(
                bid,
                ToBlockIdx(
                    x & BRICK_MASK,
                    y & BRICK_MASK,
                    z & BRICK_MASK));
        }

        #region Snapshots

        /// <summary>
        /// Backbuffer version of slot storage for read access during double-buffered updates (e.g., cellular automata).
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<SectorSlotStorage> _snapshot_slots;

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

            if (!_snapshot_brickMap.IsCreated)
            {
                _snapshot_brickMap = SparseBrickIdTable.New(allocator);
            }

            DisposeSlotTable(ref _snapshot_slots);
            _snapshot_slots = new UnsafeList<SectorSlotStorage>(MAX_SLOTS, allocator);
            _snapshot_slots.Resize(MAX_SLOTS, NativeArrayOptions.ClearMemory);
            if (slots.IsCreated)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    SectorSlotStorage source = slots[i];
                    if (source.IsCreated)
                    {
                        *(_snapshot_slots.Ptr + i) = source.Clone(allocator);
                    }
                }
            }

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
            => SetSlot(SectorSlotId.Block, x, y, z, b);

        public void SetMeta(int x, int y, int z, Meta meta)
            => SetSlot(SectorSlotId.Meta, x, y, z, meta);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSlot<T>(SectorSlotId slotId, int x, int y, int z, T value) where T : unmanaged
        {
            ref SparseBrickIdTable targetBrickMap = ref(_snapshot_enabled ? ref _snapshot_brickMap : ref brickMap);
            ref UnsafeList<SectorSlotStorage> targetSlots = ref(_snapshot_enabled ? ref _snapshot_slots : ref slots);

            int bx = x >> SHIFT_IN_BLOCKS;
            int by = y >> SHIFT_IN_BLOCKS;
            int bz = z >> SHIFT_IN_BLOCKS;
            int brick_sector_index_id = ToBrickIdx(bx, by, bz);
            short bid = targetBrickMap.indices[brick_sector_index_id];
            bool valueIsDefault = IsDefaultValue(ref value);

            // Skip setting default data to absent shared bricks or absent slot bricks.
            if (bid == BRICKID_EMPTY && valueIsDefault)
            {
                return;
            }

            int slotIndex = SlotIndex(slotId);
            int slotStride = UnsafeUtility.SizeOf<T>();
            SectorSlotStorage* targetSlot = null;
            if (targetSlots.IsCreated && slotIndex >= 0 && slotIndex < targetSlots.Length)
            {
                targetSlot = targetSlots.Ptr + slotIndex;
            }

            if ((targetSlot == null || !targetSlot->IsCreated) && valueIsDefault)
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

                targetBrickMap.AddBrick(new int3(bx, by, bz), out int newId, out bool exceedsCapacity);
                bid = (short)newId;

                if (exceedsCapacity)
                {
                    for (int i = 0; i < targetSlots.Length; i++)
                    {
                        SectorSlotStorage* slot = targetSlots.Ptr + i;
                        if (slot->IsCreated)
                        {
                            slot->EnsureBrickCapacity(targetBrickMap.Capacity);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < targetSlots.Length; i++)
                    {
                        SectorSlotStorage* slot = targetSlots.Ptr + i;
                        if (slot->IsCreated)
                        {
                            slot->ClearBrick(bid);
                        }
                    }
                }

                if (slotId == SectorSlotId.Block)
                {
                    MarkBrickDirty(brick_sector_index_id, DirtyFlags.BrickAdded, 0);
                }

                Interlocked.Decrement(ref _sectorAllocLock);
            }

            if (targetSlot == null || !targetSlot->IsCreated)
            {
                int stride = DefaultStrideForSlot(slotId);
                if (stride == 0)
                {
                    stride = slotStride;
                }

                if (!targetSlots.IsCreated)
                {
                    targetSlots = new UnsafeList<SectorSlotStorage>(MAX_SLOTS, _allocator);
                    targetSlots.Resize(MAX_SLOTS, NativeArrayOptions.ClearMemory);
                }

                targetSlot = targetSlots.Ptr + slotIndex;
                *targetSlot = SectorSlotStorage.New(stride, targetBrickMap.Capacity, _allocator);
            }

            if (valueIsDefault && !targetSlot->HasBrick(bid))
            {
                return;
            }

            if (!targetSlot->HasBrick(bid))
            {
                targetSlot->EnsureBrickPresent(bid);
            }

            // Set the slot value in target brick
            int voxelIdxInBrick = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);
            T previous = targetSlot->Get<T>(bid, voxelIdxInBrick);

            if (!ValuesEqual(ref previous, ref value))
            {
                // Use precomputed lookup table for propagation direction mask
                uint directionMask = GetVoxelPropagationMask(voxelIdxInBrick);
                DirtyFlags dirtyFlags = slotId == SectorSlotId.Block
                    ? DirtyPropagationSettings.DefaultSetBlockFlags
                    : DirtyFlags.GeneralAutomata;
                MarkBrickDirty(brick_sector_index_id, dirtyFlags, directionMask);
                targetSlot->Set(bid, voxelIdxInBrick, value);
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
                // Wait until lock release
                while (Interlocked.Read(ref _sectorSetDirtyLock) != 0) { }
                Interlocked.Increment(ref _sectorSetDirtyLock);
                
                sectorNeighborsToCreate |= crossSectorPropagation;
                
                Interlocked.Decrement(ref _sectorSetDirtyLock);
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
            SectorSlotStorage* blockSlot = GetSlotStorage(SectorSlotId.Block);
            if (blockSlot == null)
            {
                return;
            }

            for (short i = 0; i < SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS; i++)
            {
                short bid = brickIdx[i];
                if (bid != BRICKID_EMPTY && blockSlot->HasBrick(bid))
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
