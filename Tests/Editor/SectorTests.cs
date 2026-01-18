using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Unit tests for the Sector struct.
    /// Tests sector creation, block/brick operations, dirty flag system, and memory management.
    /// </summary>
    [TestFixture]
    public class SectorTests
    {
        private SectorHandle testSector;

        [SetUp]
        public void SetUp()
        {
            // Create a fresh sector for each test
            testSector = SectorHandle.AllocEmpty();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up to prevent memory leaks
            if (testSector.IsValid)
            {
                testSector.Dispose(Allocator.Persistent);
            }
        }

        #region Constants Tests

        [Test]
        public void Sector_Constants_HaveCorrectValues()
        {
            // Assert brick-level constants
            Assert.AreEqual(3, Sector.SHIFT_IN_BLOCKS, "SHIFT_IN_BLOCKS should be 3 (2^3 = 8)");
            Assert.AreEqual(8, Sector.SIZE_IN_BLOCKS, "SIZE_IN_BLOCKS should be 8");
            Assert.AreEqual(7, Sector.BRICK_MASK, "BRICK_MASK should be 7 (0b111)");
            Assert.AreEqual(64, Sector.SIZE_IN_BLOCKS_SQUARED, "SIZE_IN_BLOCKS_SQUARED should be 64");
            Assert.AreEqual(512, Sector.BLOCKS_IN_BRICK, "BLOCKS_IN_BRICK should be 512 (8^3)");

            // Assert sector-level constants
            Assert.AreEqual(4, Sector.SHIFT_IN_BRICKS, "SHIFT_IN_BRICKS should be 4 (2^4 = 16)");
            Assert.AreEqual(16, Sector.SIZE_IN_BRICKS, "SIZE_IN_BRICKS should be 16");
            Assert.AreEqual(15, Sector.SECTOR_MASK, "SECTOR_MASK should be 15 (0b1111)");
            Assert.AreEqual(256, Sector.SIZE_IN_BRICKS_SQUARED, "SIZE_IN_BRICKS_SQUARED should be 256");
            Assert.AreEqual(128, Sector.SECTOR_SIZE_IN_BLOCKS, "SECTOR_SIZE_IN_BLOCKS should be 128");

            // Assert special values
            Assert.AreEqual(-1, Sector.BRICKID_EMPTY, "BRICKID_EMPTY should be -1");
        }

        #endregion

        #region Block Operations Tests

        [Test]
        public void Sector_GetBlock_EmptySector_ReturnsEmptyBlock()
        {
            // Act
            var block = testSector.Ptr->GetBlock(0, 0, 0);

            // Assert
            Assert.IsTrue(block.isEmpty, "GetBlock on empty sector should return empty block");
        }

        [Test]
        public void Sector_SetBlock_ThenGet_ReturnsCorrectBlock()
        {
            // Arrange
            var expectedBlock = new Block(15, 20, 25, true);
            int x = 10, y = 20, z = 30;

            // Act
            testSector.Ptr->SetBlock(x, y, z, expectedBlock);
            var retrievedBlock = testSector.Ptr->GetBlock(x, y, z);

            // Assert
            Assert.AreEqual(expectedBlock, retrievedBlock, "Retrieved block should match set block");
        }

        [Test]
        public void Sector_SetBlock_MultipleBlocks_StoresIndependently()
        {
            // Arrange
            var block1 = new Block(10, 10, 10, false);
            var block2 = new Block(20, 20, 20, true);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block1);
            testSector.Ptr->SetBlock(5, 5, 5, block2);

            // Assert
            Assert.AreEqual(block1, testSector.Ptr->GetBlock(0, 0, 0), "First block should be stored correctly");
            Assert.AreEqual(block2, testSector.Ptr->GetBlock(5, 5, 5), "Second block should be stored correctly");
        }

        [Test]
        public void Sector_SetBlock_OverwriteExisting_UpdatesCorrectly()
        {
            // Arrange
            var originalBlock = new Block(10, 10, 10, false);
            var newBlock = new Block(20, 20, 20, true);

            // Act
            testSector.Ptr->SetBlock(5, 5, 5, originalBlock);
            testSector.Ptr->SetBlock(5, 5, 5, newBlock);
            var retrievedBlock = testSector.Ptr->GetBlock(5, 5, 5);

            // Assert
            Assert.AreEqual(newBlock, retrievedBlock, "Block should be overwritten with new value");
        }

        [Test]
        public void Sector_SetBlock_EmptyToEmptyBrick_DoesNotAllocate()
        {
            // Arrange
            var emptyBlock = Block.Empty;

            // Act
            testSector.Ptr->SetBlock(10, 10, 10, emptyBlock);

            // Assert
            Assert.AreEqual(0, testSector.Ptr->NonEmptyBrickCount, "Setting empty block should not allocate brick");
        }

        [Test]
        public void Sector_SetBlock_NonEmptyBlock_AllocatesBrick()
        {
            // Arrange
            var block = new Block(1, 1, 1, false);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block);

            // Assert
            Assert.AreEqual(1, testSector.Ptr->NonEmptyBrickCount, "Setting non-empty block should allocate brick");
        }

        [Test]
        public void Sector_SetBlock_MultipleBricks_AllocatesIndependently()
        {
            // Arrange - Set blocks in different bricks (bricks are 8x8x8)
            var block = new Block(1, 1, 1, false);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block);   // Brick (0,0,0)
            testSector.Ptr->SetBlock(8, 0, 0, block);   // Brick (1,0,0)
            testSector.Ptr->SetBlock(0, 8, 0, block);   // Brick (0,1,0)

            // Assert
            Assert.AreEqual(3, testSector.Ptr->NonEmptyBrickCount, "Should allocate 3 separate bricks");
        }

        [Test]
        public void Sector_SetBlock_SameBrick_OnlyAllocatesOnce()
        {
            // Arrange - Set multiple blocks in the same brick (0,0,0)
            var block = new Block(1, 1, 1, false);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block);
            testSector.Ptr->SetBlock(1, 1, 1, block);
            testSector.Ptr->SetBlock(7, 7, 7, block); // Still within brick (0,0,0)

            // Assert
            Assert.AreEqual(1, testSector.Ptr->NonEmptyBrickCount, "Multiple blocks in same brick should allocate once");
        }

        [Test]
        public void Sector_SetBlock_AtMaxCoordinates_Works()
        {
            // Arrange
            var block = new Block(31, 31, 31, true);

            // Act
            testSector.Ptr->SetBlock(127, 127, 127, block);
            var retrieved = testSector.Ptr->GetBlock(127, 127, 127);

            // Assert
            Assert.AreEqual(block, retrieved, "Block at max coordinates should be stored correctly");
        }

        #endregion

        #region Brick Index Conversion Tests

        [Test]
        public void Sector_ToBrickIdx_CalculatesCorrectIndex()
        {
            // Test corner cases
            Assert.AreEqual(0, Sector.ToBrickIdx(0, 0, 0), "Origin brick should have index 0");
            Assert.AreEqual(15, Sector.ToBrickIdx(15, 0, 0), "Max X brick should have correct index");
            Assert.AreEqual(15 * 16, Sector.ToBrickIdx(0, 15, 0), "Max Y brick should have correct index");
            Assert.AreEqual(15 * 16 * 16, Sector.ToBrickIdx(0, 0, 15), "Max Z brick should have correct index");
        }

        [Test]
        public void Sector_ToBrickPos_InvertsCorrectly()
        {
            // Test that ToBrickPos is the inverse of ToBrickIdx
            for (short z = 0; z < 16; z += 5)
            {
                for (short y = 0; y < 16; y += 5)
                {
                    for (short x = 0; x < 16; x += 5)
                    {
                        short idx = (short)Sector.ToBrickIdx(x, y, z);
                        int3 pos = Sector.ToBrickPos(idx);

                        Assert.AreEqual(new int3(x, y, z), pos,
                            $"ToBrickPos should invert ToBrickIdx for ({x},{y},{z})");
                    }
                }
            }
        }

        [Test]
        public void Sector_ToBlockIdx_CalculatesCorrectIndex()
        {
            // Test corner cases within a brick
            Assert.AreEqual(0, Sector.ToBlockIdx(0, 0, 0), "Origin block should have index 0");
            Assert.AreEqual(7, Sector.ToBlockIdx(7, 0, 0), "Max X block should have correct index");
            Assert.AreEqual(7 * 8, Sector.ToBlockIdx(0, 7, 0), "Max Y block should have correct index");
            Assert.AreEqual(7 * 8 * 8, Sector.ToBlockIdx(0, 0, 7), "Max Z block should have correct index");
            Assert.AreEqual(511, Sector.ToBlockIdx(7, 7, 7), "Max block should have index 511");
        }

        #endregion

        #region Brick Operations Tests

        [Test]
        public void Sector_GetBrick_EmptySector_ReturnsNull()
        {
            // Act
            unsafe
            {
                var brick = testSector.Ptr->GetBrick(0, 0, 0);

                // Assert
                Assert.IsTrue(brick == null, "GetBrick on empty sector should return null");
            }
        }

        [Test]
        public void Sector_GetBrick_AfterSetBlock_ReturnsValidPointer()
        {
            // Arrange
            var block = new Block(1, 2, 3, false);
            testSector.Ptr->SetBlock(0, 0, 0, block);

            // Act
            unsafe
            {
                var brick = testSector.Ptr->GetBrick(0, 0, 0);

                // Assert
                Assert.IsTrue(brick != null, "GetBrick should return non-null after block is set");
            }
        }

        [Test]
        public void Sector_GetBrick_AccessBlockData_ReturnsCorrectValues()
        {
            // Arrange
            var expectedBlock = new Block(10, 15, 20, true);
            testSector.Ptr->SetBlock(5, 5, 5, expectedBlock); // Brick (0,0,0), block (5,5,5) within brick

            // Act
            unsafe
            {
                var brick = testSector.Ptr->GetBrick(0, 0, 0);
                int blockIdx = Sector.ToBlockIdx(5, 5, 5);
                var retrievedBlock = brick[blockIdx];

                // Assert
                Assert.AreEqual(expectedBlock, retrievedBlock, "Block data should be accessible via brick pointer");
            }
        }

        #endregion

        #region Dirty Flag System Tests

        [Test]
        public void Sector_MarkBrickDirty_SetsFlagCorrectly()
        {
            // Arrange
            int brickIdx = 0;
            DirtyFlags flags = DirtyFlags.Reserved0;

            // Act
            testSector.Ptr->MarkBrickDirty(brickIdx, flags);

            // Assert
            unsafe
            {
                Assert.AreEqual((ushort)flags, testSector.Ptr->brickDirtyFlags[brickIdx],
                    "Brick dirty flag should be set");
                Assert.AreEqual((ushort)flags, testSector.Ptr->sectorDirtyFlags,
                    "Sector dirty flag should be set");
            }
        }

        [Test]
        public void Sector_MarkBrickDirty_MultipleFlags_MergesCorrectly()
        {
            // Arrange
            int brickIdx = 0;

            // Act
            testSector.Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved0);
            testSector.Ptr->MarkBrickDirty(brickIdx, DirtyFlags.Reserved1);

            // Assert
            unsafe
            {
                ushort expected = (ushort)DirtyFlags.Reserved0 | (ushort)DirtyFlags.Reserved1;
                Assert.AreEqual(expected, testSector.Ptr->brickDirtyFlags[brickIdx],
                    "Multiple dirty flags should be merged with OR");
            }
        }

        [Test]
        public void Sector_MarkBrickRequireUpdate_SetsFlagCorrectly()
        {
            // Arrange
            int brickIdx = 5;
            DirtyFlags flags = DirtyFlags.Reserved2;

            // Act
            testSector.Ptr->MarkBrickRequireUpdate(brickIdx, flags);

            // Assert
            unsafe
            {
                Assert.AreEqual((ushort)flags, testSector.Ptr->brickRequireUpdateFlags[brickIdx],
                    "Brick require update flag should be set");
                Assert.AreEqual((ushort)flags, testSector.Ptr->sectorRequireUpdateFlags,
                    "Sector require update flag should be set");
            }
        }

        [Test]
        public void Sector_ClearAllDirtyFlags_ResetsAllFlags()
        {
            // Arrange
            testSector.Ptr->MarkBrickDirty(0, DirtyFlags.Reserved0);
            testSector.Ptr->MarkBrickDirty(10, DirtyFlags.Reserved1);

            // Act
            testSector.Ptr->ClearAllDirtyFlags();

            // Assert
            unsafe
            {
                Assert.AreEqual(0, testSector.Ptr->brickDirtyFlags[0], "Brick 0 dirty flag should be cleared");
                Assert.AreEqual(0, testSector.Ptr->brickDirtyFlags[10], "Brick 10 dirty flag should be cleared");
                Assert.AreEqual(0, testSector.Ptr->sectorDirtyFlags, "Sector dirty flag should be cleared");
            }
        }

        [Test]
        public void Sector_ClearRequireUpdateFlags_ClearsSpecificFlags()
        {
            // Arrange
            testSector.Ptr->MarkBrickRequireUpdate(0, DirtyFlags.Reserved0 | DirtyFlags.Reserved1);

            // Act
            testSector.Ptr->ClearRequireUpdateFlags(DirtyFlags.Reserved0);

            // Assert
            unsafe
            {
                Assert.AreEqual((ushort)DirtyFlags.Reserved1, testSector.Ptr->brickRequireUpdateFlags[0],
                    "Only specified flag should be cleared, others remain");
            }
        }

        [Test]
        public void Sector_ClearAllRequireUpdateFlags_ResetsAllFlags()
        {
            // Arrange
            testSector.Ptr->MarkBrickRequireUpdate(0, DirtyFlags.Reserved0);
            testSector.Ptr->MarkBrickRequireUpdate(5, DirtyFlags.Reserved1);

            // Act
            testSector.Ptr->ClearAllRequireUpdateFlags();

            // Assert
            unsafe
            {
                Assert.AreEqual(0, testSector.Ptr->brickRequireUpdateFlags[0], "Brick 0 flag should be cleared");
                Assert.AreEqual(0, testSector.Ptr->brickRequireUpdateFlags[5], "Brick 5 flag should be cleared");
                Assert.AreEqual(0, testSector.Ptr->sectorRequireUpdateFlags, "Sector flag should be cleared");
            }
        }

        [Test]
        public void Sector_SetBlock_AutomaticallyMarksDirty()
        {
            // Arrange
            var block = new Block(1, 1, 1, false);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block);

            // Assert
            unsafe
            {
                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.AreNotEqual(0, testSector.Ptr->brickDirtyFlags[brickIdx],
                    "SetBlock should automatically mark brick as dirty");
            }
        }

        #endregion

        #region Update Record Tests

        [Test]
        public void Sector_SetBlock_AddsToUpdateRecord()
        {
            // Arrange
            var block = new Block(1, 1, 1, false);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block);

            // Assert
            Assert.IsFalse(testSector.Ptr->updateRecord.IsEmpty, "Update record should not be empty after SetBlock");
            Assert.AreEqual(1, testSector.Ptr->updateRecord.Length, "Update record should contain one entry");
        }

        [Test]
        public void Sector_SetBlock_NewBrick_MarkedAsAdded()
        {
            // Arrange & Act
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1, 1, 1, false));

            // Assert
            unsafe
            {
                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.AreEqual(BrickUpdateInfo.Type.Added, testSector.Ptr->brickFlags[brickIdx],
                    "New brick should be marked as Added");
            }
        }

        [Test]
        public void Sector_SetBlock_ExistingBrick_MarkedAsModified()
        {
            // Arrange
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1, 1, 1, false));
            testSector.Ptr->EndTick(); // Clear update state

            // Act
            testSector.Ptr->SetBlock(1, 1, 1, new Block(2, 2, 2, false)); // Same brick, different block

            // Assert
            unsafe
            {
                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.AreEqual(BrickUpdateInfo.Type.Modified, testSector.Ptr->brickFlags[brickIdx],
                    "Existing brick should be marked as Modified");
            }
        }

        [Test]
        public void Sector_EndTick_ClearsUpdateRecordAndFlags()
        {
            // Arrange
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1, 1, 1, false));

            // Act
            testSector.Ptr->EndTick();

            // Assert
            Assert.IsTrue(testSector.Ptr->updateRecord.IsEmpty, "EndTick should clear update record");

            unsafe
            {
                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.AreEqual(BrickUpdateInfo.Type.Idle, testSector.Ptr->brickFlags[brickIdx],
                    "EndTick should reset brick flags to Idle");
            }
        }

        #endregion

        #region Renderer State Tests

        [Test]
        public void Sector_IsRendererDirty_TrueAfterSetBlock()
        {
            // Act
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1, 1, 1, false));

            // Assert
            Assert.IsTrue(testSector.Ptr->IsRendererDirty, "Sector should be renderer-dirty after SetBlock");
        }

        [Test]
        public void Sector_IsRendererEmpty_FalseAfterSetBlock()
        {
            // Act
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1, 1, 1, false));

            // Assert
            Assert.IsFalse(testSector.Ptr->IsRendererEmpty, "Sector should not be renderer-empty after SetBlock");
        }

        #endregion

        #region Memory Tests

        [Test]
        public void Sector_MemoryUsage_ReflectsAllocation()
        {
            // Arrange
            var block = new Block(1, 1, 1, false);

            // Act
            testSector.Ptr->SetBlock(0, 0, 0, block); // Allocates one brick

            // Assert
            int expectedMinSize = Sector.BLOCKS_IN_BRICK * 4; // 512 blocks * 4 bytes per block
            Assert.GreaterOrEqual(testSector.Ptr->MemoryUsage, expectedMinSize,
                "Memory usage should reflect allocated bricks");
        }

        #endregion

        #region Clone Tests

        [Test]
        public void Sector_CloneNoRecord_CreatesIndependentCopy()
        {
            // Arrange
            var originalBlock = new Block(10, 10, 10, false);
            testSector.Ptr->SetBlock(5, 5, 5, originalBlock);

            // Act
            var clonedSector = Sector.CloneNoRecord(testSector, Allocator.TempJob);
            var clonedHandle = new SectorHandle(&clonedSector);

            try
            {
                // Assert
                Assert.AreEqual(testSector.Ptr->NonEmptyBrickCount, clonedSector.NonEmptyBrickCount,
                    "Cloned sector should have same brick count");

                var clonedBlock = clonedSector.GetBlock(5, 5, 5);
                Assert.AreEqual(originalBlock, clonedBlock, "Cloned sector should have same block data");

                // Verify independence - modify original
                var newBlock = new Block(20, 20, 20, true);
                testSector.Ptr->SetBlock(5, 5, 5, newBlock);

                var clonedBlockAfter = clonedSector.GetBlock(5, 5, 5);
                Assert.AreEqual(originalBlock, clonedBlockAfter,
                    "Clone should be independent - original modification should not affect clone");
            }
            finally
            {
                clonedSector.Dispose(Allocator.TempJob);
            }
        }

        #endregion
    }
}
