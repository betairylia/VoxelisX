using NUnit.Framework;
using Voxelis.Rendering;

namespace VoxelisX.Tests
{
    public class BrickRenderLayoutTests
    {
        [Test]
        public void BrickRenderRecordUsesHeaderOccupancyAndBlockPayload()
        {
            Assert.That(SectorRenderer.BRICK_INFO_WORDS, Is.EqualTo(2));
            Assert.That(SectorRenderer.BRICK_OCCUPANCY_WORDS, Is.EqualTo(16));
            Assert.That(SectorRenderer.BRICK_BLOCK_DATA_OFFSET, Is.EqualTo(18));
            Assert.That(SectorRenderer.BRICK_DATA_LENGTH, Is.EqualTo(274));
        }

        [TestCase(0, 0, 0, 0, 0, 2)]
        [TestCase(3, 3, 3, 0, 63, 3)]
        [TestCase(4, 0, 0, 1, 0, 4)]
        [TestCase(7, 7, 7, 7, 63, 17)]
        public void OccupancyIndicesMatchBrickQuadrants(int x, int y, int z, int coarseBit, int microBit, int wordOffset)
        {
            Assert.That(SectorRenderer.ToCoarseOccupancyBit(x, y, z), Is.EqualTo(coarseBit));
            Assert.That(SectorRenderer.ToMicroOccupancyBit(x, y, z), Is.EqualTo(microBit));
            Assert.That(SectorRenderer.ToOccupancyWordOffset(coarseBit, microBit), Is.EqualTo(wordOffset));
        }

        [Test]
        public void PackBrickInfoStoresAbsoluteIndexAndCoarseOccupancy()
        {
            int packed = SectorRenderer.PackBrickInfo(0xABC, 0b1010_0101u);

            Assert.That(packed & 0xFFF, Is.EqualTo(0xABC));
            Assert.That((packed >> 12) & 0xF, Is.EqualTo(0));
            Assert.That((packed >> 16) & 0xFF, Is.EqualTo(0b1010_0101));
            Assert.That((packed >> 24) & 0xFF, Is.EqualTo(0));
        }
    }
}
