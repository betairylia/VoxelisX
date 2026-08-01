#ifndef VOXELISX_TEMPORAL_INCLUDED
#define VOXELISX_TEMPORAL_INCLUDED

// Temporal accumulation against last frame's reprojected history.
//
// The current-frame signal is named by VOXELISX_TEMPORAL_SIGNAL_TEX, which the including shader
// must declare as a TEXTURE2D before including this file. The history half is always the
// double-buffered VoxelisXCameraHistory target, so its uniform names are fixed here.
//
// Signal convention on the way in:  .rgb = quantity,   .a = validity (> 0.001 means "has data").
// On the way out and in history:    .rgb = quantity,   .a = accumulated frame count (>= 1).

#include "VoxelisXDenoiseCommon.hlsl"

#ifndef VOXELISX_TEMPORAL_SIGNAL_TEX
#define VOXELISX_TEMPORAL_SIGNAL_TEX _IndirectRadianceTex
#endif

TEXTURE2D(_PreviousIndirectRadianceHistoryTex);
TEXTURE2D(_PreviousDepthHistoryTex);
TEXTURE2D(_PreviousNormalHistoryTex);

int _IndirectRadianceHistoryValid;
int _TemporalRadianceEnabled;
int _TemporalRadianceBilinearHistory;
int _TemporalRadianceDepthRejectionEnabled;
int _TemporalRadianceNormalRejectionEnabled;
float _TemporalRadianceCurrentFrameMinWeight;
float _TemporalRadianceDepthTolerance;
float _TemporalRadianceRelativeDepthTolerance;
float _TemporalRadianceNormalThreshold;
float _TemporalRadianceMaxFrames;

// History alpha stores the per-pixel accumulated frame count (>= 1 when valid).
// A tap is only blended in when its stored previous-frame depth matches the
// depth this surface is *expected* to have had last frame (computed by the
// raygen alongside motion vectors), which stays exact under camera motion.
bool VoxelisXValidateHistoryTap(
    uint2 tapCoord,
    float expectedPreviousDepth,
    float3 currentNormal,
    out float4 history)
{
    history = LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, tapCoord);
    if (history.a <= 0.5f)
    {
        return false;
    }

    if (_TemporalRadianceDepthRejectionEnabled != 0)
    {
        float previousDepth = LOAD_TEXTURE2D(_PreviousDepthHistoryTex, tapCoord).r;
        float tolerance = max(_TemporalRadianceDepthTolerance, abs(expectedPreviousDepth) * _TemporalRadianceRelativeDepthTolerance);
        if (abs(previousDepth - expectedPreviousDepth) > tolerance)
        {
            return false;
        }
    }

    if (_TemporalRadianceNormalRejectionEnabled != 0)
    {
        float4 previousNormalPacked = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, tapCoord);
        if (previousNormalPacked.a <= 0.001f)
        {
            return false;
        }

        float3 previousNormal = normalize(VoxelisXUnpackNormal(previousNormalPacked.rg));
        if (dot(previousNormal, currentNormal) < _TemporalRadianceNormalThreshold)
        {
            return false;
        }
    }

    return true;
}

float4 VoxelisXTemporalAccumulate(uint2 coord)
{
    float4 currentSignal = LOAD_TEXTURE2D(VOXELISX_TEMPORAL_SIGNAL_TEX, coord);
    float currentValid = currentSignal.a > 0.001f ? 1.0f : 0.0f;
    if (_TemporalRadianceEnabled == 0 || _IndirectRadianceHistoryValid == 0 || currentValid == 0.0f)
    {
        return float4(currentSignal.rgb, currentValid);
    }

    float2 currentUV = (float2(coord) + 0.5f) / VoxelisXHistoryScale();
    // Motion is stored previous-minus-current (NRD convention), so reprojection adds it.
    float2 motionVector = LOAD_TEXTURE2D(_MotionVectorTex, coord).rg;
    float2 previousUV = currentUV + motionVector;
    bool canReproject =
        all(previousUV >= float2(0.0f, 0.0f)) &&
        all(previousUV <= float2(1.0f, 1.0f));

    if (!canReproject)
    {
        return float4(currentSignal.rgb, 1.0f);
    }

    float4 currentNormalHistory = LOAD_TEXTURE2D(_CurrentNormalHistoryTex, coord);
    float3 currentNormal = normalize(VoxelisXUnpackNormal(currentNormalHistory.rg));
    float expectedPreviousDepth = currentNormalHistory.b;

    float2 historyCoord = previousUV * VoxelisXHistoryScale() - 0.5f;

    float3 historySignal = 0.0f;
    float historyFrames = 0.0f;
    float weightSum = 0.0f;

    if (_TemporalRadianceBilinearHistory != 0)
    {
        // Manual bilinear with per-tap validation: taps that fail the
        // depth/normal tests drop out and the remaining weights renormalize,
        // so history never blends across silhouettes.
        int2 baseCoord = (int2)floor(historyCoord);
        float2 blend = frac(historyCoord);

        const int2 tapOffsets[4] = { int2(0, 0), int2(1, 0), int2(0, 1), int2(1, 1) };
        float tapWeights[4] = {
            (1.0f - blend.x) * (1.0f - blend.y),
            blend.x * (1.0f - blend.y),
            (1.0f - blend.x) * blend.y,
            blend.x * blend.y
        };

        [unroll]
        for (int tap = 0; tap < 4; tap++)
        {
            if (tapWeights[tap] <= 0.0001f)
            {
                continue;
            }

            uint2 tapCoord = VoxelisXClampCoord(baseCoord + tapOffsets[tap]);
            float4 history;
            if (!VoxelisXValidateHistoryTap(tapCoord, expectedPreviousDepth, currentNormal, history))
            {
                continue;
            }

            historySignal += history.rgb * tapWeights[tap];
            historyFrames += history.a * tapWeights[tap];
            weightSum += tapWeights[tap];
        }
    }
    else
    {
        uint2 tapCoord = VoxelisXClampCoord((int2)round(historyCoord));
        float4 history;
        if (VoxelisXValidateHistoryTap(tapCoord, expectedPreviousDepth, currentNormal, history))
        {
            historySignal = history.rgb;
            historyFrames = history.a;
            weightSum = 1.0f;
        }
    }

    if (weightSum <= 0.0001f)
    {
        return float4(currentSignal.rgb, 1.0f);
    }

    historySignal /= weightSum;
    historyFrames = min(historyFrames / weightSum, max(_TemporalRadianceMaxFrames - 1.0f, 0.0f));

    float currentFrameWeight = max(_TemporalRadianceCurrentFrameMinWeight, 1.0f / (historyFrames + 1.0f));
    float3 blended = lerp(historySignal, currentSignal.rgb, currentFrameWeight);
    return float4(blended, historyFrames + 1.0f);
}

#endif
