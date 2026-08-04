using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// Enumerates the blocks selected by the Block slot's per-brick occupancy mask.
    /// </summary>
    /// <remarks>
    /// <see cref="VoxelEntityData.RefreshNonEmptyMask"/> must have refreshed the Block slot aux
    /// after voxel writes settle. Each non-zero 64-bit word is consumed with <c>math.tzcnt</c>
    /// and the lowest-set-bit clear idiom, so empty voxels are never read or tested here. There is
    /// deliberately no voxel-scan fallback: a missing or stale mask violates the tick contract.
    /// </remarks>
    [BurstCompile]
    public unsafe struct SectorNonEmptyBlockEnumerator
    {
        private SectorNonEmptyBrickEnumerator nonEmptyBricks;
        private readonly Block* blockData;
        private readonly ulong* occupancyData;
        private Block* currentBrick;
        private ulong* currentMask;
        private ulong remainingBits;
        private int nextWordIndex;
        private int currentWordBase;
        private int3 brickBlockOrigin;
        private BlockIterator current;

        /// <summary>
        /// Constructs a new sector enumerator for the specified sector.
        /// </summary>
        /// <param name="sector">The sector to enumerate.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBlockEnumerator(in Sector sector)
        {
            SectorSlotStorage* blockSlot = sector.slots + (int)SectorSlotId.Block;
            bool hasRequiredMask = sector.NonEmptyBrickCount == 0 ||
                                   (blockSlot->IsCreated && blockSlot->HasAux &&
                                    blockSlot->stride == sizeof(Block) &&
                                    blockSlot->extraPerBrickBytes == BrickBitmask.Bytes);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!hasRequiredMask)
            {
                throw new System.InvalidOperationException(
                    "SectorNonEmptyBlockEnumerator requires a refreshed Block occupancy mask.");
            }
#endif
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(hasRequiredMask);

            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
            blockData = blockSlot->IsCreated ? (Block*)blockSlot->data.Ptr : null;
            occupancyData = blockSlot->HasAux ? (ulong*)blockSlot->aux.Ptr : null;
            currentBrick = null;
            currentMask = null;
            remainingBits = 0;
            nextWordIndex = BrickBitmask.Words;
            currentWordBase = 0;
            brickBlockOrigin = default;
            current = default;
        }

        /// <summary>
        /// Advances to the next set occupancy bit.
        /// </summary>
        /// <returns>True if a non-empty block was found; false if enumeration is complete.</returns>
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
                    remainingBits = currentMask[wordIndex];
                }

                int bitInWord = math.tzcnt(remainingBits);
                remainingBits &= remainingBits - 1ul;

                int voxelIndex = currentWordBase + bitInWord;
                Block block = currentBrick[voxelIndex];
                Utils.BurstAssertSimpleExperssionsOnly.IsTrue(!block.isEmpty);

                current = new BlockIterator
                {
                    block = block,
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
            if (!nonEmptyBricks.MoveNext()) { return false; }

            SectorNonEmptyBrickEnumerator.BrickRef brickRef = nonEmptyBricks.Current;
            currentBrick = blockData + brickRef.Bid * Sector.BLOCKS_IN_BRICK;
            currentMask = occupancyData + brickRef.Bid * BrickBitmask.Words;
            remainingBits = 0;
            nextWordIndex = 0;

            int brickAbs = brickRef.BrickAbs;
            brickBlockOrigin = new int3(
                brickAbs & Sector.SECTOR_MASK,
                (brickAbs >> Sector.SHIFT_IN_BRICKS) & Sector.SECTOR_MASK,
                brickAbs >> (Sector.SHIFT_IN_BRICKS << 1)) * Sector.SIZE_IN_BLOCKS;
            return true;
        }

        /// <summary>
        /// Resets the enumerator to its initial state before the first element.
        /// </summary>
        public void Reset()
        {
            nonEmptyBricks.Reset();
            currentBrick = null;
            currentMask = null;
            remainingBits = 0;
            nextWordIndex = BrickBitmask.Words;
            currentWordBase = 0;
            brickBlockOrigin = default;
            current = default;
        }

        /// <summary>
        /// Gets the current block and its position in the enumeration.
        /// </summary>
        public BlockIterator Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        /// <summary>
        /// Returns this enumerator (enables foreach usage).
        /// </summary>
        /// <returns>This enumerator instance.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBlockEnumerator GetEnumerator() => this;
    }
    
    /// <summary>
    /// Enumerates through all non-empty blocks within a sector.
    /// Skips empty bricks entirely for efficiency.
    /// </summary>
    /// <remarks>
    /// This enumerator is Burst-compiled for performance. It iterates only through
    /// allocated bricks and only through non-empty blocks within those bricks,
    /// making it much more efficient than iterating through all possible block positions.
    /// </remarks>
    [BurstCompile]
    public struct SectorRequiredUpdateBrickEnumerator : IEnumerator<BrickIterator>
    {
        private Sector sector;
        private DirtyFlags flagMask;
        private bool includeEmpty;

        private int bX, bY, bZ;
        private SectorNonEmptyBrickEnumerator nonEmptyBricks;

        // Compact (Relative) brick index
        private short sectorBrickIndex;

        /// <summary>
        /// Constructs a new sector enumerator for the specified sector.
        /// </summary>
        /// <param name="sector">The sector to enumerate.</param>
        public SectorRequiredUpdateBrickEnumerator(Sector sector, DirtyFlags mask, bool includeEmpty)
        {
            this.sector = sector;

            bX = -1;
            bY = 0;
            bZ = 0;

            sectorBrickIndex = 0;
            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
            
            flagMask = mask;
            this.includeEmpty = includeEmpty;
        }

        /// <summary>
        /// Advances the enumerator to the next non-empty block.
        /// </summary>
        /// <returns>True if a non-empty block was found; false if enumeration is complete.</returns>
        public unsafe bool MoveNext()
        {
            // Skip clean sectors
            if((sector.sectorRequireUpdateFlags & (ushort)flagMask) == 0)
                return false;
            
            // Empty positions cannot match this branch, so delegate the allocated-brick traversal
            // to the shared enumerator and only filter its results by require-update flags here.
            if (!includeEmpty)
            {
                while (nonEmptyBricks.MoveNext())
                {
                    SectorNonEmptyBrickEnumerator.BrickRef brickRef = nonEmptyBricks.Current;
                    if ((sector.brickRequireUpdateFlags[brickRef.BrickAbs] & (ushort)flagMask) == 0)
                    {
                        continue;
                    }

                    sectorBrickIndex = brickRef.Bid;
                    int3 bPos = Sector.ToBrickPos((short)brickRef.BrickAbs);
                    bX = bPos.x;
                    bY = bPos.y;
                    bZ = bPos.z;
                    return true;
                }

                return false;
            }

            // We need to take care of empty spaces, use plain sweep instead.
            short absoluteBid;
            do
            {
                // Move to next brick
                bX++;
                if (bX >= Sector.SIZE_IN_BRICKS) { bX = 0; bY++; }
                if (bY >= Sector.SIZE_IN_BRICKS) { bY = 0; bZ++; }
                if (bZ >= Sector.SIZE_IN_BRICKS) { return false; }

                absoluteBid = (short)Sector.ToBrickIdx(bX, bY, bZ);
            } while ((sector.brickRequireUpdateFlags[absoluteBid] & (ushort)flagMask) == 0);

            return true;
        }

        /// <summary>
        /// Resets the enumerator to its initial state before the first element.
        /// </summary>
        public void Reset()
        {
            bX = -1;
            bY = 0;
            bZ = 0;
            sectorBrickIndex = 0;
            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
        }

        /// <summary>
        /// Gets the current brick and its position in the enumeration.
        /// </summary>
        public BrickIterator Current => new()
        {
            brickIdx = sectorBrickIndex,
            position = new int3(bX, bY, bZ) * Sector.SIZE_IN_BLOCKS,
        };

        /// <summary>
        /// Gets the current element (non-generic version).
        /// </summary>
        object IEnumerator.Current => Current;

        /// <summary>
        /// Disposes the enumerator. This enumerator has no unmanaged resources to release.
        /// </summary>
        public void Dispose()
        {
            // Nothing to dispose really
        }

        /// <summary>
        /// Returns this enumerator (enables foreach usage).
        /// </summary>
        /// <returns>This enumerator instance.</returns>
        public SectorRequiredUpdateBrickEnumerator GetEnumerator()
        {
            return this;
        }
    }
    
    // TODO: FIXME: Check
    // TODO: Merge this with above?
    [BurstCompile]
    public unsafe struct SectorDirtyBrickEnumerator
    {
        private Sector sector;
        private DirtyFlags mask;
        private SectorNonEmptyBrickEnumerator nonEmptyBricks;
        private DirtyBrickInfo current;

        public SectorDirtyBrickEnumerator(Sector sector, DirtyFlags mask)
        {
            this.sector = sector;
            this.mask = mask;
            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
            current = default;
        }

        public SectorDirtyBrickEnumerator GetEnumerator() => this;
        public DirtyBrickInfo Current => current;

        public bool MoveNext()
        {
            if ((sector.sectorDirtyFlags & (ushort)mask) == 0)
            {
                return false;
            }

            while (nonEmptyBricks.MoveNext())
            {
                int brickIdx = nonEmptyBricks.Current.BrickAbs;
                ushort flags = (ushort)(sector.brickDirtyFlags[brickIdx] & (ushort)mask);
                if (flags != 0)
                {
                    current = new DirtyBrickInfo
                    {
                        BrickIdx = (short)brickIdx,
                        Flags = (DirtyFlags)flags
                    };
                    return true;
                }
            }

            return false;
        }
    }
}
