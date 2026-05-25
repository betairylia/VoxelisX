using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Voxelis;

namespace VoxelisX.Tests
{
    public unsafe class BlockPackingTests
    {
        [Test]
        public void DefaultBlockIsEmpty()
        {
            var block = new Block();

            Assert.That(UnsafeUtility.SizeOf<Block>(), Is.EqualTo(sizeof(ushort)));
            Assert.That(block.data, Is.EqualTo((ushort)0));
            Assert.That(block.id, Is.EqualTo(0));
            Assert.That(block.isEmpty, Is.True);
        }

        [Test]
        public void IdConstructorStoresVisibleValue()
        {
            var block = new Block(0x1234);

            Assert.That(block.data, Is.EqualTo((ushort)0x1234));
            Assert.That(block.id, Is.EqualTo(0x1234));
        }

        [Test]
        public void MetaIsSeparateFixedSizeSlotValue()
        {
            var block = new Block { data = 0xABCD };
            var meta = new Meta { data = 0x1357 };

            Assert.That(block.id, Is.EqualTo(0xABCD));
            Assert.That(UnsafeUtility.SizeOf<Meta>(), Is.EqualTo(sizeof(ushort)));
            Assert.That(meta.data, Is.EqualTo(0x1357));
        }

        [Test]
        public void IsEmptyRequiresAllDataBitsToBeZero()
        {
            var visible = new Block { data = 1 };

            Assert.That(Block.Empty.isEmpty, Is.True);
            Assert.That(visible.isEmpty, Is.False);
        }

        [Test]
        public void IsRendererEmptyUsesBlockIdOnly()
        {
            Assert.That(Block.Empty.isRendererEmpty, Is.True);
            Assert.That(new Block(1).isRendererEmpty, Is.False);
        }

        [Test]
        public void EqualityAndHashCodeUsePackedData()
        {
            var a = new Block { data = 0x1002 };
            var b = new Block { data = 0x1002 };
            var c = new Block { data = 0x1003 };

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
        }
    }
}
