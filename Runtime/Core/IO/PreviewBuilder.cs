using Unity.Burst;

namespace Voxelis.IO
{
    /// <summary>
    /// Builds a per-brick preview blob (<c>uint[BRICKS_IN_SECTOR]</c>) from a sector's voxel data.
    /// The preview encodes occupancy + emission masks over a 2x2x2 sub-brick partition and an
    /// RGB565 average color, packed per <see cref="BrickPreview"/>.
    ///
    /// The per-voxel scan is Burst-compiled (Direct Call from managed code).
    /// </summary>
    [BurstCompile]
    public static class PreviewBuilder
    {
        [BurstCompile]
        public static unsafe void Build(Sector* sector, uint* dst)
        {
            short* indices = sector->brickMap.indices;
            for (int brickAbsIdx = 0; brickAbsIdx < Sector.BRICKS_IN_SECTOR; brickAbsIdx++)
            {
                short bid = indices[brickAbsIdx];
                if (bid == SparseBrickIdTable.EMPTY)
                {
                    dst[brickAbsIdx] = 0u;
                    continue;
                }

                Block* brick = sector->GetBrick(bid);
                uint occupancy = 0, emission = 0;
                ulong sumR = 0, sumG = 0, sumB = 0;
                int count = 0;

                for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                {
                    Block block = brick[Sector.ToBlockIdx(x, y, z)];
                    if (block.isEmpty) continue;

                    int subIdx = BrickPreview.SubBrickIndex(
                        x >> BrickPreview.SubBrickShift,
                        y >> BrickPreview.SubBrickShift,
                        z >> BrickPreview.SubBrickShift);
                    occupancy |= 1u << subIdx;

                    ushort id = block.id;
                    if ((id & 0x1) != 0)
                    {
                        emission |= 1u << subIdx;
                    }

                    sumR += (uint)((id >> 11) & 0x1F);
                    sumG += (uint)((id >> 6) & 0x1F);
                    sumB += (uint)((id >> 1) & 0x1F);
                    count++;
                }

                ushort rgb565;
                if (count > 0)
                {
                    int r5 = (int)(sumR / (ulong)count);
                    int g5 = (int)(sumG / (ulong)count);
                    int b5 = (int)(sumB / (ulong)count);
                    rgb565 = BrickPreview.ToRgb565(r5, g5, b5);
                }
                else
                {
                    rgb565 = 0;
                }

                dst[brickAbsIdx] = BrickPreview.Pack(occupancy, emission, rgb565);
            }
        }

        public static unsafe void Build(Sector* sector, uint[] dst)
        {
            fixed (uint* p = dst) Build(sector, p);
        }
    }
}
