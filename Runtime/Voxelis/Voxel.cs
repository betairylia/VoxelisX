using System;
using UnityEngine;
using Voxelis.Rendering;

namespace Voxelis
{
    /// <summary>
    /// Reference wrapper for a sector that includes rendering and entity association.
    /// Manages the lifecycle and rendering state of a sector within a voxel entity.
    /// </summary>
    /// <remarks>
    /// DEPRECATED: This class is being removed in favor of direct Sector usage.
    /// Rendering state will be managed separately from sector data.
    /// </remarks>
    public class SectorRef : IDisposable
    {
        /// <summary>
        /// The underlying sector data containing voxel blocks.
        /// </summary>
        public Sector sector;

        /// <summary>
        /// Indicates whether this sector reference has pending changes (currently unused).
        /// </summary>
        public bool isDirty;

        /// <summary>
        /// Position of this sector in sector coordinates (each unit = 128 blocks).
        /// </summary>
        public Vector3Int sectorPos;

        /// <summary>
        /// Position of this sector in block coordinates.
        /// Converts sector position to block space (sector * 128).
        /// </summary>
        /// <remarks>FIXME: WARNING: The multiplier 128 is hardcoded and should use Sector constants.</remarks>
        public Vector3Int sectorBlockPos => sectorPos * 128;

        /// <summary>
        /// The renderer responsible for preparing and submitting this sector's data to the GPU.
        /// Null if this sector is not rendered (e.g., server-side or headless mode).
        /// </summary>
        public SectorRenderer renderer;

        /// <summary>
        /// The voxel entity that owns this sector.
        /// </summary>
        public VoxelEntity entity;

        /// <summary>
        /// Gets the total host memory usage of this sector, including renderer buffers.
        /// </summary>
        public ulong MemoryUsage => (ulong)sector.MemoryUsage + (ulong)(renderer?.MemoryUsage ?? 0);

        /// <summary>
        /// Gets the total GPU memory (VRAM) usage of this sector's rendering resources.
        /// </summary>
        public ulong VRAMUsage => (ulong)(renderer?.VRAMUsage ?? 0);

        /// <summary>
        /// Constructs a new sector reference.
        /// </summary>
        /// <param name="entity">The owning voxel entity.</param>
        /// <param name="sec">The sector data structure.</param>
        /// <param name="pos">Position of the sector in sector coordinates.</param>
        /// <param name="hasRenderer">Whether to create a renderer for this sector (default: true).</param>
        public SectorRef(VoxelEntity entity, Sector sec, Vector3Int pos, bool hasRenderer = true)
        {
            this.entity = entity;
            sector = sec;
            sectorPos = pos;

            if (hasRenderer)
            {
                renderer = new SectorRenderer(this);
            }
        }

        /// <summary>
        /// Performs per-frame update for this sector.
        /// Reorders bricks and finalizes the update cycle.
        /// </summary>
        /// <remarks>TODO: Call renderer RenderEmitJob and Render here with proper parallelization.</remarks>
        public void Tick()
        {
            sector.ReorderBricks();
            // TODO: Call renderer RenderEmitJob and Render here with proper parallelization
            sector.EndTick();
        }

        /// <summary>
        /// Marks this sector for removal. Disposes the sector data and marks the renderer for removal.
        /// </summary>
        public void Remove()
        {
            sector.Dispose();
            renderer?.MarkRemove();
        }

        /// <summary>
        /// Disposes this sector reference, releasing all resources including sector data and renderer.
        /// </summary>
        public void Dispose()
        {
            sector.Dispose();
            renderer?.Dispose();
        }
    }
}
