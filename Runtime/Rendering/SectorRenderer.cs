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
using Voxelis.Utils;
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
    public partial class SectorRenderer : IDisposable
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

        public const int BRICK_INFO_WORDS = 1;
        public const int BRICK_OCCUPANCY_WORDS = 16;
        public const int BRICK_BLOCK_DATA_OFFSET = BRICK_INFO_WORDS + BRICK_OCCUPANCY_WORDS;
        public const int BRICK_BLOCK_DATA_WORDS = Sector.BLOCKS_IN_BRICK / 2;
        public const int BRICK_DATA_LENGTH = BRICK_BLOCK_DATA_OFFSET + BRICK_BLOCK_DATA_WORDS;

        public static int ToCoarseOccupancyBit(int bx, int by, int bz)
        {
            return (bx >> 2) | ((by >> 2) << 1) | ((bz >> 2) << 2);
        }

        public static int ToMicroOccupancyBit(int bx, int by, int bz)
        {
            return (bx & 3) | ((by & 3) << 2) | ((bz & 3) << 4);
        }

        public static int ToOccupancyWordOffset(int coarseBit, int microBit)
        {
            return BRICK_INFO_WORDS + coarseBit * 2 + (microBit >> 5);
        }

        public static int PackBrickInfo(int brickIdxAbsolute, uint coarseOccupancy)
        {
            return unchecked((int)(((uint)brickIdxAbsolute & 0xFFFu) | ((coarseOccupancy & 0xFFu) << 16)));
        }
        
        /// <summary>
        /// Shared material used for rendering all sectors.
        /// Set globally by VoxelisXRenderer on initialization.
        /// </summary>
        public static Material sectorMaterial;

        private bool hasRenderable = false;
        private int sectorASHandle;
        private MaterialPropertyBlock matProps = null;

        /// <summary>
        /// Constructs a new sector renderer.
        /// </summary>
        public SectorRenderer()
        {
        }

        private NativeList<AABB> hostAABBBuffer;
        private NativeList<int> hostBrickBuffer;
#if !VOXELISX_RENDER_DISABLE_CULLING
        private SparseBrickIdTable rendererBrickMap;
#endif

        public int BrickBufferSize
        {
            get
            {
#if VOXELISX_RENDER_DISABLE_CULLING
                return hostAABBBuffer.Length;
#else
                return rendererBrickMap.Capacity;
#endif
            }
        }

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
                    currentGPUBrickBufferCapacity * BRICK_DATA_LENGTH * 4);

        private GraphicsBuffer aabbBuffer;
        private GraphicsBuffer brickBuffer;
        private int currentGPUBrickBufferCapacity;

        private bool GPUBufferInitialized => brickBuffer != null && brickBuffer.IsValid();
        private bool HostBufferInitialized => hostAABBBuffer.IsCreated;

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
        /// Preallocate host buffers for the renderer data-filling job.
        /// </summary>
        /// <param name="sector">The sector to prepare buffers for.</param>
        public void PreallocateBuffers(Sector sector)
        {
            if (sector.NonEmptyBrickCount == 0) return;

            int requestedCapacity = 0;
#if VOXELISX_RENDER_DISABLE_CULLING
            requestedCapacity = sector.RendererNonEmptyBrickCount;
#endif
            
            // Prepare Host Buffers
            if (!HostBufferInitialized)
            {
                hostAABBBuffer = new NativeList<AABB>(requestedCapacity, Allocator.Persistent);
                hostBrickBuffer = new NativeList<int>(requestedCapacity * BRICK_DATA_LENGTH,
                    Allocator.Persistent);
#if !VOXELISX_RENDER_DISABLE_CULLING
                rendererBrickMap = SparseBrickIdTable.New(Allocator.Persistent);
#endif
            }
            
            // Pre-allocate buffers only for non-culling case
            // Since we already know how many bricks will be there before running the actual data-filling job
#if VOXELISX_RENDER_DISABLE_CULLING
            hostAABBBuffer.Resize(requestedCapacity, NativeArrayOptions.ClearMemory);
            hostBrickBuffer.Resize(requestedCapacity * BRICK_DATA_LENGTH, NativeArrayOptions.ClearMemory);
#endif
        }
        
        /// <summary>
        /// Prepares and resizes GPU buffers if needed to accommodate current brick count.
        /// </summary>
        /// <returns>True if buffers were reallocated; false if existing buffers are sufficient.</returns>
        public bool ExtendGPUBuffers()
        {
            Profiler.BeginSample("ExtendGPUBuffers");

            int requestedCapacity = GetCapacity(BrickBufferSize);
            if (requestedCapacity == currentGPUBrickBufferCapacity)
            {
                Profiler.EndSample();
                return false;
            }
            
            if (!GPUBufferInitialized)
            {
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);
            }
            
            // Allocate our buffers on GPU side

            var new_brickBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 
                    requestedCapacity * BRICK_DATA_LENGTH, 4);
            
            brickBuffer?.Dispose();

            brickBuffer = new_brickBuffer;
            currentGPUBrickBufferCapacity = requestedCapacity;
            
            Profiler.EndSample();

            return true;
        }
        
        // Temp variables used in Render process
        // Please ensure call RenderEmitJob and Render (after emit job) in the same frame / tick
        private JobHandle jobHandle;
        private GenerateSectorRenderDataJob rendererJob;
        private int previousBrickBufferSize;
        private bool isRealloc, jobScheduled;
        private RayTracingAABBsInstanceConfig AABBconfig;
        private bool isDirty;
        private bool shouldRemove = false;
        private Matrix4x4 previousObjectToWorld;
        private bool hasPreviousObjectToWorld;

        private static uint HashUInt(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        private static uint ComputeSectorHashSeed(VoxelEntity entity, int3 sectorPos)
        {
            uint seed = (uint)entity.GetInstanceID();
            seed ^= (uint)sectorPos.x * 0x9E3779B9u;
            seed ^= (uint)sectorPos.y * 0x85EBCA6Bu;
            seed ^= (uint)sectorPos.z * 0xC2B2AE35u;
            return HashUInt(seed);
        }

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
            bool shouldUpdateAABB = rendererJob.syncRecord[2] > 0;
            rendererJob.syncRecord.Dispose();

            isRealloc = ExtendGPUBuffers();

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
                
                // Will be set by RenderModifyAS when we have sector info
                AABBconfig = default;
            
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
        /// <param name="entity">The voxel entity this sector belongs to.</param>
        /// <param name="sectorPos">The position of this sector in sector coordinates.</param>
        /// <param name="sector">The sector data.</param>
        /// <remarks>
        /// If the sector is dirty (geometry changed), removes and re-adds the instance to the RTAS.
        /// If just the transform changed, updates the instance transform only.
        /// This method should be called after Render() to synchronize the RTAS with current voxel data.
        /// </remarks>
        /// TODO: Optimize me for static entities for better performance
        public void RenderModifyAS(ref RayTracingAccelerationStructure AS, VoxelEntity entity, int3 sectorPos, Sector sector)
        {
            Matrix4x4 objectToWorld =
                entity.transform.localToWorldMatrix *
                Matrix4x4.Translate((sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS).ToVector3Int());
            Matrix4x4 prevObjectToWorld = hasPreviousObjectToWorld ? previousObjectToWorld : objectToWorld;
            bool updatesRenderableInstance = isDirty || hasRenderable;

            if (updatesRenderableInstance)
            {
                if (matProps == null)
                {
                    matProps = new MaterialPropertyBlock();
                    matProps.SetInt("_SectorHashSeed", unchecked((int)ComputeSectorHashSeed(entity, sectorPos)));
                }

                if (brickBuffer != null && brickBuffer.IsValid())
                {
                    matProps.SetBuffer("g_bricks", brickBuffer);
                }

                // Previous transform is delivered through the per-instance property block for now.
                // This matches the sector-instance RTAS layout, but it means moving sectors need a
                // property-block update even when voxel geometry is unchanged. If that gets expensive,
                // move these matrices to a structured buffer keyed by a stable instance/sector id.
                matProps.SetMatrix("_PrevObjectToWorld", prevObjectToWorld);
            }

            if (isDirty)
            {
                // Create AABB config here now that we have sector info
                if (AABBconfig.aabbCount == 0 && matProps != null)
                {
                    AABBconfig = new RayTracingAABBsInstanceConfig(
                        aabbBuffer, BrickBufferSize, false, sectorMaterial);
                    AABBconfig.accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace;
                    AABBconfig.materialProperties = matProps;
                }

                AS.RemoveInstance(sectorASHandle);
                sectorASHandle = AS.AddInstance(AABBconfig, objectToWorld);
                hasRenderable = true;
                AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
            }
            else if(hasRenderable)
            {
                AS.UpdateInstanceTransform(sectorASHandle, objectToWorld);
                AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
            }

            if (updatesRenderableInstance)
            {
                previousObjectToWorld = objectToWorld;
                hasPreviousObjectToWorld = true;
            }
            isDirty = false;
        }

        /// <summary>
        /// Schedules a Burst job to generate render data from dirty voxel data.
        /// Must be followed by Render() to complete the job and upload to GPU.
        /// </summary>
        /// <param name="sector">The sector to render.</param>
        /// <remarks>
        /// Skips scheduling if:
        /// - Sector is marked for removal
        /// - Sector is empty
        /// - No dirty bricks need updating
        /// This allows the job to run in parallel with other sector jobs.
        /// </remarks>
        public void RenderEmitJob(SectorHandle sector, SectorNeighborHandles neighborHandle)
        {
            if (shouldRemove || sector.IsRendererEmpty || (!sector.IsRendererRequireUpdate))
            {
                return;
            }

            PreallocateBuffers(sector.Get());

            // Job generating renderer buffers
            rendererJob = new GenerateSectorRenderDataJob()
            {
                sectorHandle = sector,
                neighbors = neighborHandle,
#if !VOXELISX_RENDER_DISABLE_CULLING
                rendererBrickMap = rendererBrickMap,
#endif
                aabbBuffer = hostAABBBuffer,
                brickData = hostBrickBuffer,
                syncRecord = new NativeArray<int>(3, Allocator.TempJob)
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
#if !VOXELISX_RENDER_DISABLE_CULLING
            if (rendererBrickMap.IsCreated) rendererBrickMap.Dispose();
#endif
            
            aabbBuffer?.Dispose();
            brickBuffer?.Dispose();
        }
    }
}
