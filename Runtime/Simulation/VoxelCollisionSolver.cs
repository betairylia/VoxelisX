using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Serialization;

namespace Voxelis.Simulation
{
    public class VoxelCollisionSolver
    {
        const float EPSILON = 1e-5f;

        public struct ContactPoint
        {
            public int3 srcBlock;
            public int3 dstBlock;
            public float3 position;
            public float3 normal;
            public float depth;

            public ContactPoint Invert()
            {
                return new ContactPoint
                {
                    srcBlock = srcBlock, dstBlock = dstBlock,
                    position = position, normal = -normal,
                    depth = depth
                };
            }

            public ContactPoint ApplySectorPos(int3 srcSector, int3 dstSector)
            {
                return new ContactPoint
                {
                    srcBlock = srcBlock + srcSector, dstBlock = dstBlock + dstSector,
                    position = position, normal = normal,
                    depth = depth
                };
            }
            
            public ContactPoint Flip()
            {
                return new ContactPoint
                {
                    srcBlock = dstBlock, dstBlock = srcBlock,
                    position = position, normal = normal,
                    depth = depth
                };
            }
            
            public ContactPoint TranslateVia(float4x4 M)
            {
                float3 newPosition = math.mul(M, new float4(position, 1.0f)).xyz;
                float3 newNormal = math.mul(M, new float4(normal, 0.0f)).xyz;

                newNormal = math.normalize(newNormal);

                return new ContactPoint
                {
                    srcBlock = srcBlock, dstBlock = dstBlock,
                    position = newPosition, normal = newNormal,
                    depth = depth
                };
            }
        }
        
        [BurstCompile]
        public struct SectorJob : IJob
        {
            // Block coordinates
            public float4x4 srcToDst;
            public Sector src, dst;
            
            // Result
            public NativeList<ContactPoint> dstSpaceResults;
            
            public void Execute()
            {
                float3 dstOrigin = math.mul(srcToDst, new float4(0, 0, 0, 1)).xyz;
                float3 dstX = math.mul(srcToDst, new float4(1, 0, 0, 1)).xyz - dstOrigin;
                float3 dstY = math.mul(srcToDst, new float4(0, 1, 0, 1)).xyz - dstOrigin;
                float3 dstZ = math.mul(srcToDst, new float4(0, 0, 1, 1)).xyz - dstOrigin;
                
                // TODO: Store contact info
                int contacts = 0;

                // For all non-empty bricks in source
                // For now, simply test them one-by-one
                foreach (BlockIterator blockIter in new SectorNonEmptyBlockEnumerator(src))
                {
                    float3 srcBlockCenter = new float3(blockIter.position) + 0.5f;
                    float3 dstBlockCenter = math.mul(srcToDst, new float4(
                        srcBlockCenter,
                        1.0f)).xyz;
                    float3 dstBlockOrigin = dstBlockCenter - 0.5f + EPSILON;
                    
                    // Debug.Log(dstBlockCenter);
                    // continue;

                    int3 dstBlock2x2x2Origin = new int3(dstBlockOrigin);
                    int3 exact =
                        new int3((dstBlockOrigin - dstBlock2x2x2Origin) < (2 * EPSILON));

                    for (int dx = 0; dx < 2 - exact.x; dx++)
                    {
                        for (int dy = 0; dy < 2 - exact.y; dy++)
                        {
                            for (int dz = 0; dz < 2 - exact.z; dz++)
                            {
                                int3 destination = new int3(
                                    dstBlock2x2x2Origin.x + dx,
                                    dstBlock2x2x2Origin.y + dy,
                                    dstBlock2x2x2Origin.z + dz
                                );

                                // Check out-of-bounds
                                if (math.any(destination < 0) ||
                                    math.any(destination >= new int3(
                                        Sector.SECTOR_SIZE_IN_BLOCKS,
                                        Sector.SECTOR_SIZE_IN_BLOCKS,
                                        Sector.SECTOR_SIZE_IN_BLOCKS
                                    )))
                                {
                                    continue;
                                }

                                var dstBlock = dst.GetBlock(
                                    destination.x, destination.y, destination.z
                                );

                                if (dstBlock.isEmpty)
                                {
                                    continue;
                                }

                                float3 dstTargetCenter = (float3)destination + 0.5f;
                                
                                // Basic sphere-ish collision
                                float dsq = math.lengthsq(dstBlockCenter - dstTargetCenter);
                                if (dsq <= 100)
                                {
                                    dstSpaceResults.Add(new ContactPoint
                                    {
                                        srcBlock = blockIter.position,
                                        dstBlock = destination,
                                        position = (dstBlockCenter + dstTargetCenter) / 2.0f,
                                        normal = math.normalize(dstTargetCenter - dstBlockCenter),
                                        depth = math.sqrt(1 - dsq)
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}