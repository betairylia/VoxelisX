using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Manages a partitioned entity index file
    /// Each index file covers a spatial grid of regions and contains metadata for all entities in that space
    /// </summary>
    public class EntityIndexFile
    {
        private readonly string filePath;
        private readonly int3 gridCoords;
        private readonly Dictionary<Guid, EntityMetadata> entities;
        private bool isDirty;

        public EntityIndexFile(string savePath, int3 gridCoords)
        {
            this.gridCoords = gridCoords;
            this.filePath = Path.Combine(savePath, $"entity_index.{gridCoords.x}.{gridCoords.y}.{gridCoords.z}.eidx");
            this.entities = new Dictionary<Guid, EntityMetadata>();
            this.isDirty = false;

            Load();
        }

        /// <summary>
        /// Load entity metadata from the index file
        /// </summary>
        private void Load()
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fileStream))
                {
                    var header = reader.ReadEntityIndexFileHeader();
                    BinarySerializationHelpers.ValidateMagic(header.magic, WorldSaveConstants.MAGIC_ENTITY_INDEX, "Entity Index");
                    BinarySerializationHelpers.ValidateVersion(header.version, WorldSaveConstants.VERSION_ENTITY_INDEX, "Entity Index");

                    if (!header.gridCoords.Equals(gridCoords))
                    {
                        Debug.LogError($"Entity index grid coordinates mismatch: expected {gridCoords}, got {header.gridCoords}");
                        return;
                    }

                    for (uint i = 0; i < header.entityCount; i++)
                    {
                        var metadata = reader.ReadEntityMetadata();
                        entities[metadata.guid] = metadata;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load entity index from {filePath}: {e.Message}");
                entities.Clear();
            }
        }

        /// <summary>
        /// Add or update entity metadata
        /// </summary>
        public void SetEntity(EntityMetadata metadata)
        {
            entities[metadata.guid] = metadata;
            isDirty = true;
        }

        /// <summary>
        /// Remove entity metadata
        /// </summary>
        public void RemoveEntity(Guid guid)
        {
            if (entities.Remove(guid))
                isDirty = true;
        }

        /// <summary>
        /// Get entity metadata
        /// </summary>
        public bool TryGetEntity(Guid guid, out EntityMetadata metadata)
        {
            return entities.TryGetValue(guid, out metadata);
        }

        /// <summary>
        /// Get all entities in this index
        /// </summary>
        public IEnumerable<EntityMetadata> GetAllEntities()
        {
            return entities.Values;
        }

        /// <summary>
        /// Get all entities that intersect a specific region
        /// </summary>
        public List<EntityMetadata> GetEntitiesIntersectingRegion(int3 regionPos)
        {
            var result = new List<EntityMetadata>();

            foreach (var metadata in entities.Values)
            {
                if (metadata.IntersectsRegion(regionPos))
                    result.Add(metadata);
            }

            return result;
        }

        /// <summary>
        /// Save the index to disk
        /// </summary>
        public void Save()
        {
            if (!isDirty)
                return;

            try
            {
                // Ensure directory exists
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(fileStream))
                {
                    var header = new EntityIndexFileHeader
                    {
                        magic = WorldSaveConstants.MAGIC_ENTITY_INDEX,
                        version = WorldSaveConstants.VERSION_ENTITY_INDEX,
                        gridCoords = gridCoords,
                        entityCount = (uint)entities.Count
                    };
                    writer.Write(header);

                    foreach (var metadata in entities.Values)
                    {
                        writer.Write(metadata);
                    }
                }

                isDirty = false;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save entity index to {filePath}: {e.Message}");
            }
        }
    }
}
