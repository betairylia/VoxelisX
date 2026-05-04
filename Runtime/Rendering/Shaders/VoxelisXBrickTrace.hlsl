#ifndef VOXELISX_BRICK_TRACE_INCLUDED
#define VOXELISX_BRICK_TRACE_INCLUDED

#ifndef SHIFT_SIZE_IN_BLOCKS
#define SHIFT_SIZE_IN_BLOCKS 3
#endif

#ifndef SIZE_IN_BLOCKS
#define SIZE_IN_BLOCKS (1 << SHIFT_SIZE_IN_BLOCKS)
#endif

#ifndef SHIFT_SIZE_IN_BRICKS
#define SHIFT_SIZE_IN_BRICKS 4
#endif

#ifndef SIZE_IN_BRICKS
#define SIZE_IN_BRICKS (1 << SHIFT_SIZE_IN_BRICKS)
#endif

#ifndef BRICK_POS_MASK
#define BRICK_POS_MASK 15u
#endif

#ifndef SIZE_IN_BRICKS_SQUARED
#define SIZE_IN_BRICKS_SQUARED 256
#endif

#ifndef BRICK_DDA_MAX_STEPS
#define BRICK_DDA_MAX_STEPS 22
#endif

#ifndef BRICK_COARSE_DDA_MAX_STEPS
#define BRICK_COARSE_DDA_MAX_STEPS 4
#endif

#ifndef BRICK_MICRO_DDA_MAX_STEPS
#define BRICK_MICRO_DDA_MAX_STEPS 10
#endif

#define BRICK_INFO_WORDS 1
#define BRICK_OCCUPANCY_WORDS 16
#define BRICK_BLOCK_DATA_OFFSET 17
#define BRICK_DATA_LENGTH 273
#define BRICK_INFO_ABSOLUTE_INDEX_MASK 0xFFFu
#define BRICK_INFO_COARSE_OCCUPANCY_SHIFT 16u
#define BRICK_INFO_COARSE_OCCUPANCY_MASK 0xFFu
#define BRICK_COARSE_SIZE_IN_BLOCKS 4

struct VoxelisXBrickTraceContext
{
    float3 objectRayOrigin;
    float3 objectRayDirection;
    float currentRayT;
    uint primitiveIndex;
};

struct VoxelisXBrickHit
{
    bool hit;
    float t;
    int materialID;
    half3 objectNormal;
};

struct VoxelisXBrickAABBCandidate
{
    bool hit;
    uint brickID;
    uint coarseOccupancy;
    int3 brickOrigin;
    float t;
    int3 normal;
};

StructuredBuffer<uint> g_bricks;
float4x4 _PrevObjectToWorld;

inline VoxelisXBrickHit VoxelisXMakeBrickMiss()
{
    VoxelisXBrickHit hit;
    hit.hit = false;
    hit.t = 0.0f;
    hit.materialID = 0;
    hit.objectNormal = half3(0, 0, 0);
    return hit;
}

inline int VoxelisXReadBrick(uint brickBase, int3 localBlockPos)
{
    uint shift = uint((1 - (localBlockPos.x & 1)) << 4);
    return int((g_bricks[brickBase + ((localBlockPos.x >> 1) + (localBlockPos.y << 2) + (localBlockPos.z << 5))] >> shift) & 0xFFFFu);
}

inline uint VoxelisXBrickBase(uint brickID)
{
    return brickID * BRICK_DATA_LENGTH;
}

inline uint VoxelisXGetCoarseOccupancy(uint brickInfo)
{
    return (brickInfo >> BRICK_INFO_COARSE_OCCUPANCY_SHIFT) & BRICK_INFO_COARSE_OCCUPANCY_MASK;
}

inline uint VoxelisXCoarseOccupancyBit(int3 coarseCell)
{
    return uint(coarseCell.x | (coarseCell.y << 1) | (coarseCell.z << 2));
}

inline uint VoxelisXMicroOccupancyBit(int3 microCell)
{
    return uint(microCell.x | (microCell.y << 2) | (microCell.z << 4));
}

inline void VoxelisXLoadBrickOccupancy(uint brickBase, out uint4 occupancy0, out uint4 occupancy1, out uint4 occupancy2, out uint4 occupancy3)
{
    uint occupancyBase = brickBase + BRICK_INFO_WORDS;
    occupancy0 = uint4(
        g_bricks[occupancyBase],
        g_bricks[occupancyBase + 1u],
        g_bricks[occupancyBase + 2u],
        g_bricks[occupancyBase + 3u]);
    occupancy1 = uint4(
        g_bricks[occupancyBase + 4u],
        g_bricks[occupancyBase + 5u],
        g_bricks[occupancyBase + 6u],
        g_bricks[occupancyBase + 7u]);
    occupancy2 = uint4(
        g_bricks[occupancyBase + 8u],
        g_bricks[occupancyBase + 9u],
        g_bricks[occupancyBase + 10u],
        g_bricks[occupancyBase + 11u]);
    occupancy3 = uint4(
        g_bricks[occupancyBase + 12u],
        g_bricks[occupancyBase + 13u],
        g_bricks[occupancyBase + 14u],
        g_bricks[occupancyBase + 15u]);
}

inline void VoxelisXSelectMicroOccupancy(uint coarseBit, uint4 occupancy0, uint4 occupancy1, uint4 occupancy2, uint4 occupancy3, out uint occLo, out uint occHi)
{
    switch (coarseBit)
    {
        case 0:
            occLo = occupancy0.x;
            occHi = occupancy0.y;
            break;
        case 1:
            occLo = occupancy0.z;
            occHi = occupancy0.w;
            break;
        case 2:
            occLo = occupancy1.x;
            occHi = occupancy1.y;
            break;
        case 3:
            occLo = occupancy1.z;
            occHi = occupancy1.w;
            break;
        case 4:
            occLo = occupancy2.x;
            occHi = occupancy2.y;
            break;
        case 5:
            occLo = occupancy2.z;
            occHi = occupancy2.w;
            break;
        case 6:
            occLo = occupancy3.x;
            occHi = occupancy3.y;
            break;
        default:
            occLo = occupancy3.z;
            occHi = occupancy3.w;
            break;
    }
}

inline bool VoxelisXIsMicroOccupied(uint occLo, uint occHi, uint microBit)
{
    if (microBit < 32u)
    {
        return (occLo & (1u << microBit)) != 0u;
    }

    return (occHi & (1u << (microBit - 32u))) != 0u;
}

inline bool VoxelisXShouldTraceMicroOccupancy(uint occLo, uint occHi, float3 rayDir)
{
    // Future LUT early rejection can key off the 64-bit occupancy and a binned ray direction here.
    return (occLo | occHi) != 0u;
}

inline bool VoxelisXShouldTerminateBrickDDA(int blockID, int previousTransparentBlock)
{
    bool shouldTerminate = IsOpaque(blockID);
    shouldTerminate |= (previousTransparentBlock != -1) && (blockID != previousTransparentBlock);
    return shouldTerminate;
}

inline bool VoxelisXIntersectBrickAABB(VoxelisXBrickTraceContext context, out VoxelisXBrickAABBCandidate candidate)
{
    candidate.hit = false;
    candidate.brickID = context.primitiveIndex;
    candidate.coarseOccupancy = 0u;
    candidate.brickOrigin = int3(0, 0, 0);
    candidate.t = 0.0f;
    candidate.normal = int3(0, 0, 0);

    uint brickInfo = g_bricks[VoxelisXBrickBase(candidate.brickID)];
    uint coarseOccupancy = VoxelisXGetCoarseOccupancy(brickInfo);
    if (coarseOccupancy == 0u)
    {
        return false;
    }

    uint idx = brickInfo & BRICK_INFO_ABSOLUTE_INDEX_MASK;
    int bX = int((idx & BRICK_POS_MASK) << SHIFT_SIZE_IN_BLOCKS);
    int bY = int(((idx >> SHIFT_SIZE_IN_BRICKS) & BRICK_POS_MASK) << SHIFT_SIZE_IN_BLOCKS);
    int bZ = int((idx >> (SHIFT_SIZE_IN_BRICKS + SHIFT_SIZE_IN_BRICKS)) << SHIFT_SIZE_IN_BLOCKS);

    float3 aabbMin = float3(bX, bY, bZ);
    float3 aabbMax = float3(bX + SIZE_IN_BLOCKS, bY + SIZE_IN_BLOCKS, bZ + SIZE_IN_BLOCKS);

    float3 invDir = 1.0f / context.objectRayDirection;
    float3 t0 = (aabbMin - context.objectRayOrigin) * invDir;
    float3 t1 = (aabbMax - context.objectRayOrigin) * invDir;

    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);

    float largestTmin = max(max(tmin.x, tmin.y), tmin.z);
    float smallestTmax = min(min(tmax.x, tmax.y), tmax.z);

    if (largestTmin > smallestTmax || smallestTmax < 0 || largestTmin > context.currentRayT)
    {
        return false;
    }

    int3 hitNormal;
    if (largestTmin == tmin.x)
    {
        hitNormal = int3(context.objectRayDirection.x > 0 ? -1 : 1, 0, 0);
    }
    else if (largestTmin == tmin.y)
    {
        hitNormal = int3(0, context.objectRayDirection.y > 0 ? -1 : 1, 0);
    }
    else
    {
        hitNormal = int3(0, 0, context.objectRayDirection.z > 0 ? -1 : 1);
    }

    candidate.hit = true;
    candidate.coarseOccupancy = coarseOccupancy;
    candidate.brickOrigin = int3(bX, bY, bZ);
    candidate.t = max(0, largestTmin);
    candidate.normal = hitNormal;
    return true;
}

inline bool VoxelisXTraceBrickDDA(uint brickID, uint coarseOccupancy, float3 entryPositionInBrick, float3 rayDir, float entryT, int3 entryNormal, out DDAHit hit, out int materialID)
{
    DDAClearHit(hit);
    materialID = 0;

    uint brickBase = VoxelisXBrickBase(brickID);
    uint4 occupancy0;
    uint4 occupancy1;
    uint4 occupancy2;
    uint4 occupancy3;
    VoxelisXLoadBrickOccupancy(brickBase, occupancy0, occupancy1, occupancy2, occupancy3);

    int3 coarseGridSize = int3(2, 2, 2);
    DDACursor coarseCursor = DDACreateCursor(entryPositionInBrick * 0.25f, rayDir * 0.25f, coarseGridSize);
    int prevTransparentBlock = 0;

    for (int coarseStep = 0; coarseStep < BRICK_COARSE_DDA_MAX_STEPS; coarseStep++)
    {
        if (!DDAIsInside(coarseCursor, coarseGridSize))
        {
            break;
        }

        uint coarseBit = VoxelisXCoarseOccupancyBit(coarseCursor.cell);
        if ((coarseOccupancy & (1u << coarseBit)) != 0u)
        {
            uint occLo;
            uint occHi;
            VoxelisXSelectMicroOccupancy(coarseBit, occupancy0, occupancy1, occupancy2, occupancy3, occLo, occHi);

            if (VoxelisXShouldTraceMicroOccupancy(occLo, occHi, rayDir))
            {
                float coarseEntryT = DDACurrentT(coarseCursor, entryT);
                int3 coarseEntryNormal = DDACurrentNormal(coarseCursor, entryNormal);
                int3 coarseBlockOrigin = coarseCursor.cell * BRICK_COARSE_SIZE_IN_BLOCKS;
                float3 coarseEntryPosition = entryPositionInBrick + rayDir * (coarseEntryT - entryT) - float3(coarseBlockOrigin);

                int3 microGridSize = int3(BRICK_COARSE_SIZE_IN_BLOCKS, BRICK_COARSE_SIZE_IN_BLOCKS, BRICK_COARSE_SIZE_IN_BLOCKS);
                DDACursor microCursor = DDACreateCursor(coarseEntryPosition, rayDir, microGridSize);

                for (int microStep = 0; microStep < BRICK_MICRO_DDA_MAX_STEPS; microStep++)
                {
                    if (!DDAIsInside(microCursor, microGridSize))
                    {
                        break;
                    }

                    uint microBit = VoxelisXMicroOccupancyBit(microCursor.cell);
                    if (VoxelisXIsMicroOccupied(occLo, occHi, microBit))
                    {
                        int3 localBlockPos = coarseBlockOrigin + microCursor.cell;
                        int blockID = VoxelisXReadBrick(brickBase + BRICK_BLOCK_DATA_OFFSET, localBlockPos);
                        bool shouldTerminate = VoxelisXShouldTerminateBrickDDA(blockID, prevTransparentBlock);
                        prevTransparentBlock = blockID;

                        if (shouldTerminate)
                        {
                            materialID = blockID;
                            DDAMakeHit(microCursor, coarseEntryT, coarseEntryNormal, hit);
                            return true;
                        }
                    }

                    DDAStep(microCursor);
                }
            }
        }

        DDAStep(coarseCursor);
    }

    return false;
}

inline VoxelisXBrickHit VoxelisXTraceBrickPrimitive(VoxelisXBrickTraceContext context)
{
    VoxelisXBrickHit result = VoxelisXMakeBrickMiss();

    VoxelisXBrickAABBCandidate candidate;
    if (!VoxelisXIntersectBrickAABB(context, candidate))
    {
        return result;
    }

    float3 entryPositionInBrick = context.objectRayOrigin + context.objectRayDirection * candidate.t - float3(candidate.brickOrigin);

    DDAHit ddaHit;
    int materialID;
    if (VoxelisXTraceBrickDDA(candidate.brickID, candidate.coarseOccupancy, entryPositionInBrick, context.objectRayDirection, candidate.t, candidate.normal, ddaHit, materialID))
    {
        result.hit = true;
        result.t = ddaHit.t;
        result.materialID = materialID;
        result.objectNormal = half3(ddaHit.normal);
    }

    return result;
}

inline void VoxelisXApplyVoxelClosestHit(inout RayPayload payload, VoxelisXBrickHit hit, float3 worldRayOrigin, float3 worldRayDirection, float3 objectRayOrigin, float3 objectRayDirection, float currentRayT, float3x4 objectToWorld)
{
    int materialID = hit.materialID;
    VoxelMaterial material = GET_MATERIAL(materialID);

    float3 worldHitPosition = worldRayOrigin + worldRayDirection * currentRayT;
    float3 worldNormal = mul((float3x3)objectToWorld, hit.objectNormal);
    bool isPrimaryHit = (payload.bounceIndexOpaque | payload.bounceIndexTransparent) == 0;

    if (isPrimaryHit)
    {
        float3 objectHitPosition = objectRayOrigin + objectRayDirection * currentRayT;
        payload.prevWorldHitPosition = mul(_PrevObjectToWorld, float4(objectHitPosition, 1.0f)).xyz;
    }

    VoxelMaterial tMat = GET_MATERIAL(payload.previousTransparentMaterial);
    float3 ext = payload.previousTransparentMaterial == 0 ? float3(1, 1, 1) : exp(-(1 - tMat.albedo) * currentRayT * tMat.extinction);

    if (IsOpaque(materialID))
    {
        payload.albedo = material.albedo.rgb * ext;
        payload.bounceIndexOpaque = payload.bounceIndexOpaque + 1;
        payload.emission = material.emission.rgb;

        float fresnelFactor = FresnelReflectAmountOpaque(1, material.IOR, worldRayDirection, worldNormal);
        float specularChance = lerp(material.metallic, 1, fresnelFactor * material.smoothness);
        float doSpecular = (RandomFloat01(payload.rngState) < specularChance) ? 1 : 0;

        const float3 diffuseRayDir = normalize(worldNormal + RandomUnitVector(payload.rngState));
        float3 specularRayDir = reflect(worldRayDirection, worldNormal);
        specularRayDir = normalize(lerp(diffuseRayDir, specularRayDir, material.smoothness));
        float3 reflectedRayDir = lerp(diffuseRayDir, specularRayDir, doSpecular);

        payload.k = (doSpecular == 1) ? specularChance : 1 - specularChance;
        payload.bounceRayOrigin = worldHitPosition + K_RAY_ORIGIN_PUSH_OFF * worldNormal;
        payload.bounceRayDirection = reflectedRayDir;
        payload.worldNormal = worldNormal;
    }
    else
    {
        payload.k = 1;
        payload.albedo = float3(1, 1, 1);
        payload.emission = float3(0, 0, 0);
        payload.bounceRayOrigin = worldHitPosition - K_RAY_ORIGIN_PUSH_OFF * worldNormal;
        payload.bounceRayDirection = worldRayDirection;
        payload.worldNormal = worldNormal;
        payload.previousTransparentMaterial = materialID;
        payload.bounceIndexTransparent = payload.bounceIndexTransparent + 1;
    }
}

#endif
