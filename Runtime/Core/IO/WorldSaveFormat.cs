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
    ///     u8  entityFlags            (v6+; bit0 = has VoxelBody, bit1 = entity is static)
    ///                                (v3..v5: bodyState — 0 = off/none, 1 = static, 2 = dynamic;
    ///                                 migrated on read, see EntityFlagsFromLegacyBodyState)
    ///     6  × f32 velocity          (v4+; linear.xyz + angular.xyz — the dynamic body's physics
    ///                                 velocity so motion resumes on load; zero for pre-v4 saves)
    ///     u8  protected             (v5+; 0 = normal, 1 = protected-from-interaction; 0 for pre-v5 saves)
    ///     u64 sectorIndexOffset
    ///     u32 sectorCount
    /// </code>
    /// </summary>
    public static class WorldSaveFormat
    {
        /// <summary>Magic value for the file header: "VXLS" interpreted as little-endian u32.</summary>
        public const uint FileMagic = 0x534C5856u;

        public const ushort CurrentVersion = 6;
        public const int HeaderBytes = 64;

        /// <summary>Size in bytes of the uncompressed per-sector preview blob (u32 per brick).</summary>
        public const int PreviewBytes = Sector.BRICKS_IN_SECTOR * 4;

        /// <summary>Byte size of one entry in a per-entity sector index: int3 coord (12) + u64 offset (8) + u32 size (4).</summary>
        public const int SectorIndexEntryBytes = 12 + 8 + 4;

        /// <summary>Byte size of one entity record in the entity table (v3: +1 body/entity-flags byte; v4: +24 velocity bytes; v5: +1 protected byte).</summary>
        public const int EntityRecordBytes = 16 + 28 + 2 + 1 + 24 + 1 + 8 + 4;

        /// <summary>
        /// Migrates a pre-v6 <c>bodyState</c> byte to <see cref="EntityFlags"/>. Staticness used to live
        /// on the body, so "no body" implied a static entity — which is what <c>VoxelEntity.Awake</c>
        /// forced back then, and what keeps old saves loading unchanged.
        /// </summary>
        public static EntityFlags EntityFlagsFromLegacyBodyState(byte bodyState)
        {
            return bodyState switch
            {
                1 => EntityFlags.HasBody | EntityFlags.Static, // legacy Static
                2 => EntityFlags.HasBody,                      // legacy Dynamic
                _ => EntityFlags.Static,                       // legacy Off (or unknown)
            };
        }
    }

    /// <summary>
    /// Per-entity boolean state stored on disk as one byte (v6+). Replaces the pre-v6
    /// <c>VoxelBodyState</c> byte: staticness is a property of the entity
    /// (<see cref="VoxelEntityData.isStatic"/>), not of its <c>VoxelBody</c>, so presence of a body
    /// and staticness are now two independent bits.
    /// </summary>
    /// <remarks>
    /// The component's <c>physicsEnabled</c> flag is still not persisted — it only controls Unity
    /// Rigidbody creation, which the voxel physics never reads.
    /// </remarks>
    [Flags]
    public enum EntityFlags : byte
    {
        None = 0,

        /// <summary>The entity carried an enabled <c>VoxelBody</c> component at save time.</summary>
        HasBody = 1 << 0,

        /// <summary>The entity never moves — see <see cref="VoxelEntityData.isStatic"/>.</summary>
        Static = 1 << 1,

        All = HasBody | Static,
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

    public readonly struct EntityRecord
    {
        public readonly Guid128 Guid;
        public readonly EntityTransformRecord Transform;
        public readonly ushort EntityRequireUpdateFlags;
        public readonly EntityFlags Flags;

        /// <summary>Whether the entity carried an enabled <c>VoxelBody</c> at save time.</summary>
        public bool HasBody => (Flags & EntityFlags.HasBody) != 0;

        /// <summary>The persisted <see cref="VoxelEntityData.isStatic"/> value.</summary>
        public bool IsStatic => (Flags & EntityFlags.Static) != 0;

        /// <summary>
        /// The body's physics velocity at save time (v4+). Persisted so a dynamic body resumes its
        /// motion on load instead of restarting from rest. Zero for static/off bodies and pre-v4 saves.
        /// </summary>
        public readonly float3 LinearVelocity;
        public readonly float3 AngularVelocity;

        /// <summary>
        /// Whether this entity is protected from gameplay interaction tools (v5+). Round-tripped so
        /// the "protected world" designation survives save/load. False for pre-v5 saves.
        /// </summary>
        public readonly bool Protected;

        public EntityRecord(Guid128 guid, EntityTransformRecord transform, ushort entityRequireUpdateFlags)
            : this(guid, transform, entityRequireUpdateFlags, EntityFlags.Static)
        {
        }

        public EntityRecord(
            Guid128 guid,
            EntityTransformRecord transform,
            ushort entityRequireUpdateFlags,
            EntityFlags flags)
            : this(guid, transform, entityRequireUpdateFlags, flags, float3.zero, float3.zero)
        {
        }

        public EntityRecord(
            Guid128 guid,
            EntityTransformRecord transform,
            ushort entityRequireUpdateFlags,
            EntityFlags flags,
            float3 linearVelocity,
            float3 angularVelocity)
            : this(guid, transform, entityRequireUpdateFlags, flags, linearVelocity, angularVelocity, false)
        {
        }

        public EntityRecord(
            Guid128 guid,
            EntityTransformRecord transform,
            ushort entityRequireUpdateFlags,
            EntityFlags flags,
            float3 linearVelocity,
            float3 angularVelocity,
            bool isProtected)
        {
            Guid = guid;
            Transform = transform;
            EntityRequireUpdateFlags = entityRequireUpdateFlags;
            Flags = flags;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
            Protected = isProtected;
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
