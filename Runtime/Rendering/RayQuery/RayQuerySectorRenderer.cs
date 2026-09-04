using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Client;
using Caelix.Utils;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Inline ray query counterpart of <see cref="SectorRenderer"/>: prepares one sector's render
    /// data and keeps its acceleration-structure instance up to date.
    /// </summary>
    /// <remarks>
    /// The render-data job, the AABB buffer rules and the RTAS caching rules are the same as
    /// <see cref="SectorRenderer"/>'s — read that class's comments, in particular why the AABB
    /// buffer is thrown away and reallocated whenever a tight box moves. Two things differ:
    /// <list type="bullet">
    /// <item>brick records go into the shared <see cref="CaelixBrickPool"/> instead of a per-sector
    /// buffer, because a ray query kernel has no shader table and therefore no per-instance
    /// binding;</item>
    /// <item>the previous transform and the sector hash seed travel through
    /// <see cref="CaelixRayQueryInstanceTable"/>, addressed by the RTAS instance ID, instead of a
    /// per-instance material property block.</item>
    /// </list>
    /// </remarks>
    public class RayQuerySectorRenderer : IDisposable
    {
        private readonly Material sectorMaterial;
        private readonly int sectorHashSeed;

        private NativeList<SectorRenderer.AABB> hostAABBBuffer;
        private NativeList<int> hostBrickBuffer;
        private SparseBrickIdTable rendererBrickMap;

        private GraphicsBuffer aabbBuffer;

        private CaelixBrickPool.Range poolRange;
        /// <summary>Pool generation the current range's contents were uploaded under; -1 = never.</summary>
        private int uploadedGeneration = -1;
        /// <summary>Brick index range modified since the last upload. -1 means "nothing pending".</summary>
        private int pendingUploadMin = -1;
        private int pendingUploadMax = -1;
        private bool needsFullUpload;

        /// <summary>Slot in the instance table, and therefore this sector's RTAS instance ID.</summary>
        private int instanceSlot = -1;

        private int sectorASHandle;
        private bool hasRenderable;
        private bool isDirty;
        private bool shouldRemove;

        private RayTracingAABBsInstanceConfig AABBconfig;
        private Matrix4x4 previousObjectToWorld;
        private bool hasPreviousObjectToWorld;

        // Temp variables used in the render process. RenderEmitJob and ApplyCompletedRenderJob must
        // run in the same frame / tick.
        private JobHandle jobHandle;
        private SectorRenderer.GenerateSectorRenderDataJob rendererJob;
        private bool jobScheduled;

        /// <summary>
        /// Number of brick slots the sector currently occupies.
        /// </summary>
        /// <remarks>
        /// Always taken from the sparse brick map. Unlike <see cref="SectorRenderer"/> this class
        /// does not honour <c>CAELIX_RENDER_DISABLE_CULLING</c>: the pool addresses bricks by the
        /// renderer's own compacted ids, which only exist with culling on.
        /// </remarks>
        public int BrickBufferSize => rendererBrickMap.IsCreated ? rendererBrickMap.Capacity : 0;

        /// <summary>Gets the estimated host memory usage in bytes for this renderer's buffers.</summary>
        public ulong MemoryUsage =>
            (ulong)((hostBrickBuffer.IsCreated ? hostBrickBuffer.Capacity * sizeof(int) : 0)
                  + (hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0));

        /// <summary>Gets the estimated VRAM usage in bytes: the AABB buffer plus this sector's pool range.</summary>
        public ulong VRAMUsage =>
            (ulong)(Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * 24 +
                    poolRange.CapacityBricks * SectorRenderer.BRICK_DATA_LENGTH * 4);

        private bool HostBufferInitialized => hostAABBBuffer.IsCreated;

        /// <summary>
        /// Constructs a renderer for one (view, sector) pair.
        /// </summary>
        /// <param name="entity">The view the sector belongs to; part of the hash seed.</param>
        /// <param name="sectorPos">The sector's position in sector coordinates.</param>
        /// <param name="material">
        /// Material handed to <see cref="RayTracingAABBsInstanceConfig"/>. The ray query path never
        /// runs its hit group, but the config requires one.
        /// </param>
        public RayQuerySectorRenderer(EntityView entity, int3 sectorPos, Material material)
        {
            sectorMaterial = material;
            sectorHashSeed = unchecked((int)ComputeSectorHashSeed(entity, sectorPos));
        }

        private static uint HashUInt(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        private static uint ComputeSectorHashSeed(EntityView entity, int3 sectorPos)
        {
            uint seed = (uint)entity.Guid.GetHashCode();
            seed ^= (uint)sectorPos.x * 0x9E3779B9u;
            seed ^= (uint)sectorPos.y * 0x85EBCA6Bu;
            seed ^= (uint)sectorPos.z * 0xC2B2AE35u;
            return HashUInt(seed);
        }

        /// <summary>
        /// Schedules the Burst job that turns dirty voxel data into render data.
        /// Must be followed by <see cref="ApplyCompletedRenderJob"/> in the same frame.
        /// </summary>
        public void RenderEmitJob(SectorHandle sector, SectorNeighborHandles neighborHandle)
        {
            if (shouldRemove || sector.IsRendererEmpty || (!sector.IsRendererRequireUpdate))
            {
                return;
            }

            PreallocateBuffers(sector.Get());

            rendererJob = new SectorRenderer.GenerateSectorRenderDataJob()
            {
                sectorHandle = sector,
                neighbors = neighborHandle,
                rendererBrickMap = rendererBrickMap,
                aabbBuffer = hostAABBBuffer,
                brickData = hostBrickBuffer,
                syncRecord = new NativeArray<int>(3, Allocator.TempJob)
            };
            jobHandle = rendererJob.Schedule();

            jobScheduled = true;
        }

        /// <summary>Creates the host buffers on the first update of a non-empty sector.</summary>
        private void PreallocateBuffers(Sector sector)
        {
            if (sector.RendererNonEmptyBrickCount == 0)
            {
                return;
            }

            if (!HostBufferInitialized)
            {
                hostAABBBuffer = new NativeList<SectorRenderer.AABB>(0, Allocator.Persistent);
                hostBrickBuffer = new NativeList<int>(0, Allocator.Persistent);
                rendererBrickMap = SparseBrickIdTable.New(Allocator.Persistent);
            }
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return jobScheduled;
        }

        /// <summary>
        /// Consumes the completed render job: reallocates the AABB buffer when a tight box moved,
        /// resizes this sector's pool range, and records which bricks still have to be uploaded.
        /// </summary>
        /// <remarks>
        /// Brick uploads are deliberately deferred to <see cref="UploadBricks"/>: resizing a range
        /// can grow the pool, which replaces its <see cref="GraphicsBuffer"/> and invalidates every
        /// other sector's contents. Every sector must therefore learn its final range before any of
        /// them writes.
        /// </remarks>
        internal void ApplyCompletedRenderJob(CaelixBrickPool pool)
        {
            if (!jobScheduled)
            {
                return;
            }

            jobScheduled = false;

            int minModified = rendererJob.syncRecord[0];
            int maxModified = rendererJob.syncRecord[1];
            bool shouldUpdateAABB = rendererJob.syncRecord[2] > 0;
            rendererJob.syncRecord.Dispose();

            // Unity builds static AABB geometry once per (buffer, aabbCount) and ignores later
            // writes, so a moved tight box only reaches the BLAS through a brand-new buffer.
            // See SectorRenderer.ApplyCompletedRenderJob for the full reasoning.
            bool aabbRealloc = false;
            if (shouldUpdateAABB || aabbBuffer == null)
            {
                aabbBuffer?.Dispose();
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);

                // Invalidate the AABBconfig so RenderModifyAS recreates it against the new buffer.
                AABBconfig.aabbCount = 0;
                aabbRealloc = true;
            }

            bool poolRangeChanged = false;
            int requestedCapacity = SectorRenderer.GetCapacity(BrickBufferSize);
            if (requestedCapacity != poolRange.CapacityBricks)
            {
                pool.Free(poolRange);
                poolRange = pool.Allocate(requestedCapacity);
                needsFullUpload = true;
                poolRangeChanged = true;
            }

            if (minModified <= maxModified)
            {
                // Always the full AABB buffer: it was just replaced, so a partial upload would
                // leave the untouched boxes uninitialised.
                aabbBuffer.SetData(hostAABBBuffer.AsArray());

                pendingUploadMin = pendingUploadMin < 0 ? minModified : Math.Min(pendingUploadMin, minModified);
                pendingUploadMax = pendingUploadMax < 0 ? maxModified : Math.Max(pendingUploadMax, maxModified);
            }

            if (aabbRealloc || poolRangeChanged)
            {
                AABBconfig = default;
                isDirty = true;
            }
        }

        /// <summary>
        /// Writes this sector's brick records into the pool. Runs after every sector has settled
        /// its range, so a pool growth caused by one sector is seen by all of them.
        /// </summary>
        internal void UploadBricks(CaelixBrickPool pool)
        {
            if (!poolRange.IsValid || !hostBrickBuffer.IsCreated)
            {
                return;
            }

            int brickCount = hostBrickBuffer.Length / SectorRenderer.BRICK_DATA_LENGTH;
            if (brickCount <= 0)
            {
                pendingUploadMin = -1;
                pendingUploadMax = -1;
                return;
            }

            Debug.Assert(
                brickCount <= poolRange.CapacityBricks,
                "RayQuerySectorRenderer: host brick buffer is larger than the pool range reserved for it.");

            if (uploadedGeneration != pool.Generation || needsFullUpload)
            {
                // Either the pool replaced its buffer (contents are gone) or this sector moved to a
                // different range. Both mean the whole range has to be written again, and both move
                // the brick base the instance record publishes.
                pool.Upload(poolRange, hostBrickBuffer.AsArray(), 0, brickCount);
                uploadedGeneration = pool.Generation;
                needsFullUpload = false;
                isDirty = true;
            }
            else if (pendingUploadMin >= 0)
            {
                int last = Math.Min(pendingUploadMax, brickCount - 1);
                if (last >= pendingUploadMin)
                {
                    pool.Upload(poolRange, hostBrickBuffer.AsArray(), pendingUploadMin, last - pendingUploadMin + 1);
                }
            }

            pendingUploadMin = -1;
            pendingUploadMax = -1;
        }

        /// <summary>
        /// Updates the acceleration structure and the instance record with this sector's state.
        /// </summary>
        /// <remarks>
        /// Same three outcomes as <see cref="SectorRenderer.RenderModifyAS"/>: rebuild the instance
        /// when geometry changed, retrack it while the entity moves (or for the one frame its
        /// motion vectors must settle), otherwise do nothing.
        /// </remarks>
        public void RenderModifyAS(
            ref RayTracingAccelerationStructure AS,
            EntityView entity,
            int3 sectorPos,
            CaelixRayQueryInstanceTable table)
        {
            Matrix4x4 objectToWorld =
                entity.LocalToWorld *
                Matrix4x4.Translate((sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS).ToVector3Int());

            // Set for the frame an entity flips to static (including the initial flip on a
            // born-static body). Collapsing prev onto the current transform zeroes the motion
            // vectors once, so the denoiser stops reprojecting a body that will never move again.
            // CaelixRayQueryRenderer clears the flag after every sector of the entity consumed it.
            bool resetsMotionVectors = entity.ShouldResetMotionVectors;
            Matrix4x4 prevObjectToWorld = (hasPreviousObjectToWorld && !resetsMotionVectors)
                ? previousObjectToWorld
                : objectToWorld;

            // A dirty sector with no renderable bricks is legitimate (freshly loaded, or fully
            // culled) and RayTracingAABBsInstanceConfig throws on aabbCount == 0, so skip it; the
            // sector re-dirties and retries once real geometry appears.
            bool rebuildsInstance = isDirty && BrickBufferSize > 0;
            bool retracksInstance = hasRenderable && (!entity.IsStatic || resetsMotionVectors);

            if (rebuildsInstance || retracksInstance)
            {
                // The shader reads the previous transform and the hash seed from the instance
                // table, keyed by the RTAS instance ID. The slot is claimed once and kept for the
                // renderer's whole life, because the ID has to survive a remove + add.
                if (instanceSlot < 0)
                {
                    instanceSlot = table.Allocate();
                }

                table.Set(
                    instanceSlot,
                    prevObjectToWorld,
                    poolRange.OffsetBricks * SectorRenderer.BRICK_DATA_LENGTH,
                    (uint)sectorHashSeed);
            }

            if (rebuildsInstance)
            {
                EnsureAABBConfig();

                if (hasRenderable)
                {
                    AS.RemoveInstance(sectorASHandle);
                }

                sectorASHandle = AS.AddInstance(AABBconfig, objectToWorld, (uint)instanceSlot);
                hasRenderable = true;
            }
            else if (retracksInstance)
            {
                AS.UpdateInstanceTransform(sectorASHandle, objectToWorld);
            }

            // Recorded unconditionally: this is simply where the entity was on the last renderer
            // tick, which is what the next tick needs as its previous transform.
            previousObjectToWorld = objectToWorld;
            hasPreviousObjectToWorld = true;
            isDirty = false;
        }

        /// <summary>Builds the AABB instance config if it was invalidated (or never built).</summary>
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
            };
        }

        /// <summary>Marks this sector for removal from the acceleration structure.</summary>
        public void MarkRemove()
        {
            shouldRemove = true;
        }

        /// <summary>Releases the RTAS instance, the instance slot and the pool range, then disposes.</summary>
        public void RemoveMe(
            ref RayTracingAccelerationStructure AS, CaelixRayQueryInstanceTable table, CaelixBrickPool pool)
        {
            if (!shouldRemove)
            {
                return;
            }

            if (hasRenderable)
            {
                AS.RemoveInstance(sectorASHandle);
                hasRenderable = false;
            }

            if (instanceSlot >= 0)
            {
                table.Free(instanceSlot);
                instanceSlot = -1;
            }

            pool.Free(poolRange);
            poolRange = default;

            isDirty = false;
            Dispose();
        }

        /// <summary>Disposes host buffers, the brick map and the AABB buffer.</summary>
        public void Dispose()
        {
            // A job may still be in flight when a view despawns mid-frame; its syncRecord is a
            // TempJob allocation this renderer owns.
            if (jobScheduled)
            {
                jobHandle.Complete();
                if (rendererJob.syncRecord.IsCreated)
                {
                    rendererJob.syncRecord.Dispose();
                }

                jobScheduled = false;
            }

            if (hostAABBBuffer.IsCreated) hostAABBBuffer.Dispose();
            if (hostBrickBuffer.IsCreated) hostBrickBuffer.Dispose();
            if (rendererBrickMap.IsCreated) rendererBrickMap.Dispose();

            aabbBuffer?.Dispose();
            aabbBuffer = null;
        }
    }
}
