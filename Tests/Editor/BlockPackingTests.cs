using NUnit.Framework;
using Voxelis;

namespace VoxelisX.Tests
{
    public class BlockPackingTests
    {
        [Test]
        public void DefaultBlockIsEmpty()
        {
            var block = new Block();

            Assert.That(block.data, Is.EqualTo(0u));
            Assert.That(block.id, Is.EqualTo(0));
            Assert.That(block.meta, Is.EqualTo(0));
            Assert.That(block.isEmpty, Is.True);
        }

        [Test]
        public void IdConstructorStoresIdInUpperBits()
        {
            var block = new Block(0x1234);

            Assert.That(block.data, Is.EqualTo(0x12340000u));
            Assert.That(block.id, Is.EqualTo(0x1234));
            Assert.That(block.meta, Is.EqualTo(0));
        }

        [Test]
        public void RawDataExposesIdAndMeta()
        {
            var block = new Block { data = 0xABCD1357u };

            Assert.That(block.id, Is.EqualTo(0xABCD));
            Assert.That(block.meta, Is.EqualTo(0x1357));
        }

        [Test]
        public void IsEmptyRequiresAllDataBitsToBeZero()
        {
            var metadataOnly = new Block { data = 1u };

            Assert.That(Block.Empty.isEmpty, Is.True);
            Assert.That(metadataOnly.isEmpty, Is.False);
        }

        [Test]
        public void EqualityAndHashCodeUsePackedData()
        {
            var a = new Block { data = 0x00010002u };
            var b = new Block { data = 0x00010002u };
            var c = new Block { data = 0x00010003u };

            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a != c, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void RgbEmissionConstructorPacksId()
        {
            var block = new Block(3, 4, 5, true);
            ushort expectedId = (ushort)((3 << 11) | (4 << 6) | (5 << 1) | 1);

            Assert.That(block.id, Is.EqualTo(expectedId));
            Assert.That(block.meta, Is.EqualTo(0));
        }
    }
}
