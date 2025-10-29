using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace Voxelis.Rendering
{
    public class SectorRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AABB
        {
            internal Vector3 min;
            internal Vector3 max;
        }

        // Only for reference
        [StructLayout(LayoutKind.Sequential)]
        struct ZeroOnePassBrick
        {
            internal ulong brickInfo; // 12bit brickPosNum, 8bit semi-brick mask, 44bit reserved
            // internal int d0, d1, d2, d3, d4, d5, d6, d7, d8, d9, d10, d11, d12, d13, d14, d15;
            internal ulong ul0, ul1, ul2, ul3, ul4, ul5, ul6, ul7;
        }

        public static readonly int BRICK_DATA_LENGTH_01PASS = 
            2           // 64 bit for brick position
            + 512 / 32; // 1 bit per block
        
        public static readonly int BRICK_DATA_LENGTH = 
            1           // 32 bit for brick position
            + 512 / 2;  // 16 bit per block

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
        
        [BurstCompile]
        struct GenerateSectorRenderDataJob : IJob
        {
            public Sector sector;
            
            // Buffer of all AABB bounding boxes used for RayTracingAccelerationStructure
            public NativeList<AABB> aabbBuffer;
            
            // Buffers for RayTracing Shaders
            public NativeList<int> brickData;

            // [minModified, maxModified]
            public NativeArray<int> syncRecord;

            public BrickUpdateInfo? ProcessDirtyBrick(int index)
            {
                if (index >= sector.updateRecord.Length) return null;
                
                short absolute_bid = sector.updateRecord[index];
                
                var result = new BrickUpdateInfo()
                {
                    brickIdx = sector.brickIdx[absolute_bid],
                    brickIdxAbsolute = absolute_bid,
                    type = sector.brickFlags[absolute_bid]
                };
                
                return result;
            }
            
            public void Execute()
            {
                syncRecord[0] = 65536;
                syncRecord[1] = 0;

                int id = 0;
                
                BrickUpdateInfo? record = ProcessDirtyBrick(id);
                while (record != null)
                {
                    if (record.Value.type == BrickUpdateInfo.Type.Idle) continue;
                    if (record.Value.type == BrickUpdateInfo.Type.Removed) throw new System.NotImplementedException();
                    
                    // Buffer start position
                    short bid = record.Value.brickIdx;
                    int bp = bid * BRICK_DATA_LENGTH;
                    
                    // Record modifications for Host-Device buffer sync
                    syncRecord[0] = math.min(syncRecord[0], bid);
                    syncRecord[1] = math.max(syncRecord[1], bid);

                    // Brick position
                    brickData[bp] = record.Value.brickIdxAbsolute;

                    // Brick data
                    for (int bz = 0; bz < Sector.SIZE_IN_BLOCKS_Z; bz++)
                    {
                        for (int by = 0; by < Sector.SIZE_IN_BLOCKS_Y; by++)
                        {
                            // Assume X-First
                            int blockStart = bid * Sector.BLOCKS_IN_BRICK +
                                             Sector.ToBlockIdx(0, by, bz);

                            for (int bx = 0; bx < Sector.SIZE_IN_BLOCKS_X; bx += 2)
                            {
                                brickData[
                                        bp + 1 + bz * (Sector.SIZE_IN_BLOCKS_Y * Sector.SIZE_IN_BLOCKS_X / 2) +
                                        by * (Sector.SIZE_IN_BLOCKS_X / 2) + bx / 2] =
                                    ((sector.voxels[blockStart + bx].id << 16) | sector.voxels[blockStart + bx + 1].id);
                                    // ~(0x00010001);
                            }
                        }
                    }

                    // AABB
                    // Only do for new bricks
                    if (record.Value.type == BrickUpdateInfo.Type.Added)
                    {
                        Vector3 brickPos = Sector.ToBrickPos(record.Value.brickIdxAbsolute);
                        aabbBuffer[bid] = new AABB()
                        {
                            min = brickPos * 8,
                            max = (brickPos + Vector3.one) * 8
                        };
                    }
                    
                    // Go to next brick
                    id++;
                    record = ProcessDirtyBrick(id);
                }
            }
        }
        
        // [BurstCompile]
        // struct GenerateSector01PassRenderDataJob : IJob
        // {
        //     // ......
        //
        //             // Brick data
        //             for (int bz = 0; bz < Sector.SIZE_IN_BLOCKS_Z; bz++)
        //             {
        //                 for (int byhalf = 0; byhalf < 2; byhalf++)
        //                 {
        //                     int val = 0;
        //                     for (int by = 0; by < Sector.SIZE_IN_BLOCKS_Y / 2; by++)
        //                     {
        //                         // Assume X-First
        //                         int blockStart = bid * Sector.BLOCKS_IN_BRICK + Sector.ToBlockIdx(0, by + byhalf * Sector.SIZE_IN_BLOCKS_Y / 2, bz);
        //
        //                         for (int bx = 0; bx < Sector.SIZE_IN_BLOCKS_X; bx++)
        //                         {
        //                             val |= ((sector.voxels[blockStart + bx].isEmpty ? 0 : 1) << (bx + by * 8));
        //                         }
        //                     }
        //                     brickData[bp + 1 + (byhalf + bz * 2)] = val;
        //                 }
        //             }
        //             
        //     // ......
        // }

        public static Material sectorMaterial;

        private SectorRef sectorRef;
        private bool hasRenderable = false;
        private int sectorASHandle;
        private MaterialPropertyBlock matProps = null;

        public SectorRenderer(SectorRef sectorRef)
        {
            this.sectorRef = sectorRef;
        }

        private NativeList<AABB> hostAABBBuffer;
        private NativeList<int> hostBrickBuffer;

        public ulong MemoryUsage =>
            (ulong)(hostBrickBuffer.IsCreated
                ? (hostBrickBuffer.Capacity * sizeof(int))
                : 0 + (hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0));

        public ulong VRAMUsage =>
            (ulong)(Sector.SIZE_IN_BRICKS_X * Sector.SIZE_IN_BRICKS_Y * Sector.SIZE_IN_BRICKS_Z * 24 +
                    currentCapacity * BRICK_DATA_LENGTH * 4);

        private GraphicsBuffer aabbBuffer;
        private GraphicsBuffer brickBuffer;
        private int currentCapacity;

        private bool BufferInitialized => brickBuffer != null && brickBuffer.IsValid();

        private int GetCapacity(int requestedLength)
        {
            int result = 1;
            while (result < requestedLength)
            {
                result <<= 1;
            }
            
            // int result = requestedLength / 16 * 16 + 16;

            return result;
        }
        
        public bool PrepareBuffers()
        {
            Profiler.BeginSample("PrepareBuffers");

            if (sectorRef.sector.RendererNonEmptyBrickCount != previousAABBCount)
            {
                shouldUpdateAABB = true;
            }

            previousAABBCount = sectorRef.sector.RendererNonEmptyBrickCount;
            
            int requestedCapacity = GetCapacity(sectorRef.sector.RendererNonEmptyBrickCount);
            if (requestedCapacity == currentCapacity)
            {
                Profiler.EndSample();
                return false;
            }
            
            if (!BufferInitialized)
            {
                hostAABBBuffer = new NativeList<AABB>(requestedCapacity, Allocator.Persistent);
                hostBrickBuffer = new NativeList<int>(requestedCapacity * BRICK_DATA_LENGTH,
                    Allocator.Persistent);
                
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Sector.SIZE_IN_BRICKS_X * Sector.SIZE_IN_BRICKS_Y * Sector.SIZE_IN_BRICKS_Z, 24);
            }
            
            hostAABBBuffer.Resize(requestedCapacity, NativeArrayOptions.ClearMemory);
            hostBrickBuffer.Resize(requestedCapacity * BRICK_DATA_LENGTH, NativeArrayOptions.ClearMemory);
            
            // Allocate our buffers on GPU side

            var new_brickBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 
                    requestedCapacity * BRICK_DATA_LENGTH, 4);
            
            brickBuffer?.Dispose();

            brickBuffer = new_brickBuffer;
            currentCapacity = requestedCapacity;
            
            Profiler.EndSample();

            return true;
        }
        
        // Temp variables used in Render process
        // Please ensure call RenderEmitJob and Render (after emit job) in the same frame / tick
        private JobHandle jobHandle;
        private GenerateSectorRenderDataJob rendererJob;
        private int previousAABBCount;
        private bool isRealloc, jobScheduled, shouldUpdateAABB;
        private RayTracingAABBsInstanceConfig AABBconfig;
        private bool isDirty;
        private bool shouldRemove = false;

        public void Render()
        {
            if (!jobScheduled)
            {
                return;
            }
            
            // Let the job complete and copy buffers
            jobHandle.Complete();
            jobScheduled = false;
            
            int minModified = rendererJob.syncRecord[0];
            int maxModified = rendererJob.syncRecord[1];
            rendererJob.syncRecord.Dispose();

            if (minModified <= maxModified)
            {
                // Profiler.BeginSample($"UploadData ({maxModified - minModified + 1} Bricks)");
                Profiler.BeginSample("UploadData");
                // Partially update buffers
                // aabbBuffer.SetData(hostAABBBuffer.AsArray(), minModified, minModified, maxModified - minModified + 1);
                aabbBuffer.SetData(hostAABBBuffer.AsArray());

                if (isRealloc)
                {
                    brickBuffer.SetData(hostBrickBuffer.AsArray());
                }
                else
                {
                    brickBuffer.SetData(hostBrickBuffer.AsArray(), minModified * BRICK_DATA_LENGTH, minModified * BRICK_DATA_LENGTH,
                        (maxModified - minModified + 1) * BRICK_DATA_LENGTH);
                }
                Profiler.EndSample();
            }
            
            // isRealloc = true;
            if (shouldUpdateAABB || isRealloc)
            {
                Profiler.BeginSample("Handle Realloc");
                // If we have not yet created an instance in AS
                if (matProps == null)
                {
                    matProps = new MaterialPropertyBlock();
                }
                
                AABBconfig = new RayTracingAABBsInstanceConfig(
                    aabbBuffer, sectorRef.sector.RendererNonEmptyBrickCount, false, sectorMaterial);
                    // aabbBuffer, 4096, false, sectorMaterial);
                AABBconfig.accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace;
                AABBconfig.materialProperties = matProps;
            
                // AS.RemoveInstance(sectorASHandle);
                // Debug.Log(config.aabbCount);
                
                // Setup AABB instance config
                matProps.SetBuffer("g_bricks", brickBuffer);
                isDirty = true;
                
                Profiler.EndSample();
            }

            isRealloc = false;
        }

        public void RemoveMe(ref RayTracingAccelerationStructure AS)
        {
            if (shouldRemove)
            {
                AS.RemoveInstance(sectorASHandle);
                // Debug.Log("Removed sector");
                isDirty = false;
                Dispose();
            }
        }

        public void RenderModifyAS(ref RayTracingAccelerationStructure AS)
        {
            if (isDirty)
            {
                AS.RemoveInstance(sectorASHandle);
                sectorASHandle = AS.AddInstance(AABBconfig,
                   sectorRef.entity.transform.localToWorldMatrix * Matrix4x4.Translate(sectorRef.sectorPos * 128));
                hasRenderable = true;
                AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
            }
            else if(hasRenderable)
            {
                AS.UpdateInstanceTransform(sectorASHandle,
                    sectorRef.entity.transform.localToWorldMatrix * Matrix4x4.Translate(sectorRef.sectorPos * 128));
            }

            isDirty = false;
        }

        public void RenderEmitJob()
        {
            if (shouldRemove || sectorRef.sector.IsRendererEmpty || (!sectorRef.sector.IsRendererDirty))
            {
                return;
            }

            isRealloc = PrepareBuffers();
            
            // Job generating renderer buffers
            rendererJob = new GenerateSectorRenderDataJob()
            {
                sector = sectorRef.sector,
                aabbBuffer = hostAABBBuffer,
                brickData = hostBrickBuffer,
                syncRecord = new NativeArray<int>(2, Allocator.TempJob)
            };
            jobHandle = rendererJob.Schedule();

            jobScheduled = true;
        }

        public void MarkRemove()
        {
            shouldRemove = true;
        }

        public void Dispose()
        {
            hostAABBBuffer.Dispose();
            hostBrickBuffer.Dispose();
            
            aabbBuffer?.Dispose();
            brickBuffer?.Dispose();
        }
    }
}