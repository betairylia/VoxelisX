Shader "VoxelisX/PostFlip"
{
    HLSLINCLUDE
    
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // The Blit.hlsl file provides the vertex shader (Vert),
        // the input structure (Attributes), and the output structure (Varyings)
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // TEXTURE2D(_ColorTex);
        // SAMPLER(samlper_ColorTex);
        TEXTURE2D(_DepthTex);
        SAMPLER(sampler_DepthTex);
            
        float4 Flip (Varyings input, out float outDepth : SV_Depth) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = input.texcoord;
            
            float4 color = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);
            if(color.a < 0.01f)
            {
                clip(-1);
            }

            outDepth = SAMPLE_TEXTURE2D(_DepthTex, sampler_LinearClamp, uv).r;

            return color;
        }
    
    ENDHLSL
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZWrite On Cull Off
        
        Pass
        {
            Name "BlurPassVertical"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Flip
            
            ENDHLSL
        }
    }
}
