using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;
using Caelix;
using Caelix.Client;
using Caelix.Rendering;
using Caelix.Rendering.RayQuery;
using Caelix.Utils;

/// <summary>
/// Main rendering system for Caelix. Manages ray tracing acceleration structures
/// and coordinates rendering of all voxel entity views of a client world.
/// </summary>
/// <remarks>
/// This renderer uses Unity's ray tracing pipeline to render voxel data efficiently.
/// It maintains a ray tracing acceleration structure (RTAS) containing all voxel sectors,
/// and coordinates the update process for every <see cref="EntityView"/> of its source
/// <see cref="ClientWorld"/>. Rendering state is managed separately from entity data.
/// </remarks>
// [ExecuteInEditMode]
public class CaelixRenderer : MonoBehaviour
{
    /// <summary>The host whose client world this renderer draws. Found in the scene when empty.</summary>
    [FormerlySerializedAs("world")]
    [SerializeField] private CaelixHost host;

    private ClientWorld source;

    /// <summary>
    /// Maps (view, sectorPos) → SectorRenderer for tracking rendering state independently from entity data.
    /// </summary>
    private Dictionary<(EntityView entity, int3 sectorPos), SectorRenderer> sectorRenderers = new();

    private readonly List<(EntityView entity, int3 sectorPos)> removalScratch = new();

    /// <summary>
    /// Gets the ray tracing acceleration structure containing all voxel geometry.
    /// Automatically creates the structure if it doesn't exist.
    /// </summary>
    public RayTracingAccelerationStructure voxelScene
    {
        get
        {
            if(_voxelScene == null) ReloadAS();
            return _voxelScene;
        }
    }

    private RayTracingAccelerationStructure _voxelScene;

    /// <summary>
    /// Internal structure for testing RTAS instances.
    /// </summary>
    internal struct TestSector
    {
        internal int handle;
        internal Matrix4x4 mat;
    }

    private List<TestSector> handles = new();

    /// <summary>
    /// Material used for rendering voxel bricks.
    /// </summary>
    public Material brickMat;

    [SerializeField, Tooltip("PerSector: one g_bricks buffer per sector, bound through the hit group's property block. SharedPool: the same CaelixBrickPool the inline ray query backend uses, so the two backends differ only in dispatch model. SharedPoolInstanceTable: that pool, plus per-instance data through the g_Instances table, so the hit group has no local root arguments either.")]
    private CaelixBrickStorage brickStorage = CaelixBrickStorage.PerSector;

    [Header("Brick Pool")]
    [SerializeField, Tooltip("Upper size of one brick pool page, in bricks (1096 bytes each). Clamped to the platform's maximum buffer size. Keep it under 512 MB (2^18 bricks): the hit group reads its page through a buffer view, and D3D12 views stop at 2^27 elements. Pool storage only.")]
    private int pageCapacityLimitBricks = CaelixBrickPool.DefaultDxrPageCapacityLimitBricks;

    /// <summary>True when this renderer stores its bricks in <see cref="Pool"/> rather than per sector.</summary>
    public bool UsesBrickPool => brickStorage != CaelixBrickStorage.PerSector;

    /// <summary>
    /// True when per-instance data travels through <see cref="Instances"/> rather than a property
    /// block, so the hit group's shader records carry no local root arguments.
    /// </summary>
    public bool UsesInstanceTable => brickStorage == CaelixBrickStorage.SharedPoolInstanceTable;

    /// <summary>
    /// The shared brick records, in up to <see cref="CaelixBrickPool.MaxNamedPages"/> pages bound as
    /// <c>g_bricks0..15</c>. Null in <see cref="CaelixBrickStorage.PerSector"/> storage, where each
    /// sector owns its own buffer.
    /// </summary>
    public CaelixBrickPool Pool { get; private set; }

    /// <summary>
    /// The per-instance record buffer bound as <c>g_Instances</c>, the same table the inline ray
    /// query backend uses. Null unless <see cref="UsesInstanceTable"/>.
    /// </summary>
    public CaelixRayQueryInstanceTable Instances { get; private set; }

    /// <summary>
    /// Private instance of <see cref="brickMat"/> with the storage mode's keyword enabled.
    /// </summary>
    /// <remarks>
    /// An instance rather than the asset: enabling a keyword on the asset would dirty it on disk,
    /// and the per-sector mode has to keep running the same material with every keyword off.
    /// </remarks>
    private Material pooledBrickMat;

    /// <summary>The keyword <see cref="pooledBrickMat"/> was created with; null when there is none.</summary>
    private string pooledBrickMatKeyword;

    /// <summary>
    /// Current frame ID for rendering. Incremented each Tick().
    /// </summary>
    public uint frameId { get; private set; }

    /// <summary>
    /// Debug field showing the current number of instances in the acceleration structure.
    /// </summary>
    [Header("Debug Utils")] public int instanceCount;

    /// <summary>Debug field showing how many pages the pool has open. SharedPool storage only.</summary>
    public int poolPages;

    /// <summary>Debug field showing how many bricks are reserved by a live sector range.</summary>
    public int poolLiveBricks;

    /// <summary>Debug field showing how many bricks the pool's page buffers can hold together.</summary>
    public int poolCapacityBricks;

    /// <summary>The client world this renderer draws, once resolved.</summary>
    public ClientWorld Source => source;

    /// <summary>
    /// Creates the ray tracing acceleration structure for voxel rendering.
    /// </summary>
    private void CreateRayTracingAccelerationStructure()
    {
        if (_voxelScene == null)
        {
            RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings();
            settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
            settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
            settings.layerMask = -1;
            _voxelScene = new RayTracingAccelerationStructure(settings);
            Debug.Log($"voxAS: {_voxelScene}");
        }
    }

    private void Awake()
    {
        EnsureBrickStorage();
        ReloadAS();
    }

    /// <summary>
    /// Creates the brick pool, the instance table and the keyword material instance the current
    /// storage mode needs, then points every sector renderer at that material. Safe to call every
    /// frame.
    /// </summary>
    private void EnsureBrickStorage()
    {
        if (!UsesBrickPool)
        {
            SectorRenderer.sectorMaterial = brickMat;
            return;
        }

        Pool ??= new CaelixBrickPool(4096, pageCapacityLimitBricks);

        if (UsesInstanceTable)
        {
            Instances ??= new CaelixRayQueryInstanceTable();
        }

        string keyword = UsesInstanceTable ? "CAELIX_BRICK_POOL_TABLE" : "CAELIX_BRICK_POOL";
        if (pooledBrickMat != null && pooledBrickMatKeyword != keyword)
        {
            // The storage mode changed at run time. The two keywords select different per-instance
            // plumbing, so the instance is built again rather than re-keyworded: every sector's AABB
            // config holds this material, and a fresh one makes the next rebuild pick it up.
            CoreUtils.Destroy(pooledBrickMat);
            pooledBrickMat = null;
        }

        if (pooledBrickMat == null && brickMat != null)
        {
            pooledBrickMat = new Material(brickMat)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            pooledBrickMat.EnableKeyword(keyword);
            pooledBrickMatKeyword = keyword;
        }

        SectorRenderer.sectorMaterial = pooledBrickMat;
    }

    /// <summary>The pool sector renderers write into, or null in per-sector storage.</summary>
    private CaelixBrickPool ActivePool => UsesBrickPool ? Pool : null;

    /// <summary>
    /// The instance table sector renderers publish into, or null in the storage modes that use a
    /// per-instance property block instead. Doubles as the "table mode" flag they test.
    /// </summary>
    private CaelixRayQueryInstanceTable ActiveInstances => UsesInstanceTable ? Instances : null;

    /// <summary>Binds to the host's client world. Safe to call every frame.</summary>
    private bool EnsureSource()
    {
        if (source != null && !source.IsDisposed)
        {
            return true;
        }

        if (host == null)
        {
            host = CaelixHost.Current;
        }

        if (host == null)
        {
            return false;
        }

        host.EnsureInitialized();
        SetSource(host.ClientWorld);
        return source != null;
    }

    /// <summary>Binds a replica and releases all resources belonging to the previous one.</summary>
    public void SetSource(ClientWorld world)
    {
        if (ReferenceEquals(source, world)) return;
        if (source != null)
        {
            source.ViewDespawning -= OnViewDespawning;
            source.SectorRemoving -= RemoveSectorRenderer;
            source.Owner.WorldRemoving -= OnWorldRemoving;
        }

        foreach (var renderer in sectorRenderers.Values)
        {
            renderer.MarkRemove();
            if (_voxelScene != null) renderer.RemoveMe(ref _voxelScene, ActivePool, ActiveInstances);
            else renderer.Dispose();
        }
        sectorRenderers.Clear();
        source = world != null && !world.IsDisposed ? world : null;
        if (source != null)
        {
            source.ViewDespawning += OnViewDespawning;
            source.SectorRemoving += RemoveSectorRenderer;
            source.Owner.WorldRemoving += OnWorldRemoving;
        }
    }

    private void OnWorldRemoving(ClientWorld world)
    {
        if (ReferenceEquals(source, world)) SetSource(null);
    }

    private void RemoveSectorRenderer(EntityView view, int3 sectorPos)
    {
        var key = (view, sectorPos);
        if (!sectorRenderers.TryGetValue(key, out SectorRenderer renderer)) return;
        renderer.MarkRemove();
        if (_voxelScene != null) renderer.RemoveMe(ref _voxelScene, ActivePool, ActiveInstances);
        else renderer.Dispose();
        sectorRenderers.Remove(key);
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
            var key = removalScratch[i];
            RemoveSectorRenderer(key.entity, key.sectorPos);
        }
    }

    /// <summary>
    /// Reinitializes the ray tracing acceleration structure and resets the frame counter.
    /// Can be called from the context menu in the Unity Editor.
    /// </summary>
    [ContextMenu("Re-init voxAS")]
    public void ReloadAS()
    {
        CreateRayTracingAccelerationStructure();
        frameId = 0;
    }

    /// <summary>
    /// Rebuilds the acceleration structure with every sector of every view.
    /// Can be called from the context menu in the Unity Editor.
    /// </summary>
    [ContextMenu("Render all")]
    public void RenderAll()
    {
        EnsureBrickStorage();
        if (!EnsureSource())
        {
            return;
        }

        CaelixBrickPool pool = ActivePool;
        CaelixRayQueryInstanceTable instances = ActiveInstances;
        _voxelScene.ClearInstances();
        IReadOnlyList<EntityView> views = source.Views;
        for (int v = 0; v < views.Count; v++)
        {
            EntityView view = views[v];
            foreach (var kvp in view.Data.sectors)
            {
                int3 sectorPos = kvp.Key;
                var key = (view, sectorPos);
                if (!sectorRenderers.ContainsKey(key))
                {
                    sectorRenderers[key] = new SectorRenderer(view, sectorPos);
                }

                sectorRenderers[key].RenderModifyAS(ref _voxelScene, view, sectorPos, pool, instances);
            }

            view.ShouldResetMotionVectors = false;
        }

        instances?.Flush();
        _voxelScene.Build();
    }

    private GraphicsBuffer aabbBuffer;
    public int numAABB;
    public Vector2Int repeat;
    public bool useRandomAABB = false;

    [ContextMenu("Test")]
    public void Test()
    {
        if (aabbBuffer != null && aabbBuffer.IsValid())
        {
            aabbBuffer.Release();
        }

        aabbBuffer =
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.None, numAABB, 24);

        List<Vector3> data = new List<Vector3>();
        for (int i = 0; i < numAABB; i++)
        {
            Vector3 rnd;
            if (useRandomAABB)
            {
                rnd = Random.insideUnitSphere * 20.0f;
            }
            else
            {
                int cY = i / Sector.SIZE_IN_BRICKS_SQUARED;
                int cX = i % Sector.SIZE_IN_BRICKS;
                int cZ = (i / Sector.SIZE_IN_BRICKS) % Sector.SIZE_IN_BRICKS;
                rnd = new Vector3(cX, cY, cZ);
            }

            data.Add(rnd);
            data.Add(rnd + Vector3.one);
        }

        aabbBuffer.SetData(data);

        RayTracingAABBsInstanceConfig AABBconfig = new RayTracingAABBsInstanceConfig(aabbBuffer, numAABB, false, brickMat);
        AABBconfig.accelerationStructureBuildFlags = RayTracingAccelerationStructureBuildFlags.PreferFastTrace;

        for (int i = 0; i < repeat.x; i++)
        {
            for (int j = 0; j < repeat.y; j++)
            {
                Vector3 offset = useRandomAABB
                    ? Vector3.forward * 50 * i + Vector3.left * 50 * j
                    : Vector3.forward * Sector.SIZE_IN_BRICKS * i + Vector3.left * Sector.SIZE_IN_BRICKS * j;
                handles.Add(
                    new TestSector()
                    {
                        handle = _voxelScene.AddInstance(AABBconfig, Matrix4x4.Translate(offset)),
                        mat = Matrix4x4.Translate(offset)
                    });
            }
        }

        _voxelScene.Build();
    }

    /// <summary>
    /// Releases all resources when the renderer is disabled.
    /// </summary>
    private void OnDisable()
    {
        ReleaseResources();
    }

    /// <summary>
    /// Releases all GPU resources.
    /// </summary>
    void ReleaseResources()
    {
        SetSource(null);

        if (aabbBuffer != null && aabbBuffer.IsValid())
        {
            aabbBuffer.Release();
        }

        // Sector renderers first: their pool ranges only mean anything while the pool is alive, and
        // their AABB configs still reference the keyword material instance.
        foreach (var kvp in sectorRenderers)
        {
            kvp.Value.Dispose();
        }

        sectorRenderers.Clear();
        _voxelScene?.Dispose();
        _voxelScene = null;

        Instances?.Dispose();
        Instances = null;

        Pool?.Dispose();
        Pool = null;

        if (pooledBrickMat != null)
        {
            CoreUtils.Destroy(pooledBrickMat);
            pooledBrickMat = null;
            pooledBrickMatKeyword = null;
        }
    }

    /// <summary>
    /// When enabled, automatically calls Tick() every frame.
    /// </summary>
    [SerializeField] private bool autoTick = false;

    void Update()
    {
        if(autoTick){ Tick(); }
    }

    /// <summary>
    /// Performs one render update tick for all voxel entity views.
    /// </summary>
    /// <remarks>
    /// Pass 1 emits the render jobs for all sectors and removes sectors marked for deletion, so the
    /// jobs run in parallel. Pass 2a consumes the finished jobs and settles every sector's brick
    /// storage. Pass 2b uploads bricks and updates the acceleration structure.
    /// <para>
    /// 2a and 2b are separate loops even in per-sector storage, so the CPU-side ordering does not
    /// depend on the storage mode. In pool storage the split is required: 2a can grow the pool,
    /// which replaces a page's buffer, and 2b is what re-uploads every sector whose generation then
    /// became stale.
    /// </para>
    /// </remarks>
    public void Tick()
    {
        SectorRenderer.sectorMaterial = brickMat;
        if (!EnsureSource())
        {
            return;
        }

        if (_voxelScene == null)
        {
            ReloadAS();
        }

        EnsureBrickStorage();
        CaelixBrickPool pool = ActivePool;
        CaelixRayQueryInstanceTable instances = ActiveInstances;

        frameId += 1;
        instanceCount = (int)voxelScene.GetInstanceCount();
        JobHandle renderJobs = default;
        bool hasRenderJobs = false;

        IReadOnlyList<EntityView> views = source.Views;

        // Pass 1: Emit jobs & Remove unused sectors
        for (int v = 0; v < views.Count; v++)
        {
            EntityView view = views[v];

            // Emit render jobs for all sectors
            foreach (var kvp in view.Data.sectors)
            {
                int3 sectorPos = kvp.Key;

                var key = (view, sectorPos);
                if (!sectorRenderers.ContainsKey(key))
                {
                    sectorRenderers[key] = new SectorRenderer(view, sectorPos);
                }

                SectorRenderer renderer = sectorRenderers[key];
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
                if (!sectorRenderers.TryGetValue(key, out SectorRenderer renderer)) continue;

                renderer.ApplyCompletedRenderJob(pool);
            }
        }

        // Pass 2b: upload bricks against the final storage, then update the acceleration structure.
        for (int v = 0; v < views.Count; v++)
        {
            EntityView view = views[v];

            foreach (var kvp in view.Data.sectors)
            {
                int3 sectorPos = kvp.Key;
                ref Sector sector = ref kvp.Value.Get();

                var key = (view, sectorPos);
                if (!sectorRenderers.TryGetValue(key, out SectorRenderer renderer)) continue;

                renderer.UploadBricks(pool);
                renderer.RenderModifyAS(ref _voxelScene, view, sectorPos, pool, instances);

                // Call sector tick
                sector.ReorderBricks();
            }

            // Every sector of this view has consumed the reset; its motion vectors are settled.
            view.ShouldResetMotionVectors = false;
        }

        // One upload for every record written above; a no-op when nothing changed.
        instances?.Flush();

        if (pool != null)
        {
            poolPages = pool.PageCount;
            poolLiveBricks = pool.TotalLiveBricks;
            poolCapacityBricks = pool.TotalCapacityBricks;
        }
    }
}
