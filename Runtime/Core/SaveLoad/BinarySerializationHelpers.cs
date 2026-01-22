using System;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Binary serialization helpers for save/load structures
    /// </summary>
    public static class BinarySerializationHelpers
    {
        // ===== Basic Types =====

        public static void Write(this BinaryWriter writer, int3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        public static int3 ReadInt3(this BinaryReader reader)
        {
            return new int3(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32()
            );
        }

        public static void Write(this BinaryWriter writer, float3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        public static float3 ReadFloat3(this BinaryReader reader)
        {
            return new float3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }

        public static void Write(this BinaryWriter writer, quaternion value)
        {
            writer.Write(value.value.x);
            writer.Write(value.value.y);
            writer.Write(value.value.z);
            writer.Write(value.value.w);
        }

        public static quaternion ReadQuaternion(this BinaryReader reader)
        {
            return new quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }

        public static void Write(this BinaryWriter writer, RigidTransform value)
        {
            writer.Write(value.rot);
            writer.Write(value.pos);
        }

        public static RigidTransform ReadRigidTransform(this BinaryReader reader)
        {
            return new RigidTransform(
                reader.ReadQuaternion(),
                reader.ReadFloat3()
            );
        }

        public static void Write(this BinaryWriter writer, Guid guid)
        {
            writer.Write(guid.ToByteArray());
        }

        public static Guid ReadGuid(this BinaryReader reader)
        {
            return new Guid(reader.ReadBytes(16));
        }

        // ===== Headers =====

        public static void Write(this BinaryWriter writer, WorldMetaHeader header)
        {
            writer.Write(header.magic);
            writer.Write(header.version);
            writer.Write(header.seed);
            writer.Write(header.mainEntityGuid);
        }

        public static WorldMetaHeader ReadWorldMetaHeader(this BinaryReader reader)
        {
            return new WorldMetaHeader
            {
                magic = reader.ReadUInt32(),
                version = reader.ReadUInt32(),
                seed = reader.ReadInt32(),
                mainEntityGuid = reader.ReadGuid()
            };
        }

        public static void Write(this BinaryWriter writer, RegionFileHeader header)
        {
            writer.Write(header.magic);
            writer.Write(header.version);
            writer.Write(header.regionCoords);
            writer.Write(header.entityCount);
            writer.Write(header.sectorDataOffset);
        }

        public static RegionFileHeader ReadRegionFileHeader(this BinaryReader reader)
        {
            return new RegionFileHeader
            {
                magic = reader.ReadUInt32(),
                version = reader.ReadUInt32(),
                regionCoords = reader.ReadInt3(),
                entityCount = reader.ReadUInt32(),
                sectorDataOffset = reader.ReadInt64()
            };
        }

        public static void Write(this BinaryWriter writer, EntityIndexFileHeader header)
        {
            writer.Write(header.magic);
            writer.Write(header.version);
            writer.Write(header.gridCoords);
            writer.Write(header.entityCount);
        }

        public static EntityIndexFileHeader ReadEntityIndexFileHeader(this BinaryReader reader)
        {
            return new EntityIndexFileHeader
            {
                magic = reader.ReadUInt32(),
                version = reader.ReadUInt32(),
                gridCoords = reader.ReadInt3(),
                entityCount = reader.ReadUInt32()
            };
        }

        public static void Write(this BinaryWriter writer, EntityArchiveFileHeader header)
        {
            writer.Write(header.magic);
            writer.Write(header.version);
            writer.Write(header.gridCoords);
            writer.Write(header.entityCount);
        }

        public static EntityArchiveFileHeader ReadEntityArchiveFileHeader(this BinaryReader reader)
        {
            return new EntityArchiveFileHeader
            {
                magic = reader.ReadUInt32(),
                version = reader.ReadUInt32(),
                gridCoords = reader.ReadInt3(),
                entityCount = reader.ReadUInt32()
            };
        }

        // ===== Entity Metadata =====

        public static void Write(this BinaryWriter writer, EntityMetadata metadata)
        {
            writer.Write(metadata.guid);
            writer.Write((byte)metadata.type);
            writer.Write(metadata.transform);
            writer.Write(metadata.entityDirtyFlags);
            writer.Write(metadata.hasAABB);

            if (metadata.hasAABB)
            {
                writer.Write(metadata.aabbMin);
                writer.Write(metadata.aabbMax);
            }
        }

        public static EntityMetadata ReadEntityMetadata(this BinaryReader reader)
        {
            var metadata = new EntityMetadata
            {
                guid = reader.ReadGuid(),
                type = (EntityType)reader.ReadByte(),
                transform = reader.ReadRigidTransform(),
                entityDirtyFlags = reader.ReadUInt16(),
                hasAABB = reader.ReadBoolean()
            };

            if (metadata.hasAABB)
            {
                metadata.aabbMin = reader.ReadFloat3();
                metadata.aabbMax = reader.ReadFloat3();
            }

            return metadata;
        }

        // ===== Sector Data =====

        public static void Write(this BinaryWriter writer, SectorSerializationHeader header)
        {
            writer.Write(header.sectorPos);
            writer.Write(header.sectorDirtyFlags);
            writer.Write(header.nonEmptyBrickCount);
        }

        public static SectorSerializationHeader ReadSectorSerializationHeader(this BinaryReader reader)
        {
            return new SectorSerializationHeader
            {
                sectorPos = reader.ReadInt3(),
                sectorDirtyFlags = reader.ReadUInt16(),
                nonEmptyBrickCount = reader.ReadUInt16()
            };
        }

        public static void Write(this BinaryWriter writer, BrickSerializationHeader header)
        {
            writer.Write(header.brickIdx);
            writer.Write(header.brickDirtyFlags);
            writer.Write(header.brickDirtyDirMask);
            writer.Write(header.blockCount);
        }

        public static BrickSerializationHeader ReadBrickSerializationHeader(this BinaryReader reader)
        {
            return new BrickSerializationHeader
            {
                brickIdx = reader.ReadInt16(),
                brickDirtyFlags = reader.ReadUInt16(),
                brickDirtyDirMask = reader.ReadUInt32(),
                blockCount = reader.ReadUInt16()
            };
        }

        // ===== Sector Index Entry =====

        public static void Write(this BinaryWriter writer, SectorIndexEntry entry)
        {
            writer.Write(entry.sectorLocalPos);
            writer.Write(entry.dataOffset);
            writer.Write(entry.dataSize);
        }

        public static SectorIndexEntry ReadSectorIndexEntry(this BinaryReader reader)
        {
            return new SectorIndexEntry
            {
                sectorLocalPos = reader.ReadInt3(),
                dataOffset = reader.ReadInt64(),
                dataSize = reader.ReadUInt32()
            };
        }

        // ===== Validation =====

        public static void ValidateMagic(uint actual, uint expected, string fileType)
        {
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"Invalid {fileType} file: magic number mismatch. " +
                    $"Expected 0x{expected:X8}, got 0x{actual:X8}");
            }
        }

        public static void ValidateVersion(uint actual, uint expected, string fileType)
        {
            if (actual > expected)
            {
                Debug.LogWarning(
                    $"{fileType} file version {actual} is newer than supported version {expected}. " +
                    "Loading may fail or produce incorrect results.");
            }
        }
    }
}
