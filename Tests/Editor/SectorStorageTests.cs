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
        public void MetaDefaultsAndRoundTripsIndependentlyFromBlock()
        {
            using var scope = new SectorTestScope();
            var block = new Block(37);
            var meta = new Meta { data = 0xCAFE };

            Assert.That(scope.Handle.GetMeta(1, 2, 3), Is.EqualTo(Meta.Empty));

            scope.Handle.SetBlock(1, 2, 3, block);
            scope.Handle.SetMeta(1, 2, 3, meta);

            Assert.That(scope.Handle.GetBlock(1, 2, 3), Is.EqualTo(block));
            Assert.That(scope.Handle.GetMeta(1, 2, 3), Is.EqualTo(meta));

            scope.Handle.SetBlock(1, 2, 3, Block.Empty);

            Assert.That(scope.Handle.GetBlock(1, 2, 3), Is.EqualTo(Block.Empty));
            Assert.That(scope.Handle.GetMeta(1, 2, 3), Is.EqualTo(meta));
        }

        [Test]
        public void SetMetaAllocatesSharedBrickAndLeavesBlockDefault()
        {
            using var scope = new SectorTestScope();

            scope.Handle.SetMeta(8, 0, 0, new Meta { data = 7 });

            Assert.That(scope.Handle.NonEmptyBrickCount, Is.EqualTo(1));
            Assert.That(scope.Handle.GetBlock(8, 0, 0), Is.EqualTo(Block.Empty));
            Assert.That(scope.Handle.GetMeta(8, 0, 0), Is.EqualTo(new Meta { data = 7 }));
            Assert.That(scope.Handle.IsRendererDirty, Is.False);

            scope.Sector.UpdateNonEmptyBricks();
            Assert.That(scope.Sector.NonEmptyBricks.Length, Is.EqualTo(1));
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
            Assert.That(scope.Sector.sectorDirtyFlags & (ushort)DirtyFlags.BrickAdded, Is.Not.EqualTo(0));
            Assert.That(scope.DirtyFlagsAt(new int3(1, 0, 0)) & (ushort)DirtyFlags.BrickAdded, Is.Not.EqualTo(0));

            scope.Sector.MarkBrickRequireUpdate(Sector.ToBrickIdx(1, 0, 0), DirtyFlags.BrickAdded);

            Assert.That(scope.Handle.IsRendererDirty, Is.True);
        }

        [Test]
        public void ClearRequireUpdateFlagsClearsRendererDirtyState()
        {
            using var scope = new SectorTestScope();
            scope.Set(8, 0, 0);
            scope.Sector.MarkBrickRequireUpdate(Sector.ToBrickIdx(1, 0, 0), DirtyFlags.BrickAdded);

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
    }
}
