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
#define BRICK_MICRO_SHIFT 2
#define BRICK_MICRO_SIZE (1 << BRICK_MICRO_SHIFT)
#define BRICK_MICRO_MASK (BRICK_MICRO_SIZE - 1)

#define VOXEL_FACE_HASH_MASK 0x3FFu

#include "Utils/BlueNoise.hlsl"
#include "DDA.hlsl"

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
    uint faceHash;
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
uint _SectorHashSeed;

inline VoxelisXBrickHit VoxelisXMakeBrickMiss()
{
    VoxelisXBrickHit hit;
    hit.hit = false;
    hit.t = 0.0f;
    hit.materialID = 0;
    hit.objectNormal = half3(0, 0, 0);
    hit.faceHash = 0u;
    return hit;
}

inline uint VoxelisXHashAvalanche(uint value)
{
    value ^= value >> 16;
    value *= 0x7FEB352Du;
    value ^= value >> 15;
    value *= 0x846CA68Bu;
    value ^= value >> 16;
    return value;
}

inline uint VoxelisXFaceID(int3 normal)
{
    if (normal.x > 0) return 0u;
    if (normal.x < 0) return 1u;
    if (normal.y > 0) return 2u;
    if (normal.y < 0) return 3u;
    if (normal.z > 0) return 4u;
    return 5u;
}

inline uint VoxelisXMakeVoxelFaceHash(int3 sectorLocalVoxelPos, int3 normal)
{
    uint voxelKey =
        uint(sectorLocalVoxelPos.x & 0x7F) |
        (uint(sectorLocalVoxelPos.y & 0x7F) << 7) |
        (uint(sectorLocalVoxelPos.z & 0x7F) << 14) |
        (VoxelisXFaceID(normal) << 21);

    uint hash = VoxelisXHashAvalanche(_SectorHashSeed ^ voxelKey) & VOXEL_FACE_HASH_MASK;
    return hash == 0u ? 1u : hash;
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

inline void VoxelisXLoadMicroOccupancy(uint brickBase, uint coarseBit, out uint occLo, out uint occHi)
{
    uint occupancyBase = brickBase + BRICK_INFO_WORDS + coarseBit * 2u;
    occLo = g_bricks[occupancyBase];
    occHi = g_bricks[occupancyBase + 1u];
}

inline bool VoxelisXIsMicroOccupied(uint occLo, uint occHi, uint microBit)
{
    // return (microBit < 32u) ? ((occLo & (1u << microBit)) != 0u) : ((occHi & (1u << (microBit - 32u))) != 0u);
    if (microBit < 32u)
    {
        return (occLo & (1u << microBit)) != 0u;
    }

    return (occHi & (1u << (microBit - 32u))) != 0u;
}

inline bool VoxelisXShouldTraceMicroOccupancy(uint occLo, uint occHi, float3 rayDir)
{
    // Future LUT early rejection can key off the 64-bit occupancy and a binned ray direction here.
    return true;
}

inline bool VoxelisXShouldTerminateBrickDDA(int blockID, int previousTransparentBlock)
{
    bool shouldTerminate = IsOpaque(blockID);
    shouldTerminate |= (previousTransparentBlock != -1) && (blockID != previousTransparentBlock);
    return shouldTerminate;
}

inline bool VoxelisXIntersectBrickAABB(VoxelisXBrickTraceContext context, out VoxelisXBrickAABBCandidate candidate)
{
    candidate.brickID = context.primitiveIndex;

    uint brickInfo = g_bricks[VoxelisXBrickBase(candidate.brickID)];

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
    
    // TODO: Do coarse bit (2x2x2) early reject here? 
    // VoxelisXGetCoarseOccupancy(brickInfo)

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
    candidate.brickOrigin = int3(bX, bY, bZ);
    candidate.t = max(0, largestTmin);
    candidate.normal = hitNormal;
    return true;
}

inline bool VoxelisXTraceBrickDDA(uint brickID, float3 entryPositionInBrick, float3 rayDir, float entryT, int3 entryNormal, out DDAHit hit, out int materialID)
{
    DDAClearHit(hit);
    materialID = 0;

    uint brickBase = VoxelisXBrickBase(brickID);
    uint coarseOccupancy = VoxelisXGetCoarseOccupancy(g_bricks[brickBase]);
    DDACursor cursor = DDACreateCursor(entryPositionInBrick, rayDir, SIZE_IN_BLOCKS);
    uint occLo = 0u;
    uint occHi = 0u;
    int prevTransparentBlock = -1;
    
    // materialID = coarseOccupancy;
    // DDAMakeHit(cursor, entryT, entryNormal, hit);
    // return true;

    [loop] for (int ddaStep = 0; ddaStep < BRICK_DDA_MAX_STEPS; ddaStep++)
    {
        if (!DDAIsInside(cursor, SIZE_IN_BLOCKS))
        {
            break;
        }

        int3 coarseCell = cursor.cell >> BRICK_MICRO_SHIFT;
        uint coarseBit = VoxelisXCoarseOccupancyBit(coarseCell);
        if ((coarseOccupancy & (1u << coarseBit)) == 0u)
        {
            // Transparent -> Air boundary
            if (prevTransparentBlock > 0)
            {
                materialID = 0;
                DDAMakeHit(cursor, entryT, entryNormal, hit);
                return true;
            }
            
            // Faster than compute the exact distance needed to jump multiple voxels
            prevTransparentBlock = 0;
            DDAStep(cursor);
            continue;
        }
        
        // Let L1 Cache do its job
        VoxelisXLoadMicroOccupancy(brickBase, coarseBit, occLo, occHi);
        if (!VoxelisXShouldTraceMicroOccupancy(occLo, occHi, rayDir))
        {
            // Transparent -> Air boundary
            if (prevTransparentBlock > 0)
            {
                materialID = 0;
                DDAMakeHit(cursor, entryT, entryNormal, hit);
                return true;
            }
            
            prevTransparentBlock = 0;
            DDAStep(cursor);
            continue;
        }

        int3 microCell = cursor.cell & BRICK_MICRO_MASK;
        uint microBit = VoxelisXMicroOccupancyBit(microCell);
        if (VoxelisXIsMicroOccupied(occLo, occHi, microBit))
        {
            int blockID = VoxelisXReadBrick(brickBase + BRICK_BLOCK_DATA_OFFSET, cursor.cell);
            materialID = blockID;
            DDAMakeHit(cursor, entryT, entryNormal, hit);
            return true;
            bool shouldTerminate = VoxelisXShouldTerminateBrickDDA(blockID, prevTransparentBlock);
            prevTransparentBlock = blockID;

            if (shouldTerminate)
            {
                materialID = blockID;
                DDAMakeHit(cursor, entryT, entryNormal, hit);
                return true;
            }
        }
        else if (prevTransparentBlock > 0)
        {
            materialID = 0;
            DDAMakeHit(cursor, entryT, entryNormal, hit);
            return true;
        }
        else
        {
            prevTransparentBlock = 0;
        }

        DDAStep(cursor);
    }
    
    // if (prevTransparentBlock > 0 && !IsOpaque(prevTransparentBlock))
    // {
    //     materialID = 0;
    //     DDAMakeHit(cursor, entryT, entryNormal, hit);
    //     return true;
    // }

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
    if (VoxelisXTraceBrickDDA(candidate.brickID, entryPositionInBrick, context.objectRayDirection, candidate.t, candidate.normal, ddaHit, materialID))
    {
        result.hit = true;
        result.t = ddaHit.t;
        result.materialID = materialID;
        result.objectNormal = half3(ddaHit.normal);
        result.faceHash = VoxelisXMakeVoxelFaceHash(candidate.brickOrigin + ddaHit.cell, ddaHit.normal);
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
        payload.voxelFaceHash = hit.faceHash;
    }

    bool hasPreviousTransparentMaterial = payload.previousTransparentMaterial != 0;
    VoxelMaterial previousTransparentMaterial = GET_MATERIAL(payload.previousTransparentMaterial);
    float3 ext = hasPreviousTransparentMaterial ? exp(-(1 - previousTransparentMaterial.albedo) * currentRayT * previousTransparentMaterial.extinction) : float3(1, 1, 1);
    // TODO: Remove me vvv
    ext = float3(1, 1, 1);

    if (IsOpaque(materialID))
    {
        payload.albedo = material.albedo.rgb * ext;
        payload.bounceIndexOpaque = payload.bounceIndexOpaque + 1;
        payload.emission = material.emission.rgb;

        float fresnelFactor = FresnelReflectAmountOpaque(1, material.IOR, worldRayDirection, worldNormal);
        float specularChance = lerp(material.metallic, 1, fresnelFactor * material.smoothness);
        // float doSpecular = (RandomFloat01(payload.rngState) < specularChance) ? 1 : 0;
        float doSpecular = 0;

        const float3 diffuseRayDir = normalize(worldNormal + RandomUnitVector(payload.rngState));
        float3 specularRayDir = reflect(worldRayDirection, worldNormal);
        specularRayDir = normalize(lerp(diffuseRayDir, specularRayDir, material.smoothness));
        float3 reflectedRayDir = lerp(diffuseRayDir, specularRayDir, doSpecular);

        payload.k = (doSpecular == 1) ? specularChance : 1 - specularChance;
        payload.bounceRayOrigin = worldHitPosition + K_RAY_ORIGIN_PUSH_OFF * worldNormal;
        payload.bounceRayDirection = reflectedRayDir;
        payload.previousTransparentMaterial = payload.previousTransparentMaterial;
        payload.worldNormal = worldNormal;
    }
    else
    {
        int sourceMaterialID = payload.previousTransparentMaterial;
        int destinationMaterialID = materialID;
        bool hasDestinationTransparentMaterial = destinationMaterialID != 0;

        float3 interfaceNormal = normalize(worldNormal);
        if (dot(worldRayDirection, interfaceNormal) > 0.0f)
        {
            interfaceNormal = -interfaceNormal;
        }

        float sourceIOR = hasPreviousTransparentMaterial ? previousTransparentMaterial.IOR : 1.0f;
        float destinationIOR = hasDestinationTransparentMaterial ? material.IOR : 1.0f;
        float eta = sourceIOR / destinationIOR;

        float3 reflectionRayDir = reflect(worldRayDirection, interfaceNormal);
        float3 refractionRayDir = refract(worldRayDirection, interfaceNormal, eta);
        bool canRefract = dot(refractionRayDir, refractionRayDir) > 0.000001f;

        float fresnelFactor = canRefract
            ? FresnelReflectAmountTransparent(sourceIOR, destinationIOR, worldRayDirection, interfaceNormal)
            : 1.0f;
        fresnelFactor = 0.0f;

        float doRefraction = (canRefract && RandomFloat01(payload.rngState) >= fresnelFactor) ? 1.0f : 0.0f;
        float3 bounceRayDir = normalize(lerp(reflectionRayDir, refractionRayDir, doRefraction));
        float pushOff = doRefraction == 1.0f ? -K_RAY_ORIGIN_PUSH_OFF : K_RAY_ORIGIN_PUSH_OFF;

        payload.k = doRefraction == 1.0f ? 1.0f - fresnelFactor : fresnelFactor;
        payload.albedo = ext;
        payload.emission = float3(0, 0, 0);
        // payload.emission = fresnelFactor.xxx;
        payload.bounceRayOrigin = worldHitPosition + pushOff * interfaceNormal;
        payload.bounceRayDirection = bounceRayDir;
        payload.worldNormal = interfaceNormal;
        payload.previousTransparentMaterial = doRefraction == 1.0f ? destinationMaterialID : sourceMaterialID;
        payload.bounceIndexTransparent = payload.bounceIndexTransparent + 1;
    }
}

#endif
