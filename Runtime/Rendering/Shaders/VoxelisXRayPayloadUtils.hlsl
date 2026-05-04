#ifndef VOXELISX_RAY_PAYLOAD_UTILS_INCLUDED
#define VOXELISX_RAY_PAYLOAD_UTILS_INCLUDED

inline void VoxelisXApplyVoxelMiss(inout RayPayload payload)
{
    payload.emission = float3(0.8, 0.9, 1.0);
    payload.bounceIndexOpaque = -1;
}

#endif
