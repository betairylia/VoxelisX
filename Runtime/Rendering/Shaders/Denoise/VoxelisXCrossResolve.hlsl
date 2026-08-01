#ifndef VOXELISX_CROSS_RESOLVE_INCLUDED
#define VOXELISX_CROSS_RESOLVE_INCLUDED

// Resolves the reflect/refract checkerboard the tracer writes at the first delta interface:
// even and odd pixels traced different delta branches, so the two parities have to be averaged
// back together. Shared by both render paths -- they split the delta identically, so they
// resolve it identically. Source is _BlitTexture (the composited colour).
//
// This runs on the *composited* colour and not on any G-buffer, because the split reaches
// every one of them -- albedo, normal, depth and motion all describe a different surface on
// each parity -- and only the final colour has collapsed them into one value.
//
// It is also the one filter here that must NOT be edge-aware. Every guide the other passes
// use (face hash, depth, normal) would reject exactly the taps this needs, since the
// neighbour genuinely IS a different surface. So the cross blends unconditionally and keys
// off the checkerboard flag alone; pixels without it pass through untouched rather than
// being softened.
//
// 0.5 * center + 0.125 * each of the 4 orthogonal neighbours puts exactly half the total
// weight on each parity -- which is what the x2 Fresnel weight in the delta split is scaled
// to cancel. Diagonals are deliberately excluded: they share the center's parity and would
// bias the mix back toward the branch this pixel already traced.

#include "VoxelisXDenoiseCommon.hlsl"

float4 VoxelisXCrossResolve(uint2 centerCoord)
{
    float4 center = LOAD_TEXTURE2D_X(_BlitTexture, centerCoord);

    if (LOAD_TEXTURE2D(_MotionVectorTex, centerCoord).a <= 0.5f)
    {
        return center;
    }

    float3 radianceSum = center.rgb * 0.5f;
    float weightSum = 0.5f;

    const int2 tapOffsets[4] = { int2(-1, 0), int2(1, 0), int2(0, -1), int2(0, 1) };

    [unroll]
    for (int tap = 0; tap < 4; tap++)
    {
        uint2 tapCoord = VoxelisXClampCoord(int2(centerCoord) + tapOffsets[tap]);
        if (LOAD_TEXTURE2D(_MotionVectorTex, tapCoord).a <= 0.5f)
        {
            continue;
        }

        radianceSum += LOAD_TEXTURE2D_X(_BlitTexture, tapCoord).rgb * 0.125f;
        weightSum += 0.125f;
    }

    // Renormalizing keeps the mix at 50/50 when some taps drop out, and degenerates to a
    // plain passthrough for an isolated flagged pixel with no flagged neighbours.
    // Alpha is carried through untouched: the present stage clips on it.
    return float4(radianceSum / weightSum, center.a);
}

#endif
