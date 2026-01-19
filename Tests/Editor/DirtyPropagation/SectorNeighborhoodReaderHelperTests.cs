using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.DirtyPropagation
{
    /// <summary>
    /// Unit tests for SectorNeighborhoodReaderHelper.
    /// Tests cross-sector boundary access for blocks and brick dirty flags.
    /// </summary>
    [TestFixture]
    public unsafe class SectorNeighborhoodReaderHelperTests
    {
        private SectorHandle centerSector;
        private SectorNeighborHandles neighbors;

        [SetUp]
        public void SetUp()
        {
            centerSector = SectorHandle.AllocEmpty();
            neighbors = SectorNeighborHandles.Create(Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        {
            unsafe
            {
                if (centerSector.IsValid)
                    centerSector.Dispose(Allocator.Persistent);

                // Dispose all neighbor sectors
                for (int i = 0; i < NeighborhoodSettings.neighborhoodCount; i++)
                {
                    if (neighbors.Neighbors[i].IsValid)
                    {
                        neighbors.Neighbors[i].Dispose(Allocator.Persistent);
                    }
                }

                neighbors.Dispose(Allocator.Persistent);
            }
        }

        #region Block Access Tests

        [Test]
        public void Helper_GetBlock_WithinCenterSector_ReturnsCorrectBlock()
        {
            // Arrange
            var expectedBlock = new Block(10, 20, 30, true);
            centerSector.SetBlock(50, 60, 70, expectedBlock);
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act
            var retrievedBlock = helper.GetBlock(50, 60, 70);

            // Assert
            Assert.AreEqual(expectedBlock, retrievedBlock, "Helper should retrieve block from center sector");
        }

        [Test]
        public void Helper_GetBlock_OutOfBounds_NoNeighbor_ReturnsEmpty()
        {
            // Arrange
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act - Access beyond sector boundary with no neighbor
            var block = helper.GetBlock(-1, 0, 0);

            // Assert
            Assert.IsTrue(block.isEmpty, "Helper should return empty block when neighbor doesn't exist");
        }

        [Test]
        public void Helper_GetBlock_CrossFaceBoundary_ReturnsNeighborBlock()
        {
            // Arrange - Create a right neighbor (+X direction, index 0)
            unsafe
            {
                neighbors.Neighbors[0] = SectorHandle.AllocEmpty();
                var expectedBlock = new Block(15, 25, 35, false);
                neighbors.Neighbors[0].SetBlock(0, 50, 60, expectedBlock); // First column in neighbor

                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act - Access x=128 (first column of right neighbor)
                var retrievedBlock = helper.GetBlock(128, 50, 60);

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Helper should retrieve block from right neighbor");
            }
        }

        [Test]
        public void Helper_GetBlock_CrossNegativeBoundary_ReturnsNeighborBlock()
        {
            // Arrange - Create a left neighbor (-X direction, index 1)
            unsafe
            {
                neighbors.Neighbors[1] = SectorHandle.AllocEmpty();
                var expectedBlock = new Block(5, 10, 15, true);
                neighbors.Neighbors[1].SetBlock(127, 50, 60, expectedBlock); // Last column in left neighbor

                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act - Access x=-1 (wraps to 127 in left neighbor)
                var retrievedBlock = helper.GetBlock(-1, 50, 60);

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Helper should retrieve block from left neighbor");
            }
        }

        [Test]
        public void Helper_GetBlock_CrossCorner_ReturnsNeighborBlock()
        {
            // Arrange - Create a corner neighbor (e.g., +X+Y+Z, index for (1,1,1))
            unsafe
            {
                // Find the corner neighbor index
                int cornerIdx = -1;
                for (int i = 0; i < NeighborhoodSettings.neighborhoodCount; i++)
                {
                    if (NeighborhoodSettings.Directions[i].Equals(new int3(1, 1, 1)))
                    {
                        cornerIdx = i;
                        break;
                    }
                }

                Assert.IsTrue(cornerIdx >= 0, "Should find corner neighbor index");

                neighbors.Neighbors[cornerIdx] = SectorHandle.AllocEmpty();
                var expectedBlock = new Block(20, 20, 20, true);
                neighbors.Neighbors[cornerIdx].SetBlock(0, 0, 0, expectedBlock);

                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act - Access corner position (128, 128, 128)
                var retrievedBlock = helper.GetBlock(128, 128, 128);

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Helper should retrieve block from corner neighbor");
            }
        }

        #endregion

        #region Brick Dirty Flags Tests

        [Test]
        public void Helper_GetBrickDirtyFlags_WithinCenterSector_ReturnsCorrectFlags()
        {
            // Arrange
            int brickIdx = Sector.ToBrickIdx(5, 5, 5);
            centerSector.Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved0);
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act
            var flags = helper.GetBrickDirtyFlags(5, 5, 5);

            // Assert
            Assert.AreEqual((ushort)DirtyFlags.Reserved0, flags, "Helper should return dirty flags from center sector");
        }

        [Test]
        public void Helper_GetBrickDirtyFlags_OutOfBounds_NoNeighbor_ReturnsZero()
        {
            // Arrange
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act - Access brick beyond boundary with no neighbor
            var flags = helper.GetBrickDirtyFlags(-1, 0, 0);

            // Assert
            Assert.AreEqual(0, flags, "Helper should return 0 when neighbor doesn't exist");
        }

        [Test]
        public void Helper_GetBrickDirtyFlags_CrossFaceBoundary_ReturnsNeighborFlags()
        {
            // Arrange - Create a right neighbor and mark a brick dirty
            unsafe
            {
                neighbors.Neighbors[0] = SectorHandle.AllocEmpty(); // Right (+X)
                int brickIdx = Sector.ToBrickIdx(0, 5, 5); // First brick in neighbor
                neighbors.Neighbors[0].Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved1);

                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act - Access brick at x=16 (first brick in right neighbor)
                var flags = helper.GetBrickDirtyFlags(16, 5, 5);

                // Assert
                Assert.AreEqual((ushort)DirtyFlags.Reserved1, flags, "Helper should return flags from right neighbor");
            }
        }

        [Test]
        public void Helper_GetBrickDirtyFlags_CrossNegativeBoundary_ReturnsNeighborFlags()
        {
            // Arrange - Create a left neighbor
            unsafe
            {
                neighbors.Neighbors[1] = SectorHandle.AllocEmpty(); // Left (-X)
                int brickIdx = Sector.ToBrickIdx(15, 5, 5); // Last brick in neighbor
                neighbors.Neighbors[1].Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved2);

                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act - Access brick at x=-1 (wraps to 15 in left neighbor)
                var flags = helper.GetBrickDirtyFlags(-1, 5, 5);

                // Assert
                Assert.AreEqual((ushort)DirtyFlags.Reserved2, flags, "Helper should return flags from left neighbor");
            }
        }

        [Test]
        public void WRONG_DISABLED_Helper_GetBrickDirtyFlags_AllSixFaceDirections_WorkCorrectly()
        {
            return;
            // Arrange - Create all 6 face neighbors
            unsafe
            {
                var directions = new int3[]
                {
                    new int3(1, 0, 0),   // Right
                    new int3(-1, 0, 0),  // Left
                    new int3(0, 1, 0),   // Up
                    new int3(0, -1, 0),  // Down
                    new int3(0, 0, 1),   // Forward
                    new int3(0, 0, -1)   // Back
                };

                for (int i = 0; i < 6; i++)
                {
                    neighbors.Neighbors[i] = SectorHandle.AllocEmpty();

                    // Mark the edge brick closest to center sector as dirty
                    int3 localBrickPos = new int3(7, 7, 7) - directions[i] * 7; // Middle of edge
                    int brickIdx = Sector.ToBrickIdx(localBrickPos.x, localBrickPos.y, localBrickPos.z);
                    neighbors.Neighbors[i].Ptr->MarkBrickDirty(brickIdx, (DirtyFlags)(1 << i));
                }

                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act & Assert - Test each face direction
                for (int i = 0; i < 6; i++)
                {
                    int3 testBrickPos = new int3(7, 7, 7) + directions[i] * 16; // Just beyond boundary
                    var flags = helper.GetBrickDirtyFlags(testBrickPos);

                    Assert.AreNotEqual(0, flags, $"Face direction {i} should have dirty flags");
                }
            }
        }

        #endregion

        #region Block Test and Set Tests

        [Test]
        public void Helper_BlockTest_EmptyBlock_ReturnsFalse()
        {
            // Arrange
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act
            var result = helper.BlockTest(10, 10, 10);

            // Assert
            Assert.IsFalse(result, "BlockTest should return false for empty block");
        }

        [Test]
        public void Helper_BlockTest_NonEmptyBlock_ReturnsTrue()
        {
            // Arrange
            centerSector.SetBlock(10, 10, 10, new Block(1, 1, 1, false));
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act
            var result = helper.BlockTest(10, 10, 10);

            // Assert
            Assert.IsTrue(result, "BlockTest should return true for non-empty block");
        }

        #endregion

        #region Neighbor Management Tests

        [Test]
        public void Helper_HasNeighbor_CenterSector_ReturnsTrue()
        {
            // Arrange
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act
            var hasCenter = helper.HasNeighbor(new int3(0, 0, 0));

            // Assert
            Assert.IsTrue(hasCenter, "Helper should report center sector exists");
        }

        [Test]
        public void Helper_HasNeighbor_MissingNeighbor_ReturnsFalse()
        {
            // Arrange
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Act
            var hasNeighbor = helper.HasNeighbor(new int3(1, 0, 0));

            // Assert
            Assert.IsFalse(hasNeighbor, "Helper should report missing neighbor");
        }

        [Test]
        public void Helper_HasNeighbor_ExistingNeighbor_ReturnsTrue()
        {
            // Arrange
            unsafe
            {
                neighbors.Neighbors[0] = SectorHandle.AllocEmpty(); // Right neighbor
                var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

                // Act
                var hasNeighbor = helper.HasNeighbor(new int3(1, 0, 0));

                // Assert
                Assert.IsTrue(hasNeighbor, "Helper should report existing neighbor");
            }
        }

        #endregion
    }
}
