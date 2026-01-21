# VoxelisX

A high-performance, ray-traced dynamic voxel engine for Unity URP (Universal Render Pipeline).

## Overview

VoxelisX is a Unity package that enables efficient voxel-based rendering and simulation using modern Unity technologies. It provides both real-time ray tracing and mesh-based rendering options for voxel worlds, with built-in physics simulation and sector streaming capabilities.

**Version:** 0.1.0
**Author:** betairylia (betairya@gmail.com)
**License:** MIT

## Features

- **Dual Rendering Paths**
  - Hardware ray tracing with Unity's RayTracingAccelerationStructure (RTAS)
  - Greedy mesh generation for mesh-based fallback rendering
  - Temporal Anti-Aliasing (TAA) support

- **Efficient Voxel Storage**
  - Sparse brick-based storage (8×8×8 blocks per brick)
  - Sectors of 128×128×128 blocks (16×16×16 bricks)
  - RGB555 color format with emission bit (32-bit per block)
  - Only non-empty bricks consume memory

- **High Performance**
  - Unity DOTS (Data-Oriented Technology Stack) integration
  - Burst-compiled jobs for CPU-intensive operations
  - Native collections for minimal garbage collection
  - Dirty flag propagation system for efficient updates

- **Physics Simulation**
  - Unity Physics (DOTS) integration
  - Voxel-based collision detection
  - Per-body mass and inertia calculations
  - Configurable gravity and solver parameters

- **Sector Streaming**
  - Abstract `InfiniteLoader` base class for custom implementations
  - Manhattan distance-based loading priority
  - Configurable load/unload radii

- **Interaction System**
  - DDA-based voxel raycasting
  - Block placement and removal
  - Visual block selection indicator

- **File Import**
  - MagicaVoxel (.vox) file loading support
  - Automatic coordinate system conversion

## Requirements

- **Unity Version:** 6.0+ (6000.0 or newer)
- **Render Pipeline:** Universal Render Pipeline (URP) 17.0+
- **GPU:** DirectX Raytracing (DXR) capable hardware for ray tracing mode
- **Platform:** Windows (for ray tracing features)

## Dependencies

The package requires the following Unity packages:

- `com.unity.burst` (1.8.25+)
- `com.unity.collections` (2.6.3+)
- `com.unity.entities` (1.4.3+)
- `com.unity.mathematics` (1.3.2+)
- `com.unity.render-pipelines.universal` (17.0.0+)

These are automatically resolved by Unity's Package Manager.

## Installation

### Via Git URL (Unity Package Manager)

1. Open Unity Package Manager (Window → Package Manager)
2. Click the '+' button in the top-left
3. Select "Add package from git URL"
4. Enter: `https://github.com/betairylia/VoxelisX.git`

### Via Local Package

1. Clone this repository
2. In Unity Package Manager, select "Add package from disk"
3. Navigate to the cloned folder and select `package.json`

## Quick Start

### Basic Setup

1. **Add URP Renderer Feature**
   - Open your URP Renderer asset
   - Add the `VoxelisXRendererFeature` to enable voxel rendering

2. **Create a Voxel World**
   ```csharp
   using Voxelis.VoxelisX;

   public class MyVoxelWorld : VoxelisXWorld
   {
       protected override void Awake()
       {
           base.Awake();
           // Initialize your world
       }
   }
   ```

3. **Create a Voxel Entity**
   ```csharp
   VoxelEntity entity = new VoxelEntity();
   entity.SetBlock(new int3(0, 0, 0), Block.FromRGB(255, 0, 0)); // Red voxel
   ```

### Loading from MagicaVoxel Files

```csharp
using Voxelis.VoxelisX;

VoxelEntity entity = VoxFileLoader.LoadVoxFile("path/to/model.vox");
```

### Implementing Sector Streaming

```csharp
public class MyInfiniteLoader : InfiniteLoader
{
    protected override void LoadSector(int3 sectorPos)
    {
        // Generate or load sector data
        Sector sector = GenerateTerrainSector(sectorPos);
        Entity.CopyAndAddSectorAt(sector, sectorPos);
    }

    protected override void UnloadSector(int3 sectorPos)
    {
        // Save sector data if needed
        Entity.RemoveSector(sectorPos);
    }
}
```

### Raycasting and Interaction

```csharp
using Voxelis.VoxelisX;

// Raycast against voxel world
if (VoxelRayCast.Raycast(ray, entity, out VoxelRaycastHit hit))
{
    Debug.Log($"Hit voxel at {hit.blockPos}");

    // Modify voxel
    entity.SetBlock(hit.blockPos, Block.Empty);
}
```

## Architecture

### Core Data Structures

- **Block**: 32-bit packed voxel data (RGB555 + emission + metadata)
- **Sector**: Container of 128×128×128 blocks organized in 16×16×16 bricks
- **VoxelEntity**: Manages a collection of sectors with neighbor tracking
- **VoxelisXCoreWorld**: Base world manager coordinating all entities

### Rendering Pipeline

1. **Ray Tracing Path** (GPU ray tracing)
   - Uses Unity's RayTracingAccelerationStructure
   - Per-brick AABB buffers and material data
   - Temporal anti-aliasing post-processing
   - Dirty flag system for efficient GPU buffer updates

2. **Mesh Path** (traditional mesh rendering)
   - Burst-compiled greedy meshing algorithm
   - Per-sector mesh generation
   - Efficient face merging to reduce draw calls

### Tick System

VoxelisXWorld uses a staged tick system with dependency management:

1. **Physics Stage**: Collision detection and resolution
2. **Automata Stage**: Custom voxel update logic
3. **Rendering Stage**: Buffer updates and mesh generation

### Dirty Flag Propagation

Changes to voxels trigger a cascading dirty flag system:
- Modified bricks mark themselves dirty
- `DirtyPropagationJob` propagates flags to neighboring sectors
- Dependent systems (rendering, physics, collision) update only dirty regions

## Performance Considerations

- **Sparse Storage**: Only non-empty bricks allocate memory (~2MB per fully-populated sector)
- **Burst Compilation**: Hot paths compiled to native code for maximum performance
- **Native Collections**: Reduced garbage collection pressure
- **Parallel Jobs**: Multi-threaded operations via Unity Job System
- **Dirty Flags**: Minimal per-frame updates by tracking changes

## Project Structure

```
VoxelisX/
├── Runtime/
│   ├── Core/                    # Core voxel data structures
│   │   ├── Sector.cs
│   │   ├── Block.cs
│   │   ├── VoxelEntity.cs
│   │   └── DirtyPropagation/
│   ├── Rendering/               # Ray tracing and mesh rendering
│   │   ├── VoxelisXRenderer.cs
│   │   ├── Mesh/
│   │   └── Shaders/
│   ├── Simulation/              # Physics integration
│   │   ├── VoxelisXWorld.cs
│   │   └── VoxelBody.cs
│   └── VoxelRayCast.cs         # DDA raycasting
├── Tests/                       # Unit tests
└── package.json
```

## Testing

The package includes comprehensive unit tests for core components:

```bash
# Run tests in Unity Test Runner
Window → General → Test Runner
```

Tests cover:
- Block packing/unpacking
- Sector brick management
- Dirty flag propagation
- Neighborhood configurations

## Troubleshooting

### Ray tracing not working
- Ensure your GPU supports DXR (DirectX Raytracing)
- Verify URP asset has ray tracing enabled
- Check that `VoxelisXRendererFeature` is added to your URP Renderer

### Performance issues
- Consider using mesh-based rendering on lower-end hardware
- Adjust sector load/unload radius for streaming
- Enable Burst compilation in Project Settings

### Voxels not appearing
- Verify the renderer is properly initialized
- Check that sectors are marked dirty after modifications
- Ensure camera has the correct rendering layers

## Roadmap

- [ ] Optimized CPU-side collision mesh generation
- [ ] Advanced lighting and global illumination
- [ ] Multi-material support
- [ ] LOD system for distant sectors
- [ ] Networking support for multiplayer
- [ ] Advanced procedural generation utilities

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## License

This project is licensed under the MIT License. See LICENSE file for details.

## Credits

- **Author**: betairylia (betairya@gmail.com)
- **VoxReader**: MagicaVoxel file format support

## Contact

For questions, issues, or suggestions, please open an issue on the GitHub repository.
