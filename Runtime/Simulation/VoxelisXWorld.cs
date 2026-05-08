using Simulation.Utils;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using Voxelis;
using Voxelis.Rendering.Meshing;
using Voxelis.Simulation;
using Voxelis.Tick;

namespace Voxelis
{
    public class VoxelisXWorld : VoxelisXCoreWorld
    {
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
        [SerializeField] private float alienMotionDirtyThreshold = 0f;
        [Header("Debug")] public bool freeze = true;
        public bool isFirst = true;
        private float timer = 0.0f;
        
        public struct WorldStageInputs
        {
            public NativeList<VoxelEntityData> VoxelEntities;
        }
        
        public struct AutomataStageInputs
        {
            public NativeList<VoxelEntityData> VoxelEntities;
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
            
            tickBuf.VoxelEntities = new NativeList<VoxelEntityData>(Allocator.Persistent);
            automataTickBuf.BricksRequiredUpdate = new NativeList<BrickInfo>(Allocator.Persistent);
            alienEntityViews = new NativeList<AlienEntityView>(Allocator.Persistent);
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
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];

                e.SyncTransformToData();
                tickBuf.VoxelEntities.Add(e.GetDataCopy());
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
            for (int i = 0; i < entities.Count; i++)
            {
                foreach (var kvp in entities[i].Sectors)
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
            for (int i = 0; i < entities.Count; i++)
            {
                foreach (var kvp in entities[i].Sectors)
                {
                    kvp.Value.ApplySnapshot();
                }
            }
            Profiler.EndSample();

            // Copy data back to VoxelEntities
            Profiler.BeginSample("Burst -> Managed Boundary Copy Back");
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];

                e.CopyDataFrom(tickBuf.VoxelEntities[i]);
                e.SyncTransformFromData();
            }
            Profiler.EndSample();

            Profiler.BeginSample("Physics Step");
            physicsWorld.SimulateStep(1.0f / targetTPS);
            Profiler.EndSample();
            
            // Dirty propagation
            Profiler.BeginSample("Dirty Propagation");
            Profiler.BeginSample("Sync Transform");
            float dirtyPropagationDeltaTime = targetTPS > 0f ? 1.0f / targetTPS : Time.deltaTime;
            for (int i = 0; i < entities.Count; i++)
            {
                entities[i].SyncCurrentTransformToData(dirtyPropagationDeltaTime);
            }
            Profiler.EndSample();

            Profiler.BeginSample("Clear Require Updates");
            entities.ForEach(e => e.ClearRequireUpdates());
            Profiler.EndSample();

            Profiler.BeginSample("Propagate Dirty Flags");
            JobHandle handle = new JobHandle();
            for (int i = 0; i < entities.Count; i++)
            {
                handle = JobHandle.CombineDependencies(handle, entities[i].PropagateDirtyFlags(DirtyFlags.All, true));
            }

            Profiler.BeginSample("Burst");
            handle.Complete();
            Profiler.EndSample();

            Profiler.BeginSample("Alien Propagation");
            var dirtyPropagationEntities = new NativeArray<VoxelEntityData>(entities.Count, Allocator.TempJob);
            try
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    dirtyPropagationEntities[i] = entities[i].GetDataCopy();
                }

                AlienDirtyPropagation.Propagate(dirtyPropagationEntities, new AlienDirtyPropagationSettings
                {
                    FlagsToPropagate = DirtyFlags.All,
                    AlienMotionDirtyMask = alienMotionDirtyMask,
                    SpatialCellSize = alienSpatialCellSize,
                    DirtyHaloVoxels = alienDirtyHaloVoxels,
                    MotionThreshold = alienMotionDirtyThreshold,
                    DeltaTime = dirtyPropagationDeltaTime
                });
            }
            finally
            {
                dirtyPropagationEntities.Dispose();
            }
            Profiler.EndSample();

            Profiler.BeginSample("Clear Dirty Flags");
            entities.ForEach(e => e.ClearDirtyFlags());
            Profiler.EndSample();
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

            for (int i = 0; i < tickBuf.VoxelEntities.Length; i++)
            {
                VoxelEntityData entity = tickBuf.VoxelEntities[i];
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
                    EntityId = i,
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
    }
}
