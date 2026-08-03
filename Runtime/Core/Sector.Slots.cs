using System;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis
{
    public unsafe struct SectorSlotStorage
    {
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<byte> data;
        public int stride; // TODO: Limit this to po2?
        public int elemPerBrickShift;

        public int BytesPerBrick => (stride << elemPerBrickShift);

        public bool IsCreated => data.IsCreated;

        /// <summary>
        /// True when this slot holds one element per voxel (the default shape). Per-brick slots
        /// (<see cref="elemPerBrickShift"/> == 0) and any other custom shape return false.
        /// Persistence only covers voxel-shaped slots — see <c>SectorSerializer</c>.
        /// </summary>
        public bool IsVoxelShaped => elemPerBrickShift == 3 * Sector.SHIFT_IN_BLOCKS;

        public static SectorSlotStorage New(
            int stride, int initialBricks, Allocator allocator, NativeArrayOptions initialization = NativeArrayOptions.ClearMemory,
            int elemPerBrickShift = 3 * Sector.SHIFT_IN_BLOCKS)
        {
            int byteCapacity = math.max(1, (initialBricks << elemPerBrickShift) * stride);
            var storage = new SectorSlotStorage
            {
                data = new UnsafeList<byte>(byteCapacity, allocator),
                stride = stride,
                elemPerBrickShift = elemPerBrickShift
            };
            storage.data.Resize(byteCapacity, initialization);
            return storage;
        }

        public SectorSlotStorage Clone(Allocator allocator)
        {
            var clone = new SectorSlotStorage
            {
                data = IsCreated ? new UnsafeList<byte>(data.Length, allocator) : new UnsafeList<byte>(),
                stride = stride,
                elemPerBrickShift = elemPerBrickShift
            };
            
            if (IsCreated && data.Length > 0)
            {
                clone.data.Resize(data.Length, NativeArrayOptions.UninitializedMemory);
                UnsafeUtility.MemCpy(clone.data.Ptr, data.Ptr, data.Length);
            }
            return clone;
        }

        public void Dispose()
        {
            if (data.IsCreated) data.Dispose();
            data = default;
            stride = 0;
            elemPerBrickShift = 0;
        }

        public void EnsureBrickCapacity(int brickCapacity, NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            int bytes = brickCapacity * BytesPerBrick;
            if (bytes > data.Length) { data.Resize(bytes, initialization); }
        }

        public void ClearBrick(short bid)
        {
            UnsafeUtility.MemClear(data.Ptr + bid * BytesPerBrick, BytesPerBrick);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(short bid, int voxelIdxInBrick, int inShift, int inStride) where T : unmanaged
        {
            if (!IsCreated) return default;
            
            int byteOffset = ((bid << inShift) + voxelIdxInBrick) * inStride;
            return Get<T>(byteOffset);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Get<T>(int byteOffset) where T : unmanaged
        {
            return UnsafeUtility.ReadArrayElement<T>(data.Ptr + byteOffset, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(short bid, int voxelIdxInBrick, int inShift, int inStride, T value) where T : unmanaged
        {
            int byteOffset = ((bid << inShift) + voxelIdxInBrick) * inStride;
            Set<T>(byteOffset, value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Set<T>(int byteOffset, T value) where T : unmanaged
        {
            UnsafeUtility.WriteArrayElement(data.Ptr + byteOffset, 0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickPtr(short bid)
        {
            return data.Ptr + (bid << elemPerBrickShift) * stride;
        }
    }

    public unsafe partial struct Sector
    {
        /// <summary>
        /// Fixed table of enum-addressed slot storage. Slot data uses the shared <see cref="brickMap"/>
        /// namespace. If a shared brick ID exists, every created slot has data for it.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public SectorSlotStorage* slots;
        
        /// <summary>
        /// Backbuffer version of slot storage for read access during double-buffered updates (e.g., cellular automata).
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public SectorSlotStorage* _snapshot_slots;
        
        private static void ExtendCreatedSlots(SectorSlotStorage* slotTable, int brickCapacity)
        {
            // if (slotTable == null) return;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                SectorSlotStorage* slot = slotTable + i;
                if (slot->IsCreated) { slot->EnsureBrickCapacity(brickCapacity); }
            }
        }

        private static void ClearBrickForAllSlots(SectorSlotStorage* slotTable, short bid)
        {
            // if (slotTable == null) return;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                SectorSlotStorage* slot = slotTable + i;
                if (slot->IsCreated) { slot->ClearBrick(bid); }
            }
        }

        internal static void CopySlotsTo(SectorSlotStorage* from, SectorSlotStorage* to, Allocator allocator)
        {
            ResetSlotTable(ref to);
            for (int i = 0; i < MAX_SLOTS; i++) { to[i] = from[i].Clone(allocator); }
        }

        private static void ResetSlotTable(ref SectorSlotStorage* slotTable)
        {
            if (slotTable == null) return;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                SectorSlotStorage* slot = slotTable + i;
                if (slot->IsCreated) { slot->Dispose(); }
                *slot = default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
            => GetVoxelSlot<Block>(SectorSlotId.Block, x, y, z);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetVoxelSlot<T>(SectorSlotId id, int x, int y, int z)
            where T : unmanaged
            => GetVoxelSlot<T>(id, x, y, z, SHIFT_IN_BLOCKS * 3, UnsafeUtility.SizeOf<T>());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z, int shift, int stride) where T : unmanaged
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated) { return default; }

            return slot->Get<T>(bid, ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK), shift, stride);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetBrickSlot<T>(SectorSlotId slotId, int bX, int bY, int bZ, int bytesPerBrick, int offsetInBytes)
            where T : unmanaged
        {
            int brickIdx = ToBrickIdx(bX, bY, bZ);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated) { return default; }

            return slot->Get<T>(bid * bytesPerBrick + offsetInBytes);
        }

        /// <summary>
        /// Ensures the storage for <paramref name="slotId"/> exists and can hold every currently
        /// allocated brick. Uses the sector's own allocator and <c>sizeof(T)</c> as the stride.
        /// Safe to call repeatedly; only allocates or grows when needed. Intended for bulk
        /// slot producers (e.g. physics-info generation) that write directly into slot storage
        /// instead of going through <see cref="SetVoxelSlot{T}(SectorSlotId,int,int,int,T)"/>.
        /// </summary>
        public void EnsureSlotAllocated<T>(SectorSlotId slotId, int elemPerBrickShift = 3 * SHIFT_IN_BLOCKS) where T : unmanaged
        {
            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated)
            {
                slots[(int)slotId] = SectorSlotStorage.New(
                    UnsafeUtility.SizeOf<T>(), brickMap.Capacity, _allocator,
                    NativeArrayOptions.ClearMemory,
                    elemPerBrickShift);
            }
            else
            {
                slot->EnsureBrickCapacity(brickMap.Capacity);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block block)
            => SetVoxelSlot(SectorSlotId.Block, x, y, z, block);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVoxelSlot<T>(SectorSlotId id, int x, int y, int z, T value)
            where T : unmanaged, IEquatable<T>
            => SetVoxelSlot(id, x, y, z, SHIFT_IN_BLOCKS * 3, UnsafeUtility.SizeOf<T>(), value);
        
        /// <summary>
        /// This will NOT set the brick as dirty, unlike its voxel counter-part.
        /// </summary>
        /// <param name="slotId">The slot ID to write to.</param>
        /// <param name="bX">brick X.</param>
        /// <param name="bY">brick Y.</param>
        /// <param name="bZ">brick Z.</param>
        /// <param name="bytesPerBrick">Byte size of one brick's record in this slot (made explicit).</param>
        /// <param name="offsetInBytes">Optional offset in bytes for the write operation.</param>
        /// <param name="value">Value to write.</param>
        /// <typeparam name="T">Type of the written value.</typeparam>
        /// <exception cref="InvalidOperationException">This method will throw if attempted to write into a non-allocated brick. Write to allocated bricks only or alloc them first with SetVoxelSlot.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBrickSlot<T>(SectorSlotId slotId, int bX, int bY, int bZ, int bytesPerBrick, int offsetInBytes, T value)
            where T : unmanaged
        {
            ref SparseBrickIdTable targetBrickMap = ref(_snapshot_enabled ? ref _snapshot_brickMap : ref brickMap);
            SectorSlotStorage* targetSlots = _snapshot_enabled ? _snapshot_slots : slots;
            
            int brickIdx = ToBrickIdx(bX, bY, bZ);
            short bid = targetBrickMap.indices[brickIdx];
            
            // Brick does not exist, no alloc for brickSlots
            if (bid == BRICKID_EMPTY)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Cannot set brick slot for non-allocated bricks");
#endif
                return;
            }

            SectorSlotStorage* slot = targetSlots + (int)slotId;
            // Allocate the slot if not yet allocated for this sector
            if (!slot->IsCreated)
            {
                AcquireSpinGate(ref _sectorAllocLock);

                if (!slot->IsCreated)
                {
                    // TODO: Is this _allocator okay?
                    targetSlots[(int)slotId] =
                        SectorSlotStorage.New(
                            bytesPerBrick, targetBrickMap.Capacity, _allocator,
                            NativeArrayOptions.ClearMemory,
                            0);
                }

                ReleaseSpinGate(ref _sectorAllocLock);
            }

            slot->Set(bid * bytesPerBrick + offsetInBytes, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z, int shift, int stride, T value)
            where T : unmanaged, IEquatable<T>
        {
            ref SparseBrickIdTable targetBrickMap = ref(_snapshot_enabled ? ref _snapshot_brickMap : ref brickMap);
            SectorSlotStorage* targetSlots = _snapshot_enabled ? _snapshot_slots : slots;

            int bx = x >> SHIFT_IN_BLOCKS;
            int by = y >> SHIFT_IN_BLOCKS;
            int bz = z >> SHIFT_IN_BLOCKS;
            int brickIdx = ToBrickIdx(bx, by, bz);
            short bid = targetBrickMap.indices[brickIdx];

            // Brick does not exist, need alloc
            if (bid == BRICKID_EMPTY)
            {
                if (value.Equals(default)) return;

                // Lock and alloc
                AcquireSpinGate(ref _sectorAllocLock);

                if (targetBrickMap.indices[brickIdx] == BRICKID_EMPTY)
                {
                    targetBrickMap.AddBrick(new int3(bx, by, bz), out int newId, out bool exceedsCapacity);
                    bid = (short)newId;

                    // Alloc or reuse new brick
                    if (exceedsCapacity)
                        ExtendCreatedSlots(targetSlots, targetBrickMap.Capacity);
                    else
                        ClearBrickForAllSlots(targetSlots, bid); // TODO: Do we really need to clear it?

                    // Record the addition for Block slot
                    if (slotId == SectorSlotId.Block)
                    {
                        MarkBrickDirty(brickIdx, DirtyFlags.BlockBrickAdded, 0);
                    }
                }

                // Release the lock
                ReleaseSpinGate(ref _sectorAllocLock);
            }

            // Brick exists, corresponding slot may not be allocated yet
            
            SectorSlotStorage* slot = targetSlots + (int)slotId;
            int voxelIdx = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);
            
            // Check for same value
            // If slot !IsCreated, Get will return default
            T previous = slot->Get<T>(bid, voxelIdx, shift, stride);
            if (value.Equals(previous))
            {
                return;
            }
            
            // Allocate the slot if not yet allocated for this sector
            if (!slot->IsCreated)
            {
                AcquireSpinGate(ref _sectorAllocLock);

                if (!slot->IsCreated)
                {
                    // TODO: Is this _allocator okay?
                    targetSlots[(int)slotId] =
                        SectorSlotStorage.New(UnsafeUtility.SizeOf<T>(), targetBrickMap.Capacity, _allocator);
                }

                ReleaseSpinGate(ref _sectorAllocLock);
            }
            
            // Set the data
            DirtyFlags dirtyFlags = slotId == SectorSlotId.Block
                ? DirtyPropagationSettings.DefaultSetBlockFlags
                : DirtyFlags.GeneralAutomata;
            MarkBrickDirty(brickIdx, dirtyFlags, GetVoxelPropagationMask(voxelIdx));
            
            // Update AABB
            // TODO: Rescan AABB occasionally to handle block removal
            // TODO: Make AABB tight with current snapshot logic -- current version is correct (always superset) but may not be ideal
            if(slotId == SectorSlotId.Block && !value.Equals(default))
                blockAABB.Update(new int3(x, y, z));

            slot->Set(bid, voxelIdx, shift, stride, value);
        }
    }
}
