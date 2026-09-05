# Caelix server-client architecture

**Status:** v1 landed (branch `dev/fable/server-client-v1`, started 2026-09-03,
commits `532b157` to `aa97d84`; later branches carry it forward). Then world lifecycle
messages, two-phase replication and one host per process, 2026-09-05. Section 10 lists
what is still open. Last checked against the code on 2026-09-05.
**Decided by:** owner, after the design discussion recorded in the family `CLAUDE.md` notes.

This document is the boundary contract. Code that crosses it without going
through the channel is a bug.

## 1. Roles

| Role | Owns | Runs | Needs |
|---|---|---|---|
| Server, authoritative | `VoxelEntityData`, `VoxelBodyData`, every sector slot | automata stage, dirty propagation, alien propagation, physics, save/load, streaming | Core, Physics. No URP, no Camera, no GameObject |
| Client, render replica | A copy of the replicated slots for every sector of every entity | delta apply, Geometry propagation, renderer, raycast, tools, UI, audio | Simulation (host mode), URP |
| Shared | Core data types, `Guid128`, sector serializer, message codec, channel interface, type registries | | |

The client is a **render replica**, not a mirror. It holds the Block slot,
runs no automata and no physics, and turns inputs into commands. A full
mirror (all slots, prediction) is a later level and is not planned for v1.

Rejected alternatives, and why:

- **Deterministic lockstep.** The physics step is multithreaded with
  `NativeStream` ordering and the automata random state is seeded from wall
  time. One divergence is a permanent desync with no cheap repair.
- **True dumb terminal, no voxel data on the client.** A ray tracing brick
  record is about the size of the Block slot, meshes are larger, and the tool
  raycast needs local block data at frame rate. It moves per-renderer work to
  the server and saves nothing.
- **Zero-copy host.** The renderer reading server sectors directly would give
  it two code paths, one for host and one for remote. The Block-only replica
  costs about one third of the server's per-brick memory. Accept the copy,
  measure, revisit only if memory becomes the constraint.

## 2. Worlds

- `Caelix.Simulation.CaelixWorld` is a plain class. It is constructed from a
  `CaelixWorldConfig` (id, name, physics settings including gravity,
  replicated slot mask, alien propagation flags, drag timeout). It owns its
  entity map, body map, `VoxelPhysicsWorld` (which owns the force command
  stream), automata stage, and outgoing event queue.
- `CaelixServer` owns a list of worlds, the tick clock, the connections,
  command dispatch, and replication. **All worlds tick at the same rate, in
  lockstep.** `Step()` runs one tick of every world. The loop inside `Step()`
  is world-major today: for each world, simulate, replicate, end tick. A
  stage-major loop, where a cross-world stage can sit between per-world
  stages, is deferred (section 9).
- **The server owns the clock rule, not the host.** `Step()` runs one tick
  unconditionally. `Tick()` is the gated form: it runs `Step()` unless the
  server is frozen, and it returns whether a tick ran. A frozen server still
  runs its very first tick, because replication happens inside a tick and a
  client that never received one has no state at all. So `Frozen` means "no
  tick after the first", and `Tick()` returning false is the normal steady
  state of a frozen scene.
- Two ways to drive the clock. `CaelixHost` calls `Tick()` once per Unity
  fixed step and nothing anywhere else (section 10). `CaelixServer.Update(deltaTime)`
  is a self-clocked driver with its own accumulator and a `MaxTicksPerUpdate`
  backlog cap, kept for a loop that has no fixed step of its own, such as a
  headless server; it calls `Tick()` too and drops the accumulator when the
  freeze stops it. Nothing calls it yet.
- A connection receives every world by default. `ServerConnection.SubscribedWorlds`
  restricts it to a subset.
- An entity lives in exactly one world. The wire address of an entity is
  world id plus `Guid128`. The address of a brick is world id, guid, sector
  position, brick index.
- Cross-world dynamics are out of scope. Nothing in the world class may
  assume one world per process.
- Custom tick logic is done through hook stages for now. Generic worlds and
  Burst function pointers stay available if a game must replace a stage
  rather than add to it.

## 3. The server keeps no GameObjects

- The world's `NativeHashMap<Guid128, VoxelEntityData>` and
  `NativeHashMap<Guid128, VoxelBodyData>` are the source of truth and are
  updated in place. There is no per-tick copy in and copy back.
- `VoxelEntityData.transform` is the truth. `previousTransform` is copied
  from `transform` at tick start. The physics export writes `transform`. No
  Unity `Transform` is read or written on the server. Runtime code that moves
  an entity calls `CaelixWorld.SetEntityTransform`.
- Inspector settings live on bootstrap components and are pushed into the
  running world each frame. Debug views draw from data.
- PhysX is gone: no `Rigidbody`, no `UnityPhysicsCollider`, no
  `TemporaryCharacterCollider`, no `VoxelBody.physicsEnabled`.

## 4. Authoring without a bake step

`VoxelEntity` and `VoxelBody` are **authoring and client view components**.
They live in the client assembly. In `OnEnable` a `VoxelEntity` finds its
host by convention and registers:

1. a serialized host reference on the component, if set;
2. otherwise a `CaelixHost` found through the parents;
3. otherwise `CaelixHost.Current`, the host of this process.

**There is one host per process.** A host claims `CaelixHost.Current` in
`EnsureInitialized` and `OnEnable` and releases it in `OnDisable` and
`OnDestroy`; a second enabled host logs an error, still initializes so that
nothing throws, and is not the one components find. `Current` falls back to a
scene search, because a component's `OnEnable` may run before the host's.

The per-scene lookup (`FindForScene`, `All`) went. A host owns a server, a
client and a channel between them, so one host per scene meant several
independent servers in one process with no path from one to another, and an
entity in an additively loaded scene would silently join a different world.
Worlds, not hosts, are how one process holds several simulations (section 2).
`CaelixHost.Any` remains as an obsolete alias of `Current`.

In Host role the component registers the entity with the server world and is
bound as the client view for the same guid when the replication spawn message
arrives. Importers and generators that write blocks through the component
keep working, because in a process that runs the server the component's data
API is a direct write into the server world. In a process that does not run
the server the API splits: `SetBlock` and `IsStatic` send a command, `GetBlock`
and `GetSlot` read the view, and the sector-level API (`SetSlot`, sector
add/remove, dirty-flag calls) throws.

The guid is serialized on the component (generated once in the editor). A
prefab instance needs a fresh guid on first placement. That handling is
deferred.

> **Divergence (2026-09-04).** The implementation does not generate the guid
> in the editor. `VoxelEntity.ResolveGuid` draws a random guid at runtime when
> the serialized value is zero and does not store it, so an authored entity
> gets a new guid every play session unless code sets `PersistentGuid`. The
> design above and the implementation are both on the table; which one to
> keep is an open decision.

Edit-mode visibility of handmade entities is a later feature. It needs a
voxel asset type and an edit-mode renderer driver. The view-source design of
the renderer is what makes it possible.

## 5. Replication

- **Replicated slot mask.** `CaelixWorldConfig.replicatedSlotMask`, one bit
  per slot id, default Block only. Sent in the handshake. Delta records carry
  slot id, stride, and raw bytes, so the client never needs the C# type of a
  slot.
- **Delta detection.** The Block slot is the only writer that sets the
  `Geometry` dirty bit, so a brick with `Geometry`, `BlockBrickAdded`, or
  `BlockBrickRemoved` set at the end of the tick is a Block delta. Capture
  happens after alien propagation and before `ClearDirtyFlags`. For more
  replicated slots later, add one dirty bit `SlotReplicate` that any write to
  a masked slot sets.
- **Two phases.** Phase A, `ReplicationBatch`, packs BrickData messages for a
  list of sectors into one byte buffer, in parallel over sectors, with a size
  job, an exclusive prefix sum, and a write job over `UnsafeNetWriter`. It is
  built once per world per tick from the world's dirty sectors and every
  connection sends the same bytes. `CollectDirtySectors` walks the entity map
  and picks the sectors whose `sectorDirtyFlags` meet the replication mask;
  the packing walks `SectorDirtyBrickEnumerator`, the one legitimate reader of
  the raw write-side dirty flags, because it runs inside the tick before
  `ClearDirtyFlags`.
- **Phase B** is `ServerConnection.ReplicateWorld`, one call per connection.
  It diffs the connection's knowledge, sends the lifecycle messages, packs
  whatever that connection still has to catch up on into a second batch and
  sends it, then forwards the shared delta slices, skipping any sector it just
  sent in full this tick. A connection that is new to an entity has all of its
  sectors new, so it is served entirely by the catch-up batch; a shared delta
  slice can only name a sector the connection already knows, because a new
  one got its `SectorAdd` earlier in the same call.
- **Topology.** Per connection, the server diffs the known entity set and the
  known sector set of each entity against the world every tick. New entities
  and sectors are sent in full. Removed ones are sent as despawn or remove.
- **Transforms and flags.** Sent per entity when changed.
- **Never replicate** `PhysicsInfo`. It is derived, and the serializer already
  discards it.
- **Client apply.** `Sector.ApplyReplicatedBrick` copies the raw brick, marks
  `Geometry | GeometryWithLocalNeighbor | BlockBrickAdded` as needed, and
  widens the block AABB to the brick bounds. The client frame is three calls
  plus the renderers: `Receive` (clear require-update, then apply every pending
  message), `PrepareRender` (propagate the render flags `Geometry`,
  `GeometryWithLocalNeighbor`, `BlockBrickAdded`, `BlockBrickRemoved`), the
  renderers, then `EndFrame` (clear dirty). None of those four bits is in
  `DirtyPropagationSettings.DirtyFlagsCanAllocateLocalBricks`, so the client
  never grows phantom sectors.
- **Frozen server.** Replication runs inside the tick. Freeze is a server-level
  switch (`CaelixServer.Frozen`, driven by the host's `freeze` field), not a
  per-world flag; a frozen server sends nothing after its first tick, and that
  first tick is what gives a frozen scene its initial state (section 2).

## 6. Messages, v1

Envelope: `u8 type, u16 worldId, u32 tick`. Then the payload.

World id 0 is a real world (the default world), so a message that is not scoped
to one world carries the sentinel `NetHeader.NoWorld` (`0xFFFF`) instead. Hello
is the only such message today. **Every dispatcher tests for the sentinel before
it looks a world up**, and warns for any other type that arrives with it; a
world-scoped message never carries it.

| Direction | Message | Payload |
|---|---|---|
| S to C | Hello | tick rate, registry hash. Header world id is `NoWorld` |
| S to C | WorldAdd | replicated slot mask. Header world id names the world |
| S to C | WorldRemove | (padding byte). Header world id names the world |
| S to C | EntitySpawn | guid, transform, isStatic, isProtected, hasBody |
| S to C | EntityDespawn | guid |
| S to C | EntityState | guid, isStatic, isProtected, hasBody |
| S to C | EntityTransform | guid, transform (rotation, position) |
| S to C | SectorAdd | guid, sector position |
| S to C | SectorRemove | guid, sector position |
| S to C | BrickData | guid, sector position, brick count; then per brick: brick index, dirty flags, slot records |
| S to C | Event | registered type id, blittable payload |
| S to C | QueryReply | request id, guid, position, found; then slot records for one voxel |
| S to C | TypedQueryReply | request id, registered reply type id, reply struct, trailing payload |
| C to S | Command | registered type id, blittable payload |
| C to S | Query | request id, guid, position, slot mask |
| C to S | TypedQuery | request id, registered request type id, request struct, trailing payload |

`BrickData` is one message per sector per tick: a new sector sends every
allocated brick, a known sector sends only the bricks whose dirty flags meet
the replication mask. The client ignores the per-brick dirty flags; they are
informational.

Order per connection, every `Step()`: `Hello` once, then `WorldAdd` /
`WorldRemove` for every world that appeared or went, then that tick's
replication. `Hello` goes out on the first `Step()` after the connection is
accepted, not at accept time, so game types registered during scene start are
part of the registry hash. World lifecycle is a diff like everything else:
`CreateWorld` and `RemoveWorld` notify nobody, and the next `Step()` compares
the connection's known world set against the server's, sending removes before
adds so a reused world id reaches the client in the right order. Unsubscribing
a connection from a world reads as a removal. The client creates a replica on
`WorldAdd` and disposes it on `WorldRemove`, raising `WorldAdded` and
`WorldRemoving`; a message naming an unknown world still creates a replica, so
nothing is lost if the order is ever violated.

A command may also carry a trailing payload: send it with
`CaelixClient.SendCommand(in T, ReadOnlySpan<byte>)` and receive it with
`CaelixServer.RegisterCommand<T>(PayloadCommandHandler<T>)`. A typed query pairs a
request struct with a reply struct, and both may carry a payload
(`CaelixClient.SendQuery` / `CaelixServer.RegisterQuery`). In every case the
payload length is whatever remains in the message, so a handler reads the count
from its own struct.

Engine commands: `SetBlockCommand`, `SetEntityStaticCommand`,
`VoxelBodyForceCommand` (one-shot forces, impulses, velocity changes),
`DragCommand`, `ReleaseDragCommand` and `SpawnEntityCommand` (create an empty
entity with a client-chosen guid, so commands sent in the same frame can already
address it). A drag is held input, not a force: the
server keeps one per client and body, computes the spring every tick from the
body's own pose and velocity, and drops it on release or after
`dragTimeoutSeconds` without a refresh. Clients send the target whenever it
moves, at any rate. `DragCommand.AnchorAtCenterOfMass` holds the body by its
centre of mass instead of `AnchorLocal`, which is the stable hold for a scripted
grab and the only one a client cannot compute exactly. Games register their own
command and event types through the type registries. Engine types are registered
first, in a fixed order, on both sides.

## 7. Transport

`INetChannel` with two implementations: `LocalChannel` (in-process queue,
real serialization) and, later, `UtpChannel` (Unity Transport 2.x) in its own
optional assembly. Netcode for GameObjects, Netcode for Entities, and Mirror
were rejected; see the family notes and `ECS_vs_NonECS_Decision.md`.

## 8. Assemblies

| Assembly | Repo | Contents | References |
|---|---|---|---|
| `Caelix.Core` | Core | `VoxelEntityData`, `Sector`, serializer, dirty propagation, neighborhood reader, tick primitives, `BrickInfo`, `Caelix.Net` codec, channel, registries | Burst, Collections, Mathematics |
| `Caelix.Physics` | Physics | `VoxelBodyData`, `VoxelPhysicsWorld`, `PhysicsWorldConfig` (scene settings holder), setup jobs, force commands | Core, low-level fork, Entities, Numerics |
| `Caelix.Simulation` | Caelix | `CaelixWorld`, `CaelixServer`, `ServerConnection`, replication, engine net types, brick collector | Core, Physics, low-level fork, Entities, Numerics |
| `Caelix` | Caelix | `VoxelEntity`, `VoxelBody`, `CaelixHost`, `CaelixClient`, `ClientWorld`, renderers, raycast, importers, authoring | Simulation, URP |
| `Caelix.Transport.Utp` | Caelix | Unity Transport channel | Core, com.unity.transport. Later |

`PhysicsWorldConfig` stays in the physics package only because it keeps the
scene's serialized solver settings alive. It is a settings holder, not a
simulation object.

Namespaces do not follow assemblies. Most of `Caelix.Physics` declares
`namespace Caelix.Simulation` (only `PhysicsStepInputs` is in `Caelix`), the
same namespace as the Simulation assembly. Look at the asmdef, not the
namespace, to find which assembly owns a type.

## 9. v1 scope

Included:

- One server world, whole world sent on connect. No streaming, no interest
  management.
- No player entity. Free camera. Raycast on the client replica. Engine tools
  send commands.
- Block slot only. Other slots through queries.
- Server has no GameObjects. Data owns the transform.
- Host role in one Play Mode session with `LocalChannel`.
- Command and event registries. Titania's Spark and chime stay in Titania.
- Worlds are first-class from the start.

Deferred:

- Unity Transport channel and dedicated server build.
- Client and Server roles in the bootstrap.
- Streaming and per-client interest. `InfiniteLoader` stays unticked.
- Transform interpolation, edit prediction, compression.
- Serialized guid handling for prefab instances.
- Edit-mode voxel assets and rendering.
- Titania split into server-side and client-side assemblies.
- Stage-major batching of the physics step across worlds.

## 10. Implementation notes (v1, 2026-09-03)

- **`SharedHashMap` is load-bearing.** `VoxelEntityData` is copied by value everywhere:
  the world store, `GetDataCopy`, views, jobs. Its sector and neighbor maps therefore
  need handle semantics. `UnsafeHashMap` embeds its hash helper by value, so a copy has
  its own count and free index and, after a resize, a dangling buffer pointer. The
  wrapper allocates the map on the heap once and every copy points at it. The old
  copy-in / copy-back tick existed only to work around this. Do not replace the wrapper
  with a plain `UnsafeHashMap`; the symptom is a silent infinite loop in map
  enumeration on the second tick.
- **No Burst direct calls on the tick path.** `[BurstCompile]` static methods called from
  managed code compile synchronously in the Editor. Use a Burst job and `Run()` instead
  (`CollectBrickJob`, `PreviewBuilder`).
- **Host frame order.** `FixedUpdate` is the only place a tick runs: push the
  inspector settings, then one `Server.Tick()`, timed. `driveFixedTimestep` sets
  `Time.fixedDeltaTime` from `targetTPS`. `Update` never ticks and never pushes
  settings; it runs `Client.Receive()`, `Client.PrepareRender()`, the raycast tick,
  the renderer ticks, then `Client.EndFrame()`, and collects the timings.
  `HostTimingStats.ServerTicks` is how many ticks ran since the last frame, so 0 on a
  frozen scene and n after a hitch. Render frame rate and tick rate are independent;
  the client applies whatever ticks landed since the last frame. No interpolation yet,
  so bodies show the latest tick's pose.
- **Profiler markers.** Server: `Server.ProcessIncoming`, `Server.TickSimulate`,
  `Server.Replicate`, `Server.ReplicationBuild` (phase A, the shared delta),
  `Server.ReplicateWorld` (phase B, per connection), `Server.ReplicationFullBuild`
  (phase A again, for one connection's catch-up), `Server.EndTick`,
  `Server.DrainEvents`. Client: `Client.ClearRequireUpdate`, `Client.Receive`,
  `Client.ApplyBrickBatch`, `Client.PropagateForRender`, `Client.EndFrame`. Host:
  `Host.ServerTick`, `Host.ClientFrame`, `Host.Renderers`.
- **Scene compatibility.** `CaelixHost.cs` and `PhysicsWorldConfig.cs` keep the script
  GUIDs of `CaelixWorld` and `CaelixPhysicsWorld`, and `VoxelEntity` / `VoxelBody` kept
  theirs across the assembly move, so existing scenes stay wired. `SimplePlayer` and
  `TemporaryCharacterCollider` were deleted; scenes that had them show a missing-script
  warning until the component is removed in the editor.
- **Titania.** Automata hooks register on `host.World.AutomataStage`. WireWorld chimes
  are `ChimeNoteEvent` events (declared in `Assets/Scripts/Dynamics/WireWorld.cs`) emitted by
  the server and played by a client handler in `TitaniaCore`. Interaction tools send commands
  and queries. Titania's own message types live in
  `Assets/Scripts/Interaction/TitaniaNetTypes.cs` (`WriteVoxelsCommand` with a `VoxelRecord`
  payload, `EntityVoxelsQuery`/`EntityVoxelsReply`); that file also registers `ChimeNoteEvent`.
  All are registered after the engine types on both ends by
  `TitaniaNetTypes.EnsureRegistered(host)`. That file is the reference for game-defined
  messages. The tools read the client replica (`EntityView`), never server data.
  Titania still calls the obsolete `CaelixHost.Any` alias until it moves onto this
  branch; that is why the alias exists.
- **Not done in v1:** the rendering assembly is not yet excluded from Dedicated Server
  builds; `InfiniteLoader` is not ticked; guids on authored entities are runtime-random
  unless set through `PersistentGuid`.
