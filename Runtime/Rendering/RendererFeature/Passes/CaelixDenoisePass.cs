using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Stage 2 of 3: turns the noisy indirect radiance from the G-buffer stage into the final image.
/// </summary>
/// <remarks>
/// Four sub-steps, recorded in order:
/// <list type="number">
/// <item>combine — sums the split diffuse/specular stochastic targets into the single working
/// signal this legacy chain filters (a future NRD path will consume the split targets directly
/// and skip the rest of this pass);</item>
/// <item>spatial filter — separable 15-tap, or a multi-iteration a-trous, or nothing;</item>
/// <item>temporal accumulation against last frame's reprojected history;</item>
/// <item>composite — deterministic radiance plus albedo-modulated indirect, into the final colour target.</item>
/// </list>
/// Reads only <see cref="CaelixFrameResources"/>; it has no knowledge of the tracer.
/// </remarks>
public class CaelixDenoisePass : ScriptableRenderPass
{
    private const int PassSpatialFilterX = 0;
    private const int PassSpatialFilterY = 1;
    private const int PassTemporalAccumulation = 2;
    private const int PassComposite = 3;
    private const int PassCombineStochastic = 4;
    private const int PassCrossResolve = 5;
    private const int PassATrousFilter = 0;

    private Material indirectMaterial;
    /// <summary>
    /// One material per a-trous iteration. Blitter resolves material properties when the command
    /// buffer is submitted, not when the blit is recorded, so every iteration needs its own material
    /// instance — sharing one would silently give all iterations the last iteration's step width.
    /// </summary>
    private Material[] aTrousMaterials;

    private CaelixIndirectDenoisingSettings denoisingSettings = CaelixIndirectDenoisingSettings.Default;
    private CaelixTemporalRadianceSettings temporalSettings = new CaelixTemporalRadianceSettings().Validated();
    private bool resolveDeltaCheckerboard = true;

    internal class CombinePassData
    {
        internal int width;
        internal int height;
        internal TextureHandle Source;
        internal Material material;
    }

    internal class SpatialFilterPassData
    {
        internal int width;
        internal int height;
        internal CaelixSeparable15TapFilterSettings settings;
        internal TextureHandle Source;
        internal Material material;
        internal int passIndex;
    }

    internal class ATrousFilterPassData
    {
        internal int width;
        internal int height;
        internal int stepWidth;
        internal int frameIndex;
        internal CaelixATrousFilterSettings settings;
        internal TextureHandle Source;
        internal Material material;
    }

    internal class TemporalAccumulationPassData
    {
        internal int width;
        internal int height;
        internal bool historyValid;
        internal CaelixTemporalRadianceSettings settings;
        internal TextureHandle FilteredIndirectRadiance;
        internal TextureHandle PreviousIndirectRadianceHistory;
        internal TextureHandle PreviousDepthHistory;
        internal TextureHandle PreviousNormalHistory;
        internal Material material;
    }

    internal class CompositePassData
    {
        internal int width;
        internal int height;
        internal TextureHandle DeterministicRadiance;
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
    public void Setup(Material indirectPipelineMaterial, Material[] aTrousIterationMaterials)
    {
        indirectMaterial = indirectPipelineMaterial;
        aTrousMaterials = aTrousIterationMaterials;
    }

    /// <summary>Pushes this frame's denoising settings. Called once per camera before enqueueing.</summary>
    public void ConfigureSettings(
        CaelixIndirectDenoisingSettings denoising,
        CaelixTemporalRadianceSettings temporal,
        bool resolveCheckerboard)
    {
        denoisingSettings = denoising.Validated();
        temporalSettings = temporal.Validated();
        resolveDeltaCheckerboard = resolveCheckerboard;
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => indirectMaterial != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        CaelixFrameResources resources = frameData.GetOrCreate<CaelixFrameResources>();
        if (!resources.IsValid || resources.History == null || !IsReady)
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        CaelixCameraHistory history = resources.History;
        int width = cameraData.scaledWidth;
        int height = cameraData.scaledHeight;

        RenderTextureDescriptor indirectDesc =
            CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf);
        TextureHandle scratch = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, indirectDesc, "Caelix_outIndirectRadianceSpatialTemp", false);
        TextureHandle filtered = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, indirectDesc, "Caelix_outIndirectRadianceFiltered", false);

        resources.RawIndirectRadiance = RecordCombineStochastic(
            renderGraph, resources, indirectDesc, width, height);

        resources.FilteredIndirectRadiance = RecordSpatialFilter(
            renderGraph, resources, width, height, scratch, filtered);

        resources.AccumulatedIndirectRadiance = RecordTemporalAccumulation(
            renderGraph, resources, history, width, height);

        // The double buffer flips once the write into "current" has been recorded; everything above
        // has already captured the handles it needs, and nothing downstream reads history this frame.
        history.EndFrame();

        resources.Color = RecordComposite(renderGraph, resources, cameraData, width, height);

        if (resolveDeltaCheckerboard)
        {
            resources.Color = RecordCrossResolve(renderGraph, resources, cameraData, width, height);
        }
    }

    // --- Combine ------------------------------------------------------------

    /// <summary>
    /// Sums the split diffuse/specular stochastic targets into the single combined signal the
    /// rest of this chain filters. The split targets carry hit distance in alpha, so the chain's
    /// validity alpha is re-derived from the deterministic target's hit/miss flag in the shader.
    /// </summary>
    private TextureHandle RecordCombineStochastic(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        RenderTextureDescriptor descriptor,
        int width,
        int height)
    {
        TextureHandle combined = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, descriptor, "Caelix_outIndirectRadianceRaw", false);

        using (var builder = renderGraph.AddRasterRenderPass<CombinePassData>("Caelix Combine Stochastic", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.Source = resources.StochasticDiffuse;
            passData.material = indirectMaterial;

            builder.UseTexture(resources.StochasticDiffuse, AccessFlags.Read);
            builder.UseTexture(resources.StochasticSpecular, AccessFlags.Read);
            builder.UseTexture(resources.DeterministicRadiance, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.DiffuseRadianceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.SpecularRadianceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.DeterministicRadianceTex);
            builder.SetRenderAttachment(combined, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(combined, CaelixShaderIDs.IndirectRadianceTex);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((CombinePassData data, RasterGraphContext ctx) =>
            {
                SetFrameSize(data.material, data.width, data.height);
                Blitter.BlitTexture(ctx.cmd, data.Source, FullScreenScaleBias, data.material, PassCombineStochastic);
            });
        }

        return combined;
    }

    // --- Spatial ------------------------------------------------------------

    private TextureHandle RecordSpatialFilter(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        int width,
        int height,
        TextureHandle scratch,
        TextureHandle filtered)
    {
        switch (denoisingSettings.mode)
        {
            case CaelixIndirectSpatialFilterMode.ATrous:
                return RecordATrousFilter(renderGraph, resources, width, height, scratch, filtered);
            case CaelixIndirectSpatialFilterMode.Separable15Tap:
                return RecordSeparable15TapFilter(renderGraph, resources, width, height, scratch, filtered);
            case CaelixIndirectSpatialFilterMode.Disabled:
            default:
                return resources.RawIndirectRadiance;
        }
    }

    private TextureHandle RecordSeparable15TapFilter(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        int width,
        int height,
        TextureHandle scratch,
        TextureHandle filtered)
    {
        RecordSeparablePass(renderGraph, "Caelix Indirect Spatial Filter X",
            resources.RawIndirectRadiance, scratch, resources.Normal, width, height, PassSpatialFilterX);
        RecordSeparablePass(renderGraph, "Caelix Indirect Spatial Filter Y",
            scratch, filtered, resources.Normal, width, height, PassSpatialFilterY);

        return filtered;
    }

    private void RecordSeparablePass(
        RenderGraph renderGraph,
        string passName,
        TextureHandle source,
        TextureHandle destination,
        TextureHandle normal,
        int width,
        int height,
        int passIndex)
    {
        using (var builder = renderGraph.AddRasterRenderPass<SpatialFilterPassData>(passName, out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.settings = denoisingSettings.separable15Tap;
            passData.Source = source;
            passData.material = indirectMaterial;
            passData.passIndex = passIndex;

            builder.UseTexture(source, AccessFlags.Read);
            builder.UseTexture(normal, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.IndirectRadianceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.NormalTex);
            builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(destination, CaelixShaderIDs.IndirectRadianceTex);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((SpatialFilterPassData data, RasterGraphContext ctx) =>
            {
                SetFrameSize(data.material, data.width, data.height);
                data.material.SetInt(CaelixShaderIDs.SpatialFilterEnabled, 1);
                data.material.SetInt(CaelixShaderIDs.SeparableFilterRadius, data.settings.radius);
                data.material.SetFloat(CaelixShaderIDs.SeparableFilterDistanceSigma, data.settings.distanceSigma);

                Blitter.BlitTexture(ctx.cmd, data.Source, FullScreenScaleBias, data.material, data.passIndex);
            });
        }
    }

    private TextureHandle RecordATrousFilter(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        int width,
        int height,
        TextureHandle scratch,
        TextureHandle filtered)
    {
        if (aTrousMaterials == null || aTrousMaterials.Length == 0)
        {
            return resources.RawIndirectRadiance;
        }

        int iterations = Mathf.Min(denoisingSettings.aTrous.iterations, aTrousMaterials.Length);
        TextureHandle source = resources.RawIndirectRadiance;
        TextureHandle destination = source;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            destination = (iteration & 1) == 0 ? scratch : filtered;
            string passName = $"Caelix Indirect A-Trous Filter {iteration + 1}";

            using (var builder = renderGraph.AddRasterRenderPass<ATrousFilterPassData>(passName, out var passData))
            {
                passData.width = width;
                passData.height = height;
                passData.stepWidth = 1 << iteration;
                passData.frameIndex = Time.frameCount;
                passData.settings = denoisingSettings.aTrous;
                passData.Source = source;
                passData.material = aTrousMaterials[iteration];

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resources.Normal, AccessFlags.Read);
                builder.UseTexture(resources.CurrentDepthHistory, AccessFlags.Read);
                builder.UseGlobalTexture(CaelixShaderIDs.IndirectRadianceTex);
                builder.UseGlobalTexture(CaelixShaderIDs.NormalTex);
                builder.UseGlobalTexture(CaelixShaderIDs.CurrentDepthHistoryTex);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(destination, CaelixShaderIDs.IndirectRadianceTex);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ATrousFilterPassData data, RasterGraphContext ctx) =>
                {
                    SetFrameSize(data.material, data.width, data.height);
                    data.material.SetInt(CaelixShaderIDs.ATrousStepWidth, data.stepWidth);
                    data.material.SetInt(CaelixShaderIDs.ATrousUseFaceHash, data.settings.useFaceHash ? 1 : 0);
                    data.material.SetInt(CaelixShaderIDs.ATrousJitterTaps, data.settings.jitterTaps ? 1 : 0);
                    data.material.SetInt(CaelixShaderIDs.ATrousFrameIndex, data.frameIndex);
                    data.material.SetFloat(CaelixShaderIDs.ATrousNormalPower, data.settings.normalPower);
                    data.material.SetFloat(CaelixShaderIDs.ATrousDepthSigma, data.settings.depthSigma);
                    data.material.SetFloat(CaelixShaderIDs.ATrousRelativeDepthSigma, data.settings.relativeDepthSigma);
                    data.material.SetFloat(CaelixShaderIDs.ATrousRadianceSigma, data.settings.radianceSigma);

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
        CaelixFrameResources resources,
        CaelixCameraHistory history,
        int width,
        int height)
    {
        TextureHandle accumulated = renderGraph.ImportTexture(history.CurrentIndirectRadiance);
        TextureHandle previousIndirect = renderGraph.ImportTexture(history.PreviousIndirectRadiance);
        TextureHandle previousDepth = renderGraph.ImportTexture(history.PreviousDepth);
        TextureHandle previousNormal = renderGraph.ImportTexture(history.PreviousNormal);

        using (var builder = renderGraph.AddRasterRenderPass<TemporalAccumulationPassData>(
                   "Caelix Indirect Temporal Accumulation", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.historyValid = history.IsValid;
            passData.settings = temporalSettings;
            passData.FilteredIndirectRadiance = resources.FilteredIndirectRadiance;
            passData.PreviousIndirectRadianceHistory = previousIndirect;
            passData.PreviousDepthHistory = previousDepth;
            passData.PreviousNormalHistory = previousNormal;
            passData.material = indirectMaterial;

            builder.UseTexture(passData.FilteredIndirectRadiance, AccessFlags.Read);
            builder.UseTexture(resources.MotionVector, AccessFlags.Read);
            builder.UseTexture(passData.PreviousIndirectRadianceHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousDepthHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousNormalHistory, AccessFlags.Read);
            builder.UseTexture(resources.CurrentDepthHistory, AccessFlags.Read);
            builder.UseTexture(resources.CurrentNormalHistory, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.IndirectRadianceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.MotionVectorTex);
            builder.UseGlobalTexture(CaelixShaderIDs.CurrentDepthHistoryTex);
            builder.UseGlobalTexture(CaelixShaderIDs.CurrentNormalHistoryTex);
            builder.SetRenderAttachment(accumulated, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(accumulated, CaelixShaderIDs.AccumulatedIndirectRadianceTex);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((TemporalAccumulationPassData data, RasterGraphContext ctx) =>
            {
                Material material = data.material;
                SetFrameSize(material, data.width, data.height);
                material.SetInt(CaelixShaderIDs.IndirectRadianceHistoryValid, data.historyValid ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceEnabled, data.settings.enabled ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceBilinearHistory, data.settings.bilinearHistory ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceDepthRejectionEnabled, data.settings.depthRejection ? 1 : 0);
                material.SetInt(CaelixShaderIDs.TemporalRadianceNormalRejectionEnabled, data.settings.normalRejection ? 1 : 0);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceCurrentFrameMinWeight, data.settings.currentFrameMinWeight);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceDepthTolerance, data.settings.depthTolerance);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceRelativeDepthTolerance, data.settings.relativeDepthTolerance);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceNormalThreshold, data.settings.normalThreshold);
                material.SetFloat(CaelixShaderIDs.TemporalRadianceMaxFrames, data.settings.maximumAverageFrames);
                material.SetTexture(CaelixShaderIDs.PreviousIndirectRadianceHistoryTex, data.PreviousIndirectRadianceHistory);
                material.SetTexture(CaelixShaderIDs.PreviousDepthHistoryTex, data.PreviousDepthHistory);
                material.SetTexture(CaelixShaderIDs.PreviousNormalHistoryTex, data.PreviousNormalHistory);

                Blitter.BlitTexture(ctx.cmd, data.FilteredIndirectRadiance, FullScreenScaleBias, material, PassTemporalAccumulation);
            });
        }

        return accumulated;
    }

    // --- Composite ----------------------------------------------------------

    private TextureHandle RecordComposite(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        UniversalCameraData cameraData,
        int width,
        int height)
    {
        TextureHandle color = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outColor", false);

        using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("Caelix Composite", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.DeterministicRadiance = resources.DeterministicRadiance;
            passData.material = indirectMaterial;

            builder.UseTexture(resources.DeterministicRadiance, AccessFlags.Read);
            builder.UseTexture(resources.AccumulatedIndirectRadiance, AccessFlags.Read);
            builder.UseTexture(resources.Albedo, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.DeterministicRadianceTex);
            builder.UseGlobalTexture(CaelixShaderIDs.AlbedoTex);
            builder.UseGlobalTexture(CaelixShaderIDs.AccumulatedIndirectRadianceTex);
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((CompositePassData data, RasterGraphContext ctx) =>
            {
                SetFrameSize(data.material, data.width, data.height);
                Blitter.BlitTexture(ctx.cmd, data.DeterministicRadiance, FullScreenScaleBias, data.material, PassComposite);
            });
        }

        return color;
    }

    // --- Cross resolve ------------------------------------------------------

    /// <summary>
    /// Averages the reflect/refract checkerboard the tracer writes at the first transparent
    /// interface back into a single image.
    /// </summary>
    /// <remarks>
    /// Runs on the composited colour rather than on any G-buffer: the checkerboard reaches all of
    /// them (each parity describes a different surface), and only the final colour has collapsed
    /// the two branches into one value. Keyed off <c>MotionVector.a</c>, which the tracer sets for
    /// pixels that actually split, so unsplit geometry is passed through rather than softened.
    /// </remarks>
    private TextureHandle RecordCrossResolve(
        RenderGraph renderGraph,
        CaelixFrameResources resources,
        UniversalCameraData cameraData,
        int width,
        int height)
    {
        TextureHandle resolved = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outColorResolved", false);

        using (var builder = renderGraph.AddRasterRenderPass<CrossResolvePassData>(
                   "Caelix Delta Checkerboard Resolve", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.Source = resources.Color;
            passData.material = indirectMaterial;

            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.UseTexture(resources.MotionVector, AccessFlags.Read);
            builder.UseGlobalTexture(CaelixShaderIDs.MotionVectorTex);
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
        material.SetVector(CaelixShaderIDs.FrameSize, new Vector4(
            width,
            height,
            width > 0 ? 1.0f / width : 0.0f,
            height > 0 ? 1.0f / height : 0.0f));
    }
}
