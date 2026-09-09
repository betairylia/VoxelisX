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
    /// The renderer's own grouping of brick keys: one acceleration-structure instance covers a
    /// cube of <see cref="BricksPerAxis"/> bricks per axis.
    /// </summary>
    /// <remarks>
    /// The group size belongs to the renderer, not to storage. Shift 4 is fixed for now because
    /// the brick info word packs the group-local brick index in 12 bits
    /// (<c>BRICK_INFO_ABSOLUTE_INDEX_MASK 0xFFF</c> in <c>CaelixBrickTrace.hlsl</c>), so a larger
    /// group needs a shader-side layout change too.
    /// </remarks>
    public static class RenderGroup
    {
        /// <summary>Bits a brick key is shifted right by to get its group key.</summary>
        public const int Shift = 4;

        /// <summary>Bricks along one axis of a group (16).</summary>
        public const int BricksPerAxis = 1 << Shift;

        /// <summary>Mask that extracts a brick's position inside its group (0-15 per axis).</summary>
        public const int Mask = BricksPerAxis - 1;

        /// <summary>Bricks in one group (16 * 16 * 16 = 4096).</summary>
        public const int BricksInGroup = BricksPerAxis * BricksPerAxis * BricksPerAxis;

        /// <summary>The group a brick key belongs to. Negative keys are handled.</summary>
        public static int3 Of(int3 key) => key >> Shift;

        /// <summary>Position of a brick inside its own group (0-15 per axis).</summary>
        public static int3 LocalBrick(int3 key) => key & Mask;

        /// <summary>Flat index of a group-local brick position, x-fastest. Matches the shader's decode.</summary>
        public static int LocalBrickIdx(int3 local) => local.x | (local.y << Shift) | (local.z << (2 * Shift));

        /// <summary>The lowest brick key of a group.</summary>
        public static int3 FirstKey(int3 group) => group << Shift;

        /// <summary>The highest brick key of a group, inclusive.</summary>
        public static int3 LastKey(int3 group) => (group << Shift) + Mask;

        /// <summary>Entity-local block position of a group's first block: the instance's translation.</summary>
        public static int3 BlockOrigin(int3 group) => BrickKey.ToBlockOrigin(FirstKey(group));
    }

    /// <summary>
    /// Inline ray query counterpart of <see cref="SectorRenderer"/>: prepares one render group's
    /// data and keeps its acceleration-structure instance up to date.
    /// </summary>
    /// <remarks>
    /// The render-data job, the AABB buffer rules and the RTAS caching rules are the same as
    /// <see cref="SectorRenderer"/>'s — read that class's comments, in particular why the AABB
    /// buffer is thrown away and reallocated whenever a tight box moves. Three things differ:
    /// <list type="bullet">
    /// <item>work arrives as a slice of the entity's per-cycle change list, keyed by brick key,
    /// rather than as a sweep of one sector's 4096 flags;</item>
    /// <item>brick records go into the shared <see cref="CaelixBrickPool"/> instead of a per-sector
    /// buffer, because a ray query kernel has no shader table and therefore no per-instance
    /// binding;</item>
    /// <item>the previous transform and the group hash seed travel through
    /// <see cref="CaelixRayQueryInstanceTable"/>, addressed by the RTAS instance ID, instead of a
    /// per-instance material property block.</item>
    /// </list>
    /// </remarks>
    public class RayQueryGroupRenderer : IDisposable
    {
        private readonly Material groupMaterial;
        private readonly int groupHashSeed;

        private NativeList<SectorRenderer.AABB> hostAABBBuffer;
        private SparseBrickIdTable rendererBrickMap;

        /// <summary>
        /// The brick records the last render job rewrote, plus the slot each of them belongs in.
        /// Created per job, consumed and disposed by <see cref="UploadBricks"/>.
        /// </summary>
        /// <remarks>
        /// There is no host copy of the group's records: only the bricks a job actually touched
        /// ever exist on the CPU, and only until they have been scattered into the pool.
        /// </remarks>
        private NativeList<int> stagingWords;
        private NativeList<int> stagingSlots;

        private GraphicsBuffer aabbBuffer;

        /// <summary>This group's slice of the brick pool. Null until the first allocation.</summary>
        /// <remarks>
        /// A class the pool owns and mutates: compacting a page rewrites the offset in place. Read
        /// its fields fresh every use rather than caching them.
        /// </remarks>
        private CaelixBrickPool.Handle poolHandle;

        /// <summary>
        /// Set when the group's records are gone and every brick has to be generated again: the
        /// pool had no room, so the range — and with it everything the pool would have carried over
        /// — was dropped.
        /// </summary>
        private bool needsFullRebuild;

        /// <summary>Slot in the instance table, and therefore this group's RTAS instance ID.</summary>
        private int instanceSlot = -1;

        /// <summary>Word offset and page of the range the instance record currently names; -1 = never published.</summary>
        private int publishedBrickBase = -1;
        private int publishedPage = -1;

        private int groupASHandle;
        private bool hasRenderable;
        private bool isDirty;
        private bool shouldRemove;

        private RayTracingAABBsInstanceConfig AABBconfig;
        private Matrix4x4 previousObjectToWorld;
        private bool hasPreviousObjectToWorld;

        // Temp variables used in the render process. RenderEmitJob and ApplyCompletedRenderJob must
        // run in the same frame / tick.
        private JobHandle jobHandle;
        private GenerateGroupRenderDataJob rendererJob;
        private bool jobScheduled;

        /// <summary>
        /// Number of brick slots the group currently occupies.
        /// </summary>
        /// <remarks>
        /// Always taken from the sparse brick map. Unlike <see cref="SectorRenderer"/> this class
        /// does not honour <c>CAELIX_RENDER_DISABLE_CULLING</c>: the pool addresses bricks by the
        /// renderer's own compacted ids, which only exist with culling on.
        /// </remarks>
        public int BrickBufferSize => rendererBrickMap.IsCreated ? rendererBrickMap.Capacity : 0;

        /// <summary>
        /// Number of bricks the group actually renders. Zero means the scene renderer can drop it:
        /// nothing points at its pool range or its instance any more.
        /// </summary>
        public int RendererBrickCount => rendererBrickMap.IsCreated ? rendererBrickMap.Count : 0;

        /// <summary>
        /// True while the group's records are gone and every brick has to be generated again. The
        /// scene renderer emits for such a group even when this cycle's changes name none of it.
        /// </summary>
        public bool NeedsFullRebuild => needsFullRebuild;

        /// <summary>
        /// Gets the estimated host memory usage in bytes for this renderer's buffers: the AABB list
        /// only, because brick records are never mirrored on the host.
        /// </summary>
        public ulong MemoryUsage =>
            (ulong)(hostAABBBuffer.IsCreated ? hostAABBBuffer.Capacity * sizeof(float) * 6 : 0);

        /// <summary>Gets the estimated VRAM usage in bytes: the AABB buffer plus this group's pool range.</summary>
        public ulong VRAMUsage =>
            (ulong)(RenderGroup.BricksInGroup * 24 +
                    (poolHandle?.CapacityBricks ?? 0) * SectorRenderer.BRICK_DATA_LENGTH * 4);

        private bool HostBufferInitialized => hostAABBBuffer.IsCreated;

        /// <summary>
        /// Constructs a renderer for one (view, render group) pair.
        /// </summary>
        /// <param name="entity">The view the group belongs to; part of the hash seed.</param>
        /// <param name="groupKey">The group's position in group coordinates.</param>
        /// <param name="material">
        /// Material handed to <see cref="RayTracingAABBsInstanceConfig"/>. The ray query path never
        /// runs its hit group, but the config requires one.
        /// </param>
        public RayQueryGroupRenderer(EntityView entity, int3 groupKey, Material material)
        {
            GroupKey = groupKey;
            groupMaterial = material;
            groupHashSeed = unchecked((int)ComputeGroupHashSeed(entity, groupKey));
        }

        /// <summary>The group this renderer draws, in group coordinates.</summary>
        public int3 GroupKey { get; }

        private static uint HashUInt(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }

        private static uint ComputeGroupHashSeed(EntityView entity, int3 groupKey)
        {
            uint seed = (uint)entity.Guid.GetHashCode();
            seed ^= (uint)groupKey.x * 0x9E3779B9u;
            seed ^= (uint)groupKey.y * 0x85EBCA6Bu;
            seed ^= (uint)groupKey.z * 0xC2B2AE35u;
            return HashUInt(seed);
        }

        /// <summary>
        /// Schedules the Burst job that turns this cycle's changes into render data.
        /// Must be followed by <see cref="ApplyCompletedRenderJob"/> in the same frame.
        /// </summary>
        /// <param name="data">The entity's storage. Bricks are bound by key inside the job.</param>
        /// <param name="sortedChanges">
        /// The cycle's changes, reordered so that one group's entries are contiguous. Several group
        /// jobs read it at once, which is why the job takes it read-only.
        /// </param>
        /// <param name="start">Index of this group's first entry in <paramref name="sortedChanges"/>.</param>
        /// <param name="count">Number of entries that belong to this group.</param>
        public void RenderEmitJob(
            VoxelEntityData data, NativeArray<BrickChange> sortedChanges, int start, int count)
        {
            if (shouldRemove || (count <= 0 && !needsFullRebuild))
            {
                return;
            }

            PreallocateBuffers();

            rendererJob = new GenerateGroupRenderDataJob()
            {
                data = data,
                changes = sortedChanges,
                start = start,
                count = count,
                groupKey = GroupKey,
                forceFullUpload = needsFullRebuild,
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

        /// <summary>Creates the host buffers on the first emit.</summary>
        /// <remarks>
        /// There is no brick count to size them from: the job discovers the group's bricks as it
        /// walks the changes, and grows the AABB list whenever the brick map extends.
        /// </remarks>
        private void PreallocateBuffers()
        {
            if (HostBufferInitialized)
            {
                return;
            }

            hostAABBBuffer = new NativeList<SectorRenderer.AABB>(0, Allocator.Persistent);
            rendererBrickMap = SparseBrickIdTable.New(Allocator.Persistent);
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return jobScheduled;
        }

        /// <summary>
        /// Consumes the completed render job: replaces the AABB buffer when a tight box moved and
        /// resizes this group's pool range.
        /// </summary>
        /// <remarks>
        /// The staged brick records are deliberately written later, in <see cref="UploadBricks"/>:
        /// resizing a range can grow a pool page, which replaces its <see cref="GraphicsBuffer"/>
        /// and re-packs every other range on it. Every group must therefore learn its final range
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
                    GraphicsBuffer.Target.Structured, RenderGroup.BricksInGroup, 24);

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
                // succeeds RenderModifyAS skips this group, so it is simply not drawn.
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
        /// Adds this group's staged brick records to the frame's scatter batch. Runs after every
        /// group has settled its range, so a pool growth caused by one group is seen by all of
        /// them.
        /// </summary>
        /// <remarks>
        /// Staged, not dispatched: the renderer flushes the whole frame's batch once, after every
        /// group has staged. See <see cref="CaelixBrickGpuOps"/> for why writing and dispatching
        /// per group hung the device.
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
                    "RayQueryGroupRenderer: the group has more bricks than the pool range reserved for it.");

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
        /// Updates the acceleration structure and the instance record with this group's state.
        /// </summary>
        /// <remarks>
        /// Same three outcomes as <see cref="SectorRenderer.RenderModifyAS"/>: rebuild the instance
        /// when geometry changed, retrack it while the entity moves (or for the one frame its
        /// motion vectors must settle), otherwise do nothing.
        /// </remarks>
        public void RenderModifyAS(
            ref RayTracingAccelerationStructure AS,
            EntityView entity,
            int3 groupKey,
            CaelixRayQueryInstanceTable table)
        {
            Matrix4x4 objectToWorld =
                entity.LocalToWorld *
                Matrix4x4.Translate(RenderGroup.BlockOrigin(groupKey).ToVector3Int());

            // Set for the frame an entity flips to static (including the initial flip on a
            // born-static body). Collapsing prev onto the current transform zeroes the motion
            // vectors once, so the denoiser stops reprojecting a body that will never move again.
            // CaelixRayQueryRenderer clears the flag after every group of the entity consumed it.
            bool resetsMotionVectors = entity.ShouldResetMotionVectors;
            Matrix4x4 prevObjectToWorld = (hasPreviousObjectToWorld && !resetsMotionVectors)
                ? previousObjectToWorld
                : objectToWorld;

            // A dirty group with no renderable bricks is legitimate (freshly loaded, or fully
            // culled) and RayTracingAABBsInstanceConfig throws on aabbCount == 0, so skip it; the
            // group re-dirties and retries once real geometry appears. A group the pool could not
            // find room for is skipped the same way.
            bool hasPool = poolHandle != null && poolHandle.IsValid;

            // Fall back to the last published range while the pool has no room for this group, so
            // a retrack cannot overwrite a live instance's record with a null range.
            int brickBaseWords = hasPool
                ? poolHandle.OffsetBricks * SectorRenderer.BRICK_DATA_LENGTH
                : publishedBrickBase;
            int brickPage = hasPool ? poolHandle.Page : publishedPage;

            bool rebuildsInstance = isDirty && BrickBufferSize > 0 && hasPool;
            bool retracksInstance = hasRenderable && (!entity.IsStatic || resetsMotionVectors);
            // A range that moved — this group resized, or another one grew and compacted the page —
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

                    table.Set(instanceSlot, prevObjectToWorld, brickBaseWords, brickPage, (uint)groupHashSeed);
                    publishedBrickBase = brickBaseWords;
                    publishedPage = brickPage;
                }
            }

            if (rebuildsInstance)
            {
                EnsureAABBConfig();

                if (hasRenderable)
                {
                    AS.RemoveInstance(groupASHandle);
                }

                groupASHandle = AS.AddInstance(AABBconfig, objectToWorld, (uint)instanceSlot);
                hasRenderable = true;
            }
            else if (retracksInstance)
            {
                AS.UpdateInstanceTransform(groupASHandle, objectToWorld);
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
                aabbBuffer, BrickBufferSize, false, groupMaterial)
            {
                dynamicGeometry = false,
                accelerationStructureBuildFlagsOverride = true,
                accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace,
            };
        }

        /// <summary>Marks this group for removal from the acceleration structure.</summary>
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
                AS.RemoveInstance(groupASHandle);
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
