using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Stage 1 of 3: traces the voxel scene with DXR and produces the Caelix G-buffer.
/// </summary>
/// <remarks>
/// Writes deterministic (non-denoised) radiance, per-lobe stochastic radiance with hit distance
/// (diffuse and specular, NRD-style), albedo, normal+roughness, depth and 2.5D motion vectors,
/// plus this frame's depth/normal history for next frame's temporal rejection. Everything it
/// produces is published to <see cref="CaelixFrameResources"/> and, for the shaders that sample
/// them by name, as global textures. Downstream stages read that contract and never touch the tracer.
/// </remarks>
public class CaelixGBufferPass : ScriptableRenderPass
{
    private CaelixRenderer caelixX;
    private RayTracingShader rayTracingShader;
    private Texture2D blueNoiseTexture;
    private CaelixTraceSettings settings = new CaelixTraceSettings().Validated();

    /// <summary>Stand-in sky used when the sky provider has not published its cubemap yet.</summary>
    private static Cubemap s_FallbackSky;
    private static bool s_WarnedMissingSky;

    internal class PassData
    {
        internal uint width;
        internal uint height;
        internal float fov;
        internal int frameIndex;

        internal Matrix4x4 worldToCamera;
        internal Matrix4x4 cameraToWorld;
        internal Matrix4x4 previousWorldToCamera;
        internal Vector3 cameraWorldPosition;
        internal int convergedFrames;

        internal CaelixTraceSettings settings;
        internal Vector4 mainLightColor;

        internal TextureHandle DeterministicRadiance;
        internal TextureHandle DiffuseRadiance;
        internal TextureHandle SpecularRadiance;
        internal TextureHandle Albedo;
        internal TextureHandle Normal;
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
    /// Binds the scene renderer, tracing resources and this frame's settings.
    /// Called once per camera before enqueueing.
    /// </summary>
    public void ConfigureSettings(
        CaelixRenderer vox, RayTracingShader rtShader, Texture2D blueNoise, CaelixTraceSettings traceSettings)
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
            cameraData.scaledWidth,
            cameraData.scaledHeight,
            settings.maximumAverageFrames);

        resources.History = history;
        resources.DeterministicRadiance = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outDeterministicRadiance", false);
        resources.StochasticDiffuse = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outStochasticDiffuse", false);
        resources.StochasticSpecular = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outStochasticSpecular", false);
        resources.Albedo = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.Default),
            "Caelix_outAlbedo", false);
        resources.Normal = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.ARGBHalf),
            "Caelix_outNormal", false);
        resources.Depth = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.DescriptorWithFormat(cameraData, RenderTextureFormat.RFloat),
            "Caelix_outDepth", false);
        // ARGBFloat, NRD 2.5D: .xy = previousUV - currentUV, .z = viewZprev - viewZ, .a reserved.
        resources.MotionVector = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, CaelixFrameResources.BaseDescriptor(cameraData), "Caelix_outMotionVector", false);

        // The history halves are persistent RTHandles, so they are imported rather than created.
        // Only the "current" halves are imported here; the denoise stage imports the "previous" ones,
        // so no RTHandle enters the graph twice.
        resources.CurrentDepthHistory = renderGraph.ImportTexture(history.CurrentDepth);
        resources.CurrentNormalHistory = renderGraph.ImportTexture(history.CurrentNormal);

        using (var builder = renderGraph.AddUnsafePass<PassData>("Caelix DXR Trace", out var passData))
        {
            passData.width = (uint)cameraData.scaledWidth;
            passData.height = (uint)cameraData.scaledHeight;
            // TODO: Replace this to use the camera projection matrix instead.
            passData.fov = 60.0f;
            passData.frameIndex = Time.frameCount;

            passData.worldToCamera = worldToCamera;
            passData.cameraToWorld = worldToCamera.inverse;
            passData.previousWorldToCamera = history.PreviousViewMatrix;
            passData.cameraWorldPosition = passData.cameraToWorld.MultiplyPoint3x4(Vector3.zero);
            passData.convergedFrames = history.ConvergedFrames;

            passData.settings = settings;
            passData.mainLightColor = ResolveMainLightColor(frameData.Get<UniversalLightData>());

            passData.DeterministicRadiance = resources.DeterministicRadiance;
            passData.DiffuseRadiance = resources.StochasticDiffuse;
            passData.SpecularRadiance = resources.StochasticSpecular;
            passData.Albedo = resources.Albedo;
            passData.Normal = resources.Normal;
            passData.Depth = resources.Depth;
            passData.MotionVector = resources.MotionVector;
            passData.CurrentDepthHistory = resources.CurrentDepthHistory;
            passData.CurrentNormalHistory = resources.CurrentNormalHistory;

            passData.voxShaderRT = rayTracingShader;
            passData.voxAS = caelixX.voxelScene;
            passData.blueNoiseTexture = blueNoiseTexture;
            passData.brickMaterial = caelixX.brickMat;

            builder.UseTexture(passData.DeterministicRadiance, AccessFlags.Write);
            builder.UseTexture(passData.DiffuseRadiance, AccessFlags.Write);
            builder.UseTexture(passData.SpecularRadiance, AccessFlags.Write);
            builder.UseTexture(passData.Albedo, AccessFlags.Write);
            builder.UseTexture(passData.Normal, AccessFlags.Write);
            builder.UseTexture(passData.Depth, AccessFlags.Write);
            builder.UseTexture(passData.MotionVector, AccessFlags.Write);
            builder.UseTexture(passData.CurrentDepthHistory, AccessFlags.Write);
            builder.UseTexture(passData.CurrentNormalHistory, AccessFlags.Write);

            builder.SetGlobalTextureAfterPass(passData.DeterministicRadiance, CaelixShaderIDs.DeterministicRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.DiffuseRadiance, CaelixShaderIDs.DiffuseRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.SpecularRadiance, CaelixShaderIDs.SpecularRadianceTex);
            builder.SetGlobalTextureAfterPass(passData.Albedo, CaelixShaderIDs.AlbedoTex);
            builder.SetGlobalTextureAfterPass(passData.Normal, CaelixShaderIDs.NormalTex);
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

    private static Vector4 ResolveMainLightColor(UniversalLightData lightData)
    {
        int index = lightData.mainLightIndex;
        if (index < 0 || index >= lightData.visibleLights.Length)
        {
            return Vector4.zero;
        }

        Light light = lightData.visibleLights[index].light;
        if (light == null)
        {
            return Vector4.zero;
        }

        Color temperature = light.useColorTemperature
            ? Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature)
            : Color.white;

        return light.color.linear * light.intensity * temperature;
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
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DiffuseRadianceTarget", data.DiffuseRadiance);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "SpecularRadianceTarget", data.SpecularRadiance);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "AlbedoTarget", data.Albedo);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "NormalTarget", data.Normal);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DepthTarget", data.Depth);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "MotionVectorTarget", data.MotionVector);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "g_CurrentDepthHistory", data.CurrentDepthHistory);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "g_CurrentNormalHistory", data.CurrentNormalHistory);

        // The sky provider publishes its cubemap as a plain global, which render graph cannot track
        // (builder.UseGlobalTexture does not see it), so it is fetched directly here.
        // TODO: FIXME: Maybe PR to PBSky repo so we can use the texture properly ... idk
        natcmd.SetRayTracingTextureParam(data.voxShaderRT, "g_Sky", ResolveSkyTexture());

        if (data.blueNoiseTexture != null)
        {
            natcmd.SetRayTracingTextureParam(data.voxShaderRT, CaelixShaderIDs.BlueNoiseTexture, data.blueNoiseTexture);
        }

        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_FrameIndex", data.frameIndex);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_ConvergenceStep", data.convergedFrames);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BounceCountOpaque", data.settings.bounceCountOpaque);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BounceCountTransparent", data.settings.bounceCountTransparent);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_spp", data.settings.samplesPerPixel);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_EnableSkySun", data.settings.enableSkySun ? 1 : 0);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_SkySunDiskRadius", data.settings.sunDiskRadiusRadians);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_SkySunFlareRadius", data.settings.sunFlareRadiusRadians);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_Zoom", Mathf.Tan(Mathf.Deg2Rad * data.fov * 0.5f));
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_AspectRatio", data.width / (float)data.height);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_CameraWorldPosition", data.cameraWorldPosition);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentWorldToCamera", data.worldToCamera);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentCameraToWorld", data.cameraToWorld);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_PrevWorldToCamera", data.previousWorldToCamera);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_mainLightColor", data.mainLightColor);

        context.cmd.DispatchRays(data.voxShaderRT, "MainRayGenShader", data.width, data.height, 1, null);
    }

    private static Texture ResolveSkyTexture()
    {
        Texture sky = Shader.GetGlobalTexture(CaelixShaderIDs.GlossyEnvironmentCubeMap);
        if (sky != null)
        {
            return sky;
        }

        if (!s_WarnedMissingSky)
        {
            // Warn once: this is normal for a frame or two after a sky change, and log spam here is
            // expensive enough to skew frame timings.
            Debug.LogWarning("Caelix: cannot obtain the current sky cubemap. Using a white fallback sky.");
            s_WarnedMissingSky = true;
        }

        if (s_FallbackSky == null)
        {
            s_FallbackSky = new Cubemap(1, GraphicsFormat.B8G8R8A8_SRGB, TextureCreationFlags.None)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            for (int face = 0; face < 6; face++)
            {
                s_FallbackSky.SetPixel((CubemapFace)face, 0, 0, Color.white);
            }

            s_FallbackSky.Apply();
        }

        return s_FallbackSky;
    }
}
