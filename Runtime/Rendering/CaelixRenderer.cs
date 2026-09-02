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

    /// <summary>
    /// Current frame ID for rendering. Incremented each Tick().
    /// </summary>
    public uint frameId { get; private set; }

    /// <summary>
    /// Debug field showing the current number of instances in the acceleration structure.
    /// </summary>
    [Header("Debug Utils")] public int instanceCount;

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
        SectorRenderer.sectorMaterial = brickMat;
        ReloadAS();
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
            SectorRenderer renderer = sectorRenderers[removalScratch[i]];
            renderer.MarkRemove();
            renderer.RemoveMe(ref _voxelScene);
            sectorRenderers.Remove(removalScratch[i]);
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
        SectorRenderer.sectorMaterial = brickMat;
        if (!EnsureSource())
        {
            return;
        }

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

                sectorRenderers[key].RenderModifyAS(ref _voxelScene, view, sectorPos);
            }

            view.ShouldResetMotionVectors = false;
        }

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
        if (source != null)
        {
            source.ViewDespawning -= OnViewDespawning;
            source = null;
        }

        if (aabbBuffer != null && aabbBuffer.IsValid())
        {
            aabbBuffer.Release();
        }

        foreach (var kvp in sectorRenderers)
        {
            kvp.Value.Dispose();
        }

        sectorRenderers.Clear();
        _voxelScene?.Dispose();
        _voxelScene = null;
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
    /// This method runs in two passes:
    /// Pass 1: Emits render jobs for all sectors and removes sectors marked for deletion.
    /// Pass 2: Synchronizes GPU buffers and updates the acceleration structure.
    /// This two-pass approach allows for parallel job execution while maintaining proper synchronization.
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

        frameId += 1;
        instanceCount = (int)voxelScene.GetInstanceCount();
        JobHandle renderJobs = default;
        bool hasRenderJobs = false;

        IReadOnlyList<EntityView> views = source.Views;

        // Pass 1: Emit jobs & Remove unused sectors
        for (int v = 0; v < views.Count; v++)
        {
            EntityView view = views[v];

            // Handle sector removal
            while (view.SectorsToRemove.TryDequeue(out int3 sectorPos))
            {
                var key = (view, sectorPos);
                if (sectorRenderers.TryGetValue(key, out SectorRenderer removed))
                {
                    removed.MarkRemove();
                    removed.RemoveMe(ref _voxelScene);
                    sectorRenderers.Remove(key);
                }
            }

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

        // Pass 2: Sync buffers
        for (int v = 0; v < views.Count; v++)
        {
            EntityView view = views[v];

            foreach (var kvp in view.Data.sectors)
            {
                int3 sectorPos = kvp.Key;
                ref Sector sector = ref kvp.Value.Get();

                var key = (view, sectorPos);
                if (!sectorRenderers.ContainsKey(key)) continue;

                sectorRenderers[key].ApplyCompletedRenderJob();
                sectorRenderers[key].RenderModifyAS(ref _voxelScene, view, sectorPos);

                // Call sector tick
                sector.ReorderBricks();
            }

            // Every sector of this view has consumed the reset; its motion vectors are settled.
            view.ShouldResetMotionVectors = false;
        }
    }
}
