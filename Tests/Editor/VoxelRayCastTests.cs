using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Caelix;

namespace Caelix.Tests
{
    public class VoxelRayCastTests
    {
        private readonly struct SetTarget : IVoxelRaycastTarget
        {
            private readonly HashSet<int3> solidVoxels;

            public SetTarget(params int3[] solidVoxels)
            {
                this.solidVoxels = new HashSet<int3>(solidVoxels);
            }

            public bool IsSolid(int3 position)
            {
                return solidVoxels.Contains(position);
            }
        }

        [Test]
        public void DiagonalRayReachesVoxelInsideMaximumDistance()
        {
            var ray = new Ray(
                new Vector3(0.25f, 0.25f, 0.25f),
                new Vector3(1f, 0.9f, 0.8f).normalized);
            var expected = new int3(11, 10, 9);

            bool didHit = VoxelRayCast.RaycastVoxelGrid(
                new SetTarget(expected),
                ray,
                20f,
                out int3 hitPosition,
                out _,
                out float distance);

            Assert.That(didHit, Is.True);
            Assert.That(hitPosition, Is.EqualTo(expected));
            Assert.That(distance, Is.LessThanOrEqualTo(20f));
        }

        [Test]
        public void EdgeCrossingSkipsVoxelsThatRayOnlyTouches()
        {
            var ray = new Ray(
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(1f, 1f, 0f).normalized);
            var touchedOnly = new int3(0, 1, 0);
            var expected = new int3(2, 2, 0);

            bool didHit = VoxelRayCast.RaycastVoxelGrid(
                new SetTarget(touchedOnly, expected),
                ray,
                10f,
                out int3 hitPosition,
                out int3 hitNormal,
                out _);

            Assert.That(didHit, Is.True);
            Assert.That(hitPosition, Is.EqualTo(expected));
            Assert.That(hitNormal, Is.EqualTo(new int3(-1, 0, 0)));
        }

        [Test]
        public void NearEdgeCrossingIgnoresTransformPrecisionSliver()
        {
            // These values reproduce a center-camera ray after placing the camera and entity at
            // world coordinate 1000. Float transform precision separates the X/Y crossings by
            // about 0.00016 voxel units even though the authored ray points along the edge.
            var ray = new Ray(
                new Vector3(0.712097168f, 0.7121582f, 0.5f),
                new Vector3(0.7070457f, 0.7071679f, 0.0000357055651f));
            var precisionSliver = new int3(0, 1, 0);
            var expected = new int3(2, 2, 0);

            bool didHit = VoxelRayCast.RaycastVoxelGrid(
                new SetTarget(precisionSliver, expected),
                ray,
                10f,
                out int3 hitPosition,
                out _,
                out _);

            Assert.That(didHit, Is.True);
            Assert.That(hitPosition, Is.EqualTo(expected));
        }

        [Test]
        public void NegativeRayFromGridPlaneStartsInForwardCell()
        {
            var ray = new Ray(
                new Vector3(1f, 0.5f, 0.5f),
                Vector3.left);
            var behindRay = new int3(1, 0, 0);
            var expected = new int3(0, 0, 0);

            bool didHit = VoxelRayCast.RaycastVoxelGrid(
                new SetTarget(behindRay, expected),
                ray,
                10f,
                out int3 hitPosition,
                out int3 hitNormal,
                out float distance);

            Assert.That(didHit, Is.True);
            Assert.That(hitPosition, Is.EqualTo(expected));
            Assert.That(hitNormal, Is.EqualTo(new int3(1, 0, 0)));
            Assert.That(distance, Is.Zero);
        }
    }
}
