#ifndef VOXELISX_UTILS
#define VOXELISX_UTILS

#define half min16float
#define half2 min16float2
#define half3 min16float3
#define half4 min16float4
#define half3x3 min16float3x3
#define half3x4 min16float3x4

// 0x0000 ~ 0x0FFF -> Non-solid
// 0x1000 ~ 0xEFFF -> Solid
inline bool IsOpaque(int blk)
{
    return blk > 0;
    // return (blk & 0x8000);
}

inline int GetFaceBits(int blk)
{
    return (blk & 0x7EFF) >> 9;
}

#endif