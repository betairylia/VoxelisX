using UnityEngine;

/// <summary>
/// Shader property IDs shared by the Caelix render stages.
/// </summary>
/// <remarks>
/// Centralised so the C# names and the HLSL uniform names in
/// <c>CaelixIndirectRadiancePipeline.shader</c>, <c>CaelixIndirectATrousFilter.shader</c> and
/// <c>PostFlip.shader</c> can be kept in sync from one place.
/// </remarks>
public static class CaelixShaderIDs
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

    public static readonly int FrameSize = Shader.PropertyToID("_CaelixFrameSize");

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

    // --- Budget mode ---
    /// <summary>Octahedral view direction in .xy, metallic in .z, surface-valid flag in .w.</summary>
    public static readonly int BudgetSurfaceTex = Shader.PropertyToID("_BudgetSurfaceTex");
    /// <summary>
    /// The AO/shadow signal (.r = AO, .g = sun visibility, .a = validity). One name for the whole
    /// filter chain, rebound after every pass, so the shared kernels always read "current state".
    /// </summary>
    public static readonly int BudgetAOShadowTex = Shader.PropertyToID("_BudgetAOShadowTex");
    public static readonly int BudgetAOShadowAccumulatedTex = Shader.PropertyToID("_BudgetAOShadowAccumulatedTex");
    public static readonly int BudgetSkyTex = Shader.PropertyToID("_BudgetSkyTex");
    public static readonly int BudgetMainLightColor = Shader.PropertyToID("_BudgetMainLightColor");
    public static readonly int BudgetMainLightDirection = Shader.PropertyToID("_BudgetMainLightDirection");
    public static readonly int BudgetSkyIntensity = Shader.PropertyToID("_BudgetSkyIntensity");
    public static readonly int BudgetSkyDiffuseMip = Shader.PropertyToID("_BudgetSkyDiffuseMip");
    public static readonly int BudgetSkySpecularMaxMip = Shader.PropertyToID("_BudgetSkySpecularMaxMip");
    public static readonly int BudgetAOStrength = Shader.PropertyToID("_BudgetAOStrength");
    public static readonly int BudgetAOAffectsSpecular = Shader.PropertyToID("_BudgetAOAffectsSpecular");

    // --- Present ---
    public static readonly int DebugView = Shader.PropertyToID("_DebugView");

    // --- Ray tracing shader / brick material ---
    public const string BlueNoiseTexture = "stbnTexture";
    public const string GlossyEnvironmentCubeMap = "_GlossyEnvironmentCubeMap";
}
