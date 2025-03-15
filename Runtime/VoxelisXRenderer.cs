using System;
using System.Collections.Generic;
using NUnit.Framework.Internal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

using Voxelis;
using Voxelis.Rendering;

// [ExecuteInEditMode]
public class VoxelisXRenderer : MonoBehaviour
{
    private List<VoxelEntity> entities = new();
    public TestWorld test;

    public void AddEntity(VoxelEntity e)
    {
        if (entities.Contains(e))
        {
            return;
        }
        
        entities.Add(e);
    }

    public void RemoveEntity(VoxelEntity e)
    {
        entities.Remove(e);
    }
    
    public RayTracingAccelerationStructure voxelScene
    {
        get
        {
            if(_voxelScene == null) ReloadAS();
            return _voxelScene;
        }
    }
    private RayTracingAccelerationStructure _voxelScene;

    internal struct TestSector
    {
        internal int handle;
        internal Matrix4x4 mat;
    }
    
    private List<TestSector> handles = new();

    public Material brickMat;

    public uint frameId { get; private set; }

    [Header("Debug Utils")] public int instanceCount;

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

    void Start()
    {
        SectorRenderer.sectorMaterial = brickMat;
        ReloadAS();
    }
    
    [ContextMenu("Re-init voxAS")] 
    public void ReloadAS()
    {
        CreateRayTracingAccelerationStructure();
        
        frameId = 0;
    }

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

                sector.renderer.Render(ref _voxelScene);
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

    private void OnDisable()
    {
        ReleaseResources();
    }

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
        
        _voxelScene.Dispose();
    }

    // Update is called once per frame
    void Update()
    {
        frameId += 1;
        instanceCount = (int)voxelScene.GetInstanceCount();

        // foreach (var h in handles)
        // {
            // voxelScene.UpdateInstanceTransform(h.handle, h.mat * Matrix4x4.Rotate(Quaternion.AngleAxis(Time.time * 10.0f, Vector3.up)));
            // voxelScene.UpdateInstanceTransform(h.handle, h.mat * Matrix4x4.Rotate(Quaternion.AngleAxis(90.0f, Vector3.up)));
        // }
        
        // Pass 1: Emit jobs
        foreach (var e in entities)
        {
            if(e == null)
            {
                RemoveEntity(e);
                break;
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

                sector.renderer.Render(ref _voxelScene);
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
