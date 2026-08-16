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
        public void RefreshPhysicsSlotBuildsCompactTopologyAndInteriorBit()
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

                // Interior center: all six face neighbors are solid.
                Assert.That(PhysicsData(sector, 1, 1, 1).IsInterior, Is.True);
                // Face, edge and corner blocks are not interior. Their selection as physics keys
                // is stored only in the slot's aux bitmap.
                Assert.That(PhysicsData(sector, 1, 1, 0).IsInterior, Is.False);
                Assert.That(PhysicsData(sector, 1, 0, 0).IsInterior, Is.False);
                Assert.That(PhysicsData(sector, 0, 0, 0).IsInterior, Is.False);
                // Air block inside the allocated brick is cleared, not stale.
                Assert.That(PhysicsData(sector, 5, 5, 5).data, Is.EqualTo(0));

                Assert.That(UnsafeUtility.SizeOf<PhysicsInfo>(), Is.EqualTo(1));
                Assert.That(ForwardOccupancy(sector, 0, 0, 0), Is.EqualTo(0x7f),
                    "The minimum corner roots a complete positive 2x2x2 cubical cell");
                Assert.That(ForwardOccupancy(sector, 2, 2, 2), Is.EqualTo(0),
                    "The maximum corner has no positive occupied neighbor");
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

            // A 3x3x3 cube contains 8 Corner and 12 Edge voxels. Its 6 face centers and one
            // interior voxel must not be selected by the physics-key bitmap.
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

                for (int z = 0; z < 3; z++)
                {
                    for (int y = 0; y < 3; y++)
                    {
                        for (int x = 0; x < 3; x++)
                        {
                            int boundaryAxes = (x == 0 || x == 2 ? 1 : 0) +
                                               (y == 0 || y == 2 ? 1 : 0) +
                                               (z == 0 || z == 2 ? 1 : 0);
                            if (boundaryAxes < 2) { continue; }

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

        private static int ForwardOccupancy(SectorHandle sector, int x, int y, int z)
        {
            return sector.GetSlot<PhysicsInfo>(SectorSlotId.PhysicsInfo, x, y, z).ForwardOccupancy;
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
