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
    ///   u32   brickMapCapacity
    ///   u32   brickMapCount       (sanity check; recomputable from indices)
    ///   u32   freeCount
    ///   i16   indices[Sector.BRICKS_IN_SECTOR]
    ///   i16   freelist[freeCount]
    ///   u16   sectorRequireUpdateFlags
    ///   u16   brickRequireUpdateFlags[Sector.BRICKS_IN_SECTOR]
    ///   u32   voxels[brickMapCapacity * Sector.BLOCKS_IN_BRICK]
    /// </summary>
    public static class SectorSerializer
    {
        public static unsafe byte[] Pack(in Sector sector)
        {
            using var rawMs = new MemoryStream();
            using (var bw = new BinaryWriter(rawMs, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                int capacity = sector.brickMap.Capacity;
                int count = sector.brickMap.Count;
                int freeCount = sector.brickMap.FreeCount;

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

                int voxelCount = capacity * Sector.BLOCKS_IN_BRICK;
                if (voxelCount > 0)
                {
                    WriteRawBytes(bw, sector.voxels.Ptr, voxelCount * sizeof(uint));
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

            int capacity = (int)br.ReadUInt32();
            int count = (int)br.ReadUInt32();
            int freeCount = (int)br.ReadUInt32();

            // Allocate the sector with enough initial brick capacity to avoid a resize.
            int initialBricks = capacity > 0 ? capacity : 1;
            var sector = Sector.New(allocator, initialBricks, NativeArrayOptions.UninitializedMemory);

            ReadRawBytes(br, sector.brickMap.indices, Sector.BRICKS_IN_SECTOR * sizeof(short));

            if (freeCount > 0)
            {
                ReadRawBytes(br, sector.brickMap.FreelistRaw, freeCount * sizeof(short));
            }

            sector.brickMap.RestoreSerializedState(capacity, count, freeCount);

            sector.sectorRequireUpdateFlags = br.ReadUInt16();
            ReadRawBytes(br, sector.brickRequireUpdateFlags, Sector.BRICKS_IN_SECTOR * sizeof(ushort));

            int voxelCount = capacity * Sector.BLOCKS_IN_BRICK;
            if (voxelCount > 0)
            {
                sector.voxels.Resize(voxelCount, NativeArrayOptions.UninitializedMemory);
                ReadRawBytes(br, sector.voxels.Ptr, voxelCount * sizeof(uint));
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
