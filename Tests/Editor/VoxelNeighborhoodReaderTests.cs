using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests.Editor
{
    public class VoxelNeighborhoodReaderTests
    {
        [Test]
        public void GetBlock_PrefersLocalBlockOverAlien()
        {
            using var self = new EntityDataTestScope();
            using var alien = new EntityDataTestScope();

            var selfSector = self.AddSector(int3.zero);
            selfSector.SetBlock(1, 2, 3, new Block(7));

            var alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(1, 2, 3, new Block(9));

            using NativeArray<AlienEntityView> views = new NativeArray<AlienEntityView>(2, Allocator.Temp);
            views[0] = BuildView(0, self.Data);
            views[1] = BuildView(1, alien.Data);

            var reader = VoxelNeighborhoodReader.Create(new VoxelisXWorld.BrickInfo
            {
                EntityId = 0,
                SectorPos = int3.zero,
                LocalToWorld = RigidTransform.identity,
                Sector = selfSector,
                Neighbors = self.NeighborsAt(int3.zero)
            }, new AlienOccupancyQuery { EntitiesInDeterministicOrder = views });

            Assert.That(reader.GetBlock(new int3(1, 2, 3)), Is.EqualTo(new Block(7)));
        }

        [Test]
        public void GetBlock_FallsBackToAlienBlockWhenLocalEmpty()
        {
            using var self = new EntityDataTestScope();
            using var alien = new EntityDataTestScope();

            var selfSector = self.AddSector(int3.zero);
            var alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(4, 5, 6, new Block(11));

            using NativeArray<AlienEntityView> views = new NativeArray<AlienEntityView>(2, Allocator.Temp);
            views[0] = BuildView(0, self.Data);
            views[1] = BuildView(1, alien.Data);

            var reader = VoxelNeighborhoodReader.Create(new VoxelisXWorld.BrickInfo
            {
                EntityId = 0,
                SectorPos = int3.zero,
                LocalToWorld = RigidTransform.identity,
                Sector = selfSector,
                Neighbors = self.NeighborsAt(int3.zero)
            }, new AlienOccupancyQuery { EntitiesInDeterministicOrder = views });

            Assert.That(reader.GetBlock(new int3(4, 5, 6)), Is.EqualTo(new Block(11)));
            Assert.That(reader.IsVoxelSpaceOccupied(new int3(4, 5, 6)), Is.True);
        }

        private static AlienEntityView BuildView(int id, VoxelEntityData data)
        {
            return new AlienEntityView
            {
                EntityId = id,
                LocalToWorld = RigidTransform.identity,
                WorldToLocal = float4x4.identity,
                Sectors = data.sectors,
                WorldAabbMin = new float3(-512f),
                WorldAabbMax = new float3(512f)
            };
        }
    }
}
