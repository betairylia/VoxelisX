Shader "Caelix/VoxelMeshDiffuse"
{
    Properties
    {
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardDiffuse"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 materialData : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                half3 albedo : TEXCOORD1;
                float4 shadowCoord : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 GetVoxelAlbedo(int blockID)
            {
                switch (blockID)
                {
                    case 0x8000: return float3(0.2, 0.2, 0.2); // STONE
                    case 0x8001: return float3(0.3, 0.2, 0.1); // DIRT
                    case 0x8002: return float3(0.569, 0.729, 0.345); // GRASS
                    case 0x8003: return float3(0.282, 0.486, 0.22); // PLANT
                    case 0x8004: return float3(0.945, 0.447, 0.639); // FLOWER
                    case 0x81A8: return float3(0.149, 0.216, 0.29); // CONDUCTOR
                    case 0x81B8: return float3(1, 0.937, 0.424); // ELEC_HEAD
                    case 0x81B0: return float3(0.369, 0.49, 0.624); // ELEC_TAIL
                    case 0x81A9: return float3(0.525, 0.255, 0.212); // MOTOR
                    case 0x81AA: return float3(0.957, 0.984, 0.996); // M_CORE
                    case 0x01AB: return float3(0.5, 0.6, 0.9); // M_2X
                    case 0x01AC: return float3(0.565, 0.808, 0.612); // M_3X
                    case 0x01AD: return float3(1, 0.82, 0.404); // M_5X
                    case 0x01AE: return float3(0.945, 0.357, 0.333); // M_7X
                    case 0x01AF: return float3(0.718, 0.659, 0.8); // M_11X
                    case 0x8400: return float3(0.2, 0.8, 0.7294); // PC21
                    case 0x8403: return float3(0, 0.7333, 0.8627); // PC10
                    case 0x8404: return float3(0.2, 0.6667, 0.9333); // PC1
                    case 0x8405: return float3(0, 0.4667, 0.8667); // PC12
                    case 0x8402: return float3(0.6, 0.8039, 1); // PC6
                    case 0x8406: return float3(0.2, 0.4039, 0.8039); // PC25
                    case 0x8407: return float3(0.5333, 0.5373, 0.8); // PC18
                    case 0x8408: return float3(0.7333, 0.5333, 0.9294); // PC16
                    case 0x8409: return float3(0.8941, 0.6588, 0.7922); // PC20
                    case 0x840D: return float3(1, 0.4, 0.7373); // PC14
                    case 0x840A: return float3(1, 0.6627, 0.8); // PC7
                    case 0x840C: return float3(0.7333, 0.3961, 0.5333); // PC17
                    case 0x840E: return float3(1, 0.4039, 0.6039); // PC9
                    case 0x840B: return float3(1, 0.7294, 0.8); // PC24
                    case 0x840F: return float3(0.9333, 0.4, 0.4); // PC3
                    case 0x8410: return float3(0.8706, 0.2667, 0.2667); // PC26
                    case 0x8412: return float3(1, 0.4667, 0.1294); // PC11
                    case 0x8411: return float3(1, 0.8039, 0.6745); // PC5
                    case 0x8413: return float3(0.8, 0.6667, 0.5294); // PC19
                    case 0x8414: return float3(1, 0.7333, 0); // PC13
                    case 0x8415: return float3(1, 0.8, 0.0667); // PC22
                    case 0x8416: return float3(1, 0.8667, 0.2706); // PC2
                    case 0x8417: return float3(1, 0.9333, 0.0706); // PC23
                    case 0x8418: return float3(0.7333, 0.8706, 0.1333); // PC4
                    case 0x8419: return float3(0.2039, 0.8667, 0.6039); // PC15
                    case 0x8401: return float3(0.6039, 0.9333, 0.8706); // PC8
                    case 0x84A0: return float3(1, 1, 0.8); // ACL_ALICE
                    case 0x84A1: return float3(0.42, 0.682, 0.6); // ACL_WALQ
                    case 0x84A2: return float3(0.945, 0.565, 0.753); // ACL_MEQ
                    case 0x84A3: return float3(0.957, 0.984, 0.996); // ACL_ICEM
                    case 0x84A4: return float3(0.839, 0.882, 0.6); // ACL_ERIK
                    case 0x84A5: return float3(0.984, 0.82, 0.459); // ACL_ASPR
                    case 0x84A6: return float3(0.957, 0.537, 0.263); // ACL_FELIV
                    case 0x84A7: return float3(0.776, 0.898, 0.933); // ACL_TIX
                    case 0x81B9: return float3(0.7, 0.4, 0.7); // MOTOR_H
                    case 0x81BA: return float3(0.7, 0.4, 0.7); // M_CORE_H
                    case 0x81BB: return float3(0.7, 0.4, 0.7); // M_2X_H
                    case 0x81BC: return float3(0.7, 0.4, 0.7); // M_3X_H
                    case 0x81BD: return float3(0.7, 0.4, 0.7); // M_5X_H
                    case 0x81BE: return float3(0.7, 0.4, 0.7); // M_7X_H
                    case 0x81BF: return float3(0.7, 0.4, 0.7); // M_11X_H
                    case 0x81B1: return float3(0.7, 0.4, 0.7); // MOTOR_T
                    case 0x81B2: return float3(0.7, 0.4, 0.7); // M_CORE_T
                    case 0x01B3: return float3(0.7, 0.4, 0.7); // M_2X_T
                    case 0x01B4: return float3(0.7, 0.4, 0.7); // M_3X_T
                    case 0x01B5: return float3(0.7, 0.4, 0.7); // M_5X_T
                    case 0x01B6: return float3(0.7, 0.4, 0.7); // M_7X_T
                    case 0x01B7: return float3(0.7, 0.4, 0.7); // M_11X_T
                    default: return float3(1, 0, 1);
                }
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.normalWS = normalInput.normalWS;
                output.albedo = GetVoxelAlbedo((int)(input.materialData.x + 0.5f));
                output.shadowCoord = GetShadowCoord(vertexInput);
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight(input.shadowCoord);
                half attenuation = mainLight.distanceAttenuation * mainLight.shadowAttenuation;
                half diffuse = saturate(dot(normalWS, mainLight.direction)) * attenuation;

                // Keep fallback lighting neutral; voxel material albedo is the only color source.
                half3 color = input.albedo * (0.3h + 0.7h * diffuse);

                return half4(MixFog(color, input.fogFactor), 1.0);
            }
            ENDHLSL
        }

        // Shadow caster pass
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float3 _LightDirection;
            float3 _LightPosition;

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        // Depth only pass
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
