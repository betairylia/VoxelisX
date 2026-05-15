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

#ifndef BRICK_RAY_MAX_STEPS
#define BRICK_RAY_MAX_STEPS 22
#endif

#ifndef BRICK_COARSE_RAY_MAX_STEPS
#define BRICK_COARSE_RAY_MAX_STEPS 4
#endif

#ifndef BRICK_MICRO_RAY_MAX_STEPS
#define BRICK_MICRO_RAY_MAX_STEPS 10
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

#ifndef BRICK_RAY_GRID_EPSILON
#define BRICK_RAY_GRID_EPSILON 0.0001f
#endif

#ifndef BRICK_RAY_STEP_EPSILON
#define BRICK_RAY_STEP_EPSILON 0.001f
#endif

#ifndef BRICK_RAY_MIN_DIR
#define BRICK_RAY_MIN_DIR 1e-20f
#endif

#include "RayPayload.hlsl"
#include "VoxelisXUtils.hlsl"
#include "Utils/BlueNoise.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

struct VoxelisXBrickTraceContext
{
    float3 objectRayOrigin;
    float3 objectRayDirection;
    float currentRayT;
    uint primitiveIndex;
};

/*
float t
6bit normal
16bit matID
normalFlags: 0b000000 -> <0/low> X+ X- Y+ Y- Z+ Z- <5/high>, only 1 bit set; 0 means miss
*/
struct VoxelisXBrickHit
{
    float t;
    uint materialID_faceNormal;
};

struct VoxelisXBrickRayCursor
{
    int3 cell;
    float3 enteredSideDist;
    float localT;
    uint stepIndex;
};

struct VoxelisXBrickRayConstants
{
    half3 invDir;
    float3 tStart;
};

static half3 objectNormals[6] = {
    half3( 1, 0, 0),
    half3(-1, 0, 0),
    half3(0,  1, 0),
    half3(0, -1, 0),
    half3(0, 0,  1),
    half3(0, 0, -1),
};

inline half3 UnpackObjectNormal(uint normalFlag)
{
    return objectNormals[firstbitlow(normalFlag)];
}

StructuredBuffer<uint> g_bricks;
float4x4 _PrevObjectToWorld;
uint _SectorHashSeed;

inline VoxelisXBrickHit VoxelisXMakeBrickMiss()
{
    VoxelisXBrickHit hit;
    hit.t = 0.0f;
    hit.materialID_faceNormal = 0u;
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
    return 0u;
    uint voxelKey =
        uint(sectorLocalVoxelPos.x & 0x7F) |
        (uint(sectorLocalVoxelPos.y & 0x7F) << 7) |
        (uint(sectorLocalVoxelPos.z & 0x7F) << 14) |
        (VoxelisXFaceID(normal) << 21);

    uint hash = VoxelisXHashAvalanche(_SectorHashSeed ^ voxelKey) & VOXEL_FACE_HASH_MASK;
    return hash == 0u ? 1u : (hash & 0xFFFF);
}

inline int3 VoxelisXBrickRayIntMask(bool3 mask)
{
    return int3(mask.x ? 1 : 0, mask.y ? 1 : 0, mask.z ? 1 : 0);
}

inline int VoxelisXBrickRayStepAxis(half direction)
{
    return direction > 0.0h ? 1 : -1;
}

inline int3 VoxelisXBrickRayStepDirection(half3 rayDir)
{
    return int3(
        VoxelisXBrickRayStepAxis(rayDir.x),
        VoxelisXBrickRayStepAxis(rayDir.y),
        VoxelisXBrickRayStepAxis(rayDir.z));
}

inline half VoxelisXBrickRaySafeInvAxis(float direction)
{
    if (abs(direction) >= BRICK_RAY_MIN_DIR)
    {
        return 1.0f / direction;
    }

    return direction < 0.0f ? -1.0f / BRICK_RAY_MIN_DIR : 1.0f / BRICK_RAY_MIN_DIR;
}

inline half3 VoxelisXBrickRaySafeInvDir(float3 rayDir)
{
    return half3(
        VoxelisXBrickRaySafeInvAxis(rayDir.x),
        VoxelisXBrickRaySafeInvAxis(rayDir.y),
        VoxelisXBrickRaySafeInvAxis(rayDir.z));
}

inline float3 VoxelisXBrickRayBoundaryOffset(float3 rayDir)
{
    return float3(
        rayDir.x >= 0.0f ? 1.0f : 0.0f,
        rayDir.y >= 0.0f ? 1.0f : 0.0f,
        rayDir.z >= 0.0f ? 1.0f : 0.0f);
}

inline VoxelisXBrickRayCursor VoxelisXCreateBrickRayCursor(float3 entryPositionInGrid, int gridSize)
{
    VoxelisXBrickRayCursor cursor;

    float3 gridMax = float3(gridSize, gridSize, gridSize) - BRICK_RAY_GRID_EPSILON;
    float3 entryPos = clamp(entryPositionInGrid, 0.0f, gridMax);

    cursor.cell = int3(floor(entryPos));
    cursor.enteredSideDist = float3(0.0f, 0.0f, 0.0f);
    cursor.localT = 0.0f;
    cursor.stepIndex = 0u;

    return cursor;
}

inline VoxelisXBrickRayConstants VoxelisXCreateBrickRayConstants(float3 entryPositionInGrid, float3 rayDir)
{
    VoxelisXBrickRayConstants constants;
    constants.invDir = VoxelisXBrickRaySafeInvDir(rayDir);
    constants.tStart = (VoxelisXBrickRayBoundaryOffset(rayDir) - entryPositionInGrid) * constants.invDir;
    return constants;
}

inline bool VoxelisXBrickRayIsInside(VoxelisXBrickRayCursor cursor, int gridSize)
{
    return !any(uint3(cursor.cell) >= uint(gridSize));
}

inline bool3 VoxelisXBrickRayNextAxisMask(float3 sideDist, float nextT)
{
    return sideDist <= float3(
        nextT + BRICK_RAY_STEP_EPSILON,
        nextT + BRICK_RAY_STEP_EPSILON,
        nextT + BRICK_RAY_STEP_EPSILON);
}

inline void VoxelisXJumpBrickRay(inout VoxelisXBrickRayCursor cursor, float3 rayDir, int mask)
{
    cursor.cell = lerp(cursor.cell | mask, cursor.cell & (~mask), rayDir < 0);
}

inline void VoxelisXStepBrickRay(inout VoxelisXBrickRayCursor cursor, float3 entryPositionInGrid, half3 rayDir, VoxelisXBrickRayConstants constants)
{
    float3 sideDist = constants.tStart + float3(cursor.cell) * constants.invDir;
    float nextT = min(min(sideDist.x, sideDist.y), sideDist.z);

    cursor.enteredSideDist = sideDist;
    cursor.localT = nextT;
    cursor.cell = int3(floor(entryPositionInGrid + (nextT + BRICK_RAY_STEP_EPSILON) * rayDir));
    cursor.stepIndex++;
}

inline uint VoxelisXBrickRayNormalFlags(VoxelisXBrickRayCursor cursor, half3 rayDir, uint entryNormalFlags)
{
    if (cursor.stepIndex == 0u)
    {
        return entryNormalFlags;
    }

    bool3 axisMask = VoxelisXBrickRayNextAxisMask(cursor.enteredSideDist, cursor.localT);
    // This is brutal. Ideas?
    // Ties can happen on voxel edges/corners. Pick one face deterministically,
    // matching the X/Y/Z priority used by the brick AABB entry normal.
    uint xUse = axisMask.x ? 0xFFFFFFFFu : 0u;
    uint yUse = (axisMask.y ? 0xFFFFFFFFu : 0u) & ~xUse;
    uint zUse = ~(xUse | yUse);

    uint xFlag = rayDir.x > 0.0h ? 0b000010 : 0b000001;
    uint yFlag = rayDir.y > 0.0h ? 0b001000 : 0b000100;
    uint zFlag = rayDir.z > 0.0h ? 0b100000 : 0b010000;

    return (xFlag & xUse) | (yFlag & yUse) | (zFlag & zUse);
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

inline bool VoxelisXShouldTraceMicroOccupancy(uint occLo, uint occHi, half3 rayDir)
{
    // Future LUT early rejection can key off the 64-bit occupancy and a binned ray direction here.
    return true;
}

inline bool VoxelisXShouldTerminateBrickRay(int blockID, VoxelisXBrickRayCursor cursor, half3 rayDir, uint entryNormalFlags, float entryT)
{
    bool shouldTerminate = IsOpaque(blockID);
    UNITY_BRANCH if (shouldTerminate) return shouldTerminate;
    
    // Transparency
    if (cursor.stepIndex == 0 && entryT == 0) return false;
    shouldTerminate = GetFaceBits(blockID) & VoxelisXBrickRayNormalFlags(cursor, rayDir, entryNormalFlags);
    return shouldTerminate;
}

inline bool VoxelisXTraceBrickRay(float3 entryPositionInBrick, float3 rayDir, float entryT, uint entryNormalFlags, out VoxelisXBrickHit hit)
{
    uint entryNormal_coarseOccupancy = (entryNormalFlags << 26) | VoxelisXGetCoarseOccupancy(
        g_bricks[VoxelisXBrickBase(PrimitiveIndex())]);
    VoxelisXBrickRayCursor cursor = VoxelisXCreateBrickRayCursor(entryPositionInBrick, SIZE_IN_BLOCKS);
    VoxelisXBrickRayConstants rayConstants = VoxelisXCreateBrickRayConstants(entryPositionInBrick, rayDir);
    uint occLo = 0u;
    uint occHi = 0u;
    
    // hit.t = entryT;
    // hit.materialID_faceNormal = ((entryNormal_coarseOccupancy & 0xFFFF) << 16) + (entryNormal_coarseOccupancy >> 26);
    // return true;

    [loop] for (int rayStep = 0; rayStep < BRICK_RAY_MAX_STEPS; rayStep++)
    {
        if (!VoxelisXBrickRayIsInside(cursor, SIZE_IN_BLOCKS))
        {
            break;
        }

        int3 coarseCell = cursor.cell >> BRICK_MICRO_SHIFT;
        uint coarseBit = VoxelisXCoarseOccupancyBit(coarseCell);
        if ((entryNormal_coarseOccupancy & (1u << coarseBit)) == 0u)
        {
            // Faster than compute the exact distance needed to jump multiple voxels
            VoxelisXJumpBrickRay(cursor, rayDir, 3u);
            VoxelisXStepBrickRay(cursor, entryPositionInBrick, rayDir, rayConstants);
            continue;
        }
        
        // Let L1 Cache do its job
        VoxelisXLoadMicroOccupancy(VoxelisXBrickBase(PrimitiveIndex()), coarseBit, occLo, occHi);
        if (!VoxelisXShouldTraceMicroOccupancy(occLo, occHi, rayDir))
        {
            VoxelisXStepBrickRay(cursor, entryPositionInBrick, rayDir, rayConstants);
            continue;
        }

        int3 microCell = cursor.cell & BRICK_MICRO_MASK;
        uint microBit = VoxelisXMicroOccupancyBit(microCell);
        if (VoxelisXIsMicroOccupied(occLo, occHi, microBit))
        {
            int blockID = VoxelisXReadBrick(VoxelisXBrickBase(PrimitiveIndex()) + BRICK_BLOCK_DATA_OFFSET, cursor.cell);
            bool shouldTerminate = VoxelisXShouldTerminateBrickRay(
                blockID, cursor, rayDir, (entryNormal_coarseOccupancy >> 26), entryT);

            if (shouldTerminate)
            {
                hit.t = entryT + cursor.localT;
                hit.materialID_faceNormal = (blockID << 16) + VoxelisXBrickRayNormalFlags(cursor, rayDir, (entryNormal_coarseOccupancy >> 26));
                return true;
            }
        }

        VoxelisXStepBrickRay(cursor, entryPositionInBrick, rayDir, rayConstants);
    }

    return false;
}

inline VoxelisXBrickHit VoxelisXTraceBrickPrimitive()
{
    uint brickInfo = g_bricks[VoxelisXBrickBase(PrimitiveIndex())];
    
    // Empty brick
    // if(VoxelisXGetCoarseOccupancy(brickInfo) == 0)
    // {
    //     return false;
    // }

    // AABB Intersection
    uint idx = brickInfo & BRICK_INFO_ABSOLUTE_INDEX_MASK;
    int bX = int((idx & BRICK_POS_MASK) << SHIFT_SIZE_IN_BLOCKS);
    int bY = int(((idx >> SHIFT_SIZE_IN_BRICKS) & BRICK_POS_MASK) << SHIFT_SIZE_IN_BLOCKS);
    int bZ = int((idx >> (SHIFT_SIZE_IN_BRICKS + SHIFT_SIZE_IN_BRICKS)) << SHIFT_SIZE_IN_BLOCKS);

    float3 aabbMin = float3(bX, bY, bZ);
    float3 aabbMax = float3(bX + SIZE_IN_BLOCKS, bY + SIZE_IN_BLOCKS, bZ + SIZE_IN_BLOCKS);

    float3 rayDir = ObjectRayDirection();
    half3 invDir = 1.0h / rayDir;
    float3 t0 = (aabbMin - ObjectRayOrigin()) * invDir;
    float3 t1 = (aabbMax - ObjectRayOrigin()) * invDir;

    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);

    float largestTmin = max(max(tmin.x, tmin.y), tmin.z);
    float smallestTmax = min(min(tmax.x, tmax.y), tmax.z);

    if (largestTmin > smallestTmax || smallestTmax < 0 || largestTmin > RayTCurrent())
    {
        VoxelisXBrickHit result = VoxelisXMakeBrickMiss();
        return result;
    }
    
    float t = max(0, largestTmin);
    float3 entryPositionInBrick = ObjectRayOrigin() + rayDir * t - float3(bX, bY, bZ);
    
    // TODO: Do coarse bit (2x2x2) early reject here? 
    // VoxelisXGetCoarseOccupancy(brickInfo)

    // TODO: branchless?
    uint normalFlags;
    if (largestTmin == tmin.x)
    {
        normalFlags = rayDir.x > 0 ? 0b000010 : 0b000001;
    }
    else if (largestTmin == tmin.y)
    {
        normalFlags = rayDir.y > 0 ? 0b001000 : 0b000100;
    }
    else
    {
        normalFlags = rayDir.z > 0 ? 0b100000 : 0b010000;
    }

    VoxelisXBrickHit result = VoxelisXMakeBrickMiss();
    VoxelisXTraceBrickRay(entryPositionInBrick, rayDir, t, normalFlags, result);

    return result;
}

#endif
