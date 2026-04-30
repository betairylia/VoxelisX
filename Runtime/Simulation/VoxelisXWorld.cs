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
        [Header("Debug")] public bool freeze = true;
        public bool isFirst = true;
        
        public struct WorldStageInputs
        {
            public NativeList<VoxelEntityData> VoxelEntities;
        }

        public struct BrickInfo
        {
            public int EntityId;
            public int3 SectorPos;
            public int3 BrickOrigin;
            public short BrickId;

            public SectorHandle Sector;
            public SectorNeighborHandles Neighbors;
            
            public ushort BrickDirtyFlag;
            public ushort BrickRequireUpdateFlag;
        }
        
        public struct AutomataStageInputs
        {
            public NativeList<VoxelEntityData> VoxelEntities;
            public NativeList<BrickInfo> BricksRequiredUpdate;
            public AlienOccupancyQuery AlienQuery;
        }
        
        // Ticking
        private WorldStageInputs tickBuf;
        private AutomataStageInputs automataTickBuf;
        
        public override void Init()
        {
            base.Init();
            
            physicsStage = new();
            automataStage = new();
            
            tickBuf.VoxelEntities = new NativeList<VoxelEntityData>(Allocator.Persistent);
            automataTickBuf.BricksRequiredUpdate = new NativeList<BrickInfo>(Allocator.Persistent);
            automataTickBuf.AlienQuery = new AlienOccupancyQuery
            {
                EntitiesInDeterministicOrder = new NativeArray<AlienEntityView>(0, Allocator.Persistent)
            };
        }

        protected override void ReleaseResources()
        {
            tickBuf.VoxelEntities.Dispose();
            automataTickBuf.BricksRequiredUpdate.Dispose();
            automataTickBuf.AlienQuery.EntitiesInDeterministicOrder.Dispose();
            base.ReleaseResources();
        }

        public override void Tick()
        {
            if ((!isFirst) && freeze) return;
            isFirst = false;
            
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
                    if(kvp.Value.Get().sectorRequireUpdateFlags > 0)
                        kvp.Value.ActivateSnapshot();
                }
            }
            
            BuildAlienQueryViews();

            // Collect bricks to update
            automataTickBuf.BricksRequiredUpdate.Clear();
            BrickCollector.Collect(ref tickBuf.VoxelEntities, ref automataTickBuf.BricksRequiredUpdate);
            
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
            if(rayTracedRenderer?.enabled ?? false) rayTracedRenderer?.Tick();
            if(meshingRenderer?.enabled ?? false) meshingRenderer?.Tick();
            
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
        
        private void BuildAlienQueryViews()
        {
            int entityCount = tickBuf.VoxelEntities.Length;
            NativeArray<AlienEntityView> currentViews = automataTickBuf.AlienQuery.EntitiesInDeterministicOrder;
            if (!currentViews.IsCreated || currentViews.Length != entityCount)
            {
                if (currentViews.IsCreated) currentViews.Dispose();
                currentViews = new NativeArray<AlienEntityView>(entityCount, Allocator.Persistent);
                automataTickBuf.AlienQuery.EntitiesInDeterministicOrder = currentViews;
            }

            for (int i = 0; i < entityCount; i++)
            {
                VoxelEntityData entity = tickBuf.VoxelEntities[i];
                float3 boundsMin = new float3(float.PositiveInfinity);
                float3 boundsMax = new float3(float.NegativeInfinity);

                foreach (var kvp in entity.sectors)
                {
                    int3 sectorPos = kvp.Key;
                    float3 localMin = (float3)(sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS);
                    float3 localMax = localMin + new float3(Sector.SECTOR_SIZE_IN_BLOCKS);

                    ExpandBoundsWithPoint(math.transform(entity.transform, localMin), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, new float3(localMin.x, localMin.y, localMax.z)), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, new float3(localMin.x, localMax.y, localMin.z)), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, new float3(localMin.x, localMax.y, localMax.z)), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, new float3(localMax.x, localMin.y, localMin.z)), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, new float3(localMax.x, localMin.y, localMax.z)), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, new float3(localMax.x, localMax.y, localMin.z)), ref boundsMin, ref boundsMax);
                    ExpandBoundsWithPoint(math.transform(entity.transform, localMax), ref boundsMin, ref boundsMax);
                }

                if (entity.sectors.Count == 0)
                {
                    boundsMin = float.MaxValue;
                    boundsMax = float.MinValue;
                }

                currentViews[i] = new AlienEntityView
                {
                    EntityId = i,
                    LocalToWorld = entity.transform,
                    WorldToLocal = math.inverse(new float4x4(entity.transform)),
                    Sectors = entity.sectors,
                    WorldAabbMin = boundsMin,
                    WorldAabbMax = boundsMax
                };
            }
        }

        private static void ExpandBoundsWithPoint(float3 point, ref float3 boundsMin, ref float3 boundsMax)
        {
            boundsMin = math.min(boundsMin, point);
            boundsMax = math.max(boundsMax, point);
        }

        public virtual void DoTick(
            WorldStageInputs world) { }
    }
}