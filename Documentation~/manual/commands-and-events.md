# Register commands, events, and queries

Status: Current API walkthrough. Checked against Caelix `253d632` and Core
`e5ead50` on 2026-09-06. Examples were source-reviewed and compile-checked against
local Unity validation assemblies. They were not executed in Unity during this pass.

A command asks the server to change a world. An event tells subscribed clients
that something happened. A query asks for committed state and replies only to
the requesting client. Use unmanaged payload structs and resolve targets with
the handler's world and the payload's entity GUID.

## Register a shared message schema

`NetTypeRegistry` assigns wire IDs in registration order. Register the same types
in the same order on both ends: engine types first, then game types. Re-registering
a type returns its existing ID. Put the list in one shared method; do not let
component enable order choose message IDs.

This example defines an edit command and an acknowledgement event. Place it in
an assembly that references the Caelix runtime assemblies.

```csharp
using Caelix;
using Caelix.Client;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;
using Unity.Mathematics;
using UnityEngine;

public struct PaintVoxel
{
    public Guid128 Entity;
    public int3 Position;
    public Block Value;
}

public struct VoxelPainted
{
    public Guid128 Entity;
    public int3 Position;
}

public static class PaintMessages
{
    public static void RegisterSchema(NetTypeRegistry types)
    {
        types.Register<PaintVoxel>();
        types.Register<VoxelPainted>();
    }

    public static void InstallServer(CaelixServer server)
    {
        RegisterSchema(server.Types);
        server.RegisterCommand<PaintVoxel>((connection, world, command) =>
        {
            if (!world.TryGetEntity(command.Entity, out var entity))
                return;
            if (entity.isProtected)
                return;

            // Add game-specific permission/range/material validation here.
            world.SetBlock(command.Entity, command.Position, command.Value);
            world.EmitEvent(new VoxelPainted
            {
                Entity = command.Entity,
                Position = command.Position
            });
        });
    }

    public static void InstallClient(CaelixClient client)
    {
        RegisterSchema(client.Types);
        client.RegisterEvent<VoxelPainted>((worldId, message) =>
        {
            Debug.Log($"World {worldId}: painted {message.Position}");
        });
    }
}
```

The event acknowledges an accepted request; the example emits it even if the new
value equals the existing value. It is broadcast to subscribed clients, not just
the requester. The callback receives a world ID so a game with multiple worlds
can route its presentation correctly.

## Install and send

For a host, call `EnsureInitialized()`, `PaintMessages.InstallServer(host.Server)`,
and `PaintMessages.InstallClient(host.Client)` during bootstrap before the first
tick/handshake. Install handlers once: registering another handler for the same
type replaces the existing one; this API is not a multicast subscription list.

The host shares the registry between server and client. With separate registry
instances, both server and client constructors register engine types, then call
the shared game schema method on each before adding game handlers or sending data.

After creating an entity, send this fragment from a tool that has `client` and
`entityId`:

```csharp
client.SendCommand(new PaintVoxel
{
    Entity = entityId,
    Position = new int3(1, 2, 3),
    Value = new Block(0x8001)
}, worldId: 0);
```

The server dispatches by message kind, resolves the header's world ID, then resolves
the registered type ID to its handler. The handler looks up the entity GUID in
that world. A missing world is ignored. Never send a managed object or native
pointer as a reference to a game object.

On an accepted edit, the next explicit/running server step publishes state and
drains events. `client.Receive()` applies incoming messages and invokes event
handlers on the calling thread (the host's main thread). A frozen host retains
edit commands until an explicit step or unfreeze.

## Ask for committed state

For a voxel inspection tool, use the built-in `QueryVoxel`. This fragment assumes
the same client and entity ID as above. It requests the Block slot:

```csharp
client.QueryVoxel(entityId, new int3(1, 2, 3),
    (ushort)(1 << (int)SectorSlotId.Block), reply =>
    {
        if (reply.Found && reply.TryGetSlot<Block>(SectorSlotId.Block, out var value))
            Debug.Log($"Committed Block value: {value.data}");
    });
```

`Found` indicates that the entity/sector was found, not that the voxel is occupied.
The callback runs on a later `Receive()`. A custom loop can service it with
`server.ProcessQueries(); client.Receive();` without advancing simulation.

For a game-specific query:

1. Add the unmanaged request and reply types, in that order, to the shared schema.
2. Call `server.RegisterQuery<TRequest, TReply>(handler)`. Its handler receives
   `(ServerConnection connection, CaelixWorld world, in TRequest request,
   ref NetMessageReader requestPayload, NetMessageWriter replyPayload)` and
   returns `TReply`. Keep it read-only so it remains safe while paused.
3. Call `client.SendQuery<TRequest, TReply>(in request, payload, callback, worldId)`.
   Supply `ReadOnlySpan<byte>.Empty` when no trailing payload is needed.
4. The reply callback has `(ushort worldId, in TReply reply,
   ref NetMessageReader payload)`. Consume/copy payload data within that callback.
   Request IDs associate replies with callbacks.

There is no query timeout in this API. Do not block the main thread waiting for
a callback; represent pending state in the tool and handle teardown at the game level.

## Payload and lifetime rules

Fixed-size structs are copied as raw unmanaged bytes. Both ends must agree on
layout and versions. The hash uses registered type names and sizes; it does not
prove semantic compatibility of fields with identical sizes.

For variable-length command data, use `SendCommand(in command, payload, worldId)`
and the `PayloadCommandHandler<T>` overload. Validate counts and available bytes
before reading. Events currently carry a fixed unmanaged struct. `EmitEvent` queues
managed payload bytes; do not call it from a Burst job. Transfer job results to
main-thread code before emitting.

## Implementation and checks

- [Server registration, dispatch, and event drain](../../Runtime/Simulation/CaelixServer.cs)
- [Client send and callback APIs](../../Runtime/Client/CaelixClient.cs)
- [Built-in engine handlers](../../Runtime/Simulation/EngineNetTypes.cs)
- [Message round-trip and pause tests](../../Tests/Editor/ReplicationTests.cs)

When adding a message, check matching registries, an unknown entity, an accepted
edit, callback routing by world, and paused query/edit behavior.
