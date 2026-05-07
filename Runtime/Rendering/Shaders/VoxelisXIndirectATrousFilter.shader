Shader "Hidden/VoxelisX/IndirectATrousFilter"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_IndirectRadianceTex);
        TEXTURE2D(_NormalTex);
        TEXTURE2D(_CurrentDepthHistoryTex);

        float4 _VoxelisXFrameSize;
        int _ATrousStepWidth;
        int _ATrousUseFaceHash;
        float _ATrousNormalPower;
        float _ATrousDepthSigma;
        float _ATrousRelativeDepthSigma;
        float _ATrousRadianceSigma;

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

        float3 VoxelisXUnpackNormal(float2 packedNormal)
        {
            return UnpackNormalOctQuadEncode(packedNormal * 2.0f - 1.0f);
        }

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

        float VoxelisXLuminance(float3 color)
        {
            return dot(color, float3(0.2126f, 0.7152f, 0.0722f));
        }

        float4 ATrousFilter(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 centerCoord = VoxelisXPixelCoord(input.texcoord);
            float4 centerIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, centerCoord);
            if (centerIndirect.a <= 0.001f)
            {
                return centerIndirect;
            }

            float4 centerNormalPacked = LOAD_TEXTURE2D(_NormalTex, centerCoord);
            float centerFaceHash = round(centerNormalPacked.b);
            float3 centerNormal = normalize(VoxelisXUnpackNormal(centerNormalPacked.rg));
            float centerDepth = LOAD_TEXTURE2D(_CurrentDepthHistoryTex, centerCoord).r;

            float3 radianceSum = 0.0f;
            float weightSum = 0.0f;
            int stepWidth = max(_ATrousStepWidth, 1);

            [unroll]
            for (int y = -2; y <= 2; y++)
            {
                [unroll]
                for (int x = -2; x <= 2; x++)
                {
                    int2 sampleOffset = int2(x, y) * stepWidth;
                    uint2 sampleCoord = VoxelisXClampCoord(int2(centerCoord) + sampleOffset);

                    float4 sampleIndirect = LOAD_TEXTURE2D(_IndirectRadianceTex, sampleCoord);
                    if (sampleIndirect.a <= 0.001f)
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
                    float depthSigma = max(_ATrousDepthSigma, abs(centerDepth) * _ATrousRelativeDepthSigma);
                    float depthWeight = exp(-abs(sampleDepth - centerDepth) / max(depthSigma, 0.0001f));

                    float radianceDelta = VoxelisXLuminance(abs(sampleIndirect.rgb - centerIndirect.rgb));
                    float radianceWeight = exp(-(radianceDelta * radianceDelta) / max(_ATrousRadianceSigma * _ATrousRadianceSigma, 0.0001f));

                    float kernelWeight = VoxelisXATrousKernel(x) * VoxelisXATrousKernel(y);
                    float weight = kernelWeight * normalWeight * depthWeight * radianceWeight;

                    radianceSum += sampleIndirect.rgb * weight;
                    weightSum += weight;
                }
            }

            return float4(radianceSum / max(weightSum, 0.0001f), centerIndirect.a);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "ATrousFilter"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ATrousFilter
            ENDHLSL
        }
    }
}
