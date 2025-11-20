using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Rendering.Mesh
{
    /// <summary>
    /// Main coordinator for mesh-based voxel rendering.
    /// Automatically discovers VoxelEntity instances and manages SectorMeshRenderer for each sector.
    /// </summary>
    public class VoxelMeshRenderer : IDisposable
    {
        // Settings
        private readonly int chunkSize;
        private readonly Material material;

        // Entity tracking
        private readonly HashSet<VoxelEntity> trackedEntities = new HashSet<VoxelEntity>();
        private readonly Dictionary<(VoxelEntity, int3), SectorMeshRenderer> sectorRenderers =
            new Dictionary<(VoxelEntity, int3), SectorMeshRenderer>();

        // Cached lists to avoid allocations
        private readonly List<(VoxelEntity, int3)> sectorsToRemove = new List<(VoxelEntity, int3)>();

        /// <summary>
        /// Gets the number of currently tracked entities.
        /// </summary>
        public int TrackedEntityCount => trackedEntities.Count;

        /// <summary>
        /// Gets the total number of sector renderers.
        /// </summary>
        public int SectorRendererCount => sectorRenderers.Count;

        public VoxelMeshRenderer(int chunkSize, Material material)
        {
            this.chunkSize = chunkSize;
            this.material = material;
        }

        /// <summary>
        /// Updates all mesh renderers. Called once per frame.
        /// Implements two-phase update: schedule jobs, then complete and apply.
        /// </summary>
        public void Update()
        {
            // Discover and track new entities
            DiscoverEntities();

            // Phase 1: Schedule mesh generation jobs for all dirty sectors
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.ScheduleJobs();
            }

            // Phase 2: Complete jobs and apply meshes
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.CompleteJobs();
            }

            // End tick for all tracked sectors
            EndTick();

            // Cleanup removed sectors
            RemoveMissingSectors();
        }

        /// <summary>
        /// Discovers VoxelEntity instances in the scene and tracks them.
        /// </summary>
        private void DiscoverEntities()
        {
            var allEntities = UnityEngine.Object.FindObjectsByType<VoxelEntity>(FindObjectsSortMode.None);

            foreach (var entity in allEntities)
            {
                if (!trackedEntities.Contains(entity))
                {
                    AddEntity(entity);
                }
            }

            // Remove entities that no longer exist
            var entitiesToRemove = new List<VoxelEntity>();
            foreach (var entity in trackedEntities)
            {
                if (entity == null || !entity.gameObject.activeInHierarchy)
                {
                    entitiesToRemove.Add(entity);
                }
            }

            foreach (var entity in entitiesToRemove)
            {
                RemoveEntity(entity);
            }
        }

        /// <summary>
        /// Adds a new entity to tracking and creates renderers for its sectors.
        /// </summary>
        private void AddEntity(VoxelEntity entity)
        {
            trackedEntities.Add(entity);

            // Create renderers for all existing sectors
            var sectorPositions = entity.Sectors.GetKeyArray(Unity.Collections.Allocator.Temp);

            foreach (var sectorPos in sectorPositions)
            {
                if (entity.Sectors.TryGetValue(sectorPos, out var sectorHandle))
                {
                    AddSectorRenderer(entity, sectorPos, sectorHandle);
                }
            }

            sectorPositions.Dispose();
        }

        /// <summary>
        /// Removes an entity from tracking and destroys all its sector renderers.
        /// </summary>
        private void RemoveEntity(VoxelEntity entity)
        {
            trackedEntities.Remove(entity);

            // Remove all sector renderers for this entity
            sectorsToRemove.Clear();
            foreach (var kvp in sectorRenderers)
            {
                if (kvp.Key.Item1 == entity)
                {
                    sectorsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in sectorsToRemove)
            {
                RemoveSectorRenderer(key.Item1, key.Item2);
            }
        }

        /// <summary>
        /// Creates a sector renderer for a specific sector.
        /// </summary>
        private void AddSectorRenderer(VoxelEntity entity, int3 sectorPos, SectorHandle sectorHandle)
        {
            var key = (entity, sectorPos);

            if (sectorRenderers.ContainsKey(key))
                return;

            var renderer = new SectorMeshRenderer(
                sectorHandle,
                sectorPos,
                chunkSize,
                material,
                entity.transform
            );

            sectorRenderers[key] = renderer;
        }

        /// <summary>
        /// Removes a sector renderer.
        /// </summary>
        private void RemoveSectorRenderer(VoxelEntity entity, int3 sectorPos)
        {
            var key = (entity, sectorPos);

            if (sectorRenderers.TryGetValue(key, out var renderer))
            {
                renderer.Dispose();
                sectorRenderers.Remove(key);
            }
        }

        /// <summary>
        /// Checks for new sectors in tracked entities and removes sectors that no longer exist.
        /// </summary>
        private void RemoveMissingSectors()
        {
            sectorsToRemove.Clear();

            foreach (var kvp in sectorRenderers)
            {
                var entity = kvp.Key.Item1;
                var sectorPos = kvp.Key.Item2;

                // Check if entity still has this sector
                if (entity == null || !entity.Sectors.ContainsKey(sectorPos))
                {
                    sectorsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in sectorsToRemove)
            {
                RemoveSectorRenderer(key.Item1, key.Item2);
            }

            // Add new sectors from tracked entities
            foreach (var entity in trackedEntities)
            {
                if (entity == null)
                    continue;

                var sectorPositions = entity.Sectors.GetKeyArray(Unity.Collections.Allocator.Temp);

                foreach (var sectorPos in sectorPositions)
                {
                    var key = (entity, sectorPos);

                    if (!sectorRenderers.ContainsKey(key))
                    {
                        if (entity.Sectors.TryGetValue(sectorPos, out var sectorHandle))
                        {
                            AddSectorRenderer(entity, sectorPos, sectorHandle);
                        }
                    }
                }

                sectorPositions.Dispose();
            }
        }

        /// <summary>
        /// Calls EndTick on all tracked sectors to clear update records.
        /// </summary>
        private void EndTick()
        {
            foreach (var kvp in sectorRenderers)
            {
                ref Sector sector = ref kvp.Value.SectorObject.GetComponent<SectorMeshRenderer>() != null
                    ? ref kvp.Key.Item1.Sectors[kvp.Key.Item2].Get()
                    : ref kvp.Key.Item1.Sectors[kvp.Key.Item2].Get();

                sector.EndTick();
            }
        }

        /// <summary>
        /// Forces regeneration of all meshes.
        /// </summary>
        public void RegenerateAll()
        {
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.MarkAllDirty();
            }
        }

        /// <summary>
        /// Cleanup all resources.
        /// </summary>
        public void Dispose()
        {
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.Dispose();
            }

            sectorRenderers.Clear();
            trackedEntities.Clear();
        }
    }
}
