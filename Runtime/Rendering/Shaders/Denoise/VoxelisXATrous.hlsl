#ifndef VOXELISX_ATROUS_INCLUDED
#define VOXELISX_ATROUS_INCLUDED

// Edge-aware a-trous wavelet filter (SVGF-lite, no variance texture).
//
// The signal to filter is named by VOXELISX_ATROUS_SIGNAL_TEX, which the including shader must
// declare as a TEXTURE2D before including this file. Signal convention, identical for the path
// tracer's radiance and the budget mode's AO/shadow:
//   .rgb = the quantity being filtered   .a = validity (<= 0.001 means "no data, pass through")
//
// Geometry guides (_NormalTex, _CurrentDepthHistoryTex) come from VoxelisXDenoiseCommon.hlsl.

#include "VoxelisXDenoiseCommon.hlsl"

#ifndef VOXELISX_ATROUS_SIGNAL_TEX
#define VOXELISX_ATROUS_SIGNAL_TEX _IndirectRadianceTex
#endif

int _ATrousStepWidth;
int _ATrousUseFaceHash;
int _ATrousJitterTaps;
int _ATrousFrameIndex;
float _ATrousNormalPower;
float _ATrousDepthSigma;
float _ATrousRelativeDepthSigma;
float _ATrousRadianceSigma;

float VoxelisXATrousKernel(int offset)
{
    offset = abs(offset);
    if (offset == 0)
    {
        return 0.375f;
    }

    if (offset == 1)
    {
        return 0.25f;
    }

    return 0.0625f;
}

float4 VoxelisXATrousFilter(uint2 centerCoord)
{
    float4 centerSignal = LOAD_TEXTURE2D(VOXELISX_ATROUS_SIGNAL_TEX, centerCoord);
    if (centerSignal.a <= 0.001f)
    {
        return centerSignal;
    }

    float4 centerNormalPacked = LOAD_TEXTURE2D(_NormalTex, centerCoord);
    float centerFaceHash = round(centerNormalPacked.b);
    float3 centerNormal = normalize(VoxelisXUnpackNormal(centerNormalPacked.rg));
    float centerDepth = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, centerCoord).r;
    float centerLuminance = VoxelisXLuminance(centerSignal.rgb);

    int stepWidth = max(_ATrousStepWidth, 1);

    // Screen-space depth gradient (world units per pixel) makes the depth weight
    // slope-aware: surfaces seen at an angle tolerate the depth change their own
    // slope produces instead of rejecting their whole neighborhood. The smaller
    // one-sided difference is used so a silhouette on one side does not inflate
    // the gradient and let taps leak across the edge.
    float depthRight = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, VoxelisXClampCoord(int2(centerCoord) + int2(1, 0))).r;
    float depthLeft = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, VoxelisXClampCoord(int2(centerCoord) - int2(1, 0))).r;
    float depthUp = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, VoxelisXClampCoord(int2(centerCoord) + int2(0, 1))).r;
    float depthDown = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, VoxelisXClampCoord(int2(centerCoord) - int2(0, 1))).r;
    float2 depthGradient;
    depthGradient.x = abs(depthRight - centerDepth) < abs(centerDepth - depthLeft)
        ? depthRight - centerDepth
        : centerDepth - depthLeft;
    depthGradient.y = abs(depthUp - centerDepth) < abs(centerDepth - depthDown)
        ? depthUp - centerDepth
        : centerDepth - depthDown;

    // The first iteration (step 1) skips the luminance weight entirely: at raw
    // 1spp the luminance channel IS the noise (no real edge information), and an
    // unweighted B3 kernel has an exact zero at the Nyquist frequency, which is
    // where STBN concentrates its energy (checkerboard pattern). Any luminance
    // weighting at step 1 breaks that zero and lets the checkerboard survive --
    // later iterations sample at even strides (same parity) and can never
    // remove it. Geometry (normal/depth) weights still protect real edges.
    bool useLuminanceWeight = stepWidth > 1;

    float radianceDenom = 1.0f;
    if (useLuminanceWeight)
    {
        // Local luminance std-dev (3x3 at the current step spacing) normalizes
        // the radiance edge-stopping weight. Without it, sun-disk HDR outliers
        // next to near-zero pixels reject their own smoothing.
        float momentSum = 0.0f;
        float momentSqSum = 0.0f;
        float momentCount = 0.0f;

        [unroll]
        for (int my = -1; my <= 1; my++)
        {
            [unroll]
            for (int mx = -1; mx <= 1; mx++)
            {
                uint2 momentCoord = VoxelisXClampCoord(int2(centerCoord) + int2(mx, my) * stepWidth);
                float4 momentSignal = LOAD_TEXTURE2D(VOXELISX_ATROUS_SIGNAL_TEX, momentCoord);
                if (momentSignal.a <= 0.001f)
                {
                    continue;
                }

                float momentLuminance = VoxelisXLuminance(momentSignal.rgb);
                momentSum += momentLuminance;
                momentSqSum += momentLuminance * momentLuminance;
                momentCount += 1.0f;
            }
        }

        float luminanceStdDev = 0.0f;
        if (momentCount > 0.5f)
        {
            float luminanceMean = momentSum / momentCount;
            luminanceStdDev = sqrt(max(momentSqSum / momentCount - luminanceMean * luminanceMean, 0.0f));
        }

        radianceDenom = _ATrousRadianceSigma * luminanceStdDev + 0.01f;
    }

    // Sparse iterations (step > 1) jitter every non-center tap inside its own
    // step-sized cell (per pixel, per tap, per frame). This breaks the parity
    // lock of the a-trous hole pattern (structured checkerboard / grid-dot
    // artifacts around HDR outliers); the stochastic residue is unstructured
    // and averages out in temporal accumulation. stepWidth is a power of two.
    bool applyJitter = _ATrousJitterTaps != 0 && stepWidth > 1;
    uint jitterMask = (uint)(stepWidth - 1);
    int jitterHalf = stepWidth >> 1;
    uint pixelSeed = VoxelisXHashUint(
        centerCoord.x ^ (centerCoord.y << 16)
        ^ ((uint)_ATrousFrameIndex * 0x68bc21ebU)
        ^ ((uint)stepWidth * 0x02e5be93U));

    float3 signalSum = 0.0f;
    float weightSum = 0.0f;

    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            int2 sampleOffset = int2(x, y) * stepWidth;
            if (applyJitter && (x != 0 || y != 0))
            {
                uint tapHash = VoxelisXHashUint(pixelSeed + (uint)((y + 2) * 5 + (x + 2)));
                sampleOffset += int2((int)(tapHash & jitterMask), (int)((tapHash >> 16) & jitterMask)) - jitterHalf;
            }

            uint2 sampleCoord = VoxelisXClampCoord(int2(centerCoord) + sampleOffset);

            float4 sampleSignal = LOAD_TEXTURE2D(VOXELISX_ATROUS_SIGNAL_TEX, sampleCoord);
            if (sampleSignal.a <= 0.001f)
            {
                continue;
            }

            float4 sampleNormalPacked = LOAD_TEXTURE2D(_NormalTex, sampleCoord);
            float sampleFaceHash = round(sampleNormalPacked.b);
            if (_ATrousUseFaceHash != 0 && sampleFaceHash != centerFaceHash)
            {
                continue;
            }

            float3 sampleNormal = normalize(VoxelisXUnpackNormal(sampleNormalPacked.rg));
            float normalWeight = pow(saturate(dot(centerNormal, sampleNormal)), _ATrousNormalPower);

            float sampleDepth = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, sampleCoord).r;
            float depthDenom = _ATrousDepthSigma * abs(dot(depthGradient, float2(sampleOffset)))
                + max(_ATrousRelativeDepthSigma * abs(centerDepth), 0.001f);
            float depthWeight = exp(-abs(sampleDepth - centerDepth) / depthDenom);

            float radianceWeight = 1.0f;
            if (useLuminanceWeight)
            {
                float sampleLuminance = VoxelisXLuminance(sampleSignal.rgb);
                radianceWeight = exp(-abs(sampleLuminance - centerLuminance) / radianceDenom);
            }

            float kernelWeight = VoxelisXATrousKernel(x) * VoxelisXATrousKernel(y);
            float weight = kernelWeight * normalWeight * depthWeight * radianceWeight;

            signalSum += sampleSignal.rgb * weight;
            weightSum += weight;
        }
    }

    if (weightSum <= 0.0001f)
    {
        return centerSignal;
    }

    return float4(signalSum / weightSum, centerSignal.a);
}

#endif
