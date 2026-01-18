using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.DirtyPropagation
{
    /// <summary>
    /// Unit tests for DirtyPropagationJob.
    /// Tests dirty flag propagation across all spatial configurations including
    /// single sector, cross-sector boundaries (faces, edges, corners), and edge cases.
    /// </summary>
    [TestFixture]
    public class DirtyPropagationJobTests
    {
        #region Single Sector Propagation Tests

        [Test]
        public void Job_SingleSector_CenterBrick_PropagatesTo26Neighbors()
        {
            // Arrange - Create a single sector with a dirty brick at the center
            var sector = SectorHandle.AllocEmpty();
            var neighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                // Mark the center brick (8,8,8) as dirty
                int centerBrickIdx = Sector.ToBrickIdx(8, 8, 8);
                sector.Ptr->MarkBrickDirty(centerBrickIdx, DirtyFlags.Reserved0);

                // Set up job data
                var sectorPos = new int3(0, 0, 0);
                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, sector);
                neighborsMap.Add(sectorPos, neighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - Check that all 26 neighbors have the dirty flag propagated
                int propagatedCount = 0;
                for (int dir = 0; dir < NeighborhoodSettings.neighborhoodCount; dir++)
                {
                    int3 neighborBrickPos = new int3(8, 8, 8) + NeighborhoodSettings.Directions[dir];
                    int neighborBrickIdx = Sector.ToBrickIdx(neighborBrickPos.x, neighborBrickPos.y, neighborBrickPos.z);

                    unsafe
                    {
                        if ((sector.Ptr->brickRequireUpdateFlags[neighborBrickIdx] & (ushort)DirtyFlags.Reserved0) != 0)
                        {
                            propagatedCount++;
                        }
                    }
                }

                Assert.AreEqual(26, propagatedCount, "All 26 neighbors should receive dirty flag");

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                sector.Dispose(Allocator.Persistent);
                neighbors.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void Job_SingleSector_MultipleDirtyBricks_PropagateCorrectly()
        {
            // Arrange - Mark multiple bricks as dirty
            var sector = SectorHandle.AllocEmpty();
            var neighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                // Mark bricks at (5,5,5) and (10,10,10) as dirty
                sector.Ptr->MarkBrickDirty(Sector.ToBrickIdx(5, 5, 5), DirtyFlags.Reserved0);
                sector.Ptr->MarkBrickDirty(Sector.ToBrickIdx(10, 10, 10), DirtyFlags.Reserved1);

                var sectorPos = new int3(0, 0, 0);
                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, sector);
                neighborsMap.Add(sectorPos, neighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0 | DirtyFlags.Reserved1
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - Check specific neighbors
                unsafe
                {
                    // Neighbor of (5,5,5) in +X direction should have Reserved0
                    int neighbor1Idx = Sector.ToBrickIdx(6, 5, 5);
                    Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[neighbor1Idx] & (ushort)DirtyFlags.Reserved0,
                        "Neighbor of first brick should have Reserved0");

                    // Neighbor of (10,10,10) in +Y direction should have Reserved1
                    int neighbor2Idx = Sector.ToBrickIdx(10, 11, 10);
                    Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[neighbor2Idx] & (ushort)DirtyFlags.Reserved1,
                        "Neighbor of second brick should have Reserved1");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                sector.Dispose(Allocator.Persistent);
                neighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Cross-Sector Face Boundary Tests (6 Directions)

        [Test]
        public void Job_CrossSectorBoundary_AllSixFaces_PropagateCorrectly()
        {
            // Test all 6 face directions: +X, -X, +Y, -Y, +Z, -Z
            for (int faceIdx = 0; faceIdx < 6; faceIdx++)
            {
                TestCrossSectorPropagation(faceIdx, $"Face {faceIdx}");
            }
        }

        private void TestCrossSectorPropagation(int neighborIdx, string testName)
        {
            // Arrange - Create center sector and one neighbor
            var centerSector = SectorHandle.AllocEmpty();
            var neighborSector = SectorHandle.AllocEmpty();
            var centerNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                unsafe
                {
                    centerNeighbors.Neighbors[neighborIdx] = neighborSector;
                }

                // Get the direction for this neighbor
                int3 direction = NeighborhoodSettings.Directions[neighborIdx];

                // Mark an edge brick in the neighbor sector as dirty
                // Calculate which edge brick is closest to center sector
                int3 edgeBrickPos = new int3(
                    direction.x == -1 ? 15 : 0,  // If neighbor is in -X, use last brick (15), else first (0)
                    direction.y == -1 ? 15 : 0,
                    direction.z == -1 ? 15 : 0
                );

                int edgeBrickIdx = Sector.ToBrickIdx(edgeBrickPos.x, edgeBrickPos.y, edgeBrickPos.z);
                neighborSector.Ptr->MarkBrickDirty(edgeBrickIdx, DirtyFlags.Reserved0);

                // Set up job
                var centerPos = new int3(0, 0, 0);
                var neighborPos = centerPos + direction;

                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(2, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = centerPos;
                sectorsMap.Add(centerPos, centerSector);
                sectorsMap.Add(neighborPos, neighborSector);
                neighborsMap.Add(centerPos, centerNeighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - Check that the corresponding edge brick in center sector got the flag
                int3 centerEdgeBrickPos = new int3(
                    direction.x == 1 ? 15 : 0,  // If neighbor is in +X, center edge is at 15
                    direction.y == 1 ? 15 : 0,
                    direction.z == 1 ? 15 : 0
                );

                int centerEdgeBrickIdx = Sector.ToBrickIdx(centerEdgeBrickPos.x, centerEdgeBrickPos.y, centerEdgeBrickPos.z);

                unsafe
                {
                    var propagatedFlags = centerSector.Ptr->brickRequireUpdateFlags[centerEdgeBrickIdx];
                    Assert.AreNotEqual(0, propagatedFlags & (ushort)DirtyFlags.Reserved0,
                        $"{testName}: Edge brick in center sector should receive propagated flag");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                centerSector.Dispose(Allocator.Persistent);
                neighborSector.Dispose(Allocator.Persistent);
                centerNeighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Cross-Sector Edge Boundary Tests (12 Directions)

        [Test]
        public void Job_CrossSectorBoundary_AllEdgeDirections_PropagateCorrectly()
        {
            // Test edge directions (combinations of 2 axes)
            // These are directions 6-17 in the Moore neighborhood
            for (int edgeIdx = 6; edgeIdx < 18; edgeIdx++)
            {
                TestCrossSectorPropagation(edgeIdx, $"Edge {edgeIdx}");
            }
        }

        #endregion

        #region Cross-Sector Corner Boundary Tests (8 Directions)

        [Test]
        public void Job_CrossSectorBoundary_AllCornerDirections_PropagateCorrectly()
        {
            // Test corner directions (all 3 axes non-zero)
            // These are directions 18-25 in the Moore neighborhood
            for (int cornerIdx = 18; cornerIdx < 26; cornerIdx++)
            {
                TestCrossSectorPropagation(cornerIdx, $"Corner {cornerIdx}");
            }
        }

        #endregion

        #region Multiple Sectors Grid Tests

        [Test]
        public void Job_ThreeSectorsInLine_PropagatesAcrossMultipleBoundaries()
        {
            // Arrange - Create 3 sectors in a line: Left, Center, Right
            var leftSector = SectorHandle.AllocEmpty();
            var centerSector = SectorHandle.AllocEmpty();
            var rightSector = SectorHandle.AllocEmpty();

            var leftNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);
            var centerNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);
            var rightNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                unsafe
                {
                    // Connect neighbors
                    leftNeighbors.Neighbors[0] = centerSector;   // Left's right neighbor is center
                    centerNeighbors.Neighbors[1] = leftSector;   // Center's left neighbor is left
                    centerNeighbors.Neighbors[0] = rightSector;  // Center's right neighbor is right
                    rightNeighbors.Neighbors[1] = centerSector;  // Right's left neighbor is center

                    // Mark a brick in the left sector as dirty
                    int leftBrickIdx = Sector.ToBrickIdx(15, 8, 8); // Right edge of left sector
                    leftSector.Ptr->MarkBrickDirty(leftBrickIdx, DirtyFlags.Reserved0);
                }

                // Set up job for all 3 sectors
                var positions = new NativeArray<int3>(3, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(3, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(3, Allocator.TempJob);

                positions[0] = new int3(-1, 0, 0); // Left
                positions[1] = new int3(0, 0, 0);  // Center
                positions[2] = new int3(1, 0, 0);  // Right

                sectorsMap.Add(positions[0], leftSector);
                sectorsMap.Add(positions[1], centerSector);
                sectorsMap.Add(positions[2], rightSector);

                neighborsMap.Add(positions[0], leftNeighbors);
                neighborsMap.Add(positions[1], centerNeighbors);
                neighborsMap.Add(positions[2], rightNeighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(3, 1).Complete();

                // Assert - Check that center sector's left edge brick received the flag
                unsafe
                {
                    int centerLeftEdgeBrickIdx = Sector.ToBrickIdx(0, 8, 8);
                    Assert.AreNotEqual(0, centerSector.Ptr->brickRequireUpdateFlags[centerLeftEdgeBrickIdx] & (ushort)DirtyFlags.Reserved0,
                        "Center sector should receive flag from left sector");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                leftSector.Dispose(Allocator.Persistent);
                centerSector.Dispose(Allocator.Persistent);
                rightSector.Dispose(Allocator.Persistent);
                leftNeighbors.Dispose(Allocator.Persistent);
                centerNeighbors.Dispose(Allocator.Persistent);
                rightNeighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Early Exit and Optimization Tests

        [Test]
        public void Job_NoDirectyFlags_EarlyExit_NoChanges()
        {
            // Arrange - Create a sector with no dirty flags
            var sector = SectorHandle.AllocEmpty();
            var neighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                var sectorPos = new int3(0, 0, 0);
                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, sector);
                neighborsMap.Add(sectorPos, neighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - No flags should be set
                unsafe
                {
                    Assert.AreEqual(0, sector.Ptr->sectorRequireUpdateFlags,
                        "Sector require update flags should remain 0 with no dirty input");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                sector.Dispose(Allocator.Persistent);
                neighbors.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void Job_OnlyNeighborDirty_StillPropagates()
        {
            // Arrange - Center is clean, but neighbor has dirty flags
            var centerSector = SectorHandle.AllocEmpty();
            var rightSector = SectorHandle.AllocEmpty();
            var centerNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                unsafe
                {
                    centerNeighbors.Neighbors[0] = rightSector; // Right neighbor

                    // Mark right neighbor's left edge brick as dirty
                    int rightEdgeBrickIdx = Sector.ToBrickIdx(0, 8, 8);
                    rightSector.Ptr->MarkBrickDirty(rightEdgeBrickIdx, DirtyFlags.Reserved0);
                }

                var centerPos = new int3(0, 0, 0);
                var rightPos = new int3(1, 0, 0);

                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(2, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = centerPos;
                sectorsMap.Add(centerPos, centerSector);
                sectorsMap.Add(rightPos, rightSector);
                neighborsMap.Add(centerPos, centerNeighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - Center's right edge should get the flag
                unsafe
                {
                    int centerRightEdgeBrickIdx = Sector.ToBrickIdx(15, 8, 8);
                    Assert.AreNotEqual(0, centerSector.Ptr->brickRequireUpdateFlags[centerRightEdgeBrickIdx] & (ushort)DirtyFlags.Reserved0,
                        "Center should receive flag from dirty neighbor even when center itself isn't dirty");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                centerSector.Dispose(Allocator.Persistent);
                rightSector.Dispose(Allocator.Persistent);
                centerNeighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Null/Missing Neighbor Tests

        [Test]
        public void Job_MissingNeighbor_DoesNotCrash()
        {
            // Arrange - Sector with some neighbors missing
            var sector = SectorHandle.AllocEmpty();
            var neighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                // Mark an edge brick as dirty (which would propagate to missing neighbor)
                int edgeBrickIdx = Sector.ToBrickIdx(15, 8, 8);
                sector.Ptr->MarkBrickDirty(edgeBrickIdx, DirtyFlags.Reserved0);

                var sectorPos = new int3(0, 0, 0);
                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, sector);
                neighborsMap.Add(sectorPos, neighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act - Should not crash
                Assert.DoesNotThrow(() => job.Schedule(1, 1).Complete(),
                    "Job should handle missing neighbors without crashing");

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                sector.Dispose(Allocator.Persistent);
                neighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Flag Filtering Tests

        [Test]
        public void Job_FlagFiltering_OnlyPropagatesSpecifiedFlags()
        {
            // Arrange
            var sector = SectorHandle.AllocEmpty();
            var neighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                // Mark a brick with multiple flags
                int brickIdx = Sector.ToBrickIdx(8, 8, 8);
                sector.Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved0 | DirtyFlags.Reserved1 | DirtyFlags.Reserved2);

                var sectorPos = new int3(0, 0, 0);
                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, sector);
                neighborsMap.Add(sectorPos, neighbors);

                // Only propagate Reserved0
                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - Neighbors should only have Reserved0, not Reserved1 or Reserved2
                unsafe
                {
                    int neighborBrickIdx = Sector.ToBrickIdx(9, 8, 8); // +X neighbor
                    var flags = sector.Ptr->brickRequireUpdateFlags[neighborBrickIdx];

                    Assert.AreNotEqual(0, flags & (ushort)DirtyFlags.Reserved0,
                        "Reserved0 should be propagated");
                    Assert.AreEqual(0, flags & (ushort)DirtyFlags.Reserved1,
                        "Reserved1 should NOT be propagated");
                    Assert.AreEqual(0, flags & (ushort)DirtyFlags.Reserved2,
                        "Reserved2 should NOT be propagated");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                sector.Dispose(Allocator.Persistent);
                neighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Brick Self-Propagation Tests

        [Test]
        public void Job_BrickPropagation_IncludesSelfDirtyFlags()
        {
            // Arrange - A brick that is dirty should include its own flags in requireUpdate
            var sector = SectorHandle.AllocEmpty();
            var neighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                // Mark a brick as dirty
                int brickIdx = Sector.ToBrickIdx(5, 5, 5);
                sector.Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved0);

                var sectorPos = new int3(0, 0, 0);
                var positions = new NativeArray<int3>(1, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(1, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(1, Allocator.TempJob);

                positions[0] = sectorPos;
                sectorsMap.Add(sectorPos, sector);
                neighborsMap.Add(sectorPos, neighbors);

                var job = new DirtyPropagationJob
                {
                    allSectorPositions = positions,
                    sectors = sectorsMap,
                    sectorNeighbors = neighborsMap,
                    neighborhoodType = NeighborhoodType.Moore,
                    flagsToPropagate = DirtyFlags.Reserved0
                };

                // Act
                job.Schedule(1, 1).Complete();

                // Assert - The brick itself should have the flag in requireUpdate
                unsafe
                {
                    Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[brickIdx] & (ushort)DirtyFlags.Reserved0,
                        "Brick should include its own dirty flags in requireUpdate");
                }

                // Cleanup
                positions.Dispose();
                sectorsMap.Dispose();
                neighborsMap.Dispose();
            }
            finally
            {
                sector.Dispose(Allocator.Persistent);
                neighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion
    }
}
