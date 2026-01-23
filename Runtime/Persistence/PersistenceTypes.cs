using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis.Persistence
{
    /// <summary>
    /// Type of region file storage strategy.
    /// </summary>
    public enum RegionType : byte
    {
        /// <summary>Infinite entity with fixed grid (16x16x16 sectors per region).</summary>
        Infinite = 0,
        /// <summary>Finite entities with variable sector packing.</summary>
        Finite = 1
    }

    /// <summary>
    /// Magic numbers for file format identification.
    /// </summary>
    public static class FileMagic
    {
        /// <summary>Region file magic: "VXRG"</summary>
        public const uint Region = 0x47525856;
        /// <summary>Entity file magic: "VXEN"</summary>
        public const uint Entity = 0x4E455856;
    }

    /// <summary>
    /// Header for region files (.vxr).
    /// Fixed size: 56 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RegionFileHeader
    {
        public uint magic;              // 4 bytes: FileMagic.Region
        public ushort version;          // 2 bytes: format version
        public RegionType regionType;   // 1 byte
        public byte flags;              // 1 byte: compression flags, etc.
        public int3 regionSize;         // 12 bytes: for infinite regions (e.g., 16x16x16)
        public uint sectorCount;        // 4 bytes: number of sectors in file
        public fixed byte reserved[32]; // 32 bytes: reserved for future use

        public const int Size = 56;
    }

    /// <summary>
    /// Index entry for a sector within a region file.
    /// Fixed size: 28 bytes per entry.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SectorIndexEntry
    {
        public int3 sectorKey;      // 12 bytes: sector position (absolute or relative to region)
        public ulong fileOffset;    // 8 bytes: byte offset from start of file
        public uint dataLength;     // 4 bytes: compressed data length in bytes
        public uint checksum;       // 4 bytes: CRC32 checksum

        public const int Size = 28;
    }

    /// <summary>
    /// Header for sector data within a region file.
    /// Fixed size: 20 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SectorDataHeader
    {
        public int3 position;               // 12 bytes: sector position
        public ushort nonEmptyBrickCount;   // 2 bytes: number of non-empty bricks
        public ushort sectorDirtyFlags;     // 2 bytes: sector-level dirty flags
        public uint sectorNeighborsToCreate;// 4 bytes: neighbor creation bitmask

        public const int Size = 20;
    }

    /// <summary>
    /// Header for entity listing file (.vxe).
    /// Fixed size: 64 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct EntityFileHeader
    {
        public uint magic;              // 4 bytes: FileMagic.Entity
        public ushort version;          // 2 bytes: format version
        public ushort padding;          // 2 bytes: alignment
        public uint entityCount;        // 4 bytes: number of entities in file
        public fixed byte reserved[52]; // 52 bytes: reserved for future use

        public const int Size = 64;
    }

    /// <summary>
    /// Index entry for an entity within the entity listing file.
    /// Fixed size: 32 bytes per entry.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EntityIndexEntry
    {
        public Guid guid;           // 16 bytes: unique entity identifier
        public ulong fileOffset;    // 8 bytes: byte offset from start of file
        public uint dataLength;     // 4 bytes: entity data length in bytes
        public uint checksum;       // 4 bytes: CRC32 checksum

        public const int Size = 32;
    }

    /// <summary>
    /// Entity type flags for serialization.
    /// </summary>
    [Flags]
    public enum EntityFlags : byte
    {
        None = 0,
        HasPhysics = 1 << 0,
        IsInfinite = 1 << 1,
        IsStatic = 1 << 2,
    }

    /// <summary>
    /// Physics data for entities with VoxelBody.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PhysicsData
    {
        public float mass;              // 4 bytes
        public float3 centerOfMass;     // 12 bytes
        public float3 inertiaTensor;    // 12 bytes
        public byte isStatic;           // 1 byte (bool)

        public const int Size = 29;
    }

    /// <summary>
    /// Settings for infinite loader entities.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InfiniteLoaderSettings
    {
        public int3 sectorLoadBounds;           // 12 bytes
        public float sectorLoadRadiusInBlocks;  // 4 bytes
        public float sectorUnloadRadiusInBlocks;// 4 bytes

        public const int Size = 20;
    }

    /// <summary>
    /// Reference to a sector stored in a region file.
    /// </summary>
    public struct SectorReference
    {
        public int3 position;           // Sector position
        public int3 regionKey;          // Region coordinate or chunk index
        public RegionType regionType;   // Type of region storage
    }

    /// <summary>
    /// Global constants for the save/load system.
    /// </summary>
    public static class PersistenceConstants
    {
        /// <summary>Current version of the save format.</summary>
        public const ushort CurrentVersion = 1;

        /// <summary>Region size for infinite entities (sectors per axis).</summary>
        public static int3 InfiniteRegionSize = new int3(16, 16, 16);

        /// <summary>Base directory for save data.</summary>
        public const string SaveDataDirectory = "SaveData";

        /// <summary>Subdirectory for infinite region files.</summary>
        public const string InfiniteRegionDirectory = "regions/infinite";

        /// <summary>Subdirectory for finite region files.</summary>
        public const string FiniteRegionDirectory = "regions/finite";

        /// <summary>Entity listing filename.</summary>
        public const string EntityListingFilename = "entities.vxe";
    }
}
