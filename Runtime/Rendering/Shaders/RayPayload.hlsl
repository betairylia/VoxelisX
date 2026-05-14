struct RayPayload
{
    // float k;                // Energy conservation constraint
    // half3 albedo;
    // half3 emission;
    // uint bounceIndexOpaque;
    // uint bounceIndexTransparent;
    // float3 bounceRayOrigin;
    // float3 bounceRayDirection;
    // half3 prevWorldHitPosition;
    // half3 worldNormal;
    // uint rngState;          // Random number generator state.
    // uint voxelFaceHash;
    //
    // int previousTransparentMaterial;
    
    float T;
    // half k;
    half3 worldNormal; // -> half2 packedWorldNormal
    half3 prevWorldOffset;
    uint materialID_voxelFaceHash;
    uint bounceIndexOpaque_Transparent_RNGState;
    
    /* +2 half k
     * +2 short materialID
     * +8 float2 packedWorldNormal
     * +4 float T
     * +1 byte packedBounceIndexOpaqueTransparent
     * +2 short voxelFaceHash
     */
};
