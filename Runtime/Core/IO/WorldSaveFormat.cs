using System;
using Unity.Mathematics;
using Voxelis.Utils;

namespace Voxelis.IO
{
    /// <summary>
    /// Constants and record types for the <c>.vxw</c> world save format.
    ///
    /// File layout (offsets are file-wide, all values little-endian):
    /// <code>
    /// [Header — 64 bytes at offset 0]
    ///   u32  magic               "VXLS"
    ///   u16  version
    ///   u16  flags                (bit0 = Deflate-compressed sector payloads)
    ///   u64  entityTableOffset    (backpatched at Commit)
    ///   48 B reserved
    ///
    /// [Sector regions and per-entity sector indices, interleaved as they're written]
    ///   For each sector:
    ///     u32[Sector.BRICKS_IN_SECTOR] preview   (uncompressed, readable without payload)
    ///     u32  payloadSize
    ///     byte[payloadSize] payload              (DeflateStream-compressed Sector body)
    ///   For each entity (after its sectors):
    ///     SectorIndexEntry[entity.sectorCount]
    ///
    /// [EntityTable — at entityTableOffset]
    ///   u32  entityCount
    ///   For each entity:
    ///     16 B guid
    ///     7  × f32 transform (pos.xyz + rot.xyzw)
    ///     u16 entityRequireUpdateFlags
    ///     u8  bodyState              (v3+; 0 = off/none, 1 = static, 2 = dynamic)
    ///     u64 sectorIndexOffset
    ///     u32 sectorCount
    /// </code>
    /// </summary>
    public static class WorldSaveFormat
    {
        /// <summary>Magic value for the file header: "VXLS" interpreted as little-endian u32.</summary>
        public const uint FileMagic = 0x534C5856u;

        public const ushort CurrentVersion = 3;
        public const int HeaderBytes = 64;

        /// <summary>Size in bytes of the uncompressed per-sector preview blob (u32 per brick).</summary>
        public const int PreviewBytes = Sector.BRICKS_IN_SECTOR * 4;

        /// <summary>Byte size of one entry in a per-entity sector index: int3 coord (12) + u64 offset (8) + u32 size (4).</summary>
        public const int SectorIndexEntryBytes = 12 + 8 + 4;

        /// <summary>Byte size of one entity record in the entity table (v3: +1 body-flags byte).</summary>
        public const int EntityRecordBytes = 16 + 28 + 2 + 1 + 8 + 4;
    }

    [Flags]
    public enum SaveFlags : ushort
    {
        None = 0,
        Deflate = 1 << 0,
    }

    public readonly struct SaveHeader
    {
        public readonly ushort Version;
        public readonly SaveFlags Flags;

        public SaveHeader(ushort version, SaveFlags flags)
        {
            Version = version;
            Flags = flags;
        }
    }

    public readonly struct EntityTransformRecord
    {
        public readonly float3 Position;
        public readonly quaternion Rotation;

        public EntityTransformRecord(float3 position, quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    /// <summary>
    /// Serialized physics state of an entity's <c>VoxelBody</c> component, stored on disk as one byte.
    /// <see cref="Off"/> covers "no component" and "component present but physics disabled" alike;
    /// pre-v3 saves read as <see cref="Off"/>. Unknown byte values are treated as <see cref="Off"/>.
    /// </summary>
    public enum VoxelBodyState : byte
    {
        Off = 0,
        Static = 1,
        Dynamic = 2,
    }

    public readonly struct EntityRecord
    {
        public readonly Guid128 Guid;
        public readonly EntityTransformRecord Transform;
        public readonly ushort EntityRequireUpdateFlags;
        public readonly VoxelBodyState Body;

        public EntityRecord(Guid128 guid, EntityTransformRecord transform, ushort entityRequireUpdateFlags)
            : this(guid, transform, entityRequireUpdateFlags, VoxelBodyState.Off)
        {
        }

        public EntityRecord(
            Guid128 guid,
            EntityTransformRecord transform,
            ushort entityRequireUpdateFlags,
            VoxelBodyState body)
        {
            Guid = guid;
            Transform = transform;
            EntityRequireUpdateFlags = entityRequireUpdateFlags;
            Body = body;
        }
    }

    public readonly struct SectorIndexEntry
    {
        public readonly int3 Coord;
        public readonly ulong RegionOffset;
        public readonly uint RegionSize;

        public SectorIndexEntry(int3 coord, ulong regionOffset, uint regionSize)
        {
            Coord = coord;
            RegionOffset = regionOffset;
            RegionSize = regionSize;
        }
    }
}
