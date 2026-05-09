using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Voxelis.Utils;

namespace Voxelis.Rendering
{
    public partial class SectorRenderer
    {
        [BurstCompile]
        struct InitializeAABBJob : IJob
        {
            public NativeList<AABB> aabbBuffer;

            public void Execute()
            {
                for (int i = 0; i < aabbBuffer.Length; i++)
                {
                    aabbBuffer[i] = new AABB()
                    {
                        min = new Vector3(0, 0, 0),
                        max = new Vector3(-1, -1, -1)
                    };
                }
            }
        }

        /// <summary>
        /// Burst-compiled job that generates GPU-ready render data from sector voxel data.
        /// Processes dirty bricks and updates AABB and brick data buffers.
        /// </summary>
        [BurstCompile]
        struct GenerateSectorRenderDataJob : IJob
        {
            /// <summary>
            /// The sector to generate render data from.
            /// </summary>
            public SectorHandle sectorHandle;

            public SectorNeighborHandles neighbors;
#if !VOXELISX_RENDER_DISABLE_CULLING
            public SparseBrickIdTable rendererBrickMap;
            private SectorNeighborhoodReaderHelper helper;
#endif

            /// <summary>
            /// Buffer of all AABB bounding boxes used for RayTracingAccelerationStructure.
            /// </summary>
            public NativeList<AABB> aabbBuffer;

            /// <summary>
            /// Buffer containing packed brick data for ray tracing shaders.
            /// </summary>
            public NativeList<int> brickData;

            /// <summary>
            /// Array recording the range of modified bricks: [minModified, maxModified].
            /// Used to optimize GPU buffer uploads by only sending changed data.
            /// </summary>
            public NativeArray<int> syncRecord;

            public void Execute()
            {
                syncRecord[0] = 65536;
                syncRecord[1] = 0;

                ref Sector sector = ref sectorHandle.Get();

#if !VOXELISX_RENDER_DISABLE_CULLING
                helper = new SectorNeighborhoodReaderHelper(sectorHandle, neighbors);
#endif

                // Sweep all bricks to find dirty ones
                unsafe
                {
                    for (int brickIdxAbs = 0; brickIdxAbs < Sector.BRICKS_IN_SECTOR; brickIdxAbs++)
                    {
                        // Check require-update flags populated by dirty propagation.
                        bool isAdded = (sector.brickRequireUpdateFlags[brickIdxAbs] & (ushort)DirtyFlags.BrickAdded) !=
                                       0;
                        bool isRemoved =
                            (sector.brickRequireUpdateFlags[brickIdxAbs] & (ushort)DirtyFlags.BrickRemoved) !=
                            0;
                        bool needRebuilt = (sector.brickRequireUpdateFlags[brickIdxAbs] &
                                            (ushort)DirtyFlags.GeometryWithLocalNeighbor) != 0;

                        if (!isAdded && !isRemoved && !needRebuilt) continue;
                        if (isRemoved)
                            throw new System.NotImplementedException();

                        // Check if brick exists (not empty)
                        short bid = sector.brickIdx[brickIdxAbs];
                        if (bid == Sector.BRICKID_EMPTY) continue;

                        // Create record for this brick
                        var record = new BrickUpdateInfo()
                        {
                            brickIdx = bid,
                            brickIdxAbsolute = (short)brickIdxAbs,
                            type = isAdded ? BrickUpdateInfo.Type.Added : BrickUpdateInfo.Type.Modified
                        };

                        ProcessBrick(record);
                    }
                }
            }

            private void ProcessBrick(BrickUpdateInfo record)
            {
                // Buffer start position
                short bid = record.brickIdx;
                int3 brickPos = Sector.ToBrickPos(record.brickIdxAbsolute);
                int3 brickBlockPos = brickPos * Sector.SIZE_IN_BLOCKS;
                ref Sector sector = ref sectorHandle.Get();
                
                /////////////////////
                // int bp = bid * BRICK_DATA_LENGTH;

                // Record modifications for Host-Device buffer sync
                // syncRecord[0] = math.min(syncRecord[0], bid);
                // syncRecord[1] = math.max(syncRecord[1], bid);
                
                // for (int i = 0; i < BRICK_OCCUPANCY_WORDS; i++)
                // {
                //     brickData[bp + BRICK_INFO_WORDS + i] = 0;
                // }
                /////////////////////

                uint coarseOccupancy = 0;

                // Brick data
                for (int bz = 0; bz < Sector.SIZE_IN_BLOCKS; bz++)
                {
                    for (int by = 0; by < Sector.SIZE_IN_BLOCKS; by++)
                    {
                        // Assume X-First
                        int blockStart = bid * Sector.BLOCKS_IN_BRICK +
                                         Sector.ToBlockIdx(0, by, bz);

                        for (int bx = 0; bx < Sector.SIZE_IN_BLOCKS; bx += 2)
                        {
                            Block block0 = sector.voxels[blockStart + bx];
                            Block block1 = sector.voxels[blockStart + bx + 1];
                            
#if !VOXELISX_RENDER_DISABLE_CULLING
                            uint block0Data = GetRendererBlockData(block0, brickBlockPos + new int3(bx, by, bz));
                            uint block1Data = GetRendererBlockData(block1, brickBlockPos + new int3(bx, by, bz));
#else
                            uint block0Data = ((uint)block0.id << 16);
                            uint block1Data = ((uint)block1.id << 16);
#endif
                            
                            brickData[
                                    bp + BRICK_BLOCK_DATA_OFFSET + bz * (Sector.SIZE_IN_BLOCKS_SQUARED / 2) +
                                    by * (Sector.SIZE_IN_BLOCKS / 2) + bx / 2] =
                                unchecked((int)(((uint)block0.id << 16) | block1.id));

                            if (!block0.isRendererEmpty)
                            {
                                AccumulateOccupancy(ref coarseOccupancy, bp, bx, by, bz);
                            }

                            if (!block1.isRendererEmpty)
                            {
                                AccumulateOccupancy(ref coarseOccupancy, bp, bx + 1, by, bz);
                            }
                        }
                    }
                }

                brickData[bp] = PackBrickInfo(record.brickIdxAbsolute, coarseOccupancy);

                // AABB
                // Only do for new bricks
                if (record.type == BrickUpdateInfo.Type.Added)
                {
                    Vector3 brickPosf3 = brickPos.ToVector3Int();
                    aabbBuffer[bid] = new AABB()
                    {
                        min = brickPosf3 * Sector.SIZE_IN_BLOCKS,
                        max = (brickPosf3 + Vector3.one) * Sector.SIZE_IN_BLOCKS
                    };
                }
            }

            private ushort GetRendererBlockData(Block currentBlock, int3 sectorBlockPos)
            {
                bool alive = false;

#if !VOXELISX_RENDER_DISABLE_TRANSPARENCY
                bool isOpaque = currentBlock.isOpaque;
                uint transparentId = currentBlock.transparentId;
                uint faceMask = 0;
#endif
                
                for (int ni = 0; ni < 6; ni++)
                {
                    int3 nd = NeighborhoodSettings.Directions[ni];
                    Block neighbor = helper.GetBlock(sectorBlockPos + nd);
                    
#if !VOXELISX_RENDER_DISABLE_TRANSPARENCY
                    // Opaque face is always non-visible
                    alive |= (!neighbor.isOpaque);
                    
                    // For transparent faces, visible only adjacent to different transparent blocks
                    if(alive && (!isOpaque))
                    {
                        uint neighborTransparentId = neighbor.transparentId;
                        if (neighborTransparentId != transparentId)
                        {
                            alive = true;
                            faceMask |= (1u << ni);
                        }
                    }

                    if (alive && isOpaque) break;
#else
                    alive |= (!neighbor.isRendererEmpty);
                    if (alive) break;
#endif
                }

                if (!alive)
                {
                    return Block.Empty.id;
                }

                ushort result = 0;
#if !VOXELISX_RENDER_DISABLE_TRANSPARENCY
                if (!isOpaque)
                {
                    result = (ushort)Block.MaskTransparency(faceMask, transparentId);
                }
                else
                {
                    result = currentBlock.id;
                }
#else
                result = currentBlock.id;
#endif

                return result;
            }

            private void AccumulateOccupancy(ref uint coarseOccupancy, int bp, int bx, int by, int bz)
            {
                int coarseBit = ToCoarseOccupancyBit(bx, by, bz);
                int microBit = ToMicroOccupancyBit(bx, by, bz);
                int wordOffset = ToOccupancyWordOffset(coarseBit, microBit);
                uint wordBit = 1u << (microBit & 31);

                coarseOccupancy |= 1u << coarseBit;
                brickData[bp + wordOffset] = unchecked((int)(uint)brickData[bp + wordOffset] | (int)wordBit);
            }
        }
    }
}