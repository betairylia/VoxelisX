using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis
{
    public unsafe struct SectorSlotStorage
    {
        public UnsafeList<byte> data;
        public int stride;

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => data.IsCreated;
        }

        public int BrickCapacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => data.IsCreated && stride > 0 ? data.Length / (Sector.BLOCKS_IN_BRICK * stride) : 0;
        }

        public static SectorSlotStorage New(int stride, int initialBrickCapacity, Allocator allocator)
        {
            int byteCapacity = math.max(1, initialBrickCapacity * Sector.BLOCKS_IN_BRICK * stride);
            var storage = new SectorSlotStorage
            {
                data = new UnsafeList<byte>(byteCapacity, allocator),
                stride = stride,
            };
            storage.EnsureBrickCapacity(initialBrickCapacity);
            return storage;
        }

        public SectorSlotStorage Clone(Allocator allocator)
        {
            var clone = New(stride, BrickCapacity, allocator);
            if (data.Length > 0)
            {
                UnsafeUtility.MemCpy(clone.data.Ptr, data.Ptr, data.Length);
            }
            return clone;
        }

        public void Dispose()
        {
            if (data.IsCreated) data.Dispose();
            stride = 0;
        }

        public void EnsureBrickCapacity(int brickCapacity)
        {
            int bytes = brickCapacity * Sector.BLOCKS_IN_BRICK * stride;
            if (bytes > data.Length)
            {
                data.Resize(bytes, NativeArrayOptions.ClearMemory);
            }
        }

        public void ClearBrick(short bid)
        {
            UnsafeUtility.MemClear(data.Ptr + bid * Sector.BLOCKS_IN_BRICK * stride, Sector.BLOCKS_IN_BRICK * stride);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(short bid, int voxelIdxInBrick) where T : unmanaged
        {
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
        private static int SlotIndex(SectorSlotId slotId) => (byte)slotId;

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
            SectorSlotStorage* slot = slots.Ptr + SlotIndex(slotId);
            if (!slot->IsCreated)
            {
                *slot = SectorSlotStorage.New(stride, brickMap.Capacity, allocator);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SectorSlotStorage* GetSlotStorage(SectorSlotId slotId)
        {
            int slotIndex = SlotIndex(slotId);
            if (!slots.IsCreated || slotIndex >= slots.Length)
            {
                return null;
            }

            SectorSlotStorage* slot = slots.Ptr + slotIndex;
            return slot->IsCreated ? slot : null;
        }

        private static void ExtendCreatedSlots(ref UnsafeList<SectorSlotStorage> slotTable, int brickCapacity)
        {
            for (int i = 0; i < slotTable.Length; i++)
            {
                SectorSlotStorage* slot = slotTable.Ptr + i;
                if (slot->IsCreated)
                {
                    slot->EnsureBrickCapacity(brickCapacity);
                }
            }
        }

        private static void ClearSlotBrick(ref UnsafeList<SectorSlotStorage> slotTable, short bid)
        {
            for (int i = 0; i < slotTable.Length; i++)
            {
                SectorSlotStorage* slot = slotTable.Ptr + i;
                if (slot->IsCreated)
                {
                    slot->ClearBrick(bid);
                }
            }
        }

        private SectorSlotStorage* EnsureSlotCreated<T>(
            ref UnsafeList<SectorSlotStorage> slotTable,
            SectorSlotId slotId,
            int brickCapacity) where T : unmanaged
        {
            SectorSlotStorage* slot = slotTable.Ptr + SlotIndex(slotId);
            if (slot->IsCreated)
            {
                return slot;
            }

            while (Interlocked.Read(ref _sectorAllocLock) != 0) {}
            Interlocked.Increment(ref _sectorAllocLock);

            if (!slot->IsCreated)
            {
                *slot = SectorSlotStorage.New(UnsafeUtility.SizeOf<T>(), brickCapacity, _allocator);
            }

            Interlocked.Decrement(ref _sectorAllocLock);
            return slot;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
            => GetSlot<Block>(SectorSlotId.Block, x, y, z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Meta GetMeta(int x, int y, int z)
            => GetSlot<Meta>(SectorSlotId.Meta, x, y, z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetSlot<T>(SectorSlotId slotId, int x, int y, int z) where T : unmanaged
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots.Ptr + SlotIndex(slotId);
            if (!slot->IsCreated)
            {
                return default;
            }

            return slot->Get<T>(bid, ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block block)
            => SetSlot(SectorSlotId.Block, x, y, z, block);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetMeta(int x, int y, int z, Meta meta)
            => SetSlot(SectorSlotId.Meta, x, y, z, meta);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetSlot<T>(SectorSlotId slotId, int x, int y, int z, T value) where T : unmanaged
        {
            ref SparseBrickIdTable targetBrickMap = ref(_snapshot_enabled ? ref _snapshot_brickMap : ref brickMap);
            ref UnsafeList<SectorSlotStorage> targetSlots = ref(_snapshot_enabled ? ref _snapshot_slots : ref slots);

            int bx = x >> SHIFT_IN_BLOCKS;
            int by = y >> SHIFT_IN_BLOCKS;
            int bz = z >> SHIFT_IN_BLOCKS;
            int brickIdx = ToBrickIdx(bx, by, bz);
            short bid = targetBrickMap.indices[brickIdx];

            if (bid == BRICKID_EMPTY)
            {
                if (IsDefaultValue(ref value)) return;

                while (Interlocked.Read(ref _sectorAllocLock) != 0) {}
                Interlocked.Increment(ref _sectorAllocLock);

                targetBrickMap.AddBrick(new int3(bx, by, bz), out int newId, out bool exceedsCapacity);
                bid = (short)newId;

                if (exceedsCapacity)
                    ExtendCreatedSlots(ref targetSlots, targetBrickMap.Capacity);
                else
                    ClearSlotBrick(ref targetSlots, bid);

                if (slotId == SectorSlotId.Block)
                {
                    MarkBrickDirty(brickIdx, DirtyFlags.BrickAdded, 0);
                }

                Interlocked.Decrement(ref _sectorAllocLock);
            }

            SectorSlotStorage* slot = targetSlots.Ptr + SlotIndex(slotId);
            int voxelIdx = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);
            if (!slot->IsCreated)
            {
                if (IsDefaultValue(ref value)) return;

                slot = EnsureSlotCreated<T>(ref targetSlots, slotId, targetBrickMap.Capacity);
                DirtyFlags newSlotDirtyFlags = slotId == SectorSlotId.Block
                    ? DirtyPropagationSettings.DefaultSetBlockFlags
                    : DirtyFlags.GeneralAutomata;
                MarkBrickDirty(brickIdx, newSlotDirtyFlags, GetVoxelPropagationMask(voxelIdx));
                slot->Set(bid, voxelIdx, value);
                return;
            }

            T previous = slot->Get<T>(bid, voxelIdx);
            if (ValuesEqual(ref previous, ref value))
            {
                return;
            }

            DirtyFlags dirtyFlags = slotId == SectorSlotId.Block
                ? DirtyPropagationSettings.DefaultSetBlockFlags
                : DirtyFlags.GeneralAutomata;
            MarkBrickDirty(brickIdx, dirtyFlags, GetVoxelPropagationMask(voxelIdx));
            slot->Set(bid, voxelIdx, value);
        }
    }
}
