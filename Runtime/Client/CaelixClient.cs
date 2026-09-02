using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix.Client
{
    /// <summary>
    /// One voxel's values as answered by a server query. Slots the server does not have for
    /// that sector are absent.
    /// </summary>
    public sealed class VoxelQueryReply
    {
        public Guid128 Guid;
        public int3 Position;
        public bool Found;
        public readonly List<(byte SlotId, byte[] Bytes)> Slots = new();

        public unsafe bool TryGetSlot<T>(SectorSlotId slotId, out T value) where T : unmanaged
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].SlotId != (byte)slotId) continue;
                byte[] bytes = Slots[i].Bytes;
                if (bytes.Length != UnsafeUtility.SizeOf<T>()) break;
                fixed (byte* src = bytes)
                {
                    UnsafeUtility.CopyPtrToStructure(src, out value);
                }

                return true;
            }

            value = default;
            return false;
        }
    }

    /// <summary>
    /// The client end of the boundary: pumps the channel, applies replication into
    /// <see cref="ClientWorld"/> replicas, and sends commands and queries. Owns no simulation.
    /// </summary>
    public sealed class CaelixClient : IDisposable
    {
        private delegate void EventHandler(ushort worldId, ref NetMessageReader reader);

        private readonly INetChannel channel;
        private readonly Dictionary<ushort, ClientWorld> worlds = new();
        private readonly List<ClientWorld> worldList = new();
        private readonly Dictionary<ushort, EventHandler> eventHandlers = new();
        private readonly Dictionary<uint, Action<VoxelQueryReply>> pendingQueries = new();
        private readonly NetMessageWriter writer = new(4096);
        private uint nextRequestId = 1;
        private bool disposed;

        /// <summary>Shared type registry. Engine types are registered by the constructor.</summary>
        public NetTypeRegistry Types { get; }

        /// <summary>
        /// The host bootstrap that owns this client, when server and client share a process.
        /// Client-spawned view components use it to reach server data in host mode.
        /// </summary>
        public CaelixHost Host { get; set; }

        public bool IsConnected { get; private set; }
        public HelloMessage Hello { get; private set; }
        public uint LastServerTick { get; private set; }

        /// <summary>The default world (id 0), created on first use.</summary>
        public ClientWorld World => GetOrCreateWorld(0);

        public IReadOnlyList<ClientWorld> Worlds => worldList;

        public event Action<ClientWorld> WorldAdded;

        public CaelixClient(INetChannel channel, NetTypeRegistry types = null)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Types = types ?? new NetTypeRegistry();
            EngineNetTypes.RegisterAll(Types);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            for (int i = 0; i < worldList.Count; i++)
            {
                worldList[i].Dispose();
            }

            worldList.Clear();
            worlds.Clear();
            channel.Dispose();
        }

        public ClientWorld GetOrCreateWorld(ushort worldId)
        {
            if (!worlds.TryGetValue(worldId, out ClientWorld world))
            {
                world = new ClientWorld(this, worldId, IsConnected ? Hello.ReplicatedSlotMask : Sector.DefaultReplicatedSlotMask);
                worlds.Add(worldId, world);
                worldList.Add(world);
                WorldAdded?.Invoke(world);
            }

            return world;
        }

        #region Authored views

        public void RegisterAuthoredView(VoxelEntity component, ushort worldId = 0)
        {
            GetOrCreateWorld(worldId).RegisterAuthored(component);
        }

        public void UnregisterAuthoredView(VoxelEntity component, ushort worldId = 0)
        {
            if (worlds.TryGetValue(worldId, out ClientWorld world))
            {
                world.UnregisterAuthored(component);
            }
        }

        #endregion

        #region Frame

        /// <summary>
        /// Begins the client frame: clears last frame's require-update flags, applies every
        /// pending message, and propagates Geometry bits so the renderers see this frame's
        /// changes. Call <see cref="EndFrame"/> after the renderers ran.
        /// </summary>
        public void Update()
        {
            for (int i = 0; i < worldList.Count; i++)
            {
                worldList[i].BeginFrame();
            }

            while (channel.TryReceive(out byte[] message))
            {
                try
                {
                    Apply(message);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            for (int i = 0; i < worldList.Count; i++)
            {
                worldList[i].PropagateForRender();
            }
        }

        public void EndFrame()
        {
            for (int i = 0; i < worldList.Count; i++)
            {
                worldList[i].EndFrame();
            }
        }

        private void Apply(byte[] message)
        {
            var reader = new NetMessageReader(message);
            NetHeader header = NetHeader.Read(ref reader);
            if (header.Tick > LastServerTick)
            {
                LastServerTick = header.Tick;
            }

            switch (header.Type)
            {
                case NetMessageType.Hello:
                {
                    Hello = reader.Read<HelloMessage>();
                    IsConnected = true;
                    if (Hello.RegistryHash != Types.Hash)
                    {
                        Debug.LogError(
                            "[CaelixClient] Net type registry mismatch with the server. Commands and events " +
                            "must be registered in the same order on both ends.");
                    }

                    ClientWorld world = GetOrCreateWorld(header.WorldId);
                    world.ReplicatedSlotMask = Hello.ReplicatedSlotMask;
                    break;
                }
                case NetMessageType.EntitySpawn:
                {
                    var m = reader.Read<EntitySpawnMessage>();
                    GetOrCreateWorld(header.WorldId).OnSpawn(in m);
                    break;
                }
                case NetMessageType.EntityDespawn:
                {
                    var m = reader.Read<EntityDespawnMessage>();
                    GetOrCreateWorld(header.WorldId).OnDespawn(in m);
                    break;
                }
                case NetMessageType.EntityState:
                {
                    var m = reader.Read<EntityStateMessage>();
                    GetOrCreateWorld(header.WorldId).OnState(in m);
                    break;
                }
                case NetMessageType.EntityTransform:
                {
                    var m = reader.Read<EntityTransformMessage>();
                    GetOrCreateWorld(header.WorldId).OnTransform(in m);
                    break;
                }
                case NetMessageType.SectorAdd:
                {
                    var m = reader.Read<SectorMessage>();
                    GetOrCreateWorld(header.WorldId).OnSectorAdd(in m);
                    break;
                }
                case NetMessageType.SectorRemove:
                {
                    var m = reader.Read<SectorMessage>();
                    GetOrCreateWorld(header.WorldId).OnSectorRemove(in m);
                    break;
                }
                case NetMessageType.BrickData:
                {
                    var m = reader.Read<BrickBatchHeader>();
                    GetOrCreateWorld(header.WorldId).OnBrickBatch(in m, ref reader);
                    break;
                }
                case NetMessageType.Event:
                {
                    var typed = reader.Read<TypedPayloadHeader>();
                    if (eventHandlers.TryGetValue(typed.TypeId, out EventHandler handler))
                    {
                        handler(header.WorldId, ref reader);
                    }

                    break;
                }
                case NetMessageType.QueryReply:
                {
                    var h = reader.Read<VoxelQueryReplyHeader>();
                    var reply = new VoxelQueryReply { Guid = h.Guid, Position = h.Position, Found = h.Found != 0 };
                    int count = reader.Read<byte>();
                    for (int i = 0; i < count; i++)
                    {
                        byte slotId = reader.Read<byte>();
                        int stride = reader.Read<ushort>();
                        reply.Slots.Add((slotId, reader.ReadSpan(stride).ToArray()));
                    }

                    if (pendingQueries.TryGetValue(h.RequestId, out Action<VoxelQueryReply> callback))
                    {
                        pendingQueries.Remove(h.RequestId);
                        callback?.Invoke(reply);
                    }

                    break;
                }
                default:
                    Debug.LogWarning($"[CaelixClient] Unexpected message type {header.Type}.");
                    break;
            }
        }

        #endregion

        #region Commands, queries, events

        /// <summary>Sends a registered command to the server. Applied before the server's next tick.</summary>
        public void SendCommand<T>(in T command, ushort worldId = 0) where T : unmanaged
        {
            ushort typeId = Types.GetId<T>();
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Command, worldId, LastServerTick);
            writer.Write(new TypedPayloadHeader { TypeId = typeId });
            writer.Write(command);
            channel.Send(NetDelivery.Reliable, writer.AsSpan());
        }

        public void SetBlock(Guid128 entity, int3 position, Block block, ushort worldId = 0)
        {
            SendCommand(new SetBlockCommand { Entity = entity, Position = position, Block = block }, worldId);
        }

        public void SetEntityStatic(Guid128 entity, bool isStatic, ushort worldId = 0)
        {
            SendCommand(new SetEntityStaticCommand { Entity = entity, IsStatic = (byte)(isStatic ? 1 : 0) }, worldId);
        }

        public void AddForce(in VoxelBodyForceCommand command, ushort worldId = 0)
        {
            SendCommand(in command, worldId);
        }

        /// <summary>
        /// Starts or refreshes a held drag. Send it whenever the target moves; the server keeps the
        /// latest and applies the spring every tick. Resend within the world's drag timeout to keep
        /// it alive.
        /// </summary>
        public void SetDrag(in DragCommand command, ushort worldId = 0)
        {
            SendCommand(in command, worldId);
        }

        public void ReleaseDrag(Guid128 entity, ushort worldId = 0)
        {
            SendCommand(new ReleaseDragCommand { Entity = entity }, worldId);
        }

        /// <summary>Asks the server for one voxel's slot values. The callback runs when the reply arrives.</summary>
        public void QueryVoxel(Guid128 entity, int3 position, ushort slotMask, Action<VoxelQueryReply> callback, ushort worldId = 0)
        {
            uint requestId = nextRequestId++;
            pendingQueries[requestId] = callback;
            writer.Reset();
            NetHeader.Write(writer, NetMessageType.Query, worldId, LastServerTick);
            writer.Write(new VoxelQueryMessage
            {
                RequestId = requestId,
                Guid = entity,
                Position = position,
                SlotMask = slotMask,
            });
            channel.Send(NetDelivery.Reliable, writer.AsSpan());
        }

        /// <summary>Registers an event type and its handler. Handlers run on the main thread during <see cref="Update"/>.</summary>
        public void RegisterEvent<T>(Action<ushort, T> handler) where T : unmanaged
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ushort id = Types.Register<T>();
            eventHandlers[id] = (ushort worldId, ref NetMessageReader reader) => handler(worldId, reader.Read<T>());
        }

        #endregion
    }
}
