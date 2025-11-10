using System;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

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
}
