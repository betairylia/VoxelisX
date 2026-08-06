using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// A value selected by a slot's per-brick <see cref="BrickBitmask"/>, together with its block
    /// position inside the sector.
    /// </summary>
    public struct SectorBitmaskSlotIterator<T> where T : unmanaged
    {
        public T value;
        public int3 position;
    }

    /// <summary>
    /// Enumerates the values selected by a sector slot's per-brick bitmap aux buffer.
    /// </summary>
    /// <remarks>
    /// The constructor resolves its slot id to typed data and bitmap base pointers;
    /// the slot id is not retained or consulted by <see cref="MoveNext"/>. Consequently every
    /// specialization has the same hot loop: skip zero words, select the next bit with
    /// <c>math.tzcnt</c>, and clear it with <c>bits &amp;= bits - 1</c>.
    ///
    /// The selected slot must contain <typeparamref name="T"/> values and its aux must be a
    /// refreshed <see cref="BrickBitmask"/>. There is deliberately no data-scan fallback.
    /// </remarks>
    [BurstCompile]
    public unsafe struct SectorBitmaskSlotEnumerator<T> where T : unmanaged
    {
        private SectorNonEmptyBrickEnumerator allocatedBricks;
        private readonly T* slotData;
        private readonly ulong* bitmapData;
        private T* currentBrick;
        private ulong* currentBitmap;
        private ulong remainingBits;
        private int nextWordIndex;
        private int currentWordBase;
        private int3 brickBlockOrigin;
        private SectorBitmaskSlotIterator<T> current;

        /// <summary>
        /// Constructs an enumerator over <paramref name="slotId"/>'s bitmap-selected values.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorBitmaskSlotEnumerator(in Sector sector, SectorSlotId slotId)
        {
            int slotIndex = (int)slotId;
            bool validSlotId = (uint)slotIndex < Sector.MAX_SLOTS;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!validSlotId)
            {
                throw new System.ArgumentOutOfRangeException(nameof(slotId));
            }
#endif
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(validSlotId);

            SectorSlotStorage* slot = sector.slots + slotIndex;
            bool hasAllocatedBricks = sector.NonEmptyBrickCount != 0;
            bool hasRequiredBitmap = hasAllocatedBricks
                ? slot->IsCreated && slot->HasAux &&
                  slot->stride == sizeof(T) &&
                  slot->extraPerBrickBytes == BrickBitmask.Bytes
                : !slot->IsCreated || slot->stride == sizeof(T);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!hasRequiredBitmap)
            {
                throw new System.InvalidOperationException(
                    "SectorBitmaskSlotEnumerator requires a matching slot with a refreshed bitmap aux buffer.");
            }
#endif
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(hasRequiredBitmap);

            allocatedBricks = sector.EnumerateNonEmptyBricks();
            slotData = slot->IsCreated ? (T*)slot->data.Ptr : null;
            bitmapData = slot->HasAux ? (ulong*)slot->aux.Ptr : null;
            currentBrick = null;
            currentBitmap = null;
            remainingBits = 0;
            nextWordIndex = BrickBitmask.Words;
            currentWordBase = 0;
            brickBlockOrigin = default;
            current = default;
        }

        /// <summary>Advances to the next set bitmap bit.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (true)
            {
                while (remainingBits == 0)
                {
                    if (nextWordIndex >= BrickBitmask.Words)
                    {
                        if (!MoveToNextBrick()) { return false; }
                    }

                    int wordIndex = nextWordIndex++;
                    currentWordBase = wordIndex << 6;
                    remainingBits = currentBitmap[wordIndex];
                }

                int bitInWord = math.tzcnt(remainingBits);
                remainingBits &= remainingBits - 1ul;

                int voxelIndex = currentWordBase + bitInWord;
                current = new SectorBitmaskSlotIterator<T>
                {
                    value = currentBrick[voxelIndex],
                    position = brickBlockOrigin + new int3(
                        bitInWord & Sector.BRICK_MASK,
                        bitInWord >> Sector.SHIFT_IN_BLOCKS,
                        currentWordBase >> 6)
                };

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveToNextBrick()
        {
            if (!allocatedBricks.MoveNext()) { return false; }

            SectorNonEmptyBrickEnumerator.BrickRef brickRef = allocatedBricks.Current;
            currentBrick = slotData + brickRef.Bid * Sector.BLOCKS_IN_BRICK;
            currentBitmap = bitmapData + brickRef.Bid * BrickBitmask.Words;
            remainingBits = 0;
            nextWordIndex = 0;

            int brickAbs = brickRef.BrickAbs;
            brickBlockOrigin = new int3(
                brickAbs & Sector.SECTOR_MASK,
                (brickAbs >> Sector.SHIFT_IN_BRICKS) & Sector.SECTOR_MASK,
                brickAbs >> (Sector.SHIFT_IN_BRICKS << 1)) * Sector.SIZE_IN_BLOCKS;
            return true;
        }

        /// <summary>Resets the enumerator to its initial state before the first element.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            allocatedBricks.Reset();
            currentBrick = null;
            currentBitmap = null;
            remainingBits = 0;
            nextWordIndex = BrickBitmask.Words;
            currentWordBase = 0;
            brickBlockOrigin = default;
            current = default;
        }

        public SectorBitmaskSlotIterator<T> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorBitmaskSlotEnumerator<T> GetEnumerator() => this;
    }

    public unsafe partial struct Sector
    {
        /// <summary>Enumerates values selected by <paramref name="slotId"/>'s bitmap aux.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorBitmaskSlotEnumerator<T> EnumerateBitmaskSlot<T>(SectorSlotId slotId)
            where T : unmanaged
            => new SectorBitmaskSlotEnumerator<T>(this, slotId);

        /// <summary>
        /// Enumerates non-empty Block values selected by the refreshed Block occupancy mask.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorBitmaskSlotEnumerator<Block> EnumerateNonEmptyBlocks()
            => EnumerateBitmaskSlot<Block>(SectorSlotId.Block);

        /// <summary>
        /// Enumerates Corner/Edge PhysicsInfo values selected by the refreshed physics-key mask.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorBitmaskSlotEnumerator<PhysicsInfo> EnumeratePhysicsKeyBlocks()
            => EnumerateBitmaskSlot<PhysicsInfo>(SectorSlotId.PhysicsInfo);
    }
}
