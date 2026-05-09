#ifndef VOXELISX_UTILS
#define VOXELISX_UTILS

// 0x0000 ~ 0x0FFF -> Non-solid
// 0x1000 ~ 0xEFFF -> Solid
inline bool IsOpaque(int blk)
{
    // return blk > 0;
    return (blk & 0x8000);
}

inline int GetFaceBits(int blk)
{
    return (blk & 0x7EFF) >> 9;
}

#endif