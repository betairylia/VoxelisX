using System.Runtime.CompilerServices;

namespace Voxelis
{
    /// <summary>
    /// A 512-bit per-brick bitmask: 8 × ulong = 64 bytes, one bit per voxel, indexed by the same
    /// voxel index as <see cref="Sector.ToBlockIdx"/>. Used as a slot aux buffer
    /// (<see cref="SectorSlotStorage.aux"/>) for fast per-brick "which voxels match" queries — e.g.
    /// non-empty blocks on the Block slot, or physics-key blocks on the PhysicsInfo slot.
    /// </summary>
    public static unsafe class BrickBitmask
    {
        /// <summary>Number of ulong words (512 voxels / 64 bits per word).</summary>
        public const int Words = Sector.BLOCKS_IN_BRICK / 64;

        /// <summary>Byte size of one brick's mask; pass to <c>EnsureAuxAllocated</c>.</summary>
        public const int Bytes = Words * 8;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(ulong* mask)
        {
            for (int i = 0; i < Words; i++) { mask[i] = 0ul; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetBit(ulong* mask, int voxelIdx)
        {
            mask[voxelIdx >> 6] |= 1ul << (voxelIdx & 63);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetBit(ulong* mask, int voxelIdx)
        {
            return (mask[voxelIdx >> 6] & (1ul << (voxelIdx & 63))) != 0ul;
        }
    }
}
