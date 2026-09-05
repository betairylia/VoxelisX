Shader "Caelix/BrickRTTest"
{
    Properties
    {
//        _MainTex ("Texture", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        [Gamma] _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _IOR("Index of Refraction", Range(1.0, 2.8)) = 1.5
    }
    SubShader
    {
        Pass
        {
            Name "Caelix"
            Tags{
                "LightMode" = "RayTracing"
            }
            
            HLSLPROGRAM
            #pragma enable_ray_tracing_shader_debug_symbols
            #pragma target 6.6
            #pragma use_dxc
            // Brick storage: off = one buffer per sector bound through the property block;
            // on = the shared CaelixBrickPool, g_bricks bound per instance to the page holding the sector, offset by _BrickBase.
            // CaelixRenderer enables the keyword on a private material instance in pool mode.
            #pragma multi_compile_local _ CAELIX_BRICK_POOL

            #include "RayPayload.hlsl"
            #include "Utils/Utils.hlsl"
            #include "Assets/Caelix/VoxelMaterials.hlsl"
            #include "Utils/BlueNoise.hlsl"


            #include "CaelixBrickTrace.hlsl"
            
            float _Smoothness;
            float _Metallic;
            float _IOR;

            #pragma raytracing test
            #pragma shader_feature_raytracing _EMISSION
            #pragma multi_compile_local RAY_TRACING_PROCEDURAL_GEOMETRY

            #if RAY_TRACING_PROCEDURAL_GEOMETRY
            [shader("intersection")]
            void IntersectionMain()
            {
                AttributeData attrib;
                float T = CaelixTraceBrickPrimitive(attrib);
                
                if (attrib.matID_faceNormal)
                {
                    ReportHit(T, 0, attrib);
                    return;
                }
            }
            #endif

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                CaelixBrickHit hit;
                payload.T = RayTCurrent();
                payload.materialID_voxelFaceHash = attribs.matID_faceNormal;
                payload.packedWorldNormal = CaelixPackWorldNormal(mul((float3x3)ObjectToWorld3x4(), UnpackObjectNormal(attribs.matID_faceNormal)));

                float3 objectHitPosition = ObjectRayOrigin() + ObjectRayDirection() * payload.T;
                float3 worldHitPosition = WorldRayOrigin() + WorldRayDirection() * payload.T;
                payload.packedPrevWorldOffset = CaelixPackFloat3ToHalf4(
                mul(_PrevObjectToWorld, float4(objectHitPosition, 1.0f)).xyz - worldHitPosition);
            }
            
            // [shader("anyhit")]
            // void AniHitMain(inout RayPayload payload : SV_RayPayload, AttributeData attribs: SV_IntersectionAttributes)
            // {
            //     IgnoreHit();
            // }
            
            ENDHLSL
        }
    }
}
