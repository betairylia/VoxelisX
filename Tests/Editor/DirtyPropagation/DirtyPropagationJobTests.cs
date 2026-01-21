using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Tests.TestHelpers;

namespace VoxelisX.Tests.DirtyPropagation
{
    /// <summary>
    /// Unit tests for DirtyPropagationJob with boundary mask support.
    /// Tests use SetBlock() to trigger proper boundary mask calculation based on voxel positions.
    /// Focuses on real-world scenarios where blocks at brick/sector boundaries propagate dirty flags.
    /// </summary>
    [TestFixture]
    public unsafe class DirtyPropagationJobTests
    {
        #region Single Sector Tests

        [Test]
        public void Propagation_BlockAtBrickCorner_PropagesToNeighbors()
        {
            // Block at brick corner (0,0,0) should propagate to several neighbor bricks
            using (var test = PropagationTestBuilder.Create())
            {
                // Set block at origin (corner of brick 0,0,0)
                test.WithBlock(0, 0, 0, blockId: 100)
                    .RunPropagation();

                var sector = test.GetCenterSector();

                // The brick containing the block should be marked for update
                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[brickIdx],
                    "Brick containing dirty block should require update");
            }
        }

        [Test]
        public void Propagation_BlockAtBrickEdge_PropagatesAcrossBrickBoundary()
        {
            // Block at edge of brick (7,7,7) should propagate to adjacent brick (1,1,1)
            using (var test = PropagationTestBuilder.Create())
            {
                // Set block at the far corner of brick (0,0,0), which borders brick (1,1,1)
                test.WithBlock(7, 7, 7, blockId: 100)
                    .RunPropagation();

                var sector = test.GetCenterSector();

                // Adjacent brick should receive propagated flags
                int adjacentBrickIdx = Sector.ToBrickIdx(1, 1, 1);
                Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[adjacentBrickIdx],
                    "Adjacent brick should receive propagated flags");
            }
        }

        [Test]
        public void Propagation_NoDirectionMask_DoesNotPropagate()
        {
            // If we manually mark a brick dirty with zero direction mask, it shouldn't propagate
            using (var test = PropagationTestBuilder.Create())
            {
                int brickIdx = Sector.ToBrickIdx(5, 5, 5);

                test.MarkBrickDirty(brickIdx, DirtyFlags.Reserved0, directionMask: 0)
                    .RunPropagation();

                var sector = test.GetCenterSector();

                // Verify no neighbors got the flag (only the brick itself)
                int propagatedCount = 0;
                for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                {
                    if (sector.Ptr->brickRequireUpdateFlags[i] != 0)
                        propagatedCount++;
                }

                Assert.AreEqual(1, propagatedCount,
                    "With zero direction mask, only the dirty brick itself should be marked");
            }
        }

        #endregion

        #region Cross-Sector Boundary Tests

        [Test]
        public void Propagation_BlockAtSectorBoundary_PropagatesToNeighborSector()
        {
            // Block at sector edge should propagate across sector boundary
            using (var test = PropagationTestBuilder.Create())
            {
                // Create right neighbor (+X direction, index 0)
                test.WithNeighbor(directionIndex: 0);

                // Set block at right edge of center sector (127, 64, 64)
                test.WithBlock(127, 64, 64, blockId: 100)
                    .RunPropagation();

                var centerSector = test.GetCenterSector();
                var rightNeighbor = test.GetNeighbor(0);

                // Center sector's edge brick should be marked
                int centerEdgeBrickIdx = Sector.ToBrickIdx(15, 8, 8);
                Assert.AreNotEqual(0, centerSector.Ptr->brickRequireUpdateFlags[centerEdgeBrickIdx],
                    "Center sector edge brick should be marked");

                // Right neighbor's edge brick should receive propagation
                int neighborEdgeBrickIdx = Sector.ToBrickIdx(0, 8, 8);
                Assert.AreNotEqual(0, rightNeighbor.Ptr->brickRequireUpdateFlags[neighborEdgeBrickIdx],
                    "Neighbor sector should receive propagated flags across boundary");
            }
        }

        [TestCase(0, 127, 64, 64, 0, 8, 8)]  // +X: Right edge
        [TestCase(1, 0, 64, 64, 15, 8, 8)]   // -X: Left edge
        [TestCase(2, 64, 127, 64, 8, 0, 8)]  // +Y: Top edge
        [TestCase(3, 64, 0, 64, 8, 15, 8)]   // -Y: Bottom edge
        [TestCase(4, 64, 64, 127, 8, 8, 0)]  // +Z: Front edge
        [TestCase(5, 64, 64, 0, 8, 8, 15)]   // -Z: Back edge
        public void Propagation_AllSixFaceDirections_PropagateCorrectly(
            int dirIdx, int blockX, int blockY, int blockZ, int neighborBrickX, int neighborBrickY, int neighborBrickZ)
        {
            // Parameterized test for all 6 face directions
            using (var test = PropagationTestBuilder.Create())
            {
                test.WithNeighbor(directionIndex: dirIdx)
                    .WithBlock(blockX, blockY, blockZ, blockId: 100)
                    .RunPropagation();

                var neighbor = test.GetNeighbor(dirIdx);
                int neighborBrickIdx = Sector.ToBrickIdx(neighborBrickX, neighborBrickY, neighborBrickZ);

                Assert.AreNotEqual(0, neighbor.Ptr->brickRequireUpdateFlags[neighborBrickIdx],
                    $"Face direction {dirIdx} should propagate correctly");
            }
        }

        #endregion

        #region Direction Mask Filtering Tests

        [Test]
        public void Propagation_OnlyRelevantDirections_ReceivePropagation()
        {
            // Test that propagation respects direction masks
            using (var test = PropagationTestBuilder.Create())
            {
                // Manually mark brick with specific direction mask (only direction 0: +X)
                int brickIdx = Sector.ToBrickIdx(5, 5, 5);
                uint directionMask = 1u << 0; // Only direction 0 (+X)

                test.MarkBrickDirty(brickIdx, DirtyFlags.Reserved0, directionMask)
                    .RunPropagation();

                var sector = test.GetCenterSector();

                // Brick to the right (+X) should receive propagation
                int rightBrickIdx = Sector.ToBrickIdx(6, 5, 5);
                Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[rightBrickIdx],
                    "Right neighbor should receive propagation");

                // Brick to the left (-X) should NOT receive propagation
                int leftBrickIdx = Sector.ToBrickIdx(4, 5, 5);
                Assert.AreEqual(0, sector.Ptr->brickRequireUpdateFlags[leftBrickIdx],
                    "Left neighbor should NOT receive propagation (not in direction mask)");
            }
        }

        [Test]
        public void Propagation_FlagFiltering_OnlyPropagatesSpecifiedFlags()
        {
            // Mark brick with multiple flags, but only propagate one
            using (var test = PropagationTestBuilder.Create())
            {
                int brickIdx = Sector.ToBrickIdx(5, 5, 5);

                test.MarkBrickDirty(brickIdx, DirtyFlags.Reserved0 | DirtyFlags.Reserved1)
                    .RunPropagation(flagsToPropagate: DirtyFlags.Reserved0);

                var sector = test.GetCenterSector();

                // Verify only Reserved0 propagated, not Reserved1
                int neighborBrickIdx = Sector.ToBrickIdx(6, 5, 5);
                var flags = sector.Ptr->brickRequireUpdateFlags[neighborBrickIdx];

                Assert.AreNotEqual(0, flags & (ushort)DirtyFlags.Reserved0,
                    "Reserved0 should propagate");
                Assert.AreEqual(0, flags & (ushort)DirtyFlags.Reserved1,
                    "Reserved1 should NOT propagate (not in flagsToPropagate)");
            }
        }

        #endregion

        #region Edge Cases

        [Test]
        public void Propagation_EmptySector_NoChanges()
        {
            // Empty sector with no dirty flags should complete without errors
            using (var test = PropagationTestBuilder.Create())
            {
                test.RunPropagation();

                var sector = test.GetCenterSector();

                Assert.AreEqual(0, sector.Ptr->sectorRequireUpdateFlags,
                    "Empty sector should have no require update flags");
            }
        }

        [Test]
        public void Propagation_MissingNeighbor_DoesNotCrash()
        {
            // Block at sector edge with no neighbor should not crash
            using (var test = PropagationTestBuilder.Create())
            {
                // Set block at edge but don't create neighbor
                test.WithBlock(127, 64, 64, blockId: 100);

                Assert.DoesNotThrow(() => test.RunPropagation(),
                    "Propagation should handle missing neighbors gracefully");
            }
        }

        [Test]
        public void Propagation_SelfPropagation_IncludesOwnFlags()
        {
            // A brick should include its own dirty flags in requireUpdate
            using (var test = PropagationTestBuilder.Create())
            {
                test.WithBlock(64, 64, 64, blockId: 100)
                    .RunPropagation();

                var sector = test.GetCenterSector();
                int brickIdx = Sector.ToBrickIdx(8, 8, 8);

                Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[brickIdx],
                    "Dirty brick should include its own flags in requireUpdate");
            }
        }

        #endregion

        #region Complex Integration Tests

        [Test]
        public void Propagation_MultipleBlocksInSector_PropagateIndependently()
        {
            // Multiple dirty bricks should propagate independently
            using (var test = PropagationTestBuilder.Create())
            {
                test.WithBlock(0, 0, 0, blockId: 100)      // Brick (0,0,0)
                    .WithBlock(64, 64, 64, blockId: 200)   // Brick (8,8,8)
                    .RunPropagation();

                var sector = test.GetCenterSector();

                // Both bricks and their neighbors should be marked
                int brick1Idx = Sector.ToBrickIdx(0, 0, 0);
                int brick2Idx = Sector.ToBrickIdx(8, 8, 8);

                Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[brick1Idx],
                    "First brick should be marked");
                Assert.AreNotEqual(0, sector.Ptr->brickRequireUpdateFlags[brick2Idx],
                    "Second brick should be marked");
            }
        }

        [Test]
        public void Propagation_ChainAcrossSectors_WorksCorrectly()
        {
            // Test propagation chain: center -> right neighbor
            // This requires proper neighbor setup and bilateral propagation
            var centerSector = SectorHandle.AllocEmpty();
            var rightSector = SectorHandle.AllocEmpty();
            var centerNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);
            var rightNeighbors = SectorNeighborHandles.Create(Allocator.TempJob);

            try
            {
                unsafe
                {
                    // Connect neighbors bidirectionally
                    centerNeighbors.Neighbors[0] = rightSector;  // Center's +X is right
                    rightNeighbors.Neighbors[1] = centerSector;  // Right's -X is center

                    // Set block at center's right edge
                    centerSector.SetBlock(127, 64, 64, new Block(100));
                }

                // Run propagation on both sectors
                var positions = new NativeArray<int3>(2, Allocator.TempJob);
                var sectorsMap = new NativeHashMap<int3, SectorHandle>(2, Allocator.TempJob);
                var neighborsMap = new NativeHashMap<int3, SectorNeighborHandles>(2, Allocator.TempJob);

                try
                {
                    positions[0] = new int3(0, 0, 0);
                    positions[1] = new int3(1, 0, 0);
                    sectorsMap.Add(positions[0], centerSector);
                    sectorsMap.Add(positions[1], rightSector);
                    neighborsMap.Add(positions[0], centerNeighbors);
                    neighborsMap.Add(positions[1], rightNeighbors);

                    var job = new DirtyPropagationJob
                    {
                        allSectorPositions = positions,
                        sectors = sectorsMap,
                        sectorNeighbors = neighborsMap,
                        neighborhoodType = NeighborhoodType.Moore,
                        flagsToPropagate = DirtyFlags.Reserved0
                    };

                    job.Schedule(2, 1).Complete();

                    // Verify propagation reached right sector
                    int rightEdgeBrickIdx = Sector.ToBrickIdx(0, 8, 8);
                    Assert.AreNotEqual(0, rightSector.Ptr->brickRequireUpdateFlags[rightEdgeBrickIdx],
                        "Propagation should reach right sector's edge brick");
                }
                finally
                {
                    positions.Dispose();
                    sectorsMap.Dispose();
                    neighborsMap.Dispose();
                }
            }
            finally
            {
                centerSector.Dispose(Allocator.Persistent);
                rightSector.Dispose(Allocator.Persistent);
                centerNeighbors.Dispose(Allocator.Persistent);
                rightNeighbors.Dispose(Allocator.Persistent);
            }
        }

        #endregion
    }
}
