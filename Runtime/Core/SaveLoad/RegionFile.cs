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
    /// Manages a single region file for storing sectors of infinite entities
    /// Supports per-sector streaming: can read/write individual sectors without loading the whole region
    /// </summary>
    public class RegionFile : IDisposable
    {
        private readonly string filePath;
        private readonly int3 regionCoords;
        private bool isDisposed;

        // Index: entityGuid -> (sectorLocalPos -> SectorIndexEntry)
        private Dictionary<Guid, Dictionary<int3, SectorIndexEntry>> sectorIndex;
        private bool indexDirty;

        public RegionFile(string savePath, int3 regionCoords)
        {
            this.regionCoords = regionCoords;
            this.filePath = Path.Combine(savePath, $"region.{regionCoords.x}.{regionCoords.y}.{regionCoords.z}.vxr");
            this.sectorIndex = new Dictionary<Guid, Dictionary<int3, SectorIndexEntry>>();
            this.indexDirty = false;

            LoadIndex();
        }

        /// <summary>
        /// Load the sector index from the region file (if it exists)
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
                    // Read header
                    var header = reader.ReadRegionFileHeader();
                    BinarySerializationHelpers.ValidateMagic(header.magic, WorldSaveConstants.MAGIC_REGION, "Region");
                    BinarySerializationHelpers.ValidateVersion(header.version, WorldSaveConstants.VERSION_REGION, "Region");

                    if (!header.regionCoords.Equals(regionCoords))
                    {
                        Debug.LogError($"Region file coordinates mismatch: expected {regionCoords}, got {header.regionCoords}");
                        return;
                    }

                    // Read entity index
                    for (uint i = 0; i < header.entityCount; i++)
                    {
                        Guid entityGuid = reader.ReadGuid();
                        uint sectorCount = reader.ReadUInt32();

                        var entitySectors = new Dictionary<int3, SectorIndexEntry>();

                        for (uint j = 0; j < sectorCount; j++)
                        {
                            var entry = reader.ReadSectorIndexEntry();
                            entitySectors[entry.sectorLocalPos] = entry;
                        }

                        sectorIndex[entityGuid] = entitySectors;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load region index from {filePath}: {e.Message}");
                sectorIndex.Clear();
            }
        }

        /// <summary>
        /// Check if a sector exists in this region
        /// </summary>
        public bool HasSector(Guid entityGuid, int3 sectorPos)
        {
            int3 localPos = WorldSaveConstants.GetSectorLocalPos(sectorPos, regionCoords);
            return sectorIndex.TryGetValue(entityGuid, out var entitySectors) &&
                   entitySectors.ContainsKey(localPos);
        }

        /// <summary>
        /// Load a single sector from the region file
        /// </summary>
        public unsafe bool LoadSector(Guid entityGuid, int3 sectorPos, Sector* sector)
        {
            int3 localPos = WorldSaveConstants.GetSectorLocalPos(sectorPos, regionCoords);

            if (!sectorIndex.TryGetValue(entityGuid, out var entitySectors) ||
                !entitySectors.TryGetValue(localPos, out var indexEntry))
            {
                return false; // Sector not in this region
            }

            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fileStream))
                {
                    // Seek to sector data
                    fileStream.Seek(indexEntry.dataOffset, SeekOrigin.Begin);

                    // Read sector header
                    var sectorHeader = reader.ReadSectorSerializationHeader();

                    if (!sectorHeader.sectorPos.Equals(sectorPos))
                    {
                        Debug.LogError($"Sector position mismatch: expected {sectorPos}, got {sectorHeader.sectorPos}");
                        return false;
                    }

                    // Restore sector-level dirty flags
                    sector->sectorDirtyFlags = sectorHeader.sectorDirtyFlags;

                    // Read brick data
                    for (int i = 0; i < sectorHeader.nonEmptyBrickCount; i++)
                    {
                        var brickHeader = reader.ReadBrickSerializationHeader();

                        // Ensure brick is allocated
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
                            return false;
                        }

                        // Restore brick flags
                        sector->brickDirtyFlags[brickHeader.brickIdx] = brickHeader.brickDirtyFlags;
                        sector->brickDirtyDirectionMask[brickHeader.brickIdx] = brickHeader.brickDirtyDirMask;

                        // Mark brick as modified for renderer update
                        sector->brickFlags[brickHeader.brickIdx] = BrickUpdateInfo.Type.Modified;
                        sector->updateRecord.Add(brickHeader.brickIdx);
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load sector {sectorPos} from {filePath}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Save a single sector to the region file
        /// Updates the index and appends to the file
        /// </summary>
        public unsafe void SaveSector(Guid entityGuid, int3 sectorPos, Sector* sector)
        {
            int3 localPos = WorldSaveConstants.GetSectorLocalPos(sectorPos, regionCoords);

            // Count non-empty bricks
            ushort nonEmptyBrickCount = 0;
            for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
            {
                if (sector->brickIdx[i] != Sector.BRICKID_EMPTY)
                    nonEmptyBrickCount++;
            }

            try
            {
                using (var memStream = new MemoryStream())
                using (var writer = new BinaryWriter(memStream))
                {
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

                    // Get serialized data
                    byte[] sectorData = memStream.ToArray();

                    // Append to file and update index
                    long dataOffset;
                    using (var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    {
                        fileStream.Seek(0, SeekOrigin.End);
                        dataOffset = fileStream.Position;
                        fileStream.Write(sectorData, 0, sectorData.Length);
                    }

                    // Update index
                    if (!sectorIndex.TryGetValue(entityGuid, out var entitySectors))
                    {
                        entitySectors = new Dictionary<int3, SectorIndexEntry>();
                        sectorIndex[entityGuid] = entitySectors;
                    }

                    entitySectors[localPos] = new SectorIndexEntry
                    {
                        sectorLocalPos = localPos,
                        dataOffset = dataOffset,
                        dataSize = (uint)sectorData.Length
                    };

                    indexDirty = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save sector {sectorPos} to {filePath}: {e.Message}");
            }
        }

        /// <summary>
        /// Flush the index to disk (called after saving sectors)
        /// Rewrites the entire file with updated index
        /// </summary>
        public void Flush()
        {
            if (!indexDirty)
                return;

            try
            {
                // Create temporary file
                string tempPath = filePath + ".tmp";

                using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(tempStream))
                {
                    // Calculate header offset for sector data
                    long sectorDataOffset = CalculateHeaderSize();

                    // Write header
                    var header = new RegionFileHeader
                    {
                        magic = WorldSaveConstants.MAGIC_REGION,
                        version = WorldSaveConstants.VERSION_REGION,
                        regionCoords = regionCoords,
                        entityCount = (uint)sectorIndex.Count,
                        sectorDataOffset = sectorDataOffset
                    };
                    writer.Write(header);

                    // Write entity index
                    foreach (var entityEntry in sectorIndex)
                    {
                        writer.Write(entityEntry.Key);
                        writer.Write((uint)entityEntry.Value.Count);

                        foreach (var sectorEntry in entityEntry.Value.Values)
                        {
                            writer.Write(sectorEntry);
                        }
                    }

                    // Copy sector data from old file (if exists)
                    if (File.Exists(filePath))
                    {
                        using (var oldStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            // Find where sector data starts in old file
                            using (var reader = new BinaryReader(oldStream))
                            {
                                var oldHeader = reader.ReadRegionFileHeader();
                                oldStream.Seek(oldHeader.sectorDataOffset, SeekOrigin.Begin);

                                // Copy all sector data
                                oldStream.CopyTo(tempStream);
                            }
                        }
                    }
                }

                // Replace old file with new file
                if (File.Exists(filePath))
                    File.Delete(filePath);
                File.Move(tempPath, filePath);

                indexDirty = false;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to flush region file {filePath}: {e.Message}");
            }
        }

        private long CalculateHeaderSize()
        {
            // Header: magic(4) + version(4) + regionCoords(12) + entityCount(4) + sectorDataOffset(8) = 32 bytes
            long size = 32;

            // Entity index
            foreach (var entityEntry in sectorIndex)
            {
                // GUID (16) + sectorCount (4)
                size += 16 + 4;

                // Each sector entry: localPos(12) + dataOffset(8) + dataSize(4) = 24 bytes
                size += entityEntry.Value.Count * 24;
            }

            return size;
        }

        public void Dispose()
        {
            if (!isDisposed)
            {
                Flush();
                isDisposed = true;
            }
        }
    }
}
