using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    public class DirtyPropagationScenarioTests
    {
        [Test]
        public void DirtyBrickMarksItselfRequireUpdate()
        {
            using var world = new DirtyPropagationTestWorld();

            world.SetBlock(64, 64, 64).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(8, 8, 8)) & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirtyVoxelAtBrickEdgePropagatesToAdjacentBrick()
        {
            using var world = new DirtyPropagationTestWorld();

            world.SetBlock(7, 7, 7).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(1, 1, 1)) & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirtyVoxelAtSectorFacePropagatesToExistingNeighborSector()
        {
            using var world = new DirtyPropagationTestWorld();
            world.AddSector(new int3(1, 0, 0));

            world.SetBlock(127, 64, 64).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(1, 0, 0), new int3(0, 8, 8)) & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirtyVoxelAtSectorFaceCreatesMissingNeighborForPropagation()
        {
            using var world = new DirtyPropagationTestWorld();

            world.SetBlock(127, 64, 64).Propagate();

            Assert.That(world.Data.sectors.ContainsKey(new int3(1, 0, 0)), Is.True);
            Assert.That(world.RequireFlagsAt(new int3(1, 0, 0), new int3(0, 8, 8)) & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirectionMaskFiltersPropagationDirection()
        {
            using var world = new DirtyPropagationTestWorld();
            uint positiveXOnly = 1u << NeighborhoodSettings.DirectionToIndexMinusOne(new int3(1, 0, 0));

            world.MarkBrickDirty(new int3(5, 5, 5), DirtyFlags.GeneralAutomata, positiveXOnly).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(6, 5, 5)) & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(4, 5, 5)) & (ushort)DirtyFlags.GeneralAutomata, Is.EqualTo(0));
        }

        [Test]
        public void FlagFilterOnlyPropagatesRequestedFlags()
        {
            using var world = new DirtyPropagationTestWorld();

            world.MarkBrickDirty(new int3(5, 5, 5), DirtyFlags.GeneralAutomata | DirtyFlags.Reserved1).Propagate(DirtyFlags.GeneralAutomata);

            ushort flags = world.RequireFlagsAt(new int3(0, 0, 0), new int3(6, 5, 5));
            Assert.That(flags & (ushort)DirtyFlags.GeneralAutomata, Is.Not.EqualTo(0));
            Assert.That(flags & (ushort)DirtyFlags.Reserved1, Is.EqualTo(0));
        }

        [Test]
        public void MissingNeighborWithoutBoundaryCreationDoesNotCrash()
        {
            using var world = new DirtyPropagationTestWorld();

            world.MarkBrickDirty(new int3(15, 8, 8), DirtyFlags.GeneralAutomata, 0);

            Assert.DoesNotThrow(() => world.Propagate());
        }
    }
}
