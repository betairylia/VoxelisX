using System;
using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// Represents a single visible voxel block with encoded color and material data.
    /// Uses a 16-bit packed format for efficient storage and rendering.
    /// </summary>
    /// <remarks>
    /// The block data is encoded as follows:
    /// The block data encodes RGB555 color (5 bits per channel) plus 1 emission bit.
    /// Metadata lives in the sector's <see cref="Meta"/> slot, not in <see cref="Block"/>.
    /// </remarks>
    [Serializable]
    public struct Block : IEquatable<Block>
    {
        /// <summary>
        /// The packed 16-bit visible block value.
        /// </summary>
        public ushort data;

        // Perhaps we can just use a LUT and free those bits
        // private const uint PhaseMask = 0xC0000000; // 00 - Gas; 01 - Liquid; 10 - Powder; 11 - Solid
        // private const uint PhaseShift= 30;
        private const int OpaqueMask = 0x8000;
        private const uint TransMask = 0x01FF;      // Transparent blocks have 0~1FF (512) valid blockID slots
        private const int TFaceShift = 9;           // 6 bits for transparent blocks will be used to represent boundaries

        /// <summary>
        /// Gets the block ID portion of the packed data.
        /// </summary>
        public ushort id => data;

        /// <summary>
        /// Returns true if this block is empty (all data is zero).
        /// </summary>
        public bool isEmpty => (data == 0);

        /// <summary>
        /// Returns true if this block has no renderer-visible material ID.
        /// </summary>
        public bool isRendererEmpty => id == 0;

        public static bool IsRendererDataEmpty(uint data) => data == 0;
        // public bool isVoid => (data == 0);

        public bool isOpaque => (id & OpaqueMask) > 0;
        public uint transparentId => (id & TransMask);

        public static uint MaskTransparency(uint faceMask, uint transparencyId)
            => transparencyId + (faceMask << TFaceShift);

        /// <summary>
        /// Constructs a block with the specified block ID.
        /// </summary>
        /// <param name="id">The block ID to use.</param>
        public Block(ushort id)
        {
            data = id;
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
            data = (ushort)id;
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
            data = (ushort)id;
        }

        /// <summary>
        /// Represents an empty/air block with no data.
        /// </summary>
        public static readonly Block Empty = new Block() { data = 0 };

        public static bool operator ==(Block a, Block b) => a.data == b.data;
        public static bool operator !=(Block a, Block b) => a.data != b.data;

        public bool Equals(Block other)
        {
            return data == other.data;
        }

        public override bool Equals(object obj)
        {
            return obj is Block other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)data;
        }
    }

    /// <summary>
    /// Per-voxel physics topology: which finite cells of the voxel-center cubical complex are
    /// ROOTED at this voxel and survive dedup. Occupied voxel centers form the complex: two
    /// adjacent centers span a segment, four a square, eight a cube. A cell is rooted at its
    /// minimum-corner voxel and spans one unit along each axis of its axis mask.
    /// </summary>
    /// <remarks>
    /// A cell is dropped when a larger existing cell contains it. Every container of a cell rooted
    /// at <c>r</c> is itself rooted in the backward octet <c>r - {0,1}^3</c>, so the test reduces to
    /// single-axis growth: cell <c>(r, m)</c> survives when, for every axis <c>a</c> outside
    /// <c>m</c>, neither <c>(r, m+a)</c> nor <c>(r-a, m+a)</c> is fully occupied. The union of the
    /// surviving cells still covers every occupied voxel, so the collision surface is unchanged and
    /// only redundant contact sources are removed.
    /// </remarks>
    public struct PhysicsInfo : IEquatable<PhysicsInfo>
    {
        /// <summary>
        /// One bit per surviving cell rooted here. Bits 0-6 keep the positive octet order
        /// (+X, +Y, +Z, +XY, +XZ, +YZ, +XYZ); bit 7 marks the bare point. Zero means the voxel
        /// roots nothing and physics must not use it as a source. Physics-key state is stored only
        /// in this slot's one-bit aux mask.
        /// </summary>
        public byte data;

        public const int BitEdgeX = 0;
        public const int BitEdgeY = 1;
        public const int BitEdgeZ = 2;
        public const int BitFaceXY = 3;
        public const int BitFaceXZ = 4;
        public const int BitFaceYZ = 5;
        public const int BitCube = 6;
        public const int BitPoint = 7;

        /// <summary>Number of distinct cells a single voxel can root.</summary>
        public const int FeatureBitCount = 8;

        // Nibble i of each constant maps one direction of the bit <-> axis-mask pair. Axis masks use
        // X=1, Y=2, Z=4, so the two orders differ and a literal table is the cheapest Burst-safe map.
        private const uint k_AxisMaskByFeatureBit = 0x07653421u;
        private const uint k_FeatureBitByAxisMask = 0x65423107u;

        /// <summary>Axis mask (X=1, Y=2, Z=4) of the cell stored in <paramref name="featureBit"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AxisMaskFromFeatureBit(int featureBit)
        {
            return (int)((k_AxisMaskByFeatureBit >> (featureBit << 2)) & 0xFu);
        }

        /// <summary>Bit index holding the cell with the given axis mask (X=1, Y=2, Z=4).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FeatureBitFromAxisMask(int axisMask)
        {
            return (int)((k_FeatureBitByAxisMask >> (axisMask << 2)) & 0xFu);
        }

        // Bit i is set when the cell in feature bit i spans every axis in mask i. Used to ask
        // "does this voxel root a cell that covers these axes", which is how a contact point on a
        // cell's positive boundary finds out whether its canonical owner still exists after dedup.
        private const uint k_CoverMaskLo = 0x486A59FFu;   // axes none, X, Y, XY
        private const uint k_CoverMaskHi = 0x40605074u;   // axes Z, XZ, YZ, XYZ

        /// <summary>
        /// Feature-bit mask of the cells whose axis mask contains every axis in
        /// <paramref name="axes"/> (X=1, Y=2, Z=4).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte CoverMaskForAxes(int axes)
        {
            return axes < 4
                ? (byte)((k_CoverMaskLo >> (axes << 3)) & 0xFFu)
                : (byte)((k_CoverMaskHi >> ((axes - 4) << 3)) & 0xFFu);
        }

        /// <summary>
        /// True when a cell rooted here spans every axis in <paramref name="axes"/>, so this voxel
        /// can own a contact point that is interior along those axes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RootsCellCovering(int axes)
        {
            return (data & CoverMaskForAxes(axes)) != 0;
        }

        /// <summary>
        /// True when this voxel roots no cell at all, so physics must skip it as a contact source.
        /// Every cell it would root is contained in one rooted at a backward neighbor, which carries
        /// the same geometry. Air blocks read as true as well.
        /// </summary>
        public bool IsInterior => data == 0;

        /// <summary>True when the bare point survives, i.e. no face neighbor is occupied.</summary>
        public bool HasPointFeature => (data & (1 << BitPoint)) != 0;

        public bool Equals(PhysicsInfo other)
        {
            return data == other.data;
        }

        public override int GetHashCode()
        {
            return (int)data;
        }

    }

    // TODO: Make this customizable
    public enum SectorSlotId
    {
        Block = 0,
        PhysicsInfo = 1,
        Reserved1 = 2,
        Reserved2 = 3,
        Reserved3 = 4,
        Reserved4 = 5,
        Reserved5 = 6,
        Reserved6 = 7,
    }

}
