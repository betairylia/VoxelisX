using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.TestHelpers
{
    /// <summary>
    /// Fluent builder for setting up dirty propagation tests with automatic cleanup.
    /// Provides a simple API for creating sectors, setting blocks, and running propagation.
    /// </summary>
    public class PropagationTestBuilder : IDisposable
    {
        private SectorHandle centerSector;
        private SectorNeighborHandles neighbors;
        private Dictionary<int3, SectorHandle> additionalSectors = new Dictionary<int3, SectorHandle>();
        private bool disposed = false;

        private PropagationTestBuilder()
        {
            centerSector = SectorHandle.AllocEmpty();
            neighbors = SectorNeighborHandles.Create(Allocator.TempJob);
        }

        /// <summary>
        /// Creates a new propagation test builder.
        /// </summary>
        public static PropagationTestBuilder Create()
        {
            return new PropagationTestBuilder();
        }

        /// <summary>
        /// Sets a block at the specified position in the center sector.
        /// This automatically calculates boundary masks based on block position within brick.
        /// </summary>
        public PropagationTestBuilder WithBlock(int x, int y, int z, ushort blockId = 1)
        {
            centerSector.SetBlock(x, y, z, new Block(blockId));
            return this;
        }

        /// <summary>
        /// Sets a block at the specified position in the center sector with custom block data.
        /// </summary>
        public PropagationTestBuilder WithBlock(int x, int y, int z, Block block)
        {
            centerSector.SetBlock(x, y, z, block);
            return this;
        }

        /// <summary>
        /// Marks a brick as dirty with specific flags and direction mask.
        /// Use this for testing specific direction propagation scenarios.
        /// </summary>
        public PropagationTestBuilder MarkBrickDirty(int brickIdx, DirtyFlags flags, uint directionMask = 0xFFFFFFFF)
        {
            centerSector.Ptr->MarkBrickDirty(brickIdx, flags, directionMask);
            return this;
        }

        /// <summary>
        /// Adds a neighbor sector in the specified direction.
        /// </summary>
        public PropagationTestBuilder WithNeighbor(int directionIndex, SectorHandle neighborSector = default)
        {
            if (!neighborSector.IsValid)
            {
                neighborSector = SectorHandle.AllocEmpty();
                int3 neighborOffset = NeighborhoodSettings.Directions[directionIndex];
                additionalSectors[neighborOffset] = neighborSector;
            }

            unsafe
            {
                neighbors.Neighbors[directionIndex] = neighborSector;
            }

            return this;
        }

        /// <summary>
        /// Sets a block in a neighbor sector.
        /// </summary>
        public PropagationTestBuilder WithBlockInNeighbor(int directionIndex, int x, int y, int z, ushort blockId = 1)
        {
            unsafe
            {
                var neighbor = neighbors.Neighbors[directionIndex];
                if (!neighbor.IsValid)
                {
                    // Auto-create neighbor if it doesn't exist
                    WithNeighbor(directionIndex);
                    neighbor = neighbors.Neighbors[directionIndex];
                }

                neighbor.SetBlock(x, y, z, new Block(blockId));
            }

            return this;
        }

        /// <summary>
        /// Runs the dirty propagation job and returns this builder for chaining.
        /// </summary>
        public PropagationTestBuilder RunPropagation(DirtyFlags flagsToPropagate = DirtyFlags.Reserved0)
        {
            var sectorPos = new int3(0, 0, 0);
            var positions = new NativeArray<int3>(1, Allocator.TempJob);
            var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
            var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

            try
            {
                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, centerSector);
                neighborsMap.Add(sectorPos, neighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = flagsToPropagate
                };

                job.Schedule(1, 1).Complete();
            }
            finally
            {
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }

            return this;
        }

        /// <summary>
        /// Gets the center sector for manual verification.
        /// </summary>
        public SectorHandle GetCenterSector() => centerSector;

        /// <summary>
        /// Gets a neighbor sector by direction index.
        /// </summary>
        public SectorHandle GetNeighbor(int directionIndex)
        {
            unsafe
            {
                return neighbors.Neighbors[directionIndex];
            }
        }

        /// <summary>
        /// Asserts that a brick has specific dirty flags in requireUpdate buffer.
        /// </summary>
        public PropagationTestBuilder AssertBrickRequiresUpdate(int brickX, int brickY, int brickZ, DirtyFlags expected)
        {
            int brickIdx = Sector.ToBrickIdx(brickX, brickY, brickZ);
            unsafe
            {
                var actual = centerSector.Ptr->brickRequireUpdateFlags[brickIdx];
                if ((actual & (ushort)expected) == 0)
                {
                    throw new Exception($"Brick ({brickX},{brickY},{brickZ}) expected flags {expected} but got {actual}");
                }
            }
            return this;
        }

        /// <summary>
        /// Asserts that a brick has specific direction mask.
        /// </summary>
        public PropagationTestBuilder AssertBrickDirectionMask(int brickIdx, uint expectedMask)
        {
            unsafe
            {
                var actual = centerSector.Ptr->brickDirtyDirectionMask[brickIdx];
                if (actual != expectedMask)
                {
                    throw new Exception($"Brick {brickIdx} expected mask {expectedMask:X8} but got {actual:X8}");
                }
            }
            return this;
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (centerSector.IsValid)
                    centerSector.Dispose(Allocator.Persistent);

                foreach (var kvp in additionalSectors)
                {
                    if (kvp.Value.IsValid)
                        kvp.Value.Dispose(Allocator.Persistent);
                }

                neighbors.Dispose(Allocator.Persistent);
                disposed = true;
            }
        }
    }
}
