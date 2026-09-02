# Caelix server-client architecture

**Status:** v1 in progress (branch `dev/fable/server-client-v1`, started 2026-09-03).
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
  `CaelixWorldConfig` (id, gravity, physics settings, replicated slot mask,
  alien propagation flags). It owns its entity map, body map,
  `VoxelPhysicsWorld`, automata stage, force command stream, and outgoing
  event queue.
- `CaelixServer` owns a list of worlds, one fixed-step accumulator shared by
  all of them, the connections, command dispatch, and replication. **All
  worlds tick at the same rate, in lockstep.** The loop is stage-major so
  that a cross-world stage can be inserted between per-world stages later.
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
3. otherwise the host registered for the component's scene, or any host.

In Host role the component registers the entity with the server world and is
bound as the client view for the same guid when the replication spawn message
arrives. Importers and generators that write blocks through the component
keep working, because in a process that runs the server the component's data
API is a direct write into the server world. In a process that does not run
the server those calls throw.

The guid is serialized on the component (generated once in the editor). A
prefab instance needs a fresh guid on first placement. That handling is
deferred.

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
- **Topology.** Per connection, the server diffs the known entity set and the
  known sector set of each entity against the world every tick. New entities
  and sectors are sent in full. Removed ones are sent as despawn or remove.
- **Transforms and flags.** Sent per entity when changed.
- **Never replicate** `PhysicsInfo`. It is derived, and the serializer already
  discards it.
- **Client apply.** `Sector.ApplyReplicatedBrick` copies the raw brick, marks
  `Geometry | GeometryWithLocalNeighbor | BlockBrickAdded` as needed, and
  widens the block AABB to the brick bounds. The client tick is: clear
  require-update, apply messages, propagate Geometry bits only, render, clear
  dirty. Geometry bits cannot allocate sectors, so the client never grows
  phantom sectors.
- **Frozen worlds.** Replication runs inside the tick. A frozen world sends
  nothing. The host always runs one tick on its first frame so a frozen scene
  still gets its initial state.

## 6. Messages, v1

Envelope: `u8 type, u16 worldId, u32 tick`. Then the payload.

| Direction | Message | Payload |
|---|---|---|
| S to C | Hello | replicated slot mask, tick rate, registry hash |
| S to C | EntitySpawn | guid, transform, isStatic, isProtected, hasBody |
| S to C | EntityDespawn | guid |
| S to C | EntityState | guid, isStatic, isProtected |
| S to C | EntityTransform | guid, position, rotation |
| S to C | SectorAdd | guid, sector position |
| S to C | SectorRemove | guid, sector position |
| S to C | BrickData | guid, sector position, brick index, flags, slot records |
| S to C | Event | registered type id, blittable payload |
| S to C | QueryReply | request id, slot records for one voxel |
| C to S | Command | registered type id, blittable payload |
| C to S | Query | request id, guid, position, slot mask |

Engine commands: `SetBlockCommand`, `SetEntityStaticCommand`,
`VoxelBodyForceCommand`. Games register their own command and event types
through the type registries. Engine types are registered first, in a fixed
order, on both sides.

## 7. Transport

`INetChannel` with two implementations: `LocalChannel` (in-process queue,
real serialization) and, later, `UtpChannel` (Unity Transport 2.x) in its own
optional assembly. Netcode for GameObjects, Netcode for Entities, and Mirror
were rejected; see the family notes and `ECS_vs_NonECS_Decision.md`.

## 8. Assemblies

| Assembly | Repo | Contents | References |
|---|---|---|---|
| `Caelix.Core` | Core | `VoxelEntityData`, `Sector`, serializer, dirty propagation, neighborhood reader, tick primitives, `BrickInfo`, `Caelix.Net` codec, channel, registries | Burst, Collections, Mathematics |
| `Caelix.Physics` | Physics | `VoxelBodyData`, `VoxelPhysicsWorld`, `PhysicsWorldConfig` (scene settings holder), setup jobs, force commands | Core, low-level fork |
| `Caelix.Simulation` | Caelix | `CaelixWorld`, `CaelixServer`, replication, engine net types, brick collector | Core, Physics |
| `Caelix` | Caelix | `VoxelEntity`, `VoxelBody`, `CaelixHost`, `CaelixClient`, `ClientWorld`, renderers, raycast, importers, authoring | Simulation, URP |
| `Caelix.Transport.Utp` | Caelix | Unity Transport channel | Core, com.unity.transport. Later |

`PhysicsWorldConfig` stays in the physics package only because it keeps the
scene's serialized solver settings alive. It is a settings holder, not a
simulation object.

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
