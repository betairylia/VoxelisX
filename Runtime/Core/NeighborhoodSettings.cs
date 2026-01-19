using System;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// Defines the type of neighborhood used for spatial queries and dirty propagation.
    /// </summary>
    public enum NeighborhoodType : byte
    {
        /// <summary>6 face-adjacent neighbors (Manhattan distance = 1).</summary>
        VonNeumann = 0,
        /// <summary>26 neighbors including faces, edges, and corners (Chebyshev distance = 1).</summary>
        Moore = 1,
    }

    /// <summary>
    /// Configuration and lookup tables for neighborhood relationships between sectors and bricks.
    /// </summary>
    /// <remarks>
    /// This struct provides direction vectors and precomputed indices for efficiently accessing
    /// neighbors in a 3D grid. The neighborhood type (Von Neumann or Moore) determines how many
    /// neighbors are considered. All direction vectors and indices are statically defined for
    /// performance.
    /// </remarks>
    public struct NeighborhoodSettings
    {
        public const int VON_NEUMANN_COUNT = 6;
        public const int MOORE_COUNT = 26;
        
        public const NeighborhoodType neighborhoodType = NeighborhoodType.Moore;

        public static readonly int neighborhoodCount = (
            NeighborhoodSettings.neighborhoodType == NeighborhoodType.Moore ? MOORE_COUNT : VON_NEUMANN_COUNT);
        
        // [0-5]: Face (Von Neumann), [6-17]: Edge, [18-25]: Corner
        public static readonly int3[] Directions = new int3[26]
        {
            // Face (Von Neumann)
            new int3( 1,  0,  0), new int3(-1,  0,  0), new int3( 0,  1,  0),
            new int3( 0, -1,  0), new int3( 0,  0,  1), new int3( 0,  0, -1),
            // Edge
            new int3( 1,  1,  0), new int3( 1, -1,  0), new int3(-1,  1,  0), new int3(-1, -1,  0),
            new int3( 1,  0,  1), new int3( 1,  0, -1), new int3(-1,  0,  1), new int3(-1,  0, -1),
            new int3( 0,  1,  1), new int3( 0,  1, -1), new int3( 0, -1,  1), new int3( 0, -1, -1),
            // Corner
            new int3( 1,  1,  1), new int3( 1,  1, -1), new int3( 1, -1,  1), new int3( 1, -1, -1),
            new int3(-1,  1,  1), new int3(-1,  1, -1), new int3(-1, -1,  1), new int3(-1, -1, -1),
        };
 
        // Precomputed opposite direction indices for O(1) lookup
        // OppositeDirectionIndices[i] gives the index of the opposite direction for Directions[i]
        public static readonly int[] OppositeDirectionIndices = new int[26]
        {
            // Face pairs: 0<->1, 2<->3, 4<->5
            1, 0, 3, 2, 5, 4,
            // Edge pairs: 6<->9, 7<->8, 10<->13, 11<->12, 14<->17, 15<->16
            9, 8, 7, 6, 13, 12, 11, 10, 17, 16, 15, 14,
            // Corner pairs: 18<->25, 19<->24, 20<->23, 21<->22
            25, 24, 23, 22, 21, 20, 19, 18
        };
    }
}
