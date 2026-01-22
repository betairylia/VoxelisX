using System;
using System.Collections.Generic;
using System.IO;
using Voxelis;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// RLE (Run-Length Encoding) compression for block data
    /// Compresses consecutive identical blocks into (count, block) pairs
    /// </summary>
    public static class RLECompression
    {
        /// <summary>
        /// Compress an array of blocks using RLE
        /// </summary>
        /// <param name="blocks">Block array to compress</param>
        /// <param name="count">Number of blocks to compress</param>
        /// <returns>List of RLE runs</returns>
        public static List<RLERun> CompressBlocks(Block* blocks, int count)
        {
            var runs = new List<RLERun>();

            if (count == 0)
                return runs;

            Block currentBlock = blocks[0];
            uint runLength = 1;

            for (int i = 1; i < count; i++)
            {
                if (blocks[i] == currentBlock)
                {
                    runLength++;
                }
                else
                {
                    runs.Add(new RLERun { count = runLength, block = currentBlock });
                    currentBlock = blocks[i];
                    runLength = 1;
                }
            }

            // Add the last run
            runs.Add(new RLERun { count = runLength, block = currentBlock });

            return runs;
        }

        /// <summary>
        /// Decompress RLE runs back to block array
        /// </summary>
        /// <param name="runs">RLE runs to decompress</param>
        /// <param name="output">Output block array</param>
        /// <param name="expectedCount">Expected number of blocks (for validation)</param>
        /// <returns>True if decompression successful</returns>
        public static bool DecompressBlocks(List<RLERun> runs, Block* output, int expectedCount)
        {
            int writePos = 0;

            foreach (var run in runs)
            {
                for (uint i = 0; i < run.count; i++)
                {
                    if (writePos >= expectedCount)
                        return false; // Buffer overflow

                    output[writePos++] = run.block;
                }
            }

            return writePos == expectedCount;
        }

        /// <summary>
        /// Write RLE compressed blocks to a stream
        /// Uses varint encoding for run lengths to save space
        /// </summary>
        public static void WriteRLE(BinaryWriter writer, Block* blocks, int count)
        {
            var runs = CompressBlocks(blocks, count);

            // Write number of runs
            WriteVarUInt(writer, (uint)runs.Count);

            // Write each run
            foreach (var run in runs)
            {
                WriteVarUInt(writer, run.count);
                writer.Write(run.block.data);
            }
        }

        /// <summary>
        /// Read RLE compressed blocks from a stream
        /// </summary>
        public static bool ReadRLE(BinaryReader reader, Block* output, int expectedCount)
        {
            uint runCount = ReadVarUInt(reader);
            var runs = new List<RLERun>((int)runCount);

            for (uint i = 0; i < runCount; i++)
            {
                uint count = ReadVarUInt(reader);
                uint blockData = reader.ReadUInt32();

                runs.Add(new RLERun
                {
                    count = count,
                    block = new Block { data = blockData }
                });
            }

            return DecompressBlocks(runs, output, expectedCount);
        }

        /// <summary>
        /// Write variable-length unsigned integer (varint encoding)
        /// Uses 7 bits per byte, with MSB indicating continuation
        /// </summary>
        public static void WriteVarUInt(BinaryWriter writer, uint value)
        {
            while (value >= 0x80)
            {
                writer.Write((byte)(value | 0x80));
                value >>= 7;
            }
            writer.Write((byte)value);
        }

        /// <summary>
        /// Read variable-length unsigned integer
        /// </summary>
        public static uint ReadVarUInt(BinaryReader reader)
        {
            uint value = 0;
            int shift = 0;

            while (true)
            {
                byte b = reader.ReadByte();
                value |= (uint)(b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                    break;

                shift += 7;

                if (shift >= 35) // Prevent overflow (max 5 bytes for uint32)
                    throw new InvalidDataException("Varint too long");
            }

            return value;
        }

        /// <summary>
        /// Calculate compressed size estimate (for allocation)
        /// </summary>
        public static int EstimateCompressedSize(Block* blocks, int count)
        {
            var runs = CompressBlocks(blocks, count);

            // Each run: varint count (1-5 bytes) + block data (4 bytes)
            // Worst case: 5 bytes per varint
            // Also add varint for run count
            return 5 + runs.Count * (5 + 4);
        }
    }
}
