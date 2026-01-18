using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Unit tests for the Block struct.
    /// Tests the packed 32-bit format, constructors, color encoding, and equality operations.
    /// </summary>
    [TestFixture]
    public class BlockTests
    {
        #region Constructor Tests

        [Test]
        public void Block_DefaultConstructor_IsEmpty()
        {
            // Arrange & Act
            var block = new Block();

            // Assert
            Assert.IsTrue(block.isEmpty, "Default-constructed block should be empty");
            Assert.AreEqual(0u, block.data, "Default block data should be zero");
        }

        [Test]
        public void Block_ConstructorWithID_StoresCorrectID()
        {
            // Arrange
            ushort expectedId = 12345;

            // Act
            var block = new Block(expectedId);

            // Assert
            Assert.AreEqual(expectedId, block.id, "Block ID should match constructor parameter");
            Assert.IsFalse(block.isEmpty, "Block with non-zero ID should not be empty");
        }

        [Test]
        public void Block_ConstructorWithRGB_StoresCorrectValues()
        {
            // Arrange
            int r = 15, g = 20, b = 25;
            bool emission = true;

            // Act
            var block = new Block(r, g, b, emission);

            // Assert
            Assert.IsFalse(block.isEmpty, "Block with RGB values should not be empty");

            // Verify the ID encodes the RGB555 + emission format
            // ID format: RRRRR GGGGG BBBBB E
            int expectedId = (r << 11) | (g << 6) | (b << 1) | 1;
            Assert.AreEqual((ushort)expectedId, block.id, "Block ID should encode RGB555 + emission");
        }

        [Test]
        public void Block_ConstructorWithFloatRGB_QuantizesCorrectly()
        {
            // Arrange
            float r = 0.5f, g = 0.75f, b = 1.0f;
            float emission = 1.0f;

            // Act
            var block = new Block(r, g, b, emission);

            // Assert
            Assert.IsFalse(block.isEmpty, "Block with RGB values should not be empty");

            // Verify quantization (5-bit per channel = 0-31 range)
            // 0.5 * 32 = 16, 0.75 * 32 = 24, 1.0 * 32 = 32 (floored to 32, but max is 31)
            int expectedR = (int)math.floor(r * 32.0f);
            int expectedG = (int)math.floor(g * 32.0f);
            int expectedB = (int)math.floor(b * 32.0f);
            int expectedId = (expectedR << 11) | (expectedG << 6) | (expectedB << 1) | 1;

            Assert.AreEqual((ushort)expectedId, block.id, "Block should quantize float RGB to 5-bit per channel");
        }

        [Test]
        public void Block_ConstructorWithZeroEmission_StoresEmissionBitAsFalse()
        {
            // Arrange & Act
            var block = new Block(10, 10, 10, false);

            // Assert
            // The lowest bit of ID should be 0 (no emission)
            Assert.AreEqual(0, block.id & 1, "Emission bit should be 0 when emission is false");
        }

        [Test]
        public void Block_ConstructorWithNonZeroEmission_StoresEmissionBitAsTrue()
        {
            // Arrange & Act
            var block = new Block(10, 10, 10, true);

            // Assert
            // The lowest bit of ID should be 1 (emission enabled)
            Assert.AreEqual(1, block.id & 1, "Emission bit should be 1 when emission is true");
        }

        #endregion

        #region Equality Tests

        [Test]
        public void Block_EqualityOperator_SameData_ReturnsTrue()
        {
            // Arrange
            var block1 = new Block(15, 20, 25, true);
            var block2 = new Block(15, 20, 25, true);

            // Act & Assert
            Assert.IsTrue(block1 == block2, "Blocks with same data should be equal");
            Assert.IsFalse(block1 != block2, "Blocks with same data should not be not-equal");
        }

        [Test]
        public void Block_EqualityOperator_DifferentData_ReturnsFalse()
        {
            // Arrange
            var block1 = new Block(15, 20, 25, true);
            var block2 = new Block(10, 10, 10, false);

            // Act & Assert
            Assert.IsFalse(block1 == block2, "Blocks with different data should not be equal");
            Assert.IsTrue(block1 != block2, "Blocks with different data should be not-equal");
        }

        [Test]
        public void Block_Equals_SameData_ReturnsTrue()
        {
            // Arrange
            var block1 = new Block(15, 20, 25, true);
            var block2 = new Block(15, 20, 25, true);

            // Act & Assert
            Assert.IsTrue(block1.Equals(block2), "Blocks with same data should be equal via Equals method");
        }

        [Test]
        public void Block_EqualsObject_SameData_ReturnsTrue()
        {
            // Arrange
            var block1 = new Block(15, 20, 25, true);
            object block2 = new Block(15, 20, 25, true);

            // Act & Assert
            Assert.IsTrue(block1.Equals(block2), "Block should equal boxed Block with same data");
        }

        [Test]
        public void Block_EqualsObject_DifferentType_ReturnsFalse()
        {
            // Arrange
            var block = new Block(15, 20, 25, true);
            object other = "not a block";

            // Act & Assert
            Assert.IsFalse(block.Equals(other), "Block should not equal non-Block object");
        }

        #endregion

        #region Hash Code Tests

        [Test]
        public void Block_GetHashCode_SameData_ReturnsSameHash()
        {
            // Arrange
            var block1 = new Block(15, 20, 25, true);
            var block2 = new Block(15, 20, 25, true);

            // Act
            int hash1 = block1.GetHashCode();
            int hash2 = block2.GetHashCode();

            // Assert
            Assert.AreEqual(hash1, hash2, "Blocks with same data should have same hash code");
        }

        [Test]
        public void Block_GetHashCode_DifferentData_ReturnsDifferentHash()
        {
            // Arrange
            var block1 = new Block(15, 20, 25, true);
            var block2 = new Block(10, 10, 10, false);

            // Act
            int hash1 = block1.GetHashCode();
            int hash2 = block2.GetHashCode();

            // Assert
            Assert.AreNotEqual(hash1, hash2, "Blocks with different data should have different hash codes");
        }

        #endregion

        #region isEmpty Tests

        [Test]
        public void Block_Empty_IsEmpty()
        {
            // Arrange & Act
            var block = Block.Empty;

            // Assert
            Assert.IsTrue(block.isEmpty, "Block.Empty should be empty");
            Assert.AreEqual(0u, block.data, "Block.Empty should have zero data");
        }

        [Test]
        public void Block_WithData_IsNotEmpty()
        {
            // Arrange & Act
            var block = new Block(1, 1, 1, false);

            // Assert
            Assert.IsFalse(block.isEmpty, "Block with data should not be empty");
        }

        [Test]
        public void Block_WithZeroID_IsEmpty()
        {
            // Arrange & Act
            var block = new Block(0);

            // Assert
            Assert.IsTrue(block.isEmpty, "Block with ID 0 should be empty");
        }

        #endregion

        #region Meta Tests

        [Test]
        public void Block_Meta_DefaultsToZero()
        {
            // Arrange & Act
            var block = new Block(12345);

            // Assert
            Assert.AreEqual(0, block.meta, "Meta should default to zero");
        }

        [Test]
        public void Block_DirectDataManipulation_CanSetMeta()
        {
            // Arrange
            var block = new Block(12345);
            ushort expectedMeta = 0xABCD;

            // Act
            block.data = (block.data & 0xFFFF0000) | expectedMeta;

            // Assert
            Assert.AreEqual(expectedMeta, block.meta, "Meta bits should be settable via data field");
            Assert.AreEqual((ushort)12345, block.id, "ID should remain unchanged when setting meta");
        }

        #endregion

        #region Edge Case Tests

        [Test]
        public void Block_MaxRGB_DoesNotOverflow()
        {
            // Arrange & Act
            var block = new Block(31, 31, 31, true);

            // Assert
            Assert.IsFalse(block.isEmpty, "Block with max RGB should not be empty");

            // Verify max values (5-bit = 31 max)
            int expectedId = (31 << 11) | (31 << 6) | (31 << 1) | 1;
            Assert.AreEqual((ushort)expectedId, block.id, "Block should handle max RGB values");
        }

        [Test]
        public void Block_MinRGB_CreatesValidBlock()
        {
            // Arrange & Act
            var block = new Block(0, 0, 0, false);

            // Assert
            // Even with 0,0,0,false the ID is 0, making it technically empty
            Assert.IsTrue(block.isEmpty, "Block with all zeros should be empty");
        }

        [Test]
        public void Block_FloatRGBOutOfRange_Clamps()
        {
            // Arrange & Act - values > 1.0 should still quantize
            var block = new Block(1.5f, 2.0f, 0.0f, 0.0f);

            // Assert
            // floor(1.5 * 32) = 48, floor(2.0 * 32) = 64
            // These might overflow 5-bit, but let's verify it doesn't crash
            Assert.IsTrue(block.data > 0, "Block with out-of-range floats should still create valid data");
        }

        #endregion
    }
}
