# Alien-Entity Aware Voxel Update Plan

## Summary

This plan defines a simplified alien-entity aware voxel update system for VoxelisX/Titania.

The intended gameplay rule is:

- Automata code works in **sector-local coordinates**.
- Local voxels are authoritative.
- Alien voxels are considered only when the queried local voxel space is empty.
- Alien occupancy is point-sampled at the center of the queried voxel space: `.5, .5, .5`.
- If multiple alien sectors could answer the same query, choose the first by deterministic ordering.
- Allocation safety is exposed through an occupancy-query interface. V1 can use the same center-point rule, while later versions can test full voxel volume overlap.

The key implementation goal is to keep coordinate conversion and alien tracking out of gameplay automata code. Automata should create a reader and call `GetBlock`.

## Coordinate Model

The public automata-facing coordinate system is **sector-local block coordinates**.

For a brick update inside sector `S`, automata code should continue to use:

```text
blockPos = brickInfo.BrickOrigin + localOffset
reader.GetBlock(blockPos + direction)
```

The reader accepts sector-local coordinates. Coordinates may be outside `[0, 127]` when automata reads across sector boundaries, just like `SectorNeighborhoodReaderHelper` already supports.

Internally, alien lookups need to relate one entity's sector space to another entity's sector space. The awkward full path is:

```text
self sector-local -> self entity-local -> world -> alien entity-local -> alien sector-local
```

This math is unavoidable for independently transformed entities, especially with rotation. It should be hidden behind precomputed sector links.

The preferred per-query transform is:

```text
self sector-local point -> alien sector-local point
```

Each alien sector link should precompute:

```csharp
public struct AlienSectorLink
{
    public int OtherEntityId;
    public int3 OtherSectorPos;
    public SectorHandle OtherSector;

    // Maps a point in this sector's local block space directly into
    // the alien sector's local block space.
    public float4x4 OtherSectorLocalFromThisSectorLocal;
}
```

The matrix is built once per tick:

```text
thisSectorToWorld =
    ThisEntityLocalToWorld * Translate(thisSectorPos * 128)

otherSectorToWorld =
    OtherEntityLocalToWorld * Translate(otherSectorPos * 128)

OtherSectorLocalFromThisSectorLocal =
    inverse(otherSectorToWorld) * thisSectorToWorld
```

Then the runtime point-sampled alien read is simple:

```text
p = sectorLocalVoxel + float3(0.5, 0.5, 0.5)
q = OtherSectorLocalFromThisSectorLocal * p
alienVoxel = floor(q)
if alienVoxel is inside [0, 127], read OtherSector.GetBlock(alienVoxel)
```

This keeps the world-space and entity-center math out of automata systems.

## Per-Sector Alien Cache

Instead of exposing a cell broadphase to automata, cache alien candidates per sector.

Use a directed graph:

```text
(self entity, self sector) -> ordered alien sector links
```

Suggested storage:

```csharp
public struct SectorKey : IEquatable<SectorKey>
{
    public int EntityId;
    public int3 SectorPos;
}

public struct AlienSectorLinkRange
{
    public int Start;
    public int Length;
}

public struct AlienSectorLinkGraph
{
    [ReadOnly] public NativeHashMap<SectorKey, AlienSectorLinkRange> Ranges;
    [ReadOnly] public NativeArray<AlienSectorLink> Links;
}
```

The graph is built before automata. Each sector gets a deterministic, contiguous range of links. Query code does not enumerate every alien entity; it only walks the current sector's candidate range.

Candidate ordering:

```text
OtherEntityId ascending
OtherSectorPos.x ascending
OtherSectorPos.y ascending
OtherSectorPos.z ascending
```

This gives stable "first alien wins" behavior.

## Building The Sector Graph

V1 can build the graph directly from entity sectors with broad AABB pruning:

1. Build a list of all entity sectors.
2. For each sector, compute its world AABB.
3. Expand the AABB by the maximum automata read margin.
   - For Moore/von Neumann neighbor reads, `1 voxel` is enough.
   - If future automata reads farther, this margin must become configurable.
4. For every pair of sectors from different entities:
   - skip if their expanded world AABBs do not overlap.
   - otherwise add directed links both ways.
5. Sort links by owner sector, then by deterministic alien order.
6. Build `SectorKey -> range` metadata.

Naive sector-pair construction can be expensive:

```text
O(total sectors^2)
```

That is acceptable for an initial implementation if sector counts are modest. Later, a sweep, temporary uniform grid, or entity-pair pruning can accelerate graph construction while keeping the automata-facing graph unchanged.

Important: the graph is a candidate cache, not a proof of contact. False positives are allowed. Each query still transforms the sample point into the alien sector and verifies the voxel coordinate is inside `[0, 127]` and non-empty.

## Exposed API

Automata authors should not construct nested readers manually.

The intended public-facing types are:

```csharp
public struct AutomataReadContext
{
    [ReadOnly] public AlienSectorLinkGraph AlienGraph;

    public VoxelNeighborhoodReader CreateReader(VoxelisXWorld.BrickInfo brickInfo)
    {
        return VoxelNeighborhoodReader.Create(brickInfo, AlienGraph);
    }
}
```

```csharp
public struct VoxelNeighborhoodReader
{
    private int selfEntityId;
    private int3 selfSectorPos;
    private SectorNeighborhoodReaderHelper localReader;
    private AlienSectorLinkGraph alienGraph;

    public static VoxelNeighborhoodReader Create(
        VoxelisXWorld.BrickInfo brickInfo,
        AlienSectorLinkGraph alienGraph)
    {
        return new VoxelNeighborhoodReader
        {
            selfEntityId = brickInfo.EntityId,
            selfSectorPos = brickInfo.SectorPos,
            localReader = new SectorNeighborhoodReaderHelper(
                brickInfo.Sector,
                brickInfo.Neighbors),
            alienGraph = alienGraph
        };
    }

    public Block GetBlock(int3 sectorLocalVoxel)
    {
        Block local = localReader.GetBlock(sectorLocalVoxel);
        if (!local.isEmpty)
            return local;

        return GetAlienBlock(sectorLocalVoxel);
    }

    public Block GetLocalBlockOnly(int3 sectorLocalVoxel)
    {
        return localReader.GetBlock(sectorLocalVoxel);
    }

    public bool IsVoxelSpaceOccupied(int3 sectorLocalVoxel)
    {
        if (!localReader.GetBlock(sectorLocalVoxel).isEmpty)
            return true;

        return !GetAlienBlock(sectorLocalVoxel).isEmpty;
    }

    private Block GetAlienBlock(int3 sectorLocalVoxel)
    {
        // Implementation looks up SectorKey(selfEntityId, selfSectorPos),
        // walks that sector's AlienSectorLink range,
        // maps the sample point into each alien sector,
        // and returns the first non-empty alien block.
    }
}
```

`AutomataStageInputs` should expose a read context:

```csharp
public struct AutomataStageInputs
{
    public NativeList<VoxelEntityData> VoxelEntities;
    public NativeList<BrickInfo> BricksRequiredUpdate;
    public AutomataReadContext ReadContext;
}
```

Jobs should receive `AutomataReadContext`, not `AlienSectorLinkGraph` internals directly.

## Example Usage In Automata

This example mirrors the current `WireWorld` style, but it is self-contained for a worker who only sees `VoxelisX/`.

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxelis;
using Voxelis.Tick;

namespace Dynamics
{
    [BurstCompile]
    public struct WireWorldJob : IJobParallelForDefer
    {
        public NativeArray<VoxelisXWorld.BrickInfo> BrickInfos;

        [ReadOnly]
        public AutomataReadContext ReadContext;

        public void Execute(int index)
        {
            VoxelisXWorld.BrickInfo brickInfo = BrickInfos[index];
            SectorHandle workingSector = brickInfo.Sector;
            VoxelNeighborhoodReader reader = ReadContext.CreateReader(brickInfo);

            for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
            {
                for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                {
                    for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                    {
                        int3 blockPos = brickInfo.BrickOrigin + new int3(x, y, z);

                        byte solid = 0;
                        foreach (int3 d in NeighborhoodSettings.Directions)
                        {
                            solid += (byte)(reader.GetBlock(blockPos + d).id > 0 ? 1 : 0);
                        }

                        Block next = solid == 4
                            ? new Block(15, 15, 15, false)
                            : Block.Empty;

                        workingSector.SetBlock(blockPos.x, blockPos.y, blockPos.z, next);
                    }
                }
            }
        }
    }

    public class WireWorld : ITickHook<VoxelisXWorld.AutomataStageInputs>
    {
        public bool Execute(
            VoxelisXWorld.AutomataStageInputs inputs,
            JobHandle stageStart,
            JobHandle chained,
            out JobHandle handle)
        {
            var job = new WireWorldJob
            {
                BrickInfos = inputs.BricksRequiredUpdate.AsDeferredJobArray(),
                ReadContext = inputs.ReadContext,
            };

            handle = job.Schedule(inputs.BricksRequiredUpdate, 32, chained);
            return true;
        }
    }
}
```

The important usage contract is:

```text
var reader = ReadContext.CreateReader(brickInfo);
reader.GetBlock(sectorLocalVoxel);
reader.IsVoxelSpaceOccupied(sectorLocalVoxel);
```

Automata systems should not directly know about:

- `SectorNeighborHandles`
- `SectorNeighborhoodReaderHelper`
- entity transforms
- world-space transforms
- alien sector link ranges

Those are engine internals.

## Allocation Interface

Allocation checks should not be baked into `Sector.SetBlock`.

`Sector.SetBlock` is a storage primitive. It currently allocates bricks and records dirty state. Alien overlap policy belongs above it, in a write helper or write resolver.

V1 can expose:

```csharp
public bool TrySetBlock(
    ref SectorHandle sector,
    VoxelNeighborhoodReader reader,
    int3 sectorLocalVoxel,
    Block next)
{
    Block current = reader.GetLocalBlockOnly(sectorLocalVoxel);

    bool allocatesVoxel = current.isEmpty && !next.isEmpty;
    if (allocatesVoxel && reader.IsVoxelSpaceOccupied(sectorLocalVoxel))
        return false;

    sector.SetBlock(
        sectorLocalVoxel.x,
        sectorLocalVoxel.y,
        sectorLocalVoxel.z,
        next);

    return true;
}
```

The exact helper shape can vary, but the policy should be:

```text
if old local block is empty
and new block is non-empty
and alien query says occupied
then reject the write
```

Replacing or clearing an already-owned local block should not require alien occupancy approval.

Later, `IsVoxelSpaceOccupied` can switch from center-point occupancy to full voxel-box overlap without changing automata code.

## Integration Into VoxelisXWorld

The tick should build and pass read context before automata:

1. Sync `VoxelEntity` transforms into `VoxelEntityData`.
2. Build or refresh sector metadata for all entities.
3. Build `AlienSectorLinkGraph`.
4. Assign `automataTickBuf.ReadContext`.
5. Collect `BricksRequiredUpdate`.
6. Schedule automata hooks.
7. Complete automata jobs.
8. Apply sector snapshots.
9. Refresh physics/colliders.
10. Simulate physics.
11. Render and propagate dirty state according to the world tick policy.

The alien graph should be treated as a read-only tick view. It does not own sectors; it references existing sector handles.

## Pros And Cons Of Per-Sector Tracking

Pros:

- Matches the current update unit: `BrickInfo` already carries `EntityId`, `SectorPos`, `Sector`, and neighbor handles.
- Keeps automata code in sector-local coordinates.
- Reuses the same candidate list for many voxel reads in one sector.
- Handles sparse entities better than whole-entity AABBs.
- Gives deterministic ordering with a sorted sector link range.
- Is easier to debug visually: sector A has candidate links to these alien sectors.

Cons:

- Naive graph construction is `O(total sectors^2)`.
- Rotated sector world AABBs can be loose and produce false positives.
- Candidate links must be rebuilt or invalidated when entities move, rotate, load/unload sectors, or allocate new sectors.
- A fixed read margin must match automata behavior. If automata reads farther than the margin, alien candidates can be missed.
- Memory can grow quickly if many sectors overlap many alien sectors.

## V3: Faster Sector Graph Construction

If naive sector-pair graph building becomes too slow, keep the same automata-facing `AlienSectorLinkGraph` and only optimize how it is built.

Possible builders:

- Entity-pair pruning: skip sector comparisons when entity AABBs do not overlap.
- Sweep and prune over sector world AABB min/max on one axis.
- Temporary uniform grid: insert sector AABBs into world cells, then compare only sectors sharing a cell.

The uniform grid should be internal to graph construction. Automata should still read from per-sector link ranges.

## V4: Brick-Level Candidate Links

If sector candidates are too broad, especially for large rotated sparse sectors, introduce brick-level links.

Possible shape:

```csharp
public struct AlienBrickLink
{
    public int OtherEntityId;
    public int3 OtherSectorPos;
    public short OtherBrickIndex;
    public SectorHandle OtherSector;
    public float4x4 OtherBrickLocalFromThisSectorLocal;
}
```

This reduces false positives but increases build cost and memory pressure. It also depends on current non-empty-brick data being accurate before graph construction.

V4 should be introduced only after profiling shows sector-level false positives dominate query cost.

## Recommended Milestones

1. Add `AlienSectorLink`, `AlienSectorLinkGraph`, `AutomataReadContext`, and `VoxelNeighborhoodReader`.
2. Build a naive per-sector candidate graph before automata.
3. Replace direct `SectorNeighborhoodReaderHelper` construction in sample automata with `ReadContext.CreateReader(brickInfo)`.
4. Add center-point allocation rejection through a helper or write resolver.
5. Add tests for local-first behavior, alien fallback, deterministic link ordering, sector-local boundary reads, and allocation rejection.
6. Profile graph build cost and query cost before implementing V3 or V4.

## Open Implementation Notes

- A persistent `VoxelEntity` id is preferred. Tick index is acceptable only for experiments.
- `VoxelEntityData` currently has shallow-copy ownership hazards; the graph must be a read-only view and must not own native containers or sectors.
- If automata writes directly call `Sector.SetBlock`, allocation rejection must be done by convention through a helper. A command-buffered write resolver would be cleaner long-term.
- The sector graph should be rebuilt after entity transforms are synced and before automata reads.
- If automata creates new sectors during a tick, those sectors will not have alien links until the next graph rebuild unless the mutation pipeline explicitly patches the graph.
