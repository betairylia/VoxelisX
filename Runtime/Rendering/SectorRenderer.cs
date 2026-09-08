using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Client;
using Caelix.Rendering.RayQuery;
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
        private static readonly ProfilerMarker s_ExtendGpuBuffersMarker = new("ExtendGPUBuffers");
        private static readonly ProfilerMarker s_UploadDataMarker = new("UploadData");
        private static readonly ProfilerMarker s_HandleReallocMarker = new("Handle Realloc");

        /// <summary>
        /// Axis-aligned bounding box structure for ray tracing.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct AABB
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
        public SectorRenderer(EntityView entity, int3 sectorPos)
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
        /// <para>
        /// Never called in <see cref="CaelixBrickStorage.SharedPoolInstanceTable"/> storage, where
        /// <see cref="matProps"/> stays null: a property block IS the hit group's local root
        /// arguments, and that mode exists to have none.
        /// </para>
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

        /// <summary>
        /// The brick records the last render job rewrote, plus the slot each of them belongs in.
        /// Created per job, consumed and disposed by <see cref="UploadBricks"/>.
        /// </summary>
        /// <remarks>
        /// There is no host copy of the sector's records: only the bricks a job actually touched
        /// ever exist on the CPU, and only until they have been scattered into brick storage.
        /// </remarks>
        private NativeList<int> stagingWords;
        private NativeList<int> stagingSlots;

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
        /// Gets the estimated host memory usage in bytes for this renderer's buffers: the AABB list
        /// only, because brick records are never mirrored on the host.
        /// </summary>
        public ulong MemoryUsage =>
            (ulong)(hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0);

        private GraphicsBuffer aabbBuffer;
        private GraphicsBuffer brickBuffer;
        private int currentGPUBrickBufferCapacity;

        // --- Brick pool mode. All null / -1 / false in per-sector mode, where `pool` is always null.
        // Mirrors RayQuerySectorRenderer's bookkeeping; read that class for the reasoning.

        /// <summary>This sector's slice of the brick pool. Null until the first allocation.</summary>
        /// <remarks>
        /// A class the pool owns and mutates: compacting a page rewrites the offset in place. Read
        /// its fields fresh every use rather than caching them.
        /// </remarks>
        private CaelixBrickPool.Handle poolHandle;

        /// <summary>
        /// Set when the sector's records are gone and every brick has to be generated again: the
        /// pool had no room, so the range — and with it everything the pool would have carried over
        /// — was dropped. Pool storage only; a per-sector buffer cannot fail to allocate.
        /// </summary>
        private bool needsFullRebuild;

        /// <summary>Word offset and page the instance record currently names; -1 = never published.</summary>
        private int publishedBrickBase = -1;
        private int publishedPage = -1;

        /// <summary>
        /// Slot in the instance table, and therefore this sector's RTAS instance ID. -1 until the
        /// first publish; always -1 outside
        /// <see cref="CaelixBrickStorage.SharedPoolInstanceTable"/> storage.
        /// </summary>
        /// <remarks>
        /// Claimed once and kept for this renderer's whole life, because the ID has to survive the
        /// remove + add a geometry rebuild does. Mirrors <see cref="RayQuerySectorRenderer"/>.
        /// </remarks>
        private int instanceSlot = -1;

        private bool HostBufferInitialized => hostAABBBuffer.IsCreated;

        internal static int GetCapacity(int requestedLength)
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
#if !CAELIX_RENDER_DISABLE_CULLING
                rendererBrickMap = SparseBrickIdTable.New(Allocator.Persistent);
#endif
            }

            // Pre-allocate buffers only for non-culling case
            // Since we already know how many bricks will be there before running the actual data-filling job
#if CAELIX_RENDER_DISABLE_CULLING
            hostAABBBuffer.Resize(requestedCapacity, NativeArrayOptions.ClearMemory);
#endif
        }

        /// <summary>
        /// Resizes this sector's own brick buffer to the current brick count, carrying the records
        /// it already holds over to the new one.
        /// </summary>
        /// <param name="ops">The kernels that do the carry-over.</param>
        /// <returns>True if the buffer was replaced; false if the existing one is the right size.</returns>
        /// <remarks>
        /// The copy is what lets the render-data job stage only the bricks it rewrote: every other
        /// record exists nowhere else, so a fresh buffer would otherwise lose it. Brick ids are
        /// stable (<see cref="SparseBrickIdTable"/> never rearranges them), so slot k stays slot k
        /// and the surviving prefix is a single range.
        /// </remarks>
        private bool ExtendGPUBuffers(CaelixBrickGpuOps ops)
        {
            using (s_ExtendGpuBuffersMarker.Auto())
            {
                int requestedCapacity = GetCapacity(BrickBufferSize);
                if (requestedCapacity == currentGPUBrickBufferCapacity)
                {
                    return false;
                }

                var new_brickBuffer =
                    new GraphicsBuffer(
                        GraphicsBuffer.Target.Raw,
                        requestedCapacity * BRICK_DATA_LENGTH, 4);

                int copyBricks = Math.Min(currentGPUBrickBufferCapacity, requestedCapacity);
                if (brickBuffer != null && copyBricks > 0)
                {
                    NativeArray<uint4> ranges = new NativeArray<uint4>(1, Allocator.Temp);
                    ranges[0] = new uint4(0u, 0u, (uint)copyBricks, 0u);
                    ops.CopyRanges(brickBuffer, new_brickBuffer, ranges, 1, copyBricks);
                    ranges.Dispose();
                }

                brickBuffer?.Dispose();

                brickBuffer = new_brickBuffer;
                currentGPUBrickBufferCapacity = requestedCapacity;

                return true;
            }
        }

        /// <summary>
        /// Allocates a fresh AABB buffer at full sector capacity, replacing any existing one.
        /// </summary>
        /// <remarks>
        /// Always a brand-new buffer: Unity builds static AABB geometry once per (buffer, aabbCount)
        /// and ignores later writes, so a moved tight box only reaches the BLAS this way. Shared by
        /// both storage modes — the AABBs are per-sector either way.
        /// </remarks>
        private void EnsureAABBBuffer()
        {
            aabbBuffer?.Dispose();
            aabbBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS, 24);
        }

        // Temp variables used in Render process
        // Please ensure call RenderEmitJob and Render (after emit job) in the same frame / tick
        private JobHandle jobHandle;
        private GenerateSectorRenderDataJob rendererJob;
        private bool jobScheduled;
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

        private static uint ComputeSectorHashSeed(EntityView entity, int3 sectorPos)
        {
            // Unity 6.5 made Object.GetInstanceID() an obsolete-as-error; GetEntityId() replaces it.
            uint seed = (uint)entity.Guid.GetHashCode();
            seed ^= (uint)sectorPos.x * 0x9E3779B9u;
            seed ^= (uint)sectorPos.y * 0x85EBCA6Bu;
            seed ^= (uint)sectorPos.z * 0xC2B2AE35u;
            return HashUInt(seed);
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return jobScheduled;
        }

        /// <summary>
        /// Consumes the completed render job: replaces the AABB buffer when a tight box moved and
        /// sizes this sector's brick storage.
        /// </summary>
        /// <param name="pool">
        /// The shared brick pool in pool mode; null in per-sector mode, where this sector owns its
        /// own <c>g_bricks</c> buffer.
        /// </param>
        /// <param name="ops">
        /// The kernels that move brick records. In pool mode this is the pool's own instance; in
        /// per-sector mode it is the one <see cref="CaelixRenderer"/> owns.
        /// </param>
        /// <remarks>
        /// The staged brick records are deliberately written later, in <see cref="UploadBricks"/>,
        /// in both modes, so the CPU-side ordering does not depend on the storage mode. In pool mode
        /// the deferral is load-bearing: resizing a range can grow a pool page, which replaces its
        /// <see cref="GraphicsBuffer"/> and re-packs every other range on it, so every sector must
        /// learn its final range before any of them writes.
        /// </remarks>
        internal void ApplyCompletedRenderJob(CaelixBrickPool pool, CaelixBrickGpuOps ops)
        {
            if (!jobScheduled)
            {
                return;
            }

            jobScheduled = false;

            bool aabbChanged = rendererJob.syncRecord[0] > 0;
            rendererJob.syncRecord.Dispose();

            // TODO: FIXME: Remove this forced realloc
            // if use fixed AABB or find ways to refit a tight AABB
            bool aabbRealloc = aabbChanged || aabbBuffer == null;
            if (aabbRealloc)
            {
                EnsureAABBBuffer();

                // Invalidate the AABBconfig so later it recreates
                AABBconfig.aabbCount = 0;

                using (s_UploadDataMarker.Auto())
                {
                    // Always the whole host list, and only here: the buffer was just replaced, so a
                    // partial write would leave every untouched box uninitialised — and while the
                    // boxes did not change there is nothing to write at all.
                    if (hostAABBBuffer.IsCreated && hostAABBBuffer.Length > 0)
                    {
                        aabbBuffer.SetData(hostAABBBuffer.AsArray());
                    }
                }
            }

            if (pool == null)
            {
                bool brickRealloc = ExtendGPUBuffers(ops);

                // A per-sector buffer cannot fail to allocate, so the records are never lost.
                needsFullRebuild = false;

                if (aabbRealloc || brickRealloc)
                {
                    using (s_HandleReallocMarker.Auto())
                    {
                        // Zeroing the config makes RenderModifyAS rebuild it with the current aabbCount.
                        AABBconfig = default;

                        // Only place brickBuffer can be replaced, so the only place the binding can go
                        // stale — RenderModifyAS relies on that and never re-binds it.
                        EnsureMaterialProperties().SetBuffer("g_bricks", brickBuffer);
                        isDirty = true;
                    }
                }

                return;
            }

            int requestedCapacity = GetCapacity(BrickBufferSize);
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
        /// Adds this sector's staged brick records to the frame's scatter batch. Deferred out of
        /// <see cref="ApplyCompletedRenderJob"/> so that in pool mode every sector has settled its
        /// range — and therefore any page growth has happened — before the first write lands.
        /// </summary>
        /// <param name="pool">The shared brick pool in pool mode; null in per-sector mode.</param>
        /// <param name="ops">The kernels that do the scatter.</param>
        /// <remarks>
        /// Staged, not dispatched: the renderer flushes the whole frame's batch once, after every
        /// sector has staged. See <see cref="CaelixBrickGpuOps"/> for why writing and dispatching
        /// per sector hung the device.
        /// </remarks>
        internal void UploadBricks(CaelixBrickPool pool, CaelixBrickGpuOps ops)
        {
            if (!stagingSlots.IsCreated)
            {
                return;
            }

            if (stagingSlots.Length > 0)
            {
                GraphicsBuffer target = null;
                int slotBase = 0;

                if (pool == null)
                {
                    target = brickBuffer;
                }
                else if (poolHandle != null && poolHandle.IsValid)
                {
                    Debug.Assert(
                        BrickBufferSize <= poolHandle.CapacityBricks,
                        "SectorRenderer: the sector has more bricks than the pool range reserved for it.");

                    target = pool.GetPageBuffer(poolHandle.Page);
                    slotBase = poolHandle.OffsetBricks;
                }

                if (target != null)
                {
                    using (s_UploadDataMarker.Auto())
                    {
                        ops.StageScatter(
                            target, slotBase, stagingWords.AsArray(), stagingSlots.AsArray(), stagingSlots.Length);
                    }
                }
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
        /// Releases the RTAS instance and, in pool mode, this sector's pool range and instance
        /// table slot, then disposes.
        /// </summary>
        /// <param name="pool">The shared brick pool in pool mode; null in per-sector mode.</param>
        /// <param name="table">The instance table in table mode; null in the other modes.</param>
        public void RemoveMe(
            ref RayTracingAccelerationStructure AS, CaelixBrickPool pool, CaelixRayQueryInstanceTable table)
        {
            if (shouldRemove)
            {
                if (hasRenderable) AS.RemoveInstance(sectorASHandle);
                hasRenderable = false;
                // Debug.Log("Removed sector");
                isDirty = false;

                if (poolHandle != null)
                {
                    pool?.Free(poolHandle);
                    poolHandle = null;
                }

                if (instanceSlot >= 0)
                {
                    table?.Free(instanceSlot);
                    instanceSlot = -1;
                }

                Dispose();
            }
        }

        /// <summary>
        /// Updates the ray tracing acceleration structure with this sector's geometry.
        /// </summary>
        /// <param name="AS">The acceleration structure to update.</param>
        /// <param name="entity">The voxel entity this sector belongs to.</param>
        /// <param name="sectorPos">The position of this sector in sector coordinates.</param>
        /// <param name="pool">The shared brick pool in pool mode; null in per-sector mode.</param>
        /// <param name="table">
        /// The instance table in table mode; null in the other two modes. Non-null is what selects
        /// table mode: per-instance data then travels through the table rather than a property
        /// block, and this sector's hit-group records carry no local root arguments at all.
        /// </param>
        /// <remarks>
        /// Must be called after Render(), which is what turns fresh voxel data into isDirty.
        /// Four outcomes:
        /// - geometry changed: rebuild the RTAS instance (remove + add) and publish the record;
        /// - instance exists and still needs tracking (moving entity, or the one frame an entity
        ///   turns static and its motion vectors must settle): push transform + record;
        /// - pool mode only, the sector's range moved: publish the record alone, because the AABBs
        ///   did not change and rebuilding would throw away a perfectly good BLAS;
        /// - otherwise: nothing, which is how static entities stay free after their first frame.
        /// <para>
        /// "The record" is the property block in per-sector and plain pool storage, and a slot of
        /// <paramref name="table"/> in table storage — where a republish touches the acceleration
        /// structure not at all, because the table write is already the whole update.
        /// </para>
        /// </remarks>
        public void RenderModifyAS(
            ref RayTracingAccelerationStructure AS, EntityView entity, int3 sectorPos, CaelixBrickPool pool,
            CaelixRayQueryInstanceTable table)
        {
            bool usesTable = table != null;

            Matrix4x4 objectToWorld =
                entity.LocalToWorld *
                Matrix4x4.Translate((sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS).ToVector3Int());

            // Set for the frame an entity flips to static (including the initial flip on a
            // born-static body). Collapsing prev onto the current transform zeroes the motion
            // vectors once, so the denoiser stops reprojecting a body that will never move again.
            // CaelixRenderer clears the flag after every sector of the entity has consumed it.
            bool resetsMotionVectors = entity.ShouldResetMotionVectors;
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
            // A sector the pool could not find room for is skipped the same way: it has no bricks on
            // the GPU to point the hit group at. Always true in per-sector mode, which owns its buffer.
            bool hasPool = poolHandle != null && poolHandle.IsValid;
            bool storageReady = pool == null || hasPool;

            // Fall back to the last published range while the pool has no room for this sector, so a
            // retrack cannot overwrite a live instance's record with a null range. Both are -1 in
            // per-sector storage, where nothing is ever published.
            int brickBaseWords = hasPool ? poolHandle.OffsetBricks * BRICK_DATA_LENGTH : publishedBrickBase;
            int brickPage = hasPool ? poolHandle.Page : publishedPage;

            bool rebuildsInstance = (isDirty || settles) && BrickBufferSize > 0 && storageReady;
            bool retracksInstance = hasRenderable && (!entity.IsStatic || resetsMotionVectors);
            // Pool mode only: a range that moved — this sector resized, or another one grew and
            // compacted the page — changes only what the property block names, so it is published
            // without touching the acceleration structure.
            bool rangeMoved = hasPool && (brickBaseWords != publishedBrickBase || brickPage != publishedPage);
            bool republishesRecord = rangeMoved && hasRenderable;

            if (rebuildsInstance || retracksInstance || republishesRecord)
            {
                if (usesTable)
                {
                    // Table mode: nothing rides on the shader record, so no property block is ever
                    // created.
                    if (brickBaseWords >= 0 && brickPage >= 0)
                    {
                        // The slot is claimed once and kept for this renderer's whole life: it IS the
                        // RTAS instance ID, so it has to survive a remove + add.
                        if (instanceSlot < 0)
                        {
                            instanceSlot = table.Allocate();
                        }

                        table.Set(instanceSlot, prevObjectToWorld, brickBaseWords, brickPage, (uint)sectorHashSeed);
                        publishedBrickBase = brickBaseWords;
                        publishedPage = brickPage;
                    }
                }
                else
                {
                    // Previous transform is delivered through the per-instance property block for now.
                    // This matches the sector-instance RTAS layout, but it means moving sectors need a
                    // property-block update even when voxel geometry is unchanged. If that gets expensive,
                    // move these matrices to a structured buffer keyed by a stable instance/sector id.
                    // g_bricks needs no refresh here in per-sector storage: ApplyCompletedRenderJob
                    // re-binds it whenever the buffer is replaced.
                    EnsureMaterialProperties().SetMatrix("_PrevObjectToWorld", prevObjectToWorld);

                    if (pool != null && brickBaseWords >= 0 && brickPage >= 0)
                    {
                        // The page buffer goes through the per-instance binding the DXR path has anyway;
                        // a page switch inside the intersection shader is far slower (see CaelixBrickTrace.hlsl).
                        matProps.SetBuffer("g_bricks", pool.GetPageBuffer(brickPage));
                        matProps.SetInt("_BrickBase", brickBaseWords);
                        publishedBrickBase = brickBaseWords;
                        publishedPage = brickPage;
                    }
                }
            }

            if (rebuildsInstance)
            {
                EnsureAABBConfig(usesTable);

                // Assigned here rather than inside EnsureAABBConfig: on a settle tick Render() never
                // ran, so the config was never invalidated and still carries the previous tick's flag.
                // AABBconfig.dynamicGeometry = wantsDynamicGeometry;

                AS.RemoveInstance(sectorASHandle);
                sectorASHandle = usesTable
                    ? AS.AddInstance(AABBconfig, objectToWorld, (uint)instanceSlot)
                    : AS.AddInstance(AABBconfig, objectToWorld);
                instanceIsDynamic = wantsDynamicGeometry;
                hasRenderable = true;

                if (!usesTable)
                {
                    AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
                }
            }
            else if (retracksInstance)
            {
                AS.UpdateInstanceTransform(sectorASHandle, objectToWorld);

                if (!usesTable)
                {
                    AS.UpdateInstancePropertyBlock(sectorASHandle, matProps);
                }
            }
            else if (republishesRecord && !usesTable)
            {
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
        /// <param name="usesTable">
        /// True in table mode. The config then carries no material properties: a property block is
        /// exactly the local root arguments that mode exists to remove.
        /// </param>
        /// <remarks>
        /// dynamicGeometry is deliberately not set here — it is per-registration state decided by
        /// the caller, and this method no-ops on the settle tick.
        /// </remarks>
        private void EnsureAABBConfig(bool usesTable)
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

            if (!usesTable)
            {
                AABBconfig.materialProperties = EnsureMaterialProperties();
            }
        }

        /// <summary>
        /// Schedules a Burst job to generate render data from dirty voxel data.
        /// Must be followed by ApplyCompletedRenderJob() and UploadBricks() in the same frame.
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
            // A sector that has never been touched, or one whose records the pool dropped, has to
            // emit every brick rather than only the ones the dirty flags name.
            bool fullRebuild = needsFullRebuild || !HostBufferInitialized;
            if (shouldRemove || sector.IsRendererEmpty || (!fullRebuild && !sector.IsRendererRequireUpdate))
            {
                return;
            }

            PreallocateBuffers(sector.Get());

            // Job generating renderer buffers
            rendererJob = new GenerateSectorRenderDataJob()
            {
                forceFullUpload = fullRebuild,
                sectorHandle = sector,
                neighbors = neighborHandle,
#if !CAELIX_RENDER_DISABLE_CULLING
                rendererBrickMap = rendererBrickMap,
#endif
                aabbBuffer = hostAABBBuffer,
                stagingWords = new NativeList<int>(BRICK_DATA_LENGTH * 16, Allocator.TempJob),
                stagingSlots = new NativeList<int>(16, Allocator.TempJob),
                syncRecord = new NativeArray<int>(1, Allocator.TempJob)
            };

            stagingWords = rendererJob.stagingWords;
            stagingSlots = rendererJob.stagingSlots;
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
            // A job may still be in flight when a view despawns mid-frame; its syncRecord and its
            // staging lists are TempJob allocations this renderer owns.
            if (jobScheduled)
            {
                jobHandle.Complete();
                rendererJob.syncRecord.Dispose();
                jobScheduled = false;
            }

            DisposeStaging();

            if (hostAABBBuffer.IsCreated) hostAABBBuffer.Dispose();
#if !CAELIX_RENDER_DISABLE_CULLING
            if (rendererBrickMap.IsCreated) rendererBrickMap.Dispose();
#endif

            aabbBuffer?.Dispose();
            brickBuffer?.Dispose();
        }
    }
}
