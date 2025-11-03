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
    public struct Block : IEquatable<Block>
    {
        public uint data;

        private const uint IDMask    = 0xFFFF0000;
        private const  int IDShift   = 16;
        private const uint PhaseMask = 0xC0000000; // 00 - Gas; 01 - Liquid; 10 - Powder; 11 - Solid
        private const uint PhaseShift= 30;
        private const uint MetaMask  = 0x0000FFFF;
        private const  int MetaShift = 0;

        public ushort id => (ushort)((data & IDMask) >> IDShift);
        public ushort meta => (ushort)((data & MetaMask) >> MetaShift);
        public bool isEmpty => (data == 0);
        // public bool isVoid => (data == 0);

        public Block(ushort id)
        {
            data = (((uint)id) << IDShift) + 0;
        }

        public Block(int r, int g, int b, bool emission)
        {
            int id = (r << 11) | (g << 6) | (b << 1) | (emission ? 1 : 0);
            data = ((uint)id << IDShift);
        }

        public Block(float r, float g, float b, float emission)
        {
            int rr = (int)math.floor(r * 32.0f);
            int gg = (int)math.floor(g * 32.0f);
            int bb = (int)math.floor(b * 32.0f);
            bool emi = emission > 0;
            
            int id = (rr << 11) | (gg << 6) | (bb << 1) | (emi ? 1 : 0);
            data = ((uint)id << IDShift);
        }

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

    public struct BlockIterator
    {
        public Block block;
        public int3 position;
    }

    public struct BrickUpdateInfo
    {
        public enum Type
        {
            Idle = 0,
            Added,
            Removed,
            Modified,
        }

        public Type type;
        public short brickIdx;
        public short brickIdxAbsolute;
    }

    public struct Sector : IDisposable
    {
        public const int SHIFT_IN_BLOCKS_X = 3;
        public const int SHIFT_IN_BLOCKS_Y = 3;
        public const int SHIFT_IN_BLOCKS_Z = 3;
        public const int SIZE_IN_BLOCKS_X = (1 << SHIFT_IN_BLOCKS_X);
        public const int SIZE_IN_BLOCKS_Y = (1 << SHIFT_IN_BLOCKS_Y);
        public const int SIZE_IN_BLOCKS_Z = (1 << SHIFT_IN_BLOCKS_Z);
        public const int BRICK_MASK_X = SIZE_IN_BLOCKS_X - 1;
        public const int BRICK_MASK_Y = SIZE_IN_BLOCKS_Y - 1;
        public const int BRICK_MASK_Z = SIZE_IN_BLOCKS_Z - 1;
        public const int BLOCKS_IN_BRICK = SIZE_IN_BLOCKS_X * SIZE_IN_BLOCKS_Y * SIZE_IN_BLOCKS_Z;
    
        // Sector Size: 16 x 16 x 16 Bricks ( = 128 x 128 x 128 Voxels)
        public const int SHIFT_IN_BRICKS_X = 4;
        public const int SHIFT_IN_BRICKS_Y = 4;
        public const int SHIFT_IN_BRICKS_Z = 4;
        public const int SIZE_IN_BRICKS_X = (1 << SHIFT_IN_BRICKS_X);
        public const int SIZE_IN_BRICKS_Y = (1 << SHIFT_IN_BRICKS_Y);
        public const int SIZE_IN_BRICKS_Z = (1 << SHIFT_IN_BRICKS_Z);
        public const int SECTOR_MASK_X = SIZE_IN_BRICKS_X - 1;
        public const int SECTOR_MASK_Y = SIZE_IN_BRICKS_Y - 1;
        public const int SECTOR_MASK_Z = SIZE_IN_BRICKS_Z - 1;
        public const short BRICKID_EMPTY = -1;

        // Flatten brick data stored with relative ID
        public NativeList<Block> voxels;
        
        // Absolute ID => Relative ID
        public NativeArray<short> brickIdx;
        
        // Absolute ID => Flags
        public NativeArray<BrickUpdateInfo.Type> brickFlags;
        
        // Queue with Absolute IDs of dirty bricks
        // For internal renderer use only
        internal NativeQueue<short> updateRecord;
        internal bool IsRendererDirty => !updateRecord.IsEmpty();
        internal bool IsRendererEmpty => RendererNonEmptyBrickCount == 0;
        
        // Single-element array representing a number
        private NativeArray<int> currentBrickId;
        
        public int RendererNonEmptyBrickCount => currentBrickId[0];
        public int NonEmptyBrickCount => currentBrickId[0];
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
                    SIZE_IN_BRICKS_X * SIZE_IN_BRICKS_Y * SIZE_IN_BRICKS_Z,
                    allocator,
                    options),
                brickFlags = new NativeArray<BrickUpdateInfo.Type>(
                    SIZE_IN_BRICKS_X * SIZE_IN_BRICKS_Y * SIZE_IN_BRICKS_Z, allocator, options),
                updateRecord = new NativeQueue<short>(allocator),
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
                    SIZE_IN_BRICKS_X * SIZE_IN_BRICKS_Y * SIZE_IN_BRICKS_Z,
                    allocator),
                brickFlags = new NativeArray<BrickUpdateInfo.Type>(
                    SIZE_IN_BRICKS_X * SIZE_IN_BRICKS_Y * SIZE_IN_BRICKS_Z, allocator),
                updateRecord = new NativeQueue<short>(allocator),
                currentBrickId = new(1, allocator),
                
                nonEmptyBricks = new NativeList<short>(allocator),
            };
            
            s.voxels.AddRange(from.voxels.AsArray());
            s.brickIdx.CopyFrom(from.brickIdx);
            s.currentBrickId.CopyFrom(from.currentBrickId);
            
            return s;
        }

        public void Dispose()
        {
            if (voxels.IsCreated) voxels.Dispose();
            if (brickIdx.IsCreated) brickIdx.Dispose();
            if (brickFlags.IsCreated) brickFlags.Dispose();
            if (updateRecord.IsCreated) updateRecord.Dispose();
            if (currentBrickId.IsCreated) currentBrickId.Dispose();
            if (nonEmptyBricks.IsCreated) nonEmptyBricks.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBrickIdx(int x, int y, int z)
        {
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(x < SIZE_IN_BRICKS_X);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(y < SIZE_IN_BRICKS_Y);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(z < SIZE_IN_BRICKS_Z);
            
            return (x + y * SIZE_IN_BRICKS_X + z * (SIZE_IN_BRICKS_X * SIZE_IN_BRICKS_Y));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3Int ToBrickPos(short bidAbsolute)
        {
            return new Vector3Int(
                bidAbsolute & SECTOR_MASK_X,
                (bidAbsolute >> SHIFT_IN_BRICKS_X) & SECTOR_MASK_Y,
                (bidAbsolute >> (SHIFT_IN_BRICKS_X + SHIFT_IN_BRICKS_Y)));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBlockIdx(int x, int y, int z)
        {
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(x < SIZE_IN_BLOCKS_X);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(y < SIZE_IN_BLOCKS_Y);
            Utils.BurstAssertSimpleExperssionsOnly.IsTrue(z < SIZE_IN_BLOCKS_Z);
            
            return (x + y * SIZE_IN_BLOCKS_X + z * (SIZE_IN_BLOCKS_X * SIZE_IN_BLOCKS_Y));
        }

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

        // TODO: Per-brick thread safety, pre-alloc etc.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
        {
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS_X, y >> SHIFT_IN_BLOCKS_Y, z >> SHIFT_IN_BLOCKS_Z);
            short bid = this.brickIdx[brick_sector_index_id];
            
            if (bid == BRICKID_EMPTY)
            {
                return Block.Empty;
            }
            
            return voxels[
                bid * BLOCKS_IN_BRICK
                + ToBlockIdx(
                    x & BRICK_MASK_X,
                    y & BRICK_MASK_Y,
                    z & BRICK_MASK_Z)
            ];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block b)
        {
            int brick_sector_index_id =
                ToBrickIdx(x >> SHIFT_IN_BLOCKS_X, y >> SHIFT_IN_BLOCKS_Y, z >> SHIFT_IN_BLOCKS_Z);
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
                updateRecord.Enqueue((short)brick_sector_index_id);
                
                currentBrickId[0]++;

                isNonEmptyBricksDirty = true;
            }

            // Set the block in target brick
            voxels[
                bid * BLOCKS_IN_BRICK
                + ToBlockIdx(
                    x & BRICK_MASK_X,
                    y & BRICK_MASK_Y,
                    z & BRICK_MASK_Z)
            ] = b;

            if (brickFlags[brick_sector_index_id] == BrickUpdateInfo.Type.Idle)
            {
                brickFlags[brick_sector_index_id] = BrickUpdateInfo.Type.Modified;
                updateRecord.Enqueue((short)brick_sector_index_id);
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

        public void EndTick()
        {
            // TODO: Burst
            // TODO: Handle delete brick properly
            // foreach (var record in updateRecord)
            // {
            //     brickFlags[record] = BrickUpdateInfo.Type.Idle;
            // }
            //
            // updateRecord.Clear();
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
                for (short i = 0; i < (1 << (SHIFT_IN_BRICKS_X + SHIFT_IN_BRICKS_Y + SHIFT_IN_BRICKS_Z)); i++)
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
    
    public class SectorRef : IDisposable
    {
        public Sector sector;
        public bool isDirty;
        
        // Sector coords inside entity
        public Vector3Int sectorPos;
        public Vector3Int sectorBlockPos => sectorPos * 128; // FIXME: WARNING: <-- Hardcoded.

        public SectorRenderer renderer;
        public VoxelEntity entity;

        public ulong MemoryUsage => (ulong)sector.MemoryUsage + (ulong)(renderer?.MemoryUsage ?? 0);
        public ulong VRAMUsage => (ulong)(renderer?.VRAMUsage ?? 0);

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

        public void Tick()
        {
            sector.ReorderBricks();
            // TODO: Call renderer RenderEmitJob and Render here with proper parallelization
            sector.EndTick();
        }

        public void Remove()
        {
            sector.Dispose();
            renderer?.MarkRemove();
        }

        public void Dispose()
        {
            sector.Dispose();
            renderer?.Dispose();
        }
    }
}