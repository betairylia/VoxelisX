using System;
using Unity.Mathematics;

namespace Voxelis
{
    [Flags]
    public enum DirtyFlags : ushort
    {
        None       = 0,
        Reserved0  = 1 << 0,
        Reserved1  = 1 << 1,
        Reserved2  = 1 << 2,
        Reserved3  = 1 << 3,
        Reserved4  = 1 << 4,
        Reserved5  = 1 << 5,
        Reserved6  = 1 << 6,
        Reserved7  = 1 << 7,
        Reserved8  = 1 << 8,
        Reserved9  = 1 << 9,
        Reserved10 = 1 << 10,
        Reserved11 = 1 << 11,
        Reserved12 = 1 << 12,
        Reserved13 = 1 << 13,
        Reserved14 = 1 << 14,
        Reserved15 = 1 << 15,
        All        = 0xFFFF,
    }

    public enum NeighborhoodType : byte
    {
        VonNeumann = 0,  // 6 face-adjacent
        Moore = 1,       // 26 neighbors (face + edge + corner)
    }

    public struct DirtyPropagationSettings
    {
        public const int VON_NEUMANN_COUNT = 6;
        public const int MOORE_COUNT = 26;
        
        public const NeighborhoodType neighborhoodType = NeighborhoodType.Moore;

        public static readonly int neighborhoodCount = (
            DirtyPropagationSettings.neighborhoodType == NeighborhoodType.Moore ? MOORE_COUNT : VON_NEUMANN_COUNT);
        
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
            25, 24, 23, 22, 19, 18, 21, 20
        };
    }
}
