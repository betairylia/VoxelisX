using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using Voxelis.Utils;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests.Editor
{
    public class VoxelNeighborhoodReaderTests
    {
        private static readonly Guid128 SelfEntityId = new Guid128(1, 0, 0, 0);
        private static readonly Guid128 AlienEntityId = new Guid128(2, 0, 0, 0);

        [Test]
        public void GetBlock_PrefersLocalBlockOverAlien()
        {
            using var self = new EntityDataTestScope();
            using var alien = new EntityDataTestScope();

            var selfSector = self.AddSector(int3.zero);
            selfSector.SetBlock(1, 2, 3, new Block(7));

            var alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(1, 2, 3, new Block(9));

            var views = new NativeArray<AlienEntityView>(2, Allocator.Temp);
            try
            {
                views[0] = BuildView(SelfEntityId, self.Data);
                views[1] = BuildView(AlienEntityId, alien.Data);

                var reader = VoxelNeighborhoodReader.Create(new VoxelisXCoreWorld.BrickInfo
                {
                    EntityId = SelfEntityId,
                    SectorPos = int3.zero,
                    LocalToWorld = RigidTransform.identity,
                    Sector = selfSector,
                    Neighbors = self.NeighborsAt(int3.zero)
                }, new AlienOccupancyQuery { EntitiesInDeterministicOrder = views });

                Assert.That(reader.GetBlock(new int3(1, 2, 3)), Is.EqualTo(new Block(7)));
            }
            finally
            {
                views.Dispose();
            }
        }

        [Test]
        public void GetBlock_FallsBackToAlienBlockWhenLocalEmpty()
        {
            using var self = new EntityDataTestScope();
            using var alien = new EntityDataTestScope();

            var selfSector = self.AddSector(int3.zero);
            var alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(4, 5, 6, new Block(11));

            var views = new NativeArray<AlienEntityView>(2, Allocator.Temp);
            try
            {
                views[0] = BuildView(SelfEntityId, self.Data);
                views[1] = BuildView(AlienEntityId, alien.Data);

                var reader = VoxelNeighborhoodReader.Create(new VoxelisXCoreWorld.BrickInfo
                {
                    EntityId = SelfEntityId,
                    SectorPos = int3.zero,
                    LocalToWorld = RigidTransform.identity,
                    Sector = selfSector,
                    Neighbors = self.NeighborsAt(int3.zero)
                }, new AlienOccupancyQuery { EntitiesInDeterministicOrder = views });

                Assert.That(reader.GetBlock(new int3(4, 5, 6)), Is.EqualTo(new Block(11)));
                Assert.That(reader.IsVoxelSpaceOccupied(new int3(4, 5, 6)), Is.True);
            }
            finally
            {
                views.Dispose();
            }
        }

        private static AlienEntityView BuildView(Guid128 id, VoxelEntityData data)
        {
            return new AlienEntityView
            {
                EntityId = id,
                LocalToWorld = RigidTransform.identity,
                WorldToLocal = float4x4.identity,
                Sectors = data.sectors.AsReadOnly(),
                WorldAabbMin = new float3(-512f),
                WorldAabbMax = new float3(512f)
            };
        }
    }
}
