using System;
using System.Collections.Generic;
using NUnit.Framework.Internal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

using Voxelis;
using Voxelis.Rendering;

/// <summary>
/// Main rendering system for VoxelisX. Manages ray tracing acceleration structures
/// and coordinates rendering of all voxel entities in the scene.
/// </summary>
/// <remarks>
/// This renderer uses Unity's ray tracing pipeline to render voxel data efficiently.
/// It maintains a ray tracing acceleration structure (RTAS) containing all voxel sectors,
/// and coordinates the update process for all registered voxel entities.
/// Rendering state is managed separately from entity data.
/// </remarks>
// [ExecuteInEditMode]
public class VoxelisXRenderer : MonoSingleton<VoxelisXRenderer>
{
    public VoxelisXWorld world;
    
    /// <summary>
    /// Maps (entity, sectorPos) → SectorRenderer for tracking rendering state independently from entity data.
    /// </summary>
    private Dictionary<(VoxelEntity entity, Vector3Int sectorPos), SectorRenderer> sectorRenderers = new();

    /// <summary>
    /// Test world reference (for testing purposes).
    /// </summary>
    public TestWorld test;

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
            // settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic;
            settings.layerMask = -1;

            _voxelScene = new RayTracingAccelerationStructure(settings);
            Debug.Log($"voxAS: {_voxelScene}");
        }
    }

    /// <summary>
    /// Initializes the renderer on scene start.
    /// </summary>
    public override void Init() 
    {
        SectorRenderer.sectorMaterial = brickMat;
        world = VoxelisXWorld.instance;
        ReloadAS();
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
    /// Finds all voxel entities and rebuilds the acceleration structure with all sectors.
    /// Can be called from the context menu in the Unity Editor.
    /// </summary>
    [ContextMenu("Render all")]
    public void RenderAll()
    {
        SectorRenderer.sectorMaterial = brickMat;

        foreach (var e in FindObjectsByType<VoxelEntity>(FindObjectsSortMode.None))
        {
            world.AddEntity(e);
        }

        _voxelScene.ClearInstances();

        foreach (var e in world.entities)
        {
            foreach (var kvp in e.sectors)
            {
                Vector3Int sectorPos = kvp.Key;
                Sector sector = kvp.Value;

                var key = (e, sectorPos);
                if (!sectorRenderers.ContainsKey(key))
                {
                    sectorRenderers[key] = new SectorRenderer();
                }

                sectorRenderers[key].RenderModifyAS(ref _voxelScene, e, sectorPos, sector);
            }
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
                int cY = i / Sector.SIZE_IN_BRICKS_SQUARED; // 1,024
                int cX = i % Sector.SIZE_IN_BRICKS;  //   128
                int cZ = (i / Sector.SIZE_IN_BRICKS) % Sector.SIZE_IN_BRICKS; // 128

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
                if (useRandomAABB)
                {
                    handles.Add(
                        new TestSector()
                        {
                            handle = _voxelScene.AddInstance(AABBconfig,
                                Matrix4x4.Translate(Vector3.forward * 50 * i + Vector3.left * 50 * j)),
                            mat = Matrix4x4.Translate(Vector3.forward * 50 * i + Vector3.left * 50 * j)
                        });
                }
                else
                {
                    handles.Add(
                        new TestSector()
                        {
                            handle = _voxelScene.AddInstance(AABBconfig,
                                Matrix4x4.Translate(Vector3.forward * Sector.SIZE_IN_BRICKS * i + Vector3.left * Sector.SIZE_IN_BRICKS * j)),
                            mat = Matrix4x4.Translate(Vector3.forward * Sector.SIZE_IN_BRICKS * i + Vector3.left * Sector.SIZE_IN_BRICKS * j)
                        });
                }
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
    /// Releases all GPU resources and disposes all entities.
    /// </summary>
    void ReleaseResources()
    {
        if (aabbBuffer != null && aabbBuffer.IsValid())
        {
            aabbBuffer.Release();
        }

        foreach (var kvp in sectorRenderers)
        {
            kvp.Value.Dispose();
        }

        _voxelScene?.Dispose();
    }

    /// <summary>
    /// When enabled, automatically calls Tick() every frame.
    /// </summary>
    [SerializeField] private bool autoTick = false;

    /// <summary>
    /// Called every frame. If autoTick is enabled, calls Tick().
    /// </summary>
    void Update()
    {
        if(autoTick){ Tick(); }
    }

    /// <summary>
    /// Performs one render update tick for all voxel entities.
    /// </summary>
    /// <remarks>
    /// This method runs in two passes:
    /// Pass 1: Emits render jobs for all sectors and removes sectors marked for deletion.
    /// Pass 2: Synchronizes GPU buffers and updates the acceleration structure.
    /// This two-pass approach allows for parallel job execution while maintaining proper synchronization.
    /// </remarks>
    public void Tick()
    {
        frameId += 1;
        instanceCount = (int)voxelScene.GetInstanceCount();

        // Pass 1: Emit jobs & Remove unused sectors
        foreach (var e in world.entities)
        {
            if(e == null)
            {
                Debug.LogError("Use either backward for loop or another list");
                world.RemoveEntity(e);
                break;
            }

            // Handle sector removal
            while (e.sectorsToRemove.TryDequeue(out Vector3Int sectorPos))
            {
                var key = (e, sectorPos);
                if (sectorRenderers.ContainsKey(key))
                {
                    sectorRenderers[key].RemoveMe(ref _voxelScene);
                    sectorRenderers.Remove(key);
                }
            }

            // Emit render jobs for all sectors
            foreach (var kvp in e.sectors)
            {
                Vector3Int sectorPos = kvp.Key;
                Sector sector = kvp.Value;

                var key = (e, sectorPos);
                if (!sectorRenderers.ContainsKey(key))
                {
                    sectorRenderers[key] = new SectorRenderer();
                }

                sectorRenderers[key].RenderEmitJob(sector);
            }
        }

        // Pass 2: Sync buffers
        foreach (var e in world.entities)
        {
            if (e == null)
            {
                // "Entities" array should not be modified between passes
                throw new InvalidOperationException();
            }

            foreach (var kvp in e.sectors)
            {
                Vector3Int sectorPos = kvp.Key;
                Sector sector = kvp.Value;

                var key = (e, sectorPos);
                if (!sectorRenderers.ContainsKey(key)) continue;

                sectorRenderers[key].Render();
                sectorRenderers[key].RenderModifyAS(ref _voxelScene, e, sectorPos, sector);

                // Call sector tick
                sector.ReorderBricks();
                sector.EndTick();
            }
        }
    }

    private void OnGUI()
    {
        GUILayout.BeginVertical();

        ulong hostMemory = 0;
        ulong deviceMemory = 0;
        foreach (var e in world.entities)
        {
            hostMemory += e.GetHostMemoryUsageKB();
        }

        // Calculate GPU memory from renderers
        foreach (var renderer in sectorRenderers.Values)
        {
            deviceMemory += renderer.VRAMUsage / 1024;
        }

        GUILayout.Box($"AS: {_voxelScene.GetSize() / 1024 / 1024} MB\n" +
                      $"hRAM: {hostMemory / 1024} MB\n" +
                      $"vRAM: {deviceMemory / 1024} MB");

        GUILayout.EndVertical();
    }
}
