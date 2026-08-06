using System;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// Backing storage for one sector slot. Every slot is strictly per-voxel: <see cref="data"/>
    /// holds one <see cref="stride"/>-byte element for each of a brick's
    /// <see cref="Sector.BLOCKS_IN_BRICK"/> voxels, indexed through the shared brick-id namespace.
    ///
    /// A slot may optionally carry an <see cref="aux"/> buffer — one <see cref="extraPerBrickBytes"/>
    /// record per brick — holding data <em>derived</em> from the per-voxel contents (non-empty
    /// occupancy bitmaps, physics-key masks, …). Aux lives in its own allocation so it can be
    /// attached, grown, cleared and dropped independently of the voxel data. It is never
    /// serialized: it is recomputed from <see cref="data"/> for dirty bricks once a tick's edits
    /// settle.
    /// </summary>
    public unsafe struct SectorSlotStorage
    {
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<byte> data;   // per-voxel: brickCapacity * BLOCKS_IN_BRICK * stride bytes
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<byte> aux;    // optional per-brick: brickCapacity * extraPerBrickBytes bytes
        public int stride;              // bytes per voxel element
        public int extraPerBrickBytes;  // bytes per brick in aux; meaningful only while HasAux

        /// <summary>Bytes occupied by one brick's per-voxel records.</summary>
        public int BytesPerBrick => stride * Sector.BLOCKS_IN_BRICK;

        /// <summary>True once the per-voxel buffer exists. This is what "the slot exists" means.</summary>
        public bool IsCreated => data.IsCreated;

        /// <summary>True when this slot carries a derived per-brick aux buffer.</summary>
        public bool HasAux => aux.IsCreated;

        public static SectorSlotStorage New(
            int stride, int initialBricks, Allocator allocator,
            NativeArrayOptions initialization = NativeArrayOptions.ClearMemory,
            int extraPerBrickBytes = 0)
        {
            int voxelBytes = math.max(1, initialBricks * Sector.BLOCKS_IN_BRICK * stride);
            var storage = new SectorSlotStorage
            {
                data = new UnsafeList<byte>(voxelBytes, allocator),
                aux = default,
                stride = stride,
                extraPerBrickBytes = 0
            };
            storage.data.Resize(voxelBytes, initialization);

            if (extraPerBrickBytes > 0)
            {
                storage.AttachAux(extraPerBrickBytes, initialBricks, allocator, initialization);
            }
            return storage;
        }

        /// <summary>
        /// Allocates (replacing any existing) the aux buffer for this slot, sized to hold
        /// <paramref name="brickCapacity"/> bricks. The per-voxel <see cref="data"/> buffer is
        /// expected to already exist — aux summarizes it.
        /// </summary>
        public void AttachAux(
            int extraPerBrickBytes, int brickCapacity, Allocator allocator,
            NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            if (aux.IsCreated) aux.Dispose();
            this.extraPerBrickBytes = extraPerBrickBytes;

            int auxBytes = math.max(1, brickCapacity * extraPerBrickBytes);
            aux = new UnsafeList<byte>(auxBytes, allocator);
            aux.Resize(auxBytes, initialization);
        }

        /// <summary>
        /// Deep-copies this slot, both the voxel <see cref="data"/> and the derived <see cref="aux"/>
        /// buffer. Aux travels with its data so that after a snapshot swap a brick whose data did not
        /// change still has a valid mask; only the bricks that actually changed need re-marking.
        /// </summary>
        public SectorSlotStorage Clone(Allocator allocator)
        {
            var clone = new SectorSlotStorage
            {
                data = IsCreated ? new UnsafeList<byte>(data.Length, allocator) : new UnsafeList<byte>(),
                aux = default,
                stride = stride,
                extraPerBrickBytes = extraPerBrickBytes
            };

            if (IsCreated && data.Length > 0)
            {
                clone.data.Resize(data.Length, NativeArrayOptions.UninitializedMemory);
                UnsafeUtility.MemCpy(clone.data.Ptr, data.Ptr, data.Length);
            }

            if (aux.IsCreated)
            {
                clone.aux = new UnsafeList<byte>(aux.Length, allocator);
                if (aux.Length > 0)
                {
                    clone.aux.Resize(aux.Length, NativeArrayOptions.UninitializedMemory);
                    UnsafeUtility.MemCpy(clone.aux.Ptr, aux.Ptr, aux.Length);
                }
            }
            return clone;
        }

        public void Dispose()
        {
            if (data.IsCreated) data.Dispose();
            if (aux.IsCreated) aux.Dispose();
            data = default;
            aux = default;
            stride = 0;
            extraPerBrickBytes = 0;
        }

        public void EnsureBrickCapacity(int brickCapacity, NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            int bytes = brickCapacity * BytesPerBrick;
            if (bytes > data.Length) { data.Resize(bytes, initialization); }

            if (aux.IsCreated)
            {
                int auxBytes = brickCapacity * extraPerBrickBytes;
                if (auxBytes > aux.Length) { aux.Resize(auxBytes, initialization); }
            }
        }

        public void ClearBrick(short bid)
        {
            UnsafeUtility.MemClear(data.Ptr + bid * BytesPerBrick, BytesPerBrick);
            if (aux.IsCreated)
            {
                UnsafeUtility.MemClear(aux.Ptr + bid * extraPerBrickBytes, extraPerBrickBytes);
            }
        }

        // The element size is taken as a parameter rather than read from the `stride` field so that
        // callers with a statically-known element type can pass a compile-time constant
        // (UnsafeUtility.SizeOf<T>(), or a literal): under Burst that folds the `* inStride` to a
        // shift and drops the `this.stride` load from the hot per-voxel path. The brick shift is
        // already constant via BLOCKS_IN_BRICK, so only the stride needs threading.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(short bid, int voxelIdxInBrick, int inStride) where T : unmanaged
        {
            if (!IsCreated) return default;

            int byteOffset = (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * inStride;
            return Get<T>(byteOffset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Get<T>(int byteOffset) where T : unmanaged
        {
            return UnsafeUtility.ReadArrayElement<T>(data.Ptr + byteOffset, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(short bid, int voxelIdxInBrick, int inStride, T value) where T : unmanaged
        {
            int byteOffset = (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * inStride;
            Set<T>(byteOffset, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Set<T>(int byteOffset, T value) where T : unmanaged
        {
            UnsafeUtility.WriteArrayElement(data.Ptr + byteOffset, 0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T GetAux<T>(int auxByteOffset) where T : unmanaged
        {
            return UnsafeUtility.ReadArrayElement<T>(aux.Ptr + auxByteOffset, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetAux<T>(int auxByteOffset, T value) where T : unmanaged
        {
            UnsafeUtility.WriteArrayElement(aux.Ptr + auxByteOffset, 0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickPtr(short bid)
        {
            return data.Ptr + bid * BytesPerBrick;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickAuxPtr(short bid)
        {
            return aux.Ptr + bid * extraPerBrickBytes;
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

            // Clone carries aux across the snapshot so unchanged bricks keep a valid mask; the mark
            // stages after ApplySnapshot only re-mark the bricks that actually changed this tick.
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

        // ---- Per-voxel slot access -------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
            => GetVoxelSlot<Block>(SectorSlotId.Block, x, y, z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z) where T : unmanaged
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated) { return default; }

            return slot->Get<T>(bid, ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK), UnsafeUtility.SizeOf<T>());
        }

        /// <summary>
        /// Reads the Block and PhysicsInfo values of one voxel with a single brick-map
        /// resolution (instead of one per slot). Returns false — with default outputs — when
        /// the containing brick is not allocated; a true return can still carry an empty block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBlockAndPhysicsInfo(int x, int y, int z, out Block block, out PhysicsInfo info)
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                block = default;
                info = default;
                return false;
            }

            int voxelIdx = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);
            block = (slots + (int)SectorSlotId.Block)
                ->Get<Block>(bid, voxelIdx, UnsafeUtility.SizeOf<Block>());
            info = (slots + (int)SectorSlotId.PhysicsInfo)
                ->Get<PhysicsInfo>(bid, voxelIdx, UnsafeUtility.SizeOf<PhysicsInfo>());
            return true;
        }

        // ---- Per-brick aux access --------------------------------------------------------------
        // Aux is companion storage on a per-voxel slot. It is always addressed on the live `slots`
        // table (never the snapshot backbuffer) since it is rebuilt from settled voxel data.

        /// <summary>
        /// Ensures the per-voxel storage for <paramref name="slotId"/> exists and can hold every
        /// currently allocated brick, optionally attaching a derived aux buffer of
        /// <paramref name="extraPerBrickBytes"/> bytes per brick. Uses <c>sizeof(T)</c> as the voxel
        /// stride. Safe to call repeatedly; only allocates or grows when needed. Intended for bulk
        /// slot producers (e.g. physics-info generation) that write directly into slot storage.
        /// </summary>
        public void EnsureSlotAllocated<T>(SectorSlotId slotId, int extraPerBrickBytes = 0) where T : unmanaged
        {
            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated)
            {
                slots[(int)slotId] = SectorSlotStorage.New(
                    UnsafeUtility.SizeOf<T>(), brickMap.Capacity, _allocator,
                    NativeArrayOptions.ClearMemory, extraPerBrickBytes);
            }
            else
            {
                slot->EnsureBrickCapacity(brickMap.Capacity);
                if (extraPerBrickBytes > 0 && !slot->HasAux)
                {
                    slot->AttachAux(extraPerBrickBytes, brickMap.Capacity, _allocator);
                }
            }
        }

        /// <summary>
        /// Ensures the derived aux buffer for an already-created voxel slot exists and can hold every
        /// currently allocated brick. No-op (or throws under collections checks) if the voxel slot
        /// itself has not been created — aux summarizes voxel data, so that must come first.
        /// </summary>
        public void EnsureAuxAllocated(SectorSlotId slotId, int extraPerBrickBytes)
        {
            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException(
                    "Cannot attach aux to a slot whose voxel data is not created; create the slot first.");
#else
                return;
#endif
            }

            slot->EnsureBrickCapacity(brickMap.Capacity);
            if (!slot->HasAux)
            {
                slot->AttachAux(extraPerBrickBytes, brickMap.Capacity, _allocator);
            }
        }

        /// <summary>
        /// Reads a value from a brick's aux slice at <paramref name="offsetInBytes"/>. Returns
        /// default when the brick is empty or the slot carries no aux.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetBrickAux<T>(SectorSlotId slotId, int bX, int bY, int bZ, int offsetInBytes)
            where T : unmanaged
        {
            int brickIdx = ToBrickIdx(bX, bY, bZ);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->HasAux) { return default; }

            return slot->GetAux<T>(bid * slot->extraPerBrickBytes + offsetInBytes);
        }

        /// <summary>
        /// Raw pointer to a brick's aux slice, or null when the slot carries no aux. Intended for
        /// bulk rebuild producers that fill a brick's whole slice at once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickAuxPtr(SectorSlotId slotId, short bid)
        {
            if (bid == BRICKID_EMPTY) { return null; }
            SectorSlotStorage* slot = slots + (int)slotId;
            return slot->HasAux ? slot->GetBrickAuxPtr(bid) : null;
        }

        /// <summary>
        /// Writes a value into a brick's aux slice at <paramref name="offsetInBytes"/>. The aux
        /// buffer must already exist (see <see cref="EnsureAuxAllocated"/>); unlike the voxel setters
        /// this never lazily allocates and does NOT mark the brick dirty.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Under collections checks, thrown when the brick is not allocated or the slot has no aux.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBrickAux<T>(SectorSlotId slotId, int bX, int bY, int bZ, int offsetInBytes, T value)
            where T : unmanaged
        {
            int brickIdx = ToBrickIdx(bX, bY, bZ);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Cannot set brick aux for non-allocated bricks");
#else
                return;
#endif
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->HasAux)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException(
                    "Aux buffer not allocated for this slot; call EnsureAuxAllocated first.");
#else
                return;
#endif
            }

            slot->SetAux<T>(bid * slot->extraPerBrickBytes + offsetInBytes, value);
        }

        // ---- Per-voxel writes ------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block block)
            => SetVoxelSlot(SectorSlotId.Block, x, y, z, block);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z, T value)
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

                // Re-check under the gate; another thread may have created this brick already.
                if (targetBrickMap.indices[brickIdx] == BRICKID_EMPTY)
                {
                    targetBrickMap.AddBrick(new int3(bx, by, bz), out int newId, out bool exceedsCapacity);

                    // Alloc or reuse new brick
                    if (exceedsCapacity)
                        ExtendCreatedSlots(targetSlots, targetBrickMap.Capacity);
                    else
                        ClearBrickForAllSlots(targetSlots, (short)newId); // TODO: Do we really need to clear it?

                    // Record the addition for Block slot
                    if (slotId == SectorSlotId.Block)
                    {
                        MarkBrickDirty(brickIdx, DirtyFlags.BlockBrickAdded, 0);
                    }
                }

                // Pick up the brick id whether we or another thread created it.
                bid = targetBrickMap.indices[brickIdx];

                // Release the lock
                ReleaseSpinGate(ref _sectorAllocLock);
            }

            // Brick exists, corresponding slot may not be allocated yet

            SectorSlotStorage* slot = targetSlots + (int)slotId;
            int voxelIdx = ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK);

            // Check for same value
            // If slot !IsCreated, Get will return default
            T previous = slot->Get<T>(bid, voxelIdx, UnsafeUtility.SizeOf<T>());
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

            slot->Set(bid, voxelIdx, UnsafeUtility.SizeOf<T>(), value);
        }
    }
}
