using Unity.Collections;
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

        [Header("Components")]
        [SerializeField] protected VoxelisXPhysicsWorld physicsWorld;

        [SerializeField] protected VoxelRayCast rayCaster;
        [SerializeField] protected VoxelisXRenderer rayTracedRenderer;
        [SerializeField] protected VoxelMeshRendererComponent meshingRenderer;
        
        [Header("Performance")] public float targetTPS = 100.0f;
        
        // Ticking
        private NativeList<VoxelEntityData> tickBuf;
        
        public struct WorldStageInputs
        {
            public NativeList<VoxelEntityData> VoxelEntities;
            public float DeltaTime;
        }
        
        public override void Init()
        {
            base.Init();
            
            physicsStage = new();
        }

        public override void Tick()
        {
            // TEMP CODE -- Tick logic
            rayCaster?.Tick();
            
            physicsWorld.SimulateStep(1.0f / targetTPS);
            
            // Tick renderer
            if(rayTracedRenderer?.enabled ?? false) rayTracedRenderer?.Tick();
            if(meshingRenderer?.enabled ?? false) meshingRenderer?.Tick();
            
            // TODO: Implement proper ticking flow as below
            /*
            if (!tickBuf.IsCreated)
            {
                tickBuf = new NativeList<VoxelEntityData>(entities.Count, Allocator.Persistent);
            }
            
            // Fill native list by copying
            // TODO: Keep the unique instance in world and let VoxelEntity ref it?
            tickBuf.Clear();
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];
                
                e.SyncTransformToData();
                tickBuf.Add(e.GetDataCopy());
            }
            
            DoTick(tickBuf);
            // Tick
            
            /////// Voxel update stage
            // Random tick stage
            // Automata stage
            // Random access updating stage
            
            // Propagate dirtiness up, from brick(sector) to VoxelEntityData
            // Update physics info (MassProperties, VoxelType (Corner/Edge/Surface))
            
            /////// Physics update stage
            
            // Resolve voxel contact events into Sectors (AlienVoxelPairs)
            
            /////// Rendering update stage
            
            /////// End Tick stage
            // Clear dirtiness and propagate RequireBrickUpdate to self & neighbors
            
            // Copy data back to VoxelEntities
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];
                
                e.CopyDataFrom(tickBuf[i]);
                e.SyncTransformFromData();
            }
            */
        }
        
        public virtual void DoTick(
            NativeList<VoxelEntityData> entities) { }
    }
}