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
    [Range(1, 7)] public int radius;
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
    [Range(1, 6)] public int iterations;
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
}
