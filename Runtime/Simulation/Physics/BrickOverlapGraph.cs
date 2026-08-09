using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    /// <summary>
    /// One endpoint of the brick-overlap graph: a stable entity GUID plus a global brick
    /// coordinate in that entity's local voxel grid (including sector offsets).
    /// </summary>
    public struct BrickOverlapKey : IEquatable<BrickOverlapKey>, IComparable<BrickOverlapKey>
    {
        public Guid128 EntityId;
        public int3 BrickCoord;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(BrickOverlapKey other)
        {
            return EntityId.Equals(other.EntityId) && math.all(BrickCoord == other.BrickCoord);
        }

        public override bool Equals(object obj) => obj is BrickOverlapKey other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            uint h = math.hash(EntityId.Value);
            return (int)math.hash(new uint4(h, (uint)BrickCoord.x, (uint)BrickCoord.y, (uint)BrickCoord.z));
        }

        /// <summary> Deterministic order: GUID lanes x/y/z/w, then brick x/y/z. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(BrickOverlapKey other)
        {
            int c = EntityId.CompareTo(other.EntityId);
            if (c != 0) return c;
            if (BrickCoord.x != other.BrickCoord.x) return BrickCoord.x < other.BrickCoord.x ? -1 : 1;
            if (BrickCoord.y != other.BrickCoord.y) return BrickCoord.y < other.BrickCoord.y ? -1 : 1;
            if (BrickCoord.z != other.BrickCoord.z) return BrickCoord.z < other.BrickCoord.z ? -1 : 1;
            return 0;
        }

        public override string ToString() => $"({EntityId} @ {BrickCoord})";
    }

    /// <summary>
    /// One canonical undirected overlap pair. A is strictly smaller than B under
    /// <see cref="BrickOverlapKey.CompareTo"/>.
    /// </summary>
    public struct BrickOverlapPair
    {
        public BrickOverlapKey A;
        public BrickOverlapKey B;
    }

    /// <summary>
    /// Contiguous neighbor range of one source brick inside the graph's neighbor array.
    /// </summary>
    public struct BrickOverlapSourceRange
    {
        public BrickOverlapKey Source;
        public int Start;
        public int Count;
    }

    /// <summary>
    /// Enumerator over the neighbor keys of one source brick. Also directly enumerable
    /// with foreach.
    /// </summary>
    public struct BrickOverlapEnumerator
    {
        [ReadOnly] NativeArray<BrickOverlapKey> m_Neighbors;
        readonly int m_End;
        int m_Index;

        internal BrickOverlapEnumerator(NativeArray<BrickOverlapKey> neighbors, int start, int count)
        {
            m_Neighbors = neighbors;
            m_Index = start - 1;
            m_End = start + count;
            Count = count;
        }

        /// <summary> Number of neighbors in this range. </summary>
        public int Count { get; }

        public BrickOverlapKey Current => m_Neighbors[m_Index];

        public bool MoveNext()
        {
            m_Index++;
            return m_Index < m_End;
        }

        public BrickOverlapEnumerator GetEnumerator() => this;
    }

    /// <summary>
    /// Read-only view of the published brick-overlap graph of one physics step.
    ///
    /// The graph is conservative: a pair means a queried brick's alien-neighborhood bound
    /// reached an allocated brick of another body, not that individual occupied blocks
    /// intersect. Pair and neighbor ordering is deterministic (sorted by
    /// <see cref="BrickOverlapKey.CompareTo"/>) regardless of raw candidate order.
    ///
    /// The view borrows the builder's double-buffered storage. It stays valid until the
    /// second next publish; re-fetch it each step (check <see cref="Version"/>). A default
    /// view is safe to query: it is empty and never throws.
    /// </summary>
    public struct BrickOverlapGraph
    {
        [ReadOnly] NativeArray<BrickOverlapPair> m_Pairs;
        [ReadOnly] NativeArray<BrickOverlapKey> m_Neighbors;
        [ReadOnly] NativeArray<BrickOverlapSourceRange> m_Ranges;
        [ReadOnly] NativeParallelHashMap<BrickOverlapKey, int>.ReadOnly m_RangeLookup;

        readonly int m_Version;
        readonly bool m_IsCreated;

        internal BrickOverlapGraph(
            int version,
            NativeArray<BrickOverlapPair> pairs,
            NativeArray<BrickOverlapKey> neighbors,
            NativeArray<BrickOverlapSourceRange> ranges,
            NativeParallelHashMap<BrickOverlapKey, int>.ReadOnly rangeLookup)
        {
            m_Version = version;
            m_Pairs = pairs;
            m_Neighbors = neighbors;
            m_Ranges = ranges;
            m_RangeLookup = rangeLookup;
            m_IsCreated = true;
        }

        /// <summary> True when this view points at published storage (even an empty publish). </summary>
        public bool IsCreated => m_IsCreated;

        /// <summary> Publication counter. Advances once per successful publish. </summary>
        public int Version => m_Version;

        /// <summary> Number of canonical undirected pairs. </summary>
        public int PairCount => m_IsCreated ? m_Pairs.Length : 0;

        /// <summary> Number of source bricks that have at least one neighbor. </summary>
        public int SourceCount => m_IsCreated ? m_Ranges.Length : 0;

        /// <summary>
        /// Indexed access to the sorted canonical pair list. Duplicate raw query hits are
        /// removed by the graph builder.
        /// </summary>
        public BrickOverlapPair GetPair(int index) => m_Pairs[index];

        /// <summary>
        /// The sorted canonical pair list after raw-hit deduplication.
        /// </summary>
        public NativeArray<BrickOverlapPair>.ReadOnly Pairs => m_Pairs.AsReadOnly();

        /// <summary> All source ranges, sorted by source key. </summary>
        public NativeArray<BrickOverlapSourceRange>.ReadOnly SourceRanges => m_Ranges.AsReadOnly();

        /// <summary>
        /// Looks up the neighbors of one source brick. Returns false (with an empty
        /// enumerator) when the source has no overlaps or the graph is empty.
        /// </summary>
        public bool TryGetOverlaps(BrickOverlapKey source, out BrickOverlapEnumerator neighbors)
        {
            neighbors = default;
            if (!m_IsCreated || m_Ranges.Length == 0)
            {
                return false;
            }
            if (!m_RangeLookup.TryGetValue(source, out int rangeIndex))
            {
                return false;
            }
            BrickOverlapSourceRange range = m_Ranges[rangeIndex];
            neighbors = new BrickOverlapEnumerator(m_Neighbors, range.Start, range.Count);
            return true;
        }
    }

    /// <summary> Counters of the most recent graph build, for profiling and diagnostics. </summary>
    public struct BrickOverlapGraphStats
    {
        public int RawCandidates;
        public int PublishedPairs;
        public int ActiveSourceBricks;
        public int NumBodies;
        public double BuildMilliseconds;
        public bool UsedSerialPath;
    }
}
