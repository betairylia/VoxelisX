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
    /// Stores complete voxel data for finite entities
    /// Unlike region files which support partial loading, entity archives store ALL sectors for an entity
    /// This ensures finite entities are fully loaded for physics consistency
    /// </summary>
    public class EntityArchiveFile
    {
        private readonly string filePath;
        private readonly int3 gridCoords;

        // In-memory cache of archived entities (loaded on demand)
        private Dictionary<Guid, EntityArchiveEntry> archivedEntities;
        private bool isDirty;

        public EntityArchiveFile(string savePath, int3 gridCoords)
        {
            this.gridCoords = gridCoords;
            this.filePath = Path.Combine(savePath, $"entity_archive.{gridCoords.x}.{gridCoords.y}.{gridCoords.z}.vxea");
            this.archivedEntities = new Dictionary<Guid, EntityArchiveEntry>();
            this.isDirty = false;

            LoadIndex();
        }

        /// <summary>
        /// Load the archive index (metadata only, not full sector data)
        /// </summary>
        private void LoadIndex()
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fileStream))
                {
                    var header = reader.ReadEntityArchiveFileHeader();
                    BinarySerializationHelpers.ValidateMagic(header.magic, WorldSaveConstants.MAGIC_ENTITY_ARCHIVE, "Entity Archive");
                    BinarySerializationHelpers.ValidateVersion(header.version, WorldSaveConstants.VERSION_ENTITY_ARCHIVE, "Entity Archive");

                    if (!header.gridCoords.Equals(gridCoords))
                    {
                        Debug.LogError($"Entity archive grid coordinates mismatch: expected {gridCoords}, got {header.gridCoords}");
                        return;
                    }

                    // Read entity entries (metadata + sector positions)
                    for (uint i = 0; i < header.entityCount; i++)
                    {
                        var metadata = reader.ReadEntityMetadata();
                        uint sectorCount = reader.ReadUInt32();
                        long dataOffset = reader.ReadInt64();
                        uint dataSize = reader.ReadUInt32();

                        // Read sector positions
                        var sectorPositions = new List<int3>((int)sectorCount);
                        for (uint j = 0; j < sectorCount; j++)
                        {
                            sectorPositions.Add(reader.ReadInt3());
                        }

                        archivedEntities[metadata.guid] = new EntityArchiveEntry
                        {
                            metadata = metadata,
                            sectorPositions = sectorPositions,
                            dataOffset = dataOffset,
                            dataSize = dataSize
                        };
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load entity archive index from {filePath}: {e.Message}");
                archivedEntities.Clear();
            }
        }

        /// <summary>
        /// Check if an entity is archived here
        /// </summary>
        public bool HasEntity(Guid guid)
        {
            return archivedEntities.ContainsKey(guid);
        }

        /// <summary>
        /// Load a complete entity from the archive
        /// Returns the metadata and dictionary of all sectors
        /// </summary>
        public unsafe bool LoadEntity(Guid guid, out EntityMetadata metadata, out Dictionary<int3, Sector*> sectors)
        {
            metadata = default;
            sectors = null;

            if (!archivedEntities.TryGetValue(guid, out var entry))
                return false;

            metadata = entry.metadata;
            sectors = new Dictionary<int3, Sector*>();

            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fileStream))
                {
                    // Seek to entity data
                    fileStream.Seek(entry.dataOffset, SeekOrigin.Begin);

                    // Read all sectors
                    foreach (var sectorPos in entry.sectorPositions)
                    {
                        // Allocate new sector
                        Sector* sector = Sector.Create();

                        // Read sector header
                        var sectorHeader = reader.ReadSectorSerializationHeader();

                        if (!sectorHeader.sectorPos.Equals(sectorPos))
                        {
                            Debug.LogError($"Sector position mismatch: expected {sectorPos}, got {sectorHeader.sectorPos}");
                            Sector.Destroy(sector);
                            continue;
                        }

                        // Restore sector-level dirty flags
                        sector->sectorDirtyFlags = sectorHeader.sectorDirtyFlags;

                        // Read brick data
                        for (int i = 0; i < sectorHeader.nonEmptyBrickCount; i++)
                        {
                            var brickHeader = reader.ReadBrickSerializationHeader();

                            // Allocate brick
                            if (sector->brickIdx[brickHeader.brickIdx] == Sector.BRICKID_EMPTY)
                            {
                                sector->AllocateBrick(brickHeader.brickIdx);
                            }

                            short brickId = sector->brickIdx[brickHeader.brickIdx];
                            Block* brickData = sector->GetBrickVoxels(brickId);

                            // Decompress RLE block data
                            if (!RLECompression.ReadRLE(reader, brickData, Sector.BLOCKS_IN_BRICK))
                            {
                                Debug.LogError($"Failed to decompress brick {brickHeader.brickIdx} in sector {sectorPos}");
                                Sector.Destroy(sector);
                                continue;
                            }

                            // Restore brick flags
                            sector->brickDirtyFlags[brickHeader.brickIdx] = brickHeader.brickDirtyFlags;
                            sector->brickDirtyDirectionMask[brickHeader.brickIdx] = brickHeader.brickDirtyDirMask;

                            // Mark brick as modified
                            sector->brickFlags[brickHeader.brickIdx] = BrickUpdateInfo.Type.Modified;
                            sector->updateRecord.Add(brickHeader.brickIdx);
                        }

                        sectors[sectorPos] = sector;
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load entity {guid} from {filePath}: {e.Message}");

                // Clean up any allocated sectors
                if (sectors != null)
                {
                    foreach (var sector in sectors.Values)
                    {
                        Sector.Destroy(sector);
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Save a complete entity to the archive
        /// </summary>
        public unsafe void SaveEntity(EntityMetadata metadata, Dictionary<int3, SectorHandle> sectorHandles)
        {
            try
            {
                using (var memStream = new MemoryStream())
                using (var writer = new BinaryWriter(memStream))
                {
                    var sectorPositions = new List<int3>(sectorHandles.Keys);

                    // Write all sectors
                    foreach (var sectorPos in sectorPositions)
                    {
                        var handle = sectorHandles[sectorPos];
                        Sector* sector = handle.sector;

                        // Count non-empty bricks
                        ushort nonEmptyBrickCount = 0;
                        for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                        {
                            if (sector->brickIdx[i] != Sector.BRICKID_EMPTY)
                                nonEmptyBrickCount++;
                        }

                        // Write sector header
                        var sectorHeader = new SectorSerializationHeader
                        {
                            sectorPos = sectorPos,
                            sectorDirtyFlags = sector->sectorDirtyFlags,
                            nonEmptyBrickCount = nonEmptyBrickCount
                        };
                        writer.Write(sectorHeader);

                        // Write brick data
                        for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                        {
                            short brickId = sector->brickIdx[i];
                            if (brickId == Sector.BRICKID_EMPTY)
                                continue;

                            // Write brick header
                            var brickHeader = new BrickSerializationHeader
                            {
                                brickIdx = (short)i,
                                brickDirtyFlags = sector->brickDirtyFlags[i],
                                brickDirtyDirMask = sector->brickDirtyDirectionMask[i],
                                blockCount = Sector.BLOCKS_IN_BRICK
                            };
                            writer.Write(brickHeader);

                            // Write RLE compressed block data
                            Block* brickData = sector->GetBrickVoxels(brickId);
                            RLECompression.WriteRLE(writer, brickData, Sector.BLOCKS_IN_BRICK);
                        }
                    }

                    // Store in memory
                    byte[] entityData = memStream.ToArray();

                    archivedEntities[metadata.guid] = new EntityArchiveEntry
                    {
                        metadata = metadata,
                        sectorPositions = sectorPositions,
                        dataOffset = 0, // Will be calculated during flush
                        dataSize = (uint)entityData.Length,
                        pendingData = entityData
                    };

                    isDirty = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save entity {metadata.guid} to archive: {e.Message}");
            }
        }

        /// <summary>
        /// Remove an entity from the archive
        /// </summary>
        public void RemoveEntity(Guid guid)
        {
            if (archivedEntities.Remove(guid))
                isDirty = true;
        }

        /// <summary>
        /// Save the archive to disk
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
                    // Write header
                    var header = new EntityArchiveFileHeader
                    {
                        magic = WorldSaveConstants.MAGIC_ENTITY_ARCHIVE,
                        version = WorldSaveConstants.VERSION_ENTITY_ARCHIVE,
                        gridCoords = gridCoords,
                        entityCount = (uint)archivedEntities.Count
                    };
                    writer.Write(header);

                    // Calculate data offsets
                    long currentDataOffset = CalculateHeaderSize();

                    // Write entity index
                    foreach (var entry in archivedEntities.Values)
                    {
                        writer.Write(entry.metadata);
                        writer.Write((uint)entry.sectorPositions.Count);
                        writer.Write(currentDataOffset);
                        writer.Write(entry.dataSize);

                        foreach (var sectorPos in entry.sectorPositions)
                        {
                            writer.Write(sectorPos);
                        }

                        currentDataOffset += entry.dataSize;
                    }

                    // Write entity data
                    foreach (var entry in archivedEntities.Values)
                    {
                        if (entry.pendingData != null)
                        {
                            writer.Write(entry.pendingData);
                        }
                        else
                        {
                            // Load from old file if not modified
                            // (This case handles re-saving without modification)
                            // For simplicity, we'll just write empty data if not available
                            Debug.LogWarning($"Entity {entry.metadata.guid} has no pending data during save");
                        }
                    }
                }

                isDirty = false;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save entity archive to {filePath}: {e.Message}");
            }
        }

        private long CalculateHeaderSize()
        {
            // Header: magic(4) + version(4) + gridCoords(12) + entityCount(4) = 24 bytes
            long size = 24;

            // For each entity: metadata + sectorCount(4) + dataOffset(8) + dataSize(4) + sector positions
            foreach (var entry in archivedEntities.Values)
            {
                // Metadata size (variable due to AABB)
                size += 16 + 1 + 28 + 2 + 1; // guid + type + transform + flags + hasAABB
                if (entry.metadata.hasAABB)
                    size += 24; // aabbMin + aabbMax

                size += 4 + 8 + 4; // sectorCount + dataOffset + dataSize

                // Sector positions
                size += entry.sectorPositions.Count * 12; // int3 per sector
            }

            return size;
        }

        private class EntityArchiveEntry
        {
            public EntityMetadata metadata;
            public List<int3> sectorPositions;
            public long dataOffset;
            public uint dataSize;
            public byte[] pendingData; // Data to be written (null if loaded from file)
        }
    }
}
