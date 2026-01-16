using System;
using System.Numerics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor.TerrainTools;

namespace Voxelis
{
    /// <summary>
    /// A handle to a Sector that can be used in jobs without requiring the job struct to be marked unsafe.
    /// Internally stores a Sector pointer.
    /// </summary>
    [BurstCompile]
    public unsafe struct SectorHandle
    {
        [NativeDisableUnsafePtrRestriction]
        private Sector* _ptr;
        
        public bool IsNull => _ptr == null;

        public static SectorHandle AllocEmpty(int initialBricks = 1, Allocator allocator = Allocator.Persistent)
        {
            // Allocate memory for the Sector struct itself
            Sector* sectorPtr = (Sector*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<Sector>(),
                UnsafeUtility.AlignOf<Sector>(),
                allocator);

            // Initialize the sector in-place
            *sectorPtr = Sector.New(allocator, initialBricks);

            return new SectorHandle(sectorPtr);
        }

        public void Dispose(Allocator allocator)
        {
            _ptr->Dispose(allocator);
            UnsafeUtility.Free(_ptr, allocator);
        }

        /// <summary>
        /// Creates a new SectorHandle from a sector pointer.
        /// </summary>
        public SectorHandle(Sector* sectorPtr)
        {
            _ptr = sectorPtr;
        }

        /// <summary>
        /// Gets the raw sector pointer. Use this when you need direct pointer access.
        /// </summary>
        public Sector* Ptr => _ptr;

        /// <summary>
        /// Gets a reference to the sector.
        /// </summary>
        public ref Sector Get()
        {
            return ref *_ptr;
        }

        /// <summary>
        /// Gets the block at the specified position within the sector.
        /// </summary>
        public Block GetBlock(int x, int y, int z)
        {
            return _ptr->GetBlock(x, y, z);
        }

        /// <summary>
        /// Sets the block at the specified position within the sector.
        /// </summary>
        public void SetBlock(int x, int y, int z, Block block)
        {
            _ptr->SetBlock(x, y, z, block);
        }

        /// <summary>
        /// Gets a pointer to a brick within the sector.
        /// </summary>
        public Block* GetBrick(int x, int y, int z)
        {
            return _ptr->GetBrick(x, y, z);
        }

        /// <summary>
        /// Gets a pointer to a brick by brick ID.
        /// </summary>
        public Block* GetBrick(short bid)
        {
            return _ptr->GetBrick(bid);
        }

        /// <summary>
        /// Returns true if the handle is valid (not null).
        /// </summary>
        public bool IsValid => _ptr != null;

        /// <summary>
        /// Gets the number of non-empty bricks in this sector.
        /// </summary>
        public int NonEmptyBrickCount => _ptr->NonEmptyBrickCount;

        /// <summary>
        /// Gets the number of non-empty bricks allocated in this sector for rendering.
        /// </summary>
        public int RendererNonEmptyBrickCount => _ptr->RendererNonEmptyBrickCount;

        /// <summary>
        /// Returns true if there are pending brick updates for the renderer.
        /// </summary>
        public bool IsRendererDirty => _ptr->IsRendererDirty;

        /// <summary>
        /// Returns true if the sector contains no allocated bricks.
        /// </summary>
        public bool IsRendererEmpty => _ptr->IsRendererEmpty;
    }

    [BurstCompile]
    public struct SectorNeighborHandles
    {
        public NativeArray<SectorHandle> Neighbors;

        public static readonly int3[] Directions = new int3[6]
        {
            new int3( 1,  0,  0),
            new int3(-1,  0,  0),
            new int3( 0,  1,  0),
            new int3( 0, -1,  0),
            new int3( 0,  0,  1),
            new int3( 0,  0, -1),
        };

        public enum Direction
        {
            Right = 0,
            Left = 1,
            Up = 2,
            Down = 3,
            Forward = 4,
            Back = 5,
            Length = 6,
        }

        public static SectorNeighborHandles Create(Allocator allocator = Allocator.Persistent)
        {
            SectorNeighborHandles handles = new SectorNeighborHandles();
            handles.Neighbors = new NativeArray<SectorHandle>(6, allocator);
            return handles;
        }
        
        // X+
        public static int3 dRight => Directions[0]; 
        public SectorHandle Right => Neighbors[0];
        
        // X-
        public static int3 dLeft => Directions[1]; 
        public SectorHandle Left => Neighbors[1];
        
        // Y+
        public static int3 dUp => Directions[2]; 
        public SectorHandle Up => Neighbors[2];
        
        // Y-
        public static int3 dDown => Directions[3]; 
        public SectorHandle Down => Neighbors[3];
        
        // Z+
        public static int3 dForward => Directions[4]; 
        public SectorHandle Forward => Neighbors[4];
        
        // Z-
        public static int3 dBack => Directions[5]; 
        public SectorHandle Back => Neighbors[5];
    }
}
