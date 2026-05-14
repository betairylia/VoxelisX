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

            #include "RayPayload.hlsl"
            #include "Utils.hlsl"
            #include "Assets/VoxelisX/VoxelMaterials.hlsl"
            #include "Utils/BlueNoise.hlsl"

            #include "VoxelisXBrickTrace.hlsl"

            struct AttributeData
            {
                uint matID_faceHash;
                half3 normal;
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
                VoxelisXBrickTraceContext context;
                context.objectRayOrigin = ObjectRayOrigin();
                context.objectRayDirection = ObjectRayDirection();
                context.currentRayT = RayTCurrent();
                context.primitiveIndex = PrimitiveIndex();

                VoxelisXBrickHit hit = VoxelisXTraceBrickPrimitive(context);
                if (hit.hit)
                {
                    AttributeData attrib;
                    attrib.normal = hit.objectNormal;
                    attrib.matID_faceHash = hit.materialID_faceHash;
                    ReportHit(hit.t, 0, attrib);
                }
            }
            #endif

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                VoxelisXBrickHit hit;
                hit.hit = true;
                hit.t = RayTCurrent();
                hit.materialID_faceHash = attribs.matID_faceHash;
                hit.objectNormal = attribs.normal;

                VoxelisXApplyVoxelClosestHitMinimumPayload(payload, hit);
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
