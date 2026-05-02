using Simulation.Utils;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
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

            // TEMP CODE -- Tick logic
            rayCaster?.Tick();

            // Ticking

            // Fill native list by copying
            // TODO: Keep the unique instance in world and let VoxelEntity ref it?
            tickBuf.VoxelEntities.Clear();
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];

                e.SyncTransformToData();
                tickBuf.VoxelEntities.Add(e.GetDataCopy());
            }

            DoTick(tickBuf);
            // Tick
            JobHandle tickHandle = new JobHandle();

            /////// Voxel update stage
            // Random tick stage

            // Automata stage
            // TODO: Wrap this up and handle this properly
            // Activate sector snapshotting for modifications
            for (int i = 0; i < entities.Count; i++)
            {
                foreach (var kvp in entities[i].Sectors)
                {
                    if (kvp.Value.Get().sectorRequireUpdateFlags > 0)
                        kvp.Value.ActivateSnapshot();
                }
            }

            // Collect bricks to update
            automataTickBuf.BricksRequiredUpdate.Clear();
            BrickCollector.Collect(ref tickBuf.VoxelEntities, ref automataTickBuf.BricksRequiredUpdate);
            BuildAlienReadContext();

            tickHandle = automataStage.Schedule(automataTickBuf, tickHandle);

            // Random access updating stage

            // Propagate dirtiness up, from brick(sector) to VoxelEntityData
            // Update physics info (MassProperties, VoxelType (Corner/Edge/Surface))

            /////// Physics update stage

            // Resolve voxel contact events into Sectors (AlienVoxelPairs)

            /////// Rendering update stage

            /////// End Tick stage
            // Clear dirtiness and propagate RequireBrickUpdate to self & neighbors

            tickHandle.Complete();

            // TODO: Wrap this up and handle this properly
            // Apply sector modifications
            for (int i = 0; i < entities.Count; i++)
            {
                foreach (var kvp in entities[i].Sectors)
                {
                    kvp.Value.ApplySnapshot();
                }
            }

            // Copy data back to VoxelEntities
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];

                e.CopyDataFrom(tickBuf.VoxelEntities[i]);
                e.SyncTransformFromData();
            }

            physicsWorld.SimulateStep(1.0f / targetTPS);

            // Tick renderer
            if (rayTracedRenderer?.enabled ?? false) rayTracedRenderer?.Tick();
            if (meshingRenderer?.enabled ?? false) meshingRenderer?.Tick();

            // Dirty propagation
            entities.ForEach(e => e.ClearRequireUpdates());

            JobHandle handle = new JobHandle();
            for (int i = 0; i < entities.Count; i++)
            {
                handle = JobHandle.CombineDependencies(handle, entities[i].PropagateDirtyFlags(DirtyFlags.All, true));
            }

            handle.Complete();

            entities.ForEach(e => e.ClearDirtyFlags());
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
