using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    public unsafe class NeighborhoodTopologyTests
    {
        [Test]
        public void AddSectorLinksFaceNeighborBidirectionally()
        {
            using var scope = new EntityDataTestScope();
            var origin = scope.AddSector(new int3(0, 0, 0));
            var right = scope.AddSector(new int3(1, 0, 0));
            origin.SetBlock(0, 0, 0, new Block(1));
            right.SetBlock(0, 0, 0, new Block(2));

            var originNeighbors = scope.NeighborsAt(new int3(0, 0, 0));
            var rightNeighbors = scope.NeighborsAt(new int3(1, 0, 0));

            Assert.That(originNeighbors.Neighbors[0].GetBlock(0, 0, 0), Is.EqualTo(new Block(2)));
            Assert.That(rightNeighbors.Neighbors[1].GetBlock(0, 0, 0), Is.EqualTo(new Block(1)));
        }

        [Test]
        public void NeighborhoodReaderGetSlotReadsNonBlockSlotAcrossBoundary()
        {
            using var scope = new EntityDataTestScope();
            var center = scope.AddSector(new int3(0, 0, 0));
            var right = scope.AddSector(new int3(1, 0, 0));

            // Reserved1 (index 2) is the slot Titania maps its per-voxel Meta onto. Exercise the
            // generic GetSlot<T> path a metadata automaton relies on for cross-brick neighbor reads.
            const SectorSlotId metaSlot = (SectorSlotId)2;
            right.SetSlot(metaSlot, 0, 0, 0, (short)0x1234);
            center.SetSlot(metaSlot, 3, 4, 5, (short)0x0ABC);

            var reader = new SectorNeighborhoodReaderHelper(center, scope.NeighborsAt(new int3(0, 0, 0)));

            // x == 128 wraps into the +X neighbor's local origin.
            Assert.That(reader.GetSlot<short>(metaSlot, Sector.SECTOR_SIZE_IN_BLOCKS, 0, 0), Is.EqualTo((short)0x1234));
            // In-bounds fast path stays on the center sector.
            Assert.That(reader.GetSlot<short>(metaSlot, new int3(3, 4, 5)), Is.EqualTo((short)0x0ABC));
            // Missing neighbor yields default, never throws.
            Assert.That(reader.GetSlot<short>(metaSlot, 0, 0, -1), Is.EqualTo((short)0));
        }

        [Test]
        public void AddSectorLinksCornerNeighborBidirectionally()
        {
            using var scope = new EntityDataTestScope();
            var origin = scope.AddSector(new int3(0, 0, 0));
            var corner = scope.AddSector(new int3(1, 1, 1));
            origin.SetBlock(0, 0, 0, new Block(1));
            corner.SetBlock(0, 0, 0, new Block(2));
            int dir = NeighborhoodSettings.DirectionToIndexMinusOne(new int3(1, 1, 1));
            int opposite = NeighborhoodSettings.OppositeDirectionIndices[dir];

            Assert.That(scope.NeighborsAt(new int3(0, 0, 0)).Neighbors[dir].GetBlock(0, 0, 0), Is.EqualTo(new Block(2)));
            Assert.That(scope.NeighborsAt(new int3(1, 1, 1)).Neighbors[opposite].GetBlock(0, 0, 0), Is.EqualTo(new Block(1)));
        }

        [Test]
        public void RemoveSectorClearsReverseNeighborLinks()
        {
            using var scope = new EntityDataTestScope();
            scope.AddSector(new int3(0, 0, 0));
            scope.AddSector(new int3(1, 0, 0));

            Assert.That(scope.Data.RemoveSectorAt(new int3(1, 0, 0)), Is.True);

            Assert.That(scope.NeighborsAt(new int3(0, 0, 0)).Neighbors[0].IsNull, Is.True);
        }

        [Test]
        public void SetBlockCreatesMissingSectorAtWorldSectorCoordinate()
        {
            using var scope = new EntityDataTestScope();
            var worldPos = new int3(Sector.SECTOR_SIZE_IN_BLOCKS + 5, 7, 9);

            scope.Data.SetBlock(worldPos, new Block(12));

            Assert.That(scope.Data.sectors.ContainsKey(new int3(1, 0, 0)), Is.True);
            Assert.That(scope.Data.GetBlock(worldPos), Is.EqualTo(new Block(12)));
        }

        [Test]
        public void GenericSlotAccessCreatesMissingSectorAndKeepsDefaultBlockSlotEmpty()
        {
            using var scope = new EntityDataTestScope();
            var worldPos = new int3(Sector.SECTOR_SIZE_IN_BLOCKS + 5, 7, 9);
            const ushort reservedValue = 0x2468;

            scope.Data.SetSlot(SectorSlotId.Reserved1, worldPos, reservedValue);

            Assert.That(scope.Data.sectors.ContainsKey(new int3(1, 0, 0)), Is.True);
            Assert.That(scope.Data.GetBlock(worldPos).isEmpty, Is.True);
            Assert.That(scope.Data.GetSlot<ushort>(SectorSlotId.Reserved1, worldPos), Is.EqualTo(reservedValue));
        }

        [Test]
        public void GetBlockReadsFromCorrectSectorForWorldPositions()
        {
            using var scope = new EntityDataTestScope();
            var handle = scope.AddSector(new int3(1, 0, 0));
            handle.SetBlock(5, 6, 7, new Block(14));

            Assert.That(scope.Data.GetBlock(new int3(Sector.SECTOR_SIZE_IN_BLOCKS + 5, 6, 7)), Is.EqualTo(new Block(14)));
            Assert.That(scope.Data.GetBlock(new int3(5, 6, 7)).isEmpty, Is.True);
        }

        [Test]
        public void DuplicateSectorAddThrows()
        {
            using var scope = new EntityDataTestScope();
            scope.AddSector(new int3(0, 0, 0));

            Assert.That(() => scope.AddSector(new int3(0, 0, 0)), Throws.Exception);
        }
    }
}
