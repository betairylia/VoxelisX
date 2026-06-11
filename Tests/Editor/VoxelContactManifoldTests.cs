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
    /// Tests for the voxel-voxel narrowphase (ManifoldQueries.VoxelVoxel): face-aligned contact
    /// generation, voxel-center snapping + merging, corner support points, and the fusion of
    /// exactly opposing contacts into bilateral (equality) constraints.
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

            /// <summary>Recomputes PhysicsInfo exposure data and the non-empty brick list.</summary>
            public void Build()
            {
                ref Sector sector = ref m_Sector.Get();
                for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                {
                    sector.MarkBrickRequireUpdate(i, DirtyFlags.GeometryWithLocalNeighbor);
                }

                sector.UpdateNonEmptyBricks();

                var bodyData = new VoxelBodyData(Allocator.Persistent);
                try
                {
                    bodyData.ComputePhysicsProperties(Scope.Data.sectors, Scope.Data.sectorNeighbors);
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

            public bool IsBilateral => (Header.JacobianFlags & JacobianFlags.IsBilateral) != 0;
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

        static bool HasPointNear(ParsedManifold manifold, float3 position, float tolerance = 1e-3f)
        {
            foreach (ContactPoint point in manifold.Points)
            {
                if (math.all(math.abs(point.Position - position) < tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void StackedCube_ProducesOneManifoldWithFaceNormalAndFourCorners()
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

            // Exactly one manifold: no duplicate write of the last manifold (old bug), and no
            // spurious side contacts.
            Assert.That(manifolds.Count, Is.EqualTo(1));

            ParsedManifold m = manifolds[0];
            Assert.That(m.IsBilateral, Is.False);
            Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance));

            // 4 corner support points on the face plane y = 1, all touching (distance ~ 0).
            Assert.That(m.Points.Count, Is.EqualTo(4));
            foreach (ContactPoint point in m.Points)
            {
                Assert.That(point.Distance, Is.EqualTo(0f).Within(Tolerance));
                Assert.That(point.Position.y, Is.EqualTo(1f).Within(Tolerance));
            }

            Assert.That(HasPointNear(m, new float3(0f, 1f, 0f)), Is.True);
            Assert.That(HasPointNear(m, new float3(1f, 1f, 0f)), Is.True);
            Assert.That(HasPointNear(m, new float3(0f, 1f, 1f)), Is.True);
            Assert.That(HasPointNear(m, new float3(1f, 1f, 1f)), Is.True);

            // The per-block-pair gameplay event is still emitted.
            Assert.That(events.Count, Is.EqualTo(1));
            Assert.That(events[0].IsPhysicsContact, Is.True);
            Assert.That(math.all(events[0].VoxelInA == int3.zero), Is.True);
            Assert.That(math.all(events[0].VoxelInB == int3.zero), Is.True);
        }

        [Test]
        public void OffsetStack_RestsOnFaceNormal_NoDiagonalNormals()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            b.Set(1, 0, 0);
            a.Build();
            b.Build();

            // A resting on top of B, shifted half a voxel sideways. The old sphere-based contacts
            // either missed this entirely or produced a diagonal normal.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(0.5f, 1f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            ParsedManifold m = manifolds[0];
            Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance),
                "Resting contact must use the face normal, not a center-to-center diagonal");
            Assert.That(m.Points.Count, Is.EqualTo(4));
            foreach (ContactPoint point in m.Points)
            {
                Assert.That(point.Distance, Is.EqualTo(0f).Within(Tolerance));
            }
        }

        [Test]
        public void SnugSlot_FusesOpposingContactsIntoBilateralConstraint()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0); // left wall
            b.Set(2, 0, 0); // right wall
            a.Build();
            b.Build();

            // A sits exactly in the 1-voxel slot between the walls.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1), "Opposing contacts must fuse into one constraint");

            ParsedManifold m = manifolds[0];
            Assert.That(m.IsBilateral, Is.True);
            Assert.That(m.Header.CoefficientOfRestitution, Is.EqualTo(0f));
            Assert.That(math.distance(math.abs(m.Header.Normal), new float3(1f, 0f, 0f)), Is.LessThan(Tolerance));

            // A single equality point at the voxel center, already centered (target distance 0).
            Assert.That(m.Points.Count, Is.EqualTo(1));
            Assert.That(math.all(math.abs(m.Points[0].Position - new float3(1.5f, 0.5f, 0.5f)) < 1e-3f), Is.True);
            Assert.That(m.Points[0].Distance, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void ShiftedSnugSlot_BilateralConstraintTargetsSlackCenter()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0);
            b.Set(2, 0, 0);
            a.Build();
            b.Build();

            // A pushed 0.02 into the right wall (still within the equality slop).
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1.02f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            ParsedManifold m = manifolds[0];
            Assert.That(m.IsBilateral, Is.True);

            // distance = (gap(+x face) - gap(-x face)) / 2 = (0.02 - (-0.02)) / 2 = 0.02 with the
            // +x normal: the solver pulls A back towards the slot center.
            float sign = m.Header.Normal.x > 0f ? 1f : -1f;
            Assert.That(sign * m.Points[0].Distance, Is.EqualTo(0.02f).Within(1e-3f));
        }

        [Test]
        public void LooseSlot_KeepsUnilateralContact()
        {
            using var a = new VoxelBodyFixture();
            using var b = new VoxelBodyFixture();
            a.Set(0, 0, 0);
            b.Set(0, 0, 0); // left wall
            b.Set(3, 0, 0); // right wall, 2-voxel slot
            a.Build();
            b.Build();

            // A leans against the left wall of a loose slot.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 0f, 0f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            Assert.That(manifolds[0].IsBilateral, Is.False, "A loose fit must stay unilateral (free to rattle)");
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
        public void FlatPatch_MergesIntoOneManifoldWithDedupedCorners()
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

            // 2x2 slab resting on a 4x4 floor: 4 (A voxel, +y) buckets whose corner points merge
            // into a single shared manifold with a (2+1)x(2+1) corner lattice.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 1f, 1f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1), "All same-normal contacts must merge into one manifold");
            ParsedManifold m = manifolds[0];
            Assert.That(math.distance(m.Header.Normal, new float3(0f, 1f, 0f)), Is.LessThan(Tolerance));
            Assert.That(m.Points.Count, Is.EqualTo(9), "Shared corners between neighboring voxels must deduplicate");
        }

        [Test]
        public void LargePatch_ReducesToManifoldLimitKeepingRimCorners()
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

            // 10x10 slab on a 12x12 floor: 121 corner points reduce to the 32 point manifold
            // limit, and the reduction must keep the rim extremes (support polygon).
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 1f, 1f)),
                RigidTransform.identity,
                out _);

            Assert.That(manifolds.Count, Is.EqualTo(1));
            ParsedManifold m = manifolds[0];
            Assert.That(m.Points.Count, Is.EqualTo(32));
            Assert.That(HasPointNear(m, new float3(1f, 1f, 1f)), Is.True, "Rim corner must survive reduction");
            Assert.That(HasPointNear(m, new float3(11f, 1f, 1f)), Is.True, "Rim corner must survive reduction");
            Assert.That(HasPointNear(m, new float3(1f, 1f, 11f)), Is.True, "Rim corner must survive reduction");
            Assert.That(HasPointNear(m, new float3(11f, 1f, 11f)), Is.True, "Rim corner must survive reduction");
        }

        [Test]
        public void PegInSnugHole_LocksBothLateralAxes_LeavesSlideAxisFree()
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

            // Peg exactly inside the hole.
            List<ParsedManifold> manifolds = Collide(
                a, b,
                new RigidTransform(quaternion.identity, new float3(1f, 0f, 1f)),
                RigidTransform.identity,
                out _);

            int bilateralX = 0;
            int bilateralZ = 0;
            foreach (ParsedManifold m in manifolds)
            {
                Assert.That(m.IsBilateral, Is.True, "A snug peg must produce only equality constraints");
                if (math.abs(m.Header.Normal.x) > 0.9f)
                {
                    bilateralX += m.Points.Count;
                }
                else if (math.abs(m.Header.Normal.z) > 0.9f)
                {
                    bilateralZ += m.Points.Count;
                }
                else
                {
                    Assert.Fail("Peg must not be constrained along its slide axis (y)");
                }
            }

            // One equality point per peg voxel per locked axis: rigid against translation and
            // tilt, while the y axis stays free to slide.
            Assert.That(bilateralX, Is.EqualTo(2));
            Assert.That(bilateralZ, Is.EqualTo(2));
        }
    }
}
