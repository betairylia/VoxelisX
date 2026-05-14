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

    // return float2(0, 1);
    // return float2(0.001f, 0.999f);
    return stbnTexture[pos] * 0.99609375f + 0.001953125f;
    // return stbnTexture[pos];
    // return stbnTexture[pos] + EPSILON;
    // return ((stbnTexture[pos]) + 0.5) / 256.0;
    // return (stbnTexture[pos] == 0) * 1.0;
}

// uint WangHash(inout uint seed)
// {
//     seed = (seed ^ 61) ^ (seed >> 16);
//     seed *= 9;
//     seed = seed ^ (seed >> 4);
//     seed *= 0x27d4eb2d;
//     seed = seed ^ (seed >> 15);
//     return seed;
// }
//
// float RandomFloat01(inout uint seed)
// {
//     return float(WangHash(seed)) / float(0xFFFFFFFF);
// }
//
// float3 RandomUnitVector(inout uint state)
// {
//     float z = RandomFloat01(state) * 2.0f - 1.0f;
//     float a = RandomFloat01(state) * K_TWO_PI;
//     float r = sqrt(1.0f - z * z);
//     float x = r * cos(a);
//     float y = r * sin(a);
//     return float3(x, y, z);
// }

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
