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
    /// Manages persistence of VoxelEntity instances to disk.
    /// Handles entity metadata, transform, physics, and sector references.
    /// </summary>
    public unsafe class EntityPersistenceManager
    {
        private readonly string saveDataPath;
        private readonly RegionFileManager regionManager;

        public EntityPersistenceManager(string baseSaveDirectory = null)
        {
            if (string.IsNullOrEmpty(baseSaveDirectory))
            {
                saveDataPath = Path.Combine(Application.persistentDataPath, PersistenceConstants.SaveDataDirectory);
            }
            else
            {
                saveDataPath = baseSaveDirectory;
            }

            Directory.CreateDirectory(saveDataPath);
            regionManager = new RegionFileManager(baseSaveDirectory);
        }

        /// <summary>
        /// Gets the path to the entity listing file.
        /// </summary>
        private string GetEntityListingPath()
        {
            return Path.Combine(saveDataPath, PersistenceConstants.EntityListingFilename);
        }

        /// <summary>
        /// Saves a VoxelEntity to disk, including all sectors and metadata.
        /// </summary>
        /// <param name="entity">The entity to save.</param>
        /// <param name="saveInfiniteSettings">Whether to save infinite loader settings (if applicable).</param>
        public void SaveEntity(VoxelEntity entity, bool saveInfiniteSettings = true)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            VoxelEntityData entityData = entity.GetDataCopy();

            // Gather physics data if VoxelBody component exists
            VoxelBody body = entity.GetComponent<VoxelBody>();
            PhysicsData? physicsData = null;

            if (body != null && body.physicsEnabled)
            {
                var massProps = body.massProperties;
                physicsData = new PhysicsData
                {
                    mass = massProps.mass,
                    centerOfMass = massProps.centerOfMass,
                    inertiaTensor = massProps.inertiaTensor,
                    isStatic = (byte)(body.isStatic ? 1 : 0)
                };
            }

            // Gather infinite loader settings if applicable
            InfiniteLoaderSettings? infiniteSettings = null;
            if (saveInfiniteSettings)
            {
                InfiniteLoader loader = entity.GetComponent<InfiniteLoader>();
                if (loader != null)
                {
                    infiniteSettings = new InfiniteLoaderSettings
                    {
                        sectorLoadBounds = loader.sectorLoadBounds,
                        sectorLoadRadiusInBlocks = loader.sectorLoadRadiusInBlocks,
                        sectorUnloadRadiusInBlocks = loader.sectorUnloadRadiusInBlocks
                    };
                }
            }

            // Save entity metadata and sector references
            SaveEntityInternal(entity.EntityGuid, ref entityData, physicsData, infiniteSettings);

            // Save all sectors
            bool isInfinite = infiniteSettings.HasValue;
            SaveEntitySectors(entity.EntityGuid, ref entityData, isInfinite);
        }

        /// <summary>
        /// Saves entity metadata to the entity listing file.
        /// </summary>
        private void SaveEntityInternal(Guid guid, ref VoxelEntityData entityData,
            PhysicsData? physicsData, InfiniteLoaderSettings? infiniteSettings)
        {
            // Read existing entities
            Dictionary<Guid, EntityIndexEntry> existingIndex = ReadEntityIndex();
            Dictionary<Guid, byte[]> entityDataCache = new Dictionary<Guid, byte[]>();

            string entityPath = GetEntityListingPath();
            if (File.Exists(entityPath))
            {
                // Load existing entity data that we're not updating
                using (FileStream fs = new FileStream(entityPath, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    EntityFileHeader header = ReadEntityHeader(reader);

                    foreach (var kvp in existingIndex)
                    {
                        if (kvp.Key != guid) // Skip the one we're updating
                        {
                            fs.Seek((long)kvp.Value.fileOffset, SeekOrigin.Begin);
                            byte[] data = new byte[kvp.Value.dataLength];
                            fs.Read(data, 0, (int)kvp.Value.dataLength);
                            entityDataCache[kvp.Key] = data;
                        }
                    }
                }
            }

            // Serialize new entity data
            byte[] newEntityData = SerializeEntityData(guid, ref entityData, physicsData, infiniteSettings);
            entityDataCache[guid] = newEntityData;

            // Write all entities back
            WriteEntityFile(entityDataCache);
        }

        /// <summary>
        /// Saves all sectors belonging to an entity.
        /// </summary>
        private void SaveEntitySectors(Guid entityGuid, ref VoxelEntityData entityData, bool isInfinite)
        {
            foreach (var kvp in entityData.sectors)
            {
                int3 sectorPos = kvp.Key;
                SectorHandle handle = kvp.Value;
                Sector* sector = handle.Get();

                if (sector == null)
                    continue;

                if (isInfinite)
                {
                    // Save to infinite region file
                    string regionPath = regionManager.GetInfiniteRegionPath(sectorPos);
                    regionManager.WriteSector(regionPath, sectorPos, sector, RegionType.Infinite);
                }
                else
                {
                    // For finite entities, we'll use a simplified approach:
                    // Save to a region file named after the entity GUID
                    string regionPath = Path.Combine(saveDataPath, PersistenceConstants.FiniteRegionDirectory,
                        $"entity_{entityGuid:N}.vxr");
                    regionManager.WriteSector(regionPath, sectorPos, sector, RegionType.Finite);
                }
            }
        }

        /// <summary>
        /// Serializes entity data to a byte array.
        /// </summary>
        private byte[] SerializeEntityData(Guid guid, ref VoxelEntityData entityData,
            PhysicsData? physicsData, InfiniteLoaderSettings? infiniteSettings)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                // Write GUID
                writer.Write(guid.ToByteArray());

                // Determine flags
                EntityFlags flags = EntityFlags.None;
                if (physicsData.HasValue)
                    flags |= EntityFlags.HasPhysics;
                if (infiniteSettings.HasValue)
                    flags |= EntityFlags.IsInfinite;
                if (physicsData.HasValue && physicsData.Value.isStatic != 0)
                    flags |= EntityFlags.IsStatic;

                writer.Write((byte)flags);

                // Write transform
                writer.Write(entityData.transform.pos.x);
                writer.Write(entityData.transform.pos.y);
                writer.Write(entityData.transform.pos.z);
                writer.Write(entityData.transform.rot.value.x);
                writer.Write(entityData.transform.rot.value.y);
                writer.Write(entityData.transform.rot.value.z);
                writer.Write(entityData.transform.rot.value.w);

                // Write dirty flags
                writer.Write(entityData.entityDirtyFlags);

                // Write physics data if present
                if (physicsData.HasValue)
                {
                    PhysicsData pd = physicsData.Value;
                    writer.Write(pd.mass);
                    writer.Write(pd.centerOfMass.x);
                    writer.Write(pd.centerOfMass.y);
                    writer.Write(pd.centerOfMass.z);
                    writer.Write(pd.inertiaTensor.x);
                    writer.Write(pd.inertiaTensor.y);
                    writer.Write(pd.inertiaTensor.z);
                }

                // Write infinite settings if present
                if (infiniteSettings.HasValue)
                {
                    InfiniteLoaderSettings ils = infiniteSettings.Value;
                    writer.Write(ils.sectorLoadBounds.x);
                    writer.Write(ils.sectorLoadBounds.y);
                    writer.Write(ils.sectorLoadBounds.z);
                    writer.Write(ils.sectorLoadRadiusInBlocks);
                    writer.Write(ils.sectorUnloadRadiusInBlocks);
                }

                // Write sector count
                int sectorCount = entityData.sectors.Count();
                writer.Write(sectorCount);

                // Write sector references
                foreach (var kvp in entityData.sectors)
                {
                    int3 sectorPos = kvp.Key;
                    writer.Write(sectorPos.x);
                    writer.Write(sectorPos.y);
                    writer.Write(sectorPos.z);

                    // For now, we just store the sector position
                    // The region path is determined by isInfinite flag and position
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Loads an entity from disk.
        /// Returns the loaded VoxelEntityData, physics data, and infinite settings.
        /// </summary>
        public bool LoadEntity(Guid guid, out VoxelEntityData entityData,
            out PhysicsData? physicsData, out InfiniteLoaderSettings? infiniteSettings,
            Allocator allocator = Allocator.Persistent)
        {
            entityData = default;
            physicsData = null;
            infiniteSettings = null;

            Dictionary<Guid, EntityIndexEntry> index = ReadEntityIndex();

            if (!index.TryGetValue(guid, out EntityIndexEntry entry))
                return false;

            string entityPath = GetEntityListingPath();
            if (!File.Exists(entityPath))
                return false;

            // Read entity data
            byte[] data;
            using (FileStream fs = new FileStream(entityPath, FileMode.Open, FileAccess.Read))
            {
                fs.Seek((long)entry.fileOffset, SeekOrigin.Begin);
                data = new byte[entry.dataLength];
                fs.Read(data, 0, (int)entry.dataLength);
            }

            // Deserialize
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                // Read GUID (verify)
                Guid readGuid = new Guid(reader.ReadBytes(16));
                if (readGuid != guid)
                {
                    Debug.LogError($"GUID mismatch: expected {guid}, got {readGuid}");
                    return false;
                }

                // Read flags
                EntityFlags flags = (EntityFlags)reader.ReadByte();
                bool hasPhysics = (flags & EntityFlags.HasPhysics) != 0;
                bool isInfinite = (flags & EntityFlags.IsInfinite) != 0;
                bool isStatic = (flags & EntityFlags.IsStatic) != 0;

                // Initialize entity data
                entityData = new VoxelEntityData(allocator);
                entityData.guid = guid;

                // Read transform
                float3 pos = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                quaternion rot = new quaternion(
                    reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle());
                entityData.transform = new RigidTransform(rot, pos);

                // Read dirty flags
                entityData.entityDirtyFlags = reader.ReadUInt16();

                // Read physics data
                if (hasPhysics)
                {
                    physicsData = new PhysicsData
                    {
                        mass = reader.ReadSingle(),
                        centerOfMass = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        inertiaTensor = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        isStatic = (byte)(isStatic ? 1 : 0)
                    };
                }

                // Read infinite settings
                if (isInfinite)
                {
                    infiniteSettings = new InfiniteLoaderSettings
                    {
                        sectorLoadBounds = new int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
                        sectorLoadRadiusInBlocks = reader.ReadSingle(),
                        sectorUnloadRadiusInBlocks = reader.ReadSingle()
                    };
                }

                // Read sector positions
                int sectorCount = reader.ReadInt32();
                List<int3> sectorPositions = new List<int3>(sectorCount);

                for (int i = 0; i < sectorCount; i++)
                {
                    int3 sectorPos = new int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    sectorPositions.Add(sectorPos);
                }

                // Load all sectors
                foreach (int3 sectorPos in sectorPositions)
                {
                    // Allocate new sector
                    Sector* newSector = (Sector*)UnsafeUtility.Malloc(
                        UnsafeUtility.SizeOf<Sector>(),
                        UnsafeUtility.AlignOf<Sector>(),
                        Allocator.Persistent);

                    *newSector = Sector.New(Allocator.Persistent);

                    // Load sector data
                    bool loaded = false;
                    if (isInfinite)
                    {
                        string regionPath = regionManager.GetInfiniteRegionPath(sectorPos);
                        loaded = regionManager.ReadSector(regionPath, sectorPos, newSector);
                    }
                    else
                    {
                        string regionPath = Path.Combine(saveDataPath, PersistenceConstants.FiniteRegionDirectory,
                            $"entity_{guid:N}.vxr");
                        loaded = regionManager.ReadSector(regionPath, sectorPos, newSector);
                    }

                    if (loaded)
                    {
                        entityData.AddSectorAt(sectorPos, new SectorHandle(newSector));
                    }
                    else
                    {
                        // Failed to load sector, dispose and skip
                        newSector->Dispose(Allocator.Persistent);
                        UnsafeUtility.Free(newSector, Allocator.Persistent);
                        Debug.LogWarning($"Failed to load sector at {sectorPos} for entity {guid}");
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Gets all entity GUIDs from the entity listing file.
        /// </summary>
        public Guid[] GetAllEntityGuids()
        {
            Dictionary<Guid, EntityIndexEntry> index = ReadEntityIndex();
            Guid[] guids = new Guid[index.Count];
            index.Keys.CopyTo(guids, 0);
            return guids;
        }

        /// <summary>
        /// Checks if an entity exists in the save data.
        /// </summary>
        public bool EntityExists(Guid guid)
        {
            Dictionary<Guid, EntityIndexEntry> index = ReadEntityIndex();
            return index.ContainsKey(guid);
        }

        /// <summary>
        /// Deletes an entity from the save data.
        /// </summary>
        public void DeleteEntity(Guid guid)
        {
            // Read existing entities
            Dictionary<Guid, EntityIndexEntry> existingIndex = ReadEntityIndex();

            if (!existingIndex.ContainsKey(guid))
                return;

            // Remove from index
            existingIndex.Remove(guid);

            // Reload all entity data except the deleted one
            Dictionary<Guid, byte[]> entityDataCache = new Dictionary<Guid, byte[]>();
            string entityPath = GetEntityListingPath();

            if (File.Exists(entityPath))
            {
                using (FileStream fs = new FileStream(entityPath, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    EntityFileHeader header = ReadEntityHeader(reader);

                    foreach (var kvp in existingIndex)
                    {
                        fs.Seek((long)kvp.Value.fileOffset, SeekOrigin.Begin);
                        byte[] data = new byte[kvp.Value.dataLength];
                        fs.Read(data, 0, (int)kvp.Value.dataLength);
                        entityDataCache[kvp.Key] = data;
                    }
                }
            }

            // Write back without the deleted entity
            WriteEntityFile(entityDataCache);

            // TODO: Delete sector data (region files) for finite entities
            // For now, they remain on disk until manual cleanup
        }

        /// <summary>
        /// Reads the entity index from the entity listing file.
        /// </summary>
        private Dictionary<Guid, EntityIndexEntry> ReadEntityIndex()
        {
            Dictionary<Guid, EntityIndexEntry> index = new Dictionary<Guid, EntityIndexEntry>();

            string entityPath = GetEntityListingPath();
            if (!File.Exists(entityPath))
                return index;

            using (FileStream fs = new FileStream(entityPath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                EntityFileHeader header = ReadEntityHeader(reader);

                for (int i = 0; i < header.entityCount; i++)
                {
                    EntityIndexEntry entry = new EntityIndexEntry();
                    entry.guid = new Guid(reader.ReadBytes(16));
                    entry.fileOffset = reader.ReadUInt64();
                    entry.dataLength = reader.ReadUInt32();
                    entry.checksum = reader.ReadUInt32();

                    index[entry.guid] = entry;
                }
            }

            return index;
        }

        /// <summary>
        /// Reads the entity file header.
        /// </summary>
        private EntityFileHeader ReadEntityHeader(BinaryReader reader)
        {
            EntityFileHeader header = new EntityFileHeader();
            byte* headerPtr = (byte*)&header;

            for (int i = 0; i < EntityFileHeader.Size; i++)
            {
                headerPtr[i] = reader.ReadByte();
            }

            if (header.magic != FileMagic.Entity)
            {
                throw new InvalidDataException($"Invalid entity file magic: 0x{header.magic:X8}");
            }

            return header;
        }

        /// <summary>
        /// Writes the complete entity file with all entities.
        /// </summary>
        private void WriteEntityFile(Dictionary<Guid, byte[]> entityDataCache)
        {
            string entityPath = GetEntityListingPath();
            string tempPath = entityPath + ".tmp";

            using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                // Write header
                EntityFileHeader header = new EntityFileHeader
                {
                    magic = FileMagic.Entity,
                    version = PersistenceConstants.CurrentVersion,
                    entityCount = (uint)entityDataCache.Count
                };

                byte* headerPtr = (byte*)&header;
                for (int i = 0; i < EntityFileHeader.Size; i++)
                {
                    writer.Write(headerPtr[i]);
                }

                // Calculate offsets for index
                ulong currentOffset = (ulong)(EntityFileHeader.Size + entityDataCache.Count * EntityIndexEntry.Size);
                Dictionary<Guid, EntityIndexEntry> newIndex = new Dictionary<Guid, EntityIndexEntry>();

                foreach (var kvp in entityDataCache)
                {
                    byte[] data = kvp.Value;
                    // Simple checksum (can be improved)
                    uint checksum = (uint)data.Length; // Placeholder

                    EntityIndexEntry entry = new EntityIndexEntry
                    {
                        guid = kvp.Key,
                        fileOffset = currentOffset,
                        dataLength = (uint)data.Length,
                        checksum = checksum
                    };

                    newIndex[kvp.Key] = entry;
                    currentOffset += (uint)data.Length;
                }

                // Write index
                foreach (var kvp in newIndex)
                {
                    writer.Write(kvp.Value.guid.ToByteArray());
                    writer.Write(kvp.Value.fileOffset);
                    writer.Write(kvp.Value.dataLength);
                    writer.Write(kvp.Value.checksum);
                }

                // Write entity data
                foreach (var kvp in entityDataCache)
                {
                    writer.Write(kvp.Value);
                }
            }

            // Replace original file
            if (File.Exists(entityPath))
            {
                File.Delete(entityPath);
            }
            File.Move(tempPath, entityPath);
        }
    }
}
