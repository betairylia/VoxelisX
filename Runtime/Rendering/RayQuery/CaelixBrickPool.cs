using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// The brick records of every sector the inline ray query backend renders, held in up to
    /// <see cref="MaxPages"/> raw buffers.
    /// </summary>
    /// <remarks>
    /// The DXR path can bind a per-sector brick buffer through the hit group's property block.
    /// Ray queries have no shader table and therefore no per-instance binding, so every sector's
    /// bricks have to live in a shared buffer and be addressed by an offset the shader looks up from
    /// <see cref="CaelixRayQueryInstanceTable"/>.
    /// <para>
    /// <b>Why pages.</b> One <see cref="GraphicsBuffer"/> cannot exceed
    /// <see cref="SystemInfo.maxGraphicsBufferSize"/> (about 3.9 GB on current desktop drivers), and
    /// one brick record is <see cref="SectorRenderer.BRICK_DATA_LENGTH"/> words = 1096 bytes, so a
    /// large streamed scene (millions of live bricks) does not fit a single buffer. The pool
    /// therefore owns several, each capped at <see cref="PageCapacityLimitBricks"/>, and every
    /// sector names the page its bricks live in. The shader picks the page with a switch over the
    /// named buffers, so <see cref="MaxNamedPages"/> must match <c>CAELIX_BRICK_PAGES</c> in
    /// <c>Shaders/CaelixBrickPages.hlsl</c>.
    /// </para>
    /// <para>
    /// <b>Why compaction is free.</b> Sectors own power-of-two-sized ranges (see
    /// <see cref="SectorRenderer.GetCapacity"/>), handed out by a per-page bump pointer with a
    /// per-capacity free list. That fragments while a world streams: freed ranges are only reused by
    /// a sector of the exact same size. Growing a page allocates a NEW buffer and does not copy the
    /// old contents, so every sector on it has to re-upload its whole range anyway; re-packing the
    /// live ranges at the same time costs nothing and is what keeps a page from growing past the
    /// buffer cap out of fragmentation alone.
    /// </para>
    /// <para>
    /// <b>Why handles are classes.</b> Compaction moves ranges, so the pool has to be able to
    /// rewrite a sector's offset in place. A sector notices the move because
    /// <see cref="PageGeneration"/> changed, re-uploads, and republishes its instance record.
    /// </para>
    /// </remarks>
    public sealed class CaelixBrickPool : IDisposable
    {
        /// <summary>
        /// Number of pages the shader can select between. Must match <c>CAELIX_BRICK_PAGES</c> in
        /// <c>Shaders/CaelixBrickPages.hlsl</c>.
        /// </summary>
        public const int MaxPages = 32;

        /// <summary>
        /// Pages that can be bound by name: the shaders select the page with a switch over that many
        /// named buffers, so it must match <c>CAELIX_BRICK_PAGES</c> in
        /// <c>Shaders/CaelixBrickPages.hlsl</c>. Used by the inline ray query kernel and by the DXR
        /// hit group in <see cref="CaelixBrickStorage.SharedPoolInstanceTable"/> storage. In
        /// <see cref="CaelixBrickStorage.SharedPool"/> storage the hit group binds its page per
        /// instance instead and can use every page up to <see cref="MaxPages"/>.
        /// </summary>
        /// <remarks>
        /// Sixteen rather than four because of the DXR view limit: a hit group reads its page
        /// through a buffer view, capped at 2^18 bricks (see
        /// <see cref="DefaultDxrPageCapacityLimitBricks"/>), so a large scene needs many more pages
        /// there than the compute kernel does at 2^21 bricks each.
        /// </remarks>
        public const int MaxNamedPages = 16;

        /// <summary>
        /// Page limit for the DXR pipeline path. A hit group reads <c>g_bricks</c> through a buffer
        /// VIEW from its shader record, and D3D12 caps a buffer view at 2^27 elements, 512 MB for a
        /// raw buffer (D3D12_REQ_BUFFER_RESOURCE_TEXEL_COUNT_2_TO_EXP). Every brick past that mark
        /// in a larger page reads as zero, i.e. as empty space. 2^18 bricks is 287 MB. The compute
        /// kernel binds its pages as root descriptors and has no such limit, hence the larger
        /// <see cref="DefaultPageCapacityLimitBricks"/>.
        /// </summary>
        public const int DefaultDxrPageCapacityLimitBricks = 1 << 18;

        /// <summary>Default cap on one page, in bricks: 2^21 records = about 2.3 GB.</summary>
        public const int DefaultPageCapacityLimitBricks = 1 << 21;

        /// <summary>
        /// A sector's slice of the pool. Mutated by the pool when a page is compacted; read it fresh
        /// every use rather than caching its fields.
        /// </summary>
        public sealed class Handle
        {
            /// <summary>Index of the page holding this range; the value the instance record publishes.</summary>
            public int Page { get; internal set; }

            /// <summary>Index of the range's first brick inside its page.</summary>
            public int OffsetBricks { get; internal set; }

            /// <summary>Number of bricks reserved. Always a power of two, 0 when unallocated or freed.</summary>
            public int CapacityBricks { get; internal set; }

            public bool IsValid => CapacityBricks > 0;
        }

        /// <summary>One GPU buffer plus the bookkeeping of the ranges inside it.</summary>
        private sealed class Page
        {
            public GraphicsBuffer Buffer;

            /// <summary>Bricks the current buffer can hold.</summary>
            public int CapacityBricks;

            /// <summary>Incremented every time <see cref="Buffer"/> is replaced.</summary>
            public int Generation;

            /// <summary>Bricks handed out by the bump pointer, including bricks on a free list.</summary>
            public int UsedBricks;

            /// <summary>Freed offsets, keyed by the capacity they were handed out at.</summary>
            public readonly Dictionary<int, Stack<int>> FreeByCapacity = new();

            /// <summary>Every handle currently pointing into this page. Compaction rewrites all of them.</summary>
            public readonly List<Handle> Live = new();

            /// <summary>Sum of the live handles' capacities: what a compacted page would use.</summary>
            public int LiveBricks;
        }

        private readonly List<Page> pages = new();

        /// <summary>
        /// Cap on one page, in bricks. Clamped at construction so a page can never ask for more than
        /// <see cref="SystemInfo.maxGraphicsBufferSize"/> bytes.
        /// </summary>
        public int PageCapacityLimitBricks { get; }

        /// <summary>Number of pages currently open, 1..<see cref="MaxPages"/>.</summary>
        public int PageCount => pages.Count;

        /// <summary>Bricks all page buffers together can hold.</summary>
        public int TotalCapacityBricks
        {
            get
            {
                int total = 0;
                for (int i = 0; i < pages.Count; i++)
                {
                    total += pages[i].CapacityBricks;
                }

                return total;
            }
        }

        /// <summary>Bricks currently reserved by a live handle: what the pool would use if compacted now.</summary>
        public int TotalLiveBricks
        {
            get
            {
                int total = 0;
                for (int i = 0; i < pages.Count; i++)
                {
                    total += pages[i].LiveBricks;
                }

                return total;
            }
        }

        /// <summary>Bricks handed out by the bump pointers, including bricks sitting on a free list.</summary>
        public int TotalUsedBricks
        {
            get
            {
                int total = 0;
                for (int i = 0; i < pages.Count; i++)
                {
                    total += pages[i].UsedBricks;
                }

                return total;
            }
        }

        /// <summary>Estimated VRAM usage of every page in bytes.</summary>
        public ulong VRAMUsage => (ulong)TotalCapacityBricks * SectorRenderer.BRICK_DATA_LENGTH * 4;

        // Logged once: the message names a scene-authoring problem, and repeating it every frame
        // costs more than the allocation that failed.
        private bool warnedExhausted;

        /// <param name="initialCapacityBricks">Size of the first page, rounded up to a power of two.</param>
        /// <param name="pageCapacityLimitBricks">
        /// Upper size of one page. Clamped down to what one <see cref="GraphicsBuffer"/> can hold on
        /// this platform, and to a power of two, so growth targets stay exact.
        /// </param>
        public CaelixBrickPool(
            int initialCapacityBricks = 4096, int pageCapacityLimitBricks = DefaultPageCapacityLimitBricks)
        {
            long bytesPerBrick = SectorRenderer.BRICK_DATA_LENGTH * 4;
            long platformLimit = SystemInfo.maxGraphicsBufferSize / bytesPerBrick;
            long limit = Math.Min(pageCapacityLimitBricks, platformLimit);

            // Down to a power of two, never below one sector's worth: every capacity handed out is a
            // power of two, so a limit that is not one only wastes the tail of the page.
            PageCapacityLimitBricks = Math.Max(4096, PrevPow2((int)Math.Min(limit, int.MaxValue)));

            OpenPage(initialCapacityBricks);
        }

        /// <summary>
        /// The buffer of one page, for binding as <c>g_bricks0..15</c>.
        /// </summary>
        /// <remarks>
        /// Pages past <see cref="PageCount"/> return the last open page's buffer rather than null:
        /// the shaders declare every named page, and Unity logs "Property (g_bricksN) ... is not
        /// set" every frame for any it never sees bound. Nothing reads those slots, because no
        /// instance record names a page that is not open.
        /// </remarks>
        public GraphicsBuffer GetPageBuffer(int page)
        {
            if (pages.Count == 0)
            {
                return null;
            }

            return pages[Mathf.Clamp(page, 0, pages.Count - 1)].Buffer;
        }

        /// <summary>
        /// Generation of one page's buffer, starting at 0. A sector whose uploaded generation
        /// differs must re-upload its whole range. Returns -1 for a page that is not open.
        /// </summary>
        public int PageGeneration(int page)
        {
            if (page < 0 || page >= pages.Count)
            {
                return -1;
            }

            return pages[page].Generation;
        }

        /// <summary>
        /// Reserves a range of <paramref name="capacityBricks"/> bricks on some page.
        /// </summary>
        /// <param name="capacityBricks">
        /// Power of two, at least 1. Callers size this with <see cref="SectorRenderer.GetCapacity"/>
        /// so the free lists stay dense.
        /// </param>
        /// <returns>
        /// The reserved range, or an invalid handle when every page is full and no page is left to
        /// open. The caller must skip the sector in that case.
        /// </returns>
        public Handle Allocate(int capacityBricks)
        {
            // One sector is at most Sector.SIZE_IN_BRICKS^3 = 4096 bricks, well under any page
            // limit, so a range can always fit a page of its own.
            Debug.Assert(
                capacityBricks > 0 && capacityBricks <= PageCapacityLimitBricks,
                "CaelixBrickPool: requested range does not fit a page.");

            if (capacityBricks <= 0 || capacityBricks > PageCapacityLimitBricks)
            {
                return new Handle();
            }

            // 1. An exactly-sized hole somebody freed.
            for (int i = 0; i < pages.Count; i++)
            {
                Page page = pages[i];
                if (page.FreeByCapacity.TryGetValue(capacityBricks, out Stack<int> free) && free.Count > 0)
                {
                    return Register(page, i, free.Pop(), capacityBricks);
                }
            }

            // 2. Room past some page's bump pointer.
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i].UsedBricks + capacityBricks <= pages[i].CapacityBricks)
                {
                    return Bump(pages[i], i, capacityBricks);
                }
            }

            // 3. Grow (and thereby compact) the last page. Compaction alone can be what makes room:
            // it drops every hole the free lists were holding.
            int lastIndex = pages.Count - 1;
            if (Grow(pages[lastIndex], capacityBricks))
            {
                return Bump(pages[lastIndex], lastIndex, capacityBricks);
            }

            // 4. A fresh page.
            if (pages.Count < MaxPages)
            {
                Page opened = OpenPage(Math.Max(4096, capacityBricks));
                return Bump(opened, pages.Count - 1, capacityBricks);
            }

            // 5. Out of room entirely.
            if (!warnedExhausted)
            {
                Debug.LogError(
                    $"CaelixBrickPool: out of pages ({MaxPages} x {PageCapacityLimitBricks} bricks) with " +
                    $"{TotalLiveBricks} bricks live; raise PageCapacityLimitBricks or reduce the scene. " +
                    "Sectors that cannot be allocated are not rendered.");
                warnedExhausted = true;
            }

            return new Handle();
        }

        /// <summary>
        /// Returns a range to its page's free list and invalidates the handle. The range's contents
        /// are left as they are.
        /// </summary>
        public void Free(Handle handle)
        {
            if (handle == null || !handle.IsValid || handle.Page < 0 || handle.Page >= pages.Count)
            {
                return;
            }

            Page page = pages[handle.Page];
            page.Live.Remove(handle);
            page.LiveBricks -= handle.CapacityBricks;

            if (!page.FreeByCapacity.TryGetValue(handle.CapacityBricks, out Stack<int> free))
            {
                free = new Stack<int>();
                page.FreeByCapacity[handle.CapacityBricks] = free;
            }

            free.Push(handle.OffsetBricks);
            handle.CapacityBricks = 0;
        }

        /// <summary>
        /// Copies <paramref name="brickCount"/> bricks starting at <paramref name="firstBrick"/>
        /// from a sector's host buffer into the same position of its range.
        /// </summary>
        public void Upload(Handle handle, NativeArray<int> hostWords, int firstBrick, int brickCount)
        {
            if (brickCount <= 0 || handle == null || !handle.IsValid)
            {
                return;
            }

            int length = SectorRenderer.BRICK_DATA_LENGTH;
            pages[handle.Page].Buffer.SetData(
                hostWords,
                firstBrick * length,
                (handle.OffsetBricks + firstBrick) * length,
                brickCount * length);
        }

        /// <summary>
        /// Re-packs one page's live ranges into a new, usually larger buffer.
        /// </summary>
        /// <param name="page">The page to grow.</param>
        /// <param name="extraBricks">Bricks the caller is about to allocate on it.</param>
        /// <returns>False when even a fully compacted page could not hold the request.</returns>
        /// <remarks>
        /// The new buffer's contents are NOT copied: every sector on the page sees the new
        /// generation and re-uploads its whole range, which is also what makes moving the ranges
        /// free. Growth targets 1.5x what the page actually needs, rounded up to a power of two, so
        /// a streaming world does not double its way past the buffer cap.
        /// </remarks>
        private bool Grow(Page page, int extraBricks)
        {
            int needed = page.LiveBricks + extraBricks;
            if (needed > PageCapacityLimitBricks)
            {
                return false;
            }

            // Compaction alone may be what makes room (it drops every hole the free lists held), so
            // the page only grows when 1.5x the live data no longer fits; it never shrinks.
            int newCapacity = Math.Min(
                PageCapacityLimitBricks, NextPow2(Math.Max(page.CapacityBricks, needed * 3 / 2)));

            // Largest range first. Every capacity is a power of two, so packing them in decreasing
            // size leaves each range aligned to its own size and the page with one contiguous tail
            // instead of holes. OrderByDescending rather than List.Sort because it is stable, and
            // this runs a handful of times per session.
            int offset = 0;
            foreach (Handle handle in page.Live.OrderByDescending(h => h.CapacityBricks))
            {
                handle.OffsetBricks = offset;
                offset += handle.CapacityBricks;
            }

            page.FreeByCapacity.Clear();
            page.UsedBricks = page.LiveBricks;

            page.Buffer?.Dispose();
            page.Buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw, newCapacity * SectorRenderer.BRICK_DATA_LENGTH, 4);
            page.CapacityBricks = newCapacity;
            page.Generation++;

            return true;
        }

        /// <summary>Opens a page of at least <paramref name="capacityBricks"/> bricks and appends it.</summary>
        private Page OpenPage(int capacityBricks)
        {
            int capacity = Math.Min(PageCapacityLimitBricks, NextPow2(Math.Max(1, capacityBricks)));
            Page page = new Page
            {
                Buffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw, capacity * SectorRenderer.BRICK_DATA_LENGTH, 4),
                CapacityBricks = capacity
            };

            pages.Add(page);
            return page;
        }

        /// <summary>Records a handle at a known offset as live on <paramref name="page"/>.</summary>
        private static Handle Register(Page page, int pageIndex, int offsetBricks, int capacityBricks)
        {
            Handle handle = new Handle
            {
                Page = pageIndex,
                OffsetBricks = offsetBricks,
                CapacityBricks = capacityBricks
            };

            page.Live.Add(handle);
            page.LiveBricks += capacityBricks;
            return handle;
        }

        /// <summary>Takes a range off <paramref name="page"/>'s bump pointer. The caller must have checked it fits.</summary>
        private static Handle Bump(Page page, int pageIndex, int capacityBricks)
        {
            Handle handle = Register(page, pageIndex, page.UsedBricks, capacityBricks);
            page.UsedBricks += capacityBricks;
            return handle;
        }

        private static int NextPow2(int value)
        {
            int result = 1;
            while (result < value && result < (1 << 30))
            {
                result <<= 1;
            }

            return result;
        }

        private static int PrevPow2(int value)
        {
            int result = 1;
            while (result <= (value >> 1) && result < (1 << 30))
            {
                result <<= 1;
            }

            return result;
        }

        public void Dispose()
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Buffer?.Dispose();
                pages[i].Buffer = null;
            }

            pages.Clear();
        }
    }
}
