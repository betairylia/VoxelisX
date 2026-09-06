# Caelix: ECS vs Non-ECS Architecture Decision

> Historical decision report, archived on 2026-09-06. The original date and body follow.
> Use [Server and client usage](../../manual/server-and-client.md) for the current integration model.

**Date:** 2025-11-02
**Decision:** Abandon Unity ECS implementation, return to MonoBehaviour + Jobs + Burst architecture
**Primary Reason:** ECS complexity doesn't provide sufficient benefits for voxel engine's specific requirements

---

## Executive Summary

After extensive research and profiling, the decision has been made to **discontinue the Caelix-ECS implementation** and return to the proven MonoBehaviour-based architecture with Unity's Job System and Burst compilation.

### Key Findings

1. **Parallelization Issues**: ECS job system forces chunk-level batching, preventing true per-entity parallelization without significant boilerplate code
2. **Networking Complexity**: Unity NetCode for Entities is not well-suited for bulk voxel data synchronization
3. **Lost Features**: ECS sacrifices editor integration (gizmos, inspector, debugging) that are valuable for development
4. **Existing Solution**: Non-ECS implementation already achieves excellent performance with clean parallelization
5. **Limited Benefits**: Core advantages of ECS (thousands of entities, Unity.Physics, NetCode) don't apply to this use case

### Performance Comparison

| Metric | Non-ECS (Existing) | ECS (Attempted) |
|--------|-------------------|-----------------|
| **Sector Count** | 216 sectors (6×6×6) | 8 sectors |
| **Parallelization** | All 26 worker threads utilized | Single-threaded (Worker 0 only) |
| **Time** | Fast, distributed workload | 1200ms for 8 sectors (Burst on) |
| **Projected 216 sectors** | ~Same as current | ~9.3 seconds |
| **Code Complexity** | Low (straightforward Jobs) | High (archetypes, safety system) |
| **Editor Integration** | Full (gizmos, inspector) | Limited |

---

## Part 1: Parallelization Investigation

### Background

The `TestWorldGenerationSystem` was experiencing single-threaded execution despite using `IJobEntity` with `ScheduleParallel()`, resulting in poor performance (1200ms for 8 sectors with Burst, 79+ seconds without Burst).

### Initial Hypothesis (Incorrect)

Initially suspected that managed component `SectorGPUData` (a class containing GraphicsBuffers) in the entity archetype was forcing main-thread execution.

**Investigation Result**: Removing `SectorGPUData` from the archetype did not resolve the single-threading issue.

### Root Cause Analysis

**Actual Problem**: `IJobEntity.ScheduleParallel()` performs **chunk-level batching**, not per-entity batching.

#### How IJobEntity Scheduling Works

1. Entities with the same archetype are grouped into "chunks" (16KB blocks of memory)
2. `ScheduleParallel()` distributes work **per chunk**, not per entity
3. If all entities fit in one chunk (common with small component sizes), only one worker thread processes them
4. Each sector entity is relatively small (just pointers to native collections), so 8 sectors easily fit in a single chunk

#### Evidence from Unity Profiler

```
Timeline Analysis:
- Worker 0: Processing all 8 sectors sequentially
- Workers 1-26: Completely idle
- Job execution time: 1200ms (with Burst enabled)
```

This confirms that all work was batched into a single job instance running on one thread.

### Attempted Solutions

#### Option 1: Split Archetypes
- Create separate archetype without rendering components for generation
- Add rendering components after generation completes
- **Result**: Did not solve the batching issue (sectors still in same chunk type)

#### Option 2: IJobParallelFor with Per-Entity Indexing

**Concept**:
```csharp
[BurstCompile]
public struct TestWorldGenerationJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<Entity> entities;
    [NativeDisableParallelForRestriction] public ComponentLookup<SectorData> sectorDataLookup;
    [ReadOnly] public ComponentLookup<SectorTransform> sectorTransformLookup;

    public void Execute(int index)
    {
        var entity = entities[index];
        var sectorData = sectorDataLookup[entity];
        var sectorTransform = sectorTransformLookup[entity];
        // Generation logic...
    }
}

// In system:
var query = SystemAPI.QueryBuilder().WithAll<TestWorldSector>().Build();
var entities = query.ToEntityArray(Allocator.TempJob);

new TestWorldGenerationJob
{
    entities = entities,
    sectorDataLookup = GetComponentLookup<SectorData>(),
    sectorTransformLookup = GetComponentLookup<SectorTransform>(true)
}.Schedule(entities.Length, 1, state.Dependency);
```

**Issues**:
- Significant boilerplate code (ComponentLookup setup, query management)
- Requires `[NativeDisableParallelForRestriction]` attribute (unsafe, bypasses safety system)
- Defeats the original goal of keeping syntax simple
- Would need Roslyn source generation to reduce boilerplate

#### Option 3: Return to Non-ECS Architecture

**Proven working implementation**: Existing non-ECS Caelix already demonstrates:
- True per-sector parallelization
- All CPU cores utilized efficiently
- Clean, straightforward code
- Full editor integration

**Conclusion**: This option was selected as the best path forward.

---

## Part 2: Unity NetCode Analysis for Multiplayer

### Research Context

The primary motivation for using ECS was the assumption that Unity NetCode for Entities would be beneficial for multiplayer implementation. The game requirements are:

- Minecraft-style multiplayer
- Multiple moving VoxelObjects (ships, vehicles)
- One infinite static world VoxelObject
- Voxel cellular automata (water flow, falling sand, physics)
- Player block placement/breaking
- Sector size: 128×128×128 blocks (2,097,152 voxels per sector)

### NetCode Architecture Analysis

#### Ghost System Overview

**How NetCode Works**:
- Server sends snapshots of "Ghost" entities every network tick (~60Hz)
- Delta compression sends only changes from previous baseline
- Huffman encoding optimizes small deltas (e.g., position changes)
- Chunk-based importance scaling and relevancy filtering

**Component Size Limitations** (Critical):
- IComponentData with fixed-size lists: Maximum 64 elements
- Snapshot packet: Clamped to MTU limit (~1400 bytes) per tick
- RPCs: Limited to single packet size

**What This Means for Voxels**:
- Single sector = 2MB of data (if fully allocated)
- Average sparse sector = 100KB - 500KB
- **Completely impossible to sync via Ghost components**

#### Delta Compression Characteristics

**NetCode's delta compression is optimized for**:
- Small incremental changes (object moving by +0.5 units)
- Continuous state (float positions, rotations)
- Quantization of floating-point values

**Voxel data patterns**:
- Discrete changes (block placed/removed = entire 4-byte block changed)
- Sparse allocation (entire bricks added/removed)
- Not continuous state

**Conclusion**: Delta compression provides minimal benefit for voxel data.

### Voxel-Specific Networking Requirements

#### Minecraft's Networking Approach

```
Key Techniques:
1. Paletted containers for efficient storage
2. Multi-block change packets
3. Chunk-based synchronization
4. Only send deltas after initial load
5. Run-length encoding for homogeneous regions
```

#### Bandwidth Analysis for Caelix

**Worst Case** (sending all voxel data via NetCode):
- 1 full sector = 2MB
- At 60 ticks/sec = 120 MB/s per sector (impossible!)

**Realistic Case** (custom protocol with compression):
- 1 brick changed = 2KB raw
- RLE compression (5x typical) = 400 bytes
- 5 bricks/second = 2 KB/s per player
- 20 players = 40 KB/s total (manageable!)

#### What NetCode IS Good For

✅ **Well-Suited Use Cases**:
- Player entities (position, rotation, health, inventory)
- Moving VoxelObjects **transforms only** (not voxel data)
- Game state (time, weather, day/night cycle)
- Client prediction for player movement
- Connection management

❌ **Poorly-Suited Use Cases**:
- Bulk voxel data transfer (initial sector load)
- High-frequency block updates
- Cellular automata state synchronization
- Voxel physics data
- Large data blobs in general

### Recommended Hybrid Approach (If Staying with ECS)

```
Unity NetCode for Entities
├── Player Ghosts (transform, state)
├── VoxelObject transforms (ships/vehicles)
└── Game state replication

Custom Networking (Unity Transport)
├── Sector data streaming
├── Multi-block update packets
├── Cellular automata inputs
└── Voxel physics events
```

**Complexity Assessment**:
- Difficulty: 6/10 (moderate)
- Performance: 9/10 (optimized)
- Bandwidth: 8/10 (efficient)

**Implementation Burden**:
- Set up two separate networking systems
- Coordinate between NetCode and custom protocols
- Debug interactions between systems
- Maintain both codebases

### Custom Networking (Non-ECS Alternative)

**Using Unity Transport directly OR established solutions**:

```
Options:
1. Unity Transport (standalone)
   - Same underlying library as NetCode
   - Full control over protocols
   - No ECS required

2. Mirror Networking
   - Mature, widely-used
   - MonoBehaviour-based
   - NetworkTransform for entities
   - Custom messages for voxel data

3. Netcode for GameObjects (NGO)
   - Unity's official non-ECS networking
   - NetworkTransform built-in
   - RPC system for custom data
```

**Benefits**:
- Single networking system (simpler)
- Proven solutions for transform sync
- Can still use custom protocols for voxel data
- No ECS complexity overhead

**For Transform Synchronization** (the only NetCode advantage):
- Mirror: NetworkTransform component (~50 lines of setup)
- NGO: NetworkTransform component (built-in)
- Custom: Manual interpolation (~100 lines of code)

**Verdict**: Transform sync is a **solved problem** in non-ECS networking. Not worth keeping ECS just for this.

---

## Part 3: ECS vs Non-ECS Comparison

### When ECS Shines (General Case)

✅ **ECS is Excellent For**:
- Thousands of similar entities (10,000+ objects)
- Using Unity.Physics package
- Using Unity NetCode for Entities
- Simple component queries across many entities
- Data-oriented transformations (e.g., flocking simulation)
- Entities.Graphics for massive rendering

### Your Specific Use Case

❌ **Caelix Does NOT Fit ECS Strengths**:

| ECS Strength | Caelix Reality |
|--------------|------------------|
| Thousands of entities | ~10-100 VoxelObjects max |
| Unity.Physics | Need custom voxel colliders |
| NetCode for Entities | Only useful for transform sync (small part of game) |
| Simple queries | Complex hierarchical data (Object→Sector→Brick→Block) |
| Stateless components | Large stateful native collections |

### Architecture Comparison

#### ECS Implementation

```csharp
// Complex setup, archetype management
[BurstCompile]
[WithAll(typeof(TestWorldSector))]
public partial struct TestWorldGenerationJob : IJobEntity
{
    private void Execute(ref SectorData sectorData, in SectorTransform sectorTransform)
    {
        // Generation logic
    }
}

// Issues:
// - Chunk-level batching (parallelization problems)
// - ComponentLookup boilerplate for cross-entity access
// - Safety system restrictions
// - Lost editor features
```

#### Non-ECS Implementation

```csharp
// Straightforward, familiar patterns
public class VoxelObject : MonoBehaviour
{
    private Dictionary<int3, SectorData> sectors;

    public void GenerateSectors(int3[] positions)
    {
        var handles = new NativeArray<JobHandle>(positions.Length, Allocator.Temp);
        for (int i = 0; i < positions.Length; i++)
        {
            var job = new GenerateSectorJob
            {
                sectorData = sectors[positions[i]],
                position = positions[i]
            };
            handles[i] = job.Schedule();
        }
        JobHandle.CompleteAll(handles); // True parallelization!
    }
}

[BurstCompile]
public struct GenerateSectorJob : IJob
{
    public SectorData sectorData;
    public int3 position;

    public void Execute()
    {
        // Same generation logic, cleaner syntax
    }
}

// Benefits:
// - True per-sector parallelization (trivial to implement)
// - Full Burst compilation (same performance)
// - Editor integration (gizmos, inspector)
// - Straightforward debugging
```

### Feature Comparison Table

| Feature | ECS | Non-ECS |
|---------|-----|---------|
| **Performance (Burst)** | ✓ Excellent | ✓ Excellent |
| **Parallelization** | ✗ Complex (chunk batching) | ✓ Trivial (manual JobHandle array) |
| **Code Complexity** | ✗ High (archetypes, safety) | ✓ Low (familiar patterns) |
| **Editor Integration** | ✗ Limited | ✓ Full (gizmos, inspector, scene view) |
| **Debugging** | ✗ Complex (entity IDs, queries) | ✓ Standard (breakpoints, watches) |
| **Physics** | ✗ Can't use Unity.Physics | ✓ Custom implementation (same either way) |
| **Networking** | ~ NetCode (limited usefulness) | ✓ Mirror/NGO/Custom (simpler) |
| **Learning Curve** | ✗ Steep (paradigm shift) | ✓ Shallow (standard Unity) |
| **Maintenance** | ✗ Harder (ECS-specific issues) | ✓ Easier (widespread knowledge) |

---

## Part 4: Lessons Learned from ECS Journey

### Valuable Knowledge Gained

Despite abandoning the ECS implementation, significant learning occurred:

1. **Data-Oriented Design Principles**
   - Thinking about memory layout and cache efficiency
   - Separating data from behavior
   - Using native collections for performance

2. **Burst Compilation Mastery**
   - Understanding Burst constraints and capabilities
   - Aggressive inlining for hot paths
   - Unsafe code patterns for performance

3. **Job System Patterns**
   - Parallel job scheduling
   - Dependency management with JobHandle
   - Work distribution strategies

4. **Unity's Native Collections**
   - NativeArray, NativeList, NativeHashMap usage
   - Explicit disposal patterns (ICleanupComponentData equivalent)
   - Memory safety considerations

### These Lessons Transfer to Non-ECS

**You can keep the best parts of data-oriented design**:

```csharp
// Still data-oriented, still using Burst + Jobs
public class VoxelObject : MonoBehaviour
{
    // Native collections (same as ECS)
    private NativeHashMap<int3, SectorData> sectors;

    // Burst-compiled jobs (same performance)
    [BurstCompile]
    public struct GenerateJob : IJob { /* ... */ }

    // Parallel execution (cleaner syntax)
    public void Update()
    {
        var jobs = new NativeList<JobHandle>(Allocator.Temp);
        foreach (var sector in sectorsToGenerate)
        {
            jobs.Add(new GenerateJob { ... }.Schedule());
        }
        JobHandle.CompleteAll(jobs.AsArray());
    }
}
```

**Benefits Retained**:
- ✓ Burst compilation (1000x+ speedup vs managed code)
- ✓ Job system parallelization (full CPU utilization)
- ✓ Native collections (cache-friendly, efficient)
- ✓ Data-oriented thinking (architectural clarity)

**Complexity Removed**:
- ✗ Archetypes and chunk management
- ✗ ComponentLookup boilerplate
- ✗ Entity query syntax
- ✗ Safety system restrictions
- ✗ Limited editor integration

### Architecture Insights

**Hierarchical Data ≠ ECS**

Caelix has inherent hierarchy:
```
VoxelObject
  └── Sectors (16³ per object)
      └── Bricks (8³ per sector, sparse)
          └── Blocks (8³ per brick)
```

ECS flattens this into entity soup:
```
Entity (VoxelObject) → Component (VoxelObject data)
Entity (Sector) → Components (SectorData, SectorTransform)
```

**Problem**: Lost hierarchical relationships that are conceptually important. Need SectorIndex hashmap to reconstruct relationships.

**Non-ECS**: Natural hierarchy through object references. Cleaner mental model.

---

## Part 5: Decision Rationale

### The Core Question

**"Is NetCode's transform synchronization worth the ECS complexity?"**

### Analysis

**What NetCode Provides**:
- Player transform sync with client prediction
- VoxelObject (ship/vehicle) transform sync
- Connection management
- Lag compensation

**Estimated Effort**: ~5-10% of total networking implementation

**What Requires Custom Implementation Anyway**:
- Voxel data synchronization (bulk transfer)
- Block change updates
- Cellular automata sync
- Voxel physics events
- World streaming
- Compression protocols

**Estimated Effort**: ~90-95% of total networking implementation

### Alternative Solutions (Non-ECS)

**For Transform Synchronization**:

1. **Mirror Networking**
   - Mature, well-documented
   - NetworkTransform component (drag-and-drop)
   - Custom messages for voxel data
   - Active community, many examples

2. **Netcode for GameObjects**
   - Unity's official non-ECS solution
   - NetworkTransform built-in
   - RPC system for custom data
   - Integrated with Unity services

3. **Custom with Unity Transport**
   - Same underlying transport as NetCode
   - Full control
   - ~100 lines for basic transform sync

**Conclusion**: Transform sync is a **solved problem** with simple solutions. Does NOT justify ECS commitment.

### The Pragmatic Assessment

**ECS Benefits for Caelix**:
- Transform sync with NetCode (~5% of networking)
- Data-oriented paradigm (can keep with Jobs+Burst)

**ECS Costs for Caelix**:
- Parallelization complexity (chunk batching issue)
- Lost editor integration (gizmos, debugging)
- Steeper learning curve for collaborators
- 90% of networking still custom anyway
- Fighting framework for hierarchical data

**Ratio**: Costs heavily outweigh benefits.

### Final Decision

**Return to MonoBehaviour + Jobs + Burst architecture**

**Keep**:
- Data-oriented design principles
- Burst compilation
- Job system parallelization
- Native collections

**Regain**:
- Simple, clear parallelization
- Full editor integration
- Straightforward debugging
- Familiar patterns
- Natural hierarchical data model

**Networking Strategy**:
- Use Mirror or NGO for entity synchronization
- Implement custom protocols for voxel data (same as would be needed with ECS)
- Total implementation simpler than ECS + NetCode hybrid

---

## Part 6: Recommendations for Non-ECS Implementation

### Architecture Overview

```
VoxelObject (MonoBehaviour)
├── Sectors (Dictionary<int3, SectorData>)
├── SectorRenderer (manages GPU data)
├── GenerationJobs (IJob for parallel generation)
└── NetworkSync (custom voxel protocol)

Player (MonoBehaviour)
├── Transform sync (Mirror/NGO NetworkTransform)
├── Interaction (block place/break)
└── Standard Unity components

GameManager (MonoBehaviour)
├── World state
├── Network manager
└── Simulation coordinator
```

### Performance Best Practices

1. **Use Burst Compilation Aggressively**
```csharp
[BurstCompile]
public struct GenerateSectorJob : IJob
{
    public NativeList<Block> voxels;
    public int3 sectorPosition;

    public void Execute()
    {
        // Burst-compiled generation logic
        // Same performance as ECS
    }
}
```

2. **Parallelize with Job Arrays**
```csharp
public void GenerateMultipleSectors(List<int3> positions)
{
    var handles = new NativeArray<JobHandle>(positions.Count, Allocator.Temp);
    for (int i = 0; i < positions.Count; i++)
    {
        var job = new GenerateSectorJob
        {
            voxels = sectors[positions[i]].voxels,
            sectorPosition = positions[i]
        };
        handles[i] = job.Schedule();
    }
    JobHandle.CompleteAll(handles);
}
```

3. **Keep Native Collections for Efficiency**
```csharp
public class SectorData : IDisposable
{
    public NativeList<Block> voxels;
    public NativeArray<short> brickIdx;
    // ... same data structures as ECS version

    public void Dispose()
    {
        if (voxels.IsCreated) voxels.Dispose();
        if (brickIdx.IsCreated) brickIdx.Dispose();
    }
}
```

4. **Leverage Unity's Job System**
```csharp
// IJobParallelFor for brick-level parallelization
[BurstCompile]
public struct ProcessBricksJob : IJobParallelFor
{
    [NativeDisableParallelForRestriction]
    public NativeList<Block> voxels;
    [ReadOnly] public NativeArray<short> brickIndices;

    public void Execute(int brickIndex)
    {
        // Process individual brick
    }
}
```

### Networking Strategy

**High-Level Design**:
```
Mirror/NGO Layer:
├── Player NetworkTransform
├── VoxelObject NetworkTransform (ships/vehicles)
└── Game state sync

Custom Protocol Layer (Unity Transport):
├── Sector data streaming (initial load)
├── Multi-block update packets (changes)
├── Cellular automata inputs
└── Physics events
```

**Custom Voxel Protocol Example**:
```csharp
public enum VoxelMessageType : byte
{
    SectorRequest = 1,
    SectorData = 2,
    BlockUpdates = 3,
    AutomataInput = 4
}

public struct MultiBlockUpdate
{
    public int3 sectorPosition;
    public List<BlockChange> changes;
}

public struct BlockChange
{
    public int3 localPosition;
    public ushort blockId;
}

// RLE compression for efficiency
public byte[] CompressBrick(Block[] blocks)
{
    // Run-length encoding implementation
    // Typical 5x-10x compression
}
```

### Editor Integration Advantages

**Gizmos for Visualization**:
```csharp
void OnDrawGizmos()
{
    if (sectors == null) return;

    Gizmos.color = Color.cyan;
    foreach (var (position, sector) in sectors)
    {
        Vector3 worldPos = transform.position + position * 128f;
        Gizmos.DrawWireCube(worldPos + Vector3.one * 64f, Vector3.one * 128f);
    }
}
```

**Inspector Customization**:
```csharp
[CustomEditor(typeof(VoxelObject))]
public class VoxelObjectEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var voxelObject = (VoxelObject)target;

        EditorGUILayout.LabelField("Sectors", voxelObject.SectorCount.ToString());
        EditorGUILayout.LabelField("Total Bricks", voxelObject.BrickCount.ToString());

        if (GUILayout.Button("Generate Test World"))
        {
            voxelObject.GenerateTestWorld();
        }
    }
}
```

### Migration Path

**Step 1**: Port core data structures
- Copy SectorData struct
- Convert to class with IDisposable
- Keep native collections

**Step 2**: Port generation logic
- Extract generation code from ECS job
- Wrap in IJob interface
- Add parallel scheduling

**Step 3**: Implement MonoBehaviour wrapper
- VoxelObject component
- Lifecycle management (Dispose on destroy)
- Public API for interaction

**Step 4**: Add rendering
- Port GPU data management
- Integrate with existing renderer
- Scene view visualization

**Step 5**: Implement networking
- Choose framework (Mirror/NGO)
- Implement custom voxel protocol
- Test with multiple clients

---

## Part 7: Conclusion

### The Fundamental Mismatch

Unity ECS is a **specialized tool** optimized for specific use cases. Caelix does not match those use cases:

| ECS Optimized For | Caelix Requirements |
|-------------------|----------------------|
| Many small entities | Few large objects |
| Stateless components | Large stateful data |
| Simple queries | Complex hierarchies |
| Unity.Physics | Custom voxel physics |
| NetCode replication | Custom voxel protocols |

### What Was Learned

1. **ECS is not universally better** - it's a specialized tool
2. **Data-oriented design ≠ ECS** - can be applied anywhere
3. **Burst + Jobs** work great with MonoBehaviours
4. **NetCode's benefits** don't justify ECS for this project
5. **Existing implementation** was already superior

### The Path Forward

**Return to proven MonoBehaviour architecture with:**
- ✓ Burst compilation (keep performance)
- ✓ Job system (keep parallelization)
- ✓ Native collections (keep efficiency)
- ✓ Data-oriented thinking (keep clean design)
- ✓ Editor integration (regain productivity)
- ✓ Simple networking (use established solutions)

### Final Wisdom

**"Use the right tool for the job."**

ECS is the right tool for:
- Large-scale entity simulations (10,000+ entities)
- Physics-heavy games using Unity.Physics
- NetCode-based multiplayer where most game state is entity transforms
- Teams committed to the ECS paradigm

ECS is NOT the right tool for:
- **Voxel engines with few objects and custom networking** ← Caelix
- Hierarchical game architectures
- Projects requiring heavy editor integration
- Teams wanting straightforward Unity development

### Time to Move Forward

The ECS experiment provided valuable learning but has reached its conclusion. The evidence from profiling, networking research, and architecture analysis all point the same direction: **return to the working non-ECS implementation** and build from there.

The non-ECS Caelix already demonstrated superior performance, cleaner parallelization, and simpler code. It's time to leverage that foundation and focus energy on building gameplay features rather than fighting framework limitations.

---

## Appendix: References

### Unity Documentation
- [Unity Job System](https://docs.unity3d.com/Manual/JobSystem.html)
- [Burst Compiler](https://docs.unity3d.com/Packages/com.unity.burst@latest)
- [Unity Transport](https://docs.unity3d.com/Packages/com.unity.transport@latest)
- [NetCode for Entities](https://docs.unity3d.com/Packages/com.unity.netcode@latest)

### Networking Solutions
- [Mirror Networking](https://mirror-networking.com/)
- [Netcode for GameObjects](https://docs-multiplayer.unity3d.com/)
- [Minecraft Protocol](https://wiki.vg/Protocol)

### Community Resources
- Unity Forums: ECS discussions
- Reddit: r/Unity3D voxel engine threads
- GitHub: Unity NetCode samples

### Performance Analysis Tools
- Unity Profiler (Timeline View)
- Memory Profiler
- Frame Debugger

---

**END OF DOCUMENT**

This decision document should be preserved as institutional knowledge for the project. Future contributors or the original developer reconsidering ECS can reference this comprehensive analysis.
