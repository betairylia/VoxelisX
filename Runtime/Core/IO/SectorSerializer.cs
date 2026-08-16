using System;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Voxelis.IO
{
    /// <summary>
    /// Serializes a single <see cref="Sector"/> to/from a Deflate-compressed byte payload.
    /// The format is self-contained per sector — no inter-sector references — so streaming
    /// load/save of individual sectors needs no global state.
    ///
    /// Uncompressed payload layout (little-endian, matches <c>BinaryWriter</c> defaults):
    ///   u32   magic ('VXS2')
    ///   u32   brickMapCapacity
    ///   u32   brickMapCount       (sanity check; recomputable from indices)
    ///   u32   freeCount
    ///   i16   indices[Sector.BRICKS_IN_SECTOR]
    ///   i16   freelist[freeCount]
    ///   u16   sectorRequireUpdateFlags
    ///   u16   brickRequireUpdateFlags[Sector.BRICKS_IN_SECTOR]
    ///   u32   slotRecordCount
    ///   repeated slot records:
    ///     u8    slotId
    ///     u16   stride
    ///     u8    rawSlotData[brickMapCapacity * Sector.BLOCKS_IN_BRICK * stride]
    ///
    /// Only the per-voxel <see cref="SectorSlotStorage.data"/> buffer is persisted. A slot's
    /// optional <see cref="SectorSlotStorage.aux"/> buffer holds data derived from the voxels
    /// (occupancy bitmaps and the like); it is cheaper to recompute than to store, so it is left out
    /// of the payload and an unpacked sector comes back with every slot's aux uncreated, to be
    /// rebuilt for dirty bricks after load. PhysicsInfo voxel data is also derived. Old payloads can
    /// contain it, but unpacking discards that record so a changed runtime stride cannot corrupt
    /// memory. The first dirty physics tick rebuilds it from the authoritative Block slot.
    /// </summary>
    public static class SectorSerializer
    {
        private const uint SectorPayloadMagic = 0x32535856u; // VXS2, little-endian

        public static unsafe byte[] Pack(in Sector sector)
        {
            using var rawMs = new MemoryStream();
            using (var bw = new BinaryWriter(rawMs, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                int capacity = sector.brickMap.Capacity;
                int count = sector.brickMap.Count;
                int freeCount = sector.brickMap.FreeCount;

                bw.Write(SectorPayloadMagic);
                bw.Write((uint)capacity);
                bw.Write((uint)count);
                bw.Write((uint)freeCount);

                WriteRawBytes(bw, sector.brickMap.indices, Sector.BRICKS_IN_SECTOR * sizeof(short));

                if (freeCount > 0)
                {
                    WriteRawBytes(bw, sector.brickMap.FreelistRaw, freeCount * sizeof(short));
                }

                bw.Write(sector.sectorRequireUpdateFlags);
                WriteRawBytes(bw, sector.brickRequireUpdateFlags, Sector.BRICKS_IN_SECTOR * sizeof(ushort));

                // Only the per-voxel data buffer is written; the optional aux buffer is derived and
                // rebuilt on load. Both loops below must agree on which slots produce a record,
                // otherwise the written count desyncs from the stream.
                int slotRecordCount = 0;
                for (int i = 0; i < Sector.MAX_SLOTS; i++)
                {
                    if (sector.slots[i].IsCreated)
                    {
                        slotRecordCount++;
                    }
                }

                bw.Write((uint)slotRecordCount);

                for (int i = 0; i < Sector.MAX_SLOTS; i++)
                {
                    SectorSlotStorage slot = sector.slots[i];
                    if (!slot.IsCreated) continue;

                    bw.Write((byte)i);
                    bw.Write((ushort)slot.stride);

                    if (capacity > 0)
                    {
                        WriteRawBytes(bw, slot.data.Ptr, capacity * Sector.BLOCKS_IN_BRICK * slot.stride);
                    }
                }
            }

            rawMs.Position = 0;
            using var compressedMs = new MemoryStream();
            using (var deflate = new DeflateStream(compressedMs, CompressionLevel.Optimal, leaveOpen: true))
            {
                rawMs.CopyTo(deflate);
            }
            return compressedMs.ToArray();
        }

        public static unsafe Sector Unpack(byte[] compressed, Allocator allocator)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));

            using var compressedMs = new MemoryStream(compressed);
            using var deflate = new DeflateStream(compressedMs, CompressionMode.Decompress);
            using var rawMs = new MemoryStream();
            deflate.CopyTo(rawMs);
            rawMs.Position = 0;

            using var br = new BinaryReader(rawMs);

            uint magic = br.ReadUInt32();
            if (magic != SectorPayloadMagic)
            {
                throw new InvalidDataException("Unsupported sector payload format.");
            }

            uint rawCapacity = br.ReadUInt32();
            uint rawCount = br.ReadUInt32();
            uint rawFreeCount = br.ReadUInt32();
            if (rawCapacity > Sector.BRICKS_IN_SECTOR || rawCount > rawCapacity || rawFreeCount > rawCapacity || rawCount + rawFreeCount != rawCapacity)
                throw new InvalidDataException(
                    $"Invalid brick map counters: capacity={rawCapacity}, count={rawCount}, free={rawFreeCount}.");

            int capacity = (int)rawCapacity;
            int count = (int)rawCount;
            int freeCount = (int)rawFreeCount;

            // Allocate the sector with enough initial brick capacity to avoid a resize.
            int initialBricks = capacity > 0 ? capacity : 1;
            var sector = Sector.New(allocator, initialBricks, NativeArrayOptions.UninitializedMemory, createDefaultSlots: false);

            try
            {
                ReadRawBytes(br, sector.brickMap.indices, Sector.BRICKS_IN_SECTOR * sizeof(short));

                if (freeCount > 0)
                {
                    ReadRawBytes(br, sector.brickMap.FreelistRaw, freeCount * sizeof(short));
                }

                sector.brickMap.RestoreSerializedState(capacity, count, freeCount);

                sector.sectorRequireUpdateFlags = br.ReadUInt16();
                ReadRawBytes(br, sector.brickRequireUpdateFlags, Sector.BRICKS_IN_SECTOR * sizeof(ushort));

                uint rawSlotRecordCount = br.ReadUInt32();
                if (rawSlotRecordCount > Sector.MAX_SLOTS)
                    throw new InvalidDataException($"Invalid slot record count {rawSlotRecordCount}.");
                int slotRecordCount = (int)rawSlotRecordCount;

                for (int i = 0; i < slotRecordCount; i++)
                {
                    int slotId = br.ReadByte();
                    if (slotId >= Sector.MAX_SLOTS || sector.slots[slotId].IsCreated)
                        throw new InvalidDataException($"Invalid or duplicate slot id {slotId}.");

                    int stride = br.ReadUInt16();
                    long slotBytes = (long)capacity * Sector.BLOCKS_IN_BRICK * stride;
                    if (stride <= 0 || slotBytes > int.MaxValue)
                        throw new InvalidDataException($"Invalid slot stride {stride} for capacity {capacity}.");

                    // PhysicsInfo is a runtime cache derived from Block occupancy. Its byte layout can
                    // change without a save-format marker. Consume and discard every persisted record;
                    // load dirty flags rebuild the current format before narrowphase uses it.
                    if (slotId == (int)SectorSlotId.PhysicsInfo)
                    {
                        SkipRawBytes(br, (int)slotBytes);
                        continue;
                    }

                    var slot = SectorSlotStorage.New(stride, capacity, allocator);
                    SectorSlotStorage* slotPtr = sector.slots + slotId;
                    *slotPtr = slot;

                    if (slotBytes > 0)
                    {
                        ReadRawBytes(br, slotPtr->data.Ptr, (int)slotBytes);
                    }
                }

                // Dirty/runtime-only buffers stay zero-initialized — Sector.New already cleared them
                // when ClearMemory was requested. With UninitializedMemory above, zero them explicitly.
                int totalBricks = Sector.BRICKS_IN_SECTOR;
                UnsafeUtility.MemClear(sector.brickDirtyFlags, totalBricks * sizeof(ushort));
                UnsafeUtility.MemClear(sector.brickDirtyDirectionMask, totalBricks * sizeof(uint));
                sector.sectorDirtyFlags = 0;
                sector.sectorNeighborsToCreate = 0;

                return sector;
            }
            catch
            {
                sector.Dispose(allocator);
                throw;
            }
        }

        private static unsafe void WriteRawBytes(BinaryWriter bw, void* src, int byteCount)
        {
            if (byteCount <= 0) return;
            byte[] buffer = new byte[byteCount];
            fixed (byte* p = buffer) UnsafeUtility.MemCpy(p, src, byteCount);
            bw.Write(buffer);
        }

        private static unsafe void ReadRawBytes(BinaryReader br, void* dst, int byteCount)
        {
            if (byteCount <= 0) return;
            byte[] buffer = br.ReadBytes(byteCount);
            if (buffer.Length != byteCount)
            {
                throw new EndOfStreamException(
                    $"Sector payload truncated: expected {byteCount} bytes, got {buffer.Length}.");
            }
            fixed (byte* p = buffer) UnsafeUtility.MemCpy(dst, p, byteCount);
        }

        private static void SkipRawBytes(BinaryReader br, int byteCount)
        {
            if (byteCount <= 0) return;

            long nextPosition = br.BaseStream.Position + byteCount;
            if (nextPosition > br.BaseStream.Length)
            {
                throw new EndOfStreamException(
                    $"Sector payload truncated while skipping {byteCount} derived-slot bytes.");
            }
            br.BaseStream.Position = nextPosition;
        }
    }
}
