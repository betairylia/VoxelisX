using Unity.Mathematics;
using UnityEngine;

namespace VoxelisX
{
    /// <summary>
    /// Helper class that wraps a sector and its neighbors, providing convenient access
    /// to blocks across sector boundaries. Automatically handles coordinate translation
    /// when accessing blocks in neighboring sectors.
    /// </summary>
    /// <remarks>
    /// Example: Accessing block at (-1, -1, -1) will automatically fetch from the
    /// appropriate neighboring sector at position (127, 127, 127).
    /// </remarks>
    public unsafe struct SectorNeighborhoodHelper
    {
        private SectorHandle centerSector;
        private SectorNeighborHandles neighbors;

        /// <summary>
        /// Creates a new neighborhood helper for the given sector and its neighbors.
        /// </summary>
        /// <param name="center">The central sector</param>
        /// <param name="neighbors">The neighboring sectors</param>
        public SectorNeighborhoodHelper(SectorHandle center, SectorNeighborHandles neighbors)
        {
            this.centerSector = center;
            this.neighbors = neighbors;
        }

        /// <summary>
        /// Gets the center sector handle.
        /// </summary>
        public SectorHandle Center => centerSector;

        /// <summary>
        /// Gets the neighbor handles structure.
        /// </summary>
        public SectorNeighborHandles Neighbors => neighbors;

        /// <summary>
        /// Gets a block at the specified sector-local coordinates, automatically
        /// accessing neighboring sectors if coordinates fall outside [0, 127].
        /// </summary>
        /// <param name="x">X coordinate (can be negative or >= 128)</param>
        /// <param name="y">Y coordinate (can be negative or >= 128)</param>
        /// <param name="z">Z coordinate (can be negative or >= 128)</param>
        /// <returns>The block at the specified position, or Block.Empty if the neighbor doesn't exist</returns>
        public Block GetBlock(int x, int y, int z)
        {
            // Fast path: coordinates within center sector bounds
            if (x >= 0 && x < Sector.SECTOR_SIZE_IN_BLOCKS &&
                y >= 0 && y < Sector.SECTOR_SIZE_IN_BLOCKS &&
                z >= 0 && z < Sector.SECTOR_SIZE_IN_BLOCKS)
            {
                return centerSector.GetBlock(x, y, z);
            }

            // Calculate which neighbor sector to access
            int3 sectorOffset = new int3(
                x < 0 ? -1 : (x >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0),
                y < 0 ? -1 : (y >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0),
                z < 0 ? -1 : (z >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0)
            );

            // Find the neighbor index for this offset
            int neighborIdx = FindNeighborIndex(sectorOffset);
            if (neighborIdx < 0 || !neighbors.Neighbors[neighborIdx].IsValid)
            {
                return Block.Empty;
            }

            // Transform coordinates to neighbor's local space
            int localX = ModuloWrap(x, Sector.SECTOR_SIZE_IN_BLOCKS);
            int localY = ModuloWrap(y, Sector.SECTOR_SIZE_IN_BLOCKS);
            int localZ = ModuloWrap(z, Sector.SECTOR_SIZE_IN_BLOCKS);

            return neighbors.Neighbors[neighborIdx].GetBlock(localX, localY, localZ);
        }

        /// <summary>
        /// Gets a block at the specified sector-local coordinates.
        /// </summary>
        public Block GetBlock(int3 pos) => GetBlock(pos.x, pos.y, pos.z);

        /// <summary>
        /// Tests if a block exists (is non-empty) at the specified coordinates.
        /// Automatically handles neighbor access for out-of-bounds coordinates.
        /// </summary>
        /// <param name="x">X coordinate (can be negative or >= 128)</param>
        /// <param name="y">Y coordinate (can be negative or >= 128)</param>
        /// <param name="z">Z coordinate (can be negative or >= 128)</param>
        /// <returns>True if the block is non-empty, false otherwise</returns>
        public bool BlockTest(int x, int y, int z)
        {
            return !GetBlock(x, y, z).isEmpty;
        }

        /// <summary>
        /// Tests if a block exists (is non-empty) at the specified coordinates.
        /// </summary>
        public bool BlockTest(int3 pos) => BlockTest(pos.x, pos.y, pos.z);

        /// <summary>
        /// Gets a pointer to the brick containing the specified block coordinates.
        /// Automatically accesses neighboring sectors if coordinates fall outside bounds.
        /// </summary>
        /// <param name="x">X coordinate (can be negative or >= 128)</param>
        /// <param name="y">Y coordinate (can be negative or >= 128)</param>
        /// <param name="z">Z coordinate (can be negative or >= 128)</param>
        /// <returns>Pointer to the brick data, or null if brick doesn't exist or neighbor is invalid</returns>
        public Block* GetBrick(int x, int y, int z)
        {
            // Fast path: coordinates within center sector bounds
            if (x >= 0 && x < Sector.SECTOR_SIZE_IN_BLOCKS &&
                y >= 0 && y < Sector.SECTOR_SIZE_IN_BLOCKS &&
                z >= 0 && z < Sector.SECTOR_SIZE_IN_BLOCKS)
            {
                return centerSector.GetBrick(x, y, z);
            }

            // Calculate which neighbor sector to access
            int3 sectorOffset = new int3(
                x < 0 ? -1 : (x >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0),
                y < 0 ? -1 : (y >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0),
                z < 0 ? -1 : (z >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0)
            );

            // Find the neighbor index for this offset
            int neighborIdx = FindNeighborIndex(sectorOffset);
            if (neighborIdx < 0 || !neighbors.Neighbors[neighborIdx].IsValid)
            {
                return null;
            }

            // Transform coordinates to neighbor's local space
            int localX = ModuloWrap(x, Sector.SECTOR_SIZE_IN_BLOCKS);
            int localY = ModuloWrap(y, Sector.SECTOR_SIZE_IN_BLOCKS);
            int localZ = ModuloWrap(z, Sector.SECTOR_SIZE_IN_BLOCKS);

            return neighbors.Neighbors[neighborIdx].GetBrick(localX, localY, localZ);
        }

        /// <summary>
        /// Gets a pointer to the brick containing the specified block coordinates.
        /// </summary>
        public Block* GetBrick(int3 pos) => GetBrick(pos.x, pos.y, pos.z);

        /// <summary>
        /// Sets a block at the specified sector-local coordinates.
        /// Automatically accesses neighboring sectors if coordinates fall outside bounds.
        /// </summary>
        /// <param name="x">X coordinate (can be negative or >= 128)</param>
        /// <param name="y">Y coordinate (can be negative or >= 128)</param>
        /// <param name="z">Z coordinate (can be negative or >= 128)</param>
        /// <param name="block">The block to set</param>
        /// <returns>True if the block was set successfully, false if neighbor doesn't exist</returns>
        public bool SetBlock(int x, int y, int z, Block block)
        {
            // Fast path: coordinates within center sector bounds
            if (x >= 0 && x < Sector.SECTOR_SIZE_IN_BLOCKS &&
                y >= 0 && y < Sector.SECTOR_SIZE_IN_BLOCKS &&
                z >= 0 && z < Sector.SECTOR_SIZE_IN_BLOCKS)
            {
                centerSector.SetBlock(x, y, z, block);
                return true;
            }

            // Calculate which neighbor sector to access
            int3 sectorOffset = new int3(
                x < 0 ? -1 : (x >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0),
                y < 0 ? -1 : (y >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0),
                z < 0 ? -1 : (z >= Sector.SECTOR_SIZE_IN_BLOCKS ? 1 : 0)
            );

            // Find the neighbor index for this offset
            int neighborIdx = FindNeighborIndex(sectorOffset);
            if (neighborIdx < 0 || !neighbors.Neighbors[neighborIdx].IsValid)
            {
                return false;
            }

            // Transform coordinates to neighbor's local space
            int localX = ModuloWrap(x, Sector.SECTOR_SIZE_IN_BLOCKS);
            int localY = ModuloWrap(y, Sector.SECTOR_SIZE_IN_BLOCKS);
            int localZ = ModuloWrap(z, Sector.SECTOR_SIZE_IN_BLOCKS);

            neighbors.Neighbors[neighborIdx].SetBlock(localX, localY, localZ, block);
            return true;
        }

        /// <summary>
        /// Sets a block at the specified sector-local coordinates.
        /// </summary>
        public bool SetBlock(int3 pos, Block block) => SetBlock(pos.x, pos.y, pos.z, block);

        /// <summary>
        /// Checks if a specific neighbor exists and is valid.
        /// </summary>
        /// <param name="offset">The neighbor offset (-1, 0, or 1 for each axis)</param>
        /// <returns>True if the neighbor exists, false otherwise</returns>
        public bool HasNeighbor(int3 offset)
        {
            if (offset.x == 0 && offset.y == 0 && offset.z == 0)
            {
                return centerSector.IsValid;
            }

            int neighborIdx = FindNeighborIndex(offset);
            return neighborIdx >= 0 && neighbors.Neighbors[neighborIdx].IsValid;
        }

        /// <summary>
        /// Gets a specific neighbor sector handle.
        /// </summary>
        /// <param name="offset">The neighbor offset (-1, 0, or 1 for each axis)</param>
        /// <returns>The neighbor sector handle, or an invalid handle if not found</returns>
        public SectorHandle GetNeighbor(int3 offset)
        {
            if (offset.x == 0 && offset.y == 0 && offset.z == 0)
            {
                return centerSector;
            }

            int neighborIdx = FindNeighborIndex(offset);
            if (neighborIdx >= 0)
            {
                return neighbors.Neighbors[neighborIdx];
            }

            return default; // Invalid handle
        }

        /// <summary>
        /// Finds the neighbor array index for a given sector offset.
        /// </summary>
        /// <param name="offset">The sector offset (-1, 0, or 1 for each axis)</param>
        /// <returns>The neighbor index, or -1 if not found</returns>
        private int FindNeighborIndex(int3 offset)
        {
            for (int i = 0; i < SectorNeighborHandles.Directions.Length; i++)
            {
                if (SectorNeighborHandles.Directions[i].Equals(offset))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Properly wraps a coordinate into the range [0, size) using modulo arithmetic.
        /// Handles negative values correctly.
        /// </summary>
        /// <param name="value">The value to wrap</param>
        /// <param name="size">The size of the range</param>
        /// <returns>The wrapped value in [0, size)</returns>
        private static int ModuloWrap(int value, int size)
        {
            return ((value % size) + size) % size;
        }
    }
}
