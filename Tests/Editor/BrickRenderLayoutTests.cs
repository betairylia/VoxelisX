using NUnit.Framework;
using Unity.Mathematics;
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

        // Mirrors the unpack in VoxelisXBrickTrace.hlsl (VoxelisXTraceBrickPrimitive):
        // [minX:0-2][minY:3-5][minZ:6-8][maxX:9-11][maxY:12-14][maxZ:15-17], bounds inclusive.
        [TestCase(0, 0, 0, 7, 7, 7)]
        [TestCase(0, 0, 0, 0, 0, 0)]
        [TestCase(1, 2, 3, 4, 5, 6)]
        [TestCase(7, 7, 7, 7, 7, 7)]
        public void PackBrickTightBoundsRoundTripsPerAxis(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            int packed = SectorRenderer.PackBrickTightBounds(new int3(minX, minY, minZ), new int3(maxX, maxY, maxZ));

            Assert.That(packed & 7, Is.EqualTo(minX));
            Assert.That((packed >> 3) & 7, Is.EqualTo(minY));
            Assert.That((packed >> 6) & 7, Is.EqualTo(minZ));
            Assert.That((packed >> 9) & 7, Is.EqualTo(maxX));
            Assert.That((packed >> 12) & 7, Is.EqualTo(maxY));
            Assert.That((packed >> 15) & 7, Is.EqualTo(maxZ));
            Assert.That(packed >> 18, Is.EqualTo(0));
        }
    }
}
