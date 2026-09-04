using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Client;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Scene renderer for the inline ray query backend: owns the acceleration structure, the shared
    /// brick pool and the instance table that <c>CaelixPathTraceRQ.compute</c> reads.
    /// </summary>
    /// <remarks>
    /// Sibling of <see cref="CaelixRenderer"/>, which drives the DXR pipeline backend. Both build
    /// the same kind of RTAS from the same render-data job; this one publishes per-instance data
    /// through GPU buffers rather than a shader table, because a ray query has no hit group.
    /// <para>
    /// Enable exactly ONE of the two at a time. <see cref="EntityView.SectorsToRemove"/> is a queue
    /// that its consumer drains and <see cref="EntityView.ShouldResetMotionVectors"/> is a flag its
    /// consumer clears, so two renderers reading the same client world would steal each other's
    /// events.
    /// </para>
    /// </remarks>
    public class CaelixRayQueryRenderer : MonoBehaviour
    {
        /// <summary>The host whose client world this renderer draws. Found in the scene when empty.</summary>
        [SerializeField] private CaelixHost host;

        /// <summary>
        /// Material handed to every sector's <see cref="RayTracingAABBsInstanceConfig"/>. The ray
        /// query path never runs its hit group, but the config requires a material.
        /// </summary>
        public Material brickMat;

        /// <summary>When enabled, automatically calls <see cref="Tick"/> every frame.</summary>
        [SerializeField] private bool autoTick = false;

        [Header("Brick Pool")]
        [SerializeField, Tooltip("Upper size of one brick pool page, in bricks (1096 bytes each). Clamped to the platform's maximum buffer size. Lower it only to test paging.")]
        private int pageCapacityLimitBricks = CaelixBrickPool.DefaultPageCapacityLimitBricks;

        /// <summary>Debug field showing the current number of instances in the acceleration structure.</summary>
        [Header("Debug Utils")] public int instanceCount;

        /// <summary>Debug field showing how many pages the pool has open.</summary>
        public int poolPages;

        /// <summary>Debug field showing how many bricks are reserved by a live sector range.</summary>
        public int poolLiveBricks;

        /// <summary>Debug field showing how many bricks the pool's page buffers can hold together.</summary>
        public int poolCapacityBricks;

        private ClientWorld source;

        /// <summary>Maps (view, sectorPos) → renderer, so render state stays separate from entity data.</summary>
        private readonly Dictionary<(EntityView entity, int3 sectorPos), RayQuerySectorRenderer> sectorRenderers = new();

        private readonly List<(EntityView entity, int3 sectorPos)> removalScratch = new();

        private RayTracingAccelerationStructure _voxelScene;

        /// <summary>
        /// The acceleration structure containing all voxel geometry. Created on first access.
        /// </summary>
        public RayTracingAccelerationStructure voxelScene
        {
            get
            {
                if (_voxelScene == null) ReloadAS();
                return _voxelScene;
            }
        }

        /// <summary>The shared brick records, in up to four pages bound as <c>g_bricks0..3</c>.</summary>
        public CaelixBrickPool Pool { get; private set; }

        /// <summary>The per-instance record buffer bound as <c>g_Instances</c>.</summary>
        public CaelixRayQueryInstanceTable Instances { get; private set; }

        /// <summary>Entries in <see cref="MaterialTable"/>: one per 16-bit block ID.</summary>
        public const int MaterialTableEntries = 65536;

        /// <summary>Byte size of the HLSL <c>VoxelMaterial</c> struct: albedo, emission, smoothness, metallic, IOR, extinction.</summary>
        public const int MaterialStride = 40;

        /// <summary>
        /// One <c>VoxelMaterial</c> per 16-bit block ID, bound as <c>g_Materials</c>. The trace
        /// kernel cannot carry the static HLSL material tables (D3D12 refuses the pipeline state),
        /// so the G-buffer stage bakes them into this buffer once, then reads from it.
        /// </summary>
        public GraphicsBuffer MaterialTable { get; private set; }

        /// <summary>False until the G-buffer stage has recorded the bake into <see cref="MaterialTable"/>.</summary>
        public bool MaterialsBaked { get; set; }

        /// <summary>Current frame ID for rendering. Incremented each <see cref="Tick"/>.</summary>
        public uint frameId { get; private set; }

        /// <summary>The client world this renderer draws, once resolved.</summary>
        public ClientWorld Source => source;

        /// <summary>
        /// True while every GPU resource the trace needs exists: between Awake/Tick and OnDisable. False
        /// in edit mode (no Awake) and while disabled, which is what keeps the G-buffer stage from
        /// dispatching against null buffers and logging "Property ... is not set" every frame.
        /// </summary>
        public bool HasResources => isActiveAndEnabled && Pool != null && Instances != null
            && MaterialTable != null && _voxelScene != null;

        private void Awake()
        {
            if (!SystemInfo.supportsInlineRayTracing)
            {
                Debug.LogWarning(
                    "Caelix: this device reports no inline ray tracing support. " +
                    "CaelixPathTraceRQ.compute cannot run; use the DXR backend instead.", this);
            }

            Pool ??= new CaelixBrickPool(4096, pageCapacityLimitBricks);
            Instances ??= new CaelixRayQueryInstanceTable();
            EnsureMaterialTable();
            ReloadAS();
        }

        /// <summary>Creates the acceleration structure for voxel rendering.</summary>
        private void CreateRayTracingAccelerationStructure()
        {
            if (_voxelScene == null)
            {
                RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings();
                settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
                settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
                settings.layerMask = -1;
                _voxelScene = new RayTracingAccelerationStructure(settings);
                Debug.Log($"voxAS (ray query): {_voxelScene}");
            }
        }

        /// <summary>
        /// Reinitializes the acceleration structure and resets the frame counter.
        /// Can be called from the context menu in the Unity Editor.
        /// </summary>
        [ContextMenu("Re-init voxAS")]
        public void ReloadAS()
        {
            CreateRayTracingAccelerationStructure();
            frameId = 0;
        }

        /// <summary>Binds to the host's client world. Safe to call every frame.</summary>
        private bool EnsureSource()
        {
            if (source != null)
            {
                return true;
            }

            if (host == null)
            {
                host = CaelixHost.Any;
            }

            if (host == null)
            {
                return false;
            }

            host.EnsureInitialized();
            source = host.ClientWorld;
            if (source != null)
            {
                source.ViewDespawning += OnViewDespawning;
            }

            return source != null;
        }

        private void OnViewDespawning(EntityView view)
        {
            removalScratch.Clear();
            foreach (var kvp in sectorRenderers)
            {
                if (kvp.Key.entity == view)
                {
                    removalScratch.Add(kvp.Key);
                }
            }

            for (int i = 0; i < removalScratch.Count; i++)
            {
                RayQuerySectorRenderer renderer = sectorRenderers[removalScratch[i]];
                renderer.MarkRemove();
                renderer.RemoveMe(ref _voxelScene, Instances, Pool);
                sectorRenderers.Remove(removalScratch[i]);
            }
        }

        private void Update()
        {
            if (autoTick) { Tick(); }
        }

        /// <summary>
        /// Performs one render update tick for all voxel entity views.
        /// </summary>
        /// <remarks>
        /// Pass 1 emits the render jobs and drops sectors that went away. Pass 2a consumes the
        /// finished jobs and settles every sector's pool range. Pass 2b uploads bricks and updates
        /// the acceleration structure. 2a and 2b are separate loops on purpose: 2a can grow the
        /// pool, which replaces its buffer, and 2b is what re-uploads every sector whose generation
        /// then became stale.
        /// </remarks>
        public void Tick()
        {
            if (!EnsureSource())
            {
                return;
            }

            if (_voxelScene == null)
            {
                ReloadAS();
            }

            Pool ??= new CaelixBrickPool(4096, pageCapacityLimitBricks);
            Instances ??= new CaelixRayQueryInstanceTable();
            EnsureMaterialTable();

            frameId += 1;
            instanceCount = (int)voxelScene.GetInstanceCount();
            JobHandle renderJobs = default;
            bool hasRenderJobs = false;

            IReadOnlyList<EntityView> views = source.Views;

            // Pass 1: Emit jobs & remove unused sectors
            for (int v = 0; v < views.Count; v++)
            {
                EntityView view = views[v];

                // Handle sector removal
                while (view.SectorsToRemove.TryDequeue(out int3 sectorPos))
                {
                    var key = (view, sectorPos);
                    if (sectorRenderers.TryGetValue(key, out RayQuerySectorRenderer removed))
                    {
                        removed.MarkRemove();
                        removed.RemoveMe(ref _voxelScene, Instances, Pool);
                        sectorRenderers.Remove(key);
                    }
                }

                // The removal queue is the fast path, but a sector can also vanish without one
                // (a replicated world rebuild, for instance). Sweeping the tracked keys against the
                // live sector map is what keeps the pool from leaking ranges in that case.
                removalScratch.Clear();
                foreach (var kvp in sectorRenderers)
                {
                    if (kvp.Key.entity == view && !view.Data.sectors.ContainsKey(kvp.Key.sectorPos))
                    {
                        removalScratch.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < removalScratch.Count; i++)
                {
                    RayQuerySectorRenderer stale = sectorRenderers[removalScratch[i]];
                    stale.MarkRemove();
                    stale.RemoveMe(ref _voxelScene, Instances, Pool);
                    sectorRenderers.Remove(removalScratch[i]);
                }

                // Emit render jobs for all sectors
                foreach (var kvp in view.Data.sectors)
                {
                    int3 sectorPos = kvp.Key;

                    var key = (view, sectorPos);
                    if (!sectorRenderers.ContainsKey(key))
                    {
                        sectorRenderers[key] = new RayQuerySectorRenderer(view, sectorPos, brickMat);
                    }

                    RayQuerySectorRenderer renderer = sectorRenderers[key];
                    renderer.RenderEmitJob(kvp.Value, view.Data.sectorNeighbors[sectorPos]);
                    if (renderer.TryGetScheduledJobHandle(out JobHandle sectorJob))
                    {
                        renderJobs = JobHandle.CombineDependencies(renderJobs, sectorJob);
                        hasRenderJobs = true;
                    }
                }
            }

            if (hasRenderJobs)
            {
                renderJobs.Complete();
            }

            // Pass 2a: consume the finished jobs. May grow the pool.
            for (int v = 0; v < views.Count; v++)
            {
                EntityView view = views[v];

                foreach (var kvp in view.Data.sectors)
                {
                    var key = (view, kvp.Key);
                    if (!sectorRenderers.TryGetValue(key, out RayQuerySectorRenderer renderer)) continue;

                    renderer.ApplyCompletedRenderJob(Pool);
                }
            }

            // Pass 2b: upload bricks against the final pool, then update the acceleration structure.
            for (int v = 0; v < views.Count; v++)
            {
                EntityView view = views[v];

                foreach (var kvp in view.Data.sectors)
                {
                    int3 sectorPos = kvp.Key;
                    ref Sector sector = ref kvp.Value.Get();

                    var key = (view, sectorPos);
                    if (!sectorRenderers.TryGetValue(key, out RayQuerySectorRenderer renderer)) continue;

                    renderer.UploadBricks(Pool);
                    renderer.RenderModifyAS(ref _voxelScene, view, sectorPos, Instances);

                    // Call sector tick
                    sector.ReorderBricks();
                }

                // Every sector of this view has consumed the reset; its motion vectors are settled.
                view.ShouldResetMotionVectors = false;
            }

            Instances.Flush();

            poolPages = Pool.PageCount;
            poolLiveBricks = Pool.TotalLiveBricks;
            poolCapacityBricks = Pool.TotalCapacityBricks;
        }

        private void EnsureMaterialTable()
        {
            if (MaterialTable != null && MaterialTable.IsValid())
            {
                return;
            }

            MaterialTable = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, MaterialTableEntries, MaterialStride);
            // A fresh buffer holds garbage until the G-buffer stage bakes into it.
            MaterialsBaked = false;
        }

        /// <summary>Releases all resources when the renderer is disabled.</summary>
        private void OnDisable()
        {
            ReleaseResources();
        }

        /// <summary>Releases all GPU resources.</summary>
        private void ReleaseResources()
        {
            if (source != null)
            {
                source.ViewDespawning -= OnViewDespawning;
                source = null;
            }

            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.Dispose();
            }

            sectorRenderers.Clear();

            _voxelScene?.Dispose();
            _voxelScene = null;

            Instances?.Dispose();
            Instances = null;

            MaterialTable?.Dispose();
            MaterialTable = null;
            MaterialsBaked = false;

            Pool?.Dispose();
            Pool = null;
        }
    }
}
