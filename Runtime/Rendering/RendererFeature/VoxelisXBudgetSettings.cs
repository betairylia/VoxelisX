using System;
using UnityEngine;

/// <summary>
/// Ray budget for <see cref="VoxelisXBudgetGBufferPass"/>. Built from the budget feature's
/// serialized fields once per camera and handed to the stage as an immutable snapshot.
/// </summary>
/// <remarks>
/// Every field here bounds work rather than describing an effect: the point of budget mode is that
/// the per-pixel ray count is knowable from this struct alone. Worst case per pixel is
/// <c>2 + transparentSkipLimit</c> chain rays, plus <c>1 + aoSampleCount</c> visibility rays, each
/// of which may restart up to 4 times through transparent voxels.
/// </remarks>
[Serializable]
public struct VoxelisXBudgetTraceSettings
{
    /// <summary>Build the acceleration structure inside the pass. Disable if it is built elsewhere.</summary>
    public bool buildAccelerationStructure;

    [Tooltip("Take one reflection/refraction step at the first mirror-smooth surface. Off leaves mirrors to shade as ordinary surfaces and turns glass into a pure colour filter with no bending.")]
    public bool enableDelta;

    [Tooltip("How many transparent interfaces may be passed straight through before the chain gives up and shades whatever it is on. A pane of glass costs two (the delta on the way in, one pass-through on the way out), so 6 clears roughly three panes.")]
    [Min(0)] public int transparentSkipLimit;

    [Tooltip("Smoothness at or above which a surface is treated as a delta (mirror / clear glass) rather than something to shade.")]
    [Range(0.0f, 1.0f)] public float deltaSmoothnessThreshold;

    [Tooltip("Trace one hard shadow ray towards the main directional light.")]
    public bool enableSunShadow;

    [Tooltip("AO rays per pixel per frame. 1 is the intended budget; the spatial and temporal filters do the rest.")]
    [Min(1)] public int aoSampleCount;

    /// <summary>K_MAX: how far an AO ray reaches, in world units. Nothing beyond it occludes.</summary>
    [Tooltip("K_MAX. How far an AO ray reaches, in world units (voxels). Geometry further away than this never occludes.")]
    [Min(0.0f)] public float aoMaxDistance;

    /// <summary>Upper bound on the convergence counter fed to the tracer.</summary>
    public int maximumAverageFrames;

    public static VoxelisXBudgetTraceSettings Default => new VoxelisXBudgetTraceSettings
    {
        buildAccelerationStructure = true,
        enableDelta = true,
        transparentSkipLimit = 6,
        deltaSmoothnessThreshold = 0.99f,
        enableSunShadow = true,
        aoSampleCount = 1,
        aoMaxDistance = 20.0f,
        maximumAverageFrames = 120
    };

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public VoxelisXBudgetTraceSettings Validated()
    {
        VoxelisXBudgetTraceSettings v = this;
        v.transparentSkipLimit = Mathf.Clamp(v.transparentSkipLimit, 0, 16);
        v.deltaSmoothnessThreshold = Mathf.Clamp01(v.deltaSmoothnessThreshold);
        v.aoSampleCount = Mathf.Clamp(v.aoSampleCount, 1, 64);
        v.aoMaxDistance = Mathf.Max(0.0f, v.aoMaxDistance);
        v.maximumAverageFrames = Mathf.Max(1, v.maximumAverageFrames);
        return v;
    }
}

/// <summary>
/// The deferred shading half of budget mode: what the sky cubemap contributes and how far the
/// occlusion term is allowed to reach.
/// </summary>
[Serializable]
public struct VoxelisXBudgetShadingSettings
{
    [Tooltip("Multiplier on every sky cubemap lookup. Matches the path tracer's own sky scale by default.")]
    [Min(0.0f)] public float skyIntensity;

    [Tooltip("Mip sampled along the surface normal to stand in for sky irradiance. Higher is blurrier; clamps to the last mip if the bound cubemap has no chain.")]
    [Min(0.0f)] public float skyDiffuseMip;

    [Tooltip("Mip sampled along the reflection vector at roughness 1. Roughness scales linearly into this.")]
    [Min(0.0f)] public float skySpecularMaxMip;

    [Tooltip("How strongly AO darkens the sky ambient term. 0 disables occlusion without disabling the rays.")]
    [Range(0.0f, 1.0f)] public float aoStrength;

    [Tooltip("How much of the AO term also applies to ambient specular. 1 treats reflections like the diffuse ambient, 0 leaves them unoccluded.")]
    [Range(0.0f, 1.0f)] public float aoAffectsSpecular;

    public static VoxelisXBudgetShadingSettings Default => new VoxelisXBudgetShadingSettings
    {
        skyIntensity = 2.0f,
        skyDiffuseMip = 6.0f,
        skySpecularMaxMip = 6.0f,
        aoStrength = 1.0f,
        aoAffectsSpecular = 1.0f
    };

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public VoxelisXBudgetShadingSettings Validated()
    {
        VoxelisXBudgetShadingSettings v = this;
        v.skyIntensity = Mathf.Max(0.0f, v.skyIntensity);
        v.skyDiffuseMip = Mathf.Max(0.0f, v.skyDiffuseMip);
        v.skySpecularMaxMip = Mathf.Max(0.0f, v.skySpecularMaxMip);
        v.aoStrength = Mathf.Clamp01(v.aoStrength);
        v.aoAffectsSpecular = Mathf.Clamp01(v.aoAffectsSpecular);
        return v;
    }
}
