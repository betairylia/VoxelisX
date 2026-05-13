#ifndef VOXELISX_RAY_PAYLOAD_UTILS_INCLUDED
#define VOXELISX_RAY_PAYLOAD_UTILS_INCLUDED

inline void VoxelisXApplyVoxelMiss(inout RayPayload payload, float3 skyRadiance)
{
    // float3 ext = payload.previousTransparentMaterial == 0 ? float3(1, 1, 1) : float3(0, 0, 0);
    float3 ext = float3(1, 1, 1);

    payload.emission = skyRadiance * ext * 4;
    payload.bounceIndexOpaque = -1;
}

#endif
