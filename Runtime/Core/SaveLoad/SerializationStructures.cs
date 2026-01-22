using System;
using Unity.Mathematics;
using UnityEngine;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Type of voxel entity
    /// </summary>
    public enum EntityType : byte
    {
        Infinite = 0,  // Infinite world (like basement), sectors can be partially loaded
        Finite = 1     // Finite entity (like spaceship), all sectors must be loaded together
    }

    /// <summary>
    /// Metadata for a voxel entity (stored in entity index and archives)
    /// </summary>
    [Serializable]
    public struct EntityMetadata
    {
        public Guid guid;
        public EntityType type;
        public RigidTransform transform;  // Position and rotation
        public ushort entityDirtyFlags;

        // Only for finite entities
        public bool hasAABB;
        public float3 aabbMin;
        public float3 aabbMax;

        public EntityMetadata(Guid guid, EntityType type, RigidTransform transform, ushort dirtyFlags)
        {
            this.guid = guid;
            this.type = type;
            this.transform = transform;
            this.entityDirtyFlags = dirtyFlags;
            this.hasAABB = false;
            this.aabbMin = float3.zero;
            this.aabbMax = float3.zero;
        }

        public void SetAABB(float3 min, float3 max)
        {
            hasAABB = true;
            aabbMin = min;
            aabbMax = max;
        }

        // Check if this entity intersects a region
        public bool IntersectsRegion(int3 regionPos)
        {
            if (type == EntityType.Infinite)
                return true;  // Infinite entities are everywhere

            if (!hasAABB)
                return false;

            // Convert region coords to world space
            int sectorSize = Sector.SECTOR_SIZE_IN_BLOCKS;
            int regionSizeInSectors = WorldSaveConstants.REGION_SIZE_IN_SECTORS;
            int regionSizeInBlocks = sectorSize * regionSizeInSectors;

            float3 regionMin = (float3)regionPos * regionSizeInBlocks;
            float3 regionMax = regionMin + regionSizeInBlocks;

            // AABB intersection test
            return !(aabbMax.x < regionMin.x || aabbMin.x > regionMax.x ||
                     aabbMax.y < regionMin.y || aabbMin.y > regionMax.y ||
                     aabbMax.z < regionMin.z || aabbMin.z > regionMax.z);
        }
    }

    /// <summary>
    /// Header for serialized sector data
    /// </summary>
    public struct SectorSerializationHeader
    {
        public int3 sectorPos;           // Global sector position
        public ushort sectorDirtyFlags;  // Sector-level dirty flags
        public ushort nonEmptyBrickCount; // Number of non-empty bricks to follow
    }

    /// <summary>
    /// Header for serialized brick data
    /// </summary>
    public struct BrickSerializationHeader
    {
        public short brickIdx;            // Brick index (0-4095)
        public ushort brickDirtyFlags;    // Brick dirty flags
        public uint brickDirtyDirMask;    // Direction mask for propagation
        public ushort blockCount;         // Should be 512 for full brick
    }

    /// <summary>
    /// RLE run for block compression
    /// </summary>
    public struct RLERun
    {
        public uint count;   // Run length
        public Block block;  // Block value
    }

    /// <summary>
    /// Index entry for a sector within a region file
    /// </summary>
    public struct SectorIndexEntry
    {
        public int3 sectorLocalPos;  // Position within region (0-15 per axis)
        public long dataOffset;      // Byte offset to sector data in file
        public uint dataSize;        // Size of sector data in bytes
    }

    /// <summary>
    /// Header for world.meta file
    /// </summary>
    public struct WorldMetaHeader
    {
        public uint magic;           // MAGIC_WORLD_META
        public uint version;         // VERSION_WORLD_META
        public int seed;             // World generation seed
        public Guid mainEntityGuid;  // GUID of main infinite world entity
    }

    /// <summary>
    /// Header for region.vxr file
    /// </summary>
    public struct RegionFileHeader
    {
        public uint magic;              // MAGIC_REGION
        public uint version;            // VERSION_REGION
        public int3 regionCoords;       // Region position
        public uint entityCount;        // Number of entities with sectors in this region
        public long sectorDataOffset;   // Offset to start of sector data section
    }

    /// <summary>
    /// Header for entity_index.eidx file
    /// </summary>
    public struct EntityIndexFileHeader
    {
        public uint magic;           // MAGIC_ENTITY_INDEX
        public uint version;         // VERSION_ENTITY_INDEX
        public int3 gridCoords;      // Index grid coordinates
        public uint entityCount;     // Number of entities in this index
    }

    /// <summary>
    /// Header for entity_archive.vxea file
    /// </summary>
    public struct EntityArchiveFileHeader
    {
        public uint magic;           // MAGIC_ENTITY_ARCHIVE
        public uint version;         // VERSION_ENTITY_ARCHIVE
        public int3 gridCoords;      // Archive grid coordinates
        public uint entityCount;     // Number of entities in this archive
    }
}
