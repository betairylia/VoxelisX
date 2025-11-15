using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Voxelis.Tick;

namespace Voxelis
{
    public class VoxelisXCoreWorld : MonoSingleton<VoxelisXCoreWorld>
    {
        public List<VoxelEntity> entities = new();

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
            // Entities will dispose themselves
        }

        public override void Init()
        {
            base.Init();
        }

        private void Update()
        {
            Tick();
        }

        /// <summary>
        /// Main entry point for a game tick.
        /// Typically, this is called in FixedUpdate.
        /// </summary>
        public virtual void Tick()
        {
        }

        /// <summary>
        /// Gets a copy of all registered voxel entities.
        /// </summary>
        public List<VoxelEntity> AllEntities => new List<VoxelEntity>(entities);
    }
}