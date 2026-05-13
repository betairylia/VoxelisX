Shader "Hidden/VoxelisX/IndirectRadiancePipeline"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_IndirectRadianceTex);
        TEXTURE2D(_DirectRadianceTex);
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
        float _ConvergenceStep;
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

        float4 VoxelisXLoadPreviousIndirectPoint(float2 uv)
        {
            int2 coord = int2(round(uv * VoxelisXHistoryScale() - 0.5f));
            return LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, VoxelisXClampCoord(coord));
        }

        float4 VoxelisXLoadPreviousIndirectBilinear(float2 uv)
        {
            float2 historyCoord = uv * VoxelisXHistoryScale() - 0.5f;
            int2 baseCoord = (int2)floor(historyCoord);
            float2 blend = frac(historyCoord);

            float4 h00 = LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, VoxelisXClampCoord(baseCoord));
            float4 h10 = LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, VoxelisXClampCoord(baseCoord + int2(1, 0)));
            float4 h01 = LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, VoxelisXClampCoord(baseCoord + int2(0, 1)));
            float4 h11 = LOAD_TEXTURE2D(_PreviousIndirectRadianceHistoryTex, VoxelisXClampCoord(baseCoord + int2(1, 1)));

            return lerp(lerp(h00, h10, blend.x), lerp(h01, h11, blend.x), blend.y);
        }

        float VoxelisXLoadPreviousDepthPoint(float2 uv)
        {
            int2 coord = int2(round(uv * VoxelisXHistoryScale() - 0.5f));
            return LOAD_TEXTURE2D(_PreviousDepthHistoryTex, VoxelisXClampCoord(coord)).r;
        }

        float VoxelisXLoadPreviousDepthBilinear(float2 uv)
        {
            float2 historyCoord = uv * VoxelisXHistoryScale() - 0.5f;
            int2 baseCoord = (int2)floor(historyCoord);
            float2 blend = frac(historyCoord);

            float h00 = LOAD_TEXTURE2D(_PreviousDepthHistoryTex, VoxelisXClampCoord(baseCoord)).r;
            float h10 = LOAD_TEXTURE2D(_PreviousDepthHistoryTex, VoxelisXClampCoord(baseCoord + int2(1, 0))).r;
            float h01 = LOAD_TEXTURE2D(_PreviousDepthHistoryTex, VoxelisXClampCoord(baseCoord + int2(0, 1))).r;
            float h11 = LOAD_TEXTURE2D(_PreviousDepthHistoryTex, VoxelisXClampCoord(baseCoord + int2(1, 1))).r;

            return lerp(lerp(h00, h10, blend.x), lerp(h01, h11, blend.x), blend.y);
        }

        float4 VoxelisXLoadPreviousNormalPoint(float2 uv)
        {
            int2 coord = int2(round(uv * VoxelisXHistoryScale() - 0.5f));
            float4 packedNormal = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, VoxelisXClampCoord(coord));
            return float4(VoxelisXUnpackNormal(packedNormal.rg), packedNormal.a);
        }

        float4 VoxelisXLoadPreviousNormalBilinear(float2 uv)
        {
            float2 historyCoord = uv * VoxelisXHistoryScale() - 0.5f;
            int2 baseCoord = (int2)floor(historyCoord);
            float2 blend = frac(historyCoord);

            float4 h00 = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, VoxelisXClampCoord(baseCoord));
            float4 h10 = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, VoxelisXClampCoord(baseCoord + int2(1, 0)));
            float4 h01 = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, VoxelisXClampCoord(baseCoord + int2(0, 1)));
            float4 h11 = LOAD_TEXTURE2D(_PreviousNormalHistoryTex, VoxelisXClampCoord(baseCoord + int2(1, 1)));

            float4 packedNormal = lerp(lerp(h00, h10, blend.x), lerp(h01, h11, blend.x), blend.y);
            return float4(VoxelisXUnpackNormal(packedNormal.rg), packedNormal.a);
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
            if (_TemporalRadianceEnabled == 0 || _IndirectRadianceHistoryValid == 0 || currentIndirect.a <= 0.001f)
            {
                return currentIndirect;
            }

            float2 currentUV = (float2(coord) + 0.5f) / VoxelisXHistoryScale();
            float2 motionVector = LOAD_TEXTURE2D(_MotionVectorTex, coord).rg;
            float2 previousUV = currentUV - motionVector;
            bool canReproject =
                all(previousUV >= float2(0.0f, 0.0f)) &&
                all(previousUV <= float2(1.0f, 1.0f));

            if (!canReproject)
            {
                return currentIndirect;
            }

            float4 previousIndirect = (_TemporalRadianceBilinearHistory != 0)
                ? VoxelisXLoadPreviousIndirectBilinear(previousUV)
                : VoxelisXLoadPreviousIndirectPoint(previousUV);
            float previousDepth = (_TemporalRadianceBilinearHistory != 0)
                ? VoxelisXLoadPreviousDepthBilinear(previousUV)
                : VoxelisXLoadPreviousDepthPoint(previousUV);
            float4 previousNormal = (_TemporalRadianceBilinearHistory != 0)
                ? VoxelisXLoadPreviousNormalBilinear(previousUV)
                : VoxelisXLoadPreviousNormalPoint(previousUV);

            float currentDepth = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, coord).r;
            float4 currentNormalPacked = LOAD_TEXTURE2D(_CurrentNormalHistoryTex, coord);
            float3 currentNormal = VoxelisXUnpackNormal(currentNormalPacked.rg);

            float depthTolerance = max(_TemporalRadianceDepthTolerance, currentDepth * _TemporalRadianceRelativeDepthTolerance);
            bool depthAccept = (_TemporalRadianceDepthRejectionEnabled == 0) || (abs(previousDepth - currentDepth) <= depthTolerance);
            bool normalAccept = (_TemporalRadianceNormalRejectionEnabled == 0) || (previousNormal.a > 0.001f && dot(normalize(previousNormal.xyz), normalize(currentNormal)) > _TemporalRadianceNormalThreshold);

            if (previousIndirect.a <= 0.001f || !depthAccept || !normalAccept)
            {
                return currentIndirect;
            }

            float3 previousIncidentRadiance = previousIndirect.rgb / previousIndirect.a;
            float currentFrameWeight = max(_TemporalRadianceCurrentFrameMinWeight, 1.0f / (_ConvergenceStep + 1.0f));
            return float4(lerp(previousIncidentRadiance, currentIndirect.rgb, currentFrameWeight), currentIndirect.a);
        }

        float4 Composite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = VoxelisXPixelCoord(input.texcoord);
            float4 directRadiance = LOAD_TEXTURE2D(_DirectRadianceTex, coord);
            float4 albedo = LOAD_TEXTURE2D(_AlbedoTex, coord);
            float4 indirectRadiance = LOAD_TEXTURE2D(_AccumulatedIndirectRadianceTex, coord);

            float3 result = directRadiance.rgb + albedo.rgb * indirectRadiance.rgb;
            // float3 result = indirectRadiance.rgb;
            return float4(result, directRadiance.a);
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
    }
}
