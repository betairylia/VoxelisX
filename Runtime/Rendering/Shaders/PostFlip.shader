Shader "VoxelisX/PostFlip"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        // The Blit.hlsl file provides the vertex shader (Vert),
        // the input structure (Attributes), and the output structure (Varyings)
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // Camera depth always comes from the G-buffer depth target, whatever is being displayed,
        // so scene geometry keeps depth-testing correctly against a debug view.
        TEXTURE2D(_DepthTex);
        SAMPLER(sampler_DepthTex);

        // Must match the VoxelisXDebugView enum (VoxelisXRenderSettings.cs). The present stage binds
        // the matching buffer as _BlitTexture, so this only selects how to decode it.
        #define VOXELISX_DEBUG_REGULAR                       0
        #define VOXELISX_DEBUG_MOTION_VECTOR                 1
        #define VOXELISX_DEBUG_ALBEDO                        2
        #define VOXELISX_DEBUG_NORMAL                        3
        #define VOXELISX_DEBUG_DEPTH                         4
        #define VOXELISX_DEBUG_DIRECT_RADIANCE               5
        #define VOXELISX_DEBUG_INDIRECT_RADIANCE_RAW         6
        #define VOXELISX_DEBUG_INDIRECT_RADIANCE_FILTERED    7
        #define VOXELISX_DEBUG_INDIRECT_RADIANCE_ACCUMULATED 8

        int _DebugView;

        float4 Flip (Varyings input, out float outDepth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = input.texcoord;

            float4 source = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);
            outDepth = SAMPLE_TEXTURE2D(_DepthTex, sampler_LinearClamp, uv).r;

            if (_DebugView == VOXELISX_DEBUG_MOTION_VECTOR)
            {
                float2 encodedMotion = source.rg * 64.0f;
                return float4(
                    saturate(0.5f + encodedMotion.x),
                    saturate(0.5f + encodedMotion.y),
                    saturate(0.5f - max(abs(encodedMotion.x), abs(encodedMotion.y))),
                    1.0f);
            }

            if (_DebugView == VOXELISX_DEBUG_NORMAL)
            {
                // Normals are stored octahedral-packed in .xy over [0,1].
                float3 normal = UnpackNormalOctQuadEncode(source.xy * 2.0f - 1.0f);
                return float4(normal * 0.5f + 0.5f, 1.0f);
            }

            if (_DebugView == VOXELISX_DEBUG_DEPTH)
            {
                // Raw clip-space depth; on reversed-Z near surfaces read bright.
                return float4(source.rrr, 1.0f);
            }

            if (_DebugView != VOXELISX_DEBUG_REGULAR)
            {
                // Albedo and the radiance buffers are all plain colour; show them opaque so empty
                // regions read as black instead of letting the scene behind show through.
                return float4(source.rgb, 1.0f);
            }

            if (source.a < 0.01f)
            {
                clip(-1);
            }

            return source;
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZWrite On Cull Off

        Pass
        {
            Name "VoxelisXPresent"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Flip

            ENDHLSL
        }
    }
}
