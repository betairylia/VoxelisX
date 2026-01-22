# VoxelisX Save/Load System

## Overview

The VoxelisX save/load system provides persistent storage for infinite voxel worlds with support for:

- **Per-sector streaming**: Load/save individual sectors on-demand for infinite worlds
- **Dirty flag persistence**: Saves and restores dirty flags to maintain update behavior across sessions
- **RLE compression**: Efficient voxel data storage using run-length encoding
- **Region batching**: Groups 16×16×16 sectors (configurable) into region files for manageable file sizes
- **Multi-entity support**: Handles infinite basement world + multiple finite entities (spaceships, islands, etc.)
- **AABB tracking**: Spatial indexing for efficient entity discovery and loading

## Architecture

### File Structure

```
SavePath/
├── world.meta                              # World settings, seed, main entity GUID
├── entity_index.{x}.{y}.{z}.eidx          # Partitioned entity directory
├── region.{rx}.{ry}.{rz}.vxr              # Sector storage for infinite entities
└── entity_archive.{ax}.{ay}.{az}.vxea     # Complete storage for finite entities
```

### Entity Types

1. **Infinite Entities** (e.g., basement world)
   - Sectors stored in region files
   - Supports partial loading (per-sector streaming)
   - Ideal for procedurally generated infinite worlds

2. **Finite Entities** (e.g., spaceships, buildings)
   - All sectors stored together in entity archives
   - Always fully loaded (prevents physics issues from partial loading)
   - AABB tracked for spatial queries

### Spatial Partitioning

- **Sector**: 128³ blocks (16³ bricks of 8³ blocks each)
- **Region**: Configurable sectors per axis (default 16³ = 4096 sectors)
- **Entity Index Grid**: Configurable regions per axis (default 16³ regions)
- **Entity Archive Grid**: Same as index grid for alignment

## Usage

### 1. Create Configuration Asset

```csharp
// Create via: Assets > Create > VoxelisX > World Save Configuration
// Configure region sizes, save path, auto-save settings
```

### 2. Set Up World Persistence Manager

```csharp
// Add WorldPersistenceManager to your world root GameObject
// Assign the configuration asset
// Assign the main infinite world entity
```

### 3. Register Entities

```csharp
WorldPersistenceManager persistence = GetComponent<WorldPersistenceManager>();

// Register infinite world
Guid worldGuid = Guid.NewGuid();
persistence.RegisterEntity(infiniteWorldEntity, worldGuid, EntityType.Infinite);

// Register finite entity
Guid shipGuid = Guid.NewGuid();
persistence.RegisterEntity(spaceshipEntity, shipGuid, EntityType.Finite);
```

### 4. Integrate with Streaming

```csharp
// In your sector loader/streamer:
void OnSectorNeeded(int3 sectorPos)
{
    // Try to load from disk first
    if (persistence.LoadSectorForMainWorld(sectorPos))
    {
        // Sector loaded from save
        return;
    }

    // Check if sector was generated before
    if (persistence.MainWorldSectorExists(sectorPos))
    {
        // Sector exists but is empty - don't regenerate
        return;
    }

    // Sector never generated - run world generation
    GenerateSector(sectorPos);
}
```

### 5. Save/Load Finite Entities

```csharp
// Save
persistence.SaveEntity(spaceshipEntity);

// Load
Guid shipGuid = // ... load from somewhere
VoxelEntity loadedShip = persistence.LoadFiniteEntity(shipGuid);
```

### 6. Manual Save

```csharp
// Save all entities
persistence.SaveAll();

// Save single entity
persistence.SaveEntity(entity);
```

## Advanced Usage

### Direct WorldSaveManager Access

```csharp
WorldSaveManager saveManager = new WorldSaveManager(savePath);

// Configure settings first
WorldSaveConstants.REGION_SIZE_IN_SECTORS = 16;

// Save sector
saveManager.SaveSector(entityGuid, sectorPos, sectorPtr);

// Load sector
saveManager.LoadSector(entityGuid, sectorPos, sectorPtr);

// Save finite entity
saveManager.SaveFiniteEntity(metadata, sectors);

// Query entities in region
List<EntityMetadata> entities = saveManager.GetEntitiesIntersectingRegion(regionPos);

// Flush to disk
saveManager.FlushAll();
```

### Extension Methods

```csharp
// Create metadata from entity
EntityMetadata metadata = entity.CreateMetadata(guid, EntityType.Finite);

// Save infinite entity
entity.SaveToRegions(guid, saveManager);

// Save finite entity
entity.SaveFiniteEntity(guid, saveManager);

// Load sector
entity.LoadSectorFromRegion(guid, sectorPos, saveManager);

// Check if sector exists
bool exists = entity.SectorExistsInStorage(guid, sectorPos, saveManager);
```

## File Formats

### Region File (.vxr)

```
[Header]
  - magic: "VOXS" (0x53584F56)
  - version: 1
  - regionCoords: int3
  - entityCount: uint
  - sectorDataOffset: long

[Entity Index]
  - For each entity:
    - GUID: 16 bytes
    - sectorCount: uint
    - For each sector:
      - localPos: int3
      - dataOffset: long
      - dataSize: uint

[Sector Data]
  - For each sector:
    - sectorPos: int3
    - dirtyFlags: ushort (saved)
    - nonEmptyBrickCount: ushort
    - For each brick:
      - brickIdx: short
      - brickDirtyFlags: ushort
      - brickDirtyDirMask: uint
      - blockCount: ushort
      - RLE compressed blocks
```

### Entity Index File (.eidx)

```
[Header]
  - magic: "VXEI" (0x49455856)
  - version: 1
  - gridCoords: int3
  - entityCount: uint

[Entity Entries]
  - For each entity:
    - guid: 16 bytes
    - type: byte (0=Infinite, 1=Finite)
    - transform: RigidTransform
    - entityDirtyFlags: ushort
    - hasAABB: bool
    - [if hasAABB] aabbMin, aabbMax: float3
```

### Entity Archive File (.vxea)

```
[Header]
  - magic: "VXEA" (0x41455856)
  - version: 1
  - gridCoords: int3
  - entityCount: uint

[Entity Index]
  - For each entity:
    - metadata: EntityMetadata
    - sectorCount: uint
    - dataOffset: long
    - dataSize: uint
    - sectorPositions: int3[]

[Entity Data]
  - For each entity:
    - All sectors (same format as region file)
```

## Performance Considerations

### Compression

- RLE compression is most effective for:
  - Large empty regions (common in voxel worlds)
  - Solid blocks of same material
  - Smooth gradients
- Worst case: checkerboard patterns (no compression)

### File Sizes

Typical sizes with default settings (16³ sectors per region):
- Empty region: ~1 KB (just index)
- Sparse region (10% filled): ~50-200 KB
- Dense region (90% filled): ~500 KB - 2 MB
- Entity index: ~100 bytes per entity
- Entity archive: Varies by entity size

### Memory Usage

- Region files: Index loaded on access, sector data streamed
- Entity indices: Loaded entirely into memory (small)
- Entity archives: Index loaded, sector data on-demand

### Optimization Tips

1. **Adjust region size** for your use case:
   - Larger regions: Fewer files, larger individual files
   - Smaller regions: More files, finer-grained loading

2. **Use dirty-only saving** for frequent auto-saves

3. **Unload caches** periodically to free memory:
   ```csharp
   saveManager.UnloadCaches();
   ```

4. **Batch saves** rather than saving after every block change

## Important Notes

### Dirty Flags

- **Dirty flags ARE saved**: Restored exactly as they were
- **RequireUpdate flags are NOT saved**: Recalculated from dirty flags on load
- This ensures save/load doesn't affect update behavior
- Dirty flag propagation runs normally after loading

### Empty vs Missing Sectors

- **Missing**: Sector position not in index → Never generated, trigger world gen
- **Empty**: Sector in index but all air → Generated and empty, don't regenerate

### Thread Safety

- WorldSaveManager is **NOT thread-safe**
- All save/load operations must be called from main thread
- RLE compression is safe to use in jobs (if needed in future)

## Error Handling

The system logs errors but attempts to continue:
- Invalid magic numbers → Skip file
- Version mismatch → Warning, attempt to load anyway
- Corrupt data → Skip sector/entity, continue with others
- Missing files → Treat as not generated

Always check return values:
```csharp
if (saveManager.LoadSector(guid, pos, sector))
{
    // Success
}
else
{
    // Failed - sector doesn't exist or error occurred
}
```
