using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Persistence
{
    /// <summary>
    /// High-level facade for the save/load system.
    /// Provides simple APIs for saving and loading VoxelEntity instances.
    /// </summary>
    public class SaveLoadManager
    {
        private EntityPersistenceManager entityManager;
        private RegionFileManager regionManager;

        /// <summary>
        /// Creates a new SaveLoadManager with optional custom save directory.
        /// </summary>
        /// <param name="baseSaveDirectory">Base directory for save data. If null, uses Application.persistentDataPath.</param>
        public SaveLoadManager(string baseSaveDirectory = null)
        {
            entityManager = new EntityPersistenceManager(baseSaveDirectory);
            regionManager = new RegionFileManager(baseSaveDirectory);
        }

        /// <summary>
        /// Saves a VoxelEntity to disk.
        /// </summary>
        /// <param name="entity">The entity to save.</param>
        public void SaveEntity(VoxelEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            entityManager.SaveEntity(entity);
            Debug.Log($"Saved entity {entity.EntityGuid} with {entity.Sectors.Count()} sectors");
        }

        /// <summary>
        /// Loads a VoxelEntity from disk by GUID.
        /// Creates a new GameObject with VoxelEntity component.
        /// </summary>
        /// <param name="guid">GUID of the entity to load.</param>
        /// <returns>The loaded VoxelEntity, or null if not found.</returns>
        public VoxelEntity LoadEntity(Guid guid)
        {
            VoxelEntityData entityData;
            PhysicsData? physicsData;
            InfiniteLoaderSettings? infiniteSettings;

            bool success = entityManager.LoadEntity(guid, out entityData, out physicsData,
                out infiniteSettings, Allocator.Persistent);

            if (!success)
            {
                Debug.LogError($"Failed to load entity {guid}");
                return null;
            }

            // Create GameObject
            GameObject go = new GameObject($"VoxelEntity_{guid:N}");
            VoxelEntity entity = go.AddComponent<VoxelEntity>();

            // Set GUID before copying data
            entity.SetEntityGuid(guid);

            // Copy entity data
            entity.CopyDataFrom(entityData);

            // Sync transform from loaded data
            entity.SyncTransformFromData();

            // Add physics component if needed
            if (physicsData.HasValue)
            {
                VoxelBody body = go.AddComponent<VoxelBody>();
                body.physicsEnabled = true;
                body.isStatic = physicsData.Value.isStatic != 0;

                // Note: Mass properties will be recomputed on next physics update
                // We could add a method to VoxelBody to restore mass properties directly
            }

            // Add infinite loader if needed
            if (infiniteSettings.HasValue)
            {
                // Note: We can't instantiate abstract InfiniteLoader
                // The user will need to add their specific loader implementation
                Debug.LogWarning($"Entity {guid} was saved with InfiniteLoader settings, " +
                    "but automatic restoration is not supported. Please add your InfiniteLoader component manually.");
            }

            Debug.Log($"Loaded entity {guid} with {entityData.sectors.Count()} sectors");
            return entity;
        }

        /// <summary>
        /// Saves all VoxelEntity instances in the scene.
        /// </summary>
        public void SaveAllEntities()
        {
            VoxelEntity[] entities = UnityEngine.Object.FindObjectsOfType<VoxelEntity>();

            int savedCount = 0;
            foreach (var entity in entities)
            {
                try
                {
                    SaveEntity(entity);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to save entity {entity.EntityGuid}: {ex.Message}");
                }
            }

            Debug.Log($"Saved {savedCount} entities out of {entities.Length} total");
        }

        /// <summary>
        /// Loads all entities from the entity listing file.
        /// </summary>
        /// <returns>List of loaded VoxelEntity instances.</returns>
        public List<VoxelEntity> LoadAllEntities()
        {
            Guid[] guids = entityManager.GetAllEntityGuids();
            List<VoxelEntity> loadedEntities = new List<VoxelEntity>();

            foreach (Guid guid in guids)
            {
                try
                {
                    VoxelEntity entity = LoadEntity(guid);
                    if (entity != null)
                    {
                        loadedEntities.Add(entity);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to load entity {guid}: {ex.Message}");
                }
            }

            Debug.Log($"Loaded {loadedEntities.Count} entities out of {guids.Length} total");
            return loadedEntities;
        }

        /// <summary>
        /// Deletes an entity from save data.
        /// </summary>
        /// <param name="guid">GUID of the entity to delete.</param>
        public void DeleteEntity(Guid guid)
        {
            entityManager.DeleteEntity(guid);
            Debug.Log($"Deleted entity {guid}");
        }

        /// <summary>
        /// Checks if an entity exists in save data.
        /// </summary>
        public bool EntityExists(Guid guid)
        {
            return entityManager.EntityExists(guid);
        }

        /// <summary>
        /// Gets all entity GUIDs from save data.
        /// </summary>
        public Guid[] GetAllEntityGuids()
        {
            return entityManager.GetAllEntityGuids();
        }

        /// <summary>
        /// Saves a single sector to a region file (for advanced usage).
        /// </summary>
        public unsafe void SaveSector(int3 sectorPosition, Sector* sector, RegionType regionType)
        {
            string regionPath;

            if (regionType == RegionType.Infinite)
            {
                regionPath = regionManager.GetInfiniteRegionPath(sectorPosition);
            }
            else
            {
                // For finite, use a default chunk index
                regionPath = regionManager.GetFiniteRegionPath(0);
            }

            regionManager.WriteSector(regionPath, sectorPosition, sector, regionType);
        }

        /// <summary>
        /// Loads a single sector from a region file (for advanced usage).
        /// The sector must be pre-allocated by the caller.
        /// </summary>
        public unsafe bool LoadSector(int3 sectorPosition, Sector* outSector, RegionType regionType)
        {
            string regionPath;

            if (regionType == RegionType.Infinite)
            {
                regionPath = regionManager.GetInfiniteRegionPath(sectorPosition);
            }
            else
            {
                regionPath = regionManager.GetFiniteRegionPath(0);
            }

            return regionManager.ReadSector(regionPath, sectorPosition, outSector);
        }

        /// <summary>
        /// Compacts all region files to reclaim disk space.
        /// Should be called periodically or during maintenance.
        /// </summary>
        public void CompactAllRegions()
        {
            // This is a placeholder for a full implementation
            // In practice, you'd need to enumerate all region files and compact them
            Debug.LogWarning("CompactAllRegions is not yet fully implemented");
        }
    }
}
