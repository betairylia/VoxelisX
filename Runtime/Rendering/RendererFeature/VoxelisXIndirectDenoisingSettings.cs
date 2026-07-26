using System;
using UnityEngine;

public enum VoxelisXIndirectSpatialFilterMode
{
    Disabled = 0,
    Separable15Tap = 1,
    ATrous = 2
}

[Serializable]
public struct VoxelisXSeparable15TapFilterSettings
{
    public const int MaxRadius = 7;

    [Range(1, MaxRadius)] public int radius;
    [Min(0.0001f)] public float distanceSigma;

    public static VoxelisXSeparable15TapFilterSettings Default => new VoxelisXSeparable15TapFilterSettings
    {
        radius = 7,
        distanceSigma = 18.0f
    };
}

[Serializable]
public struct VoxelisXATrousFilterSettings
{
    /// <summary>
    /// Upper bound on iterations. Also the number of materials the denoise stage allocates, since
    /// each iteration needs its own to carry a distinct step width to submission.
    /// </summary>
    public const int MaxIterations = 6;

    [Range(1, MaxIterations)] public int iterations;
    public bool useFaceHash;
    [Tooltip("Jitter the sparse taps of iterations with step width > 1 by a per-pixel/per-frame hash. Breaks the structured checkerboard / grid-dot patterns the a-trous hole pattern leaves in heavy noise; the stochastic residue is removed by temporal accumulation.")]
    public bool jitterTaps;
    [Min(0.0f)] public float normalPower;
    [Tooltip("Multiplier on the screen-space depth gradient (slope) term of the depth weight. ~1 is a good start; larger accepts more depth variation.")]
    [Min(0.0001f)] public float depthSigma;
    [Tooltip("Depth tolerance floor as a fraction of the center depth. Covers depth quantization on surfaces facing the camera.")]
    [Min(0.0f)] public float relativeDepthSigma;
    [Tooltip("Multiplier on the local luminance std-dev for the radiance edge-stopping weight (SVGF-style). ~4 is a good start; larger blurs more across lighting edges.")]
    [Min(0.0001f)] public float radianceSigma;

    public static VoxelisXATrousFilterSettings Default => new VoxelisXATrousFilterSettings
    {
        iterations = 4,
        useFaceHash = false,
        jitterTaps = true,
        normalPower = 64.0f,
        depthSigma = 1.0f,
        relativeDepthSigma = 0.01f,
        radianceSigma = 4.0f
    };
}

[Serializable]
public struct VoxelisXIndirectDenoisingSettings
{
    public VoxelisXIndirectSpatialFilterMode mode;

    [Header("Separable 15 Tap")]
    public VoxelisXSeparable15TapFilterSettings separable15Tap;

    [Header("A-Trous")]
    public VoxelisXATrousFilterSettings aTrous;

    public static VoxelisXIndirectDenoisingSettings Default => new VoxelisXIndirectDenoisingSettings
    {
        mode = VoxelisXIndirectSpatialFilterMode.Separable15Tap,
        separable15Tap = VoxelisXSeparable15TapFilterSettings.Default,
        aTrous = VoxelisXATrousFilterSettings.Default
    };

    /// <summary>Clamps user-authored values into ranges the shaders can handle.</summary>
    public VoxelisXIndirectDenoisingSettings Validated()
    {
        VoxelisXIndirectDenoisingSettings v = this;

        v.separable15Tap.radius = Mathf.Clamp(
            v.separable15Tap.radius, 1, VoxelisXSeparable15TapFilterSettings.MaxRadius);
        v.separable15Tap.distanceSigma = Mathf.Max(0.0001f, v.separable15Tap.distanceSigma);

        v.aTrous.iterations = Mathf.Clamp(
            v.aTrous.iterations, 1, VoxelisXATrousFilterSettings.MaxIterations);
        v.aTrous.normalPower = Mathf.Max(0.0f, v.aTrous.normalPower);
        v.aTrous.depthSigma = Mathf.Max(0.0001f, v.aTrous.depthSigma);
        v.aTrous.relativeDepthSigma = Mathf.Max(0.0f, v.aTrous.relativeDepthSigma);
        v.aTrous.radianceSigma = Mathf.Max(0.0001f, v.aTrous.radianceSigma);

        return v;
    }
}
