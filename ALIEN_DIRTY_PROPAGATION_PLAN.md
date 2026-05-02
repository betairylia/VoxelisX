# Alien Dirty Propagation Plan

## Goal

Make automata dirty propagation aware of alien voxel entities without using the physics engine as the owner of this behavior.

Dirty propagation should:

- mark existing target bricks only;
- never create sectors in alien entities;
- propagate local block edits across entity boundaries;
- propagate motion-based alien influence when relative motion is present;
- remain Burst/HPC# friendly;
- preserve target-owned writes so parallel jobs do not race on `brickRequireUpdateFlags`.

For the current stage, the motion threshold is `0`, meaning any nonzero relative motion triggers motion-based dirty propagation. This intentionally allows a later threshold without adding pair-state now.

Motion propagation should use a dedicated configurable mask:

```csharp
[SerializeField] DirtyFlags alienMotionDirtyMask = DirtyFlags.Reserved0;
```

Block-edit propagation forwards the dirty brick's actual flags. Motion propagation sets only `alienMotionDirtyMask`, so renderer, physics, and unrelated systems are not woken up by alien motion unless they explicitly opt into that flag.

## Main Design

Use a target-owned final marking pass:

1. Build compact entity and sector records.
2. Build broadphase candidate records from source dirty/moving sectors.
3. Group candidates by target sector.
4. Run one Burst job item per active target sector.
5. Each target-sector job marks only that sector's `brickRequireUpdateFlags`.

Candidate generation may be source-driven, but final writes are target-owned.

## Data Model

Managed code builds native records for jobs:

```csharp
struct AlienDirtyEntityView
{
    int EntityId;
    RigidTransform LocalToWorld;
    float4x4 WorldToLocal;
    RigidTransform PreviousLocalToWorld;
    float4x4 PreviousWorldToLocal;
    float3 LinearVelocity;
    float3 AngularVelocity;
}

struct AlienDirtySectorRecord
{
    int EntityId;
    int3 SectorPos;
    SectorHandle Sector;
    Aabb CurrentWorldAabb;
    Aabb PreviousWorldAabb;
}

struct AlienDirtyCandidate
{
    int TargetSectorIndex;
    int SourceSectorIndex;
    short SourceBrickIdx; // -1 for sector-level candidates
    DirtyFlags Flags;
    AlienDirtyCandidateMode Mode; // BlockEdit or Motion
}
```

Previous transform is per entity/sector, not per entity pair. It is needed so motion invalidates places that lost an alien neighbor.

`AlienDirtyEntityView` is intentionally separate from the existing `AlienEntityView` used by `VoxelNeighborhoodReader`. The read path and dirty path have different data needs and should not be conflated.

Entity and sector membership must be frozen for the duration of the alien dirty propagation pass. Adds/removes should be applied before the pass begins or deferred until it completes.

## Spatial Query Abstraction

Use an abstraction for sector spatial queries. This keeps the current implementation general while leaving room for a future master static world at origin.

Conceptually:

```csharp
interface ISectorSpatialQuery
{
    void QueryAabb(Aabb worldAabb, NativeList<int> sectorIndices);
}
```

In Burst jobs this will not be a managed interface. It should be implemented as explicit query paths:

```csharp
QueryDynamicSpatialHash(dynamicIndex, aabb, results);
QueryGeneralStaticSpatialHash(staticIndex, aabb, results);
// Future:
QueryStaticIdentityWorldSectorMap(staticWorldSectorMap, aabb, results);
```

Current implementation can use spatial hashes for all static and dynamic instances. Later, if a rotation-free static world at origin exists, its sector map can act as the spatial index directly.

## Broadphase

Cell size should be configurable:

```csharp
[SerializeField] int alienSpatialCellSize = 64;
```

Constraints:

- power of two;
- at least `Sector.SIZE_IN_BLOCKS`;
- default `64`.

`64` is a better default than `128` for mixed static/dynamic scenes: a sector occupies about eight cells when axis-aligned, while dirty-brick queries avoid many false positives compared with full-sector cells.

Use:

```csharp
NativeParallelMultiHashMap<int3, int> sectorSpatialHash;
```

Where value is an index into `NativeArray<AlienDirtySectorRecord>`.

For block-edit queries, index current sector bounds:

```csharp
foreach (cell in CellsOverlappedBy(sector.CurrentWorldAabb))
    sectorSpatialHash.Add(cell, sectorIndex);
```

For motion queries, use a separate motion spatial hash or rebuild the hash with swept sector bounds:

```csharp
swept = Union(sector.PreviousWorldAabb, sector.CurrentWorldAabb).Inflated(halo);

foreach (cell in CellsOverlappedBy(swept))
    motionSectorSpatialHash.Add(cell, sectorIndex);
```

This catches both gained and lost alien influence, including the case where two moving entities overlapped only in their previous poses.

## Dirty Brick Enumeration

The engine currently has:

- `SectorNonEmptyBlockEnumerator`
- `SectorRequiredUpdateBrickEnumerator`

It does not currently have a dirty-brick enumerable. Add a Burst-friendly `SectorDirtyBrickEnumerator` or equivalent helper:

```csharp
struct SectorDirtyBrickEnumerator
{
    Sector Sector;
    DirtyFlags Mask;

    bool MoveNext()
    {
        if ((Sector.sectorDirtyFlags & (ushort)Mask) == 0)
            return false;

        // V1: scan 4096 brick slots.
        // Yield bricks where (brickDirtyFlags[i] & mask) != 0.
    }
}
```

A 4096-brick scan is acceptable for V1 because dirty sectors are expected to be sparse and sector-level dirty flags provide a cheap skip.

For dense dirty sectors, add a sector-level fast path. If a source sector has many dirty bricks, emitting one sector-level candidate is cheaper than emitting hundreds or thousands of per-brick candidates that mostly collapse during deduplication.

```csharp
[SerializeField] int alienDirtySectorLevelThreshold = 256;

if (dirtyBrickCount > alienDirtySectorLevelThreshold)
    emit sector-level BlockEdit candidate with SourceBrickIdx = -1;
else
    emit per-dirty-brick candidates;
```

`SourceBrickIdx == -1` is valid for `Mode == Motion` and for sector-level `Mode == BlockEdit`. Callers must check `Mode` and sentinel meaning together.

## Block-Edit Propagation

Block edits are precise at dirty-brick granularity.

### Managed Prep

Collect source sector records with `sectorDirtyFlags != 0`.

### Burst Candidate Build

Parallelized by dirty source sector.

```csharp
[BurstCompile]
struct BuildBlockEditCandidatesJob : IJobParallelFor
{
    NativeArray<AlienDirtySectorRecord> SourceSectors;
    NativeArray<AlienDirtySectorRecord> AllSectors;
    SectorSpatialHash SpatialHash;
    NativeStream.Writer Candidates;

    void Execute(int sourceSectorListIndex)
    {
        source = SourceSectors[sourceSectorListIndex];

        dirtyBrickCount = CountDirtyBricks(source.Sector, DirtyFlags.All);
        if (dirtyBrickCount > DirtySectorLevelThreshold)
        {
            sourceWorldAabb = source.CurrentWorldAabb.Inflated(halo);

            foreach (targetSectorIndex in QuerySpatialHash(SpatialHash, sourceWorldAabb))
            {
                target = AllSectors[targetSectorIndex];
                if (target.EntityId == source.EntityId)
                    continue;
                if (!AabbOverlap(sourceWorldAabb, target.CurrentWorldAabb))
                    continue;

                Candidates.Write(new AlienDirtyCandidate
                {
                    TargetSectorIndex = targetSectorIndex,
                    SourceSectorIndex = source.Index,
                    SourceBrickIdx = -1,
                    Flags = source.Sector.GetAggregatedDirtyFlags(),
                    Mode = BlockEdit
                });
            }

            return;
        }

        foreach (dirtyBrick in DirtyBricks(source.Sector, DirtyFlags.All))
        {
            sourceWorldAabb = DirtyBrickWorldAabb(source, dirtyBrick).Inflated(halo);

            foreach (targetSectorIndex in QuerySpatialHash(SpatialHash, sourceWorldAabb))
            {
                target = AllSectors[targetSectorIndex];
                if (target.EntityId == source.EntityId)
                    continue;
                if (!AabbOverlap(sourceWorldAabb, target.CurrentWorldAabb))
                    continue;

                Candidates.Write(new AlienDirtyCandidate
                {
                    TargetSectorIndex = targetSectorIndex,
                    SourceSectorIndex = source.Index,
                    SourceBrickIdx = dirtyBrick.BrickIdx,
                    Flags = dirtyBrick.Flags,
                    Mode = BlockEdit
                });
            }
        }
    }
}
```

This pass writes append-only candidate data. It does not mutate sectors.

## Motion Propagation

Motion propagation is driven by moving sectors and emits bidirectional candidates.

For each moving sector `M`, query all overlapped sectors `O` using `Union(previous, current) + halo`. Emit both:

- `M -> O`
- `O -> M`

This catches both:

- moving source affects static/dynamic target;
- moving target changes relative to static/dynamic source.

### Managed Prep

Collect moving sector records from entities with nonzero linear or angular velocity.

For current stage:

```csharp
alienMotionDirtyThreshold = 0f;
```

### Burst Candidate Build

Parallelized by moving sector.

```csharp
[BurstCompile]
struct BuildMotionCandidatesJob : IJobParallelFor
{
    NativeArray<AlienDirtySectorRecord> MovingSectors;
    NativeArray<AlienDirtySectorRecord> AllSectors;
    NativeArray<AlienDirtyEntityView> Entities;
    SectorSpatialHash MotionSpatialHash;
    float MotionThreshold;
    NativeStream.Writer Candidates;

    void Execute(int movingSectorListIndex)
    {
        moving = MovingSectors[movingSectorListIndex];
        swept = Union(moving.PreviousWorldAabb, moving.CurrentWorldAabb).Inflated(halo);

        foreach (otherSectorIndex in QuerySpatialHash(MotionSpatialHash, swept))
        {
            other = AllSectors[otherSectorIndex];
            if (other.EntityId == moving.EntityId)
                continue;

            if (!AabbOverlap(swept, other.CurrentWorldAabb) &&
                !AabbOverlap(swept, other.PreviousWorldAabb))
                continue;

            if (!RelativeMotionExceedsThreshold(
                    Entities[moving.EntityId],
                    Entities[other.EntityId],
                    MotionThreshold))
                continue;

            // moving affects other
            Candidates.Write(new AlienDirtyCandidate
            {
                TargetSectorIndex = otherSectorIndex,
                SourceSectorIndex = moving.Index,
                SourceBrickIdx = -1,
                Flags = AlienMotionDirtyMask,
                Mode = Motion
            });

            // other affects moving
            Candidates.Write(new AlienDirtyCandidate
            {
                TargetSectorIndex = moving.Index,
                SourceSectorIndex = otherSectorIndex,
                SourceBrickIdx = -1,
                Flags = AlienMotionDirtyMask,
                Mode = Motion
            });
        }
    }
}
```

Threshold check:

```csharp
bool RelativeMotionExceedsThreshold(a, b, threshold)
{
    linear = length(a.LinearVelocity - b.LinearVelocity) * dt;
    angular = length(a.AngularVelocity - b.AngularVelocity) * influenceRadius * dt;
    return linear + angular > threshold;
}
```

With threshold `0`, any nonzero relative motion triggers.

If both sectors in a pair are moving, the bidirectional moving-sector build can emit duplicate candidates: processing `A` emits `A -> B` and `B -> A`, then processing `B` can emit the same two candidates again. V1 accepts this known cost and relies on deduplication. A future optimization can use pair ordering, such as only allowing the lower `(EntityId, SectorPos)` side to emit both directions.

## Candidate Grouping

After candidate build, group by target sector.

V1 should keep grouping native. Avoid reading candidates into managed collections.

```csharp
candidates = NativeStreamToNativeArray();
NativeSort(candidates, by TargetSectorIndex, SourceSectorIndex, SourceBrickIdx, Mode);
DeduplicateSortedCandidatesJob.Schedule();
BuildTargetRangesJob.Schedule();
```

The candidate-build jobs must complete before sorting, but this is a native job dependency/sync point rather than a managed allocation-heavy grouping step.

Deduplicate at least by:

```csharp
(TargetSectorIndex, SourceSectorIndex, SourceBrickIdx, Mode)
```

Deduplication reduces repeated marking when multiple broadphase cells return the same pair.

## Target-Owned Marking

Parallelized by active target sector.

This is the only phase that writes `brickRequireUpdateFlags`.

```csharp
[BurstCompile]
struct MarkAlienRequireUpdatesJob : IJobParallelFor
{
    NativeArray<int> ActiveTargetSectors;
    NativeArray<Range> CandidateRanges;
    NativeArray<AlienDirtyCandidate> Candidates;
    NativeArray<AlienDirtySectorRecord> Sectors;
    NativeArray<AlienDirtyEntityView> Entities;

    void Execute(int activeTargetIndex)
    {
        targetSectorIndex = ActiveTargetSectors[activeTargetIndex];
        target = Sectors[targetSectorIndex];

        foreach (candidate in CandidatesForTarget(targetSectorIndex))
        {
            source = Sectors[candidate.SourceSectorIndex];

            if (candidate.Mode == BlockEdit)
            {
                obb = candidate.SourceBrickIdx >= 0
                    ? SourceDirtyBrickObbPlusHalo(source, candidate.SourceBrickIdx)
                    : SourceSectorObbPlusHalo(source);
                range = TransformObbToTargetBrickRange(obb, target);
                MarkTargetBrickRange(target.Sector, range, candidate.Flags);
            }
            else // Motion
            {
                obb = SourceSectorObbPlusHalo(source);
                range = TransformObbToTargetBrickRange(obb, target);
                MarkTargetBrickRange(target.Sector, range, candidate.Flags);
            }
        }
    }
}
```

Because each job item owns one target sector, no atomics are needed for `brickRequireUpdateFlags`.

## Rigid Transform Brick Range Simplification

Voxel size is constant. Entities may translate and rotate but do not scale.

For dirty-brick block-edit propagation:

```csharp
sourceLocalCenter = sourceSectorOrigin + dirtyBrickOrigin + 4;
sourceHalfExtent = 4 + halo;

targetLocalCenter =
    targetWorldToLocal * sourceLocalToWorld * sourceLocalCenter;

targetLocalHalfExtent =
    abs(relativeRotationMatrix) * sourceHalfExtent;

targetMin = targetLocalCenter - targetLocalHalfExtent;
targetMax = targetLocalCenter + targetLocalHalfExtent;

targetBrickMin = floor(targetMin / 8);
targetBrickMax = floor((targetMax - epsilon) / 8);
```

Intersect this range with the current target sector's brick bounds, then mark the resulting bricks.

This avoids transforming all eight corners while remaining conservative for rotations.

The halo is a correctness parameter, not just tuning. Define:

```csharp
int alienDirtyHaloVoxels = automataReadRadius;
```

For the current Moore radius-1 automata, `alienDirtyHaloVoxels = 1`. If automata rules later read farther than one voxel, this value must increase with that read radius.

## Tick Placement

Run after local dirty propagation has populated local `RequireUpdate` flags and before dirty flags are cleared.

High-level order:

```text
clear previous require-update flags
local dirty propagation
alien block-edit dirty propagation
alien motion dirty propagation
automata consumes require-update flags next tick
clear dirty flags after all consumers that need them have run
```

Exact placement should respect existing renderer/physics consumers of dirty records. Dirty/update cleanup should remain world-owned.

## Initial Implementation Stages

1. Add data records and previous transform/velocity capture.
2. Add dirty brick enumerator.
3. Add configurable general sector spatial hash for current static/dynamic instances, default cell size `64`.
4. Add block-edit candidate build, including dense-sector fallback.
5. Add native sorting, deduplication, and target range building.
6. Add target-owned marking.
7. Add motion candidate build with bidirectional `M <-> O` emission, `alienMotionDirtyMask`, and threshold `0`.
8. Add tests:
   - dirty brick in entity A marks overlapping target brick in entity B;
   - dense dirty source sector emits/handles sector-level candidate;
   - no self propagation;
   - no sector creation;
   - moving entity marks gained overlap;
   - moving entity marks lost overlap through previous/current swept AABB;
   - bidirectional motion marks both `M -> O` and `O -> M`;
   - same-velocity pair does not mark when relative motion is zero.

## Notes

The physics voxel collision path is useful as a reference for source-to-target coordinate transforms, but this system should live in VoxelisX core/simulation. Dirty propagation must not depend on physics bodies, contact generation, or physics scheduling.
