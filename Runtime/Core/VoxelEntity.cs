using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections.NotBurstCompatible;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.VisualScripting.YamlDotNet.Core.Tokens;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Voxelis
{
    public unsafe struct VoxelEntityData
    {
        /// <summary>
        /// Dictionary mapping sector positions to their corresponding sector data.
        /// Key is the sector position in sector-space coordinates.
        /// </summary>
        // public Dictionary<Vector3Int, Sector> sectors = new Dictionary<Vector3Int, Sector>();
        public NativeHashMap<int3, SectorHandle> sectors;
        public NativeQueue<int3> sectorsToRemove;
        
        public void AddSectorAt(int3 pos, SectorHandle sector)
        {
            sectors.Add(pos, sector);
        }
    }
    
    /// <summary>
    /// Represents a voxel-based entity in the game world. This is the main component for managing
    /// collections of voxel sectors and interfacing with the physics system.
    /// </summary>
    /// <remarks>
    /// VoxelEntity organizes voxels into sectors for efficient storage. Each sector
    /// contains a fixed-size grid of bricks, which in turn contain individual voxel blocks.
    /// Rendering is managed separately by VoxelisXRenderer.
    /// </remarks>
    public unsafe partial class VoxelEntity : MonoBehaviour, IDisposable
    {
        private VoxelEntityData data;
        public NativeHashMap<int3, SectorHandle> Sectors => data.sectors;

        public unsafe void AddEmptySectorAt(Vector3Int pos)
        {
            // Store the handle
            Sectors.Add(new int3(pos.x, pos.y, pos.z), SectorHandle.AllocEmpty());
        }

        public unsafe void CopyAndAddSectorAt(Vector3Int pos, Sector sector)
        {
            // Allocate memory for the Sector struct itself
            Sector* sectorPtr = (Sector*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<Sector>(),
                UnsafeUtility.AlignOf<Sector>(),
                Allocator.Persistent);

            // Initialize the sector in-place
            *sectorPtr = sector;

            Sectors.Add(new int3(pos.x, pos.y, pos.z), new SectorHandle(sectorPtr));
        }

        public void AddSectorAt(Vector3Int pos, SectorHandle sector)
        {
            Sectors.Add(new int3(pos.x, pos.y, pos.z), sector);
        }

        /// <summary>
        /// Removes and disposes a sector at the specified position.
        /// </summary>
        /// <param name="pos">The sector position to remove.</param>
        /// <returns>True if the sector was found and removed, false otherwise.</returns>
        public bool RemoveSectorAt(Vector3Int pos)
        {
            if (!Sectors.ContainsKey(pos))
            {
                return false;
            }

            Sectors[pos].Dispose(Allocator.Persistent);
            Sectors.Remove(pos);
            return true;
        }

        /// <summary>
        /// Queue of sector positions that are scheduled for removal and cleanup.
        /// Used to defer disposal of sectors until after rendering is complete.
        /// </summary>
        public Queue<Vector3Int> sectorsToRemove = new Queue<Vector3Int>();

        /// <summary>
        /// Called when the component is enabled. Registers this entity with the renderer and initializes physics body.
        /// </summary>
        private void OnEnable()
        {
            VoxelisXWorld.instance.AddEntity(this);
        }

        /// <summary>
        /// Called when the component is disabled. Unregisters this entity from the renderer.
        /// </summary>
        private void OnDisable()
        {
            VoxelisXWorld.instance.RemoveEntity(this);
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
            foreach (var kvp in Sectors)
            {
                Sector* sectorPtr = kvp.Value.Ptr;

                // First dispose the sector's internal allocations
                sectorPtr->Dispose(Allocator.Persistent);

                // Then free the Sector struct memory itself
                UnsafeUtility.Free(sectorPtr, Allocator.Persistent);
            }

            // Clear and dispose the hashmap
            Sectors.Clear();
        }

        /// <summary>
        /// Calculates the total host (CPU) memory usage of all sectors in this entity.
        /// </summary>
        /// <returns>Total memory usage in kilobytes.</returns>
        public ulong GetHostMemoryUsageKB()
        {
            ulong result = 0;
            foreach (var kvp in Sectors)
            {
                result += (ulong)(kvp.Value.Get().MemoryUsage / 1024);
            }

            return result;
        }

        /// <summary>
        /// Helper to get sector block position from sector position.
        /// </summary>
        public static Vector3Int GetSectorBlockPos(Vector3Int sectorPos)
        {
            return sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
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

            if (!Sectors.ContainsKey(sectorPos))
            {
                return Block.Empty;
            }

            return Sectors[sectorPos].GetBlock(
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
        /// </remarks>
        public void SetBlock(Vector3Int pos, Block b)
        {
            Vector3Int sectorPos = new Vector3Int(
                pos.x >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.y >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.z >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS));

            if (!Sectors.ContainsKey(sectorPos))
            {
                AddEmptySectorAt(sectorPos);
            }

            // Modify sector directly in dictionary
            Sectors[sectorPos].SetBlock(
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
