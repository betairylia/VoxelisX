# CLAUDE.md - Development Guide for AI Assistants

This document provides context and guidelines for AI assistants (like Claude) working on the VoxelisX project.

## Project Identity

**VoxelisX** is a high-performance voxel engine for Unity 6 that supports both hardware ray tracing and mesh-based rendering. The project emphasizes performance through Unity's Data-Oriented Technology Stack (DOTS), Burst compilation, and efficient memory management.

## Critical Architecture Principles

### 1. Performance-First Design

Every system is optimized for performance:

- **Use DOTS patterns**: Native collections, job system, Burst compilation
- **Minimize allocations**: Reuse buffers, use `NativeArray`, avoid managed types in hot paths
- **Unsafe code is acceptable**: Many systems use pointers for performance (`Block*`, `Brick*`)
- **Burst-compile everything**: All jobs should have `[BurstCompile]` attribute

### 2. Sparse Storage Philosophy

The voxel engine uses hierarchical sparse storage:

```
VoxelEntity → Sectors (128³) → Bricks (8³) → Blocks (1³)
```

**Key insight**: Empty space is free. Only non-empty bricks allocate memory (~4KB per brick).

### 3. Dirty Flag System

Changes propagate through a sophisticated dirty flag system:

1. Block modification marks brick dirty
2. `DirtyPropagationJob` propagates to neighbors
3. Dependent systems (renderer, physics) update only dirty regions

**Always** respect the dirty flag system when modifying voxels.

## Code Organization

### Directory Structure

```
Runtime/
├── Core/              # Pure data structures, no Unity dependencies where possible
├── Rendering/         # All rendering code (ray tracing + mesh)
├── Simulation/        # Physics and world management
└── [Utilities]        # VoxelRayCast, VoxFileLoader, etc.
```

### Assembly Boundaries

- `voxelis.core` - Core data structures (referenced by main assembly)
- `voxelis.voxelisx` - Main package assembly (depends on core)
- `voxelis.voxelisx.tests` - Test assembly

**Note**: Core should remain minimal and fast. Rendering/simulation logic stays in main assembly.

## Key Data Structures

### Block (Runtime/Core/Block.cs)

```csharp
public struct Block
{
    uint data; // RGB555 + emission bit + 16-bit metadata
}
```

**Important**:
- Use `Block.FromRGB()` for color creation
- Use `Block.Empty` for air/empty voxels
- Don't create blocks manually with `new Block()`
- Blocks are compared by value (equality checks are cheap)

### Sector (Runtime/Core/Sector.cs)

```csharp
public class Sector
{
    const int BrickSize = 8;         // 8³ blocks per brick
    const int NumBricksPerAxis = 16; // 16³ bricks per sector
    const int SectorSize = 128;      // 128³ blocks total
}
```

**Key methods**:
- `GetBlock(int3 pos)` - Thread-safe read
- `SetBlock(int3 pos, Block block)` - **Not thread-safe**, must lock
- `GetBrickPointer(int brickIndex)` - Unsafe access for jobs
- `GetBrickMetadata(int brickIndex)` - Dirty flags, lock state

**Threading rules**:
- Multiple readers OK
- Single writer requires lock
- Snapshots (`SectorSnapshot`) for safe parallel reads during writes

### VoxelEntity (Runtime/Core/VoxelEntity.cs)

Container for multiple sectors with neighbor tracking.

**Important patterns**:
```csharp
// Adding sectors
entity.AddEmptySectorAt(int3 sectorPos);
entity.CopyAndAddSectorAt(Sector source, int3 sectorPos);

// Block operations
entity.GetBlock(int3 worldPos);
entity.SetBlock(int3 worldPos, Block block);

// Access sectors
entity.GetSector(int3 sectorPos);
entity.GetSectorHandleAt(int3 sectorPos); // For neighbor access
```

### SectorHandle (Runtime/Core/SectorHandle.cs)

Lightweight reference to a sector with neighbor pointers.

**Usage**: When jobs need to access neighboring sectors, use `SectorHandle.GetNeighborBrickReadonly()` for efficient cross-sector reads.

## Rendering Systems

### Ray Tracing Path (Runtime/Rendering/VoxelisXRenderer.cs)

**Architecture**:
1. `VoxelisXRenderer` - Main coordinator
2. `SectorRenderer` - Per-sector RTAS management
3. GPU buffers: AABB data, brick material data
4. Shader: `VoxelisXRTShader.raytrace`

**Update flow**:
```
Dirty flag set → Tick() → UpdateSectorRenderer()
→ Schedule GPU buffer updates → Update RTAS → Render
```

**Key considerations**:
- Ray tracing requires DXR-capable GPU
- Materials stored per-brick (not per-block) for memory efficiency
- RTAS updates are expensive, dirty flags minimize them

### Mesh Path (Runtime/Rendering/Mesh/VoxelMeshRenderer.cs)

**Architecture**:
1. `VoxelMeshRenderer` - Coordinator
2. `SectorMeshRenderer` - Per-sector mesh management
3. Greedy meshing jobs: `Voxelis.VoxelisX.Mesh.Jobs.*`

**Update flow**:
```
Dirty flag set → ScheduleMeshingJobs() → CompleteMeshingJobs()
→ Apply mesh to MeshFilter
```

**Key considerations**:
- Greedy algorithm merges adjacent same-color faces
- Two-phase update (schedule/complete) for async processing
- Fallback for systems without ray tracing

## Physics Integration

### VoxelisXPhysicsWorld (Runtime/Simulation/VoxelisXPhysicsWorld.cs)

Uses Unity Physics (DOTS) for simulation.

**Key settings**:
- Gravity: Configurable `float3`
- Solver iterations: Balance accuracy vs performance
- Substeps: Higher = more stable but slower

### VoxelBody (Runtime/Simulation/VoxelBody.cs)

Physics wrapper for voxel entities.

**Pattern**: One `VoxelBody` = One physics-enabled entity
- Automatically calculates mass/inertia
- Collision detection with other voxel bodies
- Requires `VoxelisXPhysicsWorld` in scene

## Tick System (Runtime/Core/Tick/)

VoxelisX uses a custom hierarchical tick system:

```csharp
public class VoxelisXWorld : VoxelisXCoreWorld
{
    protected TickStage<VoxelisXWorld> physicsStage;
    protected TickStage<VoxelisXWorld> automataStage;
    protected TickStage<VoxelisXWorld> renderStage;
}
```

**Stages execute in order**: Physics → Automata → Rendering

### Adding Custom Logic

```csharp
// Create a hook
public class MyCustomHook : ITickHook<VoxelisXWorld>
{
    public void Execute(VoxelisXWorld world)
    {
        // Your logic here
    }

    public string GetDebugName() => "MyCustomHook";
}

// Register in Awake()
automataStage.AddHook(new MyCustomHook());
```

**Burst jobs**: Use `BurstJobHook<TWorld, TJob>` for parallel processing.

## Common Patterns

### Pattern 1: Modifying Voxels Safely

```csharp
// CORRECT
var sector = entity.GetSector(sectorPos);
sector.Lock();
try
{
    sector.SetBlock(localPos, newBlock);
    sector.MarkBrickDirtyByLocalPosition(localPos);
}
finally
{
    sector.Unlock();
}

// INCORRECT - Race condition!
entity.SetBlock(worldPos, newBlock); // No lock!
```

**Note**: `VoxelEntity.SetBlock()` handles locking internally, but direct sector access requires manual locking.

### Pattern 2: Creating Burst-Compiled Jobs

```csharp
[BurstCompile(CompileSynchronously = true)]
public struct MyVoxelJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<Block> input;
    [WriteOnly] public NativeArray<Block> output;

    public void Execute(int index)
    {
        // Process voxels
        output[index] = input[index];
    }
}

// Schedule
var job = new MyVoxelJob { input = inArray, output = outArray };
JobHandle handle = job.Schedule(count, 64);
handle.Complete();
```

**Key points**:
- Use `[ReadOnly]` and `[WriteOnly]` for safety checker
- Batch size (64) balances overhead vs parallelism
- Always complete jobs before accessing results

### Pattern 3: Neighbor Access in Jobs

```csharp
[BurstCompile]
public struct NeighborAwareJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<SectorHandle> sectors;

    public void Execute(int index)
    {
        SectorHandle handle = sectors[index];

        // Access neighbor
        Block* neighborBrick = handle.GetNeighborBrickReadonly(
            NeighborDirection.PosX, localBrickIndex
        );

        if (neighborBrick != null)
        {
            Block block = neighborBrick[0]; // Read first block
        }
    }
}
```

**Neighbor system**: Each sector tracks 6 cardinal neighbors for efficient cross-sector access.

### Pattern 4: Implementing Sector Streaming

```csharp
public class TerrainLoader : InfiniteLoader
{
    protected override void LoadSector(int3 sectorPos)
    {
        // Generate terrain
        Sector sector = new Sector();

        for (int x = 0; x < 128; x++)
        for (int z = 0; z < 128; z++)
        {
            int height = GetTerrainHeight(sectorPos.xz * 128 + new int2(x, z));
            for (int y = 0; y < height; y++)
            {
                sector.SetBlock(new int3(x, y, z), GetBlockForHeight(y));
            }
        }

        Entity.CopyAndAddSectorAt(sector, sectorPos);
    }

    protected override void UnloadSector(int3 sectorPos)
    {
        // Optional: Save to disk
        SaveSectorToDisk(Entity.GetSector(sectorPos), sectorPos);
        Entity.RemoveSector(sectorPos);
    }
}
```

## Testing Guidelines

### Unit Test Structure

Tests are in `Tests/Editor/` and use Unity Test Framework.

**Example**:
```csharp
[TestFixture]
public class MyFeatureTests
{
    [Test]
    public void TestBlockPacking()
    {
        Block block = Block.FromRGB(255, 128, 64);
        Assert.AreEqual(255, block.R);
        Assert.AreEqual(128, block.G);
        Assert.AreEqual(64, block.B);
    }
}
```

**Run tests**: Unity → Window → General → Test Runner

### Test Coverage Priority

1. **Core data structures** - Block, Sector, VoxelEntity
2. **Dirty flag propagation** - Ensure changes propagate correctly
3. **Job correctness** - Verify Burst jobs produce expected results
4. **Edge cases** - Sector boundaries, empty bricks, null neighbors

## Performance Profiling

### Unity Profiler Markers

Key markers to watch:
- `VoxelisXRenderer.Tick` - Ray tracing updates
- `VoxelMeshRenderer.Tick` - Mesh generation
- `DirtyPropagationJob` - Dirty flag propagation
- `VoxelisXPhysicsWorld.Tick` - Physics simulation

### Common Bottlenecks

1. **Too many RTAS updates** - Reduce dirty flag propagation
2. **Mesh generation stalls** - Increase frame budget or reduce load radius
3. **Sector loading spikes** - Implement async loading
4. **GC allocations** - Verify native collections usage

## Common Pitfalls

### 1. Forgetting to Mark Dirty

```csharp
// WRONG - Changes won't render!
sector.SetBlock(pos, block);

// CORRECT
sector.SetBlock(pos, block);
sector.MarkBrickDirtyByLocalPosition(pos);
```

### 2. Unsafe Code Without Null Checks

```csharp
// WRONG - Can crash if brick is empty!
Block* brick = sector.GetBrickPointer(index);
Block block = brick[0];

// CORRECT
Block* brick = sector.GetBrickPointer(index);
if (brick != null)
{
    Block block = brick[0];
}
```

### 3. Modifying Voxels During Job Execution

```csharp
// WRONG - Race condition!
JobHandle handle = job.Schedule(...);
entity.SetBlock(pos, block); // Job still running!
handle.Complete();

// CORRECT
JobHandle handle = job.Schedule(...);
handle.Complete();
entity.SetBlock(pos, block); // Safe after completion
```

### 4. Missing Burst Compilation

```csharp
// WRONG - Slow!
public struct MyJob : IJob { ... }

// CORRECT
[BurstCompile]
public struct MyJob : IJob { ... }
```

### 5. Coordinate System Confusion

- **Unity**: Y-up, left-handed
- **MagicaVoxel**: Z-up, right-handed
- **VoxFileLoader**: Automatically converts (Y↔Z swap)

**When implementing custom importers**: Remember to handle coordinate conversion.

## Coding Conventions

### Naming
- **Classes**: PascalCase (`VoxelEntity`)
- **Methods**: PascalCase (`GetBlock`)
- **Fields**: camelCase (`sectorDict`)
- **Constants**: PascalCase (`BrickSize`)
- **Jobs**: Suffix with `Job` (`DirtyPropagationJob`)

### File Organization
- One primary class per file
- Nested types OK if closely related
- Jobs can share file with main class if small

### Comments
- XML docs for public APIs
- `// TODO:` for planned improvements
- `// FIXME:` for known issues
- Explain "why", not "what"

### Unsafe Code
- Always document pointer usage
- Null check pointers from `GetBrickPointer()`
- Prefer safe APIs when performance difference is negligible

## Recent Changes (Git History Context)

Recent commits show focus on:
1. **Dirty flag system refactoring** - More efficient update tracking
2. **Renderer optimizations** - Removed redundant update records
3. **Sector write locking** - Thread safety improvements
4. **Ticking order fixes** - Proper stage execution sequence

**When making changes**: Consider impact on dirty flag propagation and thread safety.

## Future Development Areas

Based on TODOs and architecture:

1. **Async sector loading** - `InfiniteLoader` has parallel loading stubs
2. **Advanced lighting** - Ray tracing shader currently simple
3. **Multi-material support** - Currently one material per brick
4. **Networking** - No multiplayer support yet
5. **LOD system** - All sectors render at full resolution

## Debugging Tips

### Enable Debug GUI
`VoxelisXRenderer` and `VoxelMeshRenderer` have `OnGUI()` methods showing:
- Active sector count
- Dirty sector count
- Buffer memory usage

### Visualize Dirty Flags
Add to `SectorRenderer`:
```csharp
void OnDrawGizmos()
{
    if (IsDirty)
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(sectorWorldPos, Vector3.one * 128);
    }
}
```

### Log Excessive Updates
```csharp
if (dirtySectors.Count > 100)
{
    Debug.LogWarning($"Too many dirty sectors: {dirtySectors.Count}");
}
```

## Questions to Ask When Making Changes

1. **Does this need to be Burst-compiled?** (If it's in a hot path, yes)
2. **Are we respecting locks?** (Sector writes must be locked)
3. **Are we marking dirty flags?** (Changes must propagate)
4. **Is this allocation necessary?** (Prefer native collections)
5. **Does this work with empty bricks?** (Sparse storage means null pointers)
6. **Are we testing edge cases?** (Sector boundaries, null neighbors)

## Getting Help

When stuck:
1. Check unit tests for usage examples
2. Profile with Unity Profiler
3. Enable debug GUI for renderer stats
4. Review git history for similar changes
5. Consult this document's patterns section

## Summary for AI Assistants

When working on VoxelisX:
- **Think performance**: DOTS, Burst, native collections
- **Respect threading**: Lock sector writes, read-only where possible
- **Trust dirty flags**: Don't force-update everything
- **Test sparse cases**: Empty bricks are null pointers
- **Check sector boundaries**: Neighbor access can fail
- **Follow conventions**: Match existing code style
- **Add tests**: Especially for core systems

This is a production-quality engine. Changes should maintain the high performance and reliability standards established in the codebase.
