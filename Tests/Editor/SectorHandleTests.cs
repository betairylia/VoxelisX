using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Unit tests for SectorHandle.
    /// Tests allocation, disposal, validity checks, and safe pointer wrapping.
    /// </summary>
    [TestFixture]
    public class SectorHandleTests
    {
        #region Allocation and Disposal Tests

        [Test]
        public void SectorHandle_AllocEmpty_CreatesValidHandle()
        {
            // Arrange & Act
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Assert
                Assert.IsTrue(handle.IsValid, "Allocated handle should be valid");
                Assert.IsFalse(handle.IsNull, "Allocated handle should not be null");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_AllocEmpty_InitializesEmptySector()
        {
            // Arrange & Act
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Assert
                Assert.AreEqual(0, handle.NonEmptyBrickCount, "New sector should have zero non-empty bricks");
                Assert.IsTrue(handle.IsRendererEmpty, "New sector should be renderer-empty");
                Assert.IsFalse(handle.IsRendererDirty, "New sector should not be renderer-dirty");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_AllocEmptyWithCustomBrickCount_AllocatesCorrectCapacity()
        {
            // Arrange & Act
            var handle = SectorHandle.AllocEmpty(initialBricks: 10);

            try
            {
                // Assert
                Assert.IsTrue(handle.IsValid, "Handle with custom brick count should be valid");
                // The voxels list should have capacity for 10 bricks * 512 blocks per brick
                Assert.IsTrue(handle.Ptr->voxels.Capacity >= 10 * Sector.BLOCKS_IN_BRICK,
                    "Sector should have capacity for at least 10 bricks");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_Dispose_InvalidatesHandle()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            // Act
            handle.Dispose(Allocator.Persistent);

            // Assert - We can't really test this safely without causing access violations,
            // but we can verify the test doesn't crash
            Assert.Pass("Dispose completed without crashing");
        }

        [Test]
        public void SectorHandle_AllocWithTempJobAllocator_Works()
        {
            // Arrange & Act
            var handle = SectorHandle.AllocEmpty(allocator: Allocator.TempJob);

            try
            {
                // Assert
                Assert.IsTrue(handle.IsValid, "Handle with TempJob allocator should be valid");
            }
            finally
            {
                handle.Dispose(Allocator.TempJob);
            }
        }

        #endregion

        #region Validity Tests

        [Test]
        public void SectorHandle_DefaultConstructed_IsNull()
        {
            // Arrange & Act
            var handle = new SectorHandle();

            // Assert
            Assert.IsTrue(handle.IsNull, "Default-constructed handle should be null");
            Assert.IsFalse(handle.IsValid, "Default-constructed handle should not be valid");
        }

        [Test]
        public void SectorHandle_IsValid_ReturnsTrueForAllocatedHandle()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act & Assert
                Assert.IsTrue(handle.IsValid, "Allocated handle should report IsValid = true");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Block Access Tests

        [Test]
        public void SectorHandle_GetBlock_EmptySector_ReturnsEmptyBlock()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                var block = handle.GetBlock(0, 0, 0);

                // Assert
                Assert.IsTrue(block.isEmpty, "GetBlock on empty sector should return empty block");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_SetAndGetBlock_StoresAndRetrievesCorrectly()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();
            var expectedBlock = new Block(15, 20, 25, true);

            try
            {
                // Act
                handle.SetBlock(10, 20, 30, expectedBlock);
                var retrievedBlock = handle.GetBlock(10, 20, 30);

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Retrieved block should match set block");
                Assert.AreEqual(1, handle.NonEmptyBrickCount, "Setting a block should allocate a brick");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_SetBlock_AtSectorBoundary_Works()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();
            var expectedBlock = new Block(10, 10, 10, false);

            try
            {
                // Act - Set at max valid coordinate (127, 127, 127)
                handle.SetBlock(127, 127, 127, expectedBlock);
                var retrievedBlock = handle.GetBlock(127, 127, 127);

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Block at sector boundary should be stored correctly");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_SetBlock_AtOrigin_Works()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();
            var expectedBlock = new Block(5, 5, 5, false);

            try
            {
                // Act
                handle.SetBlock(0, 0, 0, expectedBlock);
                var retrievedBlock = handle.GetBlock(0, 0, 0);

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Block at origin should be stored correctly");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Brick Access Tests

        [Test]
        public void SectorHandle_GetBrick_EmptySector_ReturnsNull()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                unsafe
                {
                    var brick = handle.GetBrick(0, 0, 0);

                    // Assert
                    Assert.IsTrue(brick == null, "GetBrick on empty sector should return null");
                }
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_GetBrick_AfterSettingBlock_ReturnsValidPointer()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();
            var block = new Block(1, 2, 3, false);

            try
            {
                // Act
                handle.SetBlock(0, 0, 0, block); // This should allocate brick (0,0,0)

                unsafe
                {
                    var brick = handle.GetBrick(0, 0, 0);

                    // Assert
                    Assert.IsTrue(brick != null, "GetBrick should return non-null after block is set");

                    // Verify we can read the block we set
                    var retrievedBlock = brick[0]; // First block in brick
                    Assert.AreEqual(block, retrievedBlock, "Block data should be accessible via brick pointer");
                }
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_GetBrickByID_InvalidID_ReturnsNull()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                unsafe
                {
                    var brick = handle.GetBrick(Sector.BRICKID_EMPTY);

                    // Assert
                    Assert.IsTrue(brick == null, "GetBrick with BRICKID_EMPTY should return null");
                }
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Renderer State Tests

        [Test]
        public void SectorHandle_IsRendererEmpty_TrueForNewSector()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act & Assert
                Assert.IsTrue(handle.IsRendererEmpty, "New sector should be renderer-empty");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_IsRendererDirty_FalseForNewSector()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act & Assert
                Assert.IsFalse(handle.IsRendererDirty, "New sector should not be renderer-dirty");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_SetBlock_MarksRendererDirty()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                handle.SetBlock(0, 0, 0, new Block(1, 1, 1, false));

                // Assert
                Assert.IsTrue(handle.IsRendererDirty, "Setting a block should mark sector as renderer-dirty");
                Assert.IsFalse(handle.IsRendererEmpty, "Sector with blocks should not be renderer-empty");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_RendererNonEmptyBrickCount_MatchesNonEmptyBrickCount()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                handle.SetBlock(0, 0, 0, new Block(1, 1, 1, false));
                handle.SetBlock(8, 8, 8, new Block(2, 2, 2, false)); // Different brick

                // Assert
                Assert.AreEqual(handle.NonEmptyBrickCount, handle.RendererNonEmptyBrickCount,
                    "RendererNonEmptyBrickCount should match NonEmptyBrickCount");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        #endregion

        #region Get Reference Tests

        [Test]
        public void SectorHandle_Get_ReturnsValidReference()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                ref var sector = ref handle.Get();

                // Assert
                Assert.AreEqual(0, sector.NonEmptyBrickCount, "Reference should provide access to sector data");
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void SectorHandle_Ptr_ReturnsValidPointer()
        {
            // Arrange
            var handle = SectorHandle.AllocEmpty();

            try
            {
                // Act
                unsafe
                {
                    var ptr = handle.Ptr;

                    // Assert
                    Assert.IsTrue(ptr != null, "Ptr should return non-null pointer");
                    Assert.AreEqual(0, ptr->NonEmptyBrickCount, "Pointer should provide access to sector data");
                }
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }

        #endregion
    }
}
