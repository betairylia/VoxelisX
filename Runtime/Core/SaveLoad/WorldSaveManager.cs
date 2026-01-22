using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Voxelis;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Main coordinator for world save/load operations
    /// Manages region files, entity indices, and entity archives
    /// </summary>
    public class WorldSaveManager : IDisposable
    {
        private readonly string savePath;
        private Guid mainEntityGuid;
        private int worldSeed;

        // Caches for file managers (loaded on demand)
        private Dictionary<int3, RegionFile> regionFiles;
        private Dictionary<int3, EntityIndexFile> entityIndexFiles;
        private Dictionary<int3, EntityArchiveFile> entityArchiveFiles;

        private bool isDisposed;

        public WorldSaveManager(string savePath)
        {
            this.savePath = savePath;
            this.regionFiles = new Dictionary<int3, RegionFile>();
            this.entityIndexFiles = new Dictionary<int3, EntityIndexFile>();
            this.entityArchiveFiles = new Dictionary<int3, EntityArchiveFile>();

            // Ensure save directory exists
            if (!Directory.Exists(savePath))
                Directory.CreateDirectory(savePath);

            LoadWorldMeta();
        }

        /// <summary>
        /// Load world metadata file
        /// </summary>
        private void LoadWorldMeta()
        {
            string metaPath = Path.Combine(savePath, "world.meta");

            if (!File.Exists(metaPath))
            {
                // New world - will be initialized when first entity is saved
                return;
            }

            try
            {
                using (var fileStream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fileStream))
                {
                    var header = reader.ReadWorldMetaHeader();
                    BinarySerializationHelpers.ValidateMagic(header.magic, WorldSaveConstants.MAGIC_WORLD_META, "World Meta");
                    BinarySerializationHelpers.ValidateVersion(header.version, WorldSaveConstants.VERSION_WORLD_META, "World Meta");

                    mainEntityGuid = header.mainEntityGuid;
                    worldSeed = header.seed;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load world metadata: {e.Message}");
            }
        }

        /// <summary>
        /// Save world metadata file
        /// </summary>
        public void SaveWorldMeta(Guid mainEntityGuid, int seed)
        {
            this.mainEntityGuid = mainEntityGuid;
            this.worldSeed = seed;

            string metaPath = Path.Combine(savePath, "world.meta");

            try
            {
                using (var fileStream = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(fileStream))
                {
                    var header = new WorldMetaHeader
                    {
                        magic = WorldSaveConstants.MAGIC_WORLD_META,
                        version = WorldSaveConstants.VERSION_WORLD_META,
                        seed = seed,
                        mainEntityGuid = mainEntityGuid
                    };
                    writer.Write(header);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save world metadata: {e.Message}");
            }
        }

        // ===== Sector Save/Load (for infinite entities) =====

        /// <summary>
        /// Save a sector from an infinite entity to a region file
        /// </summary>
        public unsafe void SaveSector(Guid entityGuid, int3 sectorPos, Sector* sector)
        {
            int3 regionPos = WorldSaveConstants.GetRegionCoords(sectorPos);
            var regionFile = GetOrCreateRegionFile(regionPos);
            regionFile.SaveSector(entityGuid, sectorPos, sector);
        }

        /// <summary>
        /// Load a sector for an infinite entity from a region file
        /// Returns true if sector was found and loaded
        /// </summary>
        public unsafe bool LoadSector(Guid entityGuid, int3 sectorPos, Sector* sector)
        {
            int3 regionPos = WorldSaveConstants.GetRegionCoords(sectorPos);
            var regionFile = GetOrCreateRegionFile(regionPos);
            return regionFile.LoadSector(entityGuid, sectorPos, sector);
        }

        /// <summary>
        /// Check if a sector exists in storage (distinguishes empty from never-generated)
        /// </summary>
        public bool HasSector(Guid entityGuid, int3 sectorPos)
        {
            int3 regionPos = WorldSaveConstants.GetRegionCoords(sectorPos);
            var regionFile = GetOrCreateRegionFile(regionPos);
            return regionFile.HasSector(entityGuid, sectorPos);
        }

        // ===== Entity Save/Load =====

        /// <summary>
        /// Save entity metadata to the entity index
        /// </summary>
        public void SaveEntityMetadata(EntityMetadata metadata)
        {
            // Save to all index grids that this entity intersects
            var indexGrids = GetIntersectingIndexGrids(metadata);

            foreach (var gridPos in indexGrids)
            {
                var indexFile = GetOrCreateEntityIndexFile(gridPos);
                indexFile.SetEntity(metadata);
            }
        }

        /// <summary>
        /// Load entity metadata by GUID
        /// Searches all relevant index files
        /// </summary>
        public bool LoadEntityMetadata(Guid entityGuid, out EntityMetadata metadata)
        {
            // For now, search all loaded index files
            // Could be optimized with a runtime cache
            foreach (var indexFile in entityIndexFiles.Values)
            {
                if (indexFile.TryGetEntity(entityGuid, out metadata))
                    return true;
            }

            metadata = default;
            return false;
        }

        /// <summary>
        /// Save a complete finite entity to an entity archive
        /// </summary>
        public unsafe void SaveFiniteEntity(EntityMetadata metadata, Dictionary<int3, SectorHandle> sectors)
        {
            if (metadata.type != EntityType.Finite)
            {
                Debug.LogError($"Attempted to save non-finite entity {metadata.guid} to entity archive");
                return;
            }

            // Determine archive grid from entity center (or first sector)
            int3 archiveGrid = GetEntityArchiveGrid(metadata, sectors);
            var archiveFile = GetOrCreateEntityArchiveFile(archiveGrid);

            archiveFile.SaveEntity(metadata, sectors);

            // Also save metadata to index
            SaveEntityMetadata(metadata);
        }

        /// <summary>
        /// Load a complete finite entity from an entity archive
        /// </summary>
        public unsafe bool LoadFiniteEntity(Guid entityGuid, out EntityMetadata metadata, out Dictionary<int3, Sector*> sectors)
        {
            metadata = default;
            sectors = null;

            // First, find the entity metadata to determine which archive to check
            if (!LoadEntityMetadata(entityGuid, out metadata))
                return false;

            if (metadata.type != EntityType.Finite)
            {
                Debug.LogError($"Attempted to load non-finite entity {entityGuid} from entity archive");
                return false;
            }

            // Determine archive grid
            int3 archiveGrid = GetEntityArchiveGridFromMetadata(metadata);
            var archiveFile = GetOrCreateEntityArchiveFile(archiveGrid);

            return archiveFile.LoadEntity(entityGuid, out metadata, out sectors);
        }

        /// <summary>
        /// Remove an entity from storage
        /// </summary>
        public void RemoveEntity(Guid entityGuid, EntityMetadata metadata)
        {
            // Remove from entity indices
            var indexGrids = GetIntersectingIndexGrids(metadata);
            foreach (var gridPos in indexGrids)
            {
                var indexFile = GetOrCreateEntityIndexFile(gridPos);
                indexFile.RemoveEntity(entityGuid);
            }

            // If finite, remove from archive
            if (metadata.type == EntityType.Finite)
            {
                int3 archiveGrid = GetEntityArchiveGridFromMetadata(metadata);
                var archiveFile = GetOrCreateEntityArchiveFile(archiveGrid);
                archiveFile.RemoveEntity(entityGuid);
            }
        }

        // ===== Queries =====

        /// <summary>
        /// Get all entities that intersect a region
        /// Used for determining which entities to load when streaming
        /// </summary>
        public List<EntityMetadata> GetEntitiesIntersectingRegion(int3 regionPos)
        {
            var result = new List<EntityMetadata>();

            // Calculate which index grid this region belongs to
            int3 indexGrid = WorldSaveConstants.GetIndexGridCoords(regionPos);
            var indexFile = GetOrCreateEntityIndexFile(indexGrid);

            result.AddRange(indexFile.GetEntitiesIntersectingRegion(regionPos));

            return result;
        }

        // ===== Flush & Cleanup =====

        /// <summary>
        /// Flush all pending writes to disk
        /// </summary>
        public void FlushAll()
        {
            foreach (var regionFile in regionFiles.Values)
            {
                regionFile.Flush();
            }

            foreach (var indexFile in entityIndexFiles.Values)
            {
                indexFile.Save();
            }

            foreach (var archiveFile in entityArchiveFiles.Values)
            {
                archiveFile.Save();
            }
        }

        /// <summary>
        /// Unload cached files to free memory
        /// </summary>
        public void UnloadCaches()
        {
            // Flush before unloading
            FlushAll();

            // Dispose and clear caches
            foreach (var regionFile in regionFiles.Values)
            {
                regionFile.Dispose();
            }
            regionFiles.Clear();

            entityIndexFiles.Clear();
            entityArchiveFiles.Clear();
        }

        public void Dispose()
        {
            if (!isDisposed)
            {
                FlushAll();
                UnloadCaches();
                isDisposed = true;
            }
        }

        // ===== Private Helpers =====

        private RegionFile GetOrCreateRegionFile(int3 regionPos)
        {
            if (!regionFiles.TryGetValue(regionPos, out var regionFile))
            {
                regionFile = new RegionFile(savePath, regionPos);
                regionFiles[regionPos] = regionFile;
            }
            return regionFile;
        }

        private EntityIndexFile GetOrCreateEntityIndexFile(int3 gridPos)
        {
            if (!entityIndexFiles.TryGetValue(gridPos, out var indexFile))
            {
                indexFile = new EntityIndexFile(savePath, gridPos);
                entityIndexFiles[gridPos] = indexFile;
            }
            return indexFile;
        }

        private EntityArchiveFile GetOrCreateEntityArchiveFile(int3 gridPos)
        {
            if (!entityArchiveFiles.TryGetValue(gridPos, out var archiveFile))
            {
                archiveFile = new EntityArchiveFile(savePath, gridPos);
                entityArchiveFiles[gridPos] = archiveFile;
            }
            return archiveFile;
        }

        private List<int3> GetIntersectingIndexGrids(EntityMetadata metadata)
        {
            var result = new List<int3>();

            if (metadata.type == EntityType.Infinite)
            {
                // Infinite entities only in grid 0,0,0
                result.Add(new int3(0, 0, 0));
            }
            else if (metadata.hasAABB)
            {
                // Calculate all index grids that intersect the AABB
                int sectorSize = Sector.SECTOR_SIZE_IN_BLOCKS;
                int regionSizeInSectors = WorldSaveConstants.REGION_SIZE_IN_SECTORS;
                int indexGridSizeInRegions = WorldSaveConstants.INDEX_GRID_SIZE_IN_REGIONS;

                int regionSizeInBlocks = sectorSize * regionSizeInSectors;
                int indexGridSizeInBlocks = regionSizeInBlocks * indexGridSizeInRegions;

                int3 minGrid = new int3(
                    (int)math.floor(metadata.aabbMin.x / indexGridSizeInBlocks),
                    (int)math.floor(metadata.aabbMin.y / indexGridSizeInBlocks),
                    (int)math.floor(metadata.aabbMin.z / indexGridSizeInBlocks)
                );

                int3 maxGrid = new int3(
                    (int)math.floor(metadata.aabbMax.x / indexGridSizeInBlocks),
                    (int)math.floor(metadata.aabbMax.y / indexGridSizeInBlocks),
                    (int)math.floor(metadata.aabbMax.z / indexGridSizeInBlocks)
                );

                for (int x = minGrid.x; x <= maxGrid.x; x++)
                {
                    for (int y = minGrid.y; y <= maxGrid.y; y++)
                    {
                        for (int z = minGrid.z; z <= maxGrid.z; z++)
                        {
                            result.Add(new int3(x, y, z));
                        }
                    }
                }
            }

            return result;
        }

        private int3 GetEntityArchiveGrid(EntityMetadata metadata, Dictionary<int3, SectorHandle> sectors)
        {
            // Use AABB center if available
            if (metadata.hasAABB)
            {
                return GetEntityArchiveGridFromMetadata(metadata);
            }

            // Otherwise use first sector position
            if (sectors.Count > 0)
            {
                foreach (var sectorPos in sectors.Keys)
                {
                    int3 regionPos = WorldSaveConstants.GetRegionCoords(sectorPos);
                    return WorldSaveConstants.GetArchiveGridCoords(regionPos);
                }
            }

            // Fallback to origin
            return new int3(0, 0, 0);
        }

        private int3 GetEntityArchiveGridFromMetadata(EntityMetadata metadata)
        {
            if (!metadata.hasAABB)
                return new int3(0, 0, 0);

            float3 center = (metadata.aabbMin + metadata.aabbMax) * 0.5f;

            int sectorSize = Sector.SECTOR_SIZE_IN_BLOCKS;
            int regionSizeInSectors = WorldSaveConstants.REGION_SIZE_IN_SECTORS;
            int archiveGridSizeInRegions = WorldSaveConstants.ARCHIVE_GRID_SIZE_IN_REGIONS;

            int regionSizeInBlocks = sectorSize * regionSizeInSectors;
            int archiveGridSizeInBlocks = regionSizeInBlocks * archiveGridSizeInRegions;

            return new int3(
                (int)math.floor(center.x / archiveGridSizeInBlocks),
                (int)math.floor(center.y / archiveGridSizeInBlocks),
                (int)math.floor(center.z / archiveGridSizeInBlocks)
            );
        }
    }
}
