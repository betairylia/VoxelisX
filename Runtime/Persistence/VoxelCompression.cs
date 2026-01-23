using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Voxelis.Persistence
{
    /// <summary>
    /// Provides RLE (Run-Length Encoding) compression for voxel data.
    /// Uses "444111" layout: all block values first, then all run lengths.
    /// Run lengths are capped at 256 (stored as byte, where 0=1 block, 255=256 blocks).
    /// </summary>
    public static unsafe class VoxelCompression
    {
        /// <summary>
        /// Compresses a brick's block data using RLE compression.
        /// </summary>
        /// <param name="blocks">Pointer to 512 blocks to compress.</param>
        /// <param name="outBuffer">Output buffer for compressed data.</param>
        /// <param name="maxSize">Maximum size of output buffer.</param>
        /// <returns>Number of bytes written to outBuffer, or -1 if buffer too small.</returns>
        [BurstCompile]
        public static int CompressBrick(Block* blocks, byte* outBuffer, int maxSize)
        {
            if (blocks == null || outBuffer == null || maxSize < 6)
                return -1;

            // Temporary storage for RLE runs (worst case: 512 runs)
            const int maxRuns = Sector.BLOCKS_IN_BRICK;
            uint* values = stackalloc uint[maxRuns];
            byte* lengths = stackalloc byte[maxRuns];
            int runCount = 0;

            // Perform RLE encoding
            uint currentValue = blocks[0].data;
            int currentLength = 1;

            for (int i = 1; i < Sector.BLOCKS_IN_BRICK; i++)
            {
                uint blockValue = blocks[i].data;

                if (blockValue == currentValue && currentLength < 256)
                {
                    // Continue current run
                    currentLength++;
                }
                else
                {
                    // Save current run and start new one
                    values[runCount] = currentValue;
                    lengths[runCount] = (byte)(currentLength - 1); // Store as 0-255
                    runCount++;

                    currentValue = blockValue;
                    currentLength = 1;
                }
            }

            // Save final run
            values[runCount] = currentValue;
            lengths[runCount] = (byte)(currentLength - 1);
            runCount++;

            // Calculate required size
            int requiredSize = sizeof(ushort) + (runCount * sizeof(uint)) + (runCount * sizeof(byte));
            if (requiredSize > maxSize)
                return -1;

            // Write to output buffer
            int offset = 0;

            // Write run count
            *(ushort*)(outBuffer + offset) = (ushort)runCount;
            offset += sizeof(ushort);

            // Write all values (444...)
            for (int i = 0; i < runCount; i++)
            {
                *(uint*)(outBuffer + offset) = values[i];
                offset += sizeof(uint);
            }

            // Write all lengths (111...)
            for (int i = 0; i < runCount; i++)
            {
                outBuffer[offset] = lengths[i];
                offset++;
            }

            return offset;
        }

        /// <summary>
        /// Decompresses RLE data back into a brick's block array.
        /// </summary>
        /// <param name="buffer">Input buffer containing compressed RLE data.</param>
        /// <param name="bufferSize">Size of input buffer in bytes.</param>
        /// <param name="outBlocks">Output pointer for 512 decompressed blocks.</param>
        /// <returns>Number of blocks decompressed (should be 512), or -1 on error.</returns>
        [BurstCompile]
        public static int DecompressBrick(byte* buffer, int bufferSize, Block* outBlocks)
        {
            if (buffer == null || outBlocks == null || bufferSize < 2)
                return -1;

            int offset = 0;

            // Read run count
            ushort runCount = *(ushort*)(buffer + offset);
            offset += sizeof(ushort);

            // Validate buffer size
            int expectedSize = sizeof(ushort) + (runCount * sizeof(uint)) + (runCount * sizeof(byte));
            if (bufferSize < expectedSize)
                return -1;

            // Read all values
            uint* values = stackalloc uint[runCount];
            for (int i = 0; i < runCount; i++)
            {
                values[i] = *(uint*)(buffer + offset);
                offset += sizeof(uint);
            }

            // Read all lengths
            byte* lengths = stackalloc byte[runCount];
            for (int i = 0; i < runCount; i++)
            {
                lengths[i] = buffer[offset];
                offset++;
            }

            // Decompress into output
            int blockIndex = 0;
            for (int i = 0; i < runCount; i++)
            {
                uint value = values[i];
                int length = lengths[i] + 1; // Convert 0-255 to 1-256

                for (int j = 0; j < length; j++)
                {
                    if (blockIndex >= Sector.BLOCKS_IN_BRICK)
                        return -1; // Too many blocks

                    outBlocks[blockIndex].data = value;
                    blockIndex++;
                }
            }

            // Validate we got exactly 512 blocks
            if (blockIndex != Sector.BLOCKS_IN_BRICK)
                return -1;

            return blockIndex;
        }

        /// <summary>
        /// Estimates the compressed size for a brick without actually compressing.
        /// Useful for pre-allocating buffers.
        /// </summary>
        /// <param name="blocks">Pointer to 512 blocks.</param>
        /// <returns>Estimated compressed size in bytes (conservative upper bound).</returns>
        [BurstCompile]
        public static int EstimateBrickCompressedSize(Block* blocks)
        {
            if (blocks == null)
                return 0;

            // Quick RLE analysis
            int runCount = 1;
            uint currentValue = blocks[0].data;

            for (int i = 1; i < Sector.BLOCKS_IN_BRICK; i++)
            {
                if (blocks[i].data != currentValue)
                {
                    runCount++;
                    currentValue = blocks[i].data;
                }
            }

            // Account for run length cap of 256
            // In worst case, long runs get split, so add some padding
            runCount = math.min(runCount + 10, Sector.BLOCKS_IN_BRICK);

            return sizeof(ushort) + (runCount * sizeof(uint)) + (runCount * sizeof(byte));
        }

        /// <summary>
        /// Estimates the total compressed size for an entire sector.
        /// </summary>
        /// <param name="sector">Pointer to sector to estimate.</param>
        /// <returns>Estimated compressed size in bytes.</returns>
        [BurstCompile]
        public static int EstimateSectorCompressedSize(Sector* sector)
        {
            if (sector == null)
                return 0;

            int totalSize = SectorDataHeader.Size;

            // Add dirty flags arrays (always saved)
            totalSize += Sector.BRICKS_IN_SECTOR * sizeof(ushort);  // brickDirtyFlags
            totalSize += Sector.BRICKS_IN_SECTOR * sizeof(uint);    // brickDirtyDirectionMask

            // Add brick data
            int nonEmptyCount = sector->NonEmptyBrickCount;

            // Conservative estimate: average 100 bytes per brick
            // (assumes moderate compression, ~50% reduction from worst case)
            totalSize += nonEmptyCount * (sizeof(ushort) + 100);

            return totalSize;
        }

        /// <summary>
        /// Compresses an entire sector including all metadata and voxel data.
        /// </summary>
        /// <param name="sector">Pointer to sector to compress.</param>
        /// <param name="sectorPosition">Position of the sector.</param>
        /// <param name="outBuffer">Output buffer for compressed data.</param>
        /// <param name="maxSize">Maximum size of output buffer.</param>
        /// <returns>Number of bytes written, or -1 on error.</returns>
        [BurstCompile]
        public static int CompressSector(Sector* sector, int3 sectorPosition, byte* outBuffer, int maxSize)
        {
            if (sector == null || outBuffer == null || maxSize < SectorDataHeader.Size)
                return -1;

            int offset = 0;

            // Write sector header
            SectorDataHeader header = new SectorDataHeader
            {
                position = sectorPosition,
                nonEmptyBrickCount = (ushort)sector->NonEmptyBrickCount,
                sectorDirtyFlags = sector->sectorDirtyFlags,
                sectorNeighborsToCreate = sector->sectorNeighborsToCreate
            };

            *(SectorDataHeader*)(outBuffer + offset) = header;
            offset += SectorDataHeader.Size;

            // Write dirty flags arrays (entire arrays, always)
            int dirtyFlagsSize = Sector.BRICKS_IN_SECTOR * sizeof(ushort);
            if (offset + dirtyFlagsSize > maxSize)
                return -1;

            UnsafeUtility.MemCpy(outBuffer + offset, sector->brickDirtyFlags, dirtyFlagsSize);
            offset += dirtyFlagsSize;

            int dirtyMaskSize = Sector.BRICKS_IN_SECTOR * sizeof(uint);
            if (offset + dirtyMaskSize > maxSize)
                return -1;

            UnsafeUtility.MemCpy(outBuffer + offset, sector->brickDirtyDirectionMask, dirtyMaskSize);
            offset += dirtyMaskSize;

            // Write non-empty bricks
            for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
            {
                short relativeIdx = sector->brickIdx[i];
                if (relativeIdx == Sector.BRICKID_EMPTY)
                    continue;

                // Write absolute brick index
                if (offset + sizeof(ushort) > maxSize)
                    return -1;

                *(ushort*)(outBuffer + offset) = (ushort)i;
                offset += sizeof(ushort);

                // Compress brick data
                Block* brickData = sector->voxels.Ptr + (relativeIdx * Sector.BLOCKS_IN_BRICK);
                int compressedSize = CompressBrick(brickData, outBuffer + offset, maxSize - offset);

                if (compressedSize < 0)
                    return -1;

                offset += compressedSize;
            }

            return offset;
        }

        /// <summary>
        /// Decompresses a sector from compressed data.
        /// NOTE: This method requires the sector to be freshly allocated (all bricks empty).
        /// The caller is responsible for creating a new empty sector before calling this.
        /// </summary>
        /// <param name="buffer">Input buffer containing compressed sector data.</param>
        /// <param name="bufferSize">Size of input buffer.</param>
        /// <param name="outSector">Pointer to sector to decompress into (must be freshly allocated).</param>
        /// <returns>Number of bytes read from buffer, or -1 on error.</returns>
        public static int DecompressSector(byte* buffer, int bufferSize, Sector* outSector)
        {
            if (buffer == null || outSector == null || bufferSize < SectorDataHeader.Size)
                return -1;

            int offset = 0;

            // Read sector header
            SectorDataHeader header = *(SectorDataHeader*)(buffer + offset);
            offset += SectorDataHeader.Size;

            // Set sector metadata
            outSector->sectorDirtyFlags = header.sectorDirtyFlags;
            outSector->sectorNeighborsToCreate = header.sectorNeighborsToCreate;

            // Read dirty flags arrays
            int dirtyFlagsSize = Sector.BRICKS_IN_SECTOR * sizeof(ushort);
            if (offset + dirtyFlagsSize > bufferSize)
                return -1;

            UnsafeUtility.MemCpy(outSector->brickDirtyFlags, buffer + offset, dirtyFlagsSize);
            offset += dirtyFlagsSize;

            int dirtyMaskSize = Sector.BRICKS_IN_SECTOR * sizeof(uint);
            if (offset + dirtyMaskSize > bufferSize)
                return -1;

            UnsafeUtility.MemCpy(outSector->brickDirtyDirectionMask, buffer + offset, dirtyMaskSize);
            offset += dirtyMaskSize;

            // Read non-empty bricks
            // Note: We assume the sector is freshly allocated with all bricks empty
            for (int brickNum = 0; brickNum < header.nonEmptyBrickCount; brickNum++)
            {
                if (offset + sizeof(ushort) > bufferSize)
                    return -1;

                // Read absolute brick index
                ushort absoluteIdx = *(ushort*)(buffer + offset);
                offset += sizeof(ushort);

                if (absoluteIdx >= Sector.BRICKS_IN_SECTOR)
                    return -1;

                // Get current relative index for this brick
                short relativeIdx = outSector->brickIdx[absoluteIdx];

                // If brick not allocated, allocate it
                if (relativeIdx == Sector.BRICKID_EMPTY)
                {
                    // Allocate new brick - get current count and assign
                    relativeIdx = (short)outSector->NonEmptyBrickCount;
                    outSector->brickIdx[absoluteIdx] = relativeIdx;

                    // Expand voxel array to accommodate this brick
                    outSector->voxels.AddReplicate(Block.Empty, Sector.BLOCKS_IN_BRICK);
                }

                // Decompress brick data directly into the brick's location
                Block* brickData = outSector->voxels.Ptr + (relativeIdx * Sector.BLOCKS_IN_BRICK);
                int bytesRead = DecompressBrick(buffer + offset, bufferSize - offset, brickData);

                if (bytesRead < 0)
                    return -1;

                offset += bytesRead;
            }

            // Rebuild NonEmptyBricks list for physics/iteration optimization
            outSector->UpdateNonEmptyBricks();

            return offset;
        }
    }
}
