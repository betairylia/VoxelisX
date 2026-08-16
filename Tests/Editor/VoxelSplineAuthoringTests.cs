using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using Voxelis.Authoring;

namespace VoxelisX.Tests
{
    public class VoxelSplineAuthoringTests
    {
        [Test]
        public void PixelPerfectDiagonal_UsesOneVoxelPerMajorAxisStep()
        {
            var paths = new List<List<Vector3>>
            {
                new() { Vector3.zero, new Vector3(4f, 4f, 0f) },
            };
            var voxels = new HashSet<Vector3Int>();

            VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            Assert.That(voxels.Count, Is.EqualTo(5));
            for (int i = 0; i <= 4; i++)
            {
                Assert.That(voxels, Does.Contain(new Vector3Int(i, i, 0)));
            }
        }

        [Test]
        public void PointToCell_UsesTheContainingVoxelNotTheNearestLatticePoint()
        {
            var paths = new List<List<Vector3>>
            {
                new() { new Vector3(0.2f, 0.9f, 0.2f) },
                new() { new Vector3(-0.2f, -0.9f, -0.2f) },
            };
            var voxels = new HashSet<Vector3Int>();

            VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            Assert.That(voxels, Is.EquivalentTo(new[]
            {
                new Vector3Int(0, 0, 0),
                new Vector3Int(-1, -1, -1),
            }));
        }

        [Test]
        public void ParallelOffsets_LandSymmetricallyAroundTheSplineCenter()
        {
            var paths = new List<List<Vector3>>
            {
                new() { new Vector3(1.5f, 0.5f, 0.5f), new Vector3(1.5f, 0.5f, 3.5f) },
                new() { new Vector3(-1.5f, 0.5f, 0.5f), new Vector3(-1.5f, 0.5f, 3.5f) },
            };
            var voxels = new HashSet<Vector3Int>();

            VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            // Cell centers sit at index + 0.5, so a rail at x = 1.5 owns cell 1 and its mirror owns
            // cell -2. Rounding to the nearest lattice point instead would put them at 2 and -2.
            foreach (Vector3Int voxel in voxels)
            {
                Assert.That(voxels, Does.Contain(new Vector3Int(-1 - voxel.x, voxel.y, voxel.z)));
            }

            Assert.That(voxels, Does.Contain(new Vector3Int(1, 0, 1)));
            Assert.That(voxels, Does.Contain(new Vector3Int(-2, 0, 1)));
        }

        [Test]
        public void PixelPerfectCorner_DropsTheRedundantCornerCell()
        {
            var paths = new List<List<Vector3>>
            {
                new()
                {
                    new Vector3(0.5f, 0.5f, 0.5f),
                    new Vector3(1.5f, 0.5f, 0.5f),
                    new Vector3(1.5f, 1.5f, 0.5f),
                },
            };
            var voxels = new HashSet<Vector3Int>();

            VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            Assert.That(voxels, Is.EquivalentTo(new[]
            {
                new Vector3Int(0, 0, 0),
                new Vector3Int(1, 1, 0),
            }));
        }

        [Test]
        public void PixelPerfectShallowSlope_KeepsEvenRuns()
        {
            var paths = new List<List<Vector3>>
            {
                new() { new Vector3(0.5f, 0.5f, 0.5f), new Vector3(6.5f, 3.5f, 0.5f) },
            };
            var voxels = new HashSet<Vector3Int>();

            VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            // A 2:1 slope alternates one diagonal step with one straight cell, and never places two
            // cells that share a row and a column around the same corner.
            Assert.That(voxels, Is.EquivalentTo(new[]
            {
                new Vector3Int(0, 0, 0),
                new Vector3Int(1, 1, 0),
                new Vector3Int(2, 1, 0),
                new Vector3Int(3, 2, 0),
                new Vector3Int(4, 2, 0),
                new Vector3Int(5, 3, 0),
                new Vector3Int(6, 3, 0),
            }));
        }

        [Test]
        public void NonFinitePathPoints_AreSkippedInsteadOfHanging()
        {
            var paths = new List<List<Vector3>>
            {
                new()
                {
                    new Vector3(0.5f, 0.5f, 0.5f),
                    new Vector3(float.NaN, 0.5f, 0.5f),
                    new Vector3(2.5f, 0.5f, 0.5f),
                },
            };
            var voxels = new HashSet<Vector3Int>();

            bool complete = VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            Assert.That(complete, Is.False);
            Assert.That(voxels, Does.Contain(new Vector3Int(0, 0, 0)));
            Assert.That(voxels, Does.Contain(new Vector3Int(2, 0, 0)));
        }

        [Test]
        public void RadiusOne_AddsFaceNeighbors()
        {
            var paths = new List<List<Vector3>>
            {
                new() { Vector3.zero },
            };
            var voxels = new HashSet<Vector3Int>();

            VoxelSplineVoxelizer.Rasterize(paths, 1f, true, voxels);

            Assert.That(voxels.Count, Is.EqualTo(7));
            Assert.That(voxels, Does.Contain(Vector3Int.right));
            Assert.That(voxels, Does.Contain(Vector3Int.left));
            Assert.That(voxels, Does.Contain(Vector3Int.up));
        }

        [Test]
        public void ParallelRightAngle_UsesMiteredCorners()
        {
            Spline spline = CreateRightAngleSpline();
            var paths = new List<List<Vector3>>();

            VoxelSplinePathBuilder.Build(
                spline,
                10f,
                VoxelSplineMode.ParallelRails,
                2f,
                4f,
                paths);

            Assert.That(paths.Count, Is.EqualTo(2));
            Assert.That(paths[0].Count, Is.EqualTo(3));
            AssertVector(paths[0][1], new Vector3(1f, 0f, 3f));
            AssertVector(paths[1][1], new Vector3(-1f, 0f, 5f));
        }

        [Test]
        public void SharpMiterLimit_FallsBackToConnectedBevel()
        {
            Spline spline = CreateRightAngleSpline();
            var paths = new List<List<Vector3>>();

            VoxelSplinePathBuilder.Build(
                spline,
                10f,
                VoxelSplineMode.ParallelRails,
                2f,
                1f,
                paths);

            Assert.That(paths[0].Count, Is.EqualTo(4));
            AssertVector(paths[0][1], new Vector3(1f, 0f, 4f));
            AssertVector(paths[0][2], new Vector3(0f, 0f, 3f));
        }

        [Test]
        public void SnappedRails_LandOnMirroredCellCenters()
        {
            var spline = new Spline(
                new[] { new float3(0f, 0f, 0f), new float3(0f, 0f, 6f) },
                TangentMode.Linear);
            var paths = new List<List<Vector3>>();

            VoxelSplinePathBuilder.Build(
                spline,
                10f,
                VoxelSplineMode.ParallelRails,
                5.6f,
                4f,
                paths,
                true);

            // 5.6 voxels apart snaps to 5, the nearest spacing whose rails sit on voxel centers.
            Assert.That(paths.Count, Is.EqualTo(2));
            Assert.That(paths[0][0].x, Is.EqualTo(2.5f).Within(0.001f));
            Assert.That(paths[1][0].x, Is.EqualTo(-2.5f).Within(0.001f));

            var voxels = new HashSet<Vector3Int>();
            VoxelSplineVoxelizer.Rasterize(paths, 0.5f, true, voxels);

            Assert.That(voxels, Does.Contain(new Vector3Int(2, 0, 0)));
            Assert.That(voxels, Does.Contain(new Vector3Int(-3, 0, 0)));
            foreach (Vector3Int voxel in voxels)
            {
                Assert.That(voxels, Does.Contain(new Vector3Int(-1 - voxel.x, voxel.y, voxel.z)));
            }
        }

        [Test]
        public void GridAlignedMapping_AcceptsQuarterTurnAndIntegerTranslation()
        {
            var sourceObject = new GameObject("source");
            var targetObject = new GameObject("target");
            try
            {
                sourceObject.transform.SetPositionAndRotation(
                    new Vector3(2f, 0f, -1f),
                    Quaternion.Euler(0f, 90f, 0f));

                bool valid = VoxelEntityAuthoringTool.TryGetGridAlignedMapping(
                    sourceObject.transform,
                    targetObject.transform,
                    out Matrix4x4 mapping,
                    out string error);

                Assert.That(valid, Is.True, error);
                Vector3Int mapped = Vector3Int.RoundToInt(mapping.MultiplyPoint3x4(new Vector3(0f, 0f, 1f)));
                Assert.That(mapped, Is.EqualTo(new Vector3Int(3, 0, -1)));
            }
            finally
            {
                Object.DestroyImmediate(sourceObject);
                Object.DestroyImmediate(targetObject);
            }
        }

        private static Spline CreateRightAngleSpline()
        {
            return new Spline(
                new[]
                {
                    new float3(0f, 0f, 0f),
                    new float3(0f, 0f, 4f),
                    new float3(4f, 0f, 4f),
                },
                TangentMode.Linear);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(0.001f));
        }
    }
}
