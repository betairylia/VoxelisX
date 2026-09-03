using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Budget mode, stage 1 of 3: one DXR dispatch producing the G-buffer and the raw AO/shadow signal.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="CaelixGBufferPass"/> for the fixed-cost path. It traces a primary
/// ray, at most one delta step, one hard sun shadow ray and N AO rays, and shades nothing — the
/// lighting is evaluated later, once per pixel, by <see cref="CaelixBudgetDenoisePass"/>.
/// <para>
/// It publishes into the same <see cref="CaelixFrameResources"/> contract as the path-traced
/// stage for everything they share (albedo, normal, depth, motion, history), which is what lets
/// <see cref="CaelixPresentPass"/> be reused verbatim. The two stochastic radiance targets have no
/// budget-mode equivalent and stay null; <see cref="CaelixFrameResources.Surface"/> and
/// <see cref="CaelixFrameResources.AOShadowRaw"/> are the additions.
/// </para>
/// </remarks>
public class CaelixBudgetGBufferPass : ScriptableRenderPass
{
    private CaelixRenderer caelixX;
    private RayTracingShader rayTracingShader;
    private Texture2D blueNoiseTexture;
    private CaelixBudgetTraceSettings settings = CaelixBudgetTraceSettings.Default.Validated();

    /// <summary>Stand-in sky used when the sky provider has not published its cubemap yet.</summary>
    private static Cubemap s_FallbackSky;
    private static bool s_WarnedMissingSky;

    internal class PassData
    {
        internal uint width;
        internal uint height;
        internal float zoom;
        internal float aspectRatio;
        /// <summary>This frame's global sub-pixel ray offset, in pixels, in launch space.</summary>
        internal Vector2 jitter;
        internal int frameIndex;

        internal Matrix4x4 cameraToWorld;
        internal Matrix4x4 previousWorldToCamera;
        internal Vector3 cameraWorldPosition;

        internal CaelixBudgetTraceSettings settings;
        internal Vector3 mainLightDirection;

        internal TextureHandle DeterministicRadiance;
        internal TextureHandle Albedo;
        internal TextureHandle Normal;
        internal TextureHandle Surface;
        internal TextureHandle AOShadow;
        internal TextureHandle Depth;
        internal TextureHandle MotionVector;
        internal TextureHandle CurrentDepthHistory;
        internal TextureHandle CurrentNormalHistory;

        internal RayTracingShader voxShaderRT;
        internal RayTracingAccelerationStructure voxAS;
        internal Texture2D blueNoiseTexture;
        internal Material brickMaterial;
    }

    /// <summary>
    /// Binds the scene renderer, tracing resources and this frame's ray budget.
    /// Called once per camera before enqueueing.
    /// </summary>
    public void ConfigureSettings(
        CaelixRenderer vox, RayTracingShader rtShader, Texture2D blueNoise, CaelixBudgetTraceSettings traceSettings)
    {
        caelixX = vox;
        rayTracingShader = rtShader;
        blueNoiseTexture = blueNoise;
        settings = traceSettings.Validated();
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => caelixX != null && rayTracingShader != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        CaelixFrameResources resources = frameData.GetOrCreate<CaelixFrameResources>();
        if (!IsReady)
        {
            return;
        }

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        cameraData.camera.forceIntoRenderTexture = true;

        Matrix4x4 worldToCamera = cameraData.GetViewMatrix();
        CaelixCameraHistory history = CaelixCameraHistory.BeginFrame(
            cameraData.camera,
            worldToCamera,
            cameraData.camera.fieldOfView,
            cameraData.scaledWidth,
            cameraData.scaledHeight,
            settings.maximumAverageFrames);

        resources.History = history;
        resources.DeterministicRadiance = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outDeterministicRadiance", false);
        // ARGBHalf rather than the path tracer's 8-bit albedo: this one carries the delta chain's
        // throughput, and the checkerboard's x2 Fresnel weight puts it above 1.
        resources.Albedo = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outAlbedo", false);
        resources.Normal = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outNormal", false);
        resources.Surface = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outBudgetSurface", false);
        resources.AOShadowRaw = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outBudgetAOShadowRaw", false);
        resources.Depth = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.RFloat),
            "Caelix_outDepth", false);
        // ARGBFloat, NRD 2.5D: .xy = previousUV - currentUV, .z = viewZprev - viewZ,
        // .a = delta-checkerboard flag.
        resources.MotionVector = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outMotionVector", false);

        // The history halves are persistent RTHandles, so they are imported rather than created.
        // Only the "current" halves are imported here; the denoise stage imports the "previous"
        // ones, so no RTHandle enters the graph twice.
        resources.CurrentDepthHistory = renderGraph.ImportTexture(history.CurrentDepth);
        resources.CurrentNormalHistory = renderGraph.ImportTexture(history.CurrentNormal);

        using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix Budget DXR Trace", out var passData))
        {
            passData.width = (uint)cameraData.scaledWidth;
            passData.height = (uint)cameraData.scaledHeight;
            // The camera's own vertical FOV, and the jitter the filters will reconstruct rays with:
            // both come from the history so the tracer and every consumer share one projection.
            passData.zoom = history.Zoom;
            passData.aspectRatio = history.Aspect;
            passData.jitter = history.Jitter;
            passData.frameIndex = Time.frameCount;

            passData.cameraToWorld = worldToCamera.inverse;
            passData.previousWorldToCamera = history.PreviousViewMatrix;
            passData.cameraWorldPosition = passData.cameraToWorld.MultiplyPoint3x4(Vector3.zero);

            passData.settings = settings;
            passData.mainLightDirection = CaelixBudgetLighting.ResolveMainLightDirection(
                frameData.Get<UniversalLightData>());

            passData.DeterministicRadiance = resources.DeterministicRadiance;
            passData.Albedo = resources.Albedo;
            passData.Normal = resources.Normal;
            passData.Surface = resources.Surface;
            passData.AOShadow = resources.AOShadowRaw;
            passData.Depth = resources.Depth;
            passData.MotionVector = resources.MotionVector;
            passData.CurrentDepthHistory = resources.CurrentDepthHistory;
            passData.CurrentNormalHistory = resources.CurrentNormalHistory;

            passData.voxShaderRT = rayTracingShader;
            passData.voxAS = caelixX.voxelScene;
            passData.blueNoiseTexture = blueNoiseTexture;
            passData.brickMaterial = caelixX.brickMat;

            builder.UseTexture(passData.DeterministicRadiance, AccessFlags.Write);
            builder.UseTexture(passData.Albedo, AccessFlags.Write);
            builder.UseTexture(passData.Normal, AccessFlags.Write);
            builder.UseTexture(passData.Surface, AccessFlags.Write);
            builder.UseTexture(passData.AOShadow, AccessFlags.Write);
            builder.UseTexture(passData.Depth, AccessFlags.Write);
            builder.UseTexture(passData.MotionVector, AccessFlags.Write);
            builder.UseTexture(passData.CurrentDepthHistory, AccessFlags.Write);
            builder.UseTexture(passData.CurrentNormalHistory, AccessFlags.Write);

            builder.SetGlobalTextureAfterPass(passData.DeterministicRadiance, CaelixShaderIDs.DeterministicRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.Albedo, CaelixShaderIDs.AlbedoTex);
            builder.SetGlobalTextureAfterPass(passData.Normal, CaelixShaderIDs.NormalTex);
            builder.SetGlobalTextureAfterPass(passData.Surface, CaelixShaderIDs.BudgetSurfaceTex);
            builder.SetGlobalTextureAfterPass(passData.AOShadow, CaelixShaderIDs.BudgetAOShadowTex);
            builder.SetGlobalTextureAfterPass(passData.Depth, CaelixShaderIDs.DepthTex);
            builder.SetGlobalTextureAfterPass(passData.MotionVector, CaelixShaderIDs.MotionVectorTex);
            builder.SetGlobalTextureAfterPass(passData.CurrentDepthHistory, CaelixShaderIDs.CurrentDepthHistoryTex);
            builder.SetGlobalTextureAfterPass(passData.CurrentNormalHistory, CaelixShaderIDs.CurrentNormalHistoryTex);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => Execute(data, ctx));
        }

        resources.IsValid = true;
    }

    private static void Execute(PassData data, UnsafeGraphContext context)
    {
        CommandBuffer natcmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        natcmd.SetRayTracingShaderPass(data.voxShaderRT, "Caelix");

        if (data.brickMaterial != null)
        {
            if (data.blueNoiseTexture != null)
            {
                data.brickMaterial.SetTexture(CaelixShaderIDs.BlueNoiseTexture, data.blueNoiseTexture);
            }

            data.brickMaterial.SetInt("g_FrameIndex", data.frameIndex);
        }

        if (data.settings.buildAccelerationStructure)
        {
            context.cmd.BuildRayTracingAccelerationStructure(data.voxAS);
        }

        context.cmd.SetRayTracingAccelerationStructure(data.voxShaderRT, "g_AccelStruct", data.voxAS);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DeterministicRadianceTarget", data.DeterministicRadiance);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "AlbedoTarget", data.Albedo);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "NormalTarget", data.Normal);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "SurfaceTarget", data.Surface);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "AOShadowTarget", data.AOShadow);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DepthTarget", data.Depth);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "MotionVectorTarget", data.MotionVector);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "g_CurrentDepthHistory", data.CurrentDepthHistory);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "g_CurrentNormalHistory", data.CurrentNormalHistory);

        // The sky provider publishes its cubemap as a plain global, which render graph cannot track
        // (builder.UseGlobalTexture does not see it), so it is fetched directly here.
        natcmd.SetRayTracingTextureParam(data.voxShaderRT, "g_Sky", CaelixBudgetLighting.ResolveSkyTexture(
            ref s_FallbackSky, ref s_WarnedMissingSky));

        if (data.blueNoiseTexture != null)
        {
            natcmd.SetRayTracingTextureParam(data.voxShaderRT, CaelixShaderIDs.BlueNoiseTexture, data.blueNoiseTexture);
        }

        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_FrameIndex", data.frameIndex);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BudgetEnableDelta", data.settings.enableDelta ? 1 : 0);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BudgetTransparentSkipLimit", data.settings.transparentSkipLimit);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_BudgetDeltaSmoothnessThreshold", data.settings.deltaSmoothnessThreshold);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BudgetEnableSunShadow", data.settings.enableSunShadow ? 1 : 0);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BudgetAOSampleCount", data.settings.aoSampleCount);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_BudgetAOMaxDistance", data.settings.aoMaxDistance);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_Zoom", data.zoom);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_AspectRatio", data.aspectRatio);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_Jitter", data.jitter);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_CameraWorldPosition", data.cameraWorldPosition);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentCameraToWorld", data.cameraToWorld);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_PrevWorldToCamera", data.previousWorldToCamera);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_MainLightDirection", data.mainLightDirection);

        context.cmd.DispatchRays(data.voxShaderRT, "MainRayGenShader", data.width, data.height, 1, null);
    }
}

/// <summary>
/// The bits of light and sky resolution the budget stages share.
/// </summary>
/// <remarks>
/// Both the trace stage (which needs the direction for its shadow ray) and the shade stage (which
/// needs the colour and the cubemap) resolve the same main light, so the lookups live here rather
/// than being duplicated or reached for across passes.
/// </remarks>
internal static class CaelixBudgetLighting
{
    /// <summary>
    /// The direction the main directional light travels, i.e. its forward vector. The direction
    /// *towards* the light is its negation, which is what the shading maths wants.
    /// </summary>
    public static Vector3 ResolveMainLightDirection(UniversalLightData lightData)
    {
        Light light = ResolveMainLight(lightData);
        return light != null ? light.transform.forward : Vector3.down;
    }

    /// <summary>Linear colour times intensity, matching what the path-traced stage passes.</summary>
    public static Vector4 ResolveMainLightColor(UniversalLightData lightData)
    {
        Light light = ResolveMainLight(lightData);
        if (light == null)
        {
            return Vector4.zero;
        }

        Color temperature = light.useColorTemperature
            ? Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature)
            : Color.white;

        return light.color.linear * light.intensity * temperature;
    }

    private static Light ResolveMainLight(UniversalLightData lightData)
    {
        int index = lightData.mainLightIndex;
        if (index < 0 || index >= lightData.visibleLights.Length)
        {
            return null;
        }

        return lightData.visibleLights[index].light;
    }

    /// <summary>
    /// The sky cubemap the sky provider published, or a white 1x1 fallback. Warns once: this is
    /// normal for a frame or two after a sky change, and log spam here skews frame timings.
    /// </summary>
    public static Texture ResolveSkyTexture(ref Cubemap fallback, ref bool warned)
    {
        Texture sky = Shader.GetGlobalTexture(CaelixShaderIDs.GlossyEnvironmentCubeMap);
        if (sky != null)
        {
            return sky;
        }

        if (!warned)
        {
            Debug.LogWarning("Caelix: cannot obtain the current sky cubemap. Using a white fallback sky.");
            warned = true;
        }

        if (fallback == null)
        {
            fallback = new Cubemap(1, GraphicsFormat.B8G8R8A8_SRGB, TextureCreationFlags.None)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            for (int face = 0; face < 6; face++)
            {
                fallback.SetPixel((CubemapFace)face, 0, 0, Color.white);
            }

            fallback.Apply();
        }

        return fallback;
    }
}
