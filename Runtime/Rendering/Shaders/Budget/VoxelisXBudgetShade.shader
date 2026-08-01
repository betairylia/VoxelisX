Shader "Hidden/VoxelisX/BudgetShade"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // The signal this chain denoises: .r = AO, .g = sun visibility, .a = validity.
        // One global name, rebound after every filter pass (SetGlobalTextureAfterPass), so the
        // shared a-trous and temporal kernels both read "the current state of the signal".
        TEXTURE2D(_BudgetAOShadowTex);

        #define VOXELISX_ATROUS_SIGNAL_TEX _BudgetAOShadowTex
        #define VOXELISX_TEMPORAL_SIGNAL_TEX _BudgetAOShadowTex

        #include "../Denoise/VoxelisXATrous.hlsl"
        #include "../Denoise/VoxelisXTemporal.hlsl"
        #include "../Denoise/VoxelisXCrossResolve.hlsl"

        TEXTURE2D(_BudgetAOShadowAccumulatedTex);
        TEXTURE2D(_BudgetSurfaceTex);
        TEXTURE2D(_AlbedoTex);
        TEXTURE2D(_DeterministicRadianceTex);

        TEXTURECUBE(_BudgetSkyTex);
        SAMPLER(sampler_BudgetSkyTex);

        float4 _BudgetMainLightColor;
        float3 _BudgetMainLightDirection;
        float _BudgetSkyIntensity;
        float _BudgetSkyDiffuseMip;
        float _BudgetSkySpecularMaxMip;
        float _BudgetAOStrength;
        float _BudgetAOAffectsSpecular;

        float3 VoxelisXBudgetSampleSky(float3 direction, float mip)
        {
            return SAMPLE_TEXTURECUBE_LOD(_BudgetSkyTex, sampler_BudgetSkyTex, direction, mip).rgb * _BudgetSkyIntensity;
        }

        // URP's analytic GGX (the non-HQ DirectBRDFSpecular path), returning D * Vis * F.
        // `linearRoughness` is perceptual roughness, i.e. 1 - smoothness.
        float3 VoxelisXBudgetDirectSpecular(float3 N, float3 V, float3 L, float linearRoughness, float3 f0)
        {
            float3 H = SafeNormalize(L + V);
            float NoH = saturate(dot(N, H));
            float LoH = saturate(dot(L, H));

            float roughness = max(linearRoughness * linearRoughness, 0.002f);
            float roughness2 = roughness * roughness;
            float d = NoH * NoH * (roughness2 - 1.0f) + 1.00001f;
            float LoH2 = LoH * LoH;
            float normalizationTerm = roughness * 4.0f + 2.0f;

            float specularTerm = roughness2 / ((d * d) * max(0.1f, LoH2) * normalizationTerm);
            float3 fresnel = f0 + (1.0f - f0) * pow(1.0f - LoH, 5.0f);

            return specularTerm * fresnel;
        }

        // Karis' analytic environment BRDF: the split-sum scale/bias without the LUT.
        float3 VoxelisXBudgetEnvBRDF(float3 f0, float linearRoughness, float NoV)
        {
            const float4 c0 = float4(-1.0f, -0.0275f, -0.572f, 0.022f);
            const float4 c1 = float4(1.0f, 0.0425f, 1.04f, -0.04f);
            float4 r = linearRoughness * c0 + c1;
            float a004 = min(r.x * r.x, exp2(-9.28f * NoV)) * r.x + r.y;
            float2 AB = float2(-1.04f, 1.04f) * a004 + r.zw;
            return f0 * AB.x + AB.y;
        }

        float4 ATrousFilter(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return VoxelisXATrousFilter(VoxelisXPixelCoord(input.texcoord));
        }

        float4 TemporalAccumulation(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return VoxelisXTemporalAccumulate(VoxelisXPixelCoord(input.texcoord));
        }

        // The whole lighting model, evaluated once per pixel against the G-buffer:
        //   emission + sun (lambert + GGX, hard shadowed) + sky ambient (diffuse + specular),
        // with AO attenuating only the ambient half.
        float4 DeferredShade(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            uint2 coord = VoxelisXPixelCoord(input.texcoord);

            float4 deterministicRadiance = LOAD_TEXTURE2D(_DeterministicRadianceTex, coord);
            float4 surface = LOAD_TEXTURE2D(_BudgetSurfaceTex, coord);

            // Sky, or a delta branch that flew off into it: nothing to shade, and the
            // deterministic target already holds everything such a pixel contributes.
            if (surface.a <= 0.001f)
            {
                return deterministicRadiance;
            }

            float4 normalPacked = LOAD_TEXTURE2D(_NormalTex, coord);
            float4 aoShadow = LOAD_TEXTURE2D(_BudgetAOShadowAccumulatedTex, coord);

            float3 N = normalize(VoxelisXUnpackNormal(normalPacked.rg));
            float linearRoughness = saturate(normalPacked.a);
            float3 V = normalize(VoxelisXUnpackNormal(surface.rg));
            float metallic = saturate(surface.b);

            // Already carries the delta chain's throughput, so multiplying it into each term
            // tints reflections and refractions without any further bookkeeping.
            float3 albedo = LOAD_TEXTURE2D(_AlbedoTex, coord).rgb;

            float occlusion = lerp(1.0f, saturate(aoShadow.r), _BudgetAOStrength);
            float sunVisibility = saturate(aoShadow.g);

            float3 diffuseColor = albedo * (1.0f - metallic);
            float3 specularColor = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
            float NoV = saturate(dot(N, V));

            // --- Sun: lambert + GGX, gated by the hard shadow ray. AO deliberately does not
            // touch this; occlusion is an ambient-visibility term, and applying it to a
            // directional light double-counts the shadow that light already casts.
            float3 L = -_BudgetMainLightDirection;
            float NoL = saturate(dot(N, L));
            float3 sunRadiance = _BudgetMainLightColor.rgb * (NoL * sunVisibility);
            float3 color = diffuseColor * sunRadiance;
            color += VoxelisXBudgetDirectSpecular(N, V, L, linearRoughness, specularColor) * sunRadiance;

            // --- Sky ambient. The cubemap stands in for both irradiance (a high mip along the
            // normal) and the reflection probe (a roughness-selected mip along the reflection
            // vector). If the bound cubemap has no mip chain the LOD clamps and both collapse to
            // a sharp lookup, which reads as a flatter but still plausible ambient.
            float3 ambientDiffuse = VoxelisXBudgetSampleSky(N, _BudgetSkyDiffuseMip) * diffuseColor;

            float3 R = reflect(-V, N);
            float specularMip = linearRoughness * _BudgetSkySpecularMaxMip;
            float3 ambientSpecular = VoxelisXBudgetSampleSky(R, specularMip)
                * VoxelisXBudgetEnvBRDF(specularColor, linearRoughness, NoV);

            float specularOcclusion = lerp(1.0f, occlusion, _BudgetAOAffectsSpecular);
            color += ambientDiffuse * occlusion + ambientSpecular * specularOcclusion;

            // Emission bypasses lighting entirely.
            color += deterministicRadiance.rgb;

            return float4(color, deterministicRadiance.a);
        }

        float4 CrossResolve(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return VoxelisXCrossResolve(VoxelisXPixelCoord(input.texcoord));
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
            Name "DeferredShade"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DeferredShade
            ENDHLSL
        }

        Pass
        {
            Name "CrossResolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CrossResolve
            ENDHLSL
        }
    }
}
