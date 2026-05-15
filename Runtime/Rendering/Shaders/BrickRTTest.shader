Shader "VoxelisX/BrickRTTest"
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
            Name "VoxelisX"
            Tags{
                "LightMode" = "RayTracing"
            }
            
            HLSLPROGRAM
            #pragma enable_ray_tracing_shader_debug_symbols
            #pragma target 6.2
            #pragma use_dxc

            #include "RayPayload.hlsl"
            #include "Utils.hlsl"
            #include "Assets/VoxelisX/VoxelMaterials.hlsl"
            #include "Utils/BlueNoise.hlsl"

            #include "VoxelisXBrickTrace.hlsl"

            struct AttributeData
            {
                // (31) [matID:16] [reserved] [faceNormalFlags:6] (0)
                uint matID_faceNormal;
            };

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
                float T;
                AttributeData attrib;
                {
                    VoxelisXBrickHit hit = VoxelisXTraceBrickPrimitive();
                    T = hit.t;
                    attrib.matID_faceNormal = hit.materialID_faceNormal;
                }
                
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
                VoxelisXBrickHit hit;
                payload.T = RayTCurrent();
                payload.materialID_voxelFaceHash = attribs.matID_faceNormal;
                payload.packedWorldNormal = VoxelisXPackWorldNormal(mul((float3x3)ObjectToWorld3x4(), UnpackObjectNormal(attribs.matID_faceNormal)));

                float3 objectHitPosition = ObjectRayOrigin() + ObjectRayDirection() * payload.T;
                float3 worldHitPosition = WorldRayOrigin() + WorldRayDirection() * payload.T;
                payload.packedPrevWorldOffset = VoxelisXPackFloat3ToHalf4(
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
