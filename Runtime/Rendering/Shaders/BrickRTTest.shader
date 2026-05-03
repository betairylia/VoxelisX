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
            Tags{ "LightMode" = "RayTracing" }
            
            HLSLPROGRAM

            // Isometric sector constants (uniform across all axes)
            #define SIZE_IN_BLOCKS 8
            #define SIZE_IN_BRICKS 16
            #define SIZE_IN_BRICKS_SQUARED 256
            #define BRICK_DDA_MAX_STEPS 36

            #include "DDA.hlsl"
            #include "RayPayload.hlsl"
            #include "Utils.hlsl"
            #include "Assets/VoxelisX/VoxelMaterials.hlsl"

            struct AttributeData
            {
                // half4 color;
                int matID;
                half3 normal;
            };

            struct ZeroOneBrick
            {
                int brickPosNum;
                int voxels[16];
            };
            StructuredBuffer<int> g_bricks;

            float _Smoothness;
            float _Metallic;
            float _IOR;

            #pragma raytracing test
            #pragma shader_feature_raytracing _EMISSION
            #pragma multi_compile_local RAY_TRACING_PROCEDURAL_GEOMETRY

            #if RAY_TRACING_PROCEDURAL_GEOMETRY
            bool map(int3 pos)
            {
                // return pos.y <= 800;
                return (pos.x & pos.z & pos.y) == 0;
            }

            // float mandelbulb_sdf(float3 pos) {
            //     float3 z = pos;
            //     float dr = 1.0;
            //     float r = 0.0;
            //     for (int i = 0; i < 16; i++)
            //     {
            //         r = length(z);
            //         if (r>1.5) break;
            //         
            //         // convert to polar coordinates
            //         float theta = acos(z.z / r);
            //         float phi = atan2(z.y, z.x);
            //
            //         dr =  pow( r, 8.0-1.0)*8.0*dr + 1.0;
            //         
            //         // scale and rotate the point
            //         float zr = pow( r,8.0);
            //         theta = theta*8.0;
            //         phi = phi*8.0;
            //         
            //         // convert back to cartesian coordinates
            //         z = pos + zr*float3(sin(theta)*cos(phi), sin(phi)*sin(theta), cos(theta));
            //     }
            //     return 0.5*log(r)*r/dr;
            // }
            
            // bool map(int3 pos)
            // {
            //     return mandelbulb_sdf(pos / 4096.0 - 2.0) <= 0;
            // }

            int ReadBrick01Buf(ZeroOneBrick brick, int3 pos)
            {
                return (((brick.voxels[(pos.z << 1) | (pos.y >> 2)]) >> (((pos.y & 0b0011) << 3) | pos.x)) & 0b01);
                // return (((brick.voxels[(pos.z << 1) | (pos.y >> 2)])));
                // return 1;
            }

            inline int ReadBrickBuf(int bid, int3 pos)
            {
                return (g_bricks[bid * 257 + 1 + (pos.x/2 + pos.y*4 + pos.z*32)] >> ((~(pos.x & 0b01)) << 4)) & 0xFFFF;
            }

            inline bool ShouldTerminateBrickDDA(int blk, int previousTransparentBlock)
            {
                bool shouldTerminate = IsOpaque(blk);
                shouldTerminate |= (previousTransparentBlock != -1) && (blk != previousTransparentBlock);
                return shouldTerminate;
            }

            inline bool TraceBrickDDA(int bid, float3 entryPositionInBrick, float3 rayDir, float entryT, int3 entryNormal, out DDAHit hit, out int materialID)
            {
                DDAClearHit(hit);
                materialID = 0;

                int3 brickGridSize = int3(SIZE_IN_BLOCKS, SIZE_IN_BLOCKS, SIZE_IN_BLOCKS);
                DDACursor cursor = DDACreateCursor(entryPositionInBrick, rayDir, brickGridSize);
                int prevTransparentBlock = -1;

                [unroll]for(int i = 0; i < BRICK_DDA_MAX_STEPS; i++)
                {
                    if(!DDAIsInside(cursor, brickGridSize)) break;

                    int blk = ReadBrickBuf(bid, cursor.cell);
                    bool shouldTerminate = ShouldTerminateBrickDDA(blk, prevTransparentBlock);
                    prevTransparentBlock = blk;

                    if(shouldTerminate)
                    {
                        materialID = blk;
                        DDAMakeHit(cursor, entryT, entryNormal, hit);
                        return true;
                    }

                    DDAStep(cursor);
                }

                return false;
            }

            [shader("intersection")]
            void IntersectionMain()
            {
                // AttributeData attrib;
                // attrib.normal = half3(0, 1, 0);
                // attrib.color = half4((PrimitiveIndex() / 128) / 128.0, (PrimitiveIndex() % 128) / 128.0, 0, 0.0);
                // ReportHit(0, 0, attrib);
                // return;
                
                // Get ray in object space
                float3 rayOrigin = ObjectRayOrigin();
                float3 rayDir = ObjectRayDirection();
                
                // Get AABB index and position
                // Brick brick = g_bricks[PrimitiveIndex()];
                // int aabbIdx = brick.brickPosNum;
                int bid = PrimitiveIndex();
                int aabbIdx = g_bricks[bid * 257];

                if(aabbIdx == -1){ return; }
                
                // Brick pos inside sector (SIZE_IN_BRICKS^3 grid)
                int bX = SIZE_IN_BLOCKS * (aabbIdx % SIZE_IN_BRICKS);
                int bY = SIZE_IN_BLOCKS * ((aabbIdx / SIZE_IN_BRICKS) % SIZE_IN_BRICKS);
                int bZ = SIZE_IN_BLOCKS * (aabbIdx / SIZE_IN_BRICKS_SQUARED);

                // Define the AABB for this primitive (unit size boxes)
                float3 aabbMin = float3(bX, bY, bZ);
                float3 aabbMax = float3(bX + SIZE_IN_BLOCKS, bY + SIZE_IN_BLOCKS, bZ + SIZE_IN_BLOCKS);
                
                // Ray-AABB intersection with optimization for unit-sized boxes
                float3 invDir = 1.0f / rayDir;
                float3 t0 = (aabbMin - rayOrigin) * invDir;
                float3 t1 = (aabbMax - rayOrigin) * invDir;
                
                // Handle negative directions
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);
                
                // Find the largest tmin and smallest tmax
                float largestTmin = max(max(tmin.x, tmin.y), tmin.z);
                float smallestTmax = min(min(tmax.x, tmax.y), tmax.z);
                
                // Check if there's an intersection in the valid ray range
                if (largestTmin <= smallestTmax && smallestTmax >= 0 && largestTmin <= RayTCurrent())
                {
                    // Use the entry point distance if it's positive, otherwise use the exit point
                    float hitT = max(0, largestTmin);
                    
                    // Determine hit normal - we only need this if you're using it in your closest hit shader
                    int3 hitNormal;
                    if (largestTmin == tmin.x)
                        hitNormal = int3(rayDir.x > 0 ? -1 : 1, 0, 0);
                    else if (largestTmin == tmin.y)
                        hitNormal = int3(0, rayDir.y > 0 ? -1 : 1, 0);
                    else
                        hitNormal = int3(0, 0, rayDir.z > 0 ? -1 : 1);
                    
                    // Create minimalist hit attributes with just the normal
                    AttributeData attrib;
                    attrib.normal = hitNormal;
                    // attrib.matID = 0xffffffff;
                    // ReportHit(hitT, 0, attrib);
                    // return;
                    
                    float3 entryPositionInBrick = rayOrigin + rayDir * hitT - float3(bX, bY, bZ);
                    DDAHit ddaHit;
                    int materialID;
                    bool hit = TraceBrickDDA(bid, entryPositionInBrick, rayDir, hitT, hitNormal, ddaHit, materialID);

                    // attrib.color = half3(blockPos / 8.0);
                    // attrib.color = half4(0.4, 0.8, 0.5, 0.2 * (blockPos.y == 0));
                    // attrib.color = half4(0.4, 0.8, 0.5, 0.0);
                    // attrib.color = half3(1, 0.4, 0.4);
                    
                    // Report the hit with the computed distance
                    if(hit)
                    {
                        attrib.normal = ddaHit.normal;
                        attrib.matID = materialID;
                        ReportHit(ddaHit.t, 0, attrib);
                    }
                }
            }
            #endif

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                int materialID = attribs.matID;
                VoxelMaterial material = GET_MATERIAL(materialID);
                
                // float3 objectHitPosition = ObjectRayOrigin() + ObjectRayDirection() * RayTCurrent();
                float3 worldRayOrigin = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
                float3 worldNormal = mul((float3x3)ObjectToWorld3x4(), attribs.normal);

                // Accumulated albedo for transparency
                VoxelMaterial tMat = GET_MATERIAL(payload.previousTransparentMaterial);
                float3 ext = payload.previousTransparentMaterial == 0 ? float3(1, 1, 1) : exp(-(1 - tMat.albedo) * RayTCurrent() * tMat.extinction);
                // float3 ext = float3(1, 1, 1);

                if(IsOpaque(materialID))
                {
                    // payload.albedo = float4(attribs.normal * 0.5 + 0.5, 1);
                    // payload.albedo = float4(worldNormal * 0.5 + 0.5, 1);
                    payload.albedo = material.albedo.rgb * ext;
                    // payload.albedo = float4(1, 1, 1, 1);
                    // payload.albedo = float4(RayTCurrent() / 50.0, 0, 0, 1);
                    payload.bounceIndexOpaque = payload.bounceIndexOpaque + 1;
                    // payload.emission = float4(attribs.color.y == 0, (1 - attribs.color.x) * (attribs.color.y == 0), 0, 1);
                    // payload.emission = float4(.02, .02, .02, 1);
                    payload.emission = material.emission.rgb;
                    
                    float fresnelFactor = FresnelReflectAmountOpaque(1, material.IOR, WorldRayDirection(), worldNormal);
                    float specularChance = lerp(material.metallic, 1, fresnelFactor * material.smoothness);

                    // Calculate whether we are going to do a diffuse or specular reflection ray 
                    float doSpecular = (RandomFloat01(payload.rngState) < specularChance) ? 1 : 0;
                    
                    // Bounce
                    const float3 diffuseRayDir = normalize(worldNormal + RandomUnitVector(payload.rngState));
                    float3 specularRayDir = reflect(WorldRayDirection(), worldNormal);
                    specularRayDir = normalize(lerp(diffuseRayDir, specularRayDir, material.smoothness));
                    float3 reflectedRayDir = lerp(diffuseRayDir, specularRayDir, doSpecular);
                    
                    payload.k               = (doSpecular == 1) ? specularChance : 1 - specularChance;
                    payload.bounceRayOrigin = worldRayOrigin + K_RAY_ORIGIN_PUSH_OFF * worldNormal;
                    // payload.bounceRayDirection = diffuseRayDir;
                    payload.bounceRayDirection = reflectedRayDir;
                    payload.worldNormal = worldNormal;
                }
                else
                {
                    payload.k = 1;
                    payload.albedo = float3(1, 1, 1);
                    payload.emission = float3(0, 0, 0);
                    payload.bounceRayOrigin = worldRayOrigin - K_RAY_ORIGIN_PUSH_OFF * worldNormal;
                    payload.bounceRayDirection = WorldRayDirection();
                    payload.worldNormal = worldNormal;
                    payload.previousTransparentMaterial = materialID;
                    payload.bounceIndexTransparent = payload.bounceIndexTransparent + 1;
                }
            }
            
            ENDHLSL
        }
    }
}
