using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

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
    public struct SectorEnumerator : IEnumerator<BlockIterator>
    {
        private Sector sector;

        private int bX, bY, bZ, brick_acce_id;
        private int x, y, z;
        private int3 blockPosition;

        private int sectorBrickIndex, sectorBlockIndex;

        /// <summary>
        /// Constructs a new sector enumerator for the specified sector.
        /// </summary>
        /// <param name="sector">The sector to enumerate.</param>
        public SectorEnumerator(Sector sector)
        {
            this.sector = sector;

            bX = -1;
            bY = 0;
            bZ = 0;
            x = Sector.BRICK_MASK;
            y = Sector.BRICK_MASK;
            z = Sector.BRICK_MASK;

            brick_acce_id = -1;

            sectorBlockIndex = 0;
            sectorBrickIndex = 0;
            blockPosition = new int3(-1, -1, -1);
        }

        /// <summary>
        /// Advances the enumerator to the next non-empty block.
        /// </summary>
        /// <returns>True if a non-empty block was found; false if enumeration is complete.</returns>
        public bool MoveNext()
        {
            // Find next non-empty block
            do
            {
                // Move to next block
                x++;
                if (x >= Sector.SIZE_IN_BLOCKS) { x = 0; y++; }
                if (y >= Sector.SIZE_IN_BLOCKS) { y = 0; z++; }

                // Move to new brick
                if (z >= Sector.SIZE_IN_BLOCKS)
                {
                    z = 0;

                    // Find next non-empty brick
                    short absolute_bid = Sector.BRICKID_EMPTY;
                    do
                    {
                        brick_acce_id++;
                        if (brick_acce_id >= sector.NonEmptyBrickCount) return false;
                        absolute_bid = sector.GetNonEmptyBricks()[brick_acce_id];
                        sectorBrickIndex = sector.brickIdx[absolute_bid];
                    }while(sectorBrickIndex == Sector.BRICKID_EMPTY);

                    bX = absolute_bid & Sector.SECTOR_MASK;
                    bY = (absolute_bid >> Sector.SHIFT_IN_BRICKS) & Sector.SECTOR_MASK;
                    bZ = (absolute_bid >> (Sector.SHIFT_IN_BRICKS << 1)) & Sector.SECTOR_MASK;
                }

                sectorBlockIndex = (sectorBrickIndex << (Sector.SHIFT_IN_BLOCKS * 3))
                                   + Sector.ToBlockIdx(x, y, z);
            }while(sector.voxels[sectorBlockIndex].isEmpty);

            blockPosition = new int3(
                (bX << Sector.SHIFT_IN_BLOCKS) + x,
                (bY << Sector.SHIFT_IN_BLOCKS) + y,
                (bZ << Sector.SHIFT_IN_BLOCKS) + z
            );
            
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
            x = Sector.BRICK_MASK;
            y = Sector.BRICK_MASK;
            z = Sector.BRICK_MASK;

            sectorBlockIndex = 0;
            sectorBrickIndex = 0;
        }

        /// <summary>
        /// Gets the current block and its position in the enumeration.
        /// </summary>
        public BlockIterator Current => new() { block = sector.voxels[sectorBlockIndex], position = blockPosition };

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
        public SectorEnumerator GetEnumerator()
        {
            return this;
        }
    }
}