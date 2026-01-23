using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Persistence
{
    /// <summary>
    /// Manages reading and writing sector data to region files (.vxr).
    /// Handles both infinite (fixed grid) and finite (variable packing) region types.
    /// </summary>
    public unsafe class RegionFileManager
    {
        private readonly string saveDataPath;

        public RegionFileManager(string baseSaveDirectory = null)
        {
            if (string.IsNullOrEmpty(baseSaveDirectory))
            {
                saveDataPath = Path.Combine(Application.persistentDataPath, PersistenceConstants.SaveDataDirectory);
            }
            else
            {
                saveDataPath = baseSaveDirectory;
            }

            // Ensure directories exist
            Directory.CreateDirectory(Path.Combine(saveDataPath, PersistenceConstants.InfiniteRegionDirectory));
            Directory.CreateDirectory(Path.Combine(saveDataPath, PersistenceConstants.FiniteRegionDirectory));
        }

        /// <summary>
        /// Gets the region file path for a given sector position (infinite regions only).
        /// </summary>
        public string GetInfiniteRegionPath(int3 sectorPosition)
        {
            int3 regionSize = PersistenceConstants.InfiniteRegionSize;
            int3 regionCoord = new int3(
                (int)math.floor((float)sectorPosition.x / regionSize.x),
                (int)math.floor((float)sectorPosition.y / regionSize.y),
                (int)math.floor((float)sectorPosition.z / regionSize.z)
            );

            string filename = $"region_{regionCoord.x}_{regionCoord.y}_{regionCoord.z}.vxr";
            return Path.Combine(saveDataPath, PersistenceConstants.InfiniteRegionDirectory, filename);
        }

        /// <summary>
        /// Gets the finite region file path for a given chunk index.
        /// </summary>
        public string GetFiniteRegionPath(int chunkIndex)
        {
            string filename = $"entities_chunk_{chunkIndex}.vxr";
            return Path.Combine(saveDataPath, PersistenceConstants.FiniteRegionDirectory, filename);
        }

        /// <summary>
        /// Creates a new region file with appropriate header.
        /// </summary>
        public void CreateRegion(string regionPath, RegionType type, int3? gridSize = null)
        {
            // Ensure directory exists
            string directory = Path.GetDirectoryName(regionPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream fs = new FileStream(regionPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                RegionFileHeader header = new RegionFileHeader
                {
                    magic = FileMagic.Region,
                    version = PersistenceConstants.CurrentVersion,
                    regionType = type,
                    flags = 0,
                    regionSize = gridSize ?? PersistenceConstants.InfiniteRegionSize,
                    sectorCount = 0
                };

                // Write header
                byte* headerPtr = (byte*)&header;
                for (int i = 0; i < RegionFileHeader.Size; i++)
                {
                    writer.Write(headerPtr[i]);
                }
            }
        }

        /// <summary>
        /// Checks if a region file exists.
        /// </summary>
        public bool RegionExists(string regionPath)
        {
            return File.Exists(regionPath);
        }

        /// <summary>
        /// Writes a single sector to a region file.
        /// If the region doesn't exist, it will be created.
        /// </summary>
        public void WriteSector(string regionPath, int3 sectorKey, Sector* sector, RegionType regionType)
        {
            if (sector == null)
                throw new ArgumentNullException(nameof(sector));

            // Estimate buffer size
            int estimatedSize = VoxelCompression.EstimateSectorCompressedSize(sector);
            byte[] buffer = new byte[estimatedSize];

            int compressedSize;
            fixed (byte* bufferPtr = buffer)
            {
                compressedSize = VoxelCompression.CompressSector(sector, sectorKey, bufferPtr, estimatedSize);
                if (compressedSize < 0)
                {
                    throw new InvalidOperationException("Failed to compress sector data");
                }
            }

            // If region doesn't exist, create it
            if (!RegionExists(regionPath))
            {
                CreateRegion(regionPath, regionType);
            }

            // Read existing index
            Dictionary<int3, SectorIndexEntry> existingIndex = ReadSectorIndex(regionPath);

            // Calculate checksum
            uint checksum = CalculateCRC32(buffer, compressedSize);

            // Determine write position (append to end)
            ulong fileOffset;
            using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.Read))
            {
                fileOffset = (ulong)fs.Length;
            }

            // Write sector data
            using (FileStream fs = new FileStream(regionPath, FileMode.Append, FileAccess.Write))
            {
                fs.Write(buffer, 0, compressedSize);
            }

            // Update index
            SectorIndexEntry entry = new SectorIndexEntry
            {
                sectorKey = sectorKey,
                fileOffset = fileOffset,
                dataLength = (uint)compressedSize,
                checksum = checksum
            };

            existingIndex[sectorKey] = entry;

            // Write updated index
            WriteSectorIndex(regionPath, existingIndex, regionType);
        }

        /// <summary>
        /// Reads a single sector from a region file.
        /// The sector must be pre-allocated by the caller.
        /// </summary>
        public bool ReadSector(string regionPath, int3 sectorKey, Sector* outSector)
        {
            if (!RegionExists(regionPath))
                return false;

            // Read index
            Dictionary<int3, SectorIndexEntry> index = ReadSectorIndex(regionPath);

            if (!index.TryGetValue(sectorKey, out SectorIndexEntry entry))
                return false;

            // Read compressed data
            byte[] buffer = new byte[entry.dataLength];
            using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.Read))
            {
                fs.Seek((long)entry.fileOffset, SeekOrigin.Begin);
                int bytesRead = fs.Read(buffer, 0, (int)entry.dataLength);
                if (bytesRead != entry.dataLength)
                    return false;
            }

            // Verify checksum
            uint checksum = CalculateCRC32(buffer, buffer.Length);
            if (checksum != entry.checksum)
            {
                Debug.LogWarning($"Checksum mismatch for sector {sectorKey} in {regionPath}");
                return false;
            }

            // Decompress
            fixed (byte* bufferPtr = buffer)
            {
                int result = VoxelCompression.DecompressSector(bufferPtr, buffer.Length, outSector);
                if (result < 0)
                {
                    Debug.LogError($"Failed to decompress sector {sectorKey} from {regionPath}");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads the sector index from a region file.
        /// </summary>
        private Dictionary<int3, SectorIndexEntry> ReadSectorIndex(string regionPath)
        {
            Dictionary<int3, SectorIndexEntry> index = new Dictionary<int3, SectorIndexEntry>();

            if (!File.Exists(regionPath))
                return index;

            using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                // Read header
                RegionFileHeader header = ReadRegionHeader(reader);

                // Read index entries
                for (int i = 0; i < header.sectorCount; i++)
                {
                    SectorIndexEntry entry = new SectorIndexEntry();

                    entry.sectorKey = new int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    entry.fileOffset = reader.ReadUInt64();
                    entry.dataLength = reader.ReadUInt32();
                    entry.checksum = reader.ReadUInt32();

                    index[entry.sectorKey] = entry;
                }
            }

            return index;
        }

        /// <summary>
        /// Writes the sector index to a region file.
        /// Rewrites the header and index table.
        /// </summary>
        private void WriteSectorIndex(string regionPath, Dictionary<int3, SectorIndexEntry> index, RegionType regionType)
        {
            // Read existing data beyond the header+index
            byte[] sectorData = null;
            ulong dataStartOffset = 0;

            if (File.Exists(regionPath))
            {
                using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Read old header to get old sector count
                    RegionFileHeader oldHeader = ReadRegionHeader(reader);

                    // Calculate where sector data starts
                    dataStartOffset = (ulong)(RegionFileHeader.Size + oldHeader.sectorCount * SectorIndexEntry.Size);

                    if (fs.Length > (long)dataStartOffset)
                    {
                        fs.Seek((long)dataStartOffset, SeekOrigin.Begin);
                        sectorData = new byte[fs.Length - (long)dataStartOffset];
                        fs.Read(sectorData, 0, sectorData.Length);
                    }
                }
            }

            // Write new header and index
            using (FileStream fs = new FileStream(regionPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                // Write header
                RegionFileHeader header = new RegionFileHeader
                {
                    magic = FileMagic.Region,
                    version = PersistenceConstants.CurrentVersion,
                    regionType = regionType,
                    flags = 0,
                    regionSize = PersistenceConstants.InfiniteRegionSize,
                    sectorCount = (uint)index.Count
                };

                byte* headerPtr = (byte*)&header;
                for (int i = 0; i < RegionFileHeader.Size; i++)
                {
                    writer.Write(headerPtr[i]);
                }

                // Write index entries
                foreach (var kvp in index)
                {
                    writer.Write(kvp.Value.sectorKey.x);
                    writer.Write(kvp.Value.sectorKey.y);
                    writer.Write(kvp.Value.sectorKey.z);
                    writer.Write(kvp.Value.fileOffset);
                    writer.Write(kvp.Value.dataLength);
                    writer.Write(kvp.Value.checksum);
                }

                // Write sector data
                if (sectorData != null)
                {
                    writer.Write(sectorData);
                }
            }
        }

        /// <summary>
        /// Reads the region file header.
        /// </summary>
        private RegionFileHeader ReadRegionHeader(BinaryReader reader)
        {
            RegionFileHeader header = new RegionFileHeader();
            byte* headerPtr = (byte*)&header;

            for (int i = 0; i < RegionFileHeader.Size; i++)
            {
                headerPtr[i] = reader.ReadByte();
            }

            // Validate magic
            if (header.magic != FileMagic.Region)
            {
                throw new InvalidDataException($"Invalid region file magic: 0x{header.magic:X8}");
            }

            return header;
        }

        /// <summary>
        /// Calculates CRC32 checksum for data validation.
        /// </summary>
        private uint CalculateCRC32(byte[] data, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
            {
                byte index = (byte)(((crc) & 0xFF) ^ data[i]);
                crc = (crc >> 8) ^ CRC32Table[index];
            }
            return ~crc;
        }

        // CRC32 lookup table
        private static readonly uint[] CRC32Table = GenerateCRC32Table();

        private static uint[] GenerateCRC32Table()
        {
            uint[] table = new uint[256];
            uint polynomial = 0xEDB88320;

            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }

            return table;
        }

        /// <summary>
        /// Gets all sector keys stored in a region file.
        /// </summary>
        public List<int3> GetAllSectorKeys(string regionPath)
        {
            Dictionary<int3, SectorIndexEntry> index = ReadSectorIndex(regionPath);
            return new List<int3>(index.Keys);
        }

        /// <summary>
        /// Compacts a region file by removing gaps and rebuilding the index.
        /// This should be called periodically to reclaim space from deleted/updated sectors.
        /// </summary>
        public void CompactRegion(string regionPath)
        {
            if (!RegionExists(regionPath))
                return;

            // Read all sectors
            Dictionary<int3, SectorIndexEntry> index = ReadSectorIndex(regionPath);
            Dictionary<int3, byte[]> sectorData = new Dictionary<int3, byte[]>();

            using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.Read))
            {
                foreach (var kvp in index)
                {
                    fs.Seek((long)kvp.Value.fileOffset, SeekOrigin.Begin);
                    byte[] data = new byte[kvp.Value.dataLength];
                    fs.Read(data, 0, (int)kvp.Value.dataLength);
                    sectorData[kvp.Key] = data;
                }
            }

            // Read region type from header
            RegionType regionType;
            using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                RegionFileHeader header = ReadRegionHeader(reader);
                regionType = header.regionType;
            }

            // Rebuild file with compacted data
            string tempPath = regionPath + ".tmp";
            CreateRegion(tempPath, regionType);

            Dictionary<int3, SectorIndexEntry> newIndex = new Dictionary<int3, SectorIndexEntry>();
            ulong currentOffset = (ulong)(RegionFileHeader.Size + sectorData.Count * SectorIndexEntry.Size);

            using (FileStream fs = new FileStream(tempPath, FileMode.Append, FileAccess.Write))
            {
                foreach (var kvp in sectorData)
                {
                    byte[] data = kvp.Value;
                    uint checksum = CalculateCRC32(data, data.Length);

                    SectorIndexEntry entry = new SectorIndexEntry
                    {
                        sectorKey = kvp.Key,
                        fileOffset = currentOffset,
                        dataLength = (uint)data.Length,
                        checksum = checksum
                    };

                    newIndex[kvp.Key] = entry;

                    fs.Write(data, 0, data.Length);
                    currentOffset += (uint)data.Length;
                }
            }

            // Write index
            WriteSectorIndex(tempPath, newIndex, regionType);

            // Replace original file
            File.Delete(regionPath);
            File.Move(tempPath, regionPath);
        }
    }
}
