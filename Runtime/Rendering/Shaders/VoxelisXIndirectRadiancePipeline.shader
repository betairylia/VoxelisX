Shader "Hidden/VoxelisX/IndirectRadiancePipeline"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_IndirectRadianceTex);
        TEXTURE2D(_DeterministicRadianceTex);
        TEXTURE2D(_DiffuseRadianceTex);
        TEXTURE2D(_SpecularRadianceTex);
        TEXTURE2D(_AlbedoTex);
        TEXTURE2D(_NormalTex);
        TEXTURE2D(_MotionVectorTex);
        TEXTURE2D(_CurrentDepthHistoryTex);
        TEXTURE2D(_PreviousDepthHistoryTex);
        TEXTURE2D(_CurrentNormalHistoryTex);
        TEXTURE2D(_PreviousNormalHistoryTex);
        TEXTURE2D(_PreviousIndirectRadianceHistoryTex);
        TEXTURE2D(_AccumulatedIndirectRadianceTex);

        float4 _VoxelisXFrameSize;
        int _SpatialFilterEnabled;
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
        int _SeparableFilterRadius;
        float _SeparableFilterDistanceSigma;

        uint2 VoxelisXPixelCoord(float2 uv)
        {
            uint2 size = max(uint2(_VoxelisXFrameSize.xy), uint2(1, 1));
            return min((uint2)(uv * size), size - uint2(1, 1));
        }

        uint2 VoxelisXClampCoord(int2 coord)
        {
            uint2 size = max(uint2(_VoxelisXFrameSize.xy), uint2(1, 1));
            int2 maxCoord = int2(size) - int2(1, 1);
            return (uint2)clamp(coord, int2(0, 0), maxCoord);
        }

        float2 VoxelisXHistoryScale()
        {
            return max(_VoxelisXFrameSize.xy - 1.0f, float2(1.0f, 1.0f));
        }

        float3 VoxelisXUnpackNormal(float2 packedNormal)
        {
            return UnpackNormalOctQuadEncode(packedNormal * 2.0f - 1.0f);
        }

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

        float4 SpatialFilter(float2 uv, int2 filterDirection)
        {
            uint2 centerCoord = VoxelisXPixelCoord(uv);
            float4 centerIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, centerCoord);
            if (_SpatialFilterEnabled == 0 || centerIndirect.a <= 0.001f)
            {
                return centerIndirect;
            }

            float4 centerNormalPacked = LOAD_TEXTURE2D(_NormalTex, centerCoord);
            float centerFaceHash = round(centerNormalPacked.b);
            if (centerFaceHash <= 0.0f)
            {
                return centerIndirect;
            }

            float3 radianceSum = centerIndirect.rgb;
            float weightSum = 1.0f;

            [unroll]
            for (int offset = -7; offset <= 7; offset++)
            {
                if (offset == 0 || abs(offset) > _SeparableFilterRadius)
                {
                    continue;
                }

                uint2 sampleCoord = VoxelisXClampCoord(int2(centerCoord) + filterDirection * offset);
                float4 sampleIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, sampleCoord);
                float4 sampleNormalPacked = LOAD_TEXTURE2D(_NormalTex, sampleCoord);
                float sampleFaceHash = round(sampleNormalPacked.b);

                bool accept =
                    sampleIndirect.a > 0.001f &&
                    sampleFaceHash == centerFaceHash;

                if (accept)
                {
                    float distance = abs(float(offset));
                    float weight = exp(-(distance * distance) / max(_SeparableFilterDistanceSigma, 0.0001f));
                    radianceSum += sampleIndirect.rgb * weight;
                    weightSum += weight;
                }
            }

            return float4(radianceSum / max(weightSum, 0.0001f), centerIndirect.a);
        }

        float4 SpatialFilterX(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return SpatialFilter(input.texcoord, int2(1, 0));
        }

        float4 SpatialFilterY(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return SpatialFilter(input.texcoord, int2(0, 1));
        }

        float4 TemporalAccumulation(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = VoxelisXPixelCoord(input.texcoord);
            float4 currentIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, coord);
            float currentValid = currentIndirect.a > 0.001f ? 1.0f : 0.0f;
            if (_TemporalRadianceEnabled == 0 || _IndirectRadianceHistoryValid == 0 || currentValid == 0.0f)
            {
                return float4(currentIndirect.rgb, currentValid);
            }

            float2 currentUV = (float2(coord) + 0.5f) / VoxelisXHistoryScale();
            float2 motionVector = LOAD_TEXTURE2D(_MotionVectorTex, coord).rg;
            float2 previousUV = currentUV - motionVector;
            bool canReproject =
                all(previousUV >= float2(0.0f, 0.0f)) &&
                all(previousUV <= float2(1.0f, 1.0f));

            if (!canReproject)
            {
                return float4(currentIndirect.rgb, 1.0f);
            }

            float4 currentNormalHistory = LOAD_TEXTURE2D(_CurrentNormalHistoryTex, coord);
            float3 currentNormal = normalize(VoxelisXUnpackNormal(currentNormalHistory.rg));
            float expectedPreviousDepth = currentNormalHistory.b;

            float2 historyCoord = previousUV * VoxelisXHistoryScale() - 0.5f;

            float3 historyRadiance = 0.0f;
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

                    historyRadiance += history.rgb * tapWeights[tap];
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
                    historyRadiance = history.rgb;
                    historyFrames = history.a;
                    weightSum = 1.0f;
                }
            }

            if (weightSum <= 0.0001f)
            {
                return float4(currentIndirect.rgb, 1.0f);
            }

            historyRadiance /= weightSum;
            historyFrames = min(historyFrames / weightSum, max(_TemporalRadianceMaxFrames - 1.0f, 0.0f));

            float currentFrameWeight = max(_TemporalRadianceCurrentFrameMinWeight, 1.0f / (historyFrames + 1.0f));
            float3 blended = lerp(historyRadiance, currentIndirect.rgb, currentFrameWeight);
            return float4(blended, historyFrames + 1.0f);
        }

        float4 Composite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = VoxelisXPixelCoord(input.texcoord);
            float4 deterministicRadiance = LOAD_TEXTURE2D(_DeterministicRadianceTex, coord);
            float4 albedo = LOAD_TEXTURE2D(_AlbedoTex, coord);
            float4 indirectRadiance = LOAD_TEXTURE2D(_AccumulatedIndirectRadianceTex, coord);

            float3 result = deterministicRadiance.rgb + albedo.rgb * indirectRadiance.rgb;
            // float3 result = indirectRadiance.rgb;
            return float4(result, deterministicRadiance.a);
        }

        // Sums the split stochastic targets back into the single combined signal the legacy
        // spatial/temporal chain consumes. The split targets carry hit distance in .a, so the
        // chain's validity flag (alpha) is re-derived from the deterministic target's hit/miss
        // alpha instead.
        float4 CombineStochastic(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = VoxelisXPixelCoord(input.texcoord);
            float3 diffuse = LOAD_TEXTURE2D(_DiffuseRadianceTex, coord).rgb;
            float3 specular = LOAD_TEXTURE2D(_SpecularRadianceTex, coord).rgb;
            float validity = LOAD_TEXTURE2D(_DeterministicRadianceTex, coord).a > 0.001f ? 1.0f : 0.0f;
            return float4(diffuse + specular, validity);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "SpatialFilterX"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SpatialFilterX
            ENDHLSL
        }

        Pass
        {
            Name "SpatialFilterY"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SpatialFilterY
            ENDHLSL
        }

        Pass
        {
            Name "TemporalAccumulation"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment TemporalAccumulation
            ENDHLSL
        }

        Pass
        {
            Name "Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }

        Pass
        {
            Name "CombineStochastic"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CombineStochastic
            ENDHLSL
        }
    }
}
