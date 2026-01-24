using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Core;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Unit tests for the SectorSerialization API.
    /// Tests serialization/deserialization with RLE compression and dirty flag handling.
    /// </summary>
    [TestFixture]
    public unsafe class SectorSerializationTests
    {
        private SectorHandle testSector;

        [SetUp]
        public void SetUp()
        {
            testSector = SectorHandle.AllocEmpty();
        }

        [TearDown]
        public void TearDown()
        {
            if (testSector.IsValid)
            {
                testSector.Dispose(Allocator.Persistent);
            }
        }

        #region Basic Serialization Tests

        [Test]
        public void Serialize_EmptySector_RoundTrip_Success()
        {
            // Arrange
            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success, "Deserialization should succeed");
                Assert.AreEqual(0, deserializedSector.Ptr->NonEmptyBrickCount, "Empty sector should have no bricks");

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        [Test]
        public void Serialize_SingleBlock_RoundTrip_PreservesData()
        {
            // Arrange
            var expectedBlock = new Block(15, 20, 25, true);
            testSector.Ptr->SetBlock(10, 20, 30, expectedBlock);

            // Clear dirty flags to test non-dirty serialization
            testSector.Ptr->ClearAllDirtyFlags();
            testSector.Ptr->ClearAllRequireUpdateFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success, "Deserialization should succeed");
                Assert.AreEqual(1, deserializedSector.Ptr->NonEmptyBrickCount, "Should have 1 brick");

                var retrievedBlock = deserializedSector.Ptr->GetBlock(10, 20, 30);
                Assert.AreEqual(expectedBlock, retrievedBlock, "Block should be preserved after roundtrip");

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        [Test]
        public void Serialize_MultipleBricks_RoundTrip_PreservesAllData()
        {
            // Arrange - Create blocks in 3 different bricks
            var block1 = new Block(10, 10, 10, false);
            var block2 = new Block(20, 20, 20, true);
            var block3 = new Block(5, 15, 25, false);

            testSector.Ptr->SetBlock(0, 0, 0, block1);    // Brick (0,0,0)
            testSector.Ptr->SetBlock(16, 16, 16, block2);  // Brick (2,2,2)
            testSector.Ptr->SetBlock(64, 64, 64, block3);  // Brick (8,8,8)

            testSector.Ptr->ClearAllDirtyFlags();
            testSector.Ptr->ClearAllRequireUpdateFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success, "Deserialization should succeed");
                Assert.AreEqual(3, deserializedSector.Ptr->NonEmptyBrickCount, "Should have 3 bricks");

                Assert.AreEqual(block1, deserializedSector.Ptr->GetBlock(0, 0, 0), "Block 1 should be preserved");
                Assert.AreEqual(block2, deserializedSector.Ptr->GetBlock(16, 16, 16), "Block 2 should be preserved");
                Assert.AreEqual(block3, deserializedSector.Ptr->GetBlock(64, 64, 64), "Block 3 should be preserved");

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        #endregion

        #region Dirty Flags Tests

        [Test]
        public void Serialize_WithDirtyFlags_RoundTrip_PreservesDirtyFlags()
        {
            // Arrange
            var block = new Block(1, 2, 3, false);
            testSector.Ptr->SetBlock(0, 0, 0, block);

            // Sector will have dirty flags set from SetBlock
            ushort expectedSectorDirtyFlags = testSector.Ptr->sectorDirtyFlags;
            int brickIdx = Sector.ToBrickIdx(0, 0, 0);
            ushort expectedBrickDirtyFlags = testSector.Ptr->brickDirtyFlags[brickIdx];
            uint expectedDirectionMask = testSector.Ptr->brickDirtyDirectionMask[brickIdx];

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success, "Deserialization should succeed");
                Assert.AreEqual(expectedSectorDirtyFlags, deserializedSector.Ptr->sectorDirtyFlags,
                    "Sector dirty flags should be preserved");
                Assert.AreEqual(expectedBrickDirtyFlags, deserializedSector.Ptr->brickDirtyFlags[brickIdx],
                    "Brick dirty flags should be preserved");
                Assert.AreEqual(expectedDirectionMask, deserializedSector.Ptr->brickDirtyDirectionMask[brickIdx],
                    "Direction mask should be preserved");

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        [Test]
        public void Serialize_NoDirtyFlags_RoundTrip_OmitsPerBrickFlags()
        {
            // Arrange
            var block = new Block(1, 2, 3, false);
            testSector.Ptr->SetBlock(0, 0, 0, block);

            // Clear all dirty flags
            testSector.Ptr->ClearAllDirtyFlags();
            testSector.Ptr->ClearAllRequireUpdateFlags();

            var serializedWithoutFlags = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            // Now mark as dirty
            testSector.Ptr->MarkBrickDirty(0, DirtyFlags.Reserved0);
            var serializedWithFlags = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Assert - serialized size should be smaller without dirty flags
                Assert.Less(serializedWithoutFlags.Length, serializedWithFlags.Length,
                    "Serialization without dirty flags should be smaller");

                // Calculate expected size difference
                // 4096 bricks * (2 bytes dirty + 2 bytes require + 4 bytes direction) = 32768 bytes
                int expectedDifference = Sector.BRICKS_IN_SECTOR * (sizeof(ushort) + sizeof(ushort) + sizeof(uint));
                int actualDifference = serializedWithFlags.Length - serializedWithoutFlags.Length;

                Assert.AreEqual(expectedDifference, actualDifference,
                    "Size difference should match per-brick dirty flag data size");
            }
            finally
            {
                serializedWithoutFlags.Dispose();
                serializedWithFlags.Dispose();
            }
        }

        [Test]
        public void Serialize_MultipleBricksWithDirtyFlags_PreservesAllFlags()
        {
            // Arrange
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1));
            testSector.Ptr->SetBlock(16, 0, 0, new Block(2));
            testSector.Ptr->SetBlock(0, 16, 0, new Block(3));

            // Mark different bricks with different dirty flags
            int brick0 = Sector.ToBrickIdx(0, 0, 0);
            int brick1 = Sector.ToBrickIdx(2, 0, 0);
            int brick2 = Sector.ToBrickIdx(0, 2, 0);

            testSector.Ptr->MarkBrickDirty(brick0, DirtyFlags.Reserved0, 0b1010);
            testSector.Ptr->MarkBrickDirty(brick1, DirtyFlags.Reserved1, 0b0101);
            testSector.Ptr->MarkBrickRequireUpdate(brick2, DirtyFlags.Reserved2);

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success);
                Assert.AreEqual((ushort)DirtyFlags.Reserved0, deserializedSector.Ptr->brickDirtyFlags[brick0]);
                Assert.AreEqual(0b1010u, deserializedSector.Ptr->brickDirtyDirectionMask[brick0]);

                Assert.AreEqual((ushort)DirtyFlags.Reserved1, deserializedSector.Ptr->brickDirtyFlags[brick1]);
                Assert.AreEqual(0b0101u, deserializedSector.Ptr->brickDirtyDirectionMask[brick1]);

                Assert.AreEqual((ushort)DirtyFlags.Reserved2, deserializedSector.Ptr->brickRequireUpdateFlags[brick2]);

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        #endregion

        #region RLE Compression Tests

        [Test]
        public void Serialize_UniformBrick_CompressesEfficiently()
        {
            // Arrange - Fill entire brick with same block
            var uniformBlock = new Block(10, 10, 10, false);
            for (int z = 0; z < 8; z++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        testSector.Ptr->SetBlock(x, y, z, uniformBlock);
                    }
                }
            }

            testSector.Ptr->ClearAllDirtyFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Assert - Should be highly compressed
                // Header + brick map + minimal RLE data
                // For 512 identical blocks, we expect:
                // - runCount: 2 bytes
                // - 2 runs (256 + 256): 2 bytes for lengths
                // - 2 block values: 8 bytes
                // Total RLE for brick: ~12 bytes (vs 2048 bytes uncompressed)

                Assert.Less(serialized.Length, 1000,
                    "Uniform brick should compress to much less than uncompressed size");

                // Verify roundtrip preserves data
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                Assert.IsTrue(success);
                for (int z = 0; z < 8; z++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            Assert.AreEqual(uniformBlock, deserializedSector.Ptr->GetBlock(x, y, z),
                                $"Block at ({x},{y},{z}) should be preserved");
                        }
                    }
                }

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        [Test]
        public void Serialize_AlternatingPattern_UsesRLE()
        {
            // Arrange - Create alternating pattern (worst case for RLE but still compressed)
            var block1 = new Block(1, 1, 1, false);
            var block2 = new Block(2, 2, 2, false);

            bool useBlock1 = true;
            for (int z = 0; z < 8; z++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        testSector.Ptr->SetBlock(x, y, z, useBlock1 ? block1 : block2);
                        useBlock1 = !useBlock1;
                    }
                }
            }

            testSector.Ptr->ClearAllDirtyFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Assert - Verify roundtrip
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                Assert.IsTrue(success);

                useBlock1 = true;
                for (int z = 0; z < 8; z++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            var expected = useBlock1 ? block1 : block2;
                            Assert.AreEqual(expected, deserializedSector.Ptr->GetBlock(x, y, z),
                                $"Block at ({x},{y},{z}) should preserve alternating pattern");
                            useBlock1 = !useBlock1;
                        }
                    }
                }

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        [Test]
        public void Serialize_RunLengthCapping_HandlesLongRuns()
        {
            // Arrange - Create a run longer than 256 blocks
            var block = new Block(5, 5, 5, false);

            // Fill entire brick (512 blocks) with same value
            // This should create 2 runs: 256 + 256
            for (int i = 0; i < Sector.BLOCKS_IN_BRICK; i++)
            {
                int x = i % 8;
                int y = (i / 8) % 8;
                int z = i / 64;
                testSector.Ptr->SetBlock(x, y, z, block);
            }

            testSector.Ptr->ClearAllDirtyFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success, "Should handle run length capping correctly");

                // Verify all blocks are preserved
                for (int i = 0; i < Sector.BLOCKS_IN_BRICK; i++)
                {
                    int x = i % 8;
                    int y = (i / 8) % 8;
                    int z = i / 64;
                    Assert.AreEqual(block, deserializedSector.Ptr->GetBlock(x, y, z),
                        $"Block {i} should be preserved");
                }

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        #endregion

        #region Edge Cases and Error Handling

        [Test]
        public void Serialize_CalculateMaxSize_SufficientForAllData()
        {
            // Arrange - Create complex sector
            for (int i = 0; i < 10; i++)
            {
                testSector.Ptr->SetBlock(i * 10, i * 10, i * 10, new Block((byte)i));
            }

            // Act
            int maxSize = SectorSerialization.CalculateMaxSerializedSize(testSector.Ptr);
            NativeArray<byte> buffer = new NativeArray<byte>(maxSize, Allocator.Temp);

            try
            {
                int actualSize = SectorSerialization.Serialize(testSector.Ptr, (byte*)buffer.GetUnsafePtr(), maxSize);

                // Assert
                Assert.Greater(actualSize, 0, "Serialization should succeed");
                Assert.LessOrEqual(actualSize, maxSize, "Actual size should not exceed calculated max size");
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Serialize_InsufficientBuffer_ReturnsError()
        {
            // Arrange
            testSector.Ptr->SetBlock(0, 0, 0, new Block(1));

            NativeArray<byte> tinyBuffer = new NativeArray<byte>(10, Allocator.Temp);

            try
            {
                // Act
                int result = SectorSerialization.Serialize(testSector.Ptr, (byte*)tinyBuffer.GetUnsafePtr(), tinyBuffer.Length);

                // Assert
                Assert.AreEqual(-1, result, "Should return error for insufficient buffer");
            }
            finally
            {
                tinyBuffer.Dispose();
            }
        }

        [Test]
        public void Deserialize_CorruptedMagicNumber_ReturnsError()
        {
            // Arrange
            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Corrupt magic number
                uint* magicPtr = (uint*)serialized.GetUnsafePtr();
                *magicPtr = 0xDEADBEEF;

                var deserializedSector = SectorHandle.AllocEmpty();

                // Act
                int result = SectorSerialization.Deserialize((byte*)serialized.GetUnsafePtr(), serialized.Length, deserializedSector.Ptr);

                // Assert
                Assert.AreEqual(-1, result, "Should return error for corrupted magic number");

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        [Test]
        public void Serialize_EmptyBlocks_RoundTrip_PreservesEmpty()
        {
            // Arrange - Set a block, then clear it
            testSector.Ptr->SetBlock(10, 10, 10, new Block(1));
            testSector.Ptr->SetBlock(10, 10, 10, Block.Empty);

            // Brick is still allocated but contains empty block
            testSector.Ptr->ClearAllDirtyFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success);
                var retrievedBlock = deserializedSector.Ptr->GetBlock(10, 10, 10);
                Assert.IsTrue(retrievedBlock.isEmpty, "Empty block should be preserved");

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        #endregion

        #region Complex Scenarios

        [Test]
        public void Serialize_FullSector_RoundTrip_PreservesAllData()
        {
            // Arrange - Create a complex sector with multiple bricks and varied data
            var random = new Unity.Mathematics.Random(12345);

            for (int brickZ = 0; brickZ < 4; brickZ++)
            {
                for (int brickY = 0; brickY < 4; brickY++)
                {
                    for (int brickX = 0; brickX < 4; brickX++)
                    {
                        // Fill some bricks with varied data
                        for (int z = 0; z < 8; z++)
                        {
                            for (int y = 0; y < 8; y++)
                            {
                                for (int x = 0; x < 8; x++)
                                {
                                    int worldX = brickX * 8 + x;
                                    int worldY = brickY * 8 + y;
                                    int worldZ = brickZ * 8 + z;

                                    if (random.NextFloat() > 0.5f)
                                    {
                                        var block = new Block(
                                            (byte)random.NextInt(0, 32),
                                            (byte)random.NextInt(0, 32),
                                            (byte)random.NextInt(0, 32),
                                            random.NextBool()
                                        );
                                        testSector.Ptr->SetBlock(worldX, worldY, worldZ, block);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            testSector.Ptr->ClearAllDirtyFlags();

            var serialized = SectorSerialization.SerializeToArray(testSector.Ptr, Allocator.Temp);

            try
            {
                // Act
                var deserializedSector = SectorHandle.AllocEmpty();
                bool success = SectorSerialization.DeserializeFromArray(serialized, deserializedSector.Ptr);

                // Assert
                Assert.IsTrue(success);
                Assert.AreEqual(testSector.Ptr->NonEmptyBrickCount, deserializedSector.Ptr->NonEmptyBrickCount,
                    "Brick count should match");

                // Verify all blocks match
                random = new Unity.Mathematics.Random(12345); // Reset random seed
                for (int brickZ = 0; brickZ < 4; brickZ++)
                {
                    for (int brickY = 0; brickY < 4; brickY++)
                    {
                        for (int brickX = 0; brickX < 4; brickX++)
                        {
                            for (int z = 0; z < 8; z++)
                            {
                                for (int y = 0; y < 8; y++)
                                {
                                    for (int x = 0; x < 8; x++)
                                    {
                                        int worldX = brickX * 8 + x;
                                        int worldY = brickY * 8 + y;
                                        int worldZ = brickZ * 8 + z;

                                        var originalBlock = testSector.Ptr->GetBlock(worldX, worldY, worldZ);
                                        var deserializedBlock = deserializedSector.Ptr->GetBlock(worldX, worldY, worldZ);

                                        // Consume random values to keep in sync
                                        if (random.NextFloat() > 0.5f)
                                        {
                                            random.NextInt(0, 32);
                                            random.NextInt(0, 32);
                                            random.NextInt(0, 32);
                                            random.NextBool();
                                        }

                                        Assert.AreEqual(originalBlock, deserializedBlock,
                                            $"Block at ({worldX},{worldY},{worldZ}) should match");
                                    }
                                }
                            }
                        }
                    }
                }

                deserializedSector.Dispose(Allocator.Persistent);
            }
            finally
            {
                serialized.Dispose();
            }
        }

        #endregion
    }
}
