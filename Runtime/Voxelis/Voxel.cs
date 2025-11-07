using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using Voxelis.Rendering;

namespace Voxelis
{
    /// <summary>
    /// Represents a single voxel block with encoded color and material data.
    /// Uses a 32-bit packed format for efficient storage and rendering.
    /// </summary>
    /// <remarks>
    /// The block data is encoded as follows:
    /// - Bits 16-31: Block ID (includes RGB color and emission flag)
    /// - Bits 0-15: Metadata
    /// The block ID encodes RGB555 color (5 bits per channel) plus 1 emission bit.
    /// </remarks>
    public struct Block : IEquatable<Block>
    {
        /// <summary>
        /// The packed 32-bit data representing this block.
        /// </summary>
        public uint data;

        private const uint IDMask    = 0xFFFF0000;
        private const  int IDShift   = 16;
        private const uint PhaseMask = 0xC0000000; // 00 - Gas; 01 - Liquid; 10 - Powder; 11 - Solid
        private const uint PhaseShift= 30;
        private const uint MetaMask  = 0x0000FFFF;
        private const  int MetaShift = 0;

        /// <summary>
        /// Gets the block ID portion of the packed data.
        /// </summary>
        public ushort id => (ushort)((data & IDMask) >> IDShift);

        /// <summary>
        /// Gets the metadata portion of the packed data.
        /// </summary>
        public ushort meta => (ushort)((data & MetaMask) >> MetaShift);

        /// <summary>
        /// Returns true if this block is empty (all data is zero).
        /// </summary>
        public bool isEmpty => (data == 0);
        // public bool isVoid => (data == 0);

        /// <summary>
        /// Constructs a block with the specified block ID.
        /// </summary>
        /// <param name="id">The block ID to use.</param>
        public Block(ushort id)
        {
            data = (((uint)id) << IDShift) + 0;
        }

        /// <summary>
        /// Constructs a block with RGB color values (0-31 range) and emission flag.
        /// </summary>
        /// <param name="r">Red component (0-31).</param>
        /// <param name="g">Green component (0-31).</param>
        /// <param name="b">Blue component (0-31).</param>
        /// <param name="emission">Whether this block emits light.</param>
        public Block(int r, int g, int b, bool emission)
        {
            int id = (r << 11) | (g << 6) | (b << 1) | (emission ? 1 : 0);
            data = ((uint)id << IDShift);
        }

        /// <summary>
        /// Constructs a block with normalized RGB color values (0-1 range) and emission value.
        /// </summary>
        /// <param name="r">Red component (0-1), will be quantized to 5 bits.</param>
        /// <param name="g">Green component (0-1), will be quantized to 5 bits.</param>
        /// <param name="b">Blue component (0-1), will be quantized to 5 bits.</param>
        /// <param name="emission">Emission value; any value > 0 enables emission.</param>
        public Block(float r, float g, float b, float emission)
        {
            int rr = (int)math.floor(r * 32.0f);
            int gg = (int)math.floor(g * 32.0f);
            int bb = (int)math.floor(b * 32.0f);
            bool emi = emission > 0;

            int id = (rr << 11) | (gg << 6) | (bb << 1) | (emi ? 1 : 0);
            data = ((uint)id << IDShift);
        }

        /// <summary>
        /// Represents an empty/air block with no data.
        /// </summary>
        public static readonly Block Empty = new Block() { data = 0 };

        public static bool operator == (Block a, Block b) => a.data == b.data;
        public static bool operator != (Block a, Block b) => a.data != b.data;

        public bool Equals(Block other)
        {
            return data == other.data;
        }

        public override int GetHashCode()
        {
            return (int)data;
        }
    }

    /// <summary>
    /// Iterator structure for enumerating blocks within a sector along with their positions.
    /// </summary>
    public struct BlockIterator
    {
        /// <summary>
        /// The block at the current iterator position.
        /// </summary>
        public Block block;

        /// <summary>
        /// The 3D position of the block within the sector.
        /// </summary>
        public int3 position;
    }

    /// <summary>
    /// Information about a brick update for renderer synchronization.
    /// Tracks whether a brick was added, removed, or modified.
    /// </summary>
    public struct BrickUpdateInfo
    {
        /// <summary>
        /// The type of update that occurred to a brick.
        /// </summary>
        public enum Type
        {
            /// <summary>No update pending.</summary>
            Idle = 0,
            /// <summary>Brick was newly added.</summary>
            Added,
            /// <summary>Brick was removed.</summary>
            Removed,
            /// <summary>Brick contents were modified.</summary>
            Modified,
        }

        /// <summary>
        /// The type of update for this brick.
        /// </summary>
        public Type type;

        /// <summary>
        /// The relative brick index within the sector's voxel data.
        /// </summary>
        public short brickIdx;

        /// <summary>
        /// The absolute brick index in the sector's 3D grid.
        /// </summary>
        public short brickIdxAbsolute;
    }

    /// <summary>
    /// Represents a 3D sector of voxel data organized into bricks.
    /// A sector contains a 16x16x16 grid of bricks, where each brick is 8x8x8 blocks.
    /// Total sector size is 128x128x128 blocks.
    /// </summary>
    /// <remarks>
    /// The sector uses a sparse voxel octree-like structure where empty bricks are not allocated.
    /// Only bricks containing non-empty blocks consume memory. This allows for efficient storage
    /// of large voxel worlds with sparse geometry.
    /// </remarks>
    public struct Sector : IDisposable
    {
        // Brick-level constants (isometric: uniform across all axes)
        /// <summary>Bit shift for brick size (brick is 8 blocks per axis).</summary>
        public const int SHIFT_IN_BLOCKS = 3;
        /// <summary>Size of a brick in blocks (8 blocks per axis).</summary>
        public const int SIZE_IN_BLOCKS = (1 << SHIFT_IN_BLOCKS);
        /// <summary>Bitmask for extracting block position within brick (0-7).</summary>
        public const int BRICK_MASK = SIZE_IN_BLOCKS - 1;
        /// <summary>Squared size for indexing calculations (8 * 8 = 64).</summary>
        public const int SIZE_IN_BLOCKS_SQUARED = SIZE_IN_BLOCKS * SIZE_IN_BLOCKS;
        /// <summary>Total number of blocks in a single brick (8x8x8 = 512).</summary>
        public const int BLOCKS_IN_BRICK = SIZE_IN_BLOCKS * SIZE_IN_BLOCKS * SIZE_IN_BLOCKS;

        // Sector-level constants (isometric: uniform across all axes)
        /// <summary>Bit shift for sector size in bricks (16 bricks per axis).</summary>
        public const int SHIFT_IN_BRICKS = 4;
        /// <summary>Number of bricks in a sector per axis (16 bricks = 128 blocks).</summary>
        public const int SIZE_IN_BRICKS = (1 << SHIFT_IN_BRICKS);
        /// <summary>Bitmask for extracting brick position within sector (0-15).</summary>
        public const int SECTOR_MASK = SIZE_IN_BRICKS - 1;
        /// <summary>Squared size for indexing calculations (16 * 16 = 256).</summary>
        public const int SIZE_IN_BRICKS_SQUARED = SIZE_IN_BRICKS * SIZE_IN_BRICKS;
        /// <summary>Size of a sector in blocks (128 blocks per axis).</summary>
        public const int SECTOR_SIZE_IN_BLOCKS = SIZE_IN_BRICKS << SHIFT_IN_BLOCKS;

        /// <summary>Special value indicating an empty/unallocated brick.</summary>
        public const short BRICKID_EMPTY = -1;

        /// <summary>
        /// Flattened array of all block data for non-empty bricks.
        /// Bricks are stored contiguously, with each brick containing BLOCKS_IN_BRICK elements.
        /// </summary>
        public NativeList<Block> voxels;

        /// <summary>
        /// Maps absolute brick indices (position in 3D grid) to relative brick indices (position in voxels array).
        /// Contains BRICKID_EMPTY for bricks that have not been allocated.
        /// </summary>
        public NativeArray<short> brickIdx;

        /// <summary>
        /// Tracks the update state of each brick for renderer synchronization.
        /// Indexed by absolute brick ID.
        /// </summary>
        public NativeArray<BrickUpdateInfo.Type> brickFlags;

        /// <summary>
        /// List containing absolute IDs of bricks that have been modified and need rendering updates.
        /// For renderer, updater, physics etc. use.
        /// </summary>
        internal NativeList<short> updateRecord;

        /// <summary>
        /// Returns true if there are pending brick updates for the renderer.
        /// </summary>
        internal bool IsRendererDirty => !updateRecord.IsEmpty;

        /// <summary>
        /// Returns true if the sector contains no allocated bricks.
        /// </summary>
        internal bool IsRendererEmpty => RendererNonEmptyBrickCount == 0;

        /// <summary>
        /// Single-element array storing the count of allocated bricks.
        /// Using an array allows modification within burst-compiled jobs.
        /// </summary>
        private NativeArray<int> currentBrickId;

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector for rendering.
        /// </summary>
        public int RendererNonEmptyBrickCount => currentBrickId[0];

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector.
        /// </summary>
        public int NonEmptyBrickCount => currentBrickId[0];

        /// <summary>
        /// Gets the approximate host memory usage of this sector in bytes.
        /// </summary>
        public int MemoryUsage => voxels.Capacity * UnsafeUtility.SizeOf(typeof(Block));

        [BurstCompile]
        struct InitBrickIdJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<short> id;
            public void Execute(int index)
            {
                id[index] = BRICKID_EMPTY;
            }
        }

        /// <summary>
        /// Creates a new sector with the specified allocator and initial brick capacity.
        /// </summary>
        /// <param name="allocator">The memory allocator to use for native collections.</param>
        /// <param name="initialBricks">Initial capacity for brick storage (default: 1).</param>
        /// <param name="options">Memory initialization options (default: ClearMemory).</param>
        /// <returns>A newly initialized sector.</returns>
        public static Sector New(
            Allocator allocator,
            int initialBricks = 1,
            NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            Sector s = new Sector()
            {
                voxels = new NativeList<Block>(
                    initialBricks * BLOCKS_IN_BRICK, allocator),
                brickIdx = new NativeArray<short>(
                    SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS,
                    allocator,
                    options),
                brickFlags = new NativeArray<BrickUpdateInfo.Type>(
                    SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS, allocator, options),
                updateRecord = new NativeList<short>(allocator),
                currentBrickId = new(1, allocator),
                
                nonEmptyBricks = new NativeList<short>(allocator),
            };

            var initJob = new InitBrickIdJob() { id = s.brickIdx };
            initJob.Schedule(s.brickIdx.Length, s.brickIdx.Length).Complete();
            
            return s;
        }
        
        public static Sector CloneNoRecord(
            Sector from,
            Allocator allocator)
        {
            Sector s = new Sector()
            {
                voxels = new NativeList<Block>(
                    from.NonEmptyBrickCount * BLOCKS_IN_BRICK, allocator),
                brickIdx = new NativeArray<short>(
                    SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS,
                    allocator),
                brickFlags = new NativeArray<BrickUpdateInfo.Type>(
                    SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS, allocator),
                updateRecord = new NativeList<short>(allocator),
                currentBrickId = new(1, allocator),
                
                nonEmptyBricks = new NativeList<short>(allocator),
            };
            
            s.voxels.AddRange(from.voxels.AsArray());
            s.brickIdx.CopyFrom(from.brickIdx);
            s.currentBrickId.CopyFrom(from.currentBrickId);
            
            return s;
        }

        /// <summary>
        /// Disposes all native collections used by this sector, releasing unmanaged memory.
        /// </summary>
        public void Dispose()
        {
            if (voxels.IsCreated) voxels.Dispose();
            if (brickIdx.IsCreated) brickIdx.Dispose();
            if (brickFlags.IsCreated) brickFlags.Dispose();
            if (updateRecord.IsCreated) updateRecord.Dispose();
            if (currentBrickId.IsCreated) currentBrickId.Dispose();
            if (nonEmptyBricks.IsCreated) nonEmptyBricks.Dispose();
        }

        /// <summary>
        /// Converts 3D brick coordinates to a flat brick index within the sector.
        /// </summary>
        /// <param name="x">Brick X coordinate (0-15).</param>
        /// <param name="y">Brick Y coordinate (0-15).</param>
        /// <param name="z">Brick Z coordinate (0-15).</param>
        /// <returns>The flat absolute brick index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBrickIdx(int x, int y, int z)
        {
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(x < SIZE_IN_BRICKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(y < SIZE_IN_BRICKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(z < SIZE_IN_BRICKS);

            return (x + y * SIZE_IN_BRICKS + z * SIZE_IN_BRICKS_SQUARED);
        }

        /// <summary>
        /// Converts a flat absolute brick index back to 3D brick coordinates.
        /// </summary>
        /// <param name="bidAbsolute">The absolute brick index.</param>
        /// <returns>The 3D brick position within the sector.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3Int ToBrickPos(short bidAbsolute)
        {
            return new Vector3Int(
                bidAbsolute & SECTOR_MASK,
                (bidAbsolute >> SHIFT_IN_BRICKS) & SECTOR_MASK,
                (bidAbsolute >> (SHIFT_IN_BRICKS << 1)));  // >> (SHIFT_IN_BRICKS * 2)
        }

        /// <summary>
        /// Converts 3D block coordinates within a brick to a flat block index.
        /// </summary>
        /// <param name="x">Block X coordinate within brick (0-7).</param>
        /// <param name="y">Block Y coordinate within brick (0-7).</param>
        /// <param name="z">Block Z coordinate within brick (0-7).</param>
        /// <returns>The flat block index within the brick (0-511).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBlockIdx(int x, int y, int z)
        {
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(x < SIZE_IN_BLOCKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(y < SIZE_IN_BLOCKS);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(z < SIZE_IN_BLOCKS);

            return (x + y * SIZE_IN_BLOCKS + z * SIZE_IN_BLOCKS_SQUARED);
        }

        /// <summary>
        /// Gets a slice of the voxel array representing a specific brick.
        /// </summary>
        /// <param name="x">Brick X coordinate.</param>
        /// <param name="y">Brick Y coordinate.</param>
        /// <param name="z">Brick Z coordinate.</param>
        /// <returns>A NativeSlice containing the brick's blocks, or null if the brick is empty.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeSlice<Block>? GetBrick(int x, int y, int z)
        {
            short bid = this.brickIdx[ToBrickIdx(x, y, z)];
            return GetBrick(bid);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeSlice<Block>? GetBrick(short bid)
        {
            if (bid == BRICKID_EMPTY)
            {
                return null;
            }
            return new NativeSlice<Block>(voxels, bid * BLOCKS_IN_BRICK);
        }

        /// <summary>
        /// Gets the block at the specified position within this sector.
        /// </summary>
        /// <param name="x">Block X coordinate within sector (0-127).</param>
        /// <param name="y">Block Y coordinate within sector (0-127).</param>
        /// <param name="z">Block Z coordinate within sector (0-127).</param>
        /// <returns>The block at the specified position, or Block.Empty if the brick is not allocated.</returns>
        /// <remarks>TODO: Per-brick thread safety, pre-alloc etc.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
        {
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = this.brickIdx[brick_sector_index_id];

            if (bid == BRICKID_EMPTY)
            {
                return Block.Empty;
            }

            return voxels[
                bid * BLOCKS_IN_BRICK
                + ToBlockIdx(
                    x & BRICK_MASK,
                    y & BRICK_MASK,
                    z & BRICK_MASK)
            ];
        }

        /// <summary>
        /// Sets the block at the specified position within this sector.
        /// Automatically allocates a new brick if needed.
        /// </summary>
        /// <param name="x">Block X coordinate within sector (0-127).</param>
        /// <param name="y">Block Y coordinate within sector (0-127).</param>
        /// <param name="z">Block Z coordinate within sector (0-127).</param>
        /// <param name="b">The block data to set.</param>
        /// <remarks>
        /// If the target brick is not allocated and the block is non-empty, a new brick will be created.
        /// Setting an empty block to an empty brick is a no-op to avoid unnecessary allocations.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block b)
        {
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = this.brickIdx[brick_sector_index_id];

            // Skip setting empty to empty bricks
            if (bid == BRICKID_EMPTY && b.isEmpty)
            {
                return;
            }

            // To put non-empty block to empty brick,
            // Create the brick first
            if (bid == BRICKID_EMPTY)
            {
                this.brickIdx[brick_sector_index_id] = (short)currentBrickId[0];
                bid = (short)currentBrickId[0];
                voxels.AddReplicate(Block.Empty, BLOCKS_IN_BRICK);

                brickFlags[brick_sector_index_id] = BrickUpdateInfo.Type.Added;
                updateRecord.Add((short)brick_sector_index_id);
                
                currentBrickId[0]++;

                isNonEmptyBricksDirty = true;
            }

            // Set the block in target brick
            voxels[
                bid * BLOCKS_IN_BRICK
                + ToBlockIdx(
                    x & BRICK_MASK,
                    y & BRICK_MASK,
                    z & BRICK_MASK)
            ] = b;

            if (brickFlags[brick_sector_index_id] == BrickUpdateInfo.Type.Idle)
            {
                brickFlags[brick_sector_index_id] = BrickUpdateInfo.Type.Modified;
                updateRecord.Add((short)brick_sector_index_id);
            }
        }

        // This will dequeue next dirty brick and mark it undirty!
        // public BrickUpdateInfo? ProcessNextDirtyBrick()
        // {
        //     if (rendererUpdateRecord.IsEmpty()) return null;
        //     
        //     short absolute_bid = rendererUpdateRecord.Dequeue();
        //
        //     var result = new BrickUpdateInfo()
        //     {
        //         brickIdx = brickIdx[absolute_bid],
        //         brickIdxAbsolute = absolute_bid,
        //         type = brickFlags[absolute_bid]
        //     };
        //
        //     brickFlags[absolute_bid] = BrickUpdateInfo.Type.Idle;
        //
        //     return result;
        // }

        public void ReorderBricks()
        {
            // Do nothing now
            // TODO: Implement proper logic
            return;
        }

        /// <summary>
        /// Clean-up dirty-related data in sector.
        /// This is necessary since multiple systems may consume the dirty data for update.
        /// In-place consumption of dirtyness will only allow one system to use.
        /// </summary>
        public void EndTick()
        {
            // TODO: Burst
            // TODO: Handle delete brick properly
            foreach (var record in updateRecord)
            {
                brickFlags[record] = BrickUpdateInfo.Type.Idle;
            }
            
            updateRecord.Clear();
        }
        
        #region PHYSICS

        ///////////////////////////////////
        /// PHYSICS
        ///////////////////////////////////

        // Accelerators
        private bool isNonEmptyBricksDirty;

        private NativeList<short> nonEmptyBricks;

        // internal NativeArray<uint> corners;
        // internal NativeArray<uint> edges;

        [BurstCompile]
        public NativeList<short> GetNonEmptyBricks()
        {
            if (isNonEmptyBricksDirty)
            {
                nonEmptyBricks.Clear();
                for (short i = 0; i < SIZE_IN_BRICKS * SIZE_IN_BRICKS * SIZE_IN_BRICKS; i++)
                {
                    if (brickIdx[i] != BRICKID_EMPTY)
                    {
                        nonEmptyBricks.Add(i);
                    }
                }

                isNonEmptyBricksDirty = false;
            }
            
            return nonEmptyBricks;
        }

        #endregion
    }

    /// <summary>
    /// Reference wrapper for a sector that includes rendering and entity association.
    /// Manages the lifecycle and rendering state of a sector within a voxel entity.
    /// </summary>
    public class SectorRef : IDisposable
    {
        /// <summary>
        /// The underlying sector data containing voxel blocks.
        /// </summary>
        public Sector sector;

        /// <summary>
        /// Indicates whether this sector reference has pending changes (currently unused).
        /// </summary>
        public bool isDirty;

        /// <summary>
        /// Position of this sector in sector coordinates (each unit = 128 blocks).
        /// </summary>
        public Vector3Int sectorPos;

        /// <summary>
        /// Position of this sector in block coordinates.
        /// Converts sector position to block space (sector * 128).
        /// </summary>
        /// <remarks>FIXME: WARNING: The multiplier 128 is hardcoded and should use Sector constants.</remarks>
        public Vector3Int sectorBlockPos => sectorPos * 128;

        /// <summary>
        /// The renderer responsible for preparing and submitting this sector's data to the GPU.
        /// Null if this sector is not rendered (e.g., server-side or headless mode).
        /// </summary>
        public SectorRenderer renderer;

        /// <summary>
        /// The voxel entity that owns this sector.
        /// </summary>
        public VoxelEntity entity;

        /// <summary>
        /// Gets the total host memory usage of this sector, including renderer buffers.
        /// </summary>
        public ulong MemoryUsage => (ulong)sector.MemoryUsage + (ulong)(renderer?.MemoryUsage ?? 0);

        /// <summary>
        /// Gets the total GPU memory (VRAM) usage of this sector's rendering resources.
        /// </summary>
        public ulong VRAMUsage => (ulong)(renderer?.VRAMUsage ?? 0);

        /// <summary>
        /// Constructs a new sector reference.
        /// </summary>
        /// <param name="entity">The owning voxel entity.</param>
        /// <param name="sec">The sector data structure.</param>
        /// <param name="pos">Position of the sector in sector coordinates.</param>
        /// <param name="hasRenderer">Whether to create a renderer for this sector (default: true).</param>
        public SectorRef(VoxelEntity entity, Sector sec, Vector3Int pos, bool hasRenderer = true)
        {
            this.entity = entity;
            sector = sec;
            sectorPos = pos;

            if (hasRenderer)
            {
                renderer = new SectorRenderer(this);
            }
        }

        /// <summary>
        /// Performs per-frame update for this sector.
        /// Reorders bricks and finalizes the update cycle.
        /// </summary>
        /// <remarks>TODO: Call renderer RenderEmitJob and Render here with proper parallelization.</remarks>
        public void Tick()
        {
            sector.ReorderBricks();
            // TODO: Call renderer RenderEmitJob and Render here with proper parallelization
            sector.EndTick();
        }

        /// <summary>
        /// Marks this sector for removal. Disposes the sector data and marks the renderer for removal.
        /// </summary>
        public void Remove()
        {
            sector.Dispose();
            renderer?.MarkRemove();
        }

        /// <summary>
        /// Disposes this sector reference, releasing all resources including sector data and renderer.
        /// </summary>
        public void Dispose()
        {
            sector.Dispose();
            renderer?.Dispose();
        }
    }
}