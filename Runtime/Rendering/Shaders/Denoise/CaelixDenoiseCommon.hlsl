#ifndef CAELIX_DENOISE_COMMON_INCLUDED
#define CAELIX_DENOISE_COMMON_INCLUDED

// Screen-space helpers and the G-buffer guides every Caelix screen-space filter shares.
//
// Both denoise chains (the path tracer's indirect radiance and the budget mode's AO/shadow)
// run the same kernels over the same geometry guides; only the signal being filtered differs.
// That signal is named by a macro so a shader can point a kernel at its own target without the
// kernel knowing anything about it -- see CaelixATrous.hlsl / CaelixTemporal.hlsl.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// --- Shared G-buffer guides ---------------------------------------------------------------
// Declared here rather than per-shader because every filter below reads them under these exact
// names, and the C# side publishes them as globals once (CaelixShaderIDs).
TEXTURE2D(_NormalTex);
TEXTURE2D(_MotionVectorTex);
TEXTURE2D(_CurrentDepthHistoryTex);
TEXTURE2D(_CurrentNormalHistoryTex);

float4 _CaelixFrameSize;

uint2 CaelixPixelCoord(float2 uv)
{
    uint2 size = max(uint2(_CaelixFrameSize.xy), uint2(1, 1));
    return min((uint2)(uv * size), size - uint2(1, 1));
}

uint2 CaelixClampCoord(int2 coord)
{
    uint2 size = max(uint2(_CaelixFrameSize.xy), uint2(1, 1));
    int2 maxCoord = int2(size) - int2(1, 1);
    return (uint2)clamp(coord, int2(0, 0), maxCoord);
}

// The raygen builds its UVs against (size - 1), so every consumer of a reprojected UV has to
// use the same denominator or history lands half a pixel off.
float2 CaelixHistoryScale()
{
    return max(_CaelixFrameSize.xy - 1.0f, float2(1.0f, 1.0f));
}

float3 CaelixUnpackNormal(float2 packedNormal)
{
    return UnpackNormalOctQuadEncode(packedNormal * 2.0f - 1.0f);
}

float CaelixLuminance(float3 color)
{
    return dot(color, float3(0.2126f, 0.7152f, 0.0722f));
}

// lowbias32 integer hash.
uint CaelixHashUint(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352dU;
    x ^= x >> 15;
    x *= 0x846ca68bU;
    x ^= x >> 16;
    return x;
}

#endif
