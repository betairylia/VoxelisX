using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Codice.CM.SEIDInfo;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis
{
    public unsafe struct SectorSlotStorage
    {
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<byte> data;
        public int stride;

        public bool IsCreated => data.IsCreated;

        public static SectorSlotStorage New(int stride, int initialBricks, Allocator allocator, NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            int byteCapacity = math.max(1, initialBricks * Sector.BLOCKS_IN_BRICK * stride);
            var storage = new SectorSlotStorage
            {
                data = new UnsafeList<byte>(byteCapacity, allocator),
                stride = stride,
            };
            storage.data.Resize(byteCapacity, initialization);
            return storage;
        }

        public SectorSlotStorage Clone(Allocator allocator)
        {
            var clone = new SectorSlotStorage
            {
                data = IsCreated ? new UnsafeList<byte>(data.Length, allocator) : new UnsafeList<byte>(),
                stride = stride
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
        }

        public void EnsureBrickCapacity(int brickCapacity, NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            int bytes = brickCapacity * Sector.BLOCKS_IN_BRICK * stride;
            if (bytes > data.Length) { data.Resize(bytes, initialization); }
        }

        public void ClearBrick(short bid)
        {
            UnsafeUtility.MemClear(data.Ptr + bid * Sector.BLOCKS_IN_BRICK * stride, Sector.BLOCKS_IN_BRICK * stride);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(short bid, int voxelIdxInBrick) where T : unmanaged
        {
            if (!IsCreated) return default;
            
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
            return data.Ptr + bid * Sector.BLOCKS_IN_BRICK * stride;
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
            => GetSlot<Block>(SectorSlotId.Block, x, y, z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetSlot<T>(SectorSlotId slotId, int x, int y, int z) where T : unmanaged
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated) { return default; }

            return slot->Get<T>(bid, ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block block)
            => SetSlot(SectorSlotId.Block, x, y, z, block);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetSlot<T>(SectorSlotId slotId, int x, int y, int z, T value)
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
                while (Interlocked.Read(ref _sectorAllocLock) != 0) {}
                Interlocked.Increment(ref _sectorAllocLock);

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
                
                // Release the lock
                Interlocked.Decrement(ref _sectorAllocLock);
            }

            // Brick exists, corresponding slot may not be allocated yet
            
            SectorSlotStorage* slot = targetSlots + (int)slotId;
            int voxelIdx = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);
            
            // Check for same value
            // If slot !IsCreated, Get will return default
            T previous = slot->Get<T>(bid, voxelIdx);
            if (value.Equals(previous))
            {
                return;
            }
            
            // Allocate the slot if not yet allocated for this sector
            if (!slot->IsCreated)
            {
                while (Interlocked.Read(ref _sectorAllocLock) != 0) {}
                Interlocked.Increment(ref _sectorAllocLock);
                
                // TODO: Is this _allocator okay?
                targetSlots[(int)slotId] =
                    SectorSlotStorage.New(UnsafeUtility.SizeOf<T>(), targetBrickMap.Capacity, _allocator);
                
                Interlocked.Decrement(ref _sectorAllocLock);
            }
            
            // Set the data
            DirtyFlags dirtyFlags = slotId == SectorSlotId.Block
                ? DirtyPropagationSettings.DefaultSetBlockFlags
                : DirtyFlags.GeneralAutomata;
            MarkBrickDirty(brickIdx, dirtyFlags, GetVoxelPropagationMask(voxelIdx));
            slot->Set(bid, voxelIdx, value);
        }
    }
}
