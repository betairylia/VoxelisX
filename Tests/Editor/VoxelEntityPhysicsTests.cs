using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis;
using Voxelis.Simulation;
using Voxelis.Utils;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    public class VoxelEntityPhysicsTests
    {
        [BurstCompile]
        private struct CountPhysicsKeyBlocksJob : IJob
        {
            public SectorHandle Sector;
            [WriteOnly] public NativeArray<int> Result;

            public void Execute()
            {
                int count = 0;
                foreach (SectorBitmaskSlotIterator<PhysicsInfo> item in
                         Sector.Get().EnumeratePhysicsKeyBlocks())
                {
                    count++;
                }

                Result[0] = count;
            }
        }

        [Test]
        public void SectorMassMomentsForSingleBlockUseVoxelCenter()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            VoxelEntityPhysics.SectorMassMoments moments =
                VoxelEntityPhysics.ComputeSectorMassMoments(sector.Get(), int3.zero, PhysicsSettings.Settings);

            Assert.That(moments.Mass, Is.EqualTo(1f));
            Assert.That(moments.FirstMoment, Is.EqualTo(new float3(0.5f, 0.5f, 0.5f)));
            Assert.That(moments.InertiaOrigin, Is.EqualTo(new float3(0.5f, 0.5f, 0.5f)));
            Assert.That(VoxelEntityPhysics.InertiaAroundCenterOfMass(moments, moments.FirstMoment / moments.Mass),
                Is.EqualTo(float3.zero));
        }

        [Test]
        public void InertiaAroundCenterOfMassUsesParallelAxisTheorem()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            sector.SetBlock(2, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            VoxelEntityPhysics.SectorMassMoments moments =
                VoxelEntityPhysics.ComputeSectorMassMoments(sector.Get(), int3.zero, PhysicsSettings.Settings);

            float3 centerOfMass = moments.FirstMoment / moments.Mass;
            float3 inertia = VoxelEntityPhysics.InertiaAroundCenterOfMass(moments, centerOfMass);

            Assert.That(moments.Mass, Is.EqualTo(2f));
            Assert.That(centerOfMass, Is.EqualTo(new float3(1.5f, 0.5f, 0.5f)));
            Assert.That(inertia, Is.EqualTo(new float3(0f, 2f, 2f)));
        }

        [Test]
        public void SectorMassMomentsIncludeSectorBlockPosition()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            VoxelEntityPhysics.SectorMassMoments moments =
                VoxelEntityPhysics.ComputeSectorMassMoments(
                    sector.Get(),
                    new int3(Sector.SECTOR_SIZE_IN_BLOCKS, 0, 0),
                    PhysicsSettings.Settings);

            Assert.That(moments.Mass, Is.EqualTo(1f));
            Assert.That(moments.FirstMoment, Is.EqualTo(new float3(128.5f, 0.5f, 0.5f)));
        }

        [Test]
        public void VoxelBodyDataComputesMassPropertiesFromEntitySectors()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                VoxelBodyData.MassProperties massProperties =
                    bodyData.ComputePhysicsProperties(scope.Data);

                Assert.That(massProperties.mass, Is.EqualTo(1f));
                Assert.That(massProperties.centerOfMass, Is.EqualTo(new float3(0.5f, 0.5f, 0.5f)));
                Assert.That(massProperties.inertiaTensor, Is.EqualTo(float3.zero));
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        [Test]
        public void VoxelBodyDataClearsMassPropertiesForStaticBodies()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                Assert.That(bodyData.ComputePhysicsProperties(scope.Data).mass, Is.EqualTo(1f));

                scope.Data.isStatic = true;
                VoxelBodyData.MassProperties massProperties =
                    bodyData.ComputePhysicsProperties(scope.Data);

                Assert.That(massProperties.mass, Is.EqualTo(0f));
                Assert.That(massProperties.centerOfMass, Is.EqualTo(float3.zero));
                Assert.That(massProperties.inertiaTensor, Is.EqualTo(float3.zero));
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        [Test]
        public void RefreshPhysicsSlotClassifiesSolidCubeBoundary()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);

            // Solid 3x3x3 cube hugging the origin corner; entirely inside brick (0,0,0).
            for (int z = 0; z < 3; z++)
            {
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        sector.SetBlock(x, y, z, new Block(1));
                    }
                }
            }

            // Physics-slot generation is gated on the require-update (read) buffer that dirty
            // propagation would normally populate; mark it directly since no propagation runs here.
            sector.Get().MarkBrickRequireUpdate(Sector.ToBrickIdx(0, 0, 0), DirtyFlags.GeometryWithLocalNeighbor);
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);

                Assert.That(UnsafeUtility.SizeOf<PhysicsInfo>(), Is.EqualTo(1));

                // The minimum corner is a geometric corner of the box, so every surface cell rooted
                // there is active at once: three boundary faces, three convex edges and the point.
                // The cube exists too, which is what makes all eight bits set.
                Assert.That(PhysicsData(sector, 0, 0, 0).data, Is.EqualTo(0xFF),
                    "Minimum corner roots every cell, including all seven surface features");

                // A voxel on a box edge keeps the edge running along that box edge, the two boundary
                // faces meeting there, and its cube. Its point is absorbed by the collinear edges.
                Assert.That(PhysicsData(sector, 1, 0, 0).data, Is.EqualTo(
                    (1 << PhysicsInfo.BitEdgeX) | (1 << PhysicsInfo.BitFaceXY) |
                    (1 << PhysicsInfo.BitFaceXZ) | (1 << PhysicsInfo.BitCube)));

                // A voxel in the middle of a flat boundary face keeps only that face. Its edges are
                // flat subdivisions and its point is interior to the face.
                Assert.That(PhysicsData(sector, 1, 1, 0).data, Is.EqualTo(
                    (1 << PhysicsInfo.BitFaceXY) | (1 << PhysicsInfo.BitCube)));

                // The centre voxel is deep inside solid: no surface feature at all, only the volume
                // cube. This is the case IsInterior now names.
                Assert.That(PhysicsData(sector, 1, 1, 1).data,
                    Is.EqualTo(1 << PhysicsInfo.BitCube));
                Assert.That(PhysicsData(sector, 1, 1, 1).IsInterior, Is.True);
                Assert.That(PhysicsData(sector, 1, 1, 1).HasVolumeCell, Is.True);

                // The maximum side roots no cell that grows forward, but it is still real boundary.
                // A max-face voxel keeps the two in-plane edges and the face they bound; the max
                // corner keeps only its point. Under containment dedup all three read as zero.
                Assert.That(PhysicsData(sector, 2, 0, 0).data, Is.EqualTo(
                    (1 << PhysicsInfo.BitPoint) | (1 << PhysicsInfo.BitEdgeY) |
                    (1 << PhysicsInfo.BitEdgeZ) | (1 << PhysicsInfo.BitFaceYZ)));
                Assert.That(PhysicsData(sector, 2, 2, 0).data, Is.EqualTo(
                    (1 << PhysicsInfo.BitPoint) | (1 << PhysicsInfo.BitEdgeZ)));
                Assert.That(PhysicsData(sector, 2, 2, 2).data,
                    Is.EqualTo(1 << PhysicsInfo.BitPoint));
                Assert.That(PhysicsData(sector, 2, 2, 2).IsInterior, Is.False);

                // Keys are the roots carrying an active point or edge, i.e. the twelve box edge
                // chains and the eight corners. The six face centres and the centre are not keys.
                Assert.That(IsPhysicsKey(sector, 0, 0, 0), Is.True);
                Assert.That(IsPhysicsKey(sector, 2, 2, 2), Is.True);
                Assert.That(IsPhysicsKey(sector, 1, 0, 0), Is.True);
                Assert.That(IsPhysicsKey(sector, 1, 1, 0), Is.False);
                Assert.That(IsPhysicsKey(sector, 1, 1, 1), Is.False);

                // Air block inside the allocated brick is cleared, not stale.
                Assert.That(PhysicsData(sector, 5, 5, 5).data, Is.EqualTo(0));
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        [Test]
        public void RefreshPhysicsSlotRootsIsolatedVoxelAsPoint()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(1, 1, 1, new Block(1));
            sector.Get().MarkBrickRequireUpdate(
                Sector.ToBrickIdx(0, 0, 0), DirtyFlags.GeometryWithLocalNeighbor);
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);

                // With no face neighbor the bare point is the only cell, and it is the whole body.
                Assert.That(PhysicsData(sector, 1, 1, 1).data,
                    Is.EqualTo(1 << PhysicsInfo.BitPoint));
                Assert.That(PhysicsData(sector, 1, 1, 1).HasPointFeature, Is.True);
                Assert.That(IsPhysicsKey(sector, 1, 1, 1), Is.True);
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        [Test]
        public void RefreshPhysicsSlotKeepsBothWireEndpoints()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(1, 1, 1, new Block(1));
            sector.SetBlock(2, 1, 1, new Block(1));
            sector.Get().MarkBrickRequireUpdate(
                Sector.ToBrickIdx(0, 0, 0), DirtyFlags.GeometryWithLocalNeighbor);
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);

                // The segment carries the geometry, but both endpoints stay active: an endpoint owns
                // the cap of directions past the end of the segment, which the segment does not.
                // Those two points are what lets a wire form a permitted pair against a face.
                Assert.That(PhysicsData(sector, 1, 1, 1).data, Is.EqualTo(
                    (1 << PhysicsInfo.BitEdgeX) | (1 << PhysicsInfo.BitPoint)));
                Assert.That(PhysicsData(sector, 2, 1, 1).data,
                    Is.EqualTo(1 << PhysicsInfo.BitPoint));

                // Both roots carry a point or an edge, so both are contact sources.
                Assert.That(IsPhysicsKey(sector, 1, 1, 1), Is.True);
                Assert.That(IsPhysicsKey(sector, 2, 1, 1), Is.True);
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        [Test]
        public void RefreshPhysicsSlotKeepsBothArmsOfAnLCorner()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(1, 1, 1, new Block(1));
            sector.SetBlock(2, 1, 1, new Block(1));
            sector.SetBlock(1, 2, 1, new Block(1));
            sector.Get().MarkBrickRequireUpdate(
                Sector.ToBrickIdx(0, 0, 0), DirtyFlags.GeometryWithLocalNeighbor);
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);

                // No square exists, so both segments stay on the corner voxel and no diagonal square
                // is fabricated. The corner point is active too - the two arms are not collinear, so
                // they cannot absorb it - and each arm end keeps its own endpoint.
                Assert.That(PhysicsData(sector, 1, 1, 1).data, Is.EqualTo(
                    (1 << PhysicsInfo.BitEdgeX) | (1 << PhysicsInfo.BitEdgeY) |
                    (1 << PhysicsInfo.BitPoint)));
                Assert.That(PhysicsData(sector, 2, 1, 1).data,
                    Is.EqualTo(1 << PhysicsInfo.BitPoint));
                Assert.That(PhysicsData(sector, 1, 2, 1).data,
                    Is.EqualTo(1 << PhysicsInfo.BitPoint));
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        [Test]
        public unsafe void PhysicsKeyEnumeratorFollowsPhysicsInfoBitmapInVoxelIndexOrder()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);

            // In a 3x3x3 cube the keys are the roots carrying an active point or edge: the eight
            // geometric corners and the twelve box edge chains. The six face centres keep only a
            // flat face and the centre keeps only the volume cube, so neither is a key. That is
            // exactly "at least two coordinates on an extreme", i.e. 8 + 12 = 20 of the 27.
            for (int z = 0; z < 3; z++)
            {
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        sector.SetBlock(x, y, z, new Block(1));
                    }
                }
            }

            sector.Get().MarkBrickRequireUpdate(
                Sector.ToBrickIdx(0, 0, 0), DirtyFlags.GeometryWithLocalNeighbor);
            scope.Data.RefreshNonEmptyMask();

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);
                ref Sector source = ref sector.Get();
                Assert.That(source.slots[(int)SectorSlotId.PhysicsInfo].HasAux, Is.True);

                SectorBitmaskSlotEnumerator<PhysicsInfo> enumerator =
                    source.EnumeratePhysicsKeyBlocks();
                int selected = 0;

                // Walk in flat voxel-index order (x fastest, then y, then z) and require the
                // enumerator to visit exactly the expected roots in that order.
                for (int z = 0; z < 3; z++)
                {
                    for (int y = 0; y < 3; y++)
                    {
                        for (int x = 0; x < 3; x++)
                        {
                            int extremes = (x == 1 ? 0 : 1) + (y == 1 ? 0 : 1) + (z == 1 ? 0 : 1);
                            if (extremes < 2)
                            {
                                Assert.That(IsPhysicsKey(sector, x, y, z), Is.False,
                                    $"({x},{y},{z}) is a face centre or the centre");
                                continue;
                            }

                            Assert.That(enumerator.MoveNext(), Is.True);
                            Assert.That(enumerator.Current.position, Is.EqualTo(new int3(x, y, z)));
                            Assert.That(enumerator.Current.value.IsInterior, Is.False);
                            selected++;
                        }
                    }
                }

                Assert.That(selected, Is.EqualTo(20));
                Assert.That(enumerator.MoveNext(), Is.False);

                enumerator.Reset();
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(enumerator.Current.position, Is.EqualTo(int3.zero));

                using var burstCount = new NativeArray<int>(1, Allocator.TempJob);
                new CountPhysicsKeyBlocksJob
                {
                    Sector = sector,
                    Result = burstCount
                }.Schedule().Complete();
                Assert.That(burstCount[0], Is.EqualTo(20));
            }
            finally
            {
                bodyData.Dispose();
            }
        }

        private static PhysicsInfo PhysicsData(SectorHandle sector, int x, int y, int z)
        {
            return sector.GetSlot<PhysicsInfo>(SectorSlotId.PhysicsInfo, x, y, z);
        }

        private static unsafe bool IsPhysicsKey(SectorHandle sector, int x, int y, int z)
        {
            ref Sector source = ref sector.Get();
            short bid = source.brickIdx[Sector.ToBrickIdx(
                x >> Sector.SHIFT_IN_BLOCKS, y >> Sector.SHIFT_IN_BLOCKS, z >> Sector.SHIFT_IN_BLOCKS)];
            var mask = (ulong*)source.GetBrickAuxPtr(SectorSlotId.PhysicsInfo, bid);
            return BrickBitmask.GetBit(mask, Sector.ToBlockIdx(
                x & Sector.BRICK_MASK, y & Sector.BRICK_MASK, z & Sector.BRICK_MASK));
        }

        [Test]
        public void PhysicsWorldBuildReadsPersistedMotionFromVoxelBodyData()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            Guid128 guid = new Guid128(1, 2, 3, 4);
            var bodyData = new VoxelBodyData(Allocator.Persistent);
            var world = new PhysicsWorld(0, 0, 0);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent),
                nDynamicBodies = 1
            };
            NativeArray<Guid128> bodyIndexToGuid = default;

            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);
                bodyData._cached_body_index = 0;
                bodyData.motionData = new Unity.Physics.MotionData
                {
                    WorldFromMotion = RigidTransform.identity,
                    BodyFromMotion = RigidTransform.identity,
                    LinearDamping = 0.25f,
                    AngularDamping = 0.5f
                };
                bodyData.motionVelocity = new Unity.Physics.MotionVelocity
                {
                    LinearVelocity = new float3(1f, 2f, 3f),
                    AngularVelocity = new float3(4f, 5f, 6f),
                    InverseInertia = new float3(99f),
                    InverseMass = 99f,
                    AngularExpansionFactor = 7f,
                    GravityFactor = 0.25f
                };

                tickBuf.VoxelEntities.Add(guid, scope.Data);
                tickBuf.VoxelBodies.Add(guid, bodyData);

                // Global air friction (0.05 / 0.08 here) overrides each body's persisted MotionData
                // damping (0.25 / 0.5 above) so friction is a single live-tunable engine knob.
                JobHandle buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                    ref tickBuf,
                    ref world,
                    out bodyIndexToGuid,
                    0.05f,
                    0.08f,
                    default);
                buildHandle.Complete();

                Assert.That(bodyIndexToGuid[0], Is.EqualTo(guid));
                Assert.That(world.MotionDatas[0].LinearDamping, Is.EqualTo(0.05f),
                    "Global linear air friction must override the body's persisted LinearDamping");
                Assert.That(world.MotionDatas[0].AngularDamping, Is.EqualTo(0.08f),
                    "Global angular air friction must override the body's persisted AngularDamping");
                Assert.That(world.MotionVelocities[0].LinearVelocity, Is.EqualTo(new float3(1f, 2f, 3f)));
                Assert.That(world.MotionVelocities[0].AngularVelocity, Is.EqualTo(new float3(4f, 5f, 6f)));
                Assert.That(world.MotionVelocities[0].GravityFactor, Is.EqualTo(0.25f));
            }
            finally
            {
                if (bodyIndexToGuid.IsCreated)
                {
                    bodyIndexToGuid.Dispose();
                }

                world.Dispose();
                tickBuf.VoxelEntities.Dispose();
                tickBuf.VoxelBodies.Dispose();
                bodyData.Dispose();
            }
        }

        [Test]
        public void PhysicsWorldBuildUsesAbsoluteCachedIndicesForDynamicAndStaticBodies()
        {
            using var dynamicScope = new EntityDataTestScope();
            using var staticScope = new EntityDataTestScope();
            dynamicScope.AddSector(int3.zero).SetBlock(0, 0, 0, new Block(1));
            staticScope.AddSector(int3.zero).SetBlock(0, 0, 0, new Block(1));
            dynamicScope.Data.RefreshNonEmptyMask();
            staticScope.Data.RefreshNonEmptyMask();
            staticScope.Data.isStatic = true;

            Guid128 dynamicGuid = new Guid128(20, 21, 22, 23);
            Guid128 staticGuid = new Guid128(24, 25, 26, 27);
            var dynamicBody = new VoxelBodyData(Allocator.Persistent);
            var staticBody = new VoxelBodyData(Allocator.Persistent);
            var world = new PhysicsWorld(0, 0, 0);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(2, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(2, Allocator.Persistent),
                nDynamicBodies = 1
            };
            NativeArray<Guid128> bodyIndexToGuid = default;

            try
            {
                dynamicBody.ComputePhysicsProperties(dynamicScope.Data);
                dynamicBody._cached_body_index = 0;
                staticBody.ComputePhysicsProperties(staticScope.Data);
                staticBody._cached_body_index = 1;

                tickBuf.VoxelEntities.Add(dynamicGuid, dynamicScope.Data);
                tickBuf.VoxelEntities.Add(staticGuid, staticScope.Data);
                tickBuf.VoxelBodies.Add(dynamicGuid, dynamicBody);
                tickBuf.VoxelBodies.Add(staticGuid, staticBody);

                JobHandle buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                    ref tickBuf,
                    ref world,
                    out bodyIndexToGuid,
                    0f,
                    0f,
                    default);
                buildHandle.Complete();

                Assert.That(bodyIndexToGuid[0], Is.EqualTo(dynamicGuid));
                Assert.That(bodyIndexToGuid[1], Is.EqualTo(staticGuid));
            }
            finally
            {
                if (bodyIndexToGuid.IsCreated)
                {
                    bodyIndexToGuid.Dispose();
                }

                world.Dispose();
                tickBuf.VoxelEntities.Dispose();
                tickBuf.VoxelBodies.Dispose();
                dynamicBody.Dispose();
                staticBody.Dispose();
            }
        }

        [Test]
        public void PhysicsWorldExportPersistsMotionForMultipleDynamicBodies()
        {
            using var firstScope = new EntityDataTestScope();
            using var secondScope = new EntityDataTestScope();
            firstScope.AddSector(int3.zero).SetBlock(0, 0, 0, new Block(1));
            secondScope.AddSector(int3.zero).SetBlock(0, 0, 0, new Block(1));
            firstScope.Data.RefreshNonEmptyMask();
            secondScope.Data.RefreshNonEmptyMask();

            Guid128 firstGuid = new Guid128(5, 6, 7, 8);
            Guid128 secondGuid = new Guid128(9, 10, 11, 12);
            var firstBody = new VoxelBodyData(Allocator.Persistent);
            var secondBody = new VoxelBodyData(Allocator.Persistent);
            var world = new PhysicsWorld(0, 0, 0);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(2, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(2, Allocator.Persistent),
                nDynamicBodies = 2
            };
            NativeArray<Guid128> bodyIndexToGuid = default;
            bool exportScheduled = false;

            try
            {
                firstBody.ComputePhysicsProperties(firstScope.Data);
                firstBody._cached_body_index = 0;
                secondBody.ComputePhysicsProperties(secondScope.Data);
                secondBody._cached_body_index = 1;
                tickBuf.VoxelEntities.Add(firstGuid, firstScope.Data);
                tickBuf.VoxelEntities.Add(secondGuid, secondScope.Data);
                tickBuf.VoxelBodies.Add(firstGuid, firstBody);
                tickBuf.VoxelBodies.Add(secondGuid, secondBody);

                JobHandle buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                    ref tickBuf,
                    ref world,
                    out bodyIndexToGuid,
                    0f,
                    0f,
                    default);
                buildHandle.Complete();

                NativeArray<Unity.Physics.MotionData> motionDatas = world.MotionDatas;
                Unity.Physics.MotionData exportedMotionData = motionDatas[0];
                exportedMotionData.WorldFromMotion = new RigidTransform(quaternion.identity, new float3(10f, 20f, 30f));
                motionDatas[0] = exportedMotionData;
                Unity.Physics.MotionData secondExportedMotionData = motionDatas[1];
                secondExportedMotionData.WorldFromMotion =
                    new RigidTransform(quaternion.identity, new float3(40f, 50f, 60f));
                motionDatas[1] = secondExportedMotionData;

                NativeArray<Unity.Physics.MotionVelocity> motionVelocities = world.MotionVelocities;
                Unity.Physics.MotionVelocity exportedMotionVelocity = motionVelocities[0];
                exportedMotionVelocity.LinearVelocity = new float3(2f, 4f, 6f);
                exportedMotionVelocity.AngularVelocity = new float3(1f, 3f, 5f);
                exportedMotionVelocity.GravityFactor = 0.75f;
                motionVelocities[0] = exportedMotionVelocity;
                Unity.Physics.MotionVelocity secondExportedMotionVelocity = motionVelocities[1];
                secondExportedMotionVelocity.LinearVelocity = new float3(8f, 10f, 12f);
                secondExportedMotionVelocity.AngularVelocity = new float3(7f, 9f, 11f);
                secondExportedMotionVelocity.GravityFactor = 0.5f;
                motionVelocities[1] = secondExportedMotionVelocity;

                JobHandle exportHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldExport(
                    ref tickBuf,
                    ref world,
                    bodyIndexToGuid,
                    default);
                exportScheduled = true;
                exportHandle.Complete();

                VoxelBodyData exportedFirstBody = tickBuf.VoxelBodies[firstGuid];
                Assert.That(exportedFirstBody.motionData.WorldFromMotion.pos,
                    Is.EqualTo(new float3(10f, 20f, 30f)));
                Assert.That(exportedFirstBody.motionVelocity.LinearVelocity, Is.EqualTo(new float3(2f, 4f, 6f)));
                Assert.That(exportedFirstBody.motionVelocity.AngularVelocity, Is.EqualTo(new float3(1f, 3f, 5f)));
                Assert.That(exportedFirstBody.motionVelocity.GravityFactor, Is.EqualTo(0.75f));

                VoxelBodyData exportedSecondBody = tickBuf.VoxelBodies[secondGuid];
                Assert.That(exportedSecondBody.motionData.WorldFromMotion.pos,
                    Is.EqualTo(new float3(40f, 50f, 60f)));
                Assert.That(exportedSecondBody.motionVelocity.LinearVelocity, Is.EqualTo(new float3(8f, 10f, 12f)));
                Assert.That(exportedSecondBody.motionVelocity.AngularVelocity, Is.EqualTo(new float3(7f, 9f, 11f)));
                Assert.That(exportedSecondBody.motionVelocity.GravityFactor, Is.EqualTo(0.5f));
            }
            finally
            {
                if (!exportScheduled && bodyIndexToGuid.IsCreated)
                {
                    bodyIndexToGuid.Dispose();
                }

                world.Dispose();
                tickBuf.VoxelEntities.Dispose();
                tickBuf.VoxelBodies.Dispose();
                firstBody.Dispose();
                secondBody.Dispose();
            }
        }

        [Test]
        public void BodyForceCommandStreamAppliesMainThreadForceBeforePhysicsBuild()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            Guid128 guid = new Guid128(9, 10, 11, 12);
            var bodyData = new VoxelBodyData(Allocator.Persistent);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent)
            };
            var commands = new VoxelBodyForceCommandStream(Allocator.Persistent);

            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);
                tickBuf.VoxelEntities.Add(guid, scope.Data);
                tickBuf.VoxelBodies.Add(guid, bodyData);

                commands.AddForce(guid, new float3(4f, 0f, 0f), VoxelBodyForceMode.Force);
                commands.ApplyTo(ref tickBuf, 0.5f);

                VoxelBodyData updatedBody = tickBuf.VoxelBodies[guid];
                Assert.That(updatedBody.motionVelocity.LinearVelocity, Is.EqualTo(new float3(2f, 0f, 0f)));
            }
            finally
            {
                commands.Dispose();
                tickBuf.VoxelEntities.Dispose();
                tickBuf.VoxelBodies.Dispose();
                bodyData.Dispose();
            }
        }

        [Test]
        public void BodyForceCommandStreamAppliesOffCenterImpulseTorque()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));
            sector.SetBlock(2, 0, 0, new Block(1));
            scope.Data.RefreshNonEmptyMask();

            Guid128 guid = new Guid128(13, 14, 15, 16);
            var bodyData = new VoxelBodyData(Allocator.Persistent);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent)
            };
            var commands = new VoxelBodyForceCommandStream(Allocator.Persistent);

            try
            {
                bodyData.ComputePhysicsProperties(scope.Data);
                tickBuf.VoxelEntities.Add(guid, scope.Data);
                tickBuf.VoxelBodies.Add(guid, bodyData);

                // AsJobWriter now reserves capacity for N commands and returns a
                // ParallelWriter-backed writer (AddNoResize); no foreach-index bookkeeping.
                VoxelBodyForceCommandStream.JobWriter writer = commands.AsJobWriter(1);
                float3 centerOfMass = bodyData.massProperties.centerOfMass;
                writer.AddForceAtPosition(
                    guid,
                    new float3(2f, 0f, 0f),
                    centerOfMass + new float3(0f, 1f, 0f),
                    VoxelBodyForceMode.Impulse);
                commands.ApplyTo(ref tickBuf, 1f);

                VoxelBodyData updatedBody = tickBuf.VoxelBodies[guid];
                Assert.That(updatedBody.motionVelocity.LinearVelocity, Is.EqualTo(new float3(1f, 0f, 0f)));
                Assert.That(updatedBody.motionVelocity.AngularVelocity, Is.EqualTo(new float3(0f, 0f, -1f)));
            }
            finally
            {
                commands.Dispose();
                tickBuf.VoxelEntities.Dispose();
                tickBuf.VoxelBodies.Dispose();
                bodyData.Dispose();
            }
        }
    }
}
