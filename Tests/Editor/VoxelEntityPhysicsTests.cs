using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using Voxelis.Simulation;
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
    }
}
