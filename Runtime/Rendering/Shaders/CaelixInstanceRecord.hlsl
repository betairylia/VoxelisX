#ifndef CAELIX_INSTANCE_RECORD_INCLUDED
#define CAELIX_INSTANCE_RECORD_INCLUDED

// The per-RTAS-instance record. An inline ray query has no shader table, so this is how
// per-instance data reaches the trace: the kernel reads the record of every procedural candidate.

// One record per RTAS instance, indexed by InstanceID(). Mirrors Caelix.Rendering.RayQuery.CaelixRayQueryInstance.
struct CaelixRayQueryInstance
{
    // Rows of the previous frame's object-to-world matrix (Matrix4x4.GetRow), stored as rows so
    // the layout does not depend on HLSL matrix packing rules.
    float4 prevRow0;
    float4 prevRow1;
    float4 prevRow2;
    float4 prevRow3;
    // Word offset of this group's first brick inside its page.
    uint brickBase;
    uint hashSeed;
    // Brick pool page holding this group's bricks.
    uint page;
    uint pad1;
};

StructuredBuffer<CaelixRayQueryInstance> g_Instances;

inline float4x4 CaelixInstancePrevObjectToWorld(CaelixRayQueryInstance inst)
{
    return float4x4(inst.prevRow0, inst.prevRow1, inst.prevRow2, inst.prevRow3);
}

#endif
