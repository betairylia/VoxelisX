using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis
{
    // Enumerates thru all non-empty blocks
    [BurstCompile]
    public struct SectorEnumerator : IEnumerator<BlockIterator>
    {
        private Sector sector;

        private int bX, bY, bZ, brick_acce_id;
        private int x, y, z;
        private int3 blockPosition;

        private int sectorBrickIndex, sectorBlockIndex;

        public SectorEnumerator(Sector sector)
        {
            this.sector = sector;
            
            bX = -1;
            bY = 0;
            bZ = 0;
            x = 7;
            y = 7;
            z = 7;

            brick_acce_id = -1;

            sectorBlockIndex = 0;
            sectorBrickIndex = 0;
            blockPosition = new int3(-1, -1, -1);
        }
        
        public bool MoveNext()
        {
            // Find next non-empty block
            do
            {
                // Move to next block
                x++;
                if (x >= Sector.SIZE_IN_BLOCKS_X) { x = 0; y++; }
                if (y >= Sector.SIZE_IN_BLOCKS_Y) { y = 0; z++; }
                
                // Move to new brick
                if (z >= Sector.SIZE_IN_BLOCKS_Z)
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
                    
                    bX = absolute_bid & ((1 << Sector.SHIFT_IN_BRICKS_X) - 1);
                    bY = (absolute_bid >> Sector.SHIFT_IN_BRICKS_X) & ((1 << Sector.SHIFT_IN_BRICKS_Y) - 1);
                    bZ = (absolute_bid >> (Sector.SHIFT_IN_BRICKS_X + Sector.SHIFT_IN_BRICKS_Y)) & ((1 << Sector.SHIFT_IN_BRICKS_Z) - 1);
                }

                sectorBlockIndex = (sectorBrickIndex
                                   << (Sector.SHIFT_IN_BLOCKS_X + Sector.SHIFT_IN_BLOCKS_Y + Sector.SHIFT_IN_BLOCKS_Z))
                                   + Sector.ToBlockIdx(x, y, z);
            }while(sector.voxels[sectorBlockIndex].isEmpty);

            blockPosition = new int3(
                (bX << Sector.SHIFT_IN_BLOCKS_X) + x,
                (bY << Sector.SHIFT_IN_BLOCKS_Y) + y,
                (bZ << Sector.SHIFT_IN_BLOCKS_Z) + z
            );
            
            return true;
        }

        public void Reset()
        {
            bX = -1;
            bY = 0;
            bZ = 0;
            x = 7;
            y = 7;
            z = 7;

            sectorBlockIndex = 0;
            sectorBrickIndex = 0;
        }

        public BlockIterator Current => new() { block = sector.voxels[sectorBlockIndex], position = blockPosition };

        object IEnumerator.Current => Current;

        public void Dispose()
        {
            // Nothing to dispose really
        }

        public SectorEnumerator GetEnumerator()
        {
            return this;
        }
    }
}