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

        // Lookup table for converting direction vectors to indices using trinary encoding
        // Maps 3D vectors of {-1,0,1}^3 to direction indices 0-25, or -1 for center (0,0,0)
        // Encoding: index = (x+1)*9 + (y+1)*3 + (z+1), giving range 0-26
        private static readonly int[] DirectionLookup = new int[27]
        {
            25,  // 0: (-1,-1,-1)
             9,  // 1: (-1,-1, 0)
            24,  // 2: (-1,-1, 1)
            13,  // 3: (-1, 0,-1)
             1,  // 4: (-1, 0, 0)
            12,  // 5: (-1, 0, 1)
            23,  // 6: (-1, 1,-1)
             8,  // 7: (-1, 1, 0)
            22,  // 8: (-1, 1, 1)
            17,  // 9: ( 0,-1,-1)
             3,  // 10: ( 0,-1, 0)
            16,  // 11: ( 0,-1, 1)
             5,  // 12: ( 0, 0,-1)
            -1,  // 13: ( 0, 0, 0) - center, not a neighbor
             4,  // 14: ( 0, 0, 1)
            15,  // 15: ( 0, 1,-1)
             2,  // 16: ( 0, 1, 0)
            14,  // 17: ( 0, 1, 1)
            21,  // 18: ( 1,-1,-1)
             7,  // 19: ( 1,-1, 0)
            20,  // 20: ( 1,-1, 1)
            11,  // 21: ( 1, 0,-1)
             0,  // 22: ( 1, 0, 0)
            10,  // 23: ( 1, 0, 1)
            19,  // 24: ( 1, 1,-1)
             6,  // 25: ( 1, 1, 0)
            18   // 26: ( 1, 1, 1)
        };

        /// <summary>
        /// Converts a direction vector to its corresponding direction index using O(1) lookup.
        /// </summary>
        /// <param name="direction">Direction vector with components in {-1, 0, 1}</param>
        /// <returns>Direction index [0, 25], or -1 if direction is (0,0,0) or invalid</returns>
        public static int DirectionToIndex(int3 direction)
        {
            // Encode direction using trinary: (x+1)*9 + (y+1)*3 + (z+1)
            int x = direction.x + 1;
            int y = direction.y + 1;
            int z = direction.z + 1;

            // Validate that components are in valid range {0, 1, 2} (original {-1, 0, 1})
            if (x < 0 || x > 2 || y < 0 || y > 2 || z < 0 || z > 2)
            {
                return -1;
            }

            int lookupIndex = x * 9 + y * 3 + z;
            return DirectionLookup[lookupIndex];
        }
    }
}
