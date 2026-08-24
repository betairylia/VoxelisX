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
    /// ROOTED at this voxel and are COLLISION-ACTIVE. Occupied voxel centers form the complex: two
    /// adjacent centers span a segment, four a square, eight a cube. A cell is rooted at its
    /// minimum-corner voxel and spans one unit along each axis of its axis mask.
    /// </summary>
    /// <remarks>
    /// A cell is active when it owns at least one outward direction that no higher-dimensional cell
    /// containing it owns. A cell that can grow along axis <c>a</c> loses every direction with a
    /// positive component along <c>+a</c> to the grown cell rooted here, and every direction with a
    /// positive component along <c>-a</c> to the grown cell rooted one voxel back. So both of them
    /// together take everything except a zero-area slice, which gives:
    /// <code>
    /// (r, m) is active  &lt;=&gt;  for every axis a outside m,
    ///                            NOT ( (r, m+a) exists AND (r-a, m+a) exists )
    /// </code>
    /// This is the containment rule it replaced with AND in place of OR: containment dedup dropped a
    /// cell when EITHER grown cell existed, activity drops it only when BOTH do. Concretely: an edge
    /// inside a flat tiled face is inactive, a sheet rim is active, a vertex inside a flat face is
    /// inactive, a geometric corner is active, and an interior face of a solid is inactive.
    ///
    /// Coverage invariant this preserves: for any point OUTSIDE the solid region, the distance to
    /// the active cells equals the distance to the full complex, because every inactive cell is
    /// contained in a cell that is either active or buried inside solid. Interior distance is NOT
    /// preserved - containment and deep overlap need the separate volume path (see BitCube).
    /// </remarks>
    public struct PhysicsInfo : IEquatable<PhysicsInfo>
    {
        /// <summary>
        /// One bit per active cell rooted here. Bits 0-6 keep the positive octet order
        /// (+X, +Y, +Z, +XY, +XZ, +YZ, +XYZ); bit 7 marks the bare point. Bit 6 (the cube) is a
        /// VOLUME cell, not a surface feature: it is set whenever the cube exists and is excluded
        /// from surface contact generation. Physics-key state is stored only in this slot's one-bit
        /// aux mask.
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

        /// <summary>
        /// Every bit except <see cref="BitCube"/>. A root can carry all seven at once - the minimum
        /// corner voxel of a solid box roots three boundary faces, three convex edges and the corner
        /// point - so consumers must not assume the old "at most three cells" dedup bound.
        /// </summary>
        public const byte SurfaceFeatureMask = unchecked((byte)~(1 << BitCube));

        /// <summary>Number of surface cells a single voxel can root, i.e. everything but the cube.</summary>
        public const int SurfaceFeatureBitCount = FeatureBitCount - 1;

        /// <summary>The bare point, i.e. every cell of dimension 0 rooted here.</summary>
        public const byte PointMask = 1 << BitPoint;

        /// <summary>The three positive segments, i.e. every cell of dimension 1 rooted here.</summary>
        public const byte EdgeMask = (1 << BitEdgeX) | (1 << BitEdgeY) | (1 << BitEdgeZ);

        /// <summary>The three positive squares, i.e. every cell of dimension 2 rooted here.</summary>
        public const byte FaceMask = (1 << BitFaceXY) | (1 << BitFaceXZ) | (1 << BitFaceYZ);

        /// <summary>
        /// Dimension of the LOWEST-dimensional active surface cell rooted here: 0 for a point, 1 for
        /// a segment, 2 for a square. Callers must have checked <see cref="HasSurfaceFeatures"/>
        /// first; a root with no surface cell answers 2 and must not be used.
        /// </summary>
        /// <remarks>
        /// The narrowphase pairs features by dimension, and its permitted set is exactly
        /// <c>dim(a) + dim(b) &lt;= 2</c>. Because the smallest dimension present bounds every pair a
        /// root can take part in, two roots can produce a contact only when the sum of their minimum
        /// dimensions also fits in 2. That makes this the cheapest possible pair rejection, decided
        /// from two bytes before any feature or vertex is built.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MinSurfaceFeatureDimension()
        {
            if ((data & PointMask) != 0)
            {
                return 0;
            }
            return (data & EdgeMask) != 0 ? 1 : 2;
        }

        // Nibble i holds the segments (X = bit 0, Y = bit 1, Z = bit 2) contained in the squares of
        // index i, where i is the three square bits taken in BitFaceXY order (XY = 1, XZ = 2,
        // YZ = 4). A square contains the segments of both of its axes, so XY covers X and Y, XZ
        // covers X and Z, and YZ covers Y and Z.
        private const uint k_EdgesCoveredByFaces = 0x77767530u;

        /// <summary>
        /// Feature-bit mask of the active surface cells rooted here that are MAXIMAL among the
        /// active cells rooted here of dimension at most <paramref name="dimensionBudget"/>.
        /// </summary>
        /// <remarks>
        /// If cell c is contained in cell d, then for every pose and every opposing feature S the
        /// distance to d is at most the distance to c, so the constraint (S, d) implies (S, c) and
        /// the pair (S, c) can be dropped - but ONLY when (S, d) is really emitted. The narrowphase
        /// permits <c>dim(a) + dim(b) &lt;= 2</c>, so an opposing feature of dimension k leaves this
        /// root a budget of <c>2 - k</c> and only containments inside that budget may drop anything.
        /// That makes maximality a property of (root, budget), NOT of the root alone: the end voxel
        /// of a one-wide bar roots a point covered by a segment, so the point must go at budget 1
        /// and must stay at budget 0, where the segment cannot be paired at all.
        ///
        /// Every containment used here is rooted at this same voxel, so the dominating cell is built
        /// from this same byte and is always enumerated alongside the cell it replaces. No probe
        /// range, pass order or seam rule can take it away. Cells contained only in a cell rooted at
        /// a NEGATIVE neighbor are not detected, which keeps some duplicates but never opens a hole.
        ///
        /// The cube is never a dominator. It is a volume cell that takes part in no permitted pair,
        /// so a square contained only in a cube must survive - the same reason
        /// <see cref="CoverMaskForAxes"/> excludes it.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte MaximalFeatureMask(int dimensionBudget)
        {
            if (dimensionBudget < 0)
            {
                return 0;
            }

            if (dimensionBudget == 0)
            {
                // No cell of dimension 0 can contain the point, so an active point is always maximal.
                return (byte)(data & PointMask);
            }

            int edges = data & EdgeMask;
            if (dimensionBudget == 1)
            {
                // Segments are contained only in squares and cubes, both outside this budget, so
                // every active segment is maximal. The point survives only when no segment rooted
                // here contains it.
                return edges != 0 ? (byte)edges : (byte)(data & PointMask);
            }

            int faces = data & FaceMask;
            int coveredEdges = (int)((k_EdgesCoveredByFaces >> ((faces >> BitFaceXY) << 2)) & 0xFu);

            // Squares are maximal whenever they are active: their only proper superset is the cube.
            // A segment falls out when an active square rooted here spans its axis, and the point
            // falls out when any segment or square is rooted here at all.
            int point = (data & (EdgeMask | FaceMask)) == 0 ? data & PointMask : 0;
            return (byte)(faces | (edges & ~coveredEdges) | point);
        }

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

        // Bit i is set when the SURFACE cell in feature bit i spans every axis in mask i. Used to ask
        // "does this voxel root a cell that covers these axes", which is how a contact point on a
        // cell's positive boundary finds out whether its canonical owner exists.
        //
        // BitCube is deliberately absent from every entry. A cube does span all three axes, but it
        // never becomes a contact feature, so it must not claim a seam point it cannot emit. Axes
        // XYZ therefore maps to 0: no surface cell spans all three.
        private const uint k_CoverMaskLo = 0x082A19BFu;   // axes none, X, Y, XY
        private const uint k_CoverMaskHi = 0x00201034u;   // axes Z, XZ, YZ, XYZ

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
        /// True when this voxel roots at least one active surface cell, i.e. it can take part in
        /// ordinary contact generation. False for air, and false for a voxel deep inside a solid,
        /// which roots only <see cref="BitCube"/>.
        /// </summary>
        public bool HasSurfaceFeatures => (data & SurfaceFeatureMask) != 0;

        /// <summary>True when a cube cell is rooted here, i.e. the eight-voxel octet is solid.</summary>
        public bool HasVolumeCell => (data & (1 << BitCube)) != 0;

        /// <summary>
        /// True when this voxel roots no active surface cell, so the surface contact path must skip
        /// it. Either it is buried in solid and every direction belongs to a cell nearer the
        /// boundary, or a lower-dimensional cell it would root is fully absorbed by its neighbors.
        /// Air blocks read as true as well.
        /// </summary>
        public bool IsInterior => !HasSurfaceFeatures;

        /// <summary>
        /// True when the bare point is active, i.e. every axis has at least one empty face neighbor.
        /// This is the geometric-corner / endpoint / isolated-voxel class.
        /// </summary>
        public bool HasPointFeature => (data & (1 << BitPoint)) != 0;

        /// <summary>
        /// True when this root can start a sparse contact probe: it carries an active point or an
        /// active edge. Every permitted feature pair has a vertex or an edge on at least one side,
        /// so these roots are exactly the contact sources. Mirrors the slot's aux key mask.
        /// </summary>
        public bool IsContactSource =>
            (data & ((1 << BitPoint) | (1 << BitEdgeX) | (1 << BitEdgeY) | (1 << BitEdgeZ))) != 0;

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
