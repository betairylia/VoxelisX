using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>
    /// One connected client and what it already knows about each world. Replication is a diff
    /// between that knowledge and the world: new entities and sectors go out in full, known
    /// sectors send their dirty bricks, transforms and flags go out when they changed.
    /// </summary>
    public sealed class ServerConnection
    {
        private sealed class EntityKnown
        {
            public readonly HashSet<int3> Sectors = new();
            public RigidTransform Transform;
            public bool IsStatic;
            public bool IsProtected;
            public bool HasBody;
        }

        private sealed class WorldKnown
        {
            public readonly Dictionary<Guid128, EntityKnown> Entities = new();
        }

        private readonly Dictionary<ushort, WorldKnown> worlds = new();
        private readonly List<Guid128> guidScratch = new();
        private readonly List<int3> sectorScratch = new();

        public int Id { get; }
        public INetChannel Channel { get; }

        /// <summary>Set once the handshake went out. Sent with the first replication, not at accept time,
        /// so that game types registered during scene start are part of the registry hash.</summary>
        public bool HelloSent { get; internal set; }

        /// <summary>Worlds this connection receives. Empty means every world.</summary>
        public HashSet<ushort> SubscribedWorlds { get; } = new();

        internal ServerConnection(int id, INetChannel channel)
        {
            Id = id;
            Channel = channel;
        }

        public bool IsConnected => Channel != null && Channel.IsConnected;

        public bool IsSubscribed(ushort worldId) => SubscribedWorlds.Count == 0 || SubscribedWorlds.Contains(worldId);

        public void Send(NetDelivery delivery, NetMessageWriter writer)
        {
            Channel.Send(delivery, writer.AsSpan());
        }

        /// <summary>Forgets everything about a world so the next replication resends it in full.</summary>
        public void ForgetWorld(ushort worldId)
        {
            worlds.Remove(worldId);
        }

        // TODO: VibeReview: Can we burst-ify the build process (and connection data holders)?
        internal unsafe void ReplicateWorld(CaelixWorld world, NetMessageWriter writer)
        {
            if (!IsSubscribed(world.Id))
            {
                return;
            }

            if (!worlds.TryGetValue(world.Id, out WorldKnown known))
            {
                known = new WorldKnown();
                worlds.Add(world.Id, known);
            }

            ushort worldId = world.Id;
            uint tick = world.TickIndex;
            ushort slotMask = world.Config.replicatedSlotMask;
            NativeHashMap<Guid128, VoxelEntityData> entities = world.Data.VoxelEntities;

            // Despawns.
            guidScratch.Clear();
            foreach (var kvp in known.Entities)
            {
                if (!entities.ContainsKey(kvp.Key))
                {
                    guidScratch.Add(kvp.Key);
                }
            }

            for (int i = 0; i < guidScratch.Count; i++)
            {
                known.Entities.Remove(guidScratch[i]);
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.EntityDespawn, worldId, tick);
                writer.Write(new EntityDespawnMessage { Guid = guidScratch[i] });
                Send(NetDelivery.Reliable, writer);
            }

            // Spawns, state, transforms, sectors, bricks.
            foreach (var kvp in entities)
            {
                Guid128 guid = kvp.Key;
                VoxelEntityData data = kvp.Value;
                bool hasBody = world.HasBody(guid);

                bool isNew = !known.Entities.TryGetValue(guid, out EntityKnown entityKnown);
                if (isNew)
                {
                    entityKnown = new EntityKnown
                    {
                        Transform = data.transform,
                        IsStatic = data.isStatic,
                        IsProtected = data.isProtected,
                        HasBody = hasBody,
                    };
                    known.Entities.Add(guid, entityKnown);

                    writer.Reset();
                    NetHeader.Write(writer, NetMessageType.EntitySpawn, worldId, tick);
                    writer.Write(new EntitySpawnMessage
                    {
                        Guid = guid,
                        Transform = data.transform,
                        IsStatic = (byte)(data.isStatic ? 1 : 0),
                        IsProtected = (byte)(data.isProtected ? 1 : 0),
                        HasBody = (byte)(hasBody ? 1 : 0),
                    });
                    Send(NetDelivery.Reliable, writer);
                }
                else
                {
                    if (entityKnown.IsStatic != data.isStatic ||
                        entityKnown.IsProtected != data.isProtected ||
                        entityKnown.HasBody != hasBody)
                    {
                        entityKnown.IsStatic = data.isStatic;
                        entityKnown.IsProtected = data.isProtected;
                        entityKnown.HasBody = hasBody;

                        writer.Reset();
                        NetHeader.Write(writer, NetMessageType.EntityState, worldId, tick);
                        writer.Write(new EntityStateMessage
                        {
                            Guid = guid,
                            IsStatic = (byte)(data.isStatic ? 1 : 0),
                            IsProtected = (byte)(data.isProtected ? 1 : 0),
                            HasBody = (byte)(hasBody ? 1 : 0),
                        });
                        Send(NetDelivery.Reliable, writer);
                    }

                    if (!TransformEquals(entityKnown.Transform, data.transform))
                    {
                        entityKnown.Transform = data.transform;

                        writer.Reset();
                        NetHeader.Write(writer, NetMessageType.EntityTransform, worldId, tick);
                        writer.Write(new EntityTransformMessage { Guid = guid, Transform = data.transform });
                        // Reliable for now: the in-process channel never drops, and a lost final
                        // pose would leave a settled body drawn in the wrong place.
                        Send(NetDelivery.Reliable, writer);
                    }
                }

                // Sector removals.
                sectorScratch.Clear();
                foreach (int3 sectorPos in entityKnown.Sectors)
                {
                    if (!data.sectors.ContainsKey(sectorPos))
                    {
                        sectorScratch.Add(sectorPos);
                    }
                }

                for (int i = 0; i < sectorScratch.Count; i++)
                {
                    entityKnown.Sectors.Remove(sectorScratch[i]);
                    writer.Reset();
                    NetHeader.Write(writer, NetMessageType.SectorRemove, worldId, tick);
                    writer.Write(new SectorMessage { Guid = guid, SectorPos = sectorScratch[i] });
                    Send(NetDelivery.Reliable, writer);
                }

                // Sector adds and brick deltas.
                foreach (var sectorEntry in data.sectors)
                {
                    int3 sectorPos = sectorEntry.Key;
                    ref Sector sector = ref sectorEntry.Value.Get();

                    bool sectorIsNew = entityKnown.Sectors.Add(sectorPos);
                    if (sectorIsNew)
                    {
                        writer.Reset();
                        NetHeader.Write(writer, NetMessageType.SectorAdd, worldId, tick);
                        writer.Write(new SectorMessage { Guid = guid, SectorPos = sectorPos });
                        Send(NetDelivery.Reliable, writer);

                        if (sector.NonEmptyBrickCount > 0)
                        {
                            WriteBrickBatch(writer, worldId, tick, guid, sectorPos, ref sector, slotMask, fullSector: true);
                        }
                    }
                    else if ((sector.sectorDirtyFlags & (ushort)Sector.ReplicationDirtyMask) != 0)
                    {
                        WriteBrickBatch(writer, worldId, tick, guid, sectorPos, ref sector, slotMask, fullSector: false);
                    }
                }
            }
        }

        private unsafe void WriteBrickBatch(
            NetMessageWriter writer, ushort worldId, uint tick, Guid128 guid, int3 sectorPos,
            ref Sector sector, ushort slotMask, bool fullSector)
        {
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.BrickData, worldId, tick);
            int headerOffset = writer.Reserve<BrickBatchHeader>();

            ushort count = 0;
            ushort dirtyMask = (ushort)Sector.ReplicationDirtyMask;
            for (int brickIdx = 0; brickIdx < Sector.BRICKS_IN_SECTOR; brickIdx++)
            {
                if (sector.brickMap.indices[brickIdx] == Sector.BRICKID_EMPTY)
                {
                    continue;
                }

                ushort dirty = sector.brickDirtyFlags[brickIdx];
                if (!fullSector && (dirty & dirtyMask) == 0)
                {
                    continue;
                }

                writer.Write((ushort)brickIdx);
                writer.Write(dirty);
                sector.WriteReplicatedBrick(writer, brickIdx, slotMask);
                count++;
            }

            if (count == 0)
            {
                return;
            }

            writer.Patch(headerOffset, new BrickBatchHeader { Guid = guid, SectorPos = sectorPos, BrickCount = count });
            Send(NetDelivery.Reliable, writer);
        }

        private static bool TransformEquals(in RigidTransform a, in RigidTransform b)
        {
            return math.all(a.pos == b.pos) && math.all(a.rot.value == b.rot.value);
        }
    }
}
