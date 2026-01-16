using System;

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
        public static DirtyPropagationSettings Settings = new DirtyPropagationSettings
        {
            neighborhoodType = NeighborhoodType.Moore
        };

        public NeighborhoodType neighborhoodType;
    }
}
