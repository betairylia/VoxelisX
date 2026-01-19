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
        }

        protected override void ReleaseResources()
        {
            tickBuf.VoxelEntities.Dispose();
            automataTickBuf.BricksRequiredUpdate.Dispose();
            base.ReleaseResources();
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
                handle = JobHandle.CombineDependencies(handle, entities[i].PropagateDirtyFlags(DirtyFlags.All, true));
            }
            handle.Complete();
            
            entities.ForEach(e => e.ClearDirtyFlags());
            
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
        }
        
        public virtual void DoTick(
            WorldStageInputs world) { }
    }
}