using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Tests for the voxel-voxel narrowphase (ManifoldQueries.VoxelVoxel): finite cubical-complex
    /// cores swept by radius-0.5 spheres. The narrowphase intentionally performs no patch merging
    /// or reduction; contact merging is a separate upcoming stage.
    /// </summary>
    public unsafe class VoxelContactManifoldTests
    {
        const float Tolerance = 1e-4f;

        // ------------------------------------------------------------------ harness

        /// <summary>
        /// One voxel body: an entity-data scope holding a single sector at (0,0,0) plus the
        /// sector map a VoxelCollider needs. Block coordinates must stay within one sector.
        /// </summary>
        sealed class VoxelBodyFixture : IDisposable
        {
            public EntityDataTestScope Scope;
            public UnsafeHashMap<int3, SectorHandle> Sectors;

            readonly SectorHandle m_Sector;

            public VoxelBodyFixture()
            {
                Scope = new EntityDataTestScope();
                m_Sector = Scope.AddSector(int3.zero);
                Sectors = new UnsafeHashMap<int3, SectorHandle>(1, Allocator.Persistent);
                Sectors.Add(int3.zero, m_Sector);
            }

            public void Set(int x, int y, int z)
            {
                m_Sector.SetBlock(x, y, z, new Block(1));
            }

            /// <summary>
            /// Recomputes block occupancy, PhysicsInfo exposure data and the physics-key mask,
            /// in the same order as the VoxelisXWorld tick — narrowphase consumes all three.
            /// </summary>
            public void Build()
            {
                ref Sector sector = ref m_Sector.Get();
                for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                {
                    sector.MarkBrickRequireUpdate(i, DirtyFlags.GeometryWithLocalNeighbor);
                }

                Scope.Data.RefreshNonEmptyMask(DirtyFlags.GeometryWithLocalNeighbor);

                var bodyData = new VoxelBodyData(Allocator.Persistent);
                try
                {
                    bodyData.ComputePhysicsProperties(Scope.Data);
                    bodyData.RefreshPhysicsKeyMask(Scope.Data.sectors);
                }
                finally
                {
                    bodyData.Dispose();
                }
            }

            public void Dispose()
            {
                Sectors.Dispose();
                Scope.Dispose();
            }
        }

        struct ParsedManifold
        {
            public ContactHeader Header;
            public List<ContactPoint> Points;
        }

        struct ParsedEvent
        {
            public int3 VoxelInA;
            public int3 VoxelInB;
            public bool IsPhysicsContact;
        }

        static List<ParsedManifold> Collide(
            VoxelBodyFixture bodyA, VoxelBodyFixture bodyB,
            RigidTransform worldFromA, RigidTransform worldFromB,
            out List<ParsedEvent> events,
            float maxDistance = 0.05f)
        {
            var contacts = new NativeStream(1, Allocator.Temp);
            var voxelEvents = new NativeStream(1, Allocator.Temp);
            NativeStream.Writer contactWriter = contacts.AsWriter();
            NativeStream.Writer eventWriter = voxelEvents.AsWriter();

            contactWriter.BeginForEachIndex(0);
            eventWriter.BeginForEachIndex(0);

            var context = new ManifoldQueries.Context
            {
                BodyIndices = new BodyIndexPair { BodyIndexA = 0, BodyIndexB = 1 },
                BothMotionsAreKinematic = false,
                ContactWriter = (NativeStream.Writer*)UnsafeUtility.AddressOf(ref contactWriter),
                VoxelContactWriter = (NativeStream.Writer*)UnsafeUtility.AddressOf(ref eventWriter),
                ScaleA = 1.0f,
                ScaleB = 1.0f
            };

            // VoxelVoxel only reads m_Sectors and Material, so stack-built colliders suffice.
            VoxelCollider colliderA = default;
            colliderA.Material = Unity.Physics.Material.Default;
            colliderA.m_Sectors = bodyA.Sectors;
            VoxelCollider colliderB = default;
            colliderB.Material = Unity.Physics.Material.Default;
            colliderB.m_Sectors = bodyB.Sectors;

            ManifoldQueries.VoxelVoxel(
                context,
                (Unity.Physics.Collider*)&colliderA,
                (Unity.Physics.Collider*)&colliderB,
                new Unity.Physics.Math.MTransform(worldFromA),
                new Unity.Physics.Math.MTransform(worldFromB),
                maxDistance,
                false);

            contactWriter.EndForEachIndex();
            eventWriter.EndForEachIndex();

            var manifolds = new List<ParsedManifold>();
            NativeStream.Reader contactReader = contacts.AsReader();
            contactReader.BeginForEachIndex(0);
            while (contactReader.RemainingItemCount > 0)
            {
                var header = contactReader.Read<ContactHeader>();
                var parsed = new ParsedManifold { Header = header, Points = new List<ContactPoint>() };
                for (int i = 0; i < header.NumContacts; i++)
                {
                    parsed.Points.Add(contactReader.Read<ContactPoint>());
                }

                manifolds.Add(parsed);
            }

            contactReader.EndForEachIndex();

            events = new List<ParsedEvent>();
            NativeStream.Reader eventReader = voxelEvents.AsReader();
            eventReader.BeginForEachIndex(0);
            while (eventReader.RemainingItemCount > 0)
            {
                var data = eventReader.Read<VoxelContactEventData>();
                events.Add(new ParsedEvent
                {
                    VoxelInA = data.VoxelCoordsInA,
                    VoxelInB = data.VoxelCoordsInB,
                    IsPhysicsContact = data.isPhysicsContact
                });
            }

            eventReader.EndForEachIndex();

            contacts.Dispose();
            voxelEvents.Dispose();
            return manifolds;
        }

        static List<ContactPoint> AllPoints(List<ParsedManifold> manifolds)
        {
            var points = new List<ContactPoint>();
            foreach (ParsedManifold manifold in manifolds)
            {
                points.AddRange(manifold.Points);
            }

            return points;
        }

        static bool HasPointNear(List<ContactPoint> points, float3 position, float tolerance = 1e-3f)
        {
            foreach (ContactPoint point in points)
            {
                if (math.all(math.abs(point.Position - position) < tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        static float MinimumDistance(List<ParsedManifold> manifolds)
        {
            float minimum = float.MaxValue;
            foreach (ParsedManifold manifold in manifolds)
            {
                foreach (ContactPoint point in manifold.Points)
                {
                    minimum = math.min(minimum, point.Distance);
                }
            }
            return minimum;
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void StackedCube_ProducesOneFaceContactUnderVoxelCenter()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            a.Build();
            b.Build();

            // A resting exactly on top of B.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0f, 1f, 0f)),
                RigidTransform.identity,
                out List<ParsedEvent> events);

            // Exactly one raw contact: the face-aligned cell below, nothing spurious.
            Assert.That(manifolds.Count, Is.EqualTo(1));

            ParsedManifold m = manifolds[0];
            Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance));

            // One sphere contact, snapped under the voxel center on the face plane.
            Assert.That(m.Points.Count, Is.EqualTo(1));
            Assert.That(m.Points[0].Distance, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(HasPointNear(m.Points, new float3(0.5f, 1f, 0.5f)), Is.True);

            // The per-contact gameplay event is still emitted.
            Assert.That(events.Count, Is.EqualTo(1));
            Assert.That(events[0].IsPhysicsContact, Is.True);
            Assert.That(math.all(events[0].VoxelInA == int3.zero), Is.True);
            Assert.That(math.all(events[0].VoxelInB == int3.zero), Is.True);
        }

        [Test]
        public void OffsetStack_RestsOnFaceNormals_NoDiagonalNormals()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            b.Set(1, 0, 0);
            a.Build();
            b.Build();

            // A rests over the finite segment joining the two floor centers. The segment owns the
            // seam continuously, so this is one flat contact instead of two cell contacts.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0.5f, 1f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1), "The finite floor segment owns the seam");
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance),
                    "Resting above a flat core segment must use the face normal, not a diagonal");
                Assert.That(m.Points.Count, Is.EqualTo(1));
                Assert.That(m.Points[0].Distance, Is.EqualTo(0f).Within(Tolerance));
                Assert.That(HasPointNear(m.Points, new float3(1f, 1f, 0.5f)), Is.True,
                    "Contact must sit under the voxel center on the surface plane");
            }
        }

        [Test]
        public void SnugSlot_ProducesTwoOpposingUnilateralContacts()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0); // left wall
            b.Set(2, 0, 0); // right wall
            a.Build();
            b.Build();

            // A sits exactly in the 1-voxel slot between the walls: one raw touching contact per
            // wall, no merging (fusion into equality constraints is the future merging stage).
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(2));

            float normalSumX = 0f;
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(math.abs(math.abs(m.Header.Normal.x) - 1f), Is.LessThan(Tolerance));
                Assert.That(m.Points.Count, Is.EqualTo(1));
                Assert.That(m.Points[0].Distance, Is.EqualTo(0f).Within(Tolerance));
                normalSumX += m.Header.Normal.x;
            }

            Assert.That(math.abs(normalSumX), Is.LessThan(Tolerance), "The two wall normals must oppose");

            List<ContactPoint> points = AllPoints(manifolds);
            Assert.That(HasPointNear(points, new float3(1f, 0.5f, 0.5f)), Is.True, "Left wall face contact");
            Assert.That(HasPointNear(points, new float3(2f, 0.5f, 0.5f)), Is.True, "Right wall face contact");
        }

        [Test]
        public void ShiftedSnugSlot_ReportsPerSideGaps()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            b.Set(2, 0, 0);
            a.Build();
            b.Build();

            // A pushed 0.02 into the right wall: the raw contacts carry the true per-side gaps
            // (+0.02 separation from the left wall, -0.02 penetration into the right wall).
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1.02f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(2));
            foreach (ParsedManifold m in manifolds)
            {
                float expected = m.Header.Normal.x > 0f ? 0.02f : -0.02f;
                Assert.That(m.Points[0].Distance, Is.EqualTo(expected).Within(1e-3f));
            }
        }

        [Test]
        public void LooseSlot_KeepsSingleLeaningContact()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0); // left wall
            b.Set(3, 0, 0); // right wall, 2-voxel slot
            a.Build();
            b.Build();

            // A leans against the left wall of a loose slot; the far wall is out of reach.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            Assert.That(math.distance(manifolds[0].Header.Normal, new float3(1f, 0f, 0f)), Is.LessThan(Tolerance));
        }

        [Test]
        public void DeepOverlap_PushesAlongExposedFace_NotInventedUpNormal()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            a.Build();
            b.Build();

            // A overlaps B by 0.7 along x. The old code degenerated to an arbitrary (0,1,0) normal
            // with distance -1 here, pumping bogus vertical momentum while staying stuck.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0.3f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            ParsedManifold m = manifolds[0];
            Assert.That(math.distance(m.Header.Normal, new float3(1f, 0f, 0f)), Is.LessThan(Tolerance),
                "Depenetration must follow the geometric overlap axis");
            foreach (ContactPoint point in m.Points)
            {
                Assert.That(point.Distance, Is.EqualTo(-0.7f).Within(Tolerance));
            }
        }

        [Test]
        public void FlatPatch_OneFaceContactPerSlabVoxel()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            for (int x = 0; x < 2; x++)
            {
                for (int z = 0; z < 2; z++)
                {
                    a.Set(x, 0, z);
                }
            }

            for (int x = 0; x < 4; x++)
            {
                for (int z = 0; z < 4; z++)
                {
                    b.Set(x, 0, z);
                }
            }

            a.Build();
            b.Build();

            // A 2x2 slab resting grid-aligned on a 4x4 floor. Canonical finite squares use
            // half-open positive endpoints, so adjacent target squares do not duplicate seams.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 1f, 1f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(4), "One raw contact per slab voxel");
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance));
                Assert.That(m.Points.Count, Is.EqualTo(1));
            }

            List<ContactPoint> points = AllPoints(manifolds);
            Assert.That(HasPointNear(points, new float3(1.5f, 1f, 1.5f)), Is.True);
            Assert.That(HasPointNear(points, new float3(2.5f, 1f, 1.5f)), Is.True);
            Assert.That(HasPointNear(points, new float3(1.5f, 1f, 2.5f)), Is.True);
            Assert.That(HasPointNear(points, new float3(2.5f, 1f, 2.5f)), Is.True);
        }

        [Test]
        public void LargePatch_UsesSparseBoundarySources()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            for (int x = 0; x < 10; x++)
            {
                for (int z = 0; z < 10; z++)
                {
                    a.Set(x, 0, z);
                }
            }

            for (int x = 0; x < 12; x++)
            {
                for (int z = 0; z < 12; z++)
                {
                    b.Set(x, 0, z);
                }
            }

            a.Build();
            b.Build();

            // A 10x10 slab on a 12x12 floor. Sparse key sourcing keeps its 36 boundary voxels;
            // the 64 face-interior voxels do not become sources. Finite target squares provide
            // the shared flat plane under each boundary key.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 1f, 1f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(36), "Only sparse boundary keys support the patch");
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(m.Points.Count, Is.EqualTo(1));
                Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance));
                Assert.That(m.Points[0].Distance, Is.EqualTo(0f).Within(Tolerance));
            }

            // The corner contacts remain part of the sparse boundary support set.
            List<ContactPoint> points = AllPoints(manifolds);
            Assert.That(HasPointNear(points, new float3(1.5f, 1f, 1.5f)), Is.True);
            Assert.That(HasPointNear(points, new float3(10.5f, 1f, 1.5f)), Is.True);
            Assert.That(HasPointNear(points, new float3(1.5f, 1f, 10.5f)), Is.True);
            Assert.That(HasPointNear(points, new float3(10.5f, 1f, 10.5f)), Is.True);
        }

        [Test]
        public void RotatedVoxelInSnugSlot_NoJamming_SphereMetricIsRotationInvariant()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            b.Set(2, 0, 0);
            a.Build();
            b.Build();

            // Same snug slot, but A is rotated 30 degrees around Y with its voxel center kept at
            // the slot center. Rounded (sphere) voxels must neither jam nor change the contact:
            // this is what lets a 1-wide pole spin freely inside a 1-wide hole.
            float3 center = new float3(1.5f, 0.5f, 0.5f);
            quaternion rotation = quaternion.RotateY(math.radians(30f));
            float3 translation = center - math.rotate(rotation, new float3(0.5f, 0.5f, 0.5f));

            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(rotation, translation),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(2));
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(m.Points.Count, Is.EqualTo(1));
                Assert.That(math.abs(m.Points[0].Distance), Is.LessThan(1e-3f),
                    "Rotation must not create penetration (no jamming of rotated voxels)");
            }
        }

        [Test]
        public void CornerContact_KeepsRoundedDiagonalNormal()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            a.Build();
            b.Build();

            // A touches B's corner region diagonally (center-to-center distance exactly 1).
            // Real corners keep their diagonal sphere normal: voxels are rounded, so poles can
            // roll/pivot over edges instead of catching on them.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0.8f, 0.6f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            ParsedManifold m = manifolds[0];
            Assert.That(m.Points.Count, Is.EqualTo(1));
            Assert.That(m.Points[0].Distance, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(math.distance(m.Header.Normal, new float3(0.8f, 0.6f, 0f)), Is.LessThan(1e-3f),
                "Corner contact must keep the rounded diagonal normal");
        }

        [Test]
        public void PegInSnugHole_ContactsOnlyOnLateralFaces()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();

            // Vertical 2-voxel peg.
            a.Set(0, 0, 0);
            a.Set(0, 1, 0);

            // 3x3 collar with a 1x1 vertical hole in the middle, 2 voxels tall.
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    for (int z = 0; z < 3; z++)
                    {
                        if (x == 1 && z == 1)
                        {
                            continue;
                        }

                        b.Set(x, y, z);
                    }
                }
            }

            a.Build();
            b.Build();

            // Peg exactly inside the hole. Finite wall features must constrain only the two
            // lateral axes. They must not constrain the slide axis.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 0f, 1f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.GreaterThanOrEqualTo(4));

            int contactsX = 0;
            int contactsZ = 0;
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(m.Points.Count, Is.EqualTo(1));
                Assert.That(math.abs(m.Points[0].Distance), Is.LessThan(1e-3f));
                if (math.abs(m.Header.Normal.x) > 0.9f)
                {
                    contactsX++;
                }
                else if (math.abs(m.Header.Normal.z) > 0.9f)
                {
                    contactsZ++;
                }
                else
                {
                    Assert.Fail("Peg must not be constrained along its slide axis (y)");
                }
            }

            Assert.That(contactsX, Is.GreaterThanOrEqualTo(2));
            Assert.That(contactsZ, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void SegmentEndpointHandoff_KeepsDistanceContinuous()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            b.Set(1, 0, 0);
            a.Build();
            b.Build();

            // B's two centers form a finite segment ending at x=1.5. Before the endpoint, the
            // segment owns the closest core point. After it, the end voxel's point owns it. Both
            // features describe the same capsule, so the distance cannot jump at the handoff.
            float before = MinimumDistance(Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0.99f, 0.9f, 0f)),
                RigidTransform.identity,
                out _));
            float at = MinimumDistance(Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1.0f, 0.9f, 0f)),
                RigidTransform.identity,
                out _));
            float after = MinimumDistance(Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1.01f, 0.9f, 0f)),
                RigidTransform.identity,
                out _));

            Assert.That(before, Is.EqualTo(-0.1f).Within(2e-4f));
            Assert.That(at, Is.EqualTo(-0.1f).Within(2e-4f));
            Assert.That(after, Is.EqualTo(math.sqrt(0.9f * 0.9f + 0.01f * 0.01f) - 1f).Within(2e-4f));
            Assert.That(math.abs(after - before), Is.LessThan(2e-4f));
        }

        [Test]
        public void InsideCornerStep_NoPhantomDeepContact()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);

            // An inside corner: a step cell (0,0,1) and a wall cell (0,1,0) meeting at the corner
            // cell (0,0,0), whose +y and +z faces are both unexposed while +-x stay exposed.
            b.Set(0, 0, 0);
            b.Set(0, 1, 0);
            b.Set(0, 0, 1);
            a.Build();
            b.Build();

            // A rests in the corner, diagonally offset (~0, +1, +1) from the corner cell, with a
            // tiny x misalignment. The cubical complex contains two segments. It does not contain
            // the missing YZ square, so it cannot fabricate a deep contact along x.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0.0002f, 1f, 1f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(2), "Only the step and wall face contacts");
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(m.Points[0].Distance, Is.GreaterThan(-0.01f),
                    "No fabricated deep contact from the inside corner");
                Assert.That(math.abs(m.Header.Normal.x), Is.LessThan(0.5f),
                    "No contact along the corner cell's exposed axis");
            }
        }
    }
}
