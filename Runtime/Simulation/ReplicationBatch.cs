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
    /// sectors, into one byte buffer. Built once per world per tick for the delta and shared by
    /// every connection; built again on demand for the sectors a connection does not know yet.
    /// Owned and reused by <see cref="CaelixServer"/>.
    /// </summary>
    /// <remarks>
    /// Phase B (<see cref="ServerConnection.ReplicateWorld"/>) is the per-connection half: it diffs
    /// the connection's knowledge, sends the lifecycle messages, and forwards the slices this batch
    /// produced. Nothing here touches a connection, so the same bytes serve all of them.
    /// </remarks>
    public sealed unsafe class ReplicationBatch : IDisposable
    {
        private NativeList<ReplicationSectorRef> sectors;
        private NativeList<int> sizes;
        private NativeList<int> offsets;
        private NativeList<byte> bytes;
        private NativeList<ReplicationRange> ranges;
        private bool disposed;

        public ReplicationBatch()
        {
            sectors = new NativeList<ReplicationSectorRef>(64, Allocator.Persistent);
            sizes = new NativeList<int>(64, Allocator.Persistent);
            offsets = new NativeList<int>(64, Allocator.Persistent);
            bytes = new NativeList<byte>(64 * 1024, Allocator.Persistent);
            ranges = new NativeList<ReplicationRange>(64, Allocator.Persistent);
        }

        /// <summary>Number of packed ranges. Zero until <see cref="Build"/> ran.</summary>
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
        /// entity map; the packing itself is the job pair in <see cref="Build"/>.
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
        /// Runs the size job, the exclusive prefix sum, and the write job. Safe to call with zero
        /// sectors. After it returns, <see cref="Count"/> ranges describe the packed messages.
        /// </summary>
        public void Build(ushort worldId, uint tick, ushort slotMask)
        {
            ranges.Clear();
            int count = sectors.Length;
            if (count == 0)
            {
                bytes.Clear();
                return;
            }

            sizes.ResizeUninitialized(count);
            offsets.ResizeUninitialized(count);

            new SizeJob
            {
                Sectors = sectors.AsArray(),
                Sizes = sizes.AsArray(),
                SlotMask = slotMask,
            }.Schedule(count, 1).Complete();

            int total = 0;
            for (int i = 0; i < count; i++)
            {
                ReplicationSectorRef sectorRef = sectors[i];
                offsets[i] = total;
                ranges.Add(new ReplicationRange
                {
                    Guid = sectorRef.Guid,
                    SectorPos = sectorRef.SectorPos,
                    Offset = total,
                    Length = sizes[i],
                    FullSector = sectorRef.FullSector,
                });
                total += sizes[i];
            }

            bytes.ResizeUninitialized(total);
            if (total == 0)
            {
                return;
            }

            new WriteJob
            {
                Sectors = sectors.AsArray(),
                Sizes = sizes.AsArray(),
                Offsets = offsets.AsArray(),
                BasePtr = bytes.GetUnsafePtr(),
                WorldId = worldId,
                Tick = tick,
                SlotMask = slotMask,
            }.Schedule(count, 1).Complete();
        }

        /// <summary>The bytes of range <paramref name="i"/>. Valid until the next build.</summary>
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

            /// <summary>Base of the batch buffer, already sized. The job never resizes it.</summary>
            [NativeDisableUnsafePtrRestriction] public byte* BasePtr;

            public ushort WorldId;
            public uint Tick;
            public ushort SlotMask;

            public void Execute(int i)
            {
                int size = Sizes[i];
                if (size == 0)
                {
                    return;
                }

                ReplicationSectorRef sectorRef = Sectors[i];
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
