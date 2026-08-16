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

    public struct PhysicsInfo : IEquatable<PhysicsInfo>
    {
        // Bits 0-6 contain occupancy of the seven positive neighbors in the 2x2x2 octet rooted
        // at this block: +X, +Y, +Z, +XY, +XZ, +YZ and +XYZ. Bit 7 marks a block whose six face
        // neighbors are solid. Physics-key state is stored only in this slot's one-bit aux mask.
        public byte data;

        public byte ForwardOccupancy => (byte)(data & 0x7f);
        public bool IsInterior => (data & 0x80) != 0;

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
