using System;
using Unity.Mathematics;

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
}
