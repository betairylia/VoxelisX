// #define VOXELISX_RENDER_DISABLE_TRANSPARENCY

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
            /// Array recording the range of modified bricks: [minModified, maxModified, aabbModified (0/1)].
            /// Used to optimize GPU buffer uploads by only sending changed data.
            /// </summary>
            public NativeArray<int> syncRecord;

            public void Execute()
            {
                syncRecord[0] = 65536;
                syncRecord[1] = 0;
                syncRecord[2] = 0;

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
                        bool isAdded = (sector.brickRequireUpdateFlags[brickIdxAbs] & (ushort)DirtyFlags.BlockBrickAdded) !=
                                       0;
                        bool isRemoved =
                            (sector.brickRequireUpdateFlags[brickIdxAbs] & (ushort)DirtyFlags.BlockBrickRemoved) !=
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
                        // var record = new BrickUpdateInfo()
                        // {
                        //     brickIdx = bid,
                        //     brickIdxAbsolute = (short)brickIdxAbs,
                        //     type = isAdded ? BrickUpdateInfo.Type.Added : BrickUpdateInfo.Type.Modified
                        // };

                        ProcessBrick(bid, (short)brickIdxAbs, isAdded);
                    }
                }
            }

            private unsafe void ProcessBrick(short bid, short bidAbsolute, bool isAdded)
            {
                // Buffer start position
                int3 brickPos = Sector.ToBrickPos(bidAbsolute);
                int3 brickBlockPos = brickPos * Sector.SIZE_IN_BLOCKS;
                ref Sector sector = ref sectorHandle.Get();
                Block* brick = sector.GetBrick<Block>(SectorSlotId.Block, bid);
                if (brick == null)
                {
                    return;
                }

                int rendererBrickId = -1;
                int rendererBrickBase = -1;

                uint coarseOccupancy = 0;
                int3 occupiedMin = new int3(Sector.SIZE_IN_BLOCKS);
                int3 occupiedMax = new int3(-1);

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
                            Block block0 = brick[Sector.ToBlockIdx(bx, by, bz)];
                            Block block1 = brick[Sector.ToBlockIdx(bx + 1, by, bz)];
                            
                            int rendererBlockIdx = Sector.ToBlockIdx(bx, by, bz) / 2;
                            
#if !VOXELISX_RENDER_DISABLE_CULLING
                            uint block0Data = GetRendererBlockData(block0, brickBlockPos + new int3(bx, by, bz));
                            uint block1Data = GetRendererBlockData(block1, brickBlockPos + new int3(bx + 1, by, bz));
                            
                            // Do nothing if blocks are empty
                            if (Block.IsRendererDataEmpty(block0Data) && Block.IsRendererDataEmpty(block1Data))
                            {
                                if (rendererBrickBase >= 0)
                                {
                                    brickData[rendererBrickBase + BRICK_BLOCK_DATA_OFFSET + rendererBlockIdx] = 0;
                                }
                                continue;
                            }
#else
                            uint block0Data = ((uint)block0.id);
                            uint block1Data = ((uint)block1.id);
#endif
                            
                            // Brick not allocated yet
                            if (rendererBrickId == -1)
                            {
#if !VOXELISX_RENDER_DISABLE_CULLING
                                // Alloc the buffer
                                bool requireExtension = false;
                                isAdded = rendererBrickMap.AddBrick(brickPos, out rendererBrickId, out requireExtension);
                                if (requireExtension)
                                {
                                    aabbBuffer.Resize(rendererBrickMap.Capacity, NativeArrayOptions.UninitializedMemory);
                                    brickData.Resize(rendererBrickMap.Capacity * BRICK_DATA_LENGTH, NativeArrayOptions.UninitializedMemory);
                                }

                                rendererBrickBase = rendererBrickId * BRICK_DATA_LENGTH;
                                
                                // Clear previous blocks
                                for (int i = 0; i < rendererBlockIdx; i++)
                                {
                                    brickData[rendererBrickBase + BRICK_BLOCK_DATA_OFFSET + i] = 0;
                                }
#else
                                // Alloc the buffer
                                rendererBrickId = bid;
                                rendererBrickBase = rendererBrickId * BRICK_DATA_LENGTH;
#endif
                                // Record modifications for Host-Device buffer sync
                                syncRecord[0] = math.min(syncRecord[0], rendererBrickId);
                                syncRecord[1] = math.max(syncRecord[1], rendererBrickId);
                
                                // Reset coarse occupancy
                                for (int i = 0; i < BRICK_OCCUPANCY_WORDS; i++)
                                {
                                    brickData[rendererBrickBase + BRICK_INFO_WORDS + i] = 0;
                                }
                            }
                            
                            brickData[rendererBrickBase + BRICK_BLOCK_DATA_OFFSET + rendererBlockIdx] =
                                unchecked((int)((block0Data << 16) | block1Data));

                            if (!Block.IsRendererDataEmpty(block0Data))
                            {
                                AccumulateOccupancy(ref coarseOccupancy, rendererBrickBase, bx, by, bz);
                                occupiedMin = math.min(occupiedMin, new int3(bx, by, bz));
                                occupiedMax = math.max(occupiedMax, new int3(bx, by, bz));
                            }

                            if (!Block.IsRendererDataEmpty(block1Data))
                            {
                                AccumulateOccupancy(ref coarseOccupancy, rendererBrickBase, bx + 1, by, bz);
                                occupiedMin = math.min(occupiedMin, new int3(bx + 1, by, bz));
                                occupiedMax = math.max(occupiedMax, new int3(bx + 1, by, bz));
                            }
                        }
                    }
                }

                // Handle removal if brick empty
                // TODO: Compaction?
                if (coarseOccupancy == 0)
                {
#if !VOXELISX_RENDER_DISABLE_CULLING
                    int removed = rendererBrickMap.RemoveBrick(brickPos);
#else
                    int removed = SparseBrickIdTable.EMPTY;
#endif
                    if (removed != SparseBrickIdTable.EMPTY)
                    {
                        brickData[removed * BRICK_DATA_LENGTH] = PackBrickInfo(bidAbsolute, coarseOccupancy);
                        syncRecord[0] = math.min(syncRecord[0], removed);
                        syncRecord[1] = math.max(syncRecord[1], removed);

                        // A NaN min.x marks the AABB as an inactive primitive (DXR spec),
                        // so the freed slot drops out of the BLAS at the next build instead
                        // of leaving a stale full-brick box over dead data.
                        aabbBuffer[removed] = new AABB()
                        {
                            min = new Vector3(float.NaN, float.NaN, float.NaN),
                            max = new Vector3(float.NaN, float.NaN, float.NaN)
                        };
                        syncRecord[2] = 1;
                    }

                    // We are done
                    return;
                }

                brickData[rendererBrickBase] = PackBrickInfo(bidAbsolute, coarseOccupancy);
                brickData[rendererBrickBase + 1] = PackBrickTightBounds(occupiedMin, occupiedMax);

                // AABB tight to the occupied blocks, in sector-local block coordinates.
                // Rewritten on every rebuild since edits can grow or shrink the bounds;
                // syncRecord[2] (=> BLAS rebuild) is raised only when the box actually
                // changed, or for new bricks whose slot may hold garbage/NaN.
                AABB tightAABB = new AABB()
                {
                    min = new Vector3(brickBlockPos.x + occupiedMin.x,
                                      brickBlockPos.y + occupiedMin.y,
                                      brickBlockPos.z + occupiedMin.z),
                    max = new Vector3(brickBlockPos.x + occupiedMax.x + 1,
                                      brickBlockPos.y + occupiedMax.y + 1,
                                      brickBlockPos.z + occupiedMax.z + 1)
                };

                AABB previousAABB = aabbBuffer[rendererBrickId];
                bool boundsChanged = isAdded
                    || previousAABB.min.x != tightAABB.min.x
                    || previousAABB.min.y != tightAABB.min.y
                    || previousAABB.min.z != tightAABB.min.z
                    || previousAABB.max.x != tightAABB.max.x
                    || previousAABB.max.y != tightAABB.max.y
                    || previousAABB.max.z != tightAABB.max.z;

                if (boundsChanged)
                {
                    aabbBuffer[rendererBrickId] = tightAABB;            // tight aabb
                    // aabbBuffer[rendererBrickId] = new AABB()         // fixed aabb (8x8x8)
                    // {
                    //     min = brickBlockPos.ToVector3Int(),
                    //     max = (brickBlockPos + 8).ToVector3Int()
                    // };
                    syncRecord[2] = 1;
                }
            }

#if !VOXELISX_RENDER_DISABLE_CULLING
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
                    if((!neighbor.isOpaque) && (!isOpaque))
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
                    alive |= (neighbor.isRendererEmpty);
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
#endif

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
