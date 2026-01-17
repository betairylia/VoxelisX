using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
        public TickStage<WorldStageInputs> automataStage;

        [Header("Components")]
        [SerializeField] protected VoxelisXPhysicsWorld physicsWorld;

        [SerializeField] protected VoxelRayCast rayCaster;
        [SerializeField] protected VoxelisXRenderer rayTracedRenderer;
        [SerializeField] protected VoxelMeshRendererComponent meshingRenderer;
        
        [Header("Performance")] public float targetTPS = 100.0f;
        
        public struct WorldStageInputs
        {
            public NativeList<VoxelEntityData> VoxelEntities;
            // public float DeltaTime;
            
            public bool IsCreated => VoxelEntities.IsCreated;

            public void Allocate(int entityCount, Allocator allocator)
            {
                VoxelEntities = new NativeList<VoxelEntityData>(entityCount, allocator);
            }
        }
        
        // Ticking
        private WorldStageInputs tickBuf;
        
        public override void Init()
        {
            base.Init();
            
            physicsStage = new();
            automataStage = new();
        }

        public override void Tick()
        {
            // TEMP CODE -- Tick logic
            rayCaster?.Tick();

            // Dirty propagation
            entities.ForEach(e => e.ClearRequireUpdates());

            JobHandle handle = new JobHandle();
            for (int i = 0; i < entities.Count; i++)
            {
                handle = JobHandle.CombineDependencies(handle, entities[i].PropagateDirtyFlags());
            }
            handle.Complete();
            
            entities.ForEach(e => e.ClearDirtyFlags());
            
            // Ticking
            // TODO: Implement proper ticking flow as below
            if (!tickBuf.IsCreated)
            {
                tickBuf.Allocate(entities.Count, Allocator.Persistent);
            }
            
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
            tickHandle = automataStage.Schedule(tickBuf, tickHandle);
            
            // Random access updating stage
            
            // Propagate dirtiness up, from brick(sector) to VoxelEntityData
            // Update physics info (MassProperties, VoxelType (Corner/Edge/Surface))
            
            /////// Physics update stage
            
            // Resolve voxel contact events into Sectors (AlienVoxelPairs)
            
            /////// Rendering update stage
            
            /////// End Tick stage
            // Clear dirtiness and propagate RequireBrickUpdate to self & neighbors
            
            tickHandle.Complete();
            
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
        }
        
        public virtual void DoTick(
            WorldStageInputs world) { }
    }
}