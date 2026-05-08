using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// Burst-compatible sparse brick id table for a single sector.
    /// Maps brick positions (within a sector) to dense brick IDs and recycles
    /// removed IDs through an internal min-heap free list.
    /// </summary>
    /// <remarks>
    /// Backed by raw unsafe arrays — no NativeContainers — so the struct can be
    /// passed by value into Burst jobs while preserving mutations through the
    /// internal pointers. Indexing matches <see cref="Sector.ToBrickIdx"/> /
    /// <see cref="Sector.ToBrickPos"/>.
    ///
    /// Brick IDs are stable for a brick's lifespan — they are never rearranged.
    /// <see cref="Capacity"/> tracks the high water mark of assigned IDs, and
    /// the min-heap free list ensures reused IDs come from the low end so
    /// <see cref="MinimumCapacity"/> stays as small as possible.
    /// </remarks>
    [BurstCompile]
    public unsafe struct SparseBrickIdTable
    {
        /// <summary>Number of brick slots in the table (SIZE_IN_BRICKS^3).</summary>
        public const int CAPACITY = Sector.BRICKS_IN_SECTOR;

        /// <summary>Sentinel value for an unallocated brick slot.</summary>
        public const short EMPTY = -1;

        /// <summary>
        /// Position → brickID lookup, sized <see cref="CAPACITY"/>. <see cref="EMPTY"/> for invalid bricks.
        /// Use <see cref="Sector.ToBrickIdx"/> / <see cref="Sector.ToBrickPos"/> for conversion.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public short* indices;

        // Min-heap of reusable IDs (each entry is < _meta[0]).
        [NativeDisableUnsafePtrRestriction]
        private short* _freelist;

        // [0] capacity (high water mark), [1] freelist count, [2] active brick count.
        [NativeDisableUnsafePtrRestriction]
        private int* _meta;

        private Allocator _allocator;

        /// <summary>Current high-water mark of assigned IDs. New non-recycled IDs equal this value.</summary>
        public int Capacity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _meta[0]; }

        /// <summary>Number of currently-valid bricks.</summary>
        public int Count { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _meta[2]; }

        /// <summary>Number of recycled IDs available for reuse.</summary>
        public int FreeCount { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _meta[1]; }

        /// <summary>True if this table has been allocated via <see cref="New"/>.</summary>
        public bool IsCreated { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => indices != null; }

        /// <summary>
        /// Allocates a new sparse brick id table.
        /// </summary>
        public static SparseBrickIdTable New(Allocator allocator)
        {
            var t = new SparseBrickIdTable
            {
                indices   = (short*)UnsafeUtility.Malloc(CAPACITY * sizeof(short), UnsafeUtility.AlignOf<short>(), allocator),
                _freelist = (short*)UnsafeUtility.Malloc(CAPACITY * sizeof(short), UnsafeUtility.AlignOf<short>(), allocator),
                _meta     = (int*)UnsafeUtility.Malloc(3 * sizeof(int),            UnsafeUtility.AlignOf<int>(),   allocator),
                _allocator = allocator,
            };
            for (int i = 0; i < CAPACITY; i++) t.indices[i] = EMPTY;
            t._meta[0] = 0;
            t._meta[1] = 0;
            t._meta[2] = 0;
            return t;
        }

        /// <summary>
        /// Creates a deep copy of this table backed by <paramref name="allocator"/>.
        /// Indices, free list and counters are duplicated; the new table is independent of the source.
        /// </summary>
        public SparseBrickIdTable Clone(Allocator allocator)
        {
            var c = new SparseBrickIdTable
            {
                indices   = (short*)UnsafeUtility.Malloc(CAPACITY * sizeof(short), UnsafeUtility.AlignOf<short>(), allocator),
                _freelist = (short*)UnsafeUtility.Malloc(CAPACITY * sizeof(short), UnsafeUtility.AlignOf<short>(), allocator),
                _meta     = (int*)UnsafeUtility.Malloc(3 * sizeof(int),            UnsafeUtility.AlignOf<int>(),   allocator),
                _allocator = allocator,
            };
            UnsafeUtility.MemCpy(c.indices, indices, CAPACITY * sizeof(short));
            int freeCount = _meta[1];
            if (freeCount > 0)
                UnsafeUtility.MemCpy(c._freelist, _freelist, freeCount * sizeof(short));
            UnsafeUtility.MemCpy(c._meta, _meta, 3 * sizeof(int));
            return c;
        }

        /// <summary>Frees all unmanaged memory owned by this table.</summary>
        public void Dispose()
        {
            if (indices   != null) { UnsafeUtility.Free(indices,   _allocator); indices   = null; }
            if (_freelist != null) { UnsafeUtility.Free(_freelist, _allocator); _freelist = null; }
            if (_meta     != null) { UnsafeUtility.Free(_meta,     _allocator); _meta     = null; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Idx(int3 p) => Sector.ToBrickIdx(p.x, p.y, p.z);

        /// <summary>
        /// Marks a brick at <paramref name="position"/> as valid and assigns it a unique ID.
        /// </summary>
        /// <param name="position">Brick coordinate within the sector (0..SIZE_IN_BRICKS-1 per axis).</param>
        /// <param name="brickID">The assigned (or pre-existing) brick ID.</param>
        /// <param name="exceedsCapacity">True iff the assigned ID is &gt;= the previous <see cref="Capacity"/> (no reuse possible).</param>
        /// <returns>True if a new ID was assigned, False if the brick already existed.</returns>
        public bool AddBrick(int3 position, out int brickID, out bool exceedsCapacity)
        {
            int idx = Idx(position);
            short existing = indices[idx];
            if (existing != EMPTY)
            {
                brickID = existing;
                exceedsCapacity = false;
                return false;
            }

            if (_meta[1] > 0)
            {
                brickID = HeapPopMin();
                exceedsCapacity = false;
            }
            else
            {
                brickID = _meta[0]++;
                exceedsCapacity = true;
            }

            indices[idx] = (short)brickID;
            _meta[2]++;
            return true;
        }

        /// <summary>
        /// Non-strict variant of <see cref="AddBrick"/>: returns the brick's ID whether it was newly added or already existed.
        /// </summary>
        /// <param name="position">Brick coordinate within the sector.</param>
        /// <param name="brickID">The assigned (or pre-existing) brick ID.</param>
        /// <returns>True iff the assigned ID exceeds the previous <see cref="Capacity"/> (caller must grow any parallel arrays).</returns>
        public bool FindOrAddBrick(int3 position, out int brickID)
        {
            AddBrick(position, out brickID, out bool exceedsCapacity);
            return exceedsCapacity;
        }

        /// <summary>
        /// Marks a brick at <paramref name="position"/> as invalid; its ID becomes available for reuse.
        /// No-op if the brick is already invalid.
        /// </summary>
        public void RemoveBrick(int3 position)
        {
            int idx = Idx(position);
            short id = indices[idx];
            if (id == EMPTY) return;
            indices[idx] = EMPTY;
            HeapPush(id);
            _meta[2]--;
        }

        /// <summary>
        /// Minimum capacity required to hold the current valid bricks without rearranging IDs.
        /// Equals max(activeID) + 1, or 0 if no bricks are valid.
        /// </summary>
        public int MinimumCapacity()
        {
            // Brick IDs never get rearranged, so the minimum capacity is determined
            // by the highest-valued currently-active ID. Scan the indices table —
            // bounded by CAPACITY (4096) so this is cheap.
            int max = -1;
            for (int i = 0; i < CAPACITY; i++)
            {
                short v = indices[i];
                if (v > max) max = v;
            }
            return max + 1;
        }

        /// <summary>
        /// Reduces <see cref="Capacity"/> to <paramref name="target"/>.
        /// Fails if <paramref name="target"/> is not a strict reduction or cannot fit the current valid bricks.
        /// </summary>
        /// <returns>True on success.</returns>
        public bool CompactTo(int target)
        {
            if (target < 0) return false;
            if (target >= _meta[0]) return false;             // not a reduction
            if (target < MinimumCapacity()) return false;      // would orphan an active ID

            // Drop freelist entries that are now out of range, then re-heapify.
            int n = _meta[1];
            int w = 0;
            for (int r = 0; r < n; r++)
            {
                short v = _freelist[r];
                if (v < target) _freelist[w++] = v;
            }
            _meta[1] = w;
            for (int i = (w >> 1) - 1; i >= 0; i--) HeapifyDown(i);

            _meta[0] = target;
            return true;
        }

        // ---------- min-heap on _freelist[0 .. _meta[1]) ----------

        private void HeapPush(short v)
        {
            int i = _meta[1]++;
            _freelist[i] = v;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                short pv = _freelist[p];
                if (pv > _freelist[i])
                {
                    _freelist[p] = _freelist[i];
                    _freelist[i] = pv;
                    i = p;
                }
                else break;
            }
        }

        private short HeapPopMin()
        {
            short top = _freelist[0];
            int n = --_meta[1];
            if (n > 0)
            {
                _freelist[0] = _freelist[n];
                HeapifyDown(0);
            }
            return top;
        }

        private void HeapifyDown(int i)
        {
            int n = _meta[1];
            while (true)
            {
                int l = (i << 1) + 1;
                int r = l + 1;
                int s = i;
                if (l < n && _freelist[l] < _freelist[s]) s = l;
                if (r < n && _freelist[r] < _freelist[s]) s = r;
                if (s == i) break;
                short tmp = _freelist[i];
                _freelist[i] = _freelist[s];
                _freelist[s] = tmp;
                i = s;
            }
        }
    }
}
