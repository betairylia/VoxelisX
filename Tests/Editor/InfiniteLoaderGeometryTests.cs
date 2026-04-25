using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    public class InfiniteLoaderGeometryTests
    {
        [Test]
        public void GeneratedLoadOrderStartsWithOrigin()
        {
            var points = new List<int3>();

            InfiniteLoader.GeneratePointsInIntersection(new int3(3, 2, 3), 3f, ref points);

            Assert.That(points, Is.Not.Empty);
            Assert.That(points[0], Is.EqualTo(int3.zero));
        }

        [Test]
        public void GeneratedPointsStayWithinBounds()
        {
            var points = new List<int3>();
            var bounds = new int3(3, 2, 4);

            InfiniteLoader.GeneratePointsInIntersection(bounds, 5f, ref points);

            foreach (var point in points)
            {
                Assert.That(math.all(math.abs(point) <= bounds), Is.True);
            }
        }

        [Test]
        public void GeneratedPointsStayInsideHorizontalRadius()
        {
            var points = new List<int3>();
            float radius = 3f;

            InfiniteLoader.GeneratePointsInIntersection(new int3(5, 1, 5), radius, ref points);

            foreach (var point in points)
            {
                Assert.That(math.lengthsq(new float3(point.x, 0, point.z)), Is.LessThan(radius * radius));
            }
        }

        [Test]
        public void GeneratedOrderIsNonDecreasingByManhattanDistance()
        {
            var points = new List<int3>();

            InfiniteLoader.GeneratePointsInIntersection(new int3(3, 3, 3), 4f, ref points);

            int previous = 0;
            foreach (var point in points)
            {
                int current = math.abs(point.x) + math.abs(point.y) + math.abs(point.z);
                Assert.That(current, Is.GreaterThanOrEqualTo(previous));
                previous = current;
            }
        }
    }
}
