using System;
using System.Collections.Generic;
using UnityEngine;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>Handler for a command followed by a payload. The reader holds the payload bytes; <c>Remaining</c> is their length.</summary>
    public delegate void PayloadCommandHandler<T>(ServerConnection connection, CaelixWorld world, in T command, ref NetMessageReader payload) where T : unmanaged;

    /// <summary>
    /// Handler for a typed query. Returns the reply struct; bytes written to <paramref name="replyPayload"/>
    /// follow it on the wire. The writer is reset before the call.
    /// </summary>
    public delegate TReply QueryHandler<TRequest, TReply>(ServerConnection connection, CaelixWorld world, in TRequest request, ref NetMessageReader requestPayload, NetMessageWriter replyPayload)
        where TRequest : unmanaged where TReply : unmanaged;

    /// <summary>
    /// Owns the worlds, the fixed-step clock they share, the client connections, command
    /// dispatch, and replication. All worlds tick in lockstep. No GameObject; a bootstrap
    /// component drives <see cref="Update"/>.
    /// </summary>
    public sealed class CaelixServer : IDisposable
    {
        private delegate void CommandHandler(ServerConnection connection, CaelixWorld world, ref NetMessageReader reader);

        private delegate void QueryDispatch(ServerConnection connection, CaelixWorld world, uint requestId, ref NetMessageReader reader);

        private readonly List<CaelixWorld> worlds = new();
        private readonly List<ServerConnection> connections = new();
        private readonly Dictionary<ushort, CommandHandler> commandHandlers = new();
        private readonly Dictionary<ushort, QueryDispatch> queryHandlers = new();
        private readonly NetMessageWriter writer = new(64 * 1024);
        private readonly NetMessageWriter replyPayloadWriter = new(64 * 1024);
        private readonly HashSet<Type> unregisteredEventTypesWarned = new();
        private float accumulator;
        private int nextConnectionId = 1;
        private bool disposed;

        /// <summary>Shared type registry. Engine types are registered by the constructor.</summary>
        public NetTypeRegistry Types { get; }

        public IReadOnlyList<CaelixWorld> Worlds => worlds;
        public IReadOnlyList<ServerConnection> Connections => connections;
        public CaelixWorld DefaultWorld => worlds.Count > 0 ? worlds[0] : null;

        /// <summary>Ticks per second for every world. The fixed step is 1 / TickRate.</summary>
        public float TickRate { get; set; } = 100f;

        /// <summary>While frozen, <see cref="Update"/> runs no ticks. <see cref="Step"/> still works.</summary>
        public bool Frozen { get; set; }

        /// <summary>
        /// Upper bound on ticks per <see cref="Update"/>; the backlog is dropped past it. Only the
        /// accumulator path uses this; a host that steps from <c>FixedUpdate</c> relies on
        /// <c>Time.maximumDeltaTime</c> instead.
        /// </summary>
        public int MaxTicksPerUpdate { get; set; } = 4;

        /// <summary>Number of completed server ticks.</summary>
        public uint TickIndex { get; private set; }

        public float FixedDeltaTime => TickRate > 0f ? 1f / TickRate : 0f;

        public CaelixServer(NetTypeRegistry types = null)
        {
            Types = types ?? new NetTypeRegistry();
            EngineNetTypes.RegisterAll(Types);
            EngineNetTypes.RegisterServerHandlers(this);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            for (int i = 0; i < connections.Count; i++)
            {
                connections[i].Channel.Dispose();
            }

            connections.Clear();

            for (int i = 0; i < worlds.Count; i++)
            {
                worlds[i].Dispose();
            }

            worlds.Clear();
        }

        #region Worlds

        public CaelixWorld CreateWorld(CaelixWorldConfig config)
        {
            if (FindWorld(config.worldId) != null)
            {
                throw new InvalidOperationException($"World id {config.worldId} already exists.");
            }

            var world = new CaelixWorld(config);
            worlds.Add(world);
            return world;
        }

        public bool RemoveWorld(CaelixWorld world)
        {
            if (!worlds.Remove(world))
            {
                return false;
            }

            // TODO: VibeReview: Why `CreateWorld` does not notify connections but `RemoveWorld` does?
            // Also should we explicitly notify them here?
            for (int i = 0; i < connections.Count; i++)
            {
                connections[i].ForgetWorld(world.Id);
            }

            world.Dispose();
            return true;
        }

        public CaelixWorld FindWorld(ushort worldId)
        {
            for (int i = 0; i < worlds.Count; i++)
            {
                if (worlds[i].Id == worldId) return worlds[i];
            }

            return null;
        }

        #endregion

        #region Connections

        /// <summary>
        /// Accepts a channel. The client gets the handshake and the full world state on the next
        /// tick's replication.
        /// </summary>
        public ServerConnection AddConnection(INetChannel channel)
        {
            var connection = new ServerConnection(nextConnectionId++, channel);
            connections.Add(connection);
            return connection;
        }

        public bool RemoveConnection(ServerConnection connection)
        {
            if (!connections.Remove(connection))
            {
                return false;
            }

            for (int i = 0; i < worlds.Count; i++)
            {
                worlds[i].ReleaseDrags(connection.Id);
            }

            return true;
        }

        private void SendHello(ServerConnection connection)
        {
            CaelixWorld world = DefaultWorld;
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Hello, world?.Id ?? 0, TickIndex);
            writer.Write(new HelloMessage
            {
                ReplicatedSlotMask = world?.Config.replicatedSlotMask ?? Sector.DefaultReplicatedSlotMask,
                TickRate = TickRate,
                RegistryHash = Types.Hash,
                WorldCount = (ushort)worlds.Count,
            });
            connection.Send(NetDelivery.Reliable, writer);
        }

        #endregion

        #region Commands and queries

        /// <summary>
        /// Registers a command type and its handler. The handler runs on the main thread before the
        /// tick in which the command takes effect.
        /// </summary>
        public void RegisterCommand<T>(Action<ServerConnection, CaelixWorld, T> handler) where T : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort id = Types.Register<T>();
            commandHandlers[id] = (ServerConnection connection, CaelixWorld world, ref NetMessageReader reader) =>
            {
                handler(connection, world, reader.Read<T>());
            };
        }

        /// <summary>
        /// Registers a command type whose message carries trailing payload bytes. The handler reads
        /// them from the reader it is given; <c>Remaining</c> is how many are left.
        /// </summary>
        public void RegisterCommand<T>(PayloadCommandHandler<T> handler) where T : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort id = Types.Register<T>();
            commandHandlers[id] = (ServerConnection connection, CaelixWorld world, ref NetMessageReader reader) =>
            {
                T command = reader.Read<T>();
                handler(connection, world, in command, ref reader);
            };
        }

        /// <summary>
        /// Registers a query type and its reply type, in that order, together with the handler that
        /// answers it. The reply struct and whatever the handler wrote to the reply payload writer
        /// are sent back to the asking connection.
        /// </summary>
        public void RegisterQuery<TRequest, TReply>(QueryHandler<TRequest, TReply> handler)
            where TRequest : unmanaged where TReply : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort requestTypeId = Types.Register<TRequest>();
            Types.Register<TReply>();
            queryHandlers[requestTypeId] = (ServerConnection connection, CaelixWorld world, uint requestId, ref NetMessageReader reader) =>
            {
                TRequest request = reader.Read<TRequest>();
                replyPayloadWriter.Reset();
                TReply reply = handler(connection, world, in request, ref reader, replyPayloadWriter);

                writer.Reset();
                NetHeader.Write(writer, NetMessageType.TypedQueryReply, world.Id, world.TickIndex);
                writer.Write(new TypedQueryHeader { TypeId = Types.GetId<TReply>(), RequestId = requestId });
                writer.Write(reply);
                writer.WriteBytes(replyPayloadWriter.AsSpan());
                connection.Send(NetDelivery.Reliable, writer);
            };
        }

        /// <summary>Drains every connection's inbox and dispatches commands and queries.</summary>
        public void ProcessIncoming()
        {
            for (int c = 0; c < connections.Count; c++)
            {
                ServerConnection connection = connections[c];
                while (connection.Channel.TryReceive(out byte[] message))
                {
                    try
                    {
                        Dispatch(connection, message);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }
            }
        }

        private void Dispatch(ServerConnection connection, byte[] message)
        {
            var reader = new NetMessageReader(message);
            NetHeader header = NetHeader.Read(ref reader);
            CaelixWorld world = FindWorld(header.WorldId);
            if (world == null)
            {
                return;
            }

            switch (header.Type)
            {
                case NetMessageType.Command:
                {
                    var typed = reader.Read<TypedPayloadHeader>();
                    if (commandHandlers.TryGetValue(typed.TypeId, out CommandHandler handler))
                    {
                        handler(connection, world, ref reader);
                    }
                    else
                    {
                        Debug.LogWarning($"[CaelixServer] Unhandled command type id {typed.TypeId}.");
                    }

                    break;
                }
                case NetMessageType.Query:
                {
                    var query = reader.Read<VoxelQueryMessage>();
                    AnswerVoxelQuery(connection, world, in query);
                    break;
                }
                case NetMessageType.TypedQuery:
                {
                    var h = reader.Read<TypedQueryHeader>();
                    if (queryHandlers.TryGetValue(h.TypeId, out QueryDispatch dispatch))
                    {
                        dispatch(connection, world, h.RequestId, ref reader);
                    }
                    else
                    {
                        Debug.LogWarning($"[CaelixServer] Unhandled query type id {h.TypeId}.");
                    }

                    break;
                }
                default:
                    Debug.LogWarning($"[CaelixServer] Unexpected message type {header.Type} from connection {connection.Id}.");
                    break;
            }
        }

        private void AnswerVoxelQuery(ServerConnection connection, CaelixWorld world, in VoxelQueryMessage query)
        {
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.QueryReply, world.Id, world.TickIndex);
            int headerOffset = writer.Reserve<VoxelQueryReplyHeader>();

            bool found = false;
            if (world.TryGetEntity(query.Guid, out VoxelEntityData data))
            {
                int shift = Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS;
                var sectorPos = new Unity.Mathematics.int3(
                    query.Position.x >> shift, query.Position.y >> shift, query.Position.z >> shift);
                if (data.sectors.TryGetValue(sectorPos, out SectorHandle handle))
                {
                    int mask = Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS);
                    handle.Get().WriteVoxelSlots(
                        writer,
                        query.Position.x & mask, query.Position.y & mask, query.Position.z & mask,
                        query.SlotMask);
                    found = true;
                }
            }

            if (!found)
            {
                writer.Write((byte)0);
            }

            writer.Patch(headerOffset, new VoxelQueryReplyHeader
            {
                RequestId = query.RequestId,
                Guid = query.Guid,
                Position = query.Position,
                Found = (byte)(found ? 1 : 0),
            });
            connection.Send(NetDelivery.Reliable, writer);
        }

        #endregion

        #region Ticking

        /// <summary>
        /// Self-clocked driver: processes incoming commands, then runs as many fixed steps as the
        /// accumulated time allows. Call once per frame from a loop that has no fixed step of its
        /// own, such as a headless server. <c>CaelixHost</c> does not use this; it calls
        /// <see cref="Step()"/> once per Unity fixed step. Both paths share <see cref="Step()"/>.
        /// </summary>
        public void Update(float deltaTime)
        {
            ProcessIncoming();

            if (Frozen || TickRate <= 0f)
            {
                return;
            }

            float dt = FixedDeltaTime;
            accumulator += Mathf.Max(0f, deltaTime);
            int steps = 0;
            while (accumulator >= dt && steps < MaxTicksPerUpdate)
            {
                Step();
                accumulator -= dt;
                steps++;
            }

            if (steps >= MaxTicksPerUpdate)
            {
                // Drop the backlog rather than spiral.
                accumulator = 0f;
            }
        }

        /// <summary>
        /// Runs exactly one tick of every world and replicates the result. Works while frozen.
        /// Pending commands and queries are applied first, so a command sent before this call
        /// takes effect in this tick.
        /// </summary>
        public void Step()
        {
            ProcessIncoming();

            for (int c = 0; c < connections.Count; c++)
            {
                if (!connections[c].HelloSent && connections[c].IsConnected)
                {
                    SendHello(connections[c]);
                    connections[c].HelloSent = true;
                }
            }

            float dt = FixedDeltaTime;
            for (int w = 0; w < worlds.Count; w++)
            {
                CaelixWorld world = worlds[w];
                world.TickSimulate(dt);
                Replicate(world);
                world.EndTick();
            }

            TickIndex++;
        }

        public void Step(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Step();
            }
        }

        /// <summary>
        /// Notify all connections with delta packages for client replication (rendering etc.).
        /// </summary>
        /// <param name="world"></param>
        private void Replicate(CaelixWorld world)
        {
            for (int c = 0; c < connections.Count; c++)
            {
                if (connections[c].IsConnected)
                {
                    // TODO: VibeReview: Shouldn't we build this once and send to all connections instead?
                    // Currently it builds for each connection from stretch right? Seems heavy.
                    // Even if later client's update region is independent, most clients should overlap in normal game sessions.
                    connections[c].ReplicateWorld(world, writer);
                }
            }

            DrainEvents(world);
        }

        private void DrainEvents(CaelixWorld world)
        {
            List<CaelixWorld.PendingEvent> events = world.PendingEvents;
            if (events.Count == 0)
            {
                return;
            }

            for (int i = 0; i < events.Count; i++)
            {
                CaelixWorld.PendingEvent evt = events[i];
                if (!Types.TryGetId(evt.Type, out ushort typeId))
                {
                    if (unregisteredEventTypesWarned.Add(evt.Type))
                    {
                        Debug.LogWarning($"[CaelixServer] Event type {evt.Type.FullName} is not registered; dropped.");
                    }

                    continue;
                }

                writer.Reset();
                NetHeader.Write(writer, NetMessageType.Event, world.Id, world.TickIndex);
                writer.Write(new TypedPayloadHeader { TypeId = typeId });
                writer.WriteBytes(evt.Payload);

                for (int c = 0; c < connections.Count; c++)
                {
                    if (connections[c].IsConnected && connections[c].IsSubscribed(world.Id))
                    {
                        connections[c].Send(NetDelivery.Reliable, writer);
                    }
                }
            }

            events.Clear();
        }

        #endregion
    }
}
