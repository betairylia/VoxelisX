using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using Voxelis.Tick;
using Voxelis.Utils;

namespace Voxelis
{
    public class VoxelisXCoreWorld : MonoSingleton<VoxelisXCoreWorld>
    {
        public struct BrickInfo
        {
            public Utils.Guid128 EntityId;
            public int3 SectorPos;
            public int3 BrickOrigin;
            public short BrickId;
            public RigidTransform LocalToWorld;

            public SectorHandle Sector;
            public SectorNeighborHandles Neighbors;
            
            // Raw source flag; consumers should read BrickRequireUpdateFlag instead (cleared every
            // tick at the end of dirty propagation). Underscore-prefixed to flag the foot-gun.
            public ushort _BrickDirtyFlag;
            public ushort BrickRequireUpdateFlag;
        }
        
        public Dictionary<Guid128, VoxelEntity> entities = new();
        private readonly List<InfiniteLoader> worldLoaders = new();

        /// <summary>
        /// Registers a voxel entity with this world.
        /// The entity's Guid cannot be changed after registration.
        /// </summary>
        /// <param name="e">The entity to add.</param>
        public void AddEntity(VoxelEntity e)
        {
            if (entities.ContainsKey(e.PersistentGuid))
            {
                return;
            }

            entities.Add(e.PersistentGuid, e);
        }

        /// <summary>
        /// Unregisters a voxel entity from this world.
        /// </summary>
        /// <param name="e">The entity to remove.</param>
        public void RemoveEntity(VoxelEntity e)
        {
            entities.Remove(e.PersistentGuid);
        }

        /// <summary>
        /// Registers an infinite loader with this world.
        /// </summary>
        /// <param name="loader">The loader to add.</param>
        public void AddWorldLoader(InfiniteLoader loader)
        {
            if (worldLoaders.Contains(loader))
            {
                return;
            }

            worldLoaders.Add(loader);
        }

        /// <summary>
        /// Unregisters an infinite loader from this world.
        /// </summary>
        /// <param name="loader">The loader to remove.</param>
        public void RemoveWorldLoader(InfiniteLoader loader)
        {
            worldLoaders.Remove(loader);
        }

        /// <summary>
        /// Ticks all infinite loaders registered with this world.
        /// </summary>
        protected void TickWorldLoaders()
        {
            for (int i = worldLoaders.Count - 1; i >= 0; i--)
            {
                InfiniteLoader loader = worldLoaders[i];
                if (loader == null)
                {
                    worldLoaders.RemoveAt(i);
                    continue;
                }

                loader.Tick();
            }
        }

        /// <summary>
        /// Releases all resources when the world is disabled.
        /// </summary>
        private void OnDisable()
        {
            ReleaseResources();
        }

        protected virtual void ReleaseResources()
        {
            // Entities will dispose themselves
            // BoundaryMaskHelper.DisposePrecomputedBuffers();
        }

        public override void Init()
        {
            base.Init();
        }

        private void Update()
        {
            Profiler.BeginSample("VoxelisX Tick");
            Tick();
            Profiler.EndSample();
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
        public List<VoxelEntity> AllEntities => new List<VoxelEntity>(entities.Values);
    }
}
