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
    /// </summary>
    public static class SectorSerializer
    {
        private const uint Magic = 0x32535856u; // VXS2, little-endian

        public static unsafe byte[] Pack(in Sector sector)
        {
            using var rawMs = new MemoryStream();
            using (var bw = new BinaryWriter(rawMs, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                int capacity = sector.brickMap.Capacity;
                int count = sector.brickMap.Count;
                int freeCount = sector.brickMap.FreeCount;

                bw.Write(Magic);
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

                int slotRecordCount = 0;
                if (sector.slots.IsCreated)
                {
                    for (int i = 0; i < sector.slots.Length; i++)
                    {
                        if (sector.slots[i].IsCreated)
                        {
                            slotRecordCount++;
                        }
                    }
                }

                bw.Write((uint)slotRecordCount);

                if (sector.slots.IsCreated)
                {
                    for (int i = 0; i < sector.slots.Length; i++)
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
            using var compressedMs = new MemoryStream(compressed);
            using var deflate = new DeflateStream(compressedMs, CompressionMode.Decompress);
            using var rawMs = new MemoryStream();
            deflate.CopyTo(rawMs);
            rawMs.Position = 0;

            using var br = new BinaryReader(rawMs);

            uint magic = br.ReadUInt32();
            if (magic != Magic)
            {
                throw new InvalidDataException("Unsupported sector payload format.");
            }

            int capacity = (int)br.ReadUInt32();
            int count = (int)br.ReadUInt32();
            int freeCount = (int)br.ReadUInt32();

            // Allocate the sector with enough initial brick capacity to avoid a resize.
            int initialBricks = capacity > 0 ? capacity : 1;
            var sector = Sector.New(allocator, initialBricks, NativeArrayOptions.UninitializedMemory, createDefaultSlots: false);

            ReadRawBytes(br, sector.brickMap.indices, Sector.BRICKS_IN_SECTOR * sizeof(short));

            if (freeCount > 0)
            {
                ReadRawBytes(br, sector.brickMap.FreelistRaw, freeCount * sizeof(short));
            }

            sector.brickMap.RestoreSerializedState(capacity, count, freeCount);

            sector.sectorRequireUpdateFlags = br.ReadUInt16();
            ReadRawBytes(br, sector.brickRequireUpdateFlags, Sector.BRICKS_IN_SECTOR * sizeof(ushort));

            int slotRecordCount = (int)br.ReadUInt32();
            for (int i = 0; i < slotRecordCount; i++)
            {
                int slotId = br.ReadByte();
                int stride = br.ReadUInt16();

                var slot = SectorSlotStorage.New(stride, capacity, allocator);

                if (capacity > 0)
                {
                    ReadRawBytes(br, slot.data.Ptr, capacity * Sector.BLOCKS_IN_BRICK * stride);
                }

                *(sector.slots.Ptr + slotId) = slot;
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
    }
}
