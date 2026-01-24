using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace VoxelisX.Core
{
    /// <summary>
    /// Unsafe Burst-compatible serialization API for Sector data with RLE compression
    /// </summary>
    public static unsafe class SectorSerialization
    {
        private const uint MAGIC_NUMBER = 0x564F5853; // "VOXS"
        private const byte VERSION = 1;
        private const int MAX_RUN_LENGTH = 256;

        /// <summary>
        /// Header structure for sector serialization
        /// </summary>
        private struct SectorHeader
        {
            public uint Magic;
            public byte Version;
            public ushort SectorDirtyFlags;
            public int CurrentBrickId;
        }

        /// <summary>
        /// Calculate the maximum size needed for serialized sector data
        /// </summary>
        [BurstCompile]
        public static int CalculateMaxSerializedSize(Sector* sector)
        {
            int size = 0;

            // Header
            size += sizeof(SectorHeader);

            // Dirty flags section (if any dirty flags exist)
            if (sector->sectorDirtyFlags != 0)
            {
                // brickDirtyFlags (ushort) + brickRequireUpdateFlags (ushort) + brickDirtyDirectionMask (uint)
                size += Sector.BRICKS_IN_SECTOR * (sizeof(ushort) + sizeof(ushort) + sizeof(uint));
            }

            // Brick index map
            size += Sector.BRICKS_IN_SECTOR * sizeof(short);

            // Voxel data (worst case: no compression, each block is a separate run)
            int allocatedBricks = *sector->currentBrickId;
            for (int i = 0; i < allocatedBricks; i++)
            {
                // runCount (ushort) + worst case: 512 runs of 1 block each
                size += sizeof(ushort);
                size += Sector.BLOCKS_IN_BRICK * sizeof(byte);  // run lengths
                size += Sector.BLOCKS_IN_BRICK * sizeof(uint);  // block data
            }

            return size;
        }

        /// <summary>
        /// Serialize a sector to a byte array using RLE compression
        /// Format: [Header][DirtyFlags?][BrickIndexMap][RLE VoxelData]
        /// </summary>
        [BurstCompile]
        public static int Serialize(Sector* sector, byte* destination, int maxSize)
        {
            byte* writePtr = destination;
            byte* endPtr = destination + maxSize;

            // Write header
            SectorHeader header = new SectorHeader
            {
                Magic = MAGIC_NUMBER,
                Version = VERSION,
                SectorDirtyFlags = sector->sectorDirtyFlags,
                CurrentBrickId = *sector->currentBrickId
            };

            if (writePtr + sizeof(SectorHeader) > endPtr)
                return -1; // Buffer overflow

            *(SectorHeader*)writePtr = header;
            writePtr += sizeof(SectorHeader);

            // Write dirty flags section (only if sector has dirty flags)
            if (sector->sectorDirtyFlags != 0)
            {
                int dirtyFlagsSize = Sector.BRICKS_IN_SECTOR * (sizeof(ushort) + sizeof(ushort) + sizeof(uint));
                if (writePtr + dirtyFlagsSize > endPtr)
                    return -1;

                for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                {
                    *(ushort*)writePtr = sector->brickDirtyFlags[i];
                    writePtr += sizeof(ushort);

                    *(ushort*)writePtr = sector->brickRequireUpdateFlags[i];
                    writePtr += sizeof(ushort);

                    *(uint*)writePtr = sector->brickDirtyDirectionMask[i];
                    writePtr += sizeof(uint);
                }
            }

            // Write brick index map
            int brickMapSize = Sector.BRICKS_IN_SECTOR * sizeof(short);
            if (writePtr + brickMapSize > endPtr)
                return -1;

            UnsafeUtility.MemCpy(writePtr, sector->brickIdx, brickMapSize);
            writePtr += brickMapSize;

            // Write RLE compressed voxel data for each allocated brick
            int allocatedBricks = *sector->currentBrickId;
            for (int brickId = 0; brickId < allocatedBricks; brickId++)
            {
                Block* brickData = (Block*)sector->voxels.Ptr + (brickId * Sector.BLOCKS_IN_BRICK);
                int bytesWritten = SerializeBrickRLE(brickData, writePtr, (int)(endPtr - writePtr));

                if (bytesWritten < 0)
                    return -1;

                writePtr += bytesWritten;
            }

            return (int)(writePtr - destination);
        }

        /// <summary>
        /// Serialize a single brick using RLE compression with 4444...1111... layout
        /// </summary>
        [BurstCompile]
        private static int SerializeBrickRLE(Block* brickData, byte* destination, int maxSize)
        {
            byte* writePtr = destination;
            byte* endPtr = destination + maxSize;

            // Temporary arrays for RLE encoding
            const int maxRuns = Sector.BLOCKS_IN_BRICK; // worst case
            byte* runLengths = stackalloc byte[maxRuns];
            uint* runValues = stackalloc uint[maxRuns];

            // Perform RLE encoding
            int runCount = 0;
            uint currentValue = brickData[0].data;
            int currentRunLength = 1;

            for (int i = 1; i < Sector.BLOCKS_IN_BRICK; i++)
            {
                uint value = brickData[i].data;

                if (value == currentValue && currentRunLength < MAX_RUN_LENGTH)
                {
                    currentRunLength++;
                }
                else
                {
                    // Store the run
                    runLengths[runCount] = (byte)currentRunLength;
                    runValues[runCount] = currentValue;
                    runCount++;

                    // Start new run
                    currentValue = value;
                    currentRunLength = 1;
                }
            }

            // Store the last run
            runLengths[runCount] = (byte)currentRunLength;
            runValues[runCount] = currentValue;
            runCount++;

            // Write run count
            if (writePtr + sizeof(ushort) > endPtr)
                return -1;

            *(ushort*)writePtr = (ushort)runCount;
            writePtr += sizeof(ushort);

            // Write all run lengths (4444... layout)
            if (writePtr + runCount * sizeof(byte) > endPtr)
                return -1;

            UnsafeUtility.MemCpy(writePtr, runLengths, runCount * sizeof(byte));
            writePtr += runCount * sizeof(byte);

            // Write all run values (1111... layout)
            if (writePtr + runCount * sizeof(uint) > endPtr)
                return -1;

            UnsafeUtility.MemCpy(writePtr, runValues, runCount * sizeof(uint));
            writePtr += runCount * sizeof(uint);

            return (int)(writePtr - destination);
        }

        /// <summary>
        /// Deserialize a sector from a byte array
        /// </summary>
        [BurstCompile]
        public static int Deserialize(byte* source, int sourceSize, Sector* sector)
        {
            byte* readPtr = source;
            byte* endPtr = source + sourceSize;

            // Read header
            if (readPtr + sizeof(SectorHeader) > endPtr)
                return -1;

            SectorHeader header = *(SectorHeader*)readPtr;
            readPtr += sizeof(SectorHeader);

            // Validate header
            if (header.Magic != MAGIC_NUMBER)
                return -1; // Invalid magic number

            if (header.Version != VERSION)
                return -1; // Unsupported version

            // Set sector dirty flags
            sector->sectorDirtyFlags = header.SectorDirtyFlags;

            // Read dirty flags section (if present)
            if (header.SectorDirtyFlags != 0)
            {
                int dirtyFlagsSize = Sector.BRICKS_IN_SECTOR * (sizeof(ushort) + sizeof(ushort) + sizeof(uint));
                if (readPtr + dirtyFlagsSize > endPtr)
                    return -1;

                for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                {
                    sector->brickDirtyFlags[i] = *(ushort*)readPtr;
                    readPtr += sizeof(ushort);

                    sector->brickRequireUpdateFlags[i] = *(ushort*)readPtr;
                    readPtr += sizeof(ushort);

                    sector->brickDirtyDirectionMask[i] = *(uint*)readPtr;
                    readPtr += sizeof(uint);
                }
            }

            // Read brick index map
            int brickMapSize = Sector.BRICKS_IN_SECTOR * sizeof(short);
            if (readPtr + brickMapSize > endPtr)
                return -1;

            UnsafeUtility.MemCpy(sector->brickIdx, readPtr, brickMapSize);
            readPtr += brickMapSize;

            // Set current brick ID
            *sector->currentBrickId = header.CurrentBrickId;

            // Ensure voxel list has enough capacity
            int requiredCapacity = header.CurrentBrickId * Sector.BLOCKS_IN_BRICK;
            if (sector->voxels.Capacity < requiredCapacity)
            {
                sector->voxels.Resize(requiredCapacity, NativeArrayOptions.UninitializedMemory);
            }
            sector->voxels.Length = requiredCapacity;

            // Read RLE compressed voxel data for each allocated brick
            for (int brickId = 0; brickId < header.CurrentBrickId; brickId++)
            {
                Block* brickData = (Block*)sector->voxels.Ptr + (brickId * Sector.BLOCKS_IN_BRICK);
                int bytesRead = DeserializeBrickRLE(readPtr, (int)(endPtr - readPtr), brickData);

                if (bytesRead < 0)
                    return -1;

                readPtr += bytesRead;
            }

            return (int)(readPtr - source);
        }

        /// <summary>
        /// Deserialize a single brick from RLE compressed data with 4444...1111... layout
        /// </summary>
        [BurstCompile]
        private static int DeserializeBrickRLE(byte* source, int maxSize, Block* brickData)
        {
            byte* readPtr = source;
            byte* endPtr = source + maxSize;

            // Read run count
            if (readPtr + sizeof(ushort) > endPtr)
                return -1;

            ushort runCount = *(ushort*)readPtr;
            readPtr += sizeof(ushort);

            // Read all run lengths
            if (readPtr + runCount * sizeof(byte) > endPtr)
                return -1;

            byte* runLengths = readPtr;
            readPtr += runCount * sizeof(byte);

            // Read all run values
            if (readPtr + runCount * sizeof(uint) > endPtr)
                return -1;

            uint* runValues = (uint*)readPtr;
            readPtr += runCount * sizeof(uint);

            // Decompress RLE data
            int blockIdx = 0;
            for (int runIdx = 0; runIdx < runCount; runIdx++)
            {
                byte runLength = runLengths[runIdx];
                uint runValue = runValues[runIdx];

                for (int i = 0; i < runLength; i++)
                {
                    if (blockIdx >= Sector.BLOCKS_IN_BRICK)
                        return -1; // Data corruption

                    brickData[blockIdx].data = runValue;
                    blockIdx++;
                }
            }

            // Verify we decoded exactly 512 blocks
            if (blockIdx != Sector.BLOCKS_IN_BRICK)
                return -1;

            return (int)(readPtr - source);
        }

        /// <summary>
        /// Serialize a sector to a NativeArray
        /// </summary>
        [BurstCompile]
        public static NativeArray<byte> SerializeToArray(Sector* sector, Allocator allocator)
        {
            int maxSize = CalculateMaxSerializedSize(sector);
            NativeArray<byte> buffer = new NativeArray<byte>(maxSize, allocator, NativeArrayOptions.UninitializedMemory);

            int actualSize = Serialize(sector, (byte*)buffer.GetUnsafePtr(), maxSize);

            if (actualSize < 0)
            {
                buffer.Dispose();
                return default;
            }

            // Resize to actual size
            if (actualSize < maxSize)
            {
                NativeArray<byte> trimmed = new NativeArray<byte>(actualSize, allocator, NativeArrayOptions.UninitializedMemory);
                UnsafeUtility.MemCpy(trimmed.GetUnsafePtr(), buffer.GetUnsafePtr(), actualSize);
                buffer.Dispose();
                return trimmed;
            }

            return buffer;
        }

        /// <summary>
        /// Deserialize a sector from a NativeArray
        /// </summary>
        [BurstCompile]
        public static bool DeserializeFromArray(NativeArray<byte> source, Sector* sector)
        {
            int bytesRead = Deserialize((byte*)source.GetUnsafeReadOnlyPtr(), source.Length, sector);
            return bytesRead > 0;
        }
    }
}
