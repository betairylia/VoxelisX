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
using Caelix.Utils;
using Random = UnityEngine.Random;

namespace Caelix.Rendering
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

        // Word 0: absolute brick index + coarse occupancy. Word 1: packed tight occupied
        // bounds (also keeps uint64 occupancy loads 8-byte aligned).
        public const int BRICK_INFO_WORDS = 2;
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
        /// Packs the brick-local occupied block bounds (both inclusive, 0..7 per axis)
        /// into brick info word 1. Layout: [minX:0-2][minY:3-5][minZ:6-8][maxX:9-11][maxY:12-14][maxZ:15-17].
        /// </summary>
        public static int PackBrickTightBounds(int3 occupiedMin, int3 occupiedMax)
        {
            return occupiedMin.x | (occupiedMin.y << 3) | (occupiedMin.z << 6)
                 | (occupiedMax.x << 9) | (occupiedMax.y << 12) | (occupiedMax.z << 15);
        }
        
        /// <summary>
        /// Shared material used for rendering all sectors.
        /// Set globally by CaelixRenderer on initialization.
        /// </summary>
        public static Material sectorMaterial;

        private bool hasRenderable = false;
        private bool instanceIsDynamic;
        private int sectorASHandle;
        private MaterialPropertyBlock matProps = null;
        private readonly int sectorHashSeed;

        /// <summary>
        /// Constructs a new sector renderer for one (entity, sector) pair.
        /// </summary>
        /// <remarks>
        /// The identity is captured here (rather than taken per call) so that the per-instance
        /// property block has a single creation point — see <see cref="EnsureMaterialProperties"/>.
        /// </remarks>
        public SectorRenderer(VoxelEntity entity, int3 sectorPos)
        {
            sectorHashSeed = unchecked((int)ComputeSectorHashSeed(entity, sectorPos));
        }

        /// <summary>
        /// Lazily creates this sector's per-instance property block.
        /// </summary>
        /// <remarks>
        /// Sole creation point: the block is filled from two places (<see cref="Render"/> binds
        /// g_bricks on buffer realloc, <see cref="RenderModifyAS"/> writes the previous transform),
        /// and constant-per-sector values like the face-hash seed must be set exactly once no
        /// matter which of them runs first.
        /// </remarks>
        private MaterialPropertyBlock EnsureMaterialProperties()
        {
            if (matProps == null)
            {
                matProps = new MaterialPropertyBlock();
                matProps.SetInt("_SectorHashSeed", sectorHashSeed);
            }

            return matProps;
        }

        private NativeList<AABB> hostAABBBuffer;
        private NativeList<int> hostBrickBuffer;
#if !CAELIX_RENDER_DISABLE_CULLING
        private SparseBrickIdTable rendererBrickMap;
#endif

        public int BrickBufferSize
        {
            get
            {
#if CAELIX_RENDER_DISABLE_CULLING
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
            (ulong)((hostBrickBuffer.IsCreated ? hostBrickBuffer.Capacity * sizeof(int) : 0)
                  + (hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0));

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
            if (sector.RendererNonEmptyBrickCount == 0) return;

            int requestedCapacity = 0;
#if CAELIX_RENDER_DISABLE_CULLING
            requestedCapacity = sector.RendererNonEmptyBrickCount;
#endif

            // Prepare Host Buffers
            if (!HostBufferInitialized)
            {
                hostAABBBuffer = new NativeList<AABB>(requestedCapacity, Allocator.Persistent);
                hostBrickBuffer = new NativeList<int>(requestedCapacity * BRICK_DATA_LENGTH,
                    Allocator.Persistent);
#if !CAELIX_RENDER_DISABLE_CULLING
                rendererBrickMap = SparseBrickIdTable.New(Allocator.Persistent);
#endif
            }

            // Pre-allocate buffers only for non-culling case
            // Since we already know how many bricks will be there before running the actual data-filling job
#if CAELIX_RENDER_DISABLE_CULLING
            hostAABBBuffer.Resize(requestedCapacity, NativeArrayOptions.ClearMemory);
            hostBrickBuffer.Resize(requestedCapacity * BRICK_DATA_LENGTH, NativeArrayOptions.ClearMemory);
#endif
        }
        
        /// <summary>
        /// Prepares and resizes GPU buffers if needed to accommodate current brick count.
        /// </summary>
        /// <param name="forcedAABBRealloc">
        /// Force reallocation of the GraphicsBuffer.
        /// Used to tackle Unity's limitation of non-refittable AABB BLASs.
        /// </param>
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
                aabbBuffer?.Dispose();
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);
            }
            
            // Allocate our buffers on GPU side

            var new_brickBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw, 
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
            // Unity 6.5 made Object.GetInstanceID() an obsolete-as-error; GetEntityId() replaces it.
            uint seed = (uint)entity.GetEntityId().GetHashCode();
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

            // TODO: FIXME: Remove this forced realloc
            // if use fixed AABB or find ways to refit a tight AABB
            if(shouldUpdateAABB)
            {
                aabbBuffer?.Dispose();
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);

                // Invalidate the AABBconfig so later it recreates
                AABBconfig.aabbCount = 0;
            }

            isRealloc = ExtendGPUBuffers();

            if (minModified <= maxModified)
            {
                // Profiler.BeginSample($"UploadData ({maxModified - minModified + 1} Bricks)");
                Profiler.BeginSample("UploadData");
                // Partially update buffers
                // TODO: This will not work since we need to realloc the full AABB buffer everytime.
                // Therefore, always upload the full aabbBuffer unless later we can refit the AABB BLAS.
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
            
            if (shouldUpdateAABB || isRealloc)
            {
                Profiler.BeginSample("Handle Realloc");

                // Zeroing the config makes RenderModifyAS rebuild it with the current aabbCount.
                AABBconfig = default;

                // Only place brickBuffer can be replaced, so the only place the binding can go
                // stale — RenderModifyAS relies on that and never re-binds it.
                EnsureMaterialProperties().SetBuffer("g_bricks", brickBuffer);
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
        /// <remarks>
        /// Must be called after Render(), which is what turns fresh voxel data into isDirty.
        /// Three outcomes:
        /// - geometry changed: rebuild the RTAS instance (remove + add) and push the property block;
        /// - instance exists and still needs tracking (moving entity, or the one frame an entity
        ///   turns static and its motion vectors must settle): push transform + property block;
        /// - otherwise: nothing, which is how static entities stay free after their first frame.
        /// </remarks>
        public void RenderModifyAS(ref RayTracingAccelerationStructure AS, VoxelEntity entity, int3 sectorPos)
        {
            Matrix4x4 objectToWorld =
                entity.transform.localToWorldMatrix *
                Matrix4x4.Translate((sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS).ToVector3Int());

            // Set for the frame an entity flips to static (including the initial flip on a
            // born-static body). Collapsing prev onto the current transform zeroes the motion
            // vectors once, so the denoiser stops reprojecting a body that will never move again.
            // CaelixRenderer clears the flag after every sector of the entity has consumed it.
            bool resetsMotionVectors = entity._shouldResetMotionVectors;
            Matrix4x4 prevObjectToWorld = (hasPreviousObjectToWorld && !resetsMotionVectors)
                ? previousObjectToWorld
                : objectToWorld;

            // Tight AABBs change whenever an edit moves a brick's occupied bounds, with buffer and
            // aabbCount staying identical. Unity builds static AABB geometry once and ignores later
            // buffer writes (Remove+Add reuses the cached BLAS), which leaves stale boxes that crop
            // newly grown voxels — so an edited sector must be registered as dynamic geometry for
            // the build that follows. But dynamicGeometry is baked in at AddInstance time and costs
            // a full BLAS rebuild on *every* subsequent build, so it has to be taken back off again:
            // `settles` is the falling edge, the first tick a dynamic instance stops changing, and
            // the only reason to touch the RTAS when the sector is otherwise clean.
            bool wantsDynamicGeometry = isDirty;
            bool settles = hasRenderable && instanceIsDynamic && !wantsDynamicGeometry;

            // A dirty sector with no renderable bricks is legitimate — a freshly loaded sector whose
            // render-data job has not populated the brick buffer yet, or one whose faces are all
            // culled. RayTracingAABBsInstanceConfig / AddInstance throw on aabbCount == 0, and since
            // isDirty is only cleared at the end of this method, letting that throw left isDirty
            // stuck true: it re-threw every frame and stalled the RTAS update for every other sector
            // too. Skip instead; the sector re-dirties and retries once real geometry appears.
            // (A sector that empties out while registered keeps its last instance, and therefore its
            // dynamic flag, until the sector itself is removed — nothing can be re-added at count 0.)
            bool rebuildsInstance = (isDirty || settles) && BrickBufferSize > 0;
            bool retracksInstance = hasRenderable && (!entity.IsStatic || resetsMotionVectors);

            if (rebuildsInstance || retracksInstance)
            {
                // Previous transform is delivered through the per-instance property block for now.
                // This matches the sector-instance RTAS layout, but it means moving sectors need a
                // property-block update even when voxel geometry is unchanged. If that gets expensive,
                // move these matrices to a structured buffer keyed by a stable instance/sector id.
                // g_bricks needs no refresh here: Render() re-binds it whenever the buffer is replaced.
                EnsureMaterialProperties().SetMatrix("_PrevObjectToWorld", prevObjectToWorld);
            }

            if (rebuildsInstance)
            {
                EnsureAABBConfig();

                // Assigned here rather than inside EnsureAABBConfig: on a settle tick Render() never
                // ran, so the config was never invalidated and still carries the previous tick's flag.
                // AABBconfig.dynamicGeometry = wantsDynamicGeometry;

                AS.RemoveInstance(sectorASHandle);
                sectorASHandle = AS.AddInstance(AABBconfig, objectToWorld);
                instanceIsDynamic = wantsDynamicGeometry;
                hasRenderable = true;
                AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
            }
            else if (retracksInstance)
            {
                AS.UpdateInstanceTransform(sectorASHandle, objectToWorld);
                AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
            }

            // Recorded unconditionally: this is simply where the entity was on the last renderer
            // tick, which is what the next tick needs as its previous transform — whether or not
            // anything was pushed this tick.
            previousObjectToWorld = objectToWorld;
            hasPreviousObjectToWorld = true;
            isDirty = false;
        }

        /// <summary>
        /// Builds the AABB instance config if Render() invalidated it (or it was never built).
        /// </summary>
        /// <remarks>
        /// dynamicGeometry is deliberately not set here — it is per-registration state decided by
        /// the caller, and this method no-ops on the settle tick.
        /// </remarks>
        private void EnsureAABBConfig()
        {
            if (AABBconfig.aabbCount != 0)
            {
                return;
            }

            AABBconfig = new RayTracingAABBsInstanceConfig(
                aabbBuffer, BrickBufferSize, false, sectorMaterial)
            {
                dynamicGeometry = false,
                accelerationStructureBuildFlagsOverride = true,
                accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace,
                materialProperties = EnsureMaterialProperties(),
            };
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
#if !CAELIX_RENDER_DISABLE_CULLING
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
#if !CAELIX_RENDER_DISABLE_CULLING
            if (rendererBrickMap.IsCreated) rendererBrickMap.Dispose();
#endif
            
            aabbBuffer?.Dispose();
            brickBuffer?.Dispose();
        }
    }
}
