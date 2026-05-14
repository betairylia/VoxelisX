#ifndef VOXELISX_RAY_PAYLOAD_INCLUDED
#define VOXELISX_RAY_PAYLOAD_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

struct RayPayload
{
    float T;
    uint materialID_voxelFaceHash;
    uint packedWorldNormal;
    uint2 packedPrevWorldOffset;
};

inline void VoxelisXClearRayPayload(out RayPayload payload)
{
    payload.T = 0.0f;
    payload.materialID_voxelFaceHash = 0u;
    payload.packedWorldNormal = 0u;
    payload.packedPrevWorldOffset = uint2(0u, 0u);
}

inline bool VoxelisXRayPayloadHasHit(RayPayload payload)
{
    return payload.materialID_voxelFaceHash != 0u;
}

inline uint VoxelisXPackHalf2(float2 value)
{
    return (f32tof16(value.x) & 0xFFFFu) | ((f32tof16(value.y) & 0xFFFFu) << 16);
}

inline float2 VoxelisXUnpackHalf2(uint value)
{
    return float2(
        f16tof32(value & 0xFFFFu),
        f16tof32((value >> 16) & 0xFFFFu));
}

inline uint VoxelisXPackWorldNormal(float3 normal)
{
    return VoxelisXPackHalf2(PackNormalOctQuadEncode(normalize(normal)));
}

inline float3 VoxelisXUnpackWorldNormal(uint packedNormal)
{
    return normalize(UnpackNormalOctQuadEncode(VoxelisXUnpackHalf2(packedNormal)));
}

inline uint2 VoxelisXPackFloat3ToHalf4(float3 value)
{
    return uint2(
        VoxelisXPackHalf2(value.xy),
        VoxelisXPackHalf2(float2(value.z, 0.0f)));
}

inline float3 VoxelisXUnpackFloat3FromHalf4(uint2 value)
{
    return float3(VoxelisXUnpackHalf2(value.x), VoxelisXUnpackHalf2(value.y).x);
}

#endif
