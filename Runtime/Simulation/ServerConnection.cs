using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
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

        private static readonly ProfilerMarker s_ReplicateWorldMarker = new("Server.ReplicateWorld");
        private static readonly ProfilerMarker s_ReplicationFullBuildMarker = new("Server.ReplicationFullBuild");

        private readonly Dictionary<ushort, WorldKnown> worlds = new();
        private readonly List<Guid128> guidScratch = new();
        private readonly List<int3> sectorScratch = new();
        private readonly List<ushort> worldScratch = new();

        /// <summary>
        /// Sectors this connection received in full during the current <see cref="ReplicateWorld"/>
        /// call. Their shared-delta slices are redundant and are skipped. Reused across ticks.
        /// </summary>
        private readonly HashSet<(Guid128 Guid, int3 SectorPos)> sentFullThisTick = new();

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

        /// <summary>Sends bytes a <see cref="ReplicationBatch"/> packed. The span is copied by the channel.</summary>
        public void Send(NetDelivery delivery, ReadOnlySpan<byte> payload)
        {
            Channel.Send(delivery, payload);
        }

        /// <summary>Forgets a world so the next <see cref="SyncWorlds"/> sends it again in full.</summary>
        public void ForgetWorld(ushort worldId)
        {
            worlds.Remove(worldId);
        }

        /// <summary>
        /// Brings this connection's world set in line with the server's: a WorldRemove for every
        /// world it knew that is gone or no longer subscribed, then a WorldAdd for every subscribed
        /// world it does not know yet. Runs once per <c>Step</c>, before any world replicates, so
        /// <see cref="ReplicateWorld"/> can rely on the world being known.
        /// </summary>
        internal void SyncWorlds(IReadOnlyList<CaelixWorld> serverWorlds, NetMessageWriter writer, uint tick)
        {
            // Removes first: a world id that is freed and reused in the same tick must reach the
            // client as remove-then-add, not add-then-remove.
            worldScratch.Clear();
            foreach (var kvp in worlds)
            {
                ushort worldId = kvp.Key;
                bool stillThere = false;
                for (int i = 0; i < serverWorlds.Count; i++)
                {
                    if (serverWorlds[i].Id == worldId)
                    {
                        stillThere = true;
                        break;
                    }
                }

                if (!stillThere || !IsSubscribed(worldId))
                {
                    worldScratch.Add(worldId);
                }
            }

            for (int i = 0; i < worldScratch.Count; i++)
            {
                worlds.Remove(worldScratch[i]);
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.WorldRemove, worldScratch[i], tick);
                writer.Write(new WorldRemoveMessage());
                Send(NetDelivery.Reliable, writer);
            }

            for (int i = 0; i < serverWorlds.Count; i++)
            {
                CaelixWorld world = serverWorlds[i];
                if (!IsSubscribed(world.Id) || worlds.ContainsKey(world.Id))
                {
                    continue;
                }

                worlds.Add(world.Id, new WorldKnown());
                writer.Reset();
                NetHeader.Write(writer, NetMessageType.WorldAdd, world.Id, tick);
                writer.Write(new WorldAddMessage { ReplicatedSlotMask = world.Config.replicatedSlotMask });
                Send(NetDelivery.Reliable, writer);
            }
        }

        /// <summary>
        /// Phase B of replication: the per-connection diff. Sends despawns, spawns, state,
        /// transforms and sector adds and removes, packs the sectors this connection has not seen
        /// into <paramref name="full"/> and sends them, then forwards the shared
        /// <paramref name="delta"/> slices that are not already covered by a full sector.
        /// </summary>
        internal unsafe void ReplicateWorld(
            CaelixWorld world, NetMessageWriter writer, ReplicationBatch delta, ReplicationBatch full)
        {
            using var _ = s_ReplicateWorldMarker.Auto();
            if (!IsSubscribed(world.Id))
            {
                return;
            }

            if (!worlds.TryGetValue(world.Id, out WorldKnown known))
            {
                // SyncWorlds runs first every tick and creates it; nothing to do before it did.
                return;
            }

            sentFullThisTick.Clear();
            full.Clear();

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

            // Spawns, state, transforms, sectors.
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

                // Sector adds. An entity that is new to this connection has every sector new, so it
                // is served entirely by the catch-up batch and never by a shared delta slice.
                foreach (var sectorEntry in data.sectors)
                {
                    int3 sectorPos = sectorEntry.Key;
                    if (!entityKnown.Sectors.Add(sectorPos))
                    {
                        continue;
                    }

                    writer.Reset();
                    NetHeader.Write(writer, NetMessageType.SectorAdd, worldId, tick);
                    writer.Write(new SectorMessage { Guid = guid, SectorPos = sectorPos });
                    Send(NetDelivery.Reliable, writer);

                    full.Add(guid, sectorPos, sectorEntry.Value, fullSector: true);
                    sentFullThisTick.Add((guid, sectorPos));
                }
            }

            // Catch-up: the sectors this connection did not know. SectorAdd for each already went
            // out above, in the same order the batch packed them.
            if (full.SectorCount > 0)
            {
                using (s_ReplicationFullBuildMarker.Auto())
                {
                    full.Build(worldId, tick, slotMask);
                }

                for (int i = 0; i < full.Count; i++)
                {
                    if (full[i].Length > 0)
                    {
                        Send(NetDelivery.Reliable, full.Slice(i));
                    }
                }
            }

            // The shared delta. It can only name sectors this connection already knows, because a
            // sector new to it got its SectorAdd earlier in this same call and is in
            // sentFullThisTick. A range for a known sector holds exactly the bricks the old
            // per-connection WriteBrickBatch produced.
            for (int i = 0; i < delta.Count; i++)
            {
                ReplicationRange range = delta[i];
                if (range.Length == 0 || sentFullThisTick.Contains((range.Guid, range.SectorPos)))
                {
                    continue;
                }

                Send(NetDelivery.Reliable, delta.Slice(i));
            }
        }

        private static bool TransformEquals(in RigidTransform a, in RigidTransform b)
        {
            return math.all(a.pos == b.pos) && math.all(a.rot.value == b.rot.value);
        }
    }
}
