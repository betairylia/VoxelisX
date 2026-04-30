using NUnit.Framework;
using Unity.Collections;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    public class SectorSnapshotTests
    {
        [Test]
        public void WritesAfterActivateSnapshotAreInvisibleUntilApply()
        {
            using var scope = new SectorTestScope();
            scope.Handle.SetBlock(0, 0, 0, new Block(1));

            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(2));

            Assert.That(scope.Handle.GetBlock(0, 0, 0), Is.EqualTo(new Block(1)));

            scope.Handle.ApplySnapshot();

            Assert.That(scope.Handle.GetBlock(0, 0, 0), Is.EqualTo(new Block(2)));
        }

        [Test]
        public void SnapshotCanCreateANewBrickBeforeApply()
        {
            using var scope = new SectorTestScope();

            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(127, 127, 127, new Block(3));

            Assert.That(scope.Handle.GetBlock(127, 127, 127).isEmpty, Is.True);

            scope.Handle.ApplySnapshot();

            Assert.That(scope.Handle.GetBlock(127, 127, 127), Is.EqualTo(new Block(3)));
        }

        [Test]
        public void RepeatedActivateSnapshotBeforeApplyIsIdempotent()
        {
            using var scope = new SectorTestScope();
            scope.Handle.SetBlock(0, 0, 0, new Block(1));

            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(2));
            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(3));
            scope.Handle.ApplySnapshot();

            Assert.That(scope.Handle.GetBlock(0, 0, 0), Is.EqualTo(new Block(3)));
        }

        [Test]
        public void DisposingSectorWithActiveSnapshotIsSafe()
        {
            var handle = SectorHandle.AllocEmpty();
            handle.ActivateSnapshot(Allocator.Persistent);
            handle.SetBlock(0, 0, 0, new Block(4));

            Assert.DoesNotThrow(() => handle.Dispose(Allocator.Persistent));
            Assert.That(handle.IsNull, Is.True);
        }
    }
}
