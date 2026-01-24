# Sector Serialization API

## Overview

The `SectorSerialization` class provides unsafe, Burst-compatible serialization for `Sector` data with RLE (Run-Length Encoding) compression.

## Features

- **Unsafe & Burst-compiled** for maximum performance
- **RLE compression** for voxel data (run length capped at 256)
- **Dirty flag support** - stores dirty flags and boundary masks when present
- **Efficient layout** - uses `4444...1111...` RLE layout (all counts, then all values)
- **Automatic flag omission** - per-brick flags are omitted when sector dirty flags = 0

## Binary Format

```
[HEADER]
  - uint:   Magic number (0x564F5853 = "VOXS")
  - byte:   Version (1)
  - ushort: sectorDirtyFlags
  - int:    currentBrickId (number of allocated bricks)

[DIRTY FLAGS SECTION] (only if sectorDirtyFlags != 0)
  For each of 4096 bricks:
    - ushort: brickDirtyFlags
    - ushort: brickRequireUpdateFlags
    - uint:   brickDirtyDirectionMask (26-bit neighbor mask)

[BRICK INDEX MAP]
  For each of 4096 bricks:
    - short: brickIdx (BRICKID_EMPTY = -1 for unallocated)

[VOXEL DATA] (RLE compressed)
  For each allocated brick:
    - ushort:     runCount (number of RLE runs)
    - byte[]:     run lengths (runCount entries)
    - uint[]:     Block data values (runCount entries)
```

## Usage Examples

### Basic Serialization

```csharp
unsafe void SerializeSector(Sector* sector)
{
    // Calculate maximum required buffer size
    int maxSize = SectorSerialization.CalculateMaxSerializedSize(sector);

    // Allocate buffer
    NativeArray<byte> buffer = new NativeArray<byte>(maxSize, Allocator.Persistent);

    // Serialize
    int actualSize = SectorSerialization.Serialize(
        sector,
        (byte*)buffer.GetUnsafePtr(),
        maxSize
    );

    if (actualSize < 0)
    {
        Debug.LogError("Serialization failed!");
        buffer.Dispose();
        return;
    }

    // Use buffer (save to file, send over network, etc.)
    // ...

    buffer.Dispose();
}
```

### Convenient Array-based API

```csharp
unsafe void SerializeSectorToArray(Sector* sector)
{
    // Serialize directly to a NativeArray (automatically sized)
    NativeArray<byte> serialized = SectorSerialization.SerializeToArray(
        sector,
        Allocator.Persistent
    );

    if (!serialized.IsCreated)
    {
        Debug.LogError("Serialization failed!");
        return;
    }

    Debug.Log($"Serialized to {serialized.Length} bytes");

    // Use serialized data...

    serialized.Dispose();
}
```

### Deserialization

```csharp
unsafe void DeserializeSector(NativeArray<byte> data, Sector* targetSector)
{
    bool success = SectorSerialization.DeserializeFromArray(data, targetSector);

    if (!success)
    {
        Debug.LogError("Deserialization failed!");
        return;
    }

    Debug.Log($"Deserialized sector with {targetSector->NonEmptyBrickCount} bricks");
}
```

### Manual Deserialization with Error Handling

```csharp
unsafe int DeserializeManual(byte* data, int dataSize, Sector* sector)
{
    int bytesRead = SectorSerialization.Deserialize(data, dataSize, sector);

    if (bytesRead < 0)
    {
        Debug.LogError("Deserialization failed - corrupted data or invalid format");
        return -1;
    }

    Debug.Log($"Read {bytesRead} bytes");
    return bytesRead;
}
```

## RLE Compression Details

### Run-Length Encoding Layout

The serialization uses a **4444...1111...** layout instead of traditional **41414141...** pairs:

```
Traditional (count-value pairs):
[count1][value1][count2][value2][count3][value3]...

This API (all counts, then all values):
[count1][count2][count3]...[value1][value2][value3]...
```

This layout is more efficient for:
- Burst SIMD operations
- Memory prefetching
- Cache coherency

### Run Length Capping

Runs are capped at 256 blocks (1 byte). For uniform bricks (512 identical blocks), this creates 2 runs:
- Run 1: length=256, value=X
- Run 2: length=256, value=X

### Compression Efficiency Examples

| Pattern | Uncompressed | Compressed | Ratio |
|---------|-------------|------------|-------|
| Uniform brick (all same) | 2048 bytes | ~12 bytes | 170:1 |
| Alternating blocks | 2048 bytes | ~1036 bytes | 2:1 |
| Random data | 2048 bytes | ~1500-2048 bytes | 1-1.4:1 |

## Dirty Flag Optimization

When `sectorDirtyFlags == 0`, the entire dirty flags section (32 KB) is **omitted** from serialization:

```
Size savings: 4096 bricks × (2+2+4) bytes = 32,768 bytes
```

This is ideal for:
- Saving clean/static sectors
- Network transmission of unchanged data
- Archive storage

## Performance Considerations

1. **Burst compilation**: All methods are marked with `[BurstCompile]` for optimal performance
2. **Stack allocation**: Uses `stackalloc` for temporary buffers (no heap allocations)
3. **Memory copying**: Uses `UnsafeUtility.MemCpy` for bulk operations
4. **No validation overhead**: Unsafe code assumes valid input (validate externally if needed)

## Error Codes

All serialization methods return `-1` on error:
- Buffer overflow (insufficient space)
- Corrupted magic number
- Unsupported version
- Invalid data (wrong block count during decompression)

## Thread Safety

- **Serialization**: Thread-safe if different sectors are serialized concurrently
- **Deserialization**: Not thread-safe for same target sector
- Use Burst jobs for parallel batch processing

## Version Compatibility

Current version: **1**

Future versions will maintain backward compatibility through version checks in the header.
