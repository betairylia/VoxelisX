using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Mathematics;

namespace Voxelis
{
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
    
    /// <summary>One dirty brick of a sector: its absolute index and the matched dirty flags.</summary>
    public struct DirtyBrickInfo
    {
        public short BrickIdx;
        public DirtyFlags Flags;
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
