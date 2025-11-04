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
/// </remarks>
// [ExecuteInEditMode]
public class VoxelisXRenderer : MonoBehaviour
{
    private List<VoxelEntity> entities = new();

    /// <summary>
    /// Test world reference (for testing purposes).
    /// </summary>
    public TestWorld test;

    /// <summary>
    /// Registers a voxel entity with this renderer.
    /// </summary>
    /// <param name="e">The entity to add.</param>
    public void AddEntity(VoxelEntity e)
    {
        if (entities.Contains(e))
        {
            return;
        }

        entities.Add(e);
    }

    /// <summary>
    /// Unregisters a voxel entity from this renderer.
    /// </summary>
    /// <param name="e">The entity to remove.</param>
    public void RemoveEntity(VoxelEntity e)
    {
        entities.Remove(e);
    }

    /// <summary>
    /// Gets a copy of all registered voxel entities.
    /// </summary>
    public List<VoxelEntity> AllEntities => new List<VoxelEntity>(entities);

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
    void Start()
    {
        SectorRenderer.sectorMaterial = brickMat;
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
            AddEntity(e);
        }
        
        _voxelScene.ClearInstances();
        
        foreach (var e in entities)
        {
            foreach (var kvp in e.Voxels)
            {
                var position = kvp.Key;
                var sector = kvp.Value;
                
                if(sector.renderer == null) continue;

                sector.renderer.RenderModifyAS(ref _voxelScene);
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
                int cY = i / 256; // 1,024
                int cX = i % 16;  //   128
                int cZ = (i / 16) % 16; // 128
                
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
                                Matrix4x4.Translate(Vector3.forward * 16 * i + Vector3.left * 16 * j)),
                            mat = Matrix4x4.Translate(Vector3.forward * 16 * i + Vector3.left * 16 * j)
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

        foreach (var e in entities)
        {
            e.Dispose();
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

        // foreach (var h in handles)
        // {
            // voxelScene.UpdateInstanceTransform(h.handle, h.mat * Matrix4x4.Rotate(Quaternion.AngleAxis(Time.time * 10.0f, Vector3.up)));
            // voxelScene.UpdateInstanceTransform(h.handle, h.mat * Matrix4x4.Rotate(Quaternion.AngleAxis(90.0f, Vector3.up)));
        // }
        
        // Pass 1: Emit jobs & Remove unused sectors
        foreach (var e in entities)
        {
            if(e == null)
            {
                Debug.LogError("Use either backward for loop or another list");
                RemoveEntity(e);
                break;
            }

            while (e.sectorsToRemove.TryDequeue(out SectorRef result))
            {
                result.renderer?.RemoveMe(ref _voxelScene);
            }
            
            foreach (var kvp in e.Voxels)
            {
                var position = kvp.Key;
                var sector = kvp.Value;
                
                if(sector.renderer == null) continue;

                sector.renderer.RenderEmitJob();
            }
        }
        
        // Pass 2: Sync buffers
        foreach (var e in entities)
        {
            if (e == null)
            {
                // "Entities" array should not be modified between passes
                throw new InvalidOperationException();
            }

            foreach (var kvp in e.Voxels)
            {
                var sector = kvp.Value;
                
                if(sector.renderer == null) continue;

                sector.renderer.Render();
                sector.renderer.RenderModifyAS(ref _voxelScene);
                sector.Tick();
            }
        }
    }

    private void OnGUI()
    {
        GUILayout.BeginVertical(); 

        ulong hostMemory = 0;
        ulong deviceMemory = 0;
        foreach (var e in entities)
        {
            hostMemory += e.GetHostMemoryUsageKB();
            deviceMemory += e.GetGPUMemoryUsageKB();
        }
        
        GUILayout.Box($"AS: {_voxelScene.GetSize() / 1024 / 1024} MB\n" +
                      $"hRAM: {hostMemory / 1024} MB\n" +
                      $"vRAM: {deviceMemory / 1024} MB");
        
        GUILayout.EndVertical();
    }
}
