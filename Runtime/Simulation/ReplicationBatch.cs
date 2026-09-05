using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>
    /// One sector to pack: entity guid, sector position, the sector, and whether every brick goes
    /// (join / new-to-a-connection) or only the bricks whose dirty flags match
    /// <see cref="Sector.ReplicationDirtyMask"/> (delta).
    /// </summary>
    public struct ReplicationSectorRef
    {
        public Guid128 Guid;
        public int3 SectorPos;
        public SectorHandle Sector;

        /// <summary>Non-zero for a full sector, zero for a delta. A byte because the struct lives in native memory.</summary>
        public byte FullSector;
    }

    /// <summary>
    /// One finished <see cref="NetMessageType.BrickData"/> message inside a
    /// <see cref="ReplicationBatch"/>'s byte buffer. <see cref="Length"/> 0 means the sector had
    /// nothing to send; a caller skips those ranges.
    /// </summary>
    public struct ReplicationRange
    {
        public Guid128 Guid;
        public int3 SectorPos;
        public int Offset;
        public int Length;
        public byte FullSector;
    }

    /// <summary>
    /// Phase A of replication: packs BrickData messages for a list of sectors, in parallel over
    /// sectors, into a byte buffer. Built once per world per tick for the delta and shared by
    /// every connection; built again on demand for the sectors a connection does not know yet.
    /// Owned and reused by <see cref="CaelixServer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The batch packs in CHUNKS, not in one buffer: <see cref="Prepare"/> once, then
    /// <see cref="BuildNextChunk"/> until it returns false, sending the ranges of each chunk before
    /// the next one is built. A join of a large world hands the batch every sector of the world,
    /// which for an 8K world is about 2.9 million bricks of roughly a kilobyte each — near 3 GB.
    /// One buffer for that overflowed the <c>int</c> prefix sum and wrote gigabytes into a 64 KB
    /// allocation. <see cref="MaxChunkBytes"/> bounds a chunk instead.
    /// </para>
    /// <para>
    /// Phase B (<see cref="ServerConnection.ReplicateWorld"/>) is the per-connection half: it diffs
    /// the connection's knowledge, sends the lifecycle messages, and forwards the slices this batch
    /// produced. Nothing here touches a connection, so the same bytes serve all of them.
    /// </para>
    /// </remarks>
    public sealed unsafe class ReplicationBatch : IDisposable
    {
        /// <summary>Capacity the byte buffer is shrunk back to once a chunk sequence finished.</summary>
        private const int IdleBytesCapacity = 4 * 1024 * 1024;

        private NativeList<ReplicationSectorRef> sectors;
        private NativeList<int> sizes;
        private NativeList<int> offsets;
        private NativeList<byte> bytes;
        private NativeList<ReplicationRange> ranges;
        private bool disposed;

        private int cursor;
        private ushort buildWorldId;
        private uint buildTick;
        private ushort buildSlotMask;

        public ReplicationBatch()
        {
            sectors = new NativeList<ReplicationSectorRef>(64, Allocator.Persistent);
            sizes = new NativeList<int>(64, Allocator.Persistent);
            offsets = new NativeList<int>(64, Allocator.Persistent);
            bytes = new NativeList<byte>(64 * 1024, Allocator.Persistent);
            ranges = new NativeList<ReplicationRange>(64, Allocator.Persistent);
        }

        /// <summary>
        /// Upper bound on the bytes one chunk packs. A join of a large world streams through chunks
        /// of this size instead of one buffer holding the whole world (which overflowed int at
        /// ~3 GB and froze the Editor). A single sector larger than this still goes alone; 4096
        /// bricks of Block is about 4.2 MB, far under the default.
        /// </summary>
        public int MaxChunkBytes { get; set; } = 64 * 1024 * 1024;

        /// <summary>
        /// Number of chunks the last <see cref="Prepare"/> / <see cref="BuildNextChunk"/> sequence
        /// produced. For tests and stats.
        /// </summary>
        public int LastChunkCount { get; private set; }

        /// <summary>Number of packed ranges in the CURRENT chunk. Zero until a chunk was built.</summary>
        public int Count => ranges.Length;

        /// <summary>Number of sectors added since the last <see cref="Clear"/>, built or not.</summary>
        public int SectorCount => sectors.Length;

        public ReplicationRange this[int i] => ranges[i];

        /// <summary>Drops the sectors and the bytes of the previous build. Keeps the capacity.</summary>
        public void Clear()
        {
            sectors.Clear();
            sizes.Clear();
            offsets.Clear();
            bytes.Clear();
            ranges.Clear();
            cursor = 0;
        }

        /// <summary>The sector added at <paramref name="i"/>. Only valid before <see cref="Prepare"/>.</summary>
        internal ReplicationSectorRef GetSector(int i) => sectors[i];

        /// <summary>
        /// Drops the sector at <paramref name="i"/>, moving the last one into its place. Order of a
        /// batch does not matter, so a caller may filter the added sectors before
        /// <see cref="Prepare"/>.
        /// </summary>
        internal void RemoveSectorAtSwapBack(int i)
        {
            sectors.RemoveAtSwapBack(i);
        }

        public void Add(Guid128 guid, int3 sectorPos, SectorHandle sector, bool fullSector)
        {
            sectors.Add(new ReplicationSectorRef
            {
                Guid = guid,
                SectorPos = sectorPos,
                Sector = sector,
                FullSector = (byte)(fullSector ? 1 : 0),
            });
        }

        /// <summary>
        /// Adds every sector of every entity whose <c>sectorDirtyFlags</c> intersect
        /// <see cref="Sector.ReplicationDirtyMask"/>, as a delta. Main-thread walk of the world's
        /// entity map; the packing itself is the job pair in <see cref="Prepare"/> and
        /// <see cref="BuildNextChunk"/>.
        /// </summary>
        public void CollectDirtySectors(CaelixWorld world)
        {
            if (world == null)
            {
                return;
            }

            const ushort dirtyMask = (ushort)Sector.ReplicationDirtyMask;
            foreach (var entityEntry in world.Data.VoxelEntities)
            {
                VoxelEntityData data = entityEntry.Value;
                foreach (var sectorEntry in data.sectors)
                {
                    SectorHandle handle = sectorEntry.Value;
                    if ((handle.Get().sectorDirtyFlags & dirtyMask) == 0)
                    {
                        continue;
                    }

                    Add(entityEntry.Key, sectorEntry.Key, handle, fullSector: false);
                }
            }
        }

        /// <summary>
        /// Runs the size job over every added sector and rewinds the chunk cursor. Call once, then
        /// loop <see cref="BuildNextChunk"/>. Safe to call with zero sectors.
        /// </summary>
        public void Prepare(ushort worldId, uint tick, ushort slotMask)
        {
            ranges.Clear();
            bytes.Clear();
            LastChunkCount = 0;
            cursor = 0;
            buildWorldId = worldId;
            buildTick = tick;
            buildSlotMask = slotMask;

            int count = sectors.Length;
            if (count == 0)
            {
                return;
            }

            sizes.ResizeUninitialized(count);

            new SizeJob
            {
                Sectors = sectors.AsArray(),
                Sizes = sizes.AsArray(),
                SlotMask = slotMask,
            }.Schedule(count, 1).Complete();
        }

        /// <summary>
        /// Packs the next run of sectors whose sizes sum to at most <see cref="MaxChunkBytes"/>
        /// (always at least one sector) into the byte buffer and fills the ranges for them. Returns
        /// false, with <see cref="Count"/> 0, when every sector was packed. The ranges of a chunk
        /// are only valid until the next call.
        /// </summary>
        public bool BuildNextChunk()
        {
            ranges.Clear();
            if (cursor >= sectors.Length)
            {
                bytes.Clear();
                ShrinkBytes();
                return false;
            }

            int end = cursor;
            int total = 0;
            while (end < sectors.Length && (end == cursor || (long)total + sizes[end] <= MaxChunkBytes))
            {
                total += sizes[end];
                end++;
            }

            int count = end - cursor;
            offsets.ResizeUninitialized(count);

            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                ReplicationSectorRef sectorRef = sectors[cursor + i];
                int size = sizes[cursor + i];
                offsets[i] = offset;
                ranges.Add(new ReplicationRange
                {
                    Guid = sectorRef.Guid,
                    SectorPos = sectorRef.SectorPos,
                    Offset = offset,
                    Length = size,
                    FullSector = sectorRef.FullSector,
                });
                offset += size;
            }

            bytes.ResizeUninitialized(total);
            if (total > 0)
            {
                new WriteJob
                {
                    Sectors = sectors.AsArray(),
                    Sizes = sizes.AsArray(),
                    Offsets = offsets.AsArray(),
                    BasePtr = bytes.GetUnsafePtr(),
                    Start = cursor,
                    WorldId = buildWorldId,
                    Tick = buildTick,
                    SlotMask = buildSlotMask,
                }.Schedule(count, 1).Complete();
            }

            cursor = end;
            LastChunkCount++;
            return true;
        }

        /// <summary>
        /// Gives the chunk buffer back once the sequence ended, so a one-off load of a large world
        /// does not pin <see cref="MaxChunkBytes"/> for the rest of the session.
        /// </summary>
        private void ShrinkBytes()
        {
            if (bytes.Capacity > IdleBytesCapacity)
            {
                // Length is 0 here, so the shrink is always legal.
                bytes.Capacity = IdleBytesCapacity;
            }
        }

        /// <summary>The bytes of range <paramref name="i"/>. Valid until the next chunk is built.</summary>
        public ReadOnlySpan<byte> Slice(int i)
        {
            ReplicationRange range = ranges[i];
            return new ReadOnlySpan<byte>(bytes.GetUnsafeReadOnlyPtr() + range.Offset, range.Length);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (sectors.IsCreated) sectors.Dispose();
            if (sizes.IsCreated) sizes.Dispose();
            if (offsets.IsCreated) offsets.Dispose();
            if (bytes.IsCreated) bytes.Dispose();
            if (ranges.IsCreated) ranges.Dispose();
        }

        /// <summary>
        /// Bricks one sector contributes. Both jobs call this, so the count the size is computed
        /// from and the count the header carries cannot disagree: nothing writes bricks between the
        /// two jobs, because both run inside <c>CaelixServer.Replicate</c>, inside the tick.
        /// </summary>
        private static int CountBricks(ref Sector sector, byte fullSector)
        {
            if (fullSector != 0)
            {
                return sector.NonEmptyBrickCount;
            }

            int count = 0;
            SectorDirtyBrickEnumerator dirty = sector.EnumerateDirtyBricks(Sector.ReplicationDirtyMask);
            while (dirty.MoveNext())
            {
                count++;
            }

            return count;
        }

        /// <summary>Bytes one BrickData message takes for <paramref name="brickCount"/> bricks.</summary>
        private static int MessageBytes(ref Sector sector, int brickCount, ushort slotMask)
        {
            if (brickCount == 0)
            {
                return 0;
            }

            // u16 brickIdx + u16 dirtyFlags per brick, then the brick's slot records.
            return NetHeader.Size
                   + UnsafeUtility.SizeOf<BrickBatchHeader>()
                   + brickCount * (2 + 2 + sector.ReplicatedBrickBytes(slotMask));
        }

        [BurstCompile]
        private struct SizeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ReplicationSectorRef> Sectors;
            public NativeArray<int> Sizes;
            public ushort SlotMask;

            public void Execute(int i)
            {
                ReplicationSectorRef sectorRef = Sectors[i];
                ref Sector sector = ref sectorRef.Sector.Get();
                int count = CountBricks(ref sector, sectorRef.FullSector);
                Sizes[i] = MessageBytes(ref sector, count, SlotMask);
            }
        }

        [BurstCompile]
        private struct WriteJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ReplicationSectorRef> Sectors;
            [ReadOnly] public NativeArray<int> Sizes;
            [ReadOnly] public NativeArray<int> Offsets;

            /// <summary>Base of the chunk buffer, already sized. The job never resizes it.</summary>
            [NativeDisableUnsafePtrRestriction] public byte* BasePtr;

            /// <summary>Index of the chunk's first sector. <c>Offsets</c> is chunk-relative, the other two are not.</summary>
            public int Start;

            public ushort WorldId;
            public uint Tick;
            public ushort SlotMask;

            public void Execute(int i)
            {
                int size = Sizes[Start + i];
                if (size == 0)
                {
                    return;
                }

                ReplicationSectorRef sectorRef = Sectors[Start + i];
                ref Sector sector = ref sectorRef.Sector.Get();
                int count = CountBricks(ref sector, sectorRef.FullSector);

                var writer = new UnsafeNetWriter(BasePtr + Offsets[i], size);
                NetHeader.Write(ref writer, NetMessageType.BrickData, WorldId, Tick);
                writer.Write(new BrickBatchHeader
                {
                    Guid = sectorRef.Guid,
                    SectorPos = sectorRef.SectorPos,
                    BrickCount = (ushort)count,
                });

                if (sectorRef.FullSector != 0)
                {
                    SectorNonEmptyBrickEnumerator bricks = sector.EnumerateNonEmptyBricks();
                    while (bricks.MoveNext())
                    {
                        int brickIdx = bricks.Current.BrickAbs;
                        writer.Write((ushort)brickIdx);
                        writer.Write(sector.brickDirtyFlags[brickIdx]);
                        sector.WriteReplicatedBrick(ref writer, brickIdx, SlotMask);
                    }
                }
                else
                {
                    SectorDirtyBrickEnumerator bricks = sector.EnumerateDirtyBricks(Sector.ReplicationDirtyMask);
                    while (bricks.MoveNext())
                    {
                        int brickIdx = bricks.Current.BrickIdx;
                        writer.Write((ushort)brickIdx);
                        // The MATCHED flags, which is what WriteBrickBatch used to send.
                        writer.Write((ushort)bricks.Current.Flags);
                        sector.WriteReplicatedBrick(ref writer, brickIdx, SlotMask);
                    }
                }

                CheckExactFit(writer.Length, size);
            }

            [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private static void CheckExactFit(int written, int expected)
            {
                if (written != expected)
                {
                    throw new InvalidOperationException(
                        $"ReplicationBatch wrote {written} bytes into a {expected} byte range; the size job and the write job disagree.");
                }
            }
        }
    }
}
