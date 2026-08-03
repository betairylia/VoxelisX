using System.Runtime.CompilerServices;

namespace Voxelis
{
    /// <summary>
    /// Foreach-able, Burst-safe enumerator over a sector's non-empty bricks, yielding each brick's
    /// absolute index and shared brick id. Backed by a plain O(BRICKS_IN_SECTOR) sweep for now; the
    /// backing can be swapped later (e.g. an incrementally-maintained non-empty-brick list) without
    /// touching callers. Deliberately implements no <c>IEnumerator</c> interface, so it stays usable
    /// inside Burst jobs — <c>foreach</c> binds to these members directly, with no boxing.
    /// </summary>
    public unsafe struct SectorNonEmptyBrickEnumerator
    {
        /// <summary>One non-empty brick: its absolute index in the sector and its shared brick id.</summary>
        public readonly struct BrickRef
        {
            public readonly int BrickAbs;
            public readonly short Bid;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public BrickRef(int brickAbs, short bid)
            {
                BrickAbs = brickAbs;
                Bid = bid;
            }
        }

        private readonly short* indices;
        private int cursor;
        private short bid;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator(in Sector sector)
        {
            indices = sector.brickIdx;
            cursor = -1;
            bid = Sector.BRICKID_EMPTY;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            for (int i = cursor + 1; i < Sector.BRICKS_IN_SECTOR; i++)
            {
                short b = indices[i];
                if (b != Sector.BRICKID_EMPTY)
                {
                    cursor = i;
                    bid = b;
                    return true;
                }
            }
            cursor = Sector.BRICKS_IN_SECTOR;
            return false;
        }

        public BrickRef Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new BrickRef(cursor, bid);
        }
    }

    public unsafe partial struct Sector
    {
        /// <summary>Iterates this sector's non-empty bricks; see <see cref="SectorNonEmptyBrickEnumerator"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator EnumerateNonEmptyBricks()
            => new SectorNonEmptyBrickEnumerator(this);
    }
}
