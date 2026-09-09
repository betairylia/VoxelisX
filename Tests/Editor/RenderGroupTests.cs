using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix;
using Caelix.Rendering.RayQuery;

namespace Caelix.Tests
{
    /// <summary>
    /// The renderer's brick grouping and the job that buckets a change list by group.
    /// </summary>
    public class RenderGroupTests
    {
        [Test]
        public void LocalBrickIndexMatchesTheSectorBrickIndex()
        {
            for (int z = 0; z < RenderGroup.BricksPerAxis; z++)
            for (int y = 0; y < RenderGroup.BricksPerAxis; y++)
            for (int x = 0; x < RenderGroup.BricksPerAxis; x++)
            {
                Assert.That(
                    RenderGroup.LocalBrickIdx(new int3(x, y, z)),
                    Is.EqualTo(Sector.ToBrickIdx(x, y, z)));
            }
        }

        [Test]
        public void GroupSizeMatchesTheSectorItReplacesInMilestoneOne()
        {
            Assert.That(RenderGroup.BricksPerAxis, Is.EqualTo(Sector.SIZE_IN_BRICKS));
            Assert.That(RenderGroup.BricksInGroup, Is.EqualTo(Sector.BRICKS_IN_SECTOR));
            Assert.That(RenderGroup.Mask, Is.EqualTo(Sector.SECTOR_MASK));
        }

        [TestCase(0, 0, 0)]
        [TestCase(15, 15, 15)]
        [TestCase(16, 0, 0)]
        [TestCase(-1, -1, -1)]
        [TestCase(-16, -17, 33)]
        [TestCase(-129, 130, -131)]
        public void GroupKeysRoundTripBrickKeys(int x, int y, int z)
        {
            var key = new int3(x, y, z);

            int3 group = RenderGroup.Of(key);
            int3 local = RenderGroup.LocalBrick(key);

            Assert.That(math.all(local >= 0) && math.all(local < RenderGroup.BricksPerAxis), Is.True);
            Assert.That(RenderGroup.FirstKey(group) + local, Is.EqualTo(key));
            Assert.That(math.all(key >= RenderGroup.FirstKey(group)), Is.True);
            Assert.That(math.all(key <= RenderGroup.LastKey(group)), Is.True);
            Assert.That(RenderGroup.LastKey(group) - RenderGroup.FirstKey(group),
                Is.EqualTo(new int3(RenderGroup.Mask)));
        }

        [Test]
        public void BlockOriginIsTheGroupsFirstBlock()
        {
            Assert.That(RenderGroup.BlockOrigin(new int3(0, 0, 0)), Is.EqualTo(new int3(0, 0, 0)));
            Assert.That(RenderGroup.BlockOrigin(new int3(-1, 0, 0)), Is.EqualTo(new int3(-128, 0, 0)));
            Assert.That(RenderGroup.BlockOrigin(new int3(1, 2, 3)), Is.EqualTo(new int3(128, 256, 384)));
        }

        [Test]
        public void BucketChangesGroupsEntriesAndKeepsTheirRelativeOrder()
        {
            // Seven changes across three groups, deliberately interleaved.
            var keys = new[]
            {
                new int3(0, 0, 0),     // group (0,0,0)
                new int3(16, 0, 0),    // group (1,0,0)
                new int3(1, 0, 0),     // group (0,0,0)
                new int3(-1, 0, 0),    // group (-1,0,0)
                new int3(17, 0, 0),    // group (1,0,0)
                new int3(2, 0, 0),     // group (0,0,0)
                new int3(-16, 0, 0),   // group (-1,0,0)
            };

            using var source = BuildChanges(keys);
            using var sorted = new NativeList<BrickChange>(keys.Length, Allocator.Persistent);
            using var groupKeys = new NativeList<int3>(keys.Length, Allocator.Persistent);
            using var groupStarts = new NativeList<int>(keys.Length, Allocator.Persistent);
            using var groupCounts = new NativeList<int>(keys.Length, Allocator.Persistent);

            new BucketChangesJob
            {
                changes = source,
                changeCount = keys.Length,
                sorted = sorted,
                groupKeys = groupKeys,
                groupStarts = groupStarts,
                groupCounts = groupCounts
            }.Run();

            Assert.That(sorted.Length, Is.EqualTo(keys.Length));
            Assert.That(groupKeys.Length, Is.EqualTo(3));
            Assert.That(groupStarts.Length, Is.EqualTo(3));
            Assert.That(groupCounts.Length, Is.EqualTo(3));

            var expected = new Dictionary<int3, List<int3>>
            {
                { new int3(0, 0, 0), new List<int3> { new int3(0, 0, 0), new int3(1, 0, 0), new int3(2, 0, 0) } },
                { new int3(1, 0, 0), new List<int3> { new int3(16, 0, 0), new int3(17, 0, 0) } },
                { new int3(-1, 0, 0), new List<int3> { new int3(-1, 0, 0), new int3(-16, 0, 0) } },
            };

            int total = 0;
            int previousEnd = 0;
            for (int g = 0; g < groupKeys.Length; g++)
            {
                int3 group = groupKeys[g];
                Assert.That(expected.ContainsKey(group), Is.True, $"unexpected group {group}");
                Assert.That(groupStarts[g], Is.EqualTo(previousEnd), "slices must be contiguous and in order");

                List<int3> want = expected[group];
                Assert.That(groupCounts[g], Is.EqualTo(want.Count));

                for (int i = 0; i < want.Count; i++)
                {
                    BrickChange change = sorted[groupStarts[g] + i];
                    Assert.That(change.Key, Is.EqualTo(want[i]));
                    Assert.That(RenderGroup.Of(change.Key), Is.EqualTo(group));
                }

                previousEnd = groupStarts[g] + groupCounts[g];
                total += groupCounts[g];
                expected.Remove(group);
            }

            Assert.That(expected, Is.Empty);
            Assert.That(total, Is.EqualTo(keys.Length));
        }

        [Test]
        public void BucketChangesProducesNothingForAnEmptyChangeList()
        {
            using var source = new NativeArray<BrickChange>(1, Allocator.Persistent);
            using var sorted = new NativeList<BrickChange>(1, Allocator.Persistent);
            using var groupKeys = new NativeList<int3>(1, Allocator.Persistent);
            using var groupStarts = new NativeList<int>(1, Allocator.Persistent);
            using var groupCounts = new NativeList<int>(1, Allocator.Persistent);

            new BucketChangesJob
            {
                changes = source,
                changeCount = 0,
                sorted = sorted,
                groupKeys = groupKeys,
                groupStarts = groupStarts,
                groupCounts = groupCounts
            }.Run();

            Assert.That(sorted.Length, Is.EqualTo(0));
            Assert.That(groupKeys.Length, Is.EqualTo(0));
            Assert.That(sorted.AsArray().IsCreated, Is.True, "an empty sorted list must still be job-schedulable");
        }

        /// <summary>
        /// The path the scene renderer actually takes: read a live entity's change view, copy it
        /// into a job array and bucket it. Guards the read-only view's use on the main thread.
        /// </summary>
        [Test]
        public void BucketChangesAcceptsALiveChangeList()
        {
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                data.SetBlock(new int3(1, 1, 1), new Block(1));          // brick (0,0,0),    group (0,0,0)
                data.SetBlock(new int3(200, 200, 200), new Block(1));    // brick (25,25,25), group (1,1,1)
                data.PropagateDirtyFlags(DirtyFlags.All).Complete();
                data.BuildChangeList();

                NativeArray<BrickChange>.ReadOnly changes = data.Changes;
                Assert.That(changes.Length, Is.EqualTo(2));

                using var source = CopyOf(changes);
                using var sorted = new NativeList<BrickChange>(changes.Length, Allocator.Persistent);
                using var groupKeys = new NativeList<int3>(changes.Length, Allocator.Persistent);
                using var groupStarts = new NativeList<int>(changes.Length, Allocator.Persistent);
                using var groupCounts = new NativeList<int>(changes.Length, Allocator.Persistent);

                new BucketChangesJob
                {
                    changes = source,
                    changeCount = changes.Length,
                    sorted = sorted,
                    groupKeys = groupKeys,
                    groupStarts = groupStarts,
                    groupCounts = groupCounts
                }.Run();

                Assert.That(groupKeys.Length, Is.EqualTo(2));
                var found = new List<int3> { groupKeys[0], groupKeys[1] };
                Assert.That(found, Does.Contain(new int3(0, 0, 0)));
                Assert.That(found, Does.Contain(new int3(1, 1, 1)));
                Assert.That(groupCounts[0], Is.EqualTo(1));
                Assert.That(groupCounts[1], Is.EqualTo(1));
                Assert.That(sorted.Length, Is.EqualTo(2));
            }
            finally
            {
                data.Dispose();
            }
        }

        private static NativeArray<BrickChange> CopyOf(NativeArray<BrickChange>.ReadOnly changes)
        {
            var copy = new NativeArray<BrickChange>(
                math.max(1, changes.Length), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < changes.Length; i++)
            {
                copy[i] = changes[i];
            }

            return copy;
        }

        private static NativeArray<BrickChange> BuildChanges(int3[] keys)
        {
            var changes = new NativeArray<BrickChange>(keys.Length, Allocator.Persistent);
            for (int i = 0; i < keys.Length; i++)
            {
                changes[i] = new BrickChange
                {
                    Key = keys[i],
                    Kind = ChangeKind.Updated,
                    SourceFlags = DirtyFlags.Geometry,
                    RequiredFlags = DirtyFlags.GeometryWithLocalNeighbor
                };
            }

            return changes;
        }
    }
}
