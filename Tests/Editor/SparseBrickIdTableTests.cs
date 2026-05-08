using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    public unsafe class SparseBrickIdTableTests
    {
        private SparseBrickIdTable _t;

        [SetUp]
        public void SetUp()
        {
            _t = SparseBrickIdTable.New(Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        {
            if (_t.IsCreated) _t.Dispose();
        }

        // ---------- New / Dispose ----------

        [Test]
        public void New_StartsEmpty()
        {
            Assert.That(_t.IsCreated, Is.True);
            Assert.That(_t.Capacity, Is.EqualTo(0));
            Assert.That(_t.Count, Is.EqualTo(0));
            Assert.That(_t.FreeCount, Is.EqualTo(0));
            Assert.That(_t.MinimumCapacity(), Is.EqualTo(0));
        }

        [Test]
        public void New_AllIndexSlotsInitialisedToEmpty()
        {
            for (int i = 0; i < SparseBrickIdTable.CAPACITY; i++)
                Assert.That(_t.indices[i], Is.EqualTo(SparseBrickIdTable.EMPTY));
        }

        [Test]
        public void Dispose_ClearsPointersAndIsCreated()
        {
            _t.Dispose();
            Assert.That(_t.IsCreated, Is.False);
        }

        [Test]
        public void Capacity_MatchesSectorBricks()
        {
            Assert.That(SparseBrickIdTable.CAPACITY, Is.EqualTo(Sector.BRICKS_IN_SECTOR));
        }

        // ---------- AddBrick ----------

        [Test]
        public void AddBrick_FirstAddAssignsZeroAndExceedsCapacity()
        {
            bool added = _t.AddBrick(new int3(0, 0, 0), out int id, out bool exceeds);

            Assert.That(added, Is.True);
            Assert.That(id, Is.EqualTo(0));
            Assert.That(exceeds, Is.True);
            Assert.That(_t.Capacity, Is.EqualTo(1));
            Assert.That(_t.Count, Is.EqualTo(1));
        }

        [Test]
        public void AddBrick_SequentialAddsGrowCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out int id0, out bool exc0);
            _t.AddBrick(new int3(1, 0, 0), out int id1, out bool exc1);
            _t.AddBrick(new int3(0, 1, 0), out int id2, out bool exc2);

            Assert.That(new[] { id0, id1, id2 }, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(new[] { exc0, exc1, exc2 }, Is.All.True);
            Assert.That(_t.Capacity, Is.EqualTo(3));
            Assert.That(_t.Count, Is.EqualTo(3));
        }

        [Test]
        public void AddBrick_OnExistingPositionReturnsFalseAndExistingId()
        {
            _t.AddBrick(new int3(2, 3, 4), out int firstId, out _);

            bool added = _t.AddBrick(new int3(2, 3, 4), out int id, out bool exceeds);

            Assert.That(added, Is.False);
            Assert.That(id, Is.EqualTo(firstId));
            Assert.That(exceeds, Is.False);
            Assert.That(_t.Count, Is.EqualTo(1));
            Assert.That(_t.Capacity, Is.EqualTo(1));
        }

        [Test]
        public void AddBrick_WritesIdToIndicesUsingSectorToBrickIdx()
        {
            var pos = new int3(5, 6, 7);
            _t.AddBrick(pos, out int id, out _);

            int idx = Sector.ToBrickIdx(pos.x, pos.y, pos.z);
            Assert.That((int)_t.indices[idx], Is.EqualTo(id));
        }

        [Test]
        public void AddBrick_AtSectorExtentsWorks()
        {
            int max = Sector.SIZE_IN_BRICKS - 1;
            var pos = new int3(max, max, max);

            _t.AddBrick(pos, out int id, out _);

            int idx = Sector.ToBrickIdx(pos.x, pos.y, pos.z);
            Assert.That((int)_t.indices[idx], Is.EqualTo(id));
            Assert.That(Sector.ToBrickPos((short)idx), Is.EqualTo(pos));
        }

        // ---------- FindOrAddBrick ----------

        [Test]
        public void FindOrAddBrick_NewPositionReturnsExceedsCapacity()
        {
            bool exceeds = _t.FindOrAddBrick(new int3(1, 1, 1), out int id);

            Assert.That(exceeds, Is.True);
            Assert.That(id, Is.EqualTo(0));
            Assert.That(_t.Capacity, Is.EqualTo(1));
        }

        [Test]
        public void FindOrAddBrick_ExistingPositionReturnsFalseAndExistingId()
        {
            _t.AddBrick(new int3(1, 1, 1), out int firstId, out _);

            bool exceeds = _t.FindOrAddBrick(new int3(1, 1, 1), out int id);

            Assert.That(exceeds, Is.False);
            Assert.That(id, Is.EqualTo(firstId));
            Assert.That(_t.Count, Is.EqualTo(1));
        }

        [Test]
        public void FindOrAddBrick_AfterRemoveReusesIdWithoutExceedingCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out int id0, out _);
            _t.AddBrick(new int3(1, 0, 0), out int id1, out _);
            _t.RemoveBrick(new int3(0, 0, 0));

            bool exceeds = _t.FindOrAddBrick(new int3(2, 0, 0), out int reusedId);

            Assert.That(exceeds, Is.False);
            Assert.That(reusedId, Is.EqualTo(id0));
            Assert.That(_t.Capacity, Is.EqualTo(2));
        }

        // ---------- RemoveBrick ----------

        [Test]
        public void RemoveBrick_MarksSlotEmptyAndDecrementsCount()
        {
            _t.AddBrick(new int3(3, 3, 3), out int id, out _);

            _t.RemoveBrick(new int3(3, 3, 3));

            int idx = Sector.ToBrickIdx(3, 3, 3);
            Assert.That(_t.indices[idx], Is.EqualTo(SparseBrickIdTable.EMPTY));
            Assert.That(_t.Count, Is.EqualTo(0));
            Assert.That(_t.FreeCount, Is.EqualTo(1));
            Assert.That(_t.Capacity, Is.EqualTo(1), "Capacity does not shrink on remove");
        }

        [Test]
        public void RemoveBrick_NoOpOnEmptySlot()
        {
            _t.RemoveBrick(new int3(4, 5, 6));

            Assert.That(_t.Count, Is.EqualTo(0));
            Assert.That(_t.FreeCount, Is.EqualTo(0));
            Assert.That(_t.Capacity, Is.EqualTo(0));
        }

        [Test]
        public void RemoveBrick_DoubleRemoveDoesNotDoubleFree()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);

            _t.RemoveBrick(new int3(0, 0, 0));
            _t.RemoveBrick(new int3(0, 0, 0));

            Assert.That(_t.FreeCount, Is.EqualTo(1), "ID must not be pushed twice to the free list");
            Assert.That(_t.Count, Is.EqualTo(0));
        }

        // ---------- Free list (min-heap) ----------

        [Test]
        public void Reuse_PrefersLowestFreedIdFirst()
        {
            // Allocate IDs 0..4 across distinct positions.
            var positions = new int3[]
            {
                new int3(0, 0, 0), new int3(1, 0, 0), new int3(2, 0, 0),
                new int3(3, 0, 0), new int3(4, 0, 0),
            };
            var ids = new int[5];
            for (int i = 0; i < 5; i++) _t.AddBrick(positions[i], out ids[i], out _);

            // Free IDs in non-sorted order: 3, 1, 4.
            _t.RemoveBrick(positions[3]);
            _t.RemoveBrick(positions[1]);
            _t.RemoveBrick(positions[4]);

            // Min-heap should hand back 1, then 3, then 4.
            _t.AddBrick(new int3(0, 1, 0), out int r0, out bool e0);
            _t.AddBrick(new int3(1, 1, 0), out int r1, out bool e1);
            _t.AddBrick(new int3(2, 1, 0), out int r2, out bool e2);

            Assert.That(new[] { r0, r1, r2 }, Is.EqualTo(new[] { ids[1], ids[3], ids[4] }));
            Assert.That(new[] { e0, e1, e2 }, Is.All.False);
            Assert.That(_t.Capacity, Is.EqualTo(5));
        }

        [Test]
        public void Reuse_AfterFreelistDrainsHighWaterMarkResumes()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(0, 0, 0));

            _t.AddBrick(new int3(2, 0, 0), out int reused, out bool exReused);
            _t.AddBrick(new int3(3, 0, 0), out int newId, out bool exNew);

            Assert.That(reused, Is.EqualTo(0));
            Assert.That(exReused, Is.False);
            Assert.That(newId, Is.EqualTo(2));
            Assert.That(exNew, Is.True);
            Assert.That(_t.Capacity, Is.EqualTo(3));
        }

        // ---------- MinimumCapacity ----------

        [Test]
        public void MinimumCapacity_WithAllActiveEqualsCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(0, 1, 0), out _, out _);

            Assert.That(_t.MinimumCapacity(), Is.EqualTo(3));
        }

        [Test]
        public void MinimumCapacity_TracksMaxActiveIdAfterRemovingHighestId()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _); // id 0
            _t.AddBrick(new int3(1, 0, 0), out _, out _); // id 1
            _t.AddBrick(new int3(2, 0, 0), out _, out _); // id 2

            _t.RemoveBrick(new int3(2, 0, 0)); // free id 2
            Assert.That(_t.MinimumCapacity(), Is.EqualTo(2));

            _t.RemoveBrick(new int3(1, 0, 0)); // free id 1
            Assert.That(_t.MinimumCapacity(), Is.EqualTo(1));
        }

        [Test]
        public void MinimumCapacity_WithHoleBelowMaxStaysAtCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _);

            _t.RemoveBrick(new int3(1, 0, 0));

            Assert.That(_t.MinimumCapacity(), Is.EqualTo(3));
        }

        // ---------- CompactTo ----------

        [Test]
        public void CompactTo_FailsWhenTargetNotStrictReduction()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);

            Assert.That(_t.CompactTo(2), Is.False, "target == capacity is not a reduction");
            Assert.That(_t.CompactTo(5), Is.False, "target > capacity is not a reduction");
            Assert.That(_t.Capacity, Is.EqualTo(2));
        }

        [Test]
        public void CompactTo_FailsWhenTargetBelowMinimumCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _); // id 2 still active

            Assert.That(_t.CompactTo(2), Is.False);
            Assert.That(_t.Capacity, Is.EqualTo(3));
        }

        [Test]
        public void CompactTo_FailsForNegativeTarget()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);

            Assert.That(_t.CompactTo(-1), Is.False);
            Assert.That(_t.Capacity, Is.EqualTo(1));
        }

        [Test]
        public void CompactTo_SucceedsAndUpdatesCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(2, 0, 0)); // MinimumCapacity becomes 2

            Assert.That(_t.CompactTo(2), Is.True);
            Assert.That(_t.Capacity, Is.EqualTo(2));
        }

        [Test]
        public void CompactTo_PrunesFreelistEntriesAtOrAboveTarget()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(1, 0, 0)); // free id 1
            _t.RemoveBrick(new int3(2, 0, 0)); // free id 2

            Assert.That(_t.FreeCount, Is.EqualTo(2));
            Assert.That(_t.CompactTo(1), Is.True);

            Assert.That(_t.Capacity, Is.EqualTo(1));
            Assert.That(_t.FreeCount, Is.EqualTo(0), "Freed IDs >= new capacity must be discarded");
        }

        [Test]
        public void CompactTo_PreservesFreelistEntriesBelowTarget()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _);
            _t.AddBrick(new int3(3, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(1, 0, 0)); // free id 1
            _t.RemoveBrick(new int3(3, 0, 0)); // free id 3

            Assert.That(_t.CompactTo(3), Is.True);

            Assert.That(_t.Capacity, Is.EqualTo(3));
            Assert.That(_t.FreeCount, Is.EqualTo(1), "Only id 1 should remain after pruning");

            // Reusing should hand back id 1 (the only survivor).
            _t.AddBrick(new int3(0, 1, 0), out int reused, out bool exceeds);
            Assert.That(reused, Is.EqualTo(1));
            Assert.That(exceeds, Is.False);
        }

        [Test]
        public void CompactTo_NextAddAfterCompactGrowsFromNewCapacity()
        {
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(1, 0, 0));

            Assert.That(_t.CompactTo(1), Is.True);
            Assert.That(_t.Capacity, Is.EqualTo(1));
            Assert.That(_t.FreeCount, Is.EqualTo(0));

            _t.AddBrick(new int3(1, 0, 0), out int newId, out bool exceeds);
            Assert.That(newId, Is.EqualTo(1));
            Assert.That(exceeds, Is.True);
            Assert.That(_t.Capacity, Is.EqualTo(2));
        }

        // ---------- Clone ----------

        [Test]
        public void Clone_ProducesEquivalentStateAndIndependentBuffers()
        {
            _t.AddBrick(new int3(0, 0, 0), out int id0, out _);
            _t.AddBrick(new int3(1, 0, 0), out int id1, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(1, 0, 0)); // freelist now contains id1

            var clone = _t.Clone(Allocator.Persistent);
            try
            {
                Assert.That(clone.Capacity,  Is.EqualTo(_t.Capacity));
                Assert.That(clone.Count,     Is.EqualTo(_t.Count));
                Assert.That(clone.FreeCount, Is.EqualTo(_t.FreeCount));

                for (int i = 0; i < SparseBrickIdTable.CAPACITY; i++)
                    Assert.That(clone.indices[i], Is.EqualTo(_t.indices[i]));

                // Buffers are independent: mutating the clone must not affect the source.
                clone.AddBrick(new int3(3, 0, 0), out _, out _);
                clone.RemoveBrick(new int3(0, 0, 0));

                Assert.That(_t.Count, Is.EqualTo(2));
                Assert.That(_t.indices[Sector.ToBrickIdx(0, 0, 0)], Is.EqualTo((short)id0));
                Assert.That(_t.indices[Sector.ToBrickIdx(3, 0, 0)], Is.EqualTo(SparseBrickIdTable.EMPTY));
            }
            finally
            {
                clone.Dispose();
            }
        }

        [Test]
        public void Clone_PreservesFreelistOrderingForSubsequentReuse()
        {
            // Build a freelist with multiple entries; the clone should reuse them in the same order.
            _t.AddBrick(new int3(0, 0, 0), out _, out _);
            _t.AddBrick(new int3(1, 0, 0), out _, out _);
            _t.AddBrick(new int3(2, 0, 0), out _, out _);
            _t.AddBrick(new int3(3, 0, 0), out _, out _);
            _t.RemoveBrick(new int3(2, 0, 0)); // free 2
            _t.RemoveBrick(new int3(0, 0, 0)); // free 0
            _t.RemoveBrick(new int3(3, 0, 0)); // free 3

            var clone = _t.Clone(Allocator.Persistent);
            try
            {
                // Min-heap should hand back 0, 2, 3 in that order on both source and clone.
                _t.AddBrick(new int3(0, 1, 0), out int srcA, out _);
                _t.AddBrick(new int3(1, 1, 0), out int srcB, out _);
                _t.AddBrick(new int3(2, 1, 0), out int srcC, out _);
                clone.AddBrick(new int3(0, 1, 0), out int cloA, out _);
                clone.AddBrick(new int3(1, 1, 0), out int cloB, out _);
                clone.AddBrick(new int3(2, 1, 0), out int cloC, out _);

                Assert.That(new[] { cloA, cloB, cloC }, Is.EqualTo(new[] { srcA, srcB, srcC }));
                Assert.That(new[] { cloA, cloB, cloC }, Is.EqualTo(new[] { 0, 2, 3 }));
            }
            finally
            {
                clone.Dispose();
            }
        }

        [Test]
        public void Clone_EmptyTableProducesEmptyClone()
        {
            var clone = _t.Clone(Allocator.Persistent);
            try
            {
                Assert.That(clone.IsCreated, Is.True);
                Assert.That(clone.Capacity,  Is.EqualTo(0));
                Assert.That(clone.Count,     Is.EqualTo(0));
                Assert.That(clone.FreeCount, Is.EqualTo(0));
                for (int i = 0; i < SparseBrickIdTable.CAPACITY; i++)
                    Assert.That(clone.indices[i], Is.EqualTo(SparseBrickIdTable.EMPTY));
            }
            finally
            {
                clone.Dispose();
            }
        }

        [Test]
        public void Clone_DisposingSourceLeavesCloneIntact()
        {
            _t.AddBrick(new int3(4, 5, 6), out int id, out _);
            var clone = _t.Clone(Allocator.Persistent);
            try
            {
                _t.Dispose(); // SetUp/TearDown's IsCreated guard prevents double-dispose.

                Assert.That(clone.Count, Is.EqualTo(1));
                Assert.That((int)clone.indices[Sector.ToBrickIdx(4, 5, 6)], Is.EqualTo(id));
            }
            finally
            {
                clone.Dispose();
            }
        }

        // ---------- Stress / round-trip ----------

        [Test]
        public void StressRoundTrip_EachActiveSlotMatchesItsAssignedId()
        {
            // Add a checkerboard of bricks, remove half, re-add, verify indices.
            int n = 0;
            for (int z = 0; z < Sector.SIZE_IN_BRICKS; z += 4)
            for (int y = 0; y < Sector.SIZE_IN_BRICKS; y += 4)
            for (int x = 0; x < Sector.SIZE_IN_BRICKS; x += 4)
            {
                _t.AddBrick(new int3(x, y, z), out int id, out _);
                Assert.That((int)_t.indices[Sector.ToBrickIdx(x, y, z)], Is.EqualTo(id));
                n++;
            }

            Assert.That(_t.Count, Is.EqualTo(n));
            Assert.That(_t.Capacity, Is.EqualTo(n));

            // Remove every other one.
            int removed = 0;
            int toggle = 0;
            for (int z = 0; z < Sector.SIZE_IN_BRICKS; z += 4)
            for (int y = 0; y < Sector.SIZE_IN_BRICKS; y += 4)
            for (int x = 0; x < Sector.SIZE_IN_BRICKS; x += 4)
            {
                if ((toggle++ & 1) == 0)
                {
                    _t.RemoveBrick(new int3(x, y, z));
                    removed++;
                }
            }

            Assert.That(_t.Count, Is.EqualTo(n - removed));
            Assert.That(_t.FreeCount, Is.EqualTo(removed));

            // Verify every still-active slot maps back to a valid (non-empty) id within capacity.
            for (int i = 0; i < SparseBrickIdTable.CAPACITY; i++)
            {
                short id = _t.indices[i];
                if (id == SparseBrickIdTable.EMPTY) continue;
                Assert.That(id, Is.GreaterThanOrEqualTo(0));
                Assert.That(id, Is.LessThan(_t.Capacity));
            }
        }
    }
}
