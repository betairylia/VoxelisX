using UnityEngine;

/// <summary>
/// Shader property IDs shared by the VoxelisX render stages.
/// </summary>
/// <remarks>
/// Centralised so the C# names and the HLSL uniform names in
/// <c>VoxelisXIndirectRadiancePipeline.shader</c>, <c>VoxelisXIndirectATrousFilter.shader</c> and
/// <c>PostFlip.shader</c> can be kept in sync from one place.
/// </remarks>
public static class VoxelisXShaderIDs
{
    // --- G-buffer, published as globals after the trace ---
    public static readonly int DeterministicRadianceTex = Shader.PropertyToID("_DeterministicRadianceTex");
    public static readonly int DiffuseRadianceTex = Shader.PropertyToID("_DiffuseRadianceTex");
    public static readonly int SpecularRadianceTex = Shader.PropertyToID("_SpecularRadianceTex");
    /// <summary>Combined diffuse+specular stochastic radiance — the legacy denoise chain's working signal.</summary>
    public static readonly int IndirectRadianceTex = Shader.PropertyToID("_IndirectRadianceTex");
    public static readonly int AlbedoTex = Shader.PropertyToID("_AlbedoTex");
    public static readonly int NormalTex = Shader.PropertyToID("_NormalTex");
    public static readonly int DepthTex = Shader.PropertyToID("_DepthTex");
    public static readonly int MotionVectorTex = Shader.PropertyToID("_MotionVectorTex");
    public static readonly int CurrentDepthHistoryTex = Shader.PropertyToID("_CurrentDepthHistoryTex");
    public static readonly int CurrentNormalHistoryTex = Shader.PropertyToID("_CurrentNormalHistoryTex");

    // --- Denoise ---
    public static readonly int AccumulatedIndirectRadianceTex = Shader.PropertyToID("_AccumulatedIndirectRadianceTex");
    public static readonly int PreviousIndirectRadianceHistoryTex = Shader.PropertyToID("_PreviousIndirectRadianceHistoryTex");
    public static readonly int PreviousDepthHistoryTex = Shader.PropertyToID("_PreviousDepthHistoryTex");
    public static readonly int PreviousNormalHistoryTex = Shader.PropertyToID("_PreviousNormalHistoryTex");

    public static readonly int FrameSize = Shader.PropertyToID("_VoxelisXFrameSize");

    public static readonly int SpatialFilterEnabled = Shader.PropertyToID("_SpatialFilterEnabled");
    public static readonly int SeparableFilterRadius = Shader.PropertyToID("_SeparableFilterRadius");
    public static readonly int SeparableFilterDistanceSigma = Shader.PropertyToID("_SeparableFilterDistanceSigma");

    public static readonly int ATrousStepWidth = Shader.PropertyToID("_ATrousStepWidth");
    public static readonly int ATrousUseFaceHash = Shader.PropertyToID("_ATrousUseFaceHash");
    public static readonly int ATrousJitterTaps = Shader.PropertyToID("_ATrousJitterTaps");
    public static readonly int ATrousFrameIndex = Shader.PropertyToID("_ATrousFrameIndex");
    public static readonly int ATrousNormalPower = Shader.PropertyToID("_ATrousNormalPower");
    public static readonly int ATrousDepthSigma = Shader.PropertyToID("_ATrousDepthSigma");
    public static readonly int ATrousRelativeDepthSigma = Shader.PropertyToID("_ATrousRelativeDepthSigma");
    public static readonly int ATrousRadianceSigma = Shader.PropertyToID("_ATrousRadianceSigma");

    public static readonly int IndirectRadianceHistoryValid = Shader.PropertyToID("_IndirectRadianceHistoryValid");
    public static readonly int TemporalRadianceEnabled = Shader.PropertyToID("_TemporalRadianceEnabled");
    public static readonly int TemporalRadianceBilinearHistory = Shader.PropertyToID("_TemporalRadianceBilinearHistory");
    public static readonly int TemporalRadianceDepthRejectionEnabled = Shader.PropertyToID("_TemporalRadianceDepthRejectionEnabled");
    public static readonly int TemporalRadianceNormalRejectionEnabled = Shader.PropertyToID("_TemporalRadianceNormalRejectionEnabled");
    public static readonly int TemporalRadianceCurrentFrameMinWeight = Shader.PropertyToID("_TemporalRadianceCurrentFrameMinWeight");
    public static readonly int TemporalRadianceDepthTolerance = Shader.PropertyToID("_TemporalRadianceDepthTolerance");
    public static readonly int TemporalRadianceRelativeDepthTolerance = Shader.PropertyToID("_TemporalRadianceRelativeDepthTolerance");
    public static readonly int TemporalRadianceNormalThreshold = Shader.PropertyToID("_TemporalRadianceNormalThreshold");
    public static readonly int TemporalRadianceMaxFrames = Shader.PropertyToID("_TemporalRadianceMaxFrames");

    // --- Present ---
    public static readonly int DebugView = Shader.PropertyToID("_DebugView");

    // --- Ray tracing shader / brick material ---
    public const string BlueNoiseTexture = "stbnTexture";
    public const string GlossyEnvironmentCubeMap = "_GlossyEnvironmentCubeMap";
}
