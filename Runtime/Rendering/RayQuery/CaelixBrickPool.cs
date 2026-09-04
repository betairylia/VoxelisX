using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// One global raw buffer holding the brick records of every sector the inline ray query
    /// backend renders.
    /// </summary>
    /// <remarks>
    /// The DXR path can bind a per-sector brick buffer through the hit group's property block.
    /// Ray queries have no shader table and therefore no per-instance binding, so every sector's
    /// bricks have to live in one buffer and be addressed by an offset the shader looks up from
    /// <see cref="CaelixRayQueryInstanceTable"/>.
    /// <para>
    /// Sectors own power-of-two-sized ranges (see <see cref="SectorRenderer.GetCapacity"/>), handed
    /// out by a bump pointer with a per-capacity free list. Growing the pool allocates a NEW
    /// <see cref="GraphicsBuffer"/> and does not copy the old contents; <see cref="Generation"/> is
    /// how a sector notices it must re-upload its whole range.
    /// </para>
    /// </remarks>
    public sealed class CaelixBrickPool : IDisposable
    {
        /// <summary>A sector's slice of the pool, in bricks.</summary>
        public struct Range
        {
            /// <summary>Index of the range's first brick in the pool.</summary>
            public int OffsetBricks;

            /// <summary>Number of bricks reserved. Always a power of two, 0 when unallocated.</summary>
            public int CapacityBricks;

            public bool IsValid => CapacityBricks > 0;
        }

        /// <summary>The GPU buffer bound as <c>g_bricks</c>. Replaced whenever the pool grows.</summary>
        public GraphicsBuffer Buffer { get; private set; }

        /// <summary>Total number of bricks the current buffer can hold.</summary>
        public int CapacityBricks { get; private set; }

        /// <summary>
        /// Incremented every time <see cref="Buffer"/> is replaced. A sector whose uploaded
        /// generation differs must re-upload its whole range.
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>Bricks handed out by the bump pointer, including bricks currently on a free list.</summary>
        public int UsedBricks => usedBricks;

        /// <summary>Estimated VRAM usage of the pool in bytes.</summary>
        public ulong VRAMUsage => (ulong)CapacityBricks * SectorRenderer.BRICK_DATA_LENGTH * 4;

        // Bump pointer. Never rewound: freed ranges go on the free list instead, keyed by their
        // capacity, so a returning sector of the same size reuses the exact slot.
        private int usedBricks;

        private readonly Dictionary<int, Stack<int>> freeByCapacity = new();

        public CaelixBrickPool(int initialCapacityBricks = 4096)
        {
            Reallocate(NextPow2(Mathf.Max(1, initialCapacityBricks)));
        }

        /// <summary>
        /// Reserves a range of <paramref name="capacityBricks"/> bricks.
        /// </summary>
        /// <param name="capacityBricks">
        /// Power of two, at least 1. Callers size this with <see cref="SectorRenderer.GetCapacity"/>
        /// so the free lists stay dense.
        /// </param>
        public Range Allocate(int capacityBricks)
        {
            if (freeByCapacity.TryGetValue(capacityBricks, out Stack<int> free) && free.Count > 0)
            {
                return new Range { OffsetBricks = free.Pop(), CapacityBricks = capacityBricks };
            }

            if (usedBricks + capacityBricks > CapacityBricks)
            {
                Reallocate(NextPow2(Math.Max(CapacityBricks * 2, usedBricks + capacityBricks)));
            }

            Range range = new Range { OffsetBricks = usedBricks, CapacityBricks = capacityBricks };
            usedBricks += capacityBricks;
            return range;
        }

        /// <summary>Returns a range to its capacity's free list. Its contents are left as they are.</summary>
        public void Free(Range range)
        {
            if (!range.IsValid)
            {
                return;
            }

            if (!freeByCapacity.TryGetValue(range.CapacityBricks, out Stack<int> free))
            {
                free = new Stack<int>();
                freeByCapacity[range.CapacityBricks] = free;
            }

            free.Push(range.OffsetBricks);
        }

        /// <summary>
        /// Copies <paramref name="brickCount"/> bricks starting at <paramref name="firstBrick"/>
        /// from a sector's host buffer into the same position of its pool range.
        /// </summary>
        public void Upload(Range range, NativeArray<int> hostWords, int firstBrick, int brickCount)
        {
            if (brickCount <= 0)
            {
                return;
            }

            int length = SectorRenderer.BRICK_DATA_LENGTH;
            Buffer.SetData(
                hostWords,
                firstBrick * length,
                (range.OffsetBricks + firstBrick) * length,
                brickCount * length);
        }

        private void Reallocate(int capacityBricks)
        {
            Buffer?.Dispose();
            Buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw, capacityBricks * SectorRenderer.BRICK_DATA_LENGTH, 4);
            CapacityBricks = capacityBricks;
            Generation++;

            // Contents are NOT copied: every sector keeps a host copy and re-uploads when it sees
            // the new generation.
        }

        private static int NextPow2(int value)
        {
            int result = 1;
            while (result < value)
            {
                result <<= 1;
            }

            return result;
        }

        public void Dispose()
        {
            Buffer?.Dispose();
            Buffer = null;
        }
    }
}
