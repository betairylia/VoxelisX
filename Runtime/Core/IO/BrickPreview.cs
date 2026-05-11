using System.Runtime.CompilerServices;

namespace Voxelis.IO
{
    /// <summary>
    /// Per-brick 32-bit rough preview value. Layout:
    /// <list type="bullet">
    ///   <item>bits 0..7   — occupancy mask over the brick's 2x2x2 partition of 4^3 sub-bricks</item>
    ///   <item>bits 8..15  — emission mask, same sub-brick layout</item>
    ///   <item>bits 16..31 — RGB565 average color of non-empty blocks in the brick</item>
    /// </list>
    /// Empty bricks encode as 0.
    /// Sub-brick index: <c>sx | (sy &lt;&lt; 1) | (sz &lt;&lt; 2)</c> with each axis in {0,1}.
    /// </summary>
    public static class BrickPreview
    {
        public const int OccupancyShift = 0;
        public const uint OccupancyMask = 0x000000FFu;
        public const int EmissionShift = 8;
        public const uint EmissionMask = 0x0000FF00u;
        public const int ColorShift = 16;
        public const uint ColorMask = 0xFFFF0000u;

        public const int SubBricksPerAxis = 2;
        public const int SubBrickSizeInBlocks = Sector.SIZE_IN_BLOCKS / SubBricksPerAxis;
        public const int SubBrickCount = SubBricksPerAxis * SubBricksPerAxis * SubBricksPerAxis;
        public const int SubBrickShift = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SubBrickIndex(int subX, int subY, int subZ)
            => subX | (subY << 1) | (subZ << 2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Pack(uint occupancyMask, uint emissionMask, ushort rgb565)
        {
            return (occupancyMask & OccupancyMask)
                 | ((emissionMask & 0xFFu) << EmissionShift)
                 | ((uint)rgb565 << ColorShift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Occupancy(uint preview) => preview & OccupancyMask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Emission(uint preview) => (preview >> EmissionShift) & 0xFFu;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Color565(uint preview) => (ushort)(preview >> ColorShift);

        /// <summary>
        /// Converts 5-bit-per-channel averages (from Block RGB555) to a 16-bit RGB565 word.
        /// G's 5-bit value is shifted into the 6-bit slot — the LSB is left zero.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ToRgb565(int r5, int g5, int b5)
        {
            int g6 = g5 << 1;
            return (ushort)((r5 << 11) | (g6 << 5) | b5);
        }
    }
}
