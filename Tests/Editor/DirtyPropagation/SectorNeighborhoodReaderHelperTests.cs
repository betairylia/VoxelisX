using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.DirtyPropagation
{
    /// <summary>
    /// Unit tests for SectorNeighborhoodReaderHelper.
    /// Tests cross-sector boundary access for blocks, bricks, dirty flags, and direction masks.
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
            if (centerSector.IsValid)
                centerSector.Dispose(Allocator.Persistent);

            for (int i = 0; i < NeighborhoodSettings.neighborhoodCount; i++)
            {
                if (neighbors.Neighbors[i].IsValid)
                    neighbors.Neighbors[i].Dispose(Allocator.Persistent);
            }

            neighbors.Dispose(Allocator.Persistent);
        }

        #region Block Access Tests

        [Test]
        public void Helper_GetBlock_WithinCenter_ReturnsCorrectBlock()
        {
            var expectedBlock = new Block(100);
            centerSector.SetBlock(50, 60, 70, expectedBlock);
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            var retrievedBlock = helper.GetBlock(50, 60, 70);

            Assert.AreEqual(expectedBlock, retrievedBlock,
                "Helper should retrieve block from center sector");
        }

        [Test]
        public void Helper_GetBlock_OutOfBounds_NoNeighbor_ReturnsEmpty()
        {
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            var block = helper.GetBlock(-1, 0, 0);

            Assert.IsTrue(block.isEmpty,
                "Helper should return empty block when neighbor doesn't exist");
        }

        [Test]
        public void Helper_GetBlock_CrossFaceBoundary_ReturnsNeighborBlock()
        {
            neighbors.Neighbors[0] = SectorHandle.AllocEmpty(); // Right neighbor (+X)
            var expectedBlock = new Block(100);
            neighbors.Neighbors[0].SetBlock(0, 50, 60, expectedBlock);

            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);
            var retrievedBlock = helper.GetBlock(128, 50, 60); // First column of right neighbor

            Assert.AreEqual(expectedBlock, retrievedBlock,
                "Helper should retrieve block from right neighbor");
        }

        #endregion

        #region Brick Dirty Flags Tests

        [Test]
        public void Helper_GetBrickDirtyFlags_WithinCenter_ReturnsCorrectFlags()
        {
            int brickIdx = Sector.ToBrickIdx(5, 5, 5);
            centerSector.Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved0);
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            var flags = helper.GetBrickDirtyFlags(5, 5, 5);

            Assert.AreEqual((ushort)DirtyFlags.Reserved0, flags,
                "Helper should return dirty flags from center sector");
        }

        [Test]
        public void Helper_GetBrickDirtyFlags_OutOfBounds_NoNeighbor_ReturnsZero()
        {
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            var flags = helper.GetBrickDirtyFlags(-1, 0, 0);

            Assert.AreEqual(0, flags,
                "Helper should return 0 when neighbor doesn't exist");
        }

        [Test]
        public void Helper_GetBrickDirtyFlags_CrossFaceBoundary_ReturnsNeighborFlags()
        {
            neighbors.Neighbors[0] = SectorHandle.AllocEmpty(); // Right neighbor (+X)
            int brickIdx = Sector.ToBrickIdx(0, 5, 5);
            neighbors.Neighbors[0].Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved1);

            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);
            var flags = helper.GetBrickDirtyFlags(16, 5, 5); // First brick in right neighbor

            Assert.AreEqual((ushort)DirtyFlags.Reserved1, flags,
                "Helper should return flags from right neighbor");
        }

        [Test]
        public void Helper_GetBrickDirtyFlags_AllSixFaces_WorkCorrectly()
        {
            // Create all 6 face neighbors and mark edge bricks as dirty
            for (int i = 0; i < 6; i++)
            {
                neighbors.Neighbors[i] = SectorHandle.AllocEmpty();
                int3 dir = NeighborhoodSettings.Directions[i];

                // Mark brick at edge closest to center
                int3 edgeBrickPos = new int3(
                    dir.x == -1 ? 15 : 0,
                    dir.y == -1 ? 15 : 0,
                    dir.z == -1 ? 15 : 0
                );

                int brickIdx = Sector.ToBrickIdx(edgeBrickPos.x, edgeBrickPos.y, edgeBrickPos.z);
                neighbors.Neighbors[i].Ptr->MarkBrickDirty(brickIdx, (DirtyFlags)(1 << i));
            }

            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            // Test each face direction
            for (int i = 0; i < 6; i++)
            {
                int3 dir = NeighborhoodSettings.Directions[i];

                // Calculate brick position just beyond center sector boundary
                int3 testBrickPos = new int3(
                    dir.x == 1 ? 16 : (dir.x == -1 ? -1 : 7),
                    dir.y == 1 ? 16 : (dir.y == -1 ? -1 : 7),
                    dir.z == 1 ? 16 : (dir.z == -1 ? -1 : 7)
                );

                var flags = helper.GetBrickDirtyFlags(testBrickPos);

                Assert.AreNotEqual(0, flags,
                    $"Face direction {i} ({dir}) should have dirty flags at brick {testBrickPos}");
            }
        }

        #endregion

        #region Brick Direction Mask Tests

        [Test]
        public void Helper_GetBrickDirtyDirectionMask_WithinCenter_ReturnsCorrectMask()
        {
            // Set a block to trigger direction mask calculation
            centerSector.SetBlock(0, 0, 0, new Block(100));

            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);
            uint mask = helper.GetBrickDirtyDirectionMask(0, 0, 0);

            Assert.AreNotEqual(0u, mask,
                "Direction mask should be non-zero for brick with boundary blocks");
        }

        [Test]
        public void Helper_GetBrickDirtyDirectionMask_OutOfBounds_NoNeighbor_ReturnsZero()
        {
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            uint mask = helper.GetBrickDirtyDirectionMask(-1, 0, 0);

            Assert.AreEqual(0u, mask,
                "Direction mask should be 0 when neighbor doesn't exist");
        }

        [Test]
        public void Helper_GetBrickDirtyDirectionMask_CrossBoundary_ReturnsNeighborMask()
        {
            neighbors.Neighbors[0] = SectorHandle.AllocEmpty(); // Right neighbor (+X)
            neighbors.Neighbors[0].SetBlock(0, 0, 0, new Block(100)); // Sets direction mask

            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);
            uint mask = helper.GetBrickDirtyDirectionMask(16, 0, 0); // First brick in right neighbor

            Assert.AreNotEqual(0u, mask,
                "Direction mask should be retrieved from neighbor sector");
        }

        #endregion

        #region Neighbor Management Tests

        [Test]
        public void Helper_HasNeighbor_CenterSector_ReturnsTrue()
        {
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            bool hasCenter = helper.HasNeighbor(new int3(0, 0, 0));

            Assert.IsTrue(hasCenter, "Helper should report center sector exists");
        }

        [Test]
        public void Helper_HasNeighbor_MissingNeighbor_ReturnsFalse()
        {
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            bool hasNeighbor = helper.HasNeighbor(new int3(1, 0, 0));

            Assert.IsFalse(hasNeighbor, "Helper should report missing neighbor");
        }

        [Test]
        public void Helper_HasNeighbor_ExistingNeighbor_ReturnsTrue()
        {
            neighbors.Neighbors[0] = SectorHandle.AllocEmpty(); // Right neighbor
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            bool hasNeighbor = helper.HasNeighbor(new int3(1, 0, 0));

            Assert.IsTrue(hasNeighbor, "Helper should report existing neighbor");
        }

        #endregion

        #region Block Test Tests

        [Test]
        public void Helper_BlockTest_EmptyBlock_ReturnsFalse()
        {
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            bool result = helper.BlockTest(10, 10, 10);

            Assert.IsFalse(result, "BlockTest should return false for empty block");
        }

        [Test]
        public void Helper_BlockTest_NonEmptyBlock_ReturnsTrue()
        {
            centerSector.SetBlock(10, 10, 10, new Block(100));
            var helper = new SectorNeighborhoodReaderHelper(centerSector, neighbors);

            bool result = helper.BlockTest(10, 10, 10);

            Assert.IsTrue(result, "BlockTest should return true for non-empty block");
        }

        #endregion
    }
}
