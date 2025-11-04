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
    /// <summary>
    /// Manages rendering of a single voxel sector, including GPU buffer management
    /// and ray tracing acceleration structure updates.
    /// </summary>
    /// <remarks>
    /// SectorRenderer is responsible for:
    /// - Converting voxel data to GPU-friendly formats
    /// - Managing AABB and brick data buffers for ray tracing
    /// - Scheduling Burst-compiled jobs to prepare render data
    /// - Updating the ray tracing acceleration structure when geometry changes
    /// </remarks>
    public class SectorRenderer : IDisposable
    {
        /// <summary>
        /// Axis-aligned bounding box structure for ray tracing.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        struct AABB
        {
            internal Vector3 min;
            internal Vector3 max;
        }

        /// <summary>
        /// Reference structure for a potential future optimization using 1-bit-per-block encoding.
        /// Currently not used.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        struct ZeroOnePassBrick
        {
            internal ulong brickInfo; // 12bit brickPosNum, 8bit semi-brick mask, 44bit reserved
            // internal int d0, d1, d2, d3, d4, d5, d6, d7, d8, d9, d10, d11, d12, d13, d14, d15;
            internal ulong ul0, ul1, ul2, ul3, ul4, ul5, ul6, ul7;
        }

        /// <summary>
        /// Data length for 1-bit-per-block encoding (not currently used).
        /// 2 ints for brick position + 16 ints for block occupancy (512 blocks / 32 bits).
        /// </summary>
        public static readonly int BRICK_DATA_LENGTH_01PASS =
            2           // 64 bit for brick position
            + 512 / 32; // 1 bit per block

        /// <summary>
        /// Data length for current 16-bit-per-block encoding.
        /// 1 int for brick position + 256 ints for block data (512 blocks / 2 blocks per int).
        /// </summary>
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
            public Sector sector;

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

            /// <summary>
            /// Consumes the next dirty brick from the update queue.
            /// </summary>
            /// <param name="index">Index parameter (currently unused).</param>
            /// <returns>Update info for the dirty brick, or null if no more dirty bricks.</returns>
            public BrickUpdateInfo? ConsumeDirtyBrick(int index)
            {
                if (sector.updateRecord.IsEmpty()) return null;
                
                short absolute_bid = sector.updateRecord.Dequeue();
                
                var result = new BrickUpdateInfo()
                {
                    brickIdx = sector.brickIdx[absolute_bid],
                    brickIdxAbsolute = absolute_bid,
                    type = sector.brickFlags[absolute_bid]
                };

                sector.brickFlags[absolute_bid] = BrickUpdateInfo.Type.Idle;
                
                return result;
            }
            
            public void Execute()
            {
                syncRecord[0] = 65536;
                syncRecord[1] = 0;

                int id = 0;
                
                BrickUpdateInfo? record = ConsumeDirtyBrick(id);
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
                    for (int bz = 0; bz < Sector.SIZE_IN_BLOCKS; bz++)
                    {
                        for (int by = 0; by < Sector.SIZE_IN_BLOCKS; by++)
                        {
                            // Assume X-First
                            int blockStart = bid * Sector.BLOCKS_IN_BRICK +
                                             Sector.ToBlockIdx(0, by, bz);

                            for (int bx = 0; bx < Sector.SIZE_IN_BLOCKS; bx += 2)
                            {
                                brickData[
                                        bp + 1 + bz * (Sector.SIZE_IN_BLOCKS_SQUARED / 2) +
                                        by * (Sector.SIZE_IN_BLOCKS / 2) + bx / 2] =
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
                            min = brickPos * Sector.SIZE_IN_BLOCKS,
                            max = (brickPos + Vector3.one) * Sector.SIZE_IN_BLOCKS
                        };
                    }
                    
                    // Go to next brick
                    id++;
                    record = ConsumeDirtyBrick(id);
                }
            }
        }
        
        // [BurstCompile]
        // struct GenerateSector01PassRenderDataJob : IJob
        // {
        //     // ......
        //
        //             // Brick data
        //             for (int bz = 0; bz < Sector.SIZE_IN_BLOCKS; bz++)
        //             {
        //                 for (int byhalf = 0; byhalf < 2; byhalf++)
        //                 {
        //                     int val = 0;
        //                     for (int by = 0; by < Sector.SIZE_IN_BLOCKS / 2; by++)
        //                     {
        //                         // Assume X-First
        //                         int blockStart = bid * Sector.BLOCKS_IN_BRICK + Sector.ToBlockIdx(0, by + byhalf * Sector.SIZE_IN_BLOCKS / 2, bz);
        //
        //                         for (int bx = 0; bx < Sector.SIZE_IN_BLOCKS; bx++)
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

        /// <summary>
        /// Shared material used for rendering all sectors.
        /// Set globally by VoxelisXRenderer on initialization.
        /// </summary>
        public static Material sectorMaterial;

        private SectorRef sectorRef;
        private bool hasRenderable = false;
        private int sectorASHandle;
        private MaterialPropertyBlock matProps = null;

        /// <summary>
        /// Constructs a new sector renderer for the specified sector reference.
        /// </summary>
        /// <param name="sectorRef">The sector to render.</param>
        public SectorRenderer(SectorRef sectorRef)
        {
            this.sectorRef = sectorRef;
        }

        private NativeList<AABB> hostAABBBuffer;
        private NativeList<int> hostBrickBuffer;

        /// <summary>
        /// Gets the estimated host memory usage in bytes for this renderer's buffers.
        /// </summary>
        public ulong MemoryUsage =>
            (ulong)(hostBrickBuffer.IsCreated
                ? (hostBrickBuffer.Capacity * sizeof(int))
                : 0 + (hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0));

        /// <summary>
        /// Gets the estimated VRAM usage in bytes for this renderer's GPU buffers.
        /// </summary>
        public ulong VRAMUsage =>
            (ulong)(Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * 24 +
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
        
        /// <summary>
        /// Prepares and resizes GPU buffers if needed to accommodate current brick count.
        /// </summary>
        /// <returns>True if buffers were reallocated; false if existing buffers are sufficient.</returns>
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
                    Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);
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

        /// <summary>
        /// Completes the render job and uploads data to GPU buffers.
        /// Must be called after RenderEmitJob() in the same frame.
        /// </summary>
        /// <remarks>
        /// This method waits for the Burst job to complete, then uploads modified brick data
        /// and AABBs to the GPU. Only the dirty range of data is uploaded to minimize bandwidth.
        /// </remarks>
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

        /// <summary>
        /// Updates the ray tracing acceleration structure with this sector's geometry.
        /// </summary>
        /// <param name="AS">The acceleration structure to update.</param>
        /// <remarks>
        /// If the sector is dirty (geometry changed), removes and re-adds the instance to the RTAS.
        /// If just the transform changed, updates the instance transform only.
        /// This method should be called after Render() to synchronize the RTAS with current voxel data.
        /// </remarks>
        public void RenderModifyAS(ref RayTracingAccelerationStructure AS)
        {
            if (isDirty)
            {
                AS.RemoveInstance(sectorASHandle);
                sectorASHandle = AS.AddInstance(AABBconfig,
                   sectorRef.entity.transform.localToWorldMatrix * Matrix4x4.Translate(sectorRef.sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS));
                hasRenderable = true;
                AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
            }
            else if(hasRenderable)
            {
                AS.UpdateInstanceTransform(sectorASHandle,
                    sectorRef.entity.transform.localToWorldMatrix * Matrix4x4.Translate(sectorRef.sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS));
            }

            isDirty = false;
        }

        /// <summary>
        /// Schedules a Burst job to generate render data from dirty voxel data.
        /// Must be followed by Render() to complete the job and upload to GPU.
        /// </summary>
        /// <remarks>
        /// Skips scheduling if:
        /// - Sector is marked for removal
        /// - Sector is empty
        /// - No dirty bricks need updating
        /// This allows the job to run in parallel with other sector jobs.
        /// </remarks>
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

        /// <summary>
        /// Marks this sector for removal from the acceleration structure.
        /// </summary>
        public void MarkRemove()
        {
            shouldRemove = true;
        }

        /// <summary>
        /// Disposes all resources used by this renderer, including host and GPU buffers.
        /// </summary>
        public void Dispose()
        {
            if (hostAABBBuffer.IsCreated) hostAABBBuffer.Dispose();
            if (hostBrickBuffer.IsCreated) hostBrickBuffer.Dispose();
            
            aabbBuffer?.Dispose();
            brickBuffer?.Dispose();
        }
    }
}