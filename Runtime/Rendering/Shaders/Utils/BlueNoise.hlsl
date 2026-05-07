#ifndef VOXELISX_BLUE_NOISE
#define VOXELISX_BLUE_NOISE

#define EPSILON 0.00390625;

// https://developer.nvidia.com/blog/rendering-in-real-time-with-spatiotemporal-blue-noise-textures-part-1/
// Texture2D<uint2> stbnTexture;
Texture2D<half2> stbnTexture;
uint g_FrameIndex;

float2 MartinR2(uint index)
{
    return frac(index * float2(0.75487766624669276005, 0.56984029099805326591) + 0.5);
}

float2 SampleBlueNoise(inout uint state)
{
    float2 offset = MartinR2(state++);
    int2 pos = (DispatchRaysIndex().xy + int2(offset * 128)) & 127;
    pos.y += ((g_FrameIndex) & 63) * 128;

    // return stbnTexture[pos];
    return stbnTexture[pos] + EPSILON;
    // return ((stbnTexture[pos]) + 0.5) / 256.0;
    // return (stbnTexture[pos] == 0) * 1.0;
}

float RandomFloat01(inout uint state)
{
    return SampleBlueNoise(state).x;
}

float3 RandomUnitVector(float2 sample)
{
    float a = sample.x * K_TWO_PI;
    float y = sample.y * 2 - 1;
    float sy = sqrt(1.0 - y * y);
    return float3(sin(a) * sy, y, cos(a)* sy);
}

float3 RandomUnitVector(inout uint state)
{
    return RandomUnitVector(SampleBlueNoise(state));
}

#endif
