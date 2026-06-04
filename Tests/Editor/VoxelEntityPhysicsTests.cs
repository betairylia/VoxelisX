using NUnit.Framework;
using Unity.Collections;
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
        [Test]
        public void SectorMassMomentsForSingleBlockUseVoxelCenter()
        {
            using var scope = new SectorTestScope();
            scope.Set(0, 0, 0);
            scope.Sector.UpdateNonEmptyBricks();

            VoxelEntityPhysics.SectorMassMoments moments =
                VoxelEntityPhysics.ComputeSectorMassMoments(scope.Sector, int3.zero, PhysicsSettings.Settings);

            Assert.That(moments.Mass, Is.EqualTo(1f));
            Assert.That(moments.FirstMoment, Is.EqualTo(new float3(0.5f, 0.5f, 0.5f)));
            Assert.That(moments.InertiaOrigin, Is.EqualTo(new float3(0.5f, 0.5f, 0.5f)));
            Assert.That(VoxelEntityPhysics.InertiaAroundCenterOfMass(moments, moments.FirstMoment / moments.Mass),
                Is.EqualTo(float3.zero));
        }

        [Test]
        public void InertiaAroundCenterOfMassUsesParallelAxisTheorem()
        {
            using var scope = new SectorTestScope();
            scope.Set(0, 0, 0);
            scope.Set(2, 0, 0);
            scope.Sector.UpdateNonEmptyBricks();

            VoxelEntityPhysics.SectorMassMoments moments =
                VoxelEntityPhysics.ComputeSectorMassMoments(scope.Sector, int3.zero, PhysicsSettings.Settings);

            float3 centerOfMass = moments.FirstMoment / moments.Mass;
            float3 inertia = VoxelEntityPhysics.InertiaAroundCenterOfMass(moments, centerOfMass);

            Assert.That(moments.Mass, Is.EqualTo(2f));
            Assert.That(centerOfMass, Is.EqualTo(new float3(1.5f, 0.5f, 0.5f)));
            Assert.That(inertia, Is.EqualTo(new float3(0f, 2f, 2f)));
        }

        [Test]
        public void SectorMassMomentsIncludeSectorBlockPosition()
        {
            using var scope = new SectorTestScope();
            scope.Set(0, 0, 0);
            scope.Sector.UpdateNonEmptyBricks();

            VoxelEntityPhysics.SectorMassMoments moments =
                VoxelEntityPhysics.ComputeSectorMassMoments(
                    scope.Sector,
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

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                VoxelBodyData.MassProperties massProperties =
                    bodyData.ComputeMassProperties(scope.Data.sectors);

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

            var bodyData = new VoxelBodyData(Allocator.Persistent);
            try
            {
                Assert.That(bodyData.ComputeMassProperties(scope.Data.sectors).mass, Is.EqualTo(1f));

                bodyData.isStatic = true;
                VoxelBodyData.MassProperties massProperties =
                    bodyData.ComputeMassProperties(scope.Data.sectors);

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
        public void PhysicsWorldBuildReadsPersistedMotionFromVoxelBodyData()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));

            Guid128 guid = new Guid128(1, 2, 3, 4);
            var bodyData = new VoxelBodyData(Allocator.Persistent);
            var world = new PhysicsWorld(0, 0, 0);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent)
            };
            NativeArray<Guid128> bodyIndexToGuid = default;

            try
            {
                bodyData.ComputeMassProperties(scope.Data.sectors);
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

                JobHandle buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                    ref tickBuf,
                    ref world,
                    out bodyIndexToGuid,
                    out int nDynamic,
                    default);
                buildHandle.Complete();

                Assert.That(nDynamic, Is.EqualTo(1));
                Assert.That(bodyIndexToGuid[0], Is.EqualTo(guid));
                Assert.That(world.MotionDatas[0].LinearDamping, Is.EqualTo(0.25f));
                Assert.That(world.MotionDatas[0].AngularDamping, Is.EqualTo(0.5f));
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
        public void PhysicsWorldExportPersistsMotionBackToVoxelBodyData()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(0, 0, 0, new Block(1));

            Guid128 guid = new Guid128(5, 6, 7, 8);
            var bodyData = new VoxelBodyData(Allocator.Persistent);
            var world = new PhysicsWorld(0, 0, 0);
            var tickBuf = new VoxelisXWorld.WorldStageInputs
            {
                VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent),
                VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent)
            };
            NativeArray<Guid128> bodyIndexToGuid = default;
            bool exportScheduled = false;

            try
            {
                bodyData.ComputeMassProperties(scope.Data.sectors);
                tickBuf.VoxelEntities.Add(guid, scope.Data);
                tickBuf.VoxelBodies.Add(guid, bodyData);

                JobHandle buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                    ref tickBuf,
                    ref world,
                    out bodyIndexToGuid,
                    out int nDynamic,
                    default);
                buildHandle.Complete();

                Unity.Physics.MotionData exportedMotionData = world.MotionDatas[0];
                exportedMotionData.WorldFromMotion = new RigidTransform(quaternion.identity, new float3(10f, 20f, 30f));
                world.MotionDatas[0] = exportedMotionData;

                Unity.Physics.MotionVelocity exportedMotionVelocity = world.MotionVelocities[0];
                exportedMotionVelocity.LinearVelocity = new float3(2f, 4f, 6f);
                exportedMotionVelocity.AngularVelocity = new float3(1f, 3f, 5f);
                exportedMotionVelocity.GravityFactor = 0.75f;
                world.MotionVelocities[0] = exportedMotionVelocity;

                JobHandle exportHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldExport(
                    ref tickBuf,
                    ref world,
                    bodyIndexToGuid,
                    nDynamic,
                    default);
                exportScheduled = true;
                exportHandle.Complete();

                VoxelBodyData exportedBody = tickBuf.VoxelBodies[guid];
                Assert.That(exportedBody.motionData.WorldFromMotion.pos, Is.EqualTo(new float3(10f, 20f, 30f)));
                Assert.That(exportedBody.motionVelocity.LinearVelocity, Is.EqualTo(new float3(2f, 4f, 6f)));
                Assert.That(exportedBody.motionVelocity.AngularVelocity, Is.EqualTo(new float3(1f, 3f, 5f)));
                Assert.That(exportedBody.motionVelocity.GravityFactor, Is.EqualTo(0.75f));
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
                bodyData.Dispose();
            }
        }
    }
}
