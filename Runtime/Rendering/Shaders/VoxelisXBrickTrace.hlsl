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

#define BRICK_INFO_WORDS 1
#define BRICK_OCCUPANCY_WORDS 16
#define BRICK_BLOCK_DATA_OFFSET 17
#define BRICK_DATA_LENGTH 273
#define BRICK_INFO_ABSOLUTE_INDEX_MASK 0xFFFu
#define BRICK_INFO_COARSE_OCCUPANCY_SHIFT 16u
#define BRICK_INFO_COARSE_OCCUPANCY_MASK 0xFFu

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
    int3 brickOrigin;
    float t;
    int3 normal;
};

StructuredBuffer<uint> g_bricks;

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

// Mirrors SectorRenderer.{ToCoarseOccupancyBit, ToMicroOccupancyBit, ToOccupancyWordOffset}.
// Returns the word offset (relative to occupancyBase) and the bit index within that word
// for the block at `cell` in the brick's 8x8x8 block grid.
inline uint VoxelisXMaskWordOffset(int3 cell, out uint bitInWord)
{
    int3 coarseCell = cell >> 2;
    int3 microCell = cell & 3;
    uint coarseBit = uint(coarseCell.x | (coarseCell.y << 1) | (coarseCell.z << 2));
    uint microBit  = uint(microCell.x  | (microCell.y  << 2) | (microCell.z  << 4));
    bitInWord = microBit & 31u;
    return coarseBit * 2u + (microBit >> 5);
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
    candidate.brickOrigin = int3(0, 0, 0);
    candidate.t = 0.0f;
    candidate.normal = int3(0, 0, 0);

    uint brickInfo = g_bricks[VoxelisXBrickBase(candidate.brickID)];
    if (VoxelisXGetCoarseOccupancy(brickInfo) == 0u)
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
    candidate.brickOrigin = int3(bX, bY, bZ);
    candidate.t = max(0, largestTmin);
    candidate.normal = hitNormal;
    return true;
}

// Flat single-level DDA over the brick's 8x8x8 block grid.
//
// The previous two-level (2x2x2 coarse + 4x4x4 micro) variant preloaded all 16
// occupancy words into uint4s and selected per coarse cell with a switch. That
// kept ~16 GPRs live across the loop, lowering wave occupancy enough to cancel
// the L1 read savings, and added per-coarse-cell setup (switch dispatch + cursor
// reseed) on top.
//
// This version keeps zero mask state in registers: each step computes which
// occupancy word to read via VoxelisXMaskWordOffset and bit-tests it. After the
// first reads into a brick the relevant words sit in L1, so subsequent reads
// are cheap, and the higher achievable wave occupancy more than pays for the
// extra loads.
inline bool VoxelisXTraceBrickDDA(uint brickID, float3 entryPositionInBrick, float3 rayDir, float entryT, int3 entryNormal, out DDAHit hit, out int materialID)
{
    DDAClearHit(hit);
    materialID = 0;

    uint brickBase = VoxelisXBrickBase(brickID);
    uint occupancyBase = brickBase + BRICK_INFO_WORDS;
    uint blockBase = brickBase + BRICK_BLOCK_DATA_OFFSET;

    int3 gridSize = int3(SIZE_IN_BLOCKS, SIZE_IN_BLOCKS, SIZE_IN_BLOCKS);
    DDACursor cursor = DDACreateCursor(entryPositionInBrick, rayDir, gridSize);
    int prevTransparentBlock = 0;

    [loop]
    for (int step = 0; step < BRICK_DDA_MAX_STEPS; step++)
    {
        if (!DDAIsInside(cursor, gridSize))
        {
            break;
        }

        uint bitInWord;
        uint wordOffset = VoxelisXMaskWordOffset(cursor.cell, bitInWord);
        uint maskWord = g_bricks[occupancyBase + wordOffset];

        if ((maskWord & (1u << bitInWord)) != 0u)
        {
            int blockID = VoxelisXReadBrick(blockBase, cursor.cell);
            bool shouldTerminate = VoxelisXShouldTerminateBrickDDA(blockID, prevTransparentBlock);
            prevTransparentBlock = blockID;

            if (shouldTerminate)
            {
                materialID = blockID;
                DDAMakeHit(cursor, entryT, entryNormal, hit);
                return true;
            }
        }

        DDAStep(cursor);
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
    if (VoxelisXTraceBrickDDA(candidate.brickID, entryPositionInBrick, context.objectRayDirection, candidate.t, candidate.normal, ddaHit, materialID))
    {
        result.hit = true;
        result.t = ddaHit.t;
        result.materialID = materialID;
        result.objectNormal = half3(ddaHit.normal);
    }

    return result;
}

inline void VoxelisXApplyVoxelClosestHit(inout RayPayload payload, VoxelisXBrickHit hit, float3 worldRayOrigin, float3 worldRayDirection, float currentRayT, float3x4 objectToWorld)
{
    int materialID = hit.materialID;
    VoxelMaterial material = GET_MATERIAL(materialID);

    float3 worldHitPosition = worldRayOrigin + worldRayDirection * currentRayT;
    float3 worldNormal = mul((float3x3)objectToWorld, hit.objectNormal);

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
