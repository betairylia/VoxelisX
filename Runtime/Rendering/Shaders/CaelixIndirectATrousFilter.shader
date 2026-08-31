Shader "Hidden/Caelix/IndirectATrousFilter"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // The signal this chain filters. CAELIX_ATROUS_SIGNAL_TEX defaults to it, so the
        // kernel below picks it up without any further wiring.
        TEXTURE2D(_IndirectRadianceTex);

        #include "Denoise/CaelixATrous.hlsl"

        float4 ATrousFilter(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return CaelixATrousFilter(CaelixPixelCoord(input.texcoord));
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
