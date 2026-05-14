struct RayPayload
{
    float k;                // Energy conservation constraint
    half3 albedo;
    half3 emission;
    uint bounceIndexOpaque;
    uint bounceIndexTransparent;
    float3 bounceRayOrigin;
    float3 bounceRayDirection;
    half3 prevWorldHitPosition;
    half3 worldNormal;
    uint rngState;          // Random number generator state.
    uint voxelFaceHash;

    int previousTransparentMaterial;
};
