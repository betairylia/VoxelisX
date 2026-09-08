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
        private SparseBrickIdTable rendererBrickMap;

        /// <summary>
        /// The brick records the last render job rewrote, plus the slot each of them belongs in.
        /// Created per job, consumed and disposed by <see cref="UploadBricks"/>.
        /// </summary>
        /// <remarks>
        /// There is no host copy of the sector's records: only the bricks a job actually touched
        /// ever exist on the CPU, and only until they have been scattered into the pool.
        /// </remarks>
        private NativeList<int> stagingWords;
        private NativeList<int> stagingSlots;

        private GraphicsBuffer aabbBuffer;

        /// <summary>This sector's slice of the brick pool. Null until the first allocation.</summary>
        /// <remarks>
        /// A class the pool owns and mutates: compacting a page rewrites the offset in place. Read
        /// its fields fresh every use rather than caching them.
        /// </remarks>
        private CaelixBrickPool.Handle poolHandle;

        /// <summary>
        /// Set when the sector's records are gone and every brick has to be generated again: the
        /// pool had no room, so the range — and with it everything the pool would have carried over
        /// — was dropped.
        /// </summary>
        private bool needsFullRebuild;

        /// <summary>Slot in the instance table, and therefore this sector's RTAS instance ID.</summary>
        private int instanceSlot = -1;

        /// <summary>Word offset and page of the range the instance record currently names; -1 = never published.</summary>
        private int publishedBrickBase = -1;
        private int publishedPage = -1;

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

        /// <summary>
        /// Gets the estimated host memory usage in bytes for this renderer's buffers: the AABB list
        /// only, because brick records are never mirrored on the host.
        /// </summary>
        public ulong MemoryUsage =>
            (ulong)(hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0);

        /// <summary>Gets the estimated VRAM usage in bytes: the AABB buffer plus this sector's pool range.</summary>
        public ulong VRAMUsage =>
            (ulong)(Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * 24 +
                    (poolHandle?.CapacityBricks ?? 0) * SectorRenderer.BRICK_DATA_LENGTH * 4);

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
            if (shouldRemove || sector.IsRendererEmpty || (!needsFullRebuild && !sector.IsRendererRequireUpdate))
            {
                return;
            }

            PreallocateBuffers(sector.Get());

            rendererJob = new SectorRenderer.GenerateSectorRenderDataJob()
            {
                forceFullUpload = needsFullRebuild,
                sectorHandle = sector,
                neighbors = neighborHandle,
                rendererBrickMap = rendererBrickMap,
                aabbBuffer = hostAABBBuffer,
                stagingWords = new NativeList<int>(
                    SectorRenderer.BRICK_DATA_LENGTH * 16, Allocator.TempJob),
                stagingSlots = new NativeList<int>(16, Allocator.TempJob),
                syncRecord = new NativeArray<int>(1, Allocator.TempJob)
            };

            stagingWords = rendererJob.stagingWords;
            stagingSlots = rendererJob.stagingSlots;
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
                rendererBrickMap = SparseBrickIdTable.New(Allocator.Persistent);
            }
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return jobScheduled;
        }

        /// <summary>
        /// Consumes the completed render job: replaces the AABB buffer when a tight box moved and
        /// resizes this sector's pool range.
        /// </summary>
        /// <remarks>
        /// The staged brick records are deliberately written later, in <see cref="UploadBricks"/>:
        /// resizing a range can grow a pool page, which replaces its <see cref="GraphicsBuffer"/>
        /// and re-packs every other range on it. Every sector must therefore learn its final range
        /// before any of them writes.
        /// </remarks>
        internal void ApplyCompletedRenderJob(CaelixBrickPool pool)
        {
            if (!jobScheduled)
            {
                return;
            }

            jobScheduled = false;

            bool aabbChanged = rendererJob.syncRecord[0] > 0;
            rendererJob.syncRecord.Dispose();

            // Unity builds static AABB geometry once per (buffer, aabbCount) and ignores later
            // writes, so a moved tight box only reaches the BLAS through a brand-new buffer.
            // See SectorRenderer.ApplyCompletedRenderJob for the full reasoning.
            bool aabbRealloc = aabbChanged || aabbBuffer == null;
            if (aabbRealloc)
            {
                aabbBuffer?.Dispose();
                aabbBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);

                // Invalidate the AABBconfig so RenderModifyAS recreates it against the new buffer.
                AABBconfig.aabbCount = 0;

                // Always the whole host list, and only here: the buffer was just replaced, so a
                // partial write would leave every untouched box uninitialised — and while the boxes
                // did not change there is nothing to write at all.
                if (hostAABBBuffer.IsCreated && hostAABBBuffer.Length > 0)
                {
                    aabbBuffer.SetData(hostAABBBuffer.AsArray());
                }
            }

            int requestedCapacity = SectorRenderer.GetCapacity(BrickBufferSize);
            if (requestedCapacity != (poolHandle?.CapacityBricks ?? 0))
            {
                // Reallocate carries the records the range already holds over to the new one, so a
                // resize costs a GPU copy rather than a full regeneration.
                poolHandle = pool.Reallocate(poolHandle, requestedCapacity);
            }

            if (poolHandle == null || !poolHandle.IsValid)
            {
                // Every page is full, and the old range went with the failed reallocation, so every
                // brick has to be generated again once there is room. The capacity test above
                // retries on every tick because an invalid handle reports capacity 0. Until one
                // succeeds RenderModifyAS skips this sector, so it is simply not drawn.
                needsFullRebuild = true;
                return;
            }

            needsFullRebuild = false;

            if (aabbRealloc)
            {
                AABBconfig = default;
                isDirty = true;
            }
        }

        /// <summary>
        /// Adds this sector's staged brick records to the frame's scatter batch. Runs after every
        /// sector has settled its range, so a pool growth caused by one sector is seen by all of
        /// them.
        /// </summary>
        /// <remarks>
        /// Staged, not dispatched: the renderer flushes the whole frame's batch once, after every
        /// sector has staged. See <see cref="CaelixBrickGpuOps"/> for why writing and dispatching
        /// per sector hung the device.
        /// </remarks>
        internal void UploadBricks(CaelixBrickPool pool)
        {
            if (!stagingSlots.IsCreated)
            {
                return;
            }

            if (stagingSlots.Length > 0 && poolHandle != null && poolHandle.IsValid)
            {
                Debug.Assert(
                    BrickBufferSize <= poolHandle.CapacityBricks,
                    "RayQuerySectorRenderer: the sector has more bricks than the pool range reserved for it.");

                pool.Ops.StageScatter(
                    pool.GetPageBuffer(poolHandle.Page),
                    poolHandle.OffsetBricks,
                    stagingWords.AsArray(),
                    stagingSlots.AsArray(),
                    stagingSlots.Length);
            }

            // StageScatter has copied the records into the frame staging buffer, so the lists go
            // back immediately rather than waiting for the flush.
            DisposeStaging();
        }

        /// <summary>Releases the per-job staging lists.</summary>
        private void DisposeStaging()
        {
            if (stagingWords.IsCreated) stagingWords.Dispose();
            if (stagingSlots.IsCreated) stagingSlots.Dispose();
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
            // sector re-dirties and retries once real geometry appears. A sector the pool could not
            // find room for is skipped the same way.
            bool hasPool = poolHandle != null && poolHandle.IsValid;

            // Fall back to the last published range while the pool has no room for this sector, so
            // a retrack cannot overwrite a live instance's record with a null range.
            int brickBaseWords = hasPool
                ? poolHandle.OffsetBricks * SectorRenderer.BRICK_DATA_LENGTH
                : publishedBrickBase;
            int brickPage = hasPool ? poolHandle.Page : publishedPage;

            bool rebuildsInstance = isDirty && BrickBufferSize > 0 && hasPool;
            bool retracksInstance = hasRenderable && (!entity.IsStatic || resetsMotionVectors);
            // A range that moved — this sector resized, or another one grew and compacted the page —
            // changes only what the record names, so it is published without touching the
            // acceleration structure.
            bool rangeMoved = hasPool && (brickBaseWords != publishedBrickBase || brickPage != publishedPage);
            bool republishesRecord = rangeMoved && hasRenderable;

            if (rebuildsInstance || retracksInstance || republishesRecord)
            {
                if (brickBaseWords >= 0 && brickPage >= 0)
                {
                    // The shader reads the previous transform, the brick pool page and the hash seed
                    // from the instance table, keyed by the RTAS instance ID. The slot is claimed
                    // once and kept for the renderer's whole life, because the ID has to survive a
                    // remove + add.
                    if (instanceSlot < 0)
                    {
                        instanceSlot = table.Allocate();
                    }

                    table.Set(instanceSlot, prevObjectToWorld, brickBaseWords, brickPage, (uint)sectorHashSeed);
                    publishedBrickBase = brickBaseWords;
                    publishedPage = brickPage;
                }
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

            if (poolHandle != null)
            {
                pool.Free(poolHandle);
                poolHandle = null;
            }

            isDirty = false;
            Dispose();
        }

        /// <summary>Disposes host buffers, the brick map and the AABB buffer.</summary>
        public void Dispose()
        {
            // A job may still be in flight when a view despawns mid-frame; its syncRecord and its
            // staging lists are TempJob allocations this renderer owns.
            if (jobScheduled)
            {
                jobHandle.Complete();
                if (rendererJob.syncRecord.IsCreated)
                {
                    rendererJob.syncRecord.Dispose();
                }

                jobScheduled = false;
            }

            DisposeStaging();

            if (hostAABBBuffer.IsCreated) hostAABBBuffer.Dispose();
            if (rendererBrickMap.IsCreated) rendererBrickMap.Dispose();

            aabbBuffer?.Dispose();
            aabbBuffer = null;
        }
    }
}
