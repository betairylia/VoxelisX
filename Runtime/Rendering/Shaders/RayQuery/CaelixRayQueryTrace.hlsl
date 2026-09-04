#ifndef CAELIX_RAY_QUERY_TRACE_INCLUDED
#define CAELIX_RAY_QUERY_TRACE_INCLUDED

// Inline ray query replacement for TraceRay + the BrickRTTest hit group. The including compute
// kernel must define CAELIX_INLINE_RAY_QUERY, CAELIX_LAUNCH_INDEX, and declare
// `RaytracingAccelerationStructure g_AccelStruct;` before including this file.

#include "../Utils/Utils.hlsl"

// Brick pool pages. One GraphicsBuffer cannot exceed SystemInfo.maxGraphicsBufferSize (~3.9 GB),
// and a large scene holds more brick records than that, so the pool is split into up to four
// buffers and every instance record names the page its bricks live in. The page is selected once
// per candidate (below) and every brick load switches on it. Must match CaelixBrickPool.MaxPages.
#define CAELIX_BRICK_PAGES 4
ByteAddressBuffer g_bricks0;
ByteAddressBuffer g_bricks1;
ByteAddressBuffer g_bricks2;
ByteAddressBuffer g_bricks3;
static uint _CaelixBrickPage;

uint CaelixBrickPageLoad(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load(byteAddress);
        case 1u: return g_bricks1.Load(byteAddress);
        case 2u: return g_bricks2.Load(byteAddress);
        default: return g_bricks3.Load(byteAddress);
    }
}

uint2 CaelixBrickPageLoad2(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load2(byteAddress);
        case 1u: return g_bricks1.Load2(byteAddress);
        case 2u: return g_bricks2.Load2(byteAddress);
        default: return g_bricks3.Load2(byteAddress);
    }
}

uint64_t CaelixBrickPageLoad64(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load<uint64_t>(byteAddress);
        case 1u: return g_bricks1.Load<uint64_t>(byteAddress);
        case 2u: return g_bricks2.Load<uint64_t>(byteAddress);
        default: return g_bricks3.Load<uint64_t>(byteAddress);
    }
}

#define CAELIX_BRICKS_LOAD(byteAddress) CaelixBrickPageLoad(byteAddress)
#define CAELIX_BRICKS_LOAD2(byteAddress) CaelixBrickPageLoad2(byteAddress)
#define CAELIX_BRICKS_LOAD64(byteAddress) CaelixBrickPageLoad64(byteAddress)

#include "../CaelixBrickTrace.hlsl"

// One record per RTAS instance, indexed by InstanceID(). Mirrors Caelix.Rendering.RayQuery.CaelixRayQueryInstance.
struct CaelixRayQueryInstance
{
    // Rows of the previous frame's object-to-world matrix (Matrix4x4.GetRow), stored as rows so
    // the layout does not depend on HLSL matrix packing rules.
    float4 prevRow0;
    float4 prevRow1;
    float4 prevRow2;
    float4 prevRow3;
    // Word offset of this sector's first brick inside its page.
    uint brickBase;
    uint hashSeed;
    // Brick pool page holding this sector's bricks.
    uint page;
    uint pad1;
};

StructuredBuffer<CaelixRayQueryInstance> g_Instances;

// Same contract as the DXR hit group: on a hit the payload carries T, the packed
// (blockID << 16 | faceNormalFlags) word, the packed world normal and the packed previous-frame
// world offset; on a miss the payload stays cleared.
void CaelixRayQueryTrace(RayDesc ray, out RayPayload payload)
{
    CaelixClearRayPayload(payload);

    UnityRayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(g_AccelStruct, RAY_FLAG_NONE, 0xFF, ray);

    uint committedAttrib = 0u;
    uint committedInstance = 0u;

    while (q.Proceed())
    {
        if (q.CandidateType() != CANDIDATE_PROCEDURAL_PRIMITIVE)
        {
            continue;
        }

        uint instanceId = q.CandidateInstanceID();
        // One fetch: the record carries both the page every brick load below switches on and the
        // sector's word offset inside that page.
        CaelixRayQueryInstance inst = g_Instances[instanceId];
        _CaelixBrickPage = inst.page;
        uint brickBase = inst.brickBase + CaelixBrickBase(q.CandidatePrimitiveIndex());

        AttributeData attrib;
        float t = CaelixTraceBrickPrimitiveCore(
            brickBase, q.CandidateObjectRayOrigin(), q.CandidateObjectRayDirection(), q.CommittedRayT(), attrib);

        // ReportHit in the DXR path rejects t outside [TMin, TCurrent]; here that is our job.
        if (attrib.matID_faceNormal != 0u && t >= ray.TMin && t < q.CommittedRayT())
        {
            q.CommitProceduralPrimitiveHit(t);
            committedAttrib = attrib.matID_faceNormal;
            committedInstance = instanceId;
        }
    }

    if (q.CommittedStatus() != COMMITTED_PROCEDURAL_PRIMITIVE_HIT)
    {
        return;
    }

    float T = q.CommittedRayT();
    float3x4 objectToWorld = q.CommittedObjectToWorld3x4();

    payload.T = T;
    payload.materialID_voxelFaceHash = committedAttrib;
    payload.packedWorldNormal = CaelixPackWorldNormal(mul((float3x3)objectToWorld, UnpackObjectNormal(committedAttrib)));

    CaelixRayQueryInstance inst = g_Instances[committedInstance];
    float4x4 prevObjectToWorld = float4x4(inst.prevRow0, inst.prevRow1, inst.prevRow2, inst.prevRow3);
    float3 objectHitPosition = q.CommittedObjectRayOrigin() + q.CommittedObjectRayDirection() * T;
    float3 worldHitPosition = ray.Origin + ray.Direction * T;
    payload.packedPrevWorldOffset = CaelixPackFloat3ToHalf4(
        mul(prevObjectToWorld, float4(objectHitPosition, 1.0f)).xyz - worldHitPosition);
}

#endif
