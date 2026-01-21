using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Unit tests for the Block struct.
    /// Focuses on data integrity: block ID storage, meta field, equality, and isEmpty checks.
    /// Does NOT test color encoding semantics - only raw data correctness.
    /// </summary>
    [TestFixture]
    public class BlockTests
    {
        #region Constructor and Data Tests

        [Test]
        public void Block_DefaultConstructor_IsEmpty()
        {
            var block = new Block();

            Assert.IsTrue(block.isEmpty, "Default block should be empty");
            Assert.AreEqual(0u, block.data, "Default block data should be zero");
            Assert.AreEqual(0, block.id, "Default block ID should be zero");
            Assert.AreEqual(0, block.meta, "Default block meta should be zero");
        }

        [Test]
        public void Block_ConstructWithID_StoresCorrectData()
        {
            ushort expectedId = 12345;
            var block = new Block(expectedId);

            Assert.AreEqual(expectedId, block.id, "Block ID should match constructor parameter");
            Assert.IsFalse(block.isEmpty, "Block with non-zero ID should not be empty");
            Assert.AreEqual(0, block.meta, "Meta should default to zero");
        }

        [Test]
        public void Block_ConstructWithMaxID_DoesNotOverflow()
        {
            ushort maxId = ushort.MaxValue;
            var block = new Block(maxId);

            Assert.AreEqual(maxId, block.id, "Block should handle max ID value");
            Assert.IsFalse(block.isEmpty, "Block with max ID should not be empty");
        }

        [Test]
        public void Block_IDZero_IsEmpty()
        {
            var block = new Block(0);

            Assert.IsTrue(block.isEmpty, "Block with ID 0 should be empty");
            Assert.AreEqual(Block.Empty, block, "Block with ID 0 should equal Block.Empty");
        }

        #endregion

        #region Equality and Hash Tests

        [Test]
        public void Block_Equality_SameData_ReturnsTrue()
        {
            var block1 = new Block(100);
            var block2 = new Block(100);

            Assert.IsTrue(block1 == block2, "Blocks with same ID should be equal");
            Assert.IsTrue(block1.Equals(block2), "Equals should return true for same ID");
            Assert.IsFalse(block1 != block2, "!= should return false for same ID");
        }

        [Test]
        public void Block_Equality_DifferentData_ReturnsFalse()
        {
            var block1 = new Block(100);
            var block2 = new Block(200);

            Assert.IsFalse(block1 == block2, "Blocks with different IDs should not be equal");
            Assert.IsFalse(block1.Equals(block2), "Equals should return false for different IDs");
            Assert.IsTrue(block1 != block2, "!= should return true for different IDs");
        }

        [Test]
        public void Block_HashCode_SameData_ReturnsSameHash()
        {
            var block1 = new Block(100);
            var block2 = new Block(100);

            Assert.AreEqual(block1.GetHashCode(), block2.GetHashCode(),
                "Blocks with same data should have same hash code");
        }

        [Test]
        public void Block_HashCode_DifferentData_ReturnsDifferentHash()
        {
            var block1 = new Block(100);
            var block2 = new Block(200);

            Assert.AreNotEqual(block1.GetHashCode(), block2.GetHashCode(),
                "Blocks with different data should have different hash codes");
        }

        [Test]
        public void Block_EqualsObject_NonBlockType_ReturnsFalse()
        {
            var block = new Block(100);
            object other = "not a block";

            Assert.IsFalse(block.Equals(other), "Block should not equal non-Block object");
        }

        #endregion

        #region Meta Field Tests

        [Test]
        public void Block_Meta_CanBeSet()
        {
            var block = new Block(100);
            ushort expectedMeta = 0xABCD;

            // Meta is stored in lower 16 bits, ID in upper 16 bits
            block.data = ((uint)block.id << 16) | expectedMeta;

            Assert.AreEqual(expectedMeta, block.meta, "Meta should be settable via data field");
            Assert.AreEqual((ushort)100, block.id, "ID should remain unchanged when setting meta");
        }

        [Test]
        public void Block_Meta_DoesNotAffectIsEmpty()
        {
            var block = new Block(100);
            block.data = ((uint)block.id << 16) | 0x1234;

            Assert.IsFalse(block.isEmpty, "isEmpty should only check ID, not meta");
        }

        #endregion

        #region isEmpty Tests

        [Test]
        public void Block_Empty_IsEmpty()
        {
            Assert.IsTrue(Block.Empty.isEmpty, "Block.Empty should be empty");
            Assert.AreEqual(0u, Block.Empty.data, "Block.Empty should have zero data");
        }

        [Test]
        public void Block_WithAnyID_IsNotEmpty()
        {
            for (ushort id = 1; id < 100; id++)
            {
                var block = new Block(id);
                Assert.IsFalse(block.isEmpty, $"Block with ID {id} should not be empty");
            }
        }

        #endregion

        #region Data Packing Tests

        [Test]
        public void Block_DataPacking_IDAndMeta_PackCorrectly()
        {
            ushort id = 0x1234;
            ushort meta = 0x5678;
            uint expectedData = ((uint)id << 16) | meta;

            var block = new Block(id);
            block.data = ((uint)id << 16) | meta;

            Assert.AreEqual(id, block.id, "ID should be in upper 16 bits");
            Assert.AreEqual(meta, block.meta, "Meta should be in lower 16 bits");
            Assert.AreEqual(expectedData, block.data, "Data should pack ID and meta correctly");
        }

        [Test]
        public void Block_DirectDataAccess_WorksCorrectly()
        {
            var block = new Block { data = 0x12345678 };

            Assert.AreEqual((ushort)0x1234, block.id, "ID extraction should work");
            Assert.AreEqual((ushort)0x5678, block.meta, "Meta extraction should work");
        }

        #endregion
    }
}
