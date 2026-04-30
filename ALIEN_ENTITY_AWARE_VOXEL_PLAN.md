# Alien-Entity Aware Voxel Update Plan

## Summary

This plan defines a simplified alien-entity aware voxel update system for Titania/VoxelisX.

The core gameplay rule is:

- Local voxels are authoritative.
- Alien voxels are considered only when the queried local voxel space is empty.
- Alien occupancy is point-sampled at the center of the queried voxel space: `.5, .5, .5`.
- If more than one alien entity occupies that sample point, choose the first by a stable deterministic order.
- Allocation safety is exposed through an occupancy-query interface, with the first implementation using the same center-point rule.

This deliberately trades physical realism for stable, manageable automata rules.

## V1 Rule Model

For an automata query from entity `A` at local voxel coordinate `target`:

1. Read `A`'s own voxel at `target` through the existing sector neighborhood path.
2. If the local block is non-empty, return it.
3. If the local block is empty, compute the sample point:

   ```text
   selfLocalPoint = target + float3(0.5, 0.5, 0.5)
   worldPoint = SelfTransform * selfLocalPoint
   ```

4. Iterate alien entities in deterministic order.
5. For each alien entity, reject quickly if `worldPoint` is outside that entity's world AABB.
6. Transform the point into alien local voxel space:

   ```text
   alienLocalPoint = AlienWorldToLocal * worldPoint
   alienVoxel = floor(alienLocalPoint)
   ```

7. Read the alien block at `alienVoxel`.
8. Return the first non-empty alien block found.
9. If none are found, return `Block.Empty`.

This means an alien neighbor is not a full geometric contact. It is a rule-based answer to:

```text
Is the center point of this empty local voxel space occupied by another voxel entity?
```

## Key Runtime Types

Add a world-built query view used by automata jobs:

```csharp
public struct AlienEntityView
{
    public int EntityId;
    public RigidTransform LocalToWorld;
    public float4x4 WorldToLocal;
    public NativeHashMap<int3, SectorHandle> Sectors;
    public Aabb WorldAabb;
}
```

Add a query object:

```csharp
public struct AlienOccupancyQuery
{
    [ReadOnly] public NativeArray<AlienEntityView> EntitiesInDeterministicOrder;

    public Block GetFirstOccupyingAlienBlock(
        int selfEntityId,
        RigidTransform selfLocalToWorld,
        int3 selfLocalVoxel);

    public bool IsVoxelSpaceOccupied(
        int selfEntityId,
        RigidTransform selfLocalToWorld,
        int3 selfLocalVoxel);
}
```

Wrap the existing local neighborhood reader:

```csharp
public struct AlienAwareNeighborhoodReader
{
    public int SelfEntityId;
    public RigidTransform SelfLocalToWorld;
    public SectorNeighborhoodReaderHelper LocalReader;
    public AlienOccupancyQuery AlienQuery;

    public Block GetBlock(int3 selfLocalVoxel)
    {
        Block local = LocalReader.GetBlock(selfLocalVoxel);
        if (!local.isEmpty)
            return local;

        return AlienQuery.GetFirstOccupyingAlienBlock(
            SelfEntityId,
            SelfLocalToWorld,
            selfLocalVoxel);
    }
}
```

`SectorNeighborhoodReaderHelper` should remain local-sector focused. The alien behavior should live in a wrapper so the existing local storage path stays simple and reusable.

## V1 Integration

Integrate into the current `VoxelisXWorld.Tick()` flow:

1. Sync `VoxelEntity` transforms into `VoxelEntityData`, as currently done before ticking.
2. Build `AlienEntityView` records from the current entity list.
3. Sort or emit those records in stable `EntityId` order.
4. Add `AlienOccupancyQuery` to `AutomataStageInputs`.
5. Continue collecting `BricksRequiredUpdate` with the current `BrickCollector`.
6. In automata jobs, construct `AlienAwareNeighborhoodReader` instead of directly using `SectorNeighborhoodReaderHelper`.
7. Automata reads use local-first, alien-second behavior.
8. Writes that allocate a non-empty voxel into an empty local space call `IsVoxelSpaceOccupied` first.
9. If occupied, reject the write for V1.
10. Apply sector snapshots after automata jobs complete, as currently done.
11. Refresh physics/collider data after accepted writes.
12. Run physics.
13. Rendering and dirty propagation continue after physics as current pipeline policy dictates.

V1 should not require a contact graph. It uses a stable world view for reads and a simple query interface for allocation.

## Determinism

Do not rely on hash map iteration order for alien priority.

V1 should assign or derive a stable entity id and build `EntitiesInDeterministicOrder` in ascending id order. The current tick index used by `BrickInfo.EntityId` can be used temporarily, but a persistent `VoxelEntity` id is better once save/load or entity spawning order matters.

Tie behavior is:

```text
first alien entity in deterministic order whose non-empty block contains the sample point wins
```

This is arbitrary, but stable and easy to reason about.

## Allocation Interface

V1 allocation checks should not be baked into `Sector.SetBlock`.

`Sector.SetBlock` is a storage primitive. It currently allocates bricks and records dirty state. Alien overlap policy belongs above it, in a write resolver or automata helper.

The initial interface can be:

```csharp
public interface IAlienVoxelOccupancyQuery
{
    bool IsVoxelSpaceOccupied(
        int selfEntityId,
        RigidTransform selfLocalToWorld,
        int3 selfLocalVoxel);
}
```

For V1, this uses center-point occupancy. Later implementations can test full voxel box overlap without changing automata-facing code.

Recommended V1 allocation policy:

```text
if old local block is empty
and new block is non-empty
and alien query says occupied
then reject the write
```

Replacing or clearing an already-owned local block should not require alien occupancy approval.

## V1 Performance Model

The first version can use a simple entity AABB list:

```text
for each alien entity:
    if world point outside alien AABB: continue
    transform point into alien local space
    read alien voxel
```

This is acceptable when the number of voxel entities is modest. The query is cheap compared to full voxel contact generation, and automata only queries dirty/update bricks.

To avoid avoidable cost:

- Precompute `WorldToLocal` once per tick.
- Precompute `WorldAabb` once per tick.
- Keep entity views in a `NativeArray`.
- Keep the query read-only inside automata jobs.
- Avoid allocations inside `GetBlock`.

## V3: Sector-Level Broadphase

If entity-level AABBs become too broad for large sparse entities, index sectors instead of whole entities.

Use a world-space uniform grid:

```csharp
public struct BroadphaseSectorEntry
{
    public int EntityId;
    public int3 SectorCoord;
    public SectorHandle Sector;
}
```

Store:

```csharp
NativeParallelMultiHashMap<int3, BroadphaseSectorEntry> CellToSectors;
```

Build:

1. For each entity in deterministic order.
2. For each loaded sector in that entity.
3. Transform the sector local AABB to world AABB.
4. Compute broadphase cells overlapped by that AABB.
5. Add one `BroadphaseSectorEntry` to each overlapped cell.

Query:

1. Convert `worldPoint` to a broadphase cell.
2. Retrieve sector candidates from that cell.
3. Deduplicate candidates if needed.
4. Sort by stable entity id and sector coordinate if deterministic order is required.
5. For each candidate, transform `worldPoint` into that entity's local space.
6. Verify the computed local sector matches the candidate sector.
7. Read the voxel.

V3 reduces alien checks for sparse worlds but increases build cost and memory. It should be introduced only after V1 profiling shows entity-level filtering is too coarse.

## V4: Brick-Level Broadphase

If sector-level candidates are still too broad, index occupied or non-empty bricks.

Use entries like:

```csharp
public struct BroadphaseBrickEntry
{
    public int EntityId;
    public int3 SectorCoord;
    public short BrickIndex;
    public SectorHandle Sector;
}
```

Build from each sector's non-empty brick list:

1. Ensure `Sector.UpdateNonEmptyBricks()` or equivalent data is current.
2. For each non-empty brick, compute its local AABB.
3. Transform that brick AABB to world space.
4. Insert the brick entry into each overlapped broadphase cell.

Query:

1. Convert `worldPoint` to broadphase cell.
2. Retrieve brick candidates.
3. Sort or otherwise consume in deterministic order.
4. Transform point into candidate entity local space.
5. Verify the point maps into the candidate brick.
6. Read the exact voxel.

V4 is the most precise broadphase, but it is expensive to rebuild when many bricks move, rotate, or mutate. It is best reserved for worlds with many large sparse entities where V3 still returns too many sector candidates.

## Recommended Milestones

1. Add `AlienEntityView`, `AlienOccupancyQuery`, and `AlienAwareNeighborhoodReader`.
2. Extend `AutomataStageInputs` with the query view.
3. Convert one sample automata, such as `WireWorld`, to use the alien-aware reader.
4. Add center-point allocation rejection through a helper or write resolver.
5. Add editor tests for local-first behavior, alien fallback behavior, deterministic tie behavior, and allocation rejection.
6. Profile entity-AABB V1 before implementing sector or brick broadphase.

## Open Implementation Notes

- V1 needs a stable entity id. Temporary tick index is acceptable for experiments, but persistent ids are preferred.
- Current `VoxelEntityData` copy semantics are shallow. The alien query should be treated as a read-only tick view and should not own native containers.
- Automata writes currently call `Sector.SetBlock` directly in jobs. Allocation rejection will be cleaner if write commands are buffered before mutation, but V1 can start with a helper that checks before direct non-empty writes into empty local space.
- Full voxel-box overlap can replace center-point occupancy later behind the same allocation interface.
