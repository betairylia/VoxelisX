using System;
using UnityEngine;

/// <summary>
/// What the present stage displays. Values are serialized in renderer assets — keep them stable.
/// </summary>
public enum VoxelisXDebugView
{
    /// <summary>The composited image.</summary>
    Regular = 0,
    /// <summary>Screen-space motion vectors, encoded to a red/green/blue wheel.</summary>
    MotionVector = 1,
    /// <summary>Surface albedo.</summary>
    Albedo = 2,
    /// <summary>Surface normal, decoded from its octahedral encoding.</summary>
    Normal = 3,
    /// <summary>Clip-space depth as greyscale.</summary>
    Depth = 4,
    /// <summary>Non-denoised radiance only: primary emission plus sky.</summary>
    DeterministicRadiance = 5,
    /// <summary>Combined stochastic radiance (diffuse + specular), before filtering.</summary>
    IndirectRadianceRaw = 6,
    /// <summary>Combined stochastic radiance after the spatial filter, before temporal accumulation.</summary>
    IndirectRadianceFiltered = 7,
    /// <summary>Combined stochastic radiance after temporal accumulation, as fed to the composite.</summary>
    IndirectRadianceAccumulated = 8,
    /// <summary>Stochastic diffuse-path radiance straight out of the tracer (hit distance in alpha).</summary>
    StochasticDiffuse = 9,
    /// <summary>Stochastic specular-path radiance straight out of the tracer (hit distance in alpha).</summary>
    StochasticSpecular = 10
}

/// <summary>
/// Ray tracing parameters for the G-buffer stage. Built from the renderer feature's serialized
/// fields once per camera and handed to the stage as an immutable snapshot.
/// </summary>
[Serializable]
public struct VoxelisXTraceSettings
{
    /// <summary>Build the acceleration structure inside the pass. Disable if it is built elsewhere.</summary>
    public bool buildAccelerationStructure;
    public int bounceCountOpaque;
    public int bounceCountTransparent;
    public int samplesPerPixel;
    public bool enableSkySun;
    public float sunDiskRadiusRadians;
    public float sunFlareRadiusRadians;
    /// <summary>Upper bound on the convergence counter fed to the tracer.</summary>
    public int maximumAverageFrames;

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public VoxelisXTraceSettings Validated()
    {
        VoxelisXTraceSettings v = this;
        v.bounceCountOpaque = Mathf.Max(0, v.bounceCountOpaque);
        v.bounceCountTransparent = Mathf.Max(0, v.bounceCountTransparent);
        v.samplesPerPixel = Mathf.Max(1, v.samplesPerPixel);
        v.sunDiskRadiusRadians = Mathf.Max(0.0f, v.sunDiskRadiusRadians);
        v.sunFlareRadiusRadians = Mathf.Max(v.sunDiskRadiusRadians, v.sunFlareRadiusRadians);
        v.maximumAverageFrames = Mathf.Max(1, v.maximumAverageFrames);
        return v;
    }
}

/// <summary>
/// Temporal accumulation parameters for the denoise stage.
/// </summary>
[Serializable]
public struct VoxelisXTemporalRadianceSettings
{
    public bool enabled;
    /// <summary>Floor on the current frame's weight, so accumulation never fully stops responding.</summary>
    public float currentFrameMinWeight;
    public bool depthRejection;
    public float depthTolerance;
    public float relativeDepthTolerance;
    public bool normalRejection;
    /// <summary>Minimum dot product between current and reprojected normals to accept history.</summary>
    public float normalThreshold;
    public bool bilinearHistory;
    /// <summary>Accumulation window length; also the convergence counter clamp.</summary>
    public int maximumAverageFrames;

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public VoxelisXTemporalRadianceSettings Validated()
    {
        VoxelisXTemporalRadianceSettings v = this;
        v.currentFrameMinWeight = Mathf.Clamp01(v.currentFrameMinWeight);
        v.depthTolerance = Mathf.Max(0.0f, v.depthTolerance);
        v.relativeDepthTolerance = Mathf.Max(0.0f, v.relativeDepthTolerance);
        v.normalThreshold = Mathf.Clamp(v.normalThreshold, -1.0f, 1.0f);
        v.maximumAverageFrames = Mathf.Max(1, v.maximumAverageFrames);
        return v;
    }
}
