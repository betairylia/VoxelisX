using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    public class CoordinateAndNeighborhoodTests
    {
        [TestCase(0, 0, 0, 0)]
        [TestCase(7, 0, 0, 7)]
        [TestCase(0, 7, 0, 56)]
        [TestCase(0, 0, 7, 448)]
        [TestCase(7, 7, 7, 511)]
        public void ToBlockIdxFlattensBrickLocalPositions(int x, int y, int z, int expected)
        {
            Assert.That(Sector.ToBlockIdx(x, y, z), Is.EqualTo(expected));
        }

        [Test]
        public void BrickIndexAndPositionRoundTripRepresentativePositions()
        {
            var positions = new[]
            {
                new int3(0, 0, 0),
                new int3(1, 2, 3),
                new int3(7, 8, 9),
                new int3(15, 15, 15),
            };

            foreach (var pos in positions)
            {
                short idx = (short)Sector.ToBrickIdx(pos.x, pos.y, pos.z);
                Assert.That(Sector.ToBrickPos(idx), Is.EqualTo(pos));
            }
        }

        [Test]
        public void MooreDirectionsAreUniqueNonZeroUnitOffsets()
        {
            Assert.That(NeighborhoodSettings.Directions.Length, Is.EqualTo(NeighborhoodSettings.MOORE_COUNT));

            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];
                Assert.That(math.all(dir >= new int3(-1)) && math.all(dir <= new int3(1)), Is.True);
                Assert.That(math.any(dir != int3.zero), Is.True);

                for (int j = i + 1; j < NeighborhoodSettings.Directions.Length; j++)
                {
                    Assert.That(math.all(dir == NeighborhoodSettings.Directions[j]), Is.False);
                }
            }
        }

        [Test]
        public void DirectionLookupMapsEveryDirectionBackToIndex()
        {
            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                Assert.That(NeighborhoodSettings.DirectionToIndexMinusOne(NeighborhoodSettings.Directions[i]), Is.EqualTo(i));
            }

            Assert.That(NeighborhoodSettings.DirectionToIndexMinusOne(int3.zero), Is.EqualTo(-1));
            Assert.That(NeighborhoodSettings.DirectionToIndexMinusOne(new int3(2, 0, 0)), Is.EqualTo(-1));
        }

        [Test]
        public void OppositeDirectionsAreSymmetricAndNegated()
        {
            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                int opposite = NeighborhoodSettings.OppositeDirectionIndices[i];

                Assert.That(NeighborhoodSettings.OppositeDirectionIndices[opposite], Is.EqualTo(i));
                Assert.That(NeighborhoodSettings.Directions[opposite], Is.EqualTo(-NeighborhoodSettings.Directions[i]));
            }
        }

        [Test]
        public void DirectionMaskHelpersSetAndQueryBits()
        {
            uint mask = 0;
            mask = NeighborhoodSettings.SetDirection(mask, 3);
            mask = NeighborhoodSettings.SetDirection(mask, 12);

            Assert.That(NeighborhoodSettings.HasAnyDirection(mask), Is.True);
            Assert.That(NeighborhoodSettings.HasDirection(mask, 3), Is.True);
            Assert.That(NeighborhoodSettings.HasDirection(mask, 12), Is.True);
            Assert.That(NeighborhoodSettings.HasDirection(mask, 4), Is.False);
            Assert.That(NeighborhoodSettings.HasAnyDirection(0), Is.False);
        }
    }
}
