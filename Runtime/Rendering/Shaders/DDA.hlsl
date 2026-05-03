#ifndef VOXELISX_DDA_INCLUDED
#define VOXELISX_DDA_INCLUDED

#ifndef DDA_GRID_EPSILON
#define DDA_GRID_EPSILON 0.0001f
#endif

#ifndef DDA_MIN_RAY_DIR
#define DDA_MIN_RAY_DIR 1e-20f
#endif

// Buffer reads and termination policy stay with the caller so the same cursor can drive brick or hierarchy traversal.
struct DDACursor
{
    int3 cell;
    int3 step;
    float3 deltaDist;
    float3 sideDist;
    bool3 axisMask;
    uint stepIndex;
};

struct DDAHit
{
    bool hit;
    int3 cell;
    int3 normal;
    float t;
    uint stepIndex;
};

inline float3 DDAFloatMask(bool3 mask)
{
    return float3(mask.x ? 1.0f : 0.0f, mask.y ? 1.0f : 0.0f, mask.z ? 1.0f : 0.0f);
}

inline int3 DDAIntMask(bool3 mask)
{
    return int3(mask.x ? 1 : 0, mask.y ? 1 : 0, mask.z ? 1 : 0);
}

inline void DDAClearHit(out DDAHit hit)
{
    hit.hit = false;
    hit.cell = int3(0, 0, 0);
    hit.normal = int3(0, 0, 0);
    hit.t = 0.0f;
    hit.stepIndex = 0;
}

inline DDACursor DDACreateCursor(float3 entryPositionInGrid, float3 rayDir, int3 gridSize)
{
    DDACursor cursor;

    float3 gridMax = float3(gridSize.x, gridSize.y, gridSize.z) - DDA_GRID_EPSILON;
    float3 entryPos = clamp(entryPositionInGrid, 0.0f, gridMax);
    float3 raySign = sign(rayDir);
    float rayLength = length(rayDir);

    cursor.cell = int3(floor(entryPos));
    cursor.step = int3(raySign);
    cursor.deltaDist = rayLength / max(abs(rayDir), DDA_MIN_RAY_DIR);
    cursor.sideDist = (raySign * (float3(cursor.cell) - entryPos) + (raySign * 0.5f) + 0.5f) * cursor.deltaDist;
    cursor.axisMask = bool3(false, false, false);
    cursor.stepIndex = 0;

    return cursor;
}

inline bool DDAIsInside(DDACursor cursor, int3 gridSize)
{
    return !any(cursor.cell < 0) && !any(cursor.cell >= gridSize);
}

inline bool3 DDANextAxisMask(float3 sideDist)
{
    return sideDist.xyz <= min(sideDist.yzx, sideDist.zxy);
}

inline void DDAStep(inout DDACursor cursor)
{
    cursor.axisMask = DDANextAxisMask(cursor.sideDist);
    cursor.sideDist += DDAFloatMask(cursor.axisMask) * cursor.deltaDist;
    cursor.cell += DDAIntMask(cursor.axisMask) * cursor.step;
    cursor.stepIndex++;
}

inline float DDACurrentT(DDACursor cursor, float entryT)
{
    return entryT + dot(DDAFloatMask(cursor.axisMask), cursor.sideDist - cursor.deltaDist);
}

inline int3 DDACurrentNormal(DDACursor cursor, int3 entryNormal)
{
    return cursor.stepIndex == 0 ? entryNormal : DDAIntMask(cursor.axisMask) * -cursor.step;
}

inline void DDAMakeHit(DDACursor cursor, float entryT, int3 entryNormal, out DDAHit hit)
{
    hit.hit = true;
    hit.cell = cursor.cell;
    hit.normal = DDACurrentNormal(cursor, entryNormal);
    hit.t = DDACurrentT(cursor, entryT);
    hit.stepIndex = cursor.stepIndex;
}

#endif
