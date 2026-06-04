using System;
using System.Collections.Generic;
using System.IO;
using Simulation.Utils;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Profiling;
using Voxelis;
using Voxelis.IO;
using Voxelis.Rendering.Meshing;
using Voxelis.Simulation;
using Voxelis.Tick;
using Voxelis.Utils;

namespace Voxelis
{
    public class VoxelisXWorld : VoxelisXCoreWorld
    {
        public Dictionary<Guid128, VoxelBody> bodies = new();

        public TickStage<WorldStageInputs> physicsStage;
        public TickStage<AutomataStageInputs> automataStage;

        [Header("Components")]
        [SerializeField] protected VoxelisXPhysicsWorld physicsWorld;

        [SerializeField] protected VoxelRayCast rayCaster;
        [SerializeField] protected VoxelisXRenderer rayTracedRenderer;
        [SerializeField] protected VoxelMeshRendererComponent meshingRenderer;
        
        [Header("Performance")] public float targetTPS = 100.0f;
        public float slowmo = 1.0f;
        [Header("Alien Dirty Propagation")]
        [SerializeField] private int alienSpatialCellSize = 64;
        [SerializeField] private DirtyFlags alienMotionDirtyMask = DirtyFlags.GeneralAutomata;
        [SerializeField] private int alienDirtyHaloVoxels = 1;
        [Header("Debug")] public bool freeze = true;
        public bool isFirst = true;
        private float timer = 0.0f;

        private const string DefaultSaveLoadFileName = "voxelisx-world.vxw";

        [Header("Save / Load")]
        [SerializeField] private string saveLoadPath = DefaultSaveLoadFileName;
        
        public struct WorldStageInputs
        {
            public NativeHashMap<Guid128, VoxelEntityData> VoxelEntities;
            public NativeHashMap<Guid128, VoxelBodyData> VoxelBodies;
        }
        
        public struct AutomataStageInputs
        {
            public NativeHashMap<Guid128, VoxelEntityData> VoxelEntities;
            public NativeList<BrickInfo> BricksRequiredUpdate;
            public AutomataReadContext ReadContext;
        }
        
        // Ticking
        private WorldStageInputs tickBuf;
        private AutomataStageInputs automataTickBuf;
        private NativeList<AlienEntityView> alienEntityViews;
        
        public override void Init()
        {
            base.Init();
            
            physicsStage = new();
            automataStage = new();
            
            tickBuf.VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent);
            tickBuf.VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent);
            automataTickBuf.BricksRequiredUpdate = new NativeList<BrickInfo>(Allocator.Persistent);
            alienEntityViews = new NativeList<AlienEntityView>(Allocator.Persistent);
        }

        /// <summary>
        /// Registers a voxel entity with this world.
        /// The entity's Guid cannot be changed after registration.
        /// </summary>
        /// <param name="e">The entity to add.</param>
        public void AddBody(VoxelBody b)
        {
            if (bodies.ContainsKey(b.entity.PersistentGuid))
            {
                return;
            }

            bodies.Add(b.entity.PersistentGuid, b);
        }

        /// <summary>
        /// Unregisters a voxel entity from this world.
        /// </summary>
        /// <param name="e">The entity to remove.</param>
        public void RemoveBody(VoxelBody b)
        {
            entities.Remove(b.entity.PersistentGuid);
        }

        protected override void ReleaseResources()
        {
            tickBuf.VoxelEntities.Dispose();
            automataTickBuf.BricksRequiredUpdate.Dispose();
            alienEntityViews.Dispose();
            base.ReleaseResources();
        }

        public override void Tick()
        {
            // TEMP CODE -- Tick logic
            if ((!isFirst) && freeze) return;
            isFirst = false;

            float deltaTime = targetTPS > 0f ? 1.0f / targetTPS : Time.deltaTime;
            
            // timer -= Time.deltaTime;
            // if (timer > 0)
            // {
            //     return;
            // }
            // else
            // {
            //     timer = slowmo * 1.0f / targetTPS;
            // }

            // Ticking
            // TODO: FIXME: Currently inf loaders will not work due to no proper sector loading transition
            // TickWorldLoaders();
            
Profiler.BeginSample("Player Ray Cast");
            rayCaster?.Tick();
Profiler.EndSample();

            // Fill native list by copying
            // TODO: Keep the unique instance in world and let VoxelEntity ref it?
Profiler.BeginSample("Fill TickBuffer");
            tickBuf.VoxelEntities.Clear();
            tickBuf.VoxelBodies.Clear();
            foreach(var kvp in entities)
            {
                var e = kvp.Value;
                e.SyncTransformToData();
                tickBuf.VoxelEntities.Add(e.PersistentGuid, e.GetDataCopy());

                if (bodies.TryGetValue(kvp.Key, out var b))
                {
                    tickBuf.VoxelBodies.Add(b.entity.PersistentGuid, b.GetDataCopy());
                }
            }
Profiler.EndSample();

Profiler.BeginSample("WorldStage");
            DoTick(tickBuf);
Profiler.EndSample();
            
            // Tick
            JobHandle tickHandle = new JobHandle();

            /////// Voxel update stage
            // Random tick stage

            // Automata stage
            // TODO: Wrap this up and handle this properly
            // Activate sector snapshotting for modifications
Profiler.BeginSample("Activate Sector Snapshots");
            foreach (var e in entities.Values)
            {
                foreach (var kvp in e.Sectors)
                {
                    if (kvp.Value.Get().sectorRequireUpdateFlags > 0)
                        kvp.Value.ActivateSnapshot();
                }
            }
Profiler.EndSample();

            // Collect bricks to update
Profiler.BeginSample("Collect RequireUpdate Bricks");
            automataTickBuf.BricksRequiredUpdate.Clear();
            BrickCollector.Collect(ref tickBuf.VoxelEntities, ref automataTickBuf.BricksRequiredUpdate);
Profiler.EndSample();
Profiler.BeginSample("Build Alien Read Context");
            BuildAlienReadContext();
Profiler.EndSample();

Profiler.BeginSample("Automata Stage Schedule");
            tickHandle = automataStage.Schedule(automataTickBuf, tickHandle);
Profiler.EndSample();

            // Random access updating stage

            // Propagate dirtiness up, from brick(sector) to VoxelEntityData
            // Update physics info (MassProperties, VoxelType (Corner/Edge/Surface))

            /////// Physics update stage

            // Resolve voxel contact events into Sectors (AlienVoxelPairs)

            /////// Rendering update stage

            /////// End Tick stage
            // Clear dirtiness and propagate RequireBrickUpdate to self & neighbors

Profiler.BeginSample("Work Dispatch");
            tickHandle.Complete();
Profiler.EndSample();

            // TODO: Wrap this up and handle this properly
            // Apply sector modifications
Profiler.BeginSample("Apply Sector Snapshots");
            foreach(var e in entities.Values)
            {
                foreach (var kvp in e.Sectors)
                {
                    kvp.Value.ApplySnapshot();
                }
            }
Profiler.EndSample();

Profiler.BeginSample("Physics Step");
            physicsWorld.SimulateStep(deltaTime, tickBuf);
Profiler.EndSample();
            
            // Dirty propagation
Profiler.BeginSample("Dirty Propagation");
    Profiler.BeginSample("Update Velocity");
            foreach(var e in entities.Values)
            {
                e.UpdateVelocity(deltaTime);
            }
    Profiler.EndSample();

    Profiler.BeginSample("Clear Require Updates");
            foreach (var e in entities.Values)
            {
                e.ClearRequireUpdates();
            }
    Profiler.EndSample();

    Profiler.BeginSample("Propagate Dirty Flags");
            JobHandle handle = new JobHandle();
            foreach(var e in entities.Values)
            {
                handle = JobHandle.CombineDependencies(handle, e.PropagateDirtyFlags(DirtyFlags.All, true));
            }

        Profiler.BeginSample("Burst");
            handle.Complete();
        Profiler.EndSample();
    Profiler.EndSample();

    Profiler.BeginSample("Alien Propagation");
            foreach(var kvp in entities)
            {
                tickBuf.VoxelEntities[kvp.Key] = entities[kvp.Key].GetDataCopy();
            }

            AlienDirtyPropagation.Propagate(tickBuf.VoxelEntities.GetValueArray(Allocator.TempJob), new AlienDirtyPropagationSettings
            {
                FlagsToPropagate = DirtyFlags.All,
                AlienMotionDirtyMask = alienMotionDirtyMask,
                SpatialCellSize = alienSpatialCellSize,
                DirtyHaloVoxels = alienDirtyHaloVoxels,
            });
    Profiler.EndSample();

    Profiler.BeginSample("Clear Dirty Flags");
            foreach(var e in entities.Values)
            {
                e.ClearDirtyFlags();
            }
    Profiler.EndSample();
Profiler.EndSample();

            // Copy data back to VoxelEntities
Profiler.BeginSample("Burst -> Managed Boundary Copy Back");
            foreach(var kvp in entities)
            {
                kvp.Value.CopyDataFrom(tickBuf.VoxelEntities[kvp.Key]);
                kvp.Value.SyncTransformFromData();
            }
Profiler.EndSample();
            
            // Tick renderer
Profiler.BeginSample("Renderer Tick");
            if (rayTracedRenderer?.enabled ?? false) rayTracedRenderer?.Tick();
            if (meshingRenderer?.enabled ?? false) meshingRenderer?.Tick();
Profiler.EndSample();
        }

        public virtual void DoTick(
            WorldStageInputs world) { }

        private void BuildAlienReadContext()
        {
            alienEntityViews.Clear();

            foreach(var kvp in tickBuf.VoxelEntities)
            {
                VoxelEntityData entity = kvp.Value;
                float4x4 localToWorldMatrix = float4x4.TRS(entity.transform.pos, entity.transform.rot, 1f);
                float4x4 worldToLocal = math.inverse(localToWorldMatrix);
                float3 worldAabbMin = entity.transform.pos;
                float3 worldAabbMax = entity.transform.pos;

                if (entity.sectors.Count > 0)
                {
                    NativeArray<int3> sectorKeys = entity.sectors.GetKeyArray(Allocator.Temp);
                    int3 minSector = sectorKeys[0];
                    int3 maxSector = sectorKeys[0];

                    for (int k = 1; k < sectorKeys.Length; k++)
                    {
                        minSector = math.min(minSector, sectorKeys[k]);
                        maxSector = math.max(maxSector, sectorKeys[k]);
                    }

                    sectorKeys.Dispose();

                    float3 localMin = minSector * Sector.SECTOR_SIZE_IN_BLOCKS;
                    float3 localMax = (maxSector + 1) * Sector.SECTOR_SIZE_IN_BLOCKS;
                    for (int mask = 0; mask < 8; mask++)
                    {
                        float3 localCorner = new float3(
                            (mask & 1) == 0 ? localMin.x : localMax.x,
                            (mask & 2) == 0 ? localMin.y : localMax.y,
                            (mask & 4) == 0 ? localMin.z : localMax.z);
                        float3 worldCorner = math.transform(entity.transform, localCorner);
                        worldAabbMin = math.min(worldAabbMin, worldCorner);
                        worldAabbMax = math.max(worldAabbMax, worldCorner);
                    }
                }

                alienEntityViews.Add(new AlienEntityView
                {
                    EntityId = kvp.Key,
                    LocalToWorld = entity.transform,
                    WorldToLocal = worldToLocal,
                    Sectors = entity.sectors.AsReadOnly(),
                    WorldAabbMin = worldAabbMin,
                    WorldAabbMax = worldAabbMax
                });
            }

            automataTickBuf.ReadContext = new AutomataReadContext
            {
                AlienQuery = new AlienOccupancyQuery
                {
                    EntitiesInDeterministicOrder = alienEntityViews.AsArray()
                }
            };
        }

        /// <summary>
        /// Saves every registered <see cref="VoxelEntity"/> to a <c>.vxw</c> file at <paramref name="path"/>.
        /// Each entity's current Unity transform is synced into its native data prior to serialization.
        /// </summary>
        public void Save(string path)
        {
            var list = new List<(Guid128, VoxelEntity)>(entities.Count);
            foreach(var e in entities.Values)
            {
                // TODO: FIXME: Subtle bug -- will this break tick continuity? (this overwrites prevTransform)
                e.SyncTransformToData();
                list.Add((e.PersistentGuid, e));
            }
            WorldSaver.Save(path, list);
        }

        /// <summary>
        /// Saves the world using the inspector-configured save/load path.
        /// </summary>
        [InspectorButton("Save World", PlayModeOnly = true)]
        public void Save()
        {
            string path = ResolveSaveLoadPath();
            Save(path);
            Debug.Log($"Saved VoxelisX world to {path}", this);
        }

        /// <summary>
        /// Loads the world using the inspector-configured save/load path.
        /// </summary>
        [InspectorButton("Load World", PlayModeOnly = true)]
        public void Load()
        {
            string path = ResolveSaveLoadPath();

            WorldLoader.Load(path, rec =>
            {
                var go = new GameObject($"VoxelEntity_{rec.Guid}");

                go.SetActive(false);

                var e = go.AddComponent<VoxelEntity>();
                if (e != null) e.PersistentGuid = rec.Guid;
                
                go.SetActive(true);

                return e;
            });

            Debug.Log($"Loaded VoxelisX world from {path}", this);
        }

        [InspectorButton("Choose Save/Load Path")]
        private void ChooseSaveLoadPath()
        {
#if UNITY_EDITOR
            string currentPath = ResolveSaveLoadPath();
            string directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Application.persistentDataPath;
            }

            string fileName = Path.GetFileName(currentPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = DefaultSaveLoadFileName;
            }

            string selectedPath = EditorUtility.SaveFilePanel(
                "Choose VoxelisX world file",
                directory,
                fileName,
                "vxw");

            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            saveLoadPath = selectedPath;
            EditorUtility.SetDirty(this);
#endif
        }

        private string ResolveSaveLoadPath()
        {
            string path = string.IsNullOrWhiteSpace(saveLoadPath)
                ? DefaultSaveLoadFileName
                : saveLoadPath;

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(Application.persistentDataPath, path);
        }
    }
}
