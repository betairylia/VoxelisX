using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Voxelis
{
    public class VoxelisXWorld : MonoSingleton<VoxelisXWorld>
    {
        public List<VoxelEntity> entities = new();
        private NativeList<VoxelEntityData> tickBuf;

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
        /// Releases all resources when the world is disabled.
        /// </summary>
        private void OnDisable()
        {
            ReleaseResources();
        }

        void ReleaseResources()
        {
            foreach (var e in entities)
            {
                e.Dispose();
            }
        }
        
        /// <summary>
        /// Main entry point for a game tick.
        /// Typically, this is called in FixedUpdate.
        /// </summary>
        public void Tick()
        {
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
            
            // Tick
            
            // Copy data back to VoxelEntities
            for (int i = 0; i < entities.Count; i++)
            {
                VoxelEntity e = entities[i];
                
                e.CopyDataFrom(tickBuf[i]);
                e.SyncTransformFromData();
            }
        }

        /// <summary>
        /// Gets a copy of all registered voxel entities.
        /// </summary>
        public List<VoxelEntity> AllEntities => new List<VoxelEntity>(entities);
    }
}