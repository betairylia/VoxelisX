using System.Runtime.CompilerServices;

namespace Voxelis
{
    /// <summary>
    /// Foreach-able, Burst-safe enumerator over a sector's allocated brick positions, yielding each
    /// brick's absolute index and shared brick id. "Non-empty" follows the engine's historical
    /// naming here: an allocated brick may currently contain only empty blocks. Backed by a plain
    /// O(BRICKS_IN_SECTOR) sweep for now; the backing can be swapped later without touching callers.
    /// Deliberately implements no <c>IEnumerator</c> interface, so it stays usable inside Burst jobs
    /// — <c>foreach</c> binds to these members directly, with no boxing.
    /// </summary>
    public unsafe struct SectorNonEmptyBrickEnumerator
    {
        /// <summary>One allocated brick: its absolute index in the sector and its shared brick id.</summary>
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
        /// <summary>Iterates this sector's allocated bricks; see <see cref="SectorNonEmptyBrickEnumerator"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator EnumerateNonEmptyBricks()
            => new SectorNonEmptyBrickEnumerator(this);
    }
}
