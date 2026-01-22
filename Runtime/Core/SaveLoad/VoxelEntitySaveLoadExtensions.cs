using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Voxelis;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Extension methods for VoxelEntity to support save/load operations
    /// </summary>
    public static class VoxelEntitySaveLoadExtensions
    {
        /// <summary>
        /// Create EntityMetadata from a VoxelEntity
        /// </summary>
        public static EntityMetadata CreateMetadata(this VoxelEntity entity, Guid guid, EntityType type)
        {
            var data = entity.GetDataCopy();
            var metadata = new EntityMetadata(guid, type, data.transform, data.entityDirtyFlags);

            // For finite entities, calculate AABB from all sectors
            if (type == EntityType.Finite && data.sectors.Count > 0)
            {
                float3 min = new float3(float.MaxValue);
                float3 max = new float3(float.MinValue);

                int sectorSizeInBlocks = Sector.SECTOR_SIZE_IN_BLOCKS;

                foreach (var kvp in data.sectors)
                {
                    int3 sectorPos = kvp.Key;
                    float3 sectorMin = (float3)sectorPos * sectorSizeInBlocks;
                    float3 sectorMax = sectorMin + sectorSizeInBlocks;

                    min = math.min(min, sectorMin);
                    max = math.max(max, sectorMax);
                }

                metadata.SetAABB(min, max);
            }

            return metadata;
        }

        /// <summary>
        /// Save all sectors from an infinite entity to the save manager
        /// </summary>
        public static unsafe void SaveToRegions(this VoxelEntity entity, Guid entityGuid, WorldSaveManager saveManager)
        {
            var data = entity.GetDataCopy();

            foreach (var kvp in data.sectors)
            {
                int3 sectorPos = kvp.Key;
                Sector* sector = kvp.Value.Ptr;

                saveManager.SaveSector(entityGuid, sectorPos, sector);
            }
        }

        /// <summary>
        /// Save a complete finite entity to the save manager
        /// </summary>
        public static void SaveFiniteEntity(this VoxelEntity entity, Guid entityGuid, WorldSaveManager saveManager)
        {
            var data = entity.GetDataCopy();
            var metadata = entity.CreateMetadata(entityGuid, EntityType.Finite);

            // Create a copy of the sectors dictionary for saving
            var sectors = new Dictionary<int3, SectorHandle>();
            foreach (var kvp in data.sectors)
            {
                sectors[kvp.Key] = kvp.Value;
            }

            saveManager.SaveFiniteEntity(metadata, sectors);
        }

        /// <summary>
        /// Load a sector for an infinite entity from the save manager
        /// Returns true if the sector was loaded from disk
        /// </summary>
        public static unsafe bool LoadSectorFromRegion(this VoxelEntity entity, Guid entityGuid, int3 sectorPos, WorldSaveManager saveManager)
        {
            // Check if sector already exists
            if (entity.Sectors.ContainsKey(sectorPos))
                return false;

            // Allocate new sector
            Sector* sector = Sector.Create();

            // Try to load from disk
            if (saveManager.LoadSector(entityGuid, sectorPos, sector))
            {
                // Add loaded sector to entity
                entity.AddSectorAt(sectorPos, new SectorHandle(sector));
                return true;
            }
            else
            {
                // Sector doesn't exist on disk, clean up
                Sector.Destroy(sector);
                return false;
            }
        }

        /// <summary>
        /// Check if a sector exists in storage (for infinite entities)
        /// Helps distinguish between "not generated" vs "generated but not loaded"
        /// </summary>
        public static bool SectorExistsInStorage(this VoxelEntity entity, Guid entityGuid, int3 sectorPos, WorldSaveManager saveManager)
        {
            return saveManager.HasSector(entityGuid, sectorPos);
        }

        /// <summary>
        /// Load a complete finite entity from the save manager
        /// Returns a new VoxelEntity with all sectors loaded
        /// </summary>
        public static unsafe VoxelEntity LoadFiniteEntity(Guid entityGuid, WorldSaveManager saveManager, Transform parent = null)
        {
            if (!saveManager.LoadFiniteEntity(entityGuid, out var metadata, out var sectors))
            {
                Debug.LogError($"Failed to load finite entity {entityGuid}");
                return null;
            }

            // Create GameObject and VoxelEntity
            var go = new GameObject($"FiniteEntity_{entityGuid}");
            if (parent != null)
                go.transform.SetParent(parent);

            var entity = go.AddComponent<VoxelEntity>();

            // Set transform from metadata
            go.transform.SetPositionAndRotation(metadata.transform.pos, metadata.transform.rot);
            entity.SyncTransformToData();

            // Add all loaded sectors
            foreach (var kvp in sectors)
            {
                entity.AddSectorAt(kvp.Key, new SectorHandle(kvp.Value));
            }

            return entity;
        }

        /// <summary>
        /// Save entity metadata without saving sector data
        /// Useful for updating entity transform or AABB without full save
        /// </summary>
        public static void SaveMetadata(this VoxelEntity entity, Guid entityGuid, EntityType type, WorldSaveManager saveManager)
        {
            var metadata = entity.CreateMetadata(entityGuid, type);
            saveManager.SaveEntityMetadata(metadata);
        }
    }
}
