# Caelix Development Notes

This file captures engine contracts that should stay true unless a change explicitly redesigns the affected systems and tests.

Start with the [documentation index](Documentation~/index.md). Follow the
[documentation guide](Documentation~/documentation-guide.md) when writing pages.
Before changing simulation, read [tick and dirty propagation](Documentation~/internals/tick-and-dirty.md)
and the [server/client contract](Documentation~/internals/SERVER_CLIENT_ARCHITECTURE.md).

## Key Assumptions

- Voxels are full cubic cells. A block either occupies its whole voxel space or is empty; there are no partial voxels, sub-voxel shapes, slopes, or per-block collision meshes.
- Voxel size is constant and uniform in entity-local voxel space.
- Blocks contain fixed-size data only. Current `Block` storage is one packed `ushort` (16 bits); metadata lives in separate slots. Store variable-length gameplay data externally and reference it through fixed-size metadata or an entity-level side table.
- Voxel entities may translate and rotate, but not scale. Treat entity scale as exactly `1`; non-uniform or animated scale would require redesigning neighborhood reads, alien reads, AABBs, physics, and rendering.
- The active topology is Moore neighborhood: 26 neighbors including faces, edges, and corners.
- Automata should be written as local block-state systems. Information should move through neighbor reads/writes at about one voxel per tick unless the system explicitly documents a different model.
- Rigidbody and entity motion are outside the automata propagation-speed rule. A whole entity can move farther than one voxel per physics step; alien dirty propagation handles transform-based influence separately.
- Local voxel data is authoritative. Alien voxel reads are fallback occupancy reads only when the local voxel is empty.

## Storage Model

- `Block.Empty` means all Block data bits are zero. In the current layout, `id` returns that same 16-bit value.
- Sectors are sparse by brick allocation. An unallocated brick slot returns empty blocks and setting empty into an unallocated brick is a no-op.
- Allocated bricks are not automatically deallocated when they become empty.
- `Sector.NonEmptyBricks` currently means the refreshed allocated-brick list, not a guaranteed list of bricks containing non-empty blocks.
- Brick and sector dimensions are fixed by `Sector`: bricks are `8x8x8` blocks, sectors are `16x16x16` bricks, and sectors are `128x128x128` blocks.

## Dirty / Require-Update Contract

- `dirty` means source-side changes from this tick.
- `requireUpdate` means target-side work scheduled by propagation.
- Automata consumes `requireUpdate`, writes block changes, and those writes produce new `dirty`.
- Dirty propagation converts `dirty` into next-tick `requireUpdate`.
- Do not treat `dirty` and `requireUpdate` as interchangeable flags.
- Local dirty propagation may create missing neighbor sectors when dirty boundary bricks need same-entity propagation.
- Alien dirty propagation must not create target sectors or bricks; it only marks existing allocated target bricks as requiring updates.

## General Tick Dataflow

```text
Server processes messages; authoritative entity/body maps stay owned by the world
-> sectors with requireUpdate activate snapshots
-> required bricks are collected
-> alien read context is built
-> automata hooks run against snapshots/read context
-> jobs complete and snapshots are applied
-> consumed requireUpdate flags are cleared
-> dirty flags propagate locally
-> occupancy masks and body properties are refreshed
-> physics simulation runs
-> alien influence propagates across existing target bricks
-> server replication reads dirty state and drains events
-> dirty flags are cleared
-> client receives and prepares its replica; renderers consume frame work
```

## Development Process

- Keep automata code sector/brick-local where possible.
- Use the existing voxel enumerators. Do not add raw sweeps over the 4,096 brick
  positions to discover allocated, dirty, or required-update bricks. Read the
  [Core enumerator rules and inventory](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/reference/enumerators.md),
  including the current required-update collector limitation.
- Use `VoxelNeighborhoodReader` and `AutomataReadContext` instead of manually converting through sector, entity, world, and alien coordinate spaces.
- Add focused Editor tests under `Tests/Editor` when changing storage, topology, dirty propagation, alien reads, or tick behavior.
- Do not change sector/brick size, neighborhood topology, block payload size, or scale assumptions casually; these are cross-cutting engine contracts.
- Treat the [archived alien plans](Documentation~/archive/index.md) as historical design context; verify against current code before relying on them.
- Do not manually edit generated propagation mask tables unless the generator and related tests are updated together.
