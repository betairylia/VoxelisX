using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Caelix.Rendering.RayQuery;

/// <summary>
/// Stage 1 of 3: traces the voxel scene and produces the Caelix G-buffer.
/// </summary>
/// <remarks>
/// Writes deterministic (non-denoised) radiance, per-lobe stochastic radiance with hit distance
/// (diffuse and specular, NRD-style), albedo, normal+roughness, depth and 2.5D motion vectors,
/// plus this frame's depth/normal history for next frame's temporal rejection. Everything it
/// produces is published to <see cref="CaelixFrameResources"/> and, for the shaders that sample
/// them by name, as global textures. Downstream stages read that contract and never touch the tracer.
/// <para>
/// Two interchangeable trace backends produce that same G-buffer: the DXR pipeline
/// (<see cref="CaelixTraceBackend.DXR"/>) and an inline ray query compute kernel
/// (<see cref="CaelixTraceBackend.InlineRayQuery"/>). They run the same path-tracing code and are
/// configured through <see cref="ConfigureSettings"/> and <see cref="ConfigureRayQuery"/>.
/// </para>
/// </remarks>
public class CaelixGBufferPass : ScriptableRenderPass
{
    private CaelixRenderer caelixX;
    private RayTracingShader rayTracingShader;
    private Texture2D blueNoiseTexture;
    private CaelixTraceSettings settings = new CaelixTraceSettings().Validated();

    private CaelixTraceBackend backend = CaelixTraceBackend.DXR;
    private CaelixRayQueryRenderer rayQuery;
    private ComputeShader computeShader;
    private int kernel = -1;
    /// <summary>The three per-field material bake kernels; all must exist for the backend to be ready.</summary>
    private int[] bakeMaterialsKernels;

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

        internal CaelixTraceBackend backend;
        internal ComputeShader computeShader;
        internal int kernel;
        internal int[] bakeMaterialsKernels;
        /// <summary>Set the first time a material buffer is used: the bake kernel fills it before the trace.</summary>
        internal bool bakeMaterials;
        /// <summary>One VoxelMaterial per 16-bit block ID, bound as <c>g_Materials</c>. Ray query backend only.</summary>
        internal GraphicsBuffer materialTable;
        /// <summary>Every sector's brick records, bound as <c>g_bricks</c>. Ray query backend only.</summary>
        internal GraphicsBuffer brickPool;
        /// <summary>Per-RTAS-instance records, bound as <c>g_Instances</c>. Ray query backend only.</summary>
        internal GraphicsBuffer instanceTable;
    }

    /// <summary>
    /// Binds the DXR scene renderer, tracing resources and this frame's settings.
    /// Called once per camera before enqueueing.
    /// </summary>
    public void ConfigureSettings(
        CaelixRenderer vox, RayTracingShader rtShader, Texture2D blueNoise, CaelixTraceSettings traceSettings)
    {
        backend = CaelixTraceBackend.DXR;
        caelixX = vox;
        rayTracingShader = rtShader;
        blueNoiseTexture = blueNoise;
        settings = traceSettings.Validated();
    }

    /// <summary>
    /// Binds the inline ray query scene renderer, its compute kernel and this frame's settings.
    /// Called once per camera before enqueueing, instead of <see cref="ConfigureSettings"/>.
    /// </summary>
    public void ConfigureRayQuery(
        CaelixRayQueryRenderer rq, ComputeShader cs, Texture2D blueNoise, CaelixTraceSettings traceSettings)
    {
        backend = CaelixTraceBackend.InlineRayQuery;
        rayQuery = rq;
        computeShader = cs;
        // HasKernel first: FindKernel logs an error and throws when the kernel is missing, and a
        // renderer asset can easily point at the wrong compute shader.
        kernel = (cs != null && cs.HasKernel("CaelixPathTraceKernel")) ? cs.FindKernel("CaelixPathTraceKernel") : -1;
        bakeMaterialsKernels = FindBakeKernels(cs);
        blueNoiseTexture = blueNoise;
        settings = traceSettings.Validated();
    }

    private static readonly string[] BakeKernelNames =
    {
        "CaelixBakeMaterialsAlbedo", "CaelixBakeMaterialsEmission", "CaelixBakeMaterialsScalars"
    };

    /// <summary>All three bake kernel indices, or null when any is missing from the compute shader.</summary>
    private static int[] FindBakeKernels(ComputeShader cs)
    {
        if (cs == null)
        {
            return null;
        }

        int[] kernels = new int[BakeKernelNames.Length];
        for (int i = 0; i < kernels.Length; i++)
        {
            if (!cs.HasKernel(BakeKernelNames[i]))
            {
                return null;
            }

            kernels[i] = cs.FindKernel(BakeKernelNames[i]);
        }

        return kernels;
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => backend == CaelixTraceBackend.DXR
        ? (caelixX != null && rayTracingShader != null)
        : (rayQuery != null && computeShader != null && kernel >= 0 && bakeMaterialsKernels != null);

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

        // The pass name is what the frame debugger and the profiler show, so it names the backend:
        // the two are meant to be compared against each other.
        string passName = backend == CaelixTraceBackend.DXR ? "Caelix DXR Trace" : "Caelix RayQuery Trace";
        using (var builder = renderGraph.AddUnsafePass<PassData>(passName, out var passData))
        {
            passData.width = (uint)cameraData.scaledWidth;
            passData.height = (uint)cameraData.scaledHeight;
            // The camera's own vertical FOV, and the jitter the filters will reconstruct rays with:
            // both come from the history so the tracer and every consumer share one projection.
            passData.zoom = history.Zoom;
            passData.aspectRatio = history.Aspect;
            passData.jitter = history.Jitter;
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
            passData.voxAS = backend == CaelixTraceBackend.DXR ? caelixX.voxelScene : rayQuery.voxelScene;
            passData.blueNoiseTexture = blueNoiseTexture;
            // Only the DXR path has a hit-group material to push per-frame uniforms onto.
            passData.brickMaterial = backend == CaelixTraceBackend.DXR ? caelixX.brickMat : null;

            passData.backend = backend;
            passData.computeShader = computeShader;
            passData.kernel = kernel;
            passData.brickPool = rayQuery?.Pool?.Buffer;
            passData.instanceTable = rayQuery?.Instances?.Buffer;
            passData.bakeMaterialsKernels = bakeMaterialsKernels;
            passData.materialTable = rayQuery?.MaterialTable;
            // The bake is recorded ahead of the trace in the same command buffer, so flipping the
            // flag at record time is safe; the pass is never culled.
            passData.bakeMaterials = rayQuery != null && !rayQuery.MaterialsBaked;
            if (rayQuery != null)
            {
                rayQuery.MaterialsBaked = true;
            }

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
        if (data.backend == CaelixTraceBackend.InlineRayQuery)
        {
            ExecuteRayQuery(data, context);
            return;
        }

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
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_Zoom", data.zoom);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_AspectRatio", data.aspectRatio);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_Jitter", data.jitter);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_CameraWorldPosition", data.cameraWorldPosition);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentWorldToCamera", data.worldToCamera);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentCameraToWorld", data.cameraToWorld);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_PrevWorldToCamera", data.previousWorldToCamera);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_mainLightColor", data.mainLightColor);

        context.cmd.DispatchRays(data.voxShaderRT, "MainRayGenShader", data.width, data.height, 1, null);
    }

    /// <summary>
    /// Inline ray query backend: the same uniforms and the same targets as the DXR body, bound to a
    /// compute kernel instead of a ray tracing shader, plus the two buffers that replace the shader
    /// table's per-instance bindings.
    /// </summary>
    /// <remarks>
    /// Everything goes through the native command buffer: the unsafe pass context exposes no
    /// compute-shader setters of its own.
    /// </remarks>
    private static void ExecuteRayQuery(PassData data, UnsafeGraphContext context)
    {
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        ComputeShader cs = data.computeShader;
        int k = data.kernel;

        if (data.settings.buildAccelerationStructure)
        {
            cmd.BuildRayTracingAccelerationStructure(data.voxAS);
        }

        cmd.SetRayTracingAccelerationStructure(cs, k, "g_AccelStruct", data.voxAS);
        cmd.SetComputeBufferParam(cs, k, "g_bricks", data.brickPool);
        cmd.SetComputeBufferParam(cs, k, "g_Instances", data.instanceTable);

        // The static material tables cannot live in the trace kernel (see the compute shader),
        // so they are copied into a buffer once, by a kernel small enough to carry them.
        if (data.bakeMaterials)
        {
            for (int i = 0; i < data.bakeMaterialsKernels.Length; i++)
            {
                int bake = data.bakeMaterialsKernels[i];
                cmd.SetComputeBufferParam(cs, bake, "g_MaterialsOut", data.materialTable);
                cmd.DispatchCompute(cs, bake, CaelixRayQueryRenderer.MaterialTableEntries / 64, 1, 1);
            }
        }

        cmd.SetComputeBufferParam(cs, k, "g_Materials", data.materialTable);

        cmd.SetComputeTextureParam(cs, k, "DeterministicRadianceTarget", (RTHandle)data.DeterministicRadiance);
        cmd.SetComputeTextureParam(cs, k, "DiffuseRadianceTarget", (RTHandle)data.DiffuseRadiance);
        cmd.SetComputeTextureParam(cs, k, "SpecularRadianceTarget", (RTHandle)data.SpecularRadiance);
        cmd.SetComputeTextureParam(cs, k, "AlbedoTarget", (RTHandle)data.Albedo);
        cmd.SetComputeTextureParam(cs, k, "NormalTarget", (RTHandle)data.Normal);
        cmd.SetComputeTextureParam(cs, k, "DepthTarget", (RTHandle)data.Depth);
        cmd.SetComputeTextureParam(cs, k, "MotionVectorTarget", (RTHandle)data.MotionVector);
        cmd.SetComputeTextureParam(cs, k, "g_CurrentDepthHistory", (RTHandle)data.CurrentDepthHistory);
        cmd.SetComputeTextureParam(cs, k, "g_CurrentNormalHistory", (RTHandle)data.CurrentNormalHistory);

        // The sky provider publishes its cubemap as a plain global, which render graph cannot track,
        // so it is fetched directly here — same as the DXR body.
        cmd.SetComputeTextureParam(cs, k, "g_Sky", ResolveSkyTexture());

        if (data.blueNoiseTexture != null)
        {
            cmd.SetComputeTextureParam(cs, k, CaelixShaderIDs.BlueNoiseTexture, data.blueNoiseTexture);
        }

        cmd.SetComputeIntParam(cs, "g_FrameIndex", data.frameIndex);
        cmd.SetComputeIntParam(cs, "g_ConvergenceStep", data.convergedFrames);
        cmd.SetComputeIntParam(cs, "g_BounceCountOpaque", data.settings.bounceCountOpaque);
        cmd.SetComputeIntParam(cs, "g_BounceCountTransparent", data.settings.bounceCountTransparent);
        cmd.SetComputeIntParam(cs, "g_spp", data.settings.samplesPerPixel);
        cmd.SetComputeIntParam(cs, "g_EnableSkySun", data.settings.enableSkySun ? 1 : 0);
        cmd.SetComputeFloatParam(cs, "g_SkySunDiskRadius", data.settings.sunDiskRadiusRadians);
        cmd.SetComputeFloatParam(cs, "g_SkySunFlareRadius", data.settings.sunFlareRadiusRadians);
        cmd.SetComputeFloatParam(cs, "g_Zoom", data.zoom);
        cmd.SetComputeFloatParam(cs, "g_AspectRatio", data.aspectRatio);
        cmd.SetComputeVectorParam(cs, "g_Jitter", data.jitter);
        cmd.SetComputeVectorParam(cs, "g_CameraWorldPosition", data.cameraWorldPosition);
        cmd.SetComputeVectorParam(cs, "g_mainLightColor", data.mainLightColor);
        cmd.SetComputeMatrixParam(cs, "g_CurrentWorldToCamera", data.worldToCamera);
        cmd.SetComputeMatrixParam(cs, "g_CurrentCameraToWorld", data.cameraToWorld);
        cmd.SetComputeMatrixParam(cs, "g_PrevWorldToCamera", data.previousWorldToCamera);

        // Replaces DispatchRaysDimensions: the kernel needs the launch size both to flip the
        // vertical axis and to discard the threads the 8x8 rounding adds.
        cmd.SetComputeIntParams(cs, "g_LaunchDim", (int)data.width, (int)data.height);

        cmd.DispatchCompute(cs, k, (int)((data.width + 7) / 8), (int)((data.height + 7) / 8), 1);
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
