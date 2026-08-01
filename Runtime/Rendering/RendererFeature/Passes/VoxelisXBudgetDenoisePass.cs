using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Budget mode, stage 2 of 3: denoises the AO/shadow signal, then shades the G-buffer once.
/// </summary>
/// <remarks>
/// Four sub-steps, recorded in order:
/// <list type="number">
/// <item>spatial filter — the shared a-trous kernel, run over AO/shadow instead of radiance;</item>
/// <item>temporal accumulation — the shared kernel, against the same reprojected history;</item>
/// <item>deferred shade — sun (lambert + GGX, hard shadowed) plus sky ambient scaled by AO, once
/// per pixel, into the final colour target;</item>
/// <item>cross resolve — averages the delta checkerboard back together.</item>
/// </list>
/// Steps 1, 2 and 4 are the *same HLSL* the path-traced chain runs (Shaders/Denoise/*.hlsl); only
/// the signal pointed at them differs. Step 3 is the one genuinely new piece of shading in budget
/// mode. Reads only <see cref="VoxelisXFrameResources"/>; it has no knowledge of the tracer.
/// </remarks>
public class VoxelisXBudgetDenoisePass : ScriptableRenderPass
{
    private const int PassATrousFilter = 0;
    private const int PassTemporalAccumulation = 1;
    private const int PassDeferredShade = 2;
    private const int PassCrossResolve = 3;

    private Material shadeMaterial;
    /// <summary>
    /// One material per a-trous iteration. Blitter resolves material properties when the command
    /// buffer is submitted, not when the blit is recorded, so every iteration needs its own
    /// instance — sharing one would silently give all iterations the last iteration's step width.
    /// </summary>
    private Material[] aTrousMaterials;

    private VoxelisXATrousFilterSettings aTrousSettings = VoxelisXATrousFilterSettings.Default;
    private bool spatialFilterEnabled = true;
    private VoxelisXTemporalRadianceSettings temporalSettings = new VoxelisXTemporalRadianceSettings().Validated();
    private VoxelisXBudgetShadingSettings shadingSettings = VoxelisXBudgetShadingSettings.Default.Validated();
    private bool resolveDeltaCheckerboard = true;

    private static Cubemap s_FallbackSky;
    private static bool s_WarnedMissingSky;

    internal class ATrousFilterPassData
    {
        internal int width;
        internal int height;
        internal int stepWidth;
        internal int frameIndex;
        internal VoxelisXATrousFilterSettings settings;
        internal TextureHandle Source;
        internal Material material;
    }

    internal class TemporalAccumulationPassData
    {
        internal int width;
        internal int height;
        internal bool historyValid;
        internal VoxelisXTemporalRadianceSettings settings;
        internal TextureHandle FilteredAOShadow;
        internal TextureHandle PreviousAOShadowHistory;
        internal TextureHandle PreviousDepthHistory;
        internal TextureHandle PreviousNormalHistory;
        internal Material material;
    }

    internal class DeferredShadePassData
    {
        internal int width;
        internal int height;
        internal VoxelisXBudgetShadingSettings settings;
        internal Vector4 mainLightColor;
        internal Vector3 mainLightDirection;
        internal TextureHandle Albedo;
        internal Material material;
    }

    internal class CrossResolvePassData
    {
        internal int width;
        internal int height;
        internal TextureHandle Source;
        internal Material material;
    }

    /// <summary>Binds the materials owned by the renderer feature. Called when the feature is created.</summary>
    public void Setup(Material shade, Material[] aTrousIterationMaterials)
    {
        shadeMaterial = shade;
        aTrousMaterials = aTrousIterationMaterials;
    }

    /// <summary>Pushes this frame's denoising and shading settings. Called once per camera before enqueueing.</summary>
    public void ConfigureSettings(
        bool spatialEnabled,
        VoxelisXATrousFilterSettings aTrous,
        VoxelisXTemporalRadianceSettings temporal,
        VoxelisXBudgetShadingSettings shading,
        bool resolveCheckerboard)
    {
        spatialFilterEnabled = spatialEnabled;
        aTrousSettings = aTrous;
        temporalSettings = temporal.Validated();
        shadingSettings = shading.Validated();
        resolveDeltaCheckerboard = resolveCheckerboard;
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => shadeMaterial != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        VoxelisXFrameResources resources = frameData.GetOrCreate<VoxelisXFrameResources>();
        if (!resources.IsValid || resources.History == null || !IsReady || !resources.AOShadowRaw.IsValid())
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        VoxelisXCameraHistory history = resources.History;
        int width = cameraData.scaledWidth;
        int height = cameraData.scaledHeight;

        resources.AOShadowFiltered = RecordSpatialFilter(renderGraph, resources, cameraData, width, height);
        resources.AOShadowAccumulated = RecordTemporalAccumulation(renderGraph, resources, history, width, height);

        // The double buffer flips once the write into "current" has been recorded; everything above
        // has already captured the handles it needs, and nothing downstream reads history this frame.
        history.EndFrame();

        resources.Color = RecordDeferredShade(renderGraph, resources, frameData, cameraData, width, height);

        if (resolveDeltaCheckerboard)
        {
            resources.Color = RecordCrossResolve(renderGraph, resources, cameraData, width, height);
        }
    }

    // --- Spatial ------------------------------------------------------------

    private TextureHandle RecordSpatialFilter(
        RenderGraph renderGraph,
        VoxelisXFrameResources resources,
        UniversalCameraData cameraData,
        int width,
        int height)
    {
        if (!spatialFilterEnabled || aTrousMaterials == null || aTrousMaterials.Length == 0)
        {
            return resources.AOShadowRaw;
        }

        RenderTextureDescriptor descriptor =
            VoxelisXFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf);
        TextureHandle scratch = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, descriptor, "VoxelisX_outBudgetAOShadowSpatialTemp", false);
        TextureHandle filtered = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, descriptor, "VoxelisX_outBudgetAOShadowFiltered", false);

        int iterations = Mathf.Min(aTrousSettings.iterations, aTrousMaterials.Length);
        TextureHandle source = resources.AOShadowRaw;
        TextureHandle destination = source;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            destination = (iteration & 1) == 0 ? scratch : filtered;
            string passName = $"VoxelisX Budget AO A-Trous Filter {iteration + 1}";

            using (var builder = renderGraph.AddRasterRenderPass<ATrousFilterPassData>(passName, out var passData))
            {
                passData.width = width;
                passData.height = height;
                passData.stepWidth = 1 << iteration;
                passData.frameIndex = Time.frameCount;
                passData.settings = aTrousSettings;
                passData.Source = source;
                passData.material = aTrousMaterials[iteration];

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resources.Normal, AccessFlags.Read);
                builder.UseTexture(resources.CurrentDepthHistory, AccessFlags.Read);
                builder.UseGlobalTexture(VoxelisXShaderIDs.BudgetAOShadowTex);
                builder.UseGlobalTexture(VoxelisXShaderIDs.NormalTex);
                builder.UseGlobalTexture(VoxelisXShaderIDs.CurrentDepthHistoryTex);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(destination, VoxelisXShaderIDs.BudgetAOShadowTex);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ATrousFilterPassData data, RasterGraphContext ctx) =>
                {
                    SetFrameSize(data.material, data.width, data.height);
                    data.material.SetInt(VoxelisXShaderIDs.ATrousStepWidth, data.stepWidth);
                    data.material.SetInt(VoxelisXShaderIDs.ATrousUseFaceHash, data.settings.useFaceHash ? 1 : 0);
                    data.material.SetInt(VoxelisXShaderIDs.ATrousJitterTaps, data.settings.jitterTaps ? 1 : 0);
                    data.material.SetInt(VoxelisXShaderIDs.ATrousFrameIndex, data.frameIndex);
                    data.material.SetFloat(VoxelisXShaderIDs.ATrousNormalPower, data.settings.normalPower);
                    data.material.SetFloat(VoxelisXShaderIDs.ATrousDepthSigma, data.settings.depthSigma);
                    data.material.SetFloat(VoxelisXShaderIDs.ATrousRelativeDepthSigma, data.settings.relativeDepthSigma);
                    data.material.SetFloat(VoxelisXShaderIDs.ATrousRadianceSigma, data.settings.radianceSigma);

                    Blitter.BlitTexture(ctx.cmd, data.Source, FullScreenScaleBias, data.material, PassATrousFilter);
                });
            }

            source = destination;
        }

        return destination;
    }

    // --- Temporal -----------------------------------------------------------

    private TextureHandle RecordTemporalAccumulation(
        RenderGraph renderGraph,
        VoxelisXFrameResources resources,
        VoxelisXCameraHistory history,
        int width,
        int height)
    {
        // The history double buffer is signal-agnostic ARGBHalf, so budget mode stores AO/shadow
        // in the same targets the path tracer stores radiance in. Only one feature can be enabled
        // at a time, so they never contend for it.
        TextureHandle accumulated = renderGraph.ImportTexture(history.CurrentIndirectRadiance);
        TextureHandle previousAOShadow = renderGraph.ImportTexture(history.PreviousIndirectRadiance);
        TextureHandle previousDepth = renderGraph.ImportTexture(history.PreviousDepth);
        TextureHandle previousNormal = renderGraph.ImportTexture(history.PreviousNormal);

        using (var builder = renderGraph.AddRasterRenderPass<TemporalAccumulationPassData>(
                   "VoxelisX Budget AO Temporal Accumulation", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.historyValid = history.IsValid;
            passData.settings = temporalSettings;
            passData.FilteredAOShadow = resources.AOShadowFiltered;
            passData.PreviousAOShadowHistory = previousAOShadow;
            passData.PreviousDepthHistory = previousDepth;
            passData.PreviousNormalHistory = previousNormal;
            passData.material = shadeMaterial;

            builder.UseTexture(passData.FilteredAOShadow, AccessFlags.Read);
            builder.UseTexture(resources.MotionVector, AccessFlags.Read);
            builder.UseTexture(passData.PreviousAOShadowHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousDepthHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousNormalHistory, AccessFlags.Read);
            builder.UseTexture(resources.CurrentNormalHistory, AccessFlags.Read);
            builder.UseGlobalTexture(VoxelisXShaderIDs.BudgetAOShadowTex);
            builder.UseGlobalTexture(VoxelisXShaderIDs.MotionVectorTex);
            builder.UseGlobalTexture(VoxelisXShaderIDs.CurrentNormalHistoryTex);
            builder.SetRenderAttachment(accumulated, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(accumulated, VoxelisXShaderIDs.BudgetAOShadowAccumulatedTex);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((TemporalAccumulationPassData data, RasterGraphContext ctx) =>
            {
                Material material = data.material;
                SetFrameSize(material, data.width, data.height);
                material.SetInt(VoxelisXShaderIDs.IndirectRadianceHistoryValid, data.historyValid ? 1 : 0);
                material.SetInt(VoxelisXShaderIDs.TemporalRadianceEnabled, data.settings.enabled ? 1 : 0);
                material.SetInt(VoxelisXShaderIDs.TemporalRadianceBilinearHistory, data.settings.bilinearHistory ? 1 : 0);
                material.SetInt(VoxelisXShaderIDs.TemporalRadianceDepthRejectionEnabled, data.settings.depthRejection ? 1 : 0);
                material.SetInt(VoxelisXShaderIDs.TemporalRadianceNormalRejectionEnabled, data.settings.normalRejection ? 1 : 0);
                material.SetFloat(VoxelisXShaderIDs.TemporalRadianceCurrentFrameMinWeight, data.settings.currentFrameMinWeight);
                material.SetFloat(VoxelisXShaderIDs.TemporalRadianceDepthTolerance, data.settings.depthTolerance);
                material.SetFloat(VoxelisXShaderIDs.TemporalRadianceRelativeDepthTolerance, data.settings.relativeDepthTolerance);
                material.SetFloat(VoxelisXShaderIDs.TemporalRadianceNormalThreshold, data.settings.normalThreshold);
                material.SetFloat(VoxelisXShaderIDs.TemporalRadianceMaxFrames, data.settings.maximumAverageFrames);
                material.SetTexture(VoxelisXShaderIDs.PreviousIndirectRadianceHistoryTex, data.PreviousAOShadowHistory);
                material.SetTexture(VoxelisXShaderIDs.PreviousDepthHistoryTex, data.PreviousDepthHistory);
                material.SetTexture(VoxelisXShaderIDs.PreviousNormalHistoryTex, data.PreviousNormalHistory);

                Blitter.BlitTexture(ctx.cmd, data.FilteredAOShadow, FullScreenScaleBias, material, PassTemporalAccumulation);
            });
        }

        return accumulated;
    }

    // --- Deferred shade -----------------------------------------------------

    private TextureHandle RecordDeferredShade(
        RenderGraph renderGraph,
        VoxelisXFrameResources resources,
        ContextContainer frameData,
        UniversalCameraData cameraData,
        int width,
        int height)
    {
        TextureHandle color = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, VoxelisXFrameResources.BaseDescriptor(cameraData), "VoxelisX_outColor", false);

        UniversalLightData lightData = frameData.Get<UniversalLightData>();

        using (var builder = renderGraph.AddRasterRenderPass<DeferredShadePassData>("VoxelisX Budget Deferred Shade", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.settings = shadingSettings;
            passData.mainLightColor = VoxelisXBudgetLighting.ResolveMainLightColor(lightData);
            passData.mainLightDirection = VoxelisXBudgetLighting.ResolveMainLightDirection(lightData);
            passData.Albedo = resources.Albedo;
            passData.material = shadeMaterial;

            builder.UseTexture(resources.DeterministicRadiance, AccessFlags.Read);
            builder.UseTexture(resources.Albedo, AccessFlags.Read);
            builder.UseTexture(resources.Normal, AccessFlags.Read);
            builder.UseTexture(resources.Surface, AccessFlags.Read);
            builder.UseTexture(resources.AOShadowAccumulated, AccessFlags.Read);
            builder.UseGlobalTexture(VoxelisXShaderIDs.DeterministicRadianceTex);
            builder.UseGlobalTexture(VoxelisXShaderIDs.AlbedoTex);
            builder.UseGlobalTexture(VoxelisXShaderIDs.NormalTex);
            builder.UseGlobalTexture(VoxelisXShaderIDs.BudgetSurfaceTex);
            builder.UseGlobalTexture(VoxelisXShaderIDs.BudgetAOShadowAccumulatedTex);
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((DeferredShadePassData data, RasterGraphContext ctx) =>
            {
                Material material = data.material;
                SetFrameSize(material, data.width, data.height);
                material.SetVector(VoxelisXShaderIDs.BudgetMainLightColor, data.mainLightColor);
                material.SetVector(VoxelisXShaderIDs.BudgetMainLightDirection, data.mainLightDirection);
                material.SetFloat(VoxelisXShaderIDs.BudgetSkyIntensity, data.settings.skyIntensity);
                material.SetFloat(VoxelisXShaderIDs.BudgetSkyDiffuseMip, data.settings.skyDiffuseMip);
                material.SetFloat(VoxelisXShaderIDs.BudgetSkySpecularMaxMip, data.settings.skySpecularMaxMip);
                material.SetFloat(VoxelisXShaderIDs.BudgetAOStrength, data.settings.aoStrength);
                material.SetFloat(VoxelisXShaderIDs.BudgetAOAffectsSpecular, data.settings.aoAffectsSpecular);
                // The sky provider publishes its cubemap as a plain global that render graph cannot
                // track, so it is fetched and bound directly, as the trace stage does.
                material.SetTexture(VoxelisXShaderIDs.BudgetSkyTex,
                    VoxelisXBudgetLighting.ResolveSkyTexture(ref s_FallbackSky, ref s_WarnedMissingSky));

                Blitter.BlitTexture(ctx.cmd, data.Albedo, FullScreenScaleBias, material, PassDeferredShade);
            });
        }

        return color;
    }

    // --- Cross resolve ------------------------------------------------------

    private TextureHandle RecordCrossResolve(
        RenderGraph renderGraph,
        VoxelisXFrameResources resources,
        UniversalCameraData cameraData,
        int width,
        int height)
    {
        TextureHandle resolved = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, VoxelisXFrameResources.BaseDescriptor(cameraData), "VoxelisX_outColorResolved", false);

        using (var builder = renderGraph.AddRasterRenderPass<CrossResolvePassData>(
                   "VoxelisX Budget Delta Checkerboard Resolve", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.Source = resources.Color;
            passData.material = shadeMaterial;

            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.UseTexture(resources.MotionVector, AccessFlags.Read);
            builder.UseGlobalTexture(VoxelisXShaderIDs.MotionVectorTex);
            builder.SetRenderAttachment(resolved, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((CrossResolvePassData data, RasterGraphContext ctx) =>
            {
                SetFrameSize(data.material, data.width, data.height);
                Blitter.BlitTexture(ctx.cmd, data.Source, FullScreenScaleBias, data.material, PassCrossResolve);
            });
        }

        return resolved;
    }

    private static readonly Vector4 FullScreenScaleBias = new Vector4(1, 1, 0, 0);

    private static void SetFrameSize(Material material, int width, int height)
    {
        material.SetVector(VoxelisXShaderIDs.FrameSize, new Vector4(
            width,
            height,
            width > 0 ? 1.0f / width : 0.0f,
            height > 0 ? 1.0f / height : 0.0f));
    }
}
