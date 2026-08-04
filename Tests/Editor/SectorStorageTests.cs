using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    public unsafe class SectorStorageTests
    {
        [Test]
        public void NewSectorReadsEmptyAndEmptyWritesDoNotAllocate()
        {
            using var scope = new SectorTestScope();

            scope.Handle.SetBlock(10, 20, 30, Block.Empty);

            Assert.That(scope.Handle.GetBlock(10, 20, 30).isEmpty, Is.True);
            Assert.That(scope.Handle.NonEmptyBrickCount, Is.EqualTo(0));
            Assert.That(scope.Handle.IsRendererDirty, Is.False);
        }

        [TestCase(0, 0, 0)]
        [TestCase(64, 64, 64)]
        [TestCase(127, 127, 127)]
        public void SetGetStoresBlocksAtRepresentativePositions(int x, int y, int z)
        {
            using var scope = new SectorTestScope();
            var block = new Block(37);

            scope.Handle.SetBlock(x, y, z, block);

            Assert.That(scope.Handle.GetBlock(x, y, z), Is.EqualTo(block));
        }

        [Test]
        public void GenericSlotAccessStoresDataIndependentlyFromBlockSlot()
        {
            using var scope = new SectorTestScope();
            var block = new Block(37);
            const ushort reservedValue = 0x1357;

            scope.Handle.SetBlock(1, 2, 3, block);
            scope.Handle.SetSlot(SectorSlotId.Reserved1, 1, 2, 3, reservedValue);

            Assert.That(scope.Handle.GetBlock(1, 2, 3), Is.EqualTo(block));
            Assert.That(scope.Handle.GetSlot<ushort>(SectorSlotId.Reserved1, 1, 2, 3), Is.EqualTo(reservedValue));
        }

        [Test]
        public void MultipleWritesInsideOneBrickAllocateOnce()
        {
            using var scope = new SectorTestScope();

            scope.Set(0, 0, 0);
            scope.Set(1, 2, 3);
            scope.Set(7, 7, 7);

            Assert.That(scope.Handle.NonEmptyBrickCount, Is.EqualTo(1));
        }

        [Test]
        public void WritesInDifferentBricksAllocateSeparateStorage()
        {
            using var scope = new SectorTestScope();

            scope.Set(0, 0, 0);
            scope.Set(8, 0, 0);
            scope.Set(0, 8, 0);
            scope.Set(0, 0, 8);

            Assert.That(scope.Handle.NonEmptyBrickCount, Is.EqualTo(4));
        }

        [Test]
        public void OverwritingBlockDoesNotAllocateANewBrick()
        {
            using var scope = new SectorTestScope();

            scope.Set(5, 5, 5, 1);
            scope.Set(5, 5, 5, 2);

            Assert.That(scope.Handle.NonEmptyBrickCount, Is.EqualTo(1));
            Assert.That(scope.Handle.GetBlock(5, 5, 5), Is.EqualTo(new Block(2)));
        }

        [Test]
        public void SettingBlockSetsAddedDirtyFlagAndRendererWaitsForRequireUpdate()
        {
            using var scope = new SectorTestScope();

            scope.Set(8, 0, 0);

            Assert.That(scope.Handle.IsRendererDirty, Is.False);
            Assert.That(scope.Sector.sectorDirtyFlags & (ushort)DirtyFlags.BlockBrickAdded, Is.Not.EqualTo(0));
            Assert.That(scope.DirtyFlagsAt(new int3(1, 0, 0)) & (ushort)DirtyFlags.BlockBrickAdded, Is.Not.EqualTo(0));

            scope.Sector.MarkBrickRequireUpdate(Sector.ToBrickIdx(1, 0, 0), DirtyFlags.BlockBrickAdded);

            Assert.That(scope.Handle.IsRendererDirty, Is.True);
        }

        [Test]
        public void ClearRequireUpdateFlagsClearsRendererDirtyState()
        {
            using var scope = new SectorTestScope();
            scope.Set(8, 0, 0);
            scope.Sector.MarkBrickRequireUpdate(Sector.ToBrickIdx(1, 0, 0), DirtyFlags.BlockBrickAdded);

            scope.Sector.ClearAllRequireUpdateFlags();

            Assert.That(scope.Handle.IsRendererDirty, Is.False);
        }

        [Test]
        public void RendererDirtyStateUsesGeometryRequireUpdateFlag()
        {
            using var scope = new SectorTestScope();
            int brickIdx = Sector.ToBrickIdx(1, 0, 0);

            scope.Sector.MarkBrickRequireUpdate(brickIdx, DirtyFlags.GeneralAutomata);

            Assert.That(scope.Handle.IsRendererDirty, Is.False);

            scope.Sector.MarkBrickRequireUpdate(brickIdx, DirtyFlags.GeometryWithLocalNeighbor);

            Assert.That(scope.Handle.IsRendererDirty, Is.True);
        }

        [Test]
        public void CloneNoRecordCopiesBlocksWithoutSharingStorage()
        {
            using var scope = new SectorTestScope();
            scope.Set(1, 2, 3, 7);

            var clone = Sector.CloneWithUndefinedDirtiness(scope.Sector, Allocator.Persistent);
            try
            {
                clone.SetBlock(1, 2, 3, new Block(8));

                Assert.That(scope.Handle.GetBlock(1, 2, 3), Is.EqualTo(new Block(7)));
                Assert.That(clone.GetBlock(1, 2, 3), Is.EqualTo(new Block(8)));
            }
            finally
            {
                clone.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void UpdateNonEmptyBricksRebuildsIterableBrickList()
        {
            using var scope = new SectorTestScope();
            scope.Set(0, 0, 0);
            scope.Set(16, 0, 0);

            scope.Sector.UpdateNonEmptyBricks();

            Assert.That(scope.Sector.NonEmptyBricks.Length, Is.EqualTo(2));
            Assert.That(scope.Sector.NonEmptyBricks[0], Is.EqualTo((short)Sector.ToBrickIdx(0, 0, 0)));
            Assert.That(scope.Sector.NonEmptyBricks[1], Is.EqualTo((short)Sector.ToBrickIdx(2, 0, 0)));
        }

        [Test]
        public void EnumerateNonEmptyBricksReadsCurrentBrickMapWithoutLegacyListRefresh()
        {
            using var scope = new SectorTestScope();
            scope.Set(16, 0, 0);
            scope.Set(0, 0, 0);

            // SetBlock does not maintain the legacy list incrementally. The new helper reads the
            // authoritative brick map, so callers do not need to coordinate a list refresh first.
            Assert.That(scope.Sector.NonEmptyBricks.Length, Is.Zero);

            SectorNonEmptyBrickEnumerator enumerator = scope.Sector.EnumerateNonEmptyBricks();

            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current.BrickAbs, Is.EqualTo(Sector.ToBrickIdx(0, 0, 0)));
            Assert.That(enumerator.Current.Bid,
                Is.EqualTo(scope.Sector.brickIdx[Sector.ToBrickIdx(0, 0, 0)]));

            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current.BrickAbs, Is.EqualTo(Sector.ToBrickIdx(2, 0, 0)));
            Assert.That(enumerator.Current.Bid,
                Is.EqualTo(scope.Sector.brickIdx[Sector.ToBrickIdx(2, 0, 0)]));

            Assert.That(enumerator.MoveNext(), Is.False);
        }

        [Test]
        public void NonEmptyBlockEnumeratorFollowsOccupancyBitsInVoxelIndexOrder()
        {
            using var scope = new EntityDataTestScope();
            SectorHandle sector = scope.AddSector(int3.zero);
            sector.SetBlock(7, 7, 7, new Block(4));   // voxel index 511
            sector.SetBlock(0, 0, 1, new Block(3));   // voxel index 64
            sector.SetBlock(7, 7, 0, new Block(2));   // voxel index 63
            sector.SetBlock(0, 0, 0, new Block(1));   // voxel index 0
            sector.SetBlock(16, 0, 0, new Block(5));  // later absolute brick
            scope.Data.RefreshNonEmptyMask();

            ref Sector source = ref sector.Get();
            Assert.That(source.slots[(int)SectorSlotId.Block].HasAux, Is.True);

            int3[] expectedPositions =
            {
                new int3(0, 0, 0),
                new int3(7, 7, 0),
                new int3(0, 0, 1),
                new int3(7, 7, 7),
                new int3(16, 0, 0),
            };

            var enumerator = new SectorNonEmptyBlockEnumerator(source);
            for (int i = 0; i < expectedPositions.Length; i++)
            {
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(enumerator.Current.position, Is.EqualTo(expectedPositions[i]));
                Assert.That(enumerator.Current.block.id, Is.EqualTo(i + 1));
            }

            Assert.That(enumerator.MoveNext(), Is.False);

            enumerator.Reset();
            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current.position, Is.EqualTo(expectedPositions[0]));
        }

        [Test]
        public void NonEmptyBlockEnumeratorAllowsEmptySectorWithoutOccupancyAllocation()
        {
            using var scope = new SectorTestScope();

            var enumerator = new SectorNonEmptyBlockEnumerator(scope.Sector);

            Assert.That(enumerator.MoveNext(), Is.False);
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Test]
        public void NonEmptyBlockEnumeratorRejectsAllocatedSectorWithoutOccupancyMask()
        {
            using var scope = new SectorTestScope();
            scope.Set(0, 0, 0);
            Sector source = scope.Sector;

            Assert.Throws<System.InvalidOperationException>(() =>
            {
                _ = new SectorNonEmptyBlockEnumerator(source);
            });
        }
#endif
    }
}
