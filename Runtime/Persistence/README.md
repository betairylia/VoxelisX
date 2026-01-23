# VoxelisX Save/Load System

A comprehensive save/load system for VoxelisX that provides efficient sector serialization with RLE compression and entity persistence.

## Features

- **RLE Compression**: Sector voxel data compressed with run-length encoding (444111 layout)
- **Dirty Flag Persistence**: Full preservation of sector dirty flags and boundary masks
- **Region Files**: Sectors packed into region files for efficient storage
  - Infinite entities: 16×16×16 fixed grid regions
  - Finite entities: Variable-size packed regions
- **Entity Persistence**: Complete entity serialization including:
  - Transform data
  - Physics properties
  - Infinite loader settings
  - Sector references
- **GUID-based Identification**: Each entity has a unique GUID for reliable save/load

## Quick Start

### Saving Entities

```csharp
using Voxelis.Persistence;

// Create save/load manager
SaveLoadManager saveManager = new SaveLoadManager();

// Save a single entity
VoxelEntity entity = GetComponent<VoxelEntity>();
saveManager.SaveEntity(entity);

// Or save all entities in scene
saveManager.SaveAllEntities();
```

### Loading Entities

```csharp
using Voxelis.Persistence;

SaveLoadManager saveManager = new SaveLoadManager();

// Load entity by GUID
Guid entityGuid = ...; // Get GUID from somewhere
VoxelEntity loadedEntity = saveManager.LoadEntity(entityGuid);

// Or load all entities
List<VoxelEntity> allEntities = saveManager.LoadAllEntities();
```

### Entity Management

```csharp
// Get all entity GUIDs
Guid[] guids = saveManager.GetAllEntityGuids();

// Check if entity exists
bool exists = saveManager.EntityExists(someGuid);

// Delete entity
saveManager.DeleteEntity(someGuid);
```

## Advanced Usage

### Direct Sector Save/Load

```csharp
using Voxelis.Persistence;
using Unity.Mathematics;

RegionFileManager regionManager = new RegionFileManager();

unsafe
{
    // Save a sector
    int3 sectorPos = new int3(0, 0, 0);
    Sector* sector = ...; // Get sector pointer

    string regionPath = regionManager.GetInfiniteRegionPath(sectorPos);
    regionManager.WriteSector(regionPath, sectorPos, sector, RegionType.Infinite);

    // Load a sector (must pre-allocate)
    Sector* loadedSector = (Sector*)UnsafeUtility.Malloc(
        UnsafeUtility.SizeOf<Sector>(),
        UnsafeUtility.AlignOf<Sector>(),
        Allocator.Persistent);

    *loadedSector = Sector.New(Allocator.Persistent);

    bool success = regionManager.ReadSector(regionPath, sectorPos, loadedSector);
}
```

### Custom Save Directory

```csharp
// Use custom save directory instead of Application.persistentDataPath
string customPath = "/path/to/save/directory";
SaveLoadManager saveManager = new SaveLoadManager(customPath);
```

### Region Configuration

Global constants can be modified in `PersistenceConstants`:

```csharp
// Change infinite region size (default: 16×16×16 sectors)
PersistenceConstants.InfiniteRegionSize = new int3(32, 32, 32);
```

## File Structure

```
SaveData/
├── entities.vxe                    # Entity listing file
├── regions/
│   ├── infinite/                   # Infinite entity regions
│   │   ├── region_0_0_0.vxr       # 16³ sectors per region
│   │   ├── region_0_0_1.vxr
│   │   └── ...
│   └── finite/                     # Finite entity regions
│       ├── entity_{guid}.vxr       # One region per finite entity
│       └── ...
```

## API Reference

### SaveLoadManager

Main facade for save/load operations.

- `SaveEntity(VoxelEntity)`: Save a single entity
- `LoadEntity(Guid)`: Load entity by GUID
- `SaveAllEntities()`: Save all entities in scene
- `LoadAllEntities()`: Load all saved entities
- `DeleteEntity(Guid)`: Delete entity from save data
- `EntityExists(Guid)`: Check if entity exists
- `GetAllEntityGuids()`: Get all saved entity GUIDs

### RegionFileManager

Low-level region file management.

- `WriteSector(path, key, sector, type)`: Write sector to region
- `ReadSector(path, key, outSector)`: Read sector from region
- `GetInfiniteRegionPath(sectorPos)`: Get region path for sector
- `CompactRegion(path)`: Remove gaps and rebuild index

### VoxelCompression

RLE compression utilities.

- `CompressBrick(blocks, outBuffer, maxSize)`: Compress 512 blocks
- `DecompressBrick(buffer, bufferSize, outBlocks)`: Decompress to 512 blocks
- `CompressSector(sector, position, outBuffer, maxSize)`: Compress entire sector
- `DecompressSector(buffer, bufferSize, outSector)`: Decompress entire sector
- `EstimateSectorCompressedSize(sector)`: Estimate compressed size

## Data Formats

### Sector Data

- **Header**: Position, brick count, dirty flags, neighbor flags
- **Dirty Flags**: Complete ushort[4096] + uint[4096] arrays
- **Voxel Data**: RLE compressed per brick
  - Run count (ushort)
  - Block values (uint[runCount])
  - Run lengths (byte[runCount], 0-255 = 1-256 blocks)

### Entity Data

- **Metadata**: GUID, flags, transform
- **Physics**: Mass, center of mass, inertia tensor (optional)
- **Infinite Settings**: Load bounds, load/unload radii (optional)
- **Sector References**: List of sector positions

## Notes

- Sectors must be pre-allocated before loading (use `Sector.New()`)
- `brickRequireUpdateFlags` and `updateRecord` are NOT saved (transient data)
- `NonEmptyBricks` list is rebuilt on load via `UpdateNonEmptyBricks()`
- Region files use CRC32 checksums for data integrity
- Infinite loader components must be manually re-added after load

## Future Enhancements

- Incremental saves (only modified sectors)
- Additional compression (LZ4, DEFLATE)
- Async save/load operations
- Save format versioning and migration
- Region file defragmentation
