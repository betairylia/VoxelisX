using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.NotBurstCompatible;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Voxelis.Simulation;

namespace Voxelis
{
    /// <summary>
    /// Represents a voxel-based entity in the game world. This is the main component for managing
    /// collections of voxel sectors, handling rendering, and interfacing with the physics system.
    /// </summary>
    /// <remarks>
    /// VoxelEntity organizes voxels into sectors for efficient storage and rendering. Each sector
    /// contains a fixed-size grid of bricks, which in turn contain individual voxel blocks.
    /// The entity automatically registers/unregisters itself with the VoxelisXRenderer on enable/disable.
    /// </remarks>
    public partial class VoxelEntity : MonoBehaviour, IDisposable
    {
        /// <summary>
        /// Dictionary mapping sector positions to their corresponding sector references.
        /// Key is the sector position in sector-space coordinates.
        /// </summary>
        public Dictionary<Vector3Int, SectorRef> Voxels = new Dictionary<Vector3Int, SectorRef>();

        /// <summary>
        /// Queue of sectors that are scheduled for removal and cleanup.
        /// Used to defer disposal of sectors until after rendering is complete.
        /// </summary>
        public Queue<SectorRef> sectorsToRemove = new Queue<SectorRef>();

        /// <summary>
        /// Called when the component is enabled. Registers this entity with the renderer and initializes physics body.
        /// </summary>
        private void OnEnable()
        {
            VoxelisXRenderer.instance.AddEntity(this);
        }

        /// <summary>
        /// Called when the component is disabled. Unregisters this entity from the renderer.
        /// </summary>
        private void OnDisable()
        {
            VoxelisXRenderer.instance.RemoveEntity(this);
        }

        /// <summary>
        /// Called when the component is destroyed. Disposes all voxel data.
        /// </summary>
        private void OnDestroy()
        {
            Dispose();
        }

        /// <summary>
        /// Disposes all resources used by this voxel entity, including all sectors and their data.
        /// </summary>
        public void Dispose()
        {
            foreach (var sector in Voxels.Values)
            {
                sector?.Dispose();
            }
            Voxels.Clear();
        }

        /// <summary>
        /// Calculates the total host (CPU) memory usage of all sectors in this entity.
        /// </summary>
        /// <returns>Total memory usage in kilobytes.</returns>
        public ulong GetHostMemoryUsageKB()
        {
            ulong result = 0;
            foreach (var s in Voxels.Values)
            {
                result += (ulong)(s.MemoryUsage / 1024);
            }

            return result;
        }

        /// <summary>
        /// Calculates the total GPU (VRAM) memory usage of all sectors in this entity.
        /// </summary>
        /// <returns>Total VRAM usage in kilobytes.</returns>
        public ulong GetGPUMemoryUsageKB()
        {
            ulong result = 0;
            foreach (var s in Voxels.Values)
            {
                result += (ulong)(s.VRAMUsage / 1024);
            }

            return result;
        }

        /// <summary>
        /// Gets the block at the specified world position.
        /// </summary>
        /// <param name="pos">World position of the block in block coordinates.</param>
        /// <returns>The block at the specified position, or Block.Empty if no sector exists at that location.</returns>
        public Block GetBlock(Vector3Int pos)
        {
            Vector3Int sectorPos = new Vector3Int(
                pos.x >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.y >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.z >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS));

            var found = Voxels.TryGetValue(sectorPos, out SectorRef sector);
            if (!found)
            {
                return Block.Empty;
            }

            return sector.sector.GetBlock(
                pos.x & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.y & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.z & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS))
            );
        }

        /// <summary>
        /// Sets the block at the specified world position. Creates a new sector if one doesn't exist.
        /// </summary>
        /// <param name="pos">World position of the block in block coordinates.</param>
        /// <param name="b">The block data to set.</param>
        /// <remarks>
        /// If the sector doesn't exist at the calculated sector position, a new sector will be automatically created.
        /// The method also triggers a physics body update (marked as temporary and should be removed).
        /// </remarks>
        public void SetBlock(Vector3Int pos, Block b)
        {
            Vector3Int sectorPos = new Vector3Int(
                pos.x >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.y >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.z >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS));

            var found = Voxels.TryGetValue(sectorPos, out SectorRef sector);
            if (!found)
            {
                sector = new SectorRef(
                    this,
                    Sector.New(Allocator.Persistent, 1),
                    sectorPos);
                Voxels.Add(sectorPos, sector);
            }

            sector.sector.SetBlock(
                pos.x & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.y & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.z & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                b
            );
        }

        /// <summary>
        /// Gets the object-to-world transformation matrix for this entity.
        /// </summary>
        /// <returns>The local-to-world matrix of this entity's transform.</returns>
        public float4x4 ObjectToWorld()
        {
            return transform.localToWorldMatrix;
        }

        /// <summary>
        /// Gets the world-to-object transformation matrix for this entity.
        /// </summary>
        /// <returns>The world-to-local matrix of this entity's transform.</returns>
        public float4x4 WorldToObject()
        {
            return transform.worldToLocalMatrix;
        }
    }
}
