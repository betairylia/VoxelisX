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
    [Min(0.0f)] public float normalPower;
    [Min(0.0001f)] public float depthSigma;
    [Min(0.0f)] public float relativeDepthSigma;
    [Min(0.0001f)] public float radianceSigma;

    public static VoxelisXATrousFilterSettings Default => new VoxelisXATrousFilterSettings
    {
        iterations = 4,
        useFaceHash = true,
        normalPower = 64.0f,
        depthSigma = 0.25f,
        relativeDepthSigma = 0.02f,
        radianceSigma = 2.0f
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
