# Alien-Entity Aware Voxel Reader Plan

## Summary

This plan describes a simplified alien-entity aware voxel reader for VoxelisX automata.

The intended V1 is **per-entity**, not per-sector-link based:

- Local voxels are authoritative.
- Alien voxels are considered only when the queried local voxel space is empty.
- Alien occupancy is point-sampled at the center of the queried voxel space: `.5, .5, .5`.
- If multiple alien entities occupy the same sample point, the first entity in deterministic order wins.
- Automata code should stay sector-local and should not manually convert through sector, entity, world, and alien coordinate spaces.
- Allocation safety is exposed as a query interface; V1 can use the same center-point rule, and later versions can replace it with full voxel-box overlap.

The goal is a manageable rule-based sandbox, not physically perfect voxel contact semantics.

This file is an implementation plan only. Treat `VoxelisX/` as the package to implement in later work; this revision changes only the root design document.

## Current Baseline

Before implementing this plan, the relevant existing engine shape is:

- `VoxelisXWorld.BrickInfo` contains `EntityId`, `SectorPos`, `BrickOrigin`, `Sector`, and `Neighbors`.
- `AutomataStageInputs` contains `VoxelEntities` and `BricksRequiredUpdate`.
- Automata examples such as `WireWorld` construct `SectorNeighborhoodReaderHelper` manually from `brickInfo.Sector` and `brickInfo.Neighbors`.
- `SectorNeighborhoodReaderHelper` reads local sector-neighborhood data only. It accepts center-sector-local coordinates and crosses same-entity sector boundaries.

The V1 implementation should preserve the useful part of this model: automata jobs continue processing `BrickInfo` and continue passing sector-local coordinates to a reader.

## Coordinate Model

There are three coordinate spaces that matter:

```text
sector-local voxel coordinate
    Coordinate relative to the center sector currently being processed.
    This is what existing automata code uses.

entity-local voxel coordinate
    Coordinate relative to the owning VoxelEntity's voxel space.
    This is used by VoxelEntityData.GetBlock/SetBlock style APIs.

world coordinate
    Unity/world-space position after applying the VoxelEntity transform.
    This is required to compare two independently transformed entities.
```

Existing `WireWorld` style code works in sector-local coordinates:

```text
brickInfo.BrickOrigin       sector-local block coordinate
blockPos                    sector-local block coordinate
blockPos + direction        sector-local coordinate, possibly outside 0..127
```

Alien occupancy naturally requires entity/world conversion:

```text
self sector-local query
-> self entity-local voxel
-> self world sample point
-> alien entity-local point
-> alien voxel
```

That chain is unavoidable if entities can move or rotate independently. The important design rule is that gameplay/automata code should not see it.

For a query `p` passed to `VoxelNeighborhoodReader.GetBlock(p)`:

```text
selfEntityLocalVoxel =
    centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + p

selfLocalSamplePoint =
    selfEntityLocalVoxel + float3(0.5, 0.5, 0.5)

worldSamplePoint =
    SelfLocalToWorld * selfLocalSamplePoint
```

Then for each alien entity in deterministic order:

```text
if worldSamplePoint outside alien world AABB:
    skip

alienLocalPoint =
    AlienWorldToLocal * worldSamplePoint

alienVoxel =
    floor(alienLocalPoint)

alienBlock =
    alien.GetBlock(alienVoxel)
```

If `alienBlock` is non-empty, return it. Otherwise continue.

Important: this plan intentionally does **not** use a precomputed per-sector transform link such as `OtherSectorLocalFromThisSectorLocal`. Keeping the query per-entity is simpler, less fragile at sector boundaries, and easier to evolve.

## V1 Read Semantics

`VoxelNeighborhoodReader.GetBlock(sectorLocalVoxel)` should behave as:

1. Read local data through `SectorNeighborhoodReaderHelper`.
2. If the local block is non-empty, return it.
3. Convert the sector-local coordinate to self entity-local coordinates internally.
4. Use `AlienOccupancyQuery` to apply the center-point alien rule.
5. Return the first non-empty alien block found.
6. If none is found, return `Block.Empty`.

So an alien neighbor is a rule-based fallback answer to:

```text
If this local sector-space voxel is empty, does another entity occupy its center point?
```

It is not a real contact manifold, and it is not a full broadphase collision query.

## Public API

The exposed API should minimize boilerplate. Automata authors should not construct nested readers manually.

### AlienEntityView

Build once per tick from the current world entity list:

```csharp
public struct AlienEntityView
{
    public int EntityId;
    public RigidTransform LocalToWorld;
    public float4x4 WorldToLocal;
    public NativeHashMap<int3, SectorHandle> Sectors;
    public float3 WorldAabbMin;
    public float3 WorldAabbMax;

    public bool ContainsWorldPoint(float3 worldPoint);
    public Block GetBlockAtLocalVoxel(int3 localVoxel);
}
```

`Sectors` is a read-only tick view. It must not be treated as an owning container.

### AlienOccupancyQuery

The per-entity alien query:

```csharp
public struct AlienOccupancyQuery
{
    [ReadOnly] public NativeArray<AlienEntityView> EntitiesInDeterministicOrder;

    public Block GetFirstOccupyingAlienBlock(
        int selfEntityId,
        RigidTransform selfLocalToWorld,
        int3 selfEntityLocalVoxel);

    public bool IsVoxelSpaceOccupied(
        int selfEntityId,
        RigidTransform selfLocalToWorld,
        int3 selfEntityLocalVoxel);
}
```

`GetFirstOccupyingAlienBlock` owns the world-space conversion and alien iteration. It should not require callers to provide world coordinates.

### VoxelNeighborhoodReader

This is the main automata-facing reader. Prefer this name over `AlienAwareNeighborhoodReader`; automata should think in terms of voxel neighborhood reads, not alien plumbing.

```csharp
public struct VoxelNeighborhoodReader
{
    public static VoxelNeighborhoodReader Create(
        VoxelisXWorld.BrickInfo brickInfo,
        AlienOccupancyQuery alienQuery);

    public Block GetBlock(int3 sectorLocalVoxel);

    public bool IsVoxelSpaceOccupied(int3 sectorLocalVoxel);
}
```

Internally, it may contain:

```csharp
private int selfEntityId;
private int3 centerSectorPos;
private RigidTransform selfLocalToWorld;
private SectorNeighborhoodReaderHelper localReader;
private AlienOccupancyQuery alienQuery;
```

Those fields should not appear at automata call sites.

### AutomataReadContext

Add a small factory context for jobs:

```csharp
public struct AutomataReadContext
{
    [ReadOnly] public AlienOccupancyQuery AlienQuery;

    public VoxelNeighborhoodReader CreateReader(VoxelisXWorld.BrickInfo brickInfo)
    {
        return VoxelNeighborhoodReader.Create(brickInfo, AlienQuery);
    }
}
```

Then extend stage inputs:

```csharp
public struct AutomataStageInputs
{
    public NativeList<VoxelEntityData> VoxelEntities;
    public NativeList<BrickInfo> BricksRequiredUpdate;
    public AutomataReadContext ReadContext;
}
```

If implementation simplicity requires keeping `AlienOccupancyQuery AlienQuery` directly in `AutomataStageInputs`, that is acceptable, but automata jobs should still use a single factory method. Avoid this pattern in gameplay code:

```csharp
new AlienAwareNeighborhoodReader
{
    LocalReader = new SectorNeighborhoodReaderHelper(...),
    ...
}
```

Nested construction is engine wiring and should be hidden by the library.

## Example Usage: WireWorld

This example is included so a worker implementing only inside `VoxelisX/` can understand the desired API usage, even though the sample gameplay file may live outside the package.

Existing style:

```csharp
SectorNeighborhoodReaderHelper readerHelper =
    new SectorNeighborhoodReaderHelper(
        brickInfo.Sector,
        brickInfo.Neighbors);
```

Desired style:

```csharp
VoxelNeighborhoodReader reader = ReadContext.CreateReader(brickInfo);
```

Full job example:

```csharp
[BurstCompile]
public struct WireWorldJob : IJobParallelForDefer
{
    public NativeArray<VoxelisXWorld.BrickInfo> brickInfos;
    [ReadOnly] public AutomataReadContext ReadContext;

    public void Execute(int index)
    {
        VoxelisXWorld.BrickInfo brickInfo = brickInfos[index];
        SectorHandle workingSector = brickInfo.Sector;
        VoxelNeighborhoodReader reader = ReadContext.CreateReader(brickInfo);

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int z = 0; z < 8; z++)
                {
                    int3 blockPos = brickInfo.BrickOrigin + new int3(x, y, z);

                    byte solid = 0;
                    foreach (int3 direction in NeighborhoodSettings.Directions)
                    {
                        solid += (byte)(reader.GetBlock(blockPos + direction).id > 0 ? 1 : 0);
                    }

                    workingSector.SetBlock(
                        blockPos.x, blockPos.y, blockPos.z,
                        solid == 4 ? new Block(15, 15, 15, false) : default);
                }
            }
        }
    }
}
```

Scheduling example:

```csharp
public bool Execute(
    VoxelisXWorld.AutomataStageInputs inputs,
    JobHandle stageStart,
    JobHandle chained,
    out JobHandle handle)
{
    var job = new WireWorldJob
    {
        brickInfos = inputs.BricksRequiredUpdate.AsDeferredJobArray(),
        ReadContext = inputs.ReadContext,
    };

    handle = job.Schedule(inputs.BricksRequiredUpdate, 32, chained);
    return true;
}
```

Automata authors should only need to know:

```text
BrickInfo identifies the brick being processed.
ReadContext creates a neighborhood reader for that brick's sector.
reader.GetBlock(...) performs local-first, alien-second reads.
```

They should not need to know:

```text
SectorNeighborhoodReaderHelper construction
SectorNeighborHandles
center sector origin conversion
self entity transform
alien entity transform
world-space AABB rejection
alien local voxel lookup
```

## Tick Integration

Integrate into `VoxelisXWorld.Tick()` before scheduling automata:

1. Sync `VoxelEntity` transforms into `VoxelEntityData`.
2. Build `AlienEntityView` records from the current entity list.
3. Emit views in deterministic entity order.
4. Store the query in `AutomataReadContext`.
5. Put `AutomataReadContext` in `AutomataStageInputs`.
6. Continue collecting `BricksRequiredUpdate` with the existing `BrickCollector`.
7. Automata jobs create `VoxelNeighborhoodReader` from `ReadContext.CreateReader(brickInfo)`.
8. Automata reads use `reader.GetBlock(...)`.
9. Allocation-sensitive writes use `reader.IsVoxelSpaceOccupied(...)` or a write resolver before creating a non-empty block in empty local space.
10. Apply sector snapshots after automata jobs complete.
11. Refresh physics/collider state after accepted writes.
12. Run physics.
13. Rendering and dirty propagation continue afterward according to the world tick policy.

`SectorNeighborhoodReaderHelper` remains local-sector focused. `VoxelNeighborhoodReader` composes local sector reading with alien occupancy.

## Allocation Query

Allocation policy should not be baked into `Sector.SetBlock`.

`Sector.SetBlock` is storage-level mutation. Alien overlap policy belongs in an automata helper or write resolver.

V1 rule:

```text
if old local block is empty
and new block is non-empty
and reader.IsVoxelSpaceOccupied(target) is true
then reject the write
```

Clearing or replacing a voxel already owned by the same entity should not require alien approval.

The automata-facing allocation check can stay minimal:

```csharp
public bool IsVoxelSpaceOccupied(int3 sectorLocalVoxel);
```

Behind the reader:

```text
local non-empty block -> occupied
local empty block but alien center-point hit -> occupied
otherwise -> free
```

Later, a stricter implementation can test full voxel-box overlap behind the same API.

## Determinism

Do not rely on hash map iteration order.

V1 should build `AlienEntityView` in deterministic order. Temporary tick index order is acceptable for experiments if `BrickInfo.EntityId` matches the view index, but a stable persistent `VoxelEntity` id is better for spawning, saving, replay, and networking.

Tie behavior:

```text
first alien entity in deterministic order whose non-empty block contains the sample point wins
```

This is arbitrary, but stable.

If entity ids become sparse or persistent, add an entity-id-to-view-index lookup. Do not assume forever:

```text
EntityId == index in EntitiesInDeterministicOrder
```

## Per-Entity V1 And Later Pruning

Keep V1 per-entity:

```text
for each alien entity in deterministic order:
    AABB reject
    transform point into alien local space
    read alien voxel
```

This is simple, testable, and avoids a precomputed per-sector transform/link model.

Possible later evolution:

### Entity AABB List

This is V1. It is good when entity count is modest and automata only processes dirty/update bricks.

### Sector Candidate Cache

If entity-level AABBs become too broad, the world can build a per-sector candidate cache:

```text
self SectorKey -> candidate alien entity ids or alien sector handles
```

This cache is only a pruning structure. Query semantics stay the same:

```text
self sector-local query
-> self entity-local sample point
-> alien entity-local lookup
```

Pros:

- Matches the current `BrickInfo` sector processing unit.
- Reduces alien entity scans for sparse worlds.
- Candidate lists can be sorted deterministically.
- Easier to debug than a global cell hash.

Cons:

- Needs rebuild or invalidation when entities move, rotate, or sectors load/unload.
- Naive building can be expensive with many sectors.
- Rotated sector AABBs create false positives, so exact point queries are still required.
- Boundary queries may target a neighboring self sector; the reader must compute the target entity-local voxel and choose candidates accordingly if the cache becomes sector-keyed.

Do not expose this cache to automata code. It should sit behind `AlienOccupancyQuery`.

### Internal Cell Broadphase

A world-cell broadphase can still be useful, but only as an internal way to build entity or sector candidates faster. It should not become the automata-facing API.

### Brick-Level Cache

If sector candidates are still too broad, cache non-empty alien bricks. This is the most precise broadphase but has the highest rebuild cost. Defer it until profiling proves sector or entity candidates are too expensive.

## Recommended Milestones

1. Add `AlienEntityView` and `AlienOccupancyQuery`.
2. Add `VoxelNeighborhoodReader.Create(brickInfo, alienQuery)`.
3. Add `AutomataReadContext.CreateReader(brickInfo)`.
4. Extend `AutomataStageInputs` with `ReadContext`.
5. Convert one sample automata to the compact API shown above.
6. Add tests for local-first behavior, alien fallback, sector-local coordinate conversion, deterministic tie behavior, and allocation rejection.
7. Profile the per-entity AABB query before adding any sector candidate cache.

## Open Implementation Notes

- `VoxelEntityData` currently has shallow-copy semantics in the tick path. Query views should be read-only tick views and should not own native containers.
- Reader construction must correctly account for sector-local versus entity-local coordinates. This is the main correctness risk.
- `SectorNeighborhoodReaderHelper.GetBlock` handles same-entity sector-neighborhood reads. Alien queries should only run after that local read returns empty.
- Automata writes currently call `Sector.SetBlock` directly in jobs. Allocation rejection is cleaner with buffered write commands, but V1 can start with helper-guarded direct writes.
- Full voxel-box overlap can replace center-point occupancy later behind the same `IsVoxelSpaceOccupied` API.
