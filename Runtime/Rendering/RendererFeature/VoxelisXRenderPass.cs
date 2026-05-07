using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Main render pass for ray-traced voxel rendering in Unity's URP render graph.
/// </summary>
/// <remarks>
/// This pass uses DXR (DirectX Raytracing) to render voxels stored in ray tracing
/// acceleration structures. It supports temporal accumulation for progressive rendering
/// and outputs color, albedo, normal, and depth information.
/// </remarks>
public class VoxelisXRenderPass : ScriptableRenderPass
{
    /// <summary>
    /// Ray tracing shader for voxel rendering.
    /// </summary>
    public RayTracingShader rayTracingShader = null;

    private VoxelisXRenderer voxelisX;

    private int maximumAverageFrames;
    private int bounceCountOpaque = 4;
    private int bounceCountTransparent = 5;
    private int samplesPerPixel = 1;
    private VoxelisXRendererFeature.DebugView debugView = VoxelisXRendererFeature.DebugView.Regular;
    private VoxelisXIndirectDenoisingSettings indirectDenoisingSettings = VoxelisXIndirectDenoisingSettings.Default;
    private bool enableTemporalRadiance = true;
    private float temporalRadianceCurrentFrameMinWeight = 0.0f;
    private bool temporalRadianceDepthRejection = true;
    private float temporalRadianceDepthTolerance = 0.05f;
    private float temporalRadianceRelativeDepthTolerance = 0.01f;
    private bool temporalRadianceNormalRejection = true;
    private float temporalRadianceNormalThreshold = 0.85f;
    private bool temporalRadianceBilinearHistory = true;

    /// <summary>
    /// Post-process material for copying depth and flipping render targets if needed.
    /// </summary>
    private Material flip;
    private Texture2D blueNoiseTexture;
    private Material indirectRadiancePipelineMaterial;
    private Material indirectATrousFilterMaterial;
    private const string IndirectRadiancePipelineShaderName = "Hidden/VoxelisX/IndirectRadiancePipeline";
    private const string IndirectATrousFilterShaderName = "Hidden/VoxelisX/IndirectATrousFilter";
    private const int IndirectPipelinePassSpatialFilterX = 0;
    private const int IndirectPipelinePassSpatialFilterY = 1;
    private const int IndirectPipelinePassTemporalAccumulation = 2;
    private const int IndirectPipelinePassComposite = 3;
    private const int IndirectATrousFilterPass = 0;
    private static readonly int IndirectRadianceTexID = Shader.PropertyToID("_IndirectRadianceTex");
    private static readonly int DirectRadianceTexID = Shader.PropertyToID("_DirectRadianceTex");
    private static readonly int AlbedoTexID = Shader.PropertyToID("_AlbedoTex");
    private static readonly int NormalTexID = Shader.PropertyToID("_NormalTex");
    private static readonly int DepthTexID = Shader.PropertyToID("_DepthTex");
    private static readonly int MotionVectorTexID = Shader.PropertyToID("_MotionVectorTex");
    private static readonly int CurrentDepthHistoryTexID = Shader.PropertyToID("_CurrentDepthHistoryTex");
    private static readonly int CurrentNormalHistoryTexID = Shader.PropertyToID("_CurrentNormalHistoryTex");
    private static readonly int AccumulatedIndirectRadianceTexID = Shader.PropertyToID("_AccumulatedIndirectRadianceTex");
    private static readonly int PreviousIndirectRadianceHistoryTexID = Shader.PropertyToID("_PreviousIndirectRadianceHistoryTex");
    private static readonly int PreviousDepthHistoryTexID = Shader.PropertyToID("_PreviousDepthHistoryTex");
    private static readonly int PreviousNormalHistoryTexID = Shader.PropertyToID("_PreviousNormalHistoryTex");
    private static readonly int SeparableFilterRadiusID = Shader.PropertyToID("_SeparableFilterRadius");
    private static readonly int SeparableFilterDistanceSigmaID = Shader.PropertyToID("_SeparableFilterDistanceSigma");
    private static readonly int ATrousStepWidthID = Shader.PropertyToID("_ATrousStepWidth");
    private static readonly int ATrousUseFaceHashID = Shader.PropertyToID("_ATrousUseFaceHash");
    private static readonly int ATrousNormalPowerID = Shader.PropertyToID("_ATrousNormalPower");
    private static readonly int ATrousDepthSigmaID = Shader.PropertyToID("_ATrousDepthSigma");
    private static readonly int ATrousRelativeDepthSigmaID = Shader.PropertyToID("_ATrousRelativeDepthSigma");
    private static readonly int ATrousRadianceSigmaID = Shader.PropertyToID("_ATrousRadianceSigma");
    private const string BlueNoiseTextureName = "stbnTexture";

    /// <summary>
    /// Stores previous camera state for temporal accumulation.
    /// </summary>
    internal sealed class PrevCameraState
    {
        internal Matrix4x4 mat;
        internal int frames;
        internal readonly HistoryBuffers history = new HistoryBuffers();
    }

    private static Dictionary<Camera, PrevCameraState> prevFrameState = new Dictionary<Camera, PrevCameraState>();
    private int EnvMapID = Shader.PropertyToID("_GlossyEnvironmentCubeMap");

    internal sealed class HistoryBuffers
    {
        private RTHandle indirectRadianceA;
        private RTHandle indirectRadianceB;
        private RTHandle depthA;
        private RTHandle depthB;
        private RTHandle normalA;
        private RTHandle normalB;
        private bool useAAsPrevious = true;

        internal bool IsValid { get; private set; }

        internal RTHandle PreviousIndirectRadiance => useAAsPrevious ? indirectRadianceA : indirectRadianceB;
        internal RTHandle CurrentIndirectRadiance => useAAsPrevious ? indirectRadianceB : indirectRadianceA;
        internal RTHandle PreviousDepth => useAAsPrevious ? depthA : depthB;
        internal RTHandle CurrentDepth => useAAsPrevious ? depthB : depthA;
        internal RTHandle PreviousNormal => useAAsPrevious ? normalA : normalB;
        internal RTHandle CurrentNormal => useAAsPrevious ? normalB : normalA;

        internal void EnsureAllocated(int width, int height)
        {
            RenderTextureDescriptor indirectDesc = MakeHistoryDescriptor(width, height, RenderTextureFormat.ARGBHalf);
            RenderTextureDescriptor depthDesc = MakeHistoryDescriptor(width, height, RenderTextureFormat.RFloat);
            RenderTextureDescriptor normalDesc = MakeHistoryDescriptor(width, height, RenderTextureFormat.ARGBHalf);

            bool reallocated = false;
            reallocated |= RenderingUtils.ReAllocateIfNeeded(
                ref indirectRadianceA, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "VoxelisX_HistoryIndirectRadiance_A");
            reallocated |= RenderingUtils.ReAllocateIfNeeded(
                ref indirectRadianceB, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "VoxelisX_HistoryIndirectRadiance_B");
            reallocated |= RenderingUtils.ReAllocateIfNeeded(
                ref depthA, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "VoxelisX_HistoryDepth_A");
            reallocated |= RenderingUtils.ReAllocateIfNeeded(
                ref depthB, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "VoxelisX_HistoryDepth_B");
            reallocated |= RenderingUtils.ReAllocateIfNeeded(
                ref normalA, normalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "VoxelisX_HistoryNormal_A");
            reallocated |= RenderingUtils.ReAllocateIfNeeded(
                ref normalB, normalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "VoxelisX_HistoryNormal_B");

            if (reallocated)
            {
                IsValid = false;
                useAAsPrevious = true;
            }
        }

        internal void SwapAfterWrite()
        {
            useAAsPrevious = !useAAsPrevious;
            IsValid = true;
        }

        internal void Dispose()
        {
            indirectRadianceA?.Release();
            indirectRadianceB?.Release();
            depthA?.Release();
            depthB?.Release();
            normalA?.Release();
            normalB?.Release();

            indirectRadianceA = null;
            indirectRadianceB = null;
            depthA = null;
            depthB = null;
            normalA = null;
            normalB = null;
            IsValid = false;
        }

        private static RenderTextureDescriptor MakeHistoryDescriptor(int width, int height, RenderTextureFormat format)
        {
            RenderTextureDescriptor desc = new RenderTextureDescriptor(width, height, format, 0)
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                volumeDepth = 1,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false
            };

            return desc;
        }
    }

    internal class RayTracingPassData
    {
        internal uint width;
        internal uint height;
        internal float fov;

        internal Matrix4x4 cameraMat;
        internal Matrix4x4 cameraToWorldMat;
        internal Vector3 cameraWorldPosition;

        internal int avgFrames;
        internal int bounceCountOpaque;
        internal int bounceCountTransparent;
        internal int samplesPerPixel;

        internal Light mainLight;

        internal TextureHandle DirectRadiance;
        internal TextureHandle IndirectRadiance;
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
        internal PrevCameraState cameraState;
        internal bool historyValid;
    }

    internal class SpatialFilterPassData
    {
        internal int width;
        internal int height;
        internal bool enabled;
        internal VoxelisXSeparable15TapFilterSettings settings;
        internal TextureHandle RawIndirectRadiance;
        internal TextureHandle Normal;
        internal Material material;
        internal int passIndex;
    }

    internal class ATrousFilterPassData
    {
        internal int width;
        internal int height;
        internal int stepWidth;
        internal VoxelisXATrousFilterSettings settings;
        internal TextureHandle SourceIndirectRadiance;
        internal TextureHandle Normal;
        internal TextureHandle CurrentDepthHistory;
        internal Material material;
    }

    internal class TemporalAccumulationPassData
    {
        internal int width;
        internal int height;
        internal int convergenceStep;
        internal bool enableTemporalRadiance;
        internal float temporalRadianceCurrentFrameMinWeight;
        internal bool temporalRadianceDepthRejection;
        internal float temporalRadianceDepthTolerance;
        internal float temporalRadianceRelativeDepthTolerance;
        internal bool temporalRadianceNormalRejection;
        internal float temporalRadianceNormalThreshold;
        internal bool temporalRadianceBilinearHistory;
        internal bool historyValid;
        internal TextureHandle FilteredIndirectRadiance;
        internal TextureHandle MotionVector;
        internal TextureHandle PreviousIndirectRadianceHistory;
        internal TextureHandle PreviousDepthHistory;
        internal TextureHandle CurrentDepthHistory;
        internal TextureHandle PreviousNormalHistory;
        internal TextureHandle CurrentNormalHistory;
        internal Material material;
        internal HistoryBuffers history;
    }

    internal class CompositePassData
    {
        internal int width;
        internal int height;
        internal TextureHandle DirectRadiance;
        internal TextureHandle AccumulatedIndirectRadiance;
        internal TextureHandle Albedo;
        internal Material material;
    }

    internal class CopyToCameraPassData
    {
        internal VoxelisXRendererFeature.DebugView debugView;
        internal TextureHandle Color;
        internal TextureHandle Depth;
        internal TextureHandle MotionVector;
        internal TextureHandle GI_dest;
        internal TextureHandle Depth_dest;
        internal Material copyDSMat;
    }

    public class VoxelisXPassData : ContextItem
    {
        public TextureHandle Color;
        public TextureHandle Albedo;
        public TextureHandle Normal;
        public TextureHandle Depth;
        public TextureHandle MotionVector;
        public TextureHandle RawIndirectRadiance;
        public TextureHandle FilteredIndirectRadiance;
        public TextureHandle AccumulatedIndirectRadiance;

        public override void Reset()
        {
            Color = TextureHandle.nullHandle;
            Albedo = TextureHandle.nullHandle;
            Normal = TextureHandle.nullHandle;
            Depth = TextureHandle.nullHandle;
            MotionVector = TextureHandle.nullHandle;
            RawIndirectRadiance = TextureHandle.nullHandle;
            FilteredIndirectRadiance = TextureHandle.nullHandle;
            AccumulatedIndirectRadiance = TextureHandle.nullHandle;
        }
    }

    internal class PostPassData
    {
        internal TextureHandle RT;
        internal TextureHandle Depth;
        internal bool needsFlip;
    }

    /// <summary>
    /// Constructs the render pass with the specified maximum accumulation frames.
    /// </summary>
    /// <param name="maxAvgFrames">Maximum frames to accumulate for temporal anti-aliasing (default: 120).</param>
    public VoxelisXRenderPass(int maxAvgFrames = 120)
    {
        maximumAverageFrames = maxAvgFrames;
    }

    /// <summary>
    /// Initializes the render pass with the VoxelisX renderer and required shaders/materials.
    /// </summary>
    /// <param name="rtShader">Ray tracing shader for voxel rendering.</param>
    /// <param name="vox">VoxelisX renderer instance.</param>
    /// <param name="flip">Material for post-processing and depth copying.</param>
    /// <param name="blueNoiseTexture">Optional 128x8192 RG integer STBN texture for ray sampling.</param>
    public void InitVoxelisX(RayTracingShader rtShader, VoxelisXRenderer vox, Material flip, Texture2D blueNoiseTexture)
    {
        rayTracingShader = rtShader;
        voxelisX = vox;
        this.flip = flip;
        ConfigureBlueNoiseTexture(blueNoiseTexture);
    }

    public void ConfigureRayTracingSettings(int bounceCountOpaque, int bounceCountTransparent, int samplesPerPixel)
    {
        this.bounceCountOpaque = Mathf.Max(0, bounceCountOpaque);
        this.bounceCountTransparent = Mathf.Max(0, bounceCountTransparent);
        this.samplesPerPixel = Mathf.Max(1, samplesPerPixel);
    }

    public void ConfigureBlueNoiseTexture(Texture2D texture)
    {
        blueNoiseTexture = texture;
    }

    public void ConfigureDebugView(VoxelisXRendererFeature.DebugView debugView)
    {
        this.debugView = debugView;
    }

    public void ConfigureIndirectDenoisingSettings(VoxelisXIndirectDenoisingSettings settings)
    {
        indirectDenoisingSettings = ValidateIndirectDenoisingSettings(settings);
    }

    private static VoxelisXIndirectDenoisingSettings ValidateIndirectDenoisingSettings(VoxelisXIndirectDenoisingSettings settings)
    {
        settings.separable15Tap.radius = Mathf.Clamp(settings.separable15Tap.radius, 1, 7);
        settings.separable15Tap.distanceSigma = Mathf.Max(0.0001f, settings.separable15Tap.distanceSigma);

        settings.aTrous.iterations = Mathf.Clamp(settings.aTrous.iterations, 1, 6);
        settings.aTrous.normalPower = Mathf.Max(0.0f, settings.aTrous.normalPower);
        settings.aTrous.depthSigma = Mathf.Max(0.0001f, settings.aTrous.depthSigma);
        settings.aTrous.relativeDepthSigma = Mathf.Max(0.0f, settings.aTrous.relativeDepthSigma);
        settings.aTrous.radianceSigma = Mathf.Max(0.0001f, settings.aTrous.radianceSigma);

        return settings;
    }

    public void ConfigureTemporalRadianceSettings(
        bool enabled,
        float currentFrameMinWeight,
        bool depthRejection,
        float depthTolerance,
        float relativeDepthTolerance,
        bool normalRejection,
        float normalThreshold,
        bool bilinearHistory)
    {
        enableTemporalRadiance = enabled;
        temporalRadianceCurrentFrameMinWeight = Mathf.Clamp01(currentFrameMinWeight);
        temporalRadianceDepthRejection = depthRejection;
        temporalRadianceDepthTolerance = Mathf.Max(0.0f, depthTolerance);
        temporalRadianceRelativeDepthTolerance = Mathf.Max(0.0f, relativeDepthTolerance);
        temporalRadianceNormalRejection = normalRejection;
        temporalRadianceNormalThreshold = Mathf.Clamp(normalThreshold, -1.0f, 1.0f);
        temporalRadianceBilinearHistory = bilinearHistory;
    }

    /// <summary>
    /// Records the render pass into Unity's render graph.
    /// </summary>
    /// <param name="renderGraph">The render graph to record into.</param>
    /// <param name="frameData">Frame context data including camera and lighting information.</param>
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        // Get camera data to help define texture properties
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        cameraData.camera.forceIntoRenderTexture = true;

        // RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
        RenderTextureDescriptor rtDesc = new RenderTextureDescriptor(
            cameraData.cameraTargetDescriptor.width,
            cameraData.cameraTargetDescriptor.height,
            RenderTextureFormat.ARGBFloat
        );

        // rtDesc.depthBufferBits = cameraData.cameraTargetDescriptor.depthBufferBits;
        // rtDesc.depthStencilFormat = cameraData.cameraTargetDescriptor.depthStencilFormat;
        rtDesc.enableRandomWrite = true;

        Material indirectMaterial = EnsureIndirectRadiancePipelineMaterial();
        if (indirectMaterial == null)
        {
            return;
        }

        TextureHandle RT = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, rtDesc, "VoxelisX_outColor", false);
        TextureHandle RT_DirectRadiance = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, rtDesc, "VoxelisX_outDirectRadiance", false);

        RenderTextureDescriptor indirectDesc = rtDesc;
        indirectDesc.colorFormat = RenderTextureFormat.ARGBHalf;
        TextureHandle RT_RawIndirectRadiance = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, indirectDesc, "VoxelisX_outIndirectRadianceRaw", false);
        TextureHandle RT_SpatialIndirectRadianceTemp = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, indirectDesc, "VoxelisX_outIndirectRadianceSpatialTemp", false);
        TextureHandle RT_FilteredIndirectRadiance = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, indirectDesc, "VoxelisX_outIndirectRadianceFiltered", false);

        RenderTextureDescriptor albedoDesc = rtDesc;
        albedoDesc.colorFormat = RenderTextureFormat.Default;
        TextureHandle RT_Albedo = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, albedoDesc, "VoxelisX_outAlbedo", false);

        RenderTextureDescriptor normalDesc = rtDesc;
        normalDesc.colorFormat = RenderTextureFormat.ARGBHalf;
        TextureHandle RT_Normal = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, normalDesc, "VoxelisX_outNormal", false);

        RenderTextureDescriptor depthDesc = rtDesc;
        depthDesc.colorFormat = RenderTextureFormat.RFloat;

        TextureHandle RT_Depth = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, depthDesc, "VoxelisX_NormalDepth", false);

        RenderTextureDescriptor motionVectorDesc = rtDesc;
        motionVectorDesc.colorFormat = RenderTextureFormat.RGFloat;
        TextureHandle RT_MotionVector = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, motionVectorDesc, "VoxelisX_outMotionVector", false);

        // TextureHandle RT_TAA = UniversalRenderer.CreateRenderGraphTexture(
        //     renderGraph, rtDesc, "VoxelisX_accumulateColor", false);

        Matrix4x4 cameraMat = cameraData.GetViewMatrix();

        PrevCameraState cameraState = GetOrCreateCameraState(cameraData.camera, cameraMat);
        cameraState.history.EnsureAllocated(cameraData.scaledWidth, cameraData.scaledHeight);

        TextureHandle RT_PreviousIndirectRadianceHistory = renderGraph.ImportTexture(cameraState.history.PreviousIndirectRadiance);
        TextureHandle RT_CurrentIndirectRadianceHistory = renderGraph.ImportTexture(cameraState.history.CurrentIndirectRadiance);
        TextureHandle RT_PreviousDepthHistory = renderGraph.ImportTexture(cameraState.history.PreviousDepth);
        TextureHandle RT_CurrentDepthHistory = renderGraph.ImportTexture(cameraState.history.CurrentDepth);
        TextureHandle RT_PreviousNormalHistory = renderGraph.ImportTexture(cameraState.history.PreviousNormal);
        TextureHandle RT_CurrentNormalHistory = renderGraph.ImportTexture(cameraState.history.CurrentNormal);

        var vxPassData = frameData.GetOrCreate<VoxelisXPassData>();
        vxPassData.Color = RT;
        vxPassData.Albedo = RT_Albedo;
        vxPassData.Normal = RT_Normal;
        vxPassData.Depth = RT_Depth;
        vxPassData.MotionVector = RT_MotionVector;
        vxPassData.RawIndirectRadiance = RT_RawIndirectRadiance;
        vxPassData.AccumulatedIndirectRadiance = RT_CurrentIndirectRadianceHistory;

        using (var builder = renderGraph.AddUnsafePass<RayTracingPassData>("VoxelisX DXR Trace", out var passData))
        {
            passData.width = (uint)cameraData.scaledWidth;
            passData.height = (uint)cameraData.scaledHeight;
            passData.fov = 60.0f;

            passData.DirectRadiance = RT_DirectRadiance;
            passData.IndirectRadiance = RT_RawIndirectRadiance;
            passData.Albedo = RT_Albedo;
            passData.Normal = RT_Normal;
            passData.Depth = RT_Depth;
            passData.MotionVector = RT_MotionVector;
            passData.CurrentDepthHistory = RT_CurrentDepthHistory;
            passData.CurrentNormalHistory = RT_CurrentNormalHistory;

            passData.mainLight = frameData.Get<UniversalLightData>().visibleLights[frameData.Get<UniversalLightData>().mainLightIndex].light;

            passData.cameraMat = cameraMat;
            passData.cameraToWorldMat = passData.cameraMat.inverse;
            passData.cameraWorldPosition = passData.cameraToWorldMat.MultiplyPoint3x4(Vector3.zero);
            passData.avgFrames = maximumAverageFrames;
            passData.bounceCountOpaque = bounceCountOpaque;
            passData.bounceCountTransparent = bounceCountTransparent;
            passData.samplesPerPixel = samplesPerPixel;

            passData.voxShaderRT = rayTracingShader;
            passData.voxAS = voxelisX.voxelScene;
            passData.blueNoiseTexture = GetBlueNoiseTexture();
            passData.brickMaterial = voxelisX.brickMat;
            passData.cameraState = cameraState;
            passData.historyValid = cameraState.history.IsValid;

            builder.UseTexture(passData.DirectRadiance, AccessFlags.Write);
            builder.UseTexture(passData.IndirectRadiance, AccessFlags.Write);
            builder.UseTexture(RT_Albedo, AccessFlags.Write);
            builder.UseTexture(RT_Normal, AccessFlags.Write);
            builder.UseTexture(passData.Depth, AccessFlags.Write);
            builder.UseTexture(passData.MotionVector, AccessFlags.Write);
            builder.UseTexture(passData.CurrentDepthHistory, AccessFlags.Write);
            builder.UseTexture(passData.CurrentNormalHistory, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(passData.DirectRadiance, DirectRadianceTexID);
            builder.SetGlobalTextureAfterPass(passData.IndirectRadiance, IndirectRadianceTexID);
            builder.SetGlobalTextureAfterPass(passData.Albedo, AlbedoTexID);
            builder.SetGlobalTextureAfterPass(passData.Normal, NormalTexID);
            builder.SetGlobalTextureAfterPass(passData.Depth, DepthTexID);
            builder.SetGlobalTextureAfterPass(passData.MotionVector, MotionVectorTexID);
            builder.SetGlobalTextureAfterPass(passData.CurrentDepthHistory, CurrentDepthHistoryTexID);
            builder.SetGlobalTextureAfterPass(passData.CurrentNormalHistory, CurrentNormalHistoryTexID);

            // RenderGraph does not see this properly set so ...
            // builder.UseGlobalTexture(EnvMapID);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((RayTracingPassData data, UnsafeGraphContext ctx) =>
            {
                RenderRayTracingPass(data, ctx);
            });
        }

        TextureHandle RT_SelectedIndirectRadiance = RecordIndirectDenoising(
            renderGraph,
            cameraData.scaledWidth,
            cameraData.scaledHeight,
            RT_RawIndirectRadiance,
            RT_SpatialIndirectRadianceTemp,
            RT_FilteredIndirectRadiance,
            RT_Normal,
            RT_CurrentDepthHistory,
            indirectMaterial);
        vxPassData.FilteredIndirectRadiance = RT_SelectedIndirectRadiance;

        using (var builder = renderGraph.AddRasterRenderPass<TemporalAccumulationPassData>("VoxelisX Indirect Temporal Accumulation", out var passData))
        {
            passData.width = cameraData.scaledWidth;
            passData.height = cameraData.scaledHeight;
            passData.convergenceStep = cameraState.history.IsValid
                ? Mathf.Min(cameraState.frames + 1, maximumAverageFrames)
                : 0;
            passData.enableTemporalRadiance = enableTemporalRadiance;
            passData.temporalRadianceCurrentFrameMinWeight = temporalRadianceCurrentFrameMinWeight;
            passData.temporalRadianceDepthRejection = temporalRadianceDepthRejection;
            passData.temporalRadianceDepthTolerance = temporalRadianceDepthTolerance;
            passData.temporalRadianceRelativeDepthTolerance = temporalRadianceRelativeDepthTolerance;
            passData.temporalRadianceNormalRejection = temporalRadianceNormalRejection;
            passData.temporalRadianceNormalThreshold = temporalRadianceNormalThreshold;
            passData.temporalRadianceBilinearHistory = temporalRadianceBilinearHistory;
            passData.historyValid = cameraState.history.IsValid;
            passData.FilteredIndirectRadiance = RT_SelectedIndirectRadiance;
            passData.MotionVector = RT_MotionVector;
            passData.PreviousIndirectRadianceHistory = RT_PreviousIndirectRadianceHistory;
            passData.PreviousDepthHistory = RT_PreviousDepthHistory;
            passData.CurrentDepthHistory = RT_CurrentDepthHistory;
            passData.PreviousNormalHistory = RT_PreviousNormalHistory;
            passData.CurrentNormalHistory = RT_CurrentNormalHistory;
            passData.material = indirectMaterial;
            passData.history = cameraState.history;

            builder.UseTexture(passData.FilteredIndirectRadiance, AccessFlags.Read);
            builder.UseTexture(passData.MotionVector, AccessFlags.Read);
            builder.UseTexture(passData.PreviousIndirectRadianceHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousDepthHistory, AccessFlags.Read);
            builder.UseTexture(passData.CurrentDepthHistory, AccessFlags.Read);
            builder.UseTexture(passData.PreviousNormalHistory, AccessFlags.Read);
            builder.UseTexture(passData.CurrentNormalHistory, AccessFlags.Read);
            builder.UseGlobalTexture(IndirectRadianceTexID);
            builder.UseGlobalTexture(MotionVectorTexID);
            builder.UseGlobalTexture(CurrentDepthHistoryTexID);
            builder.UseGlobalTexture(CurrentNormalHistoryTexID);
            builder.SetRenderAttachment(RT_CurrentIndirectRadianceHistory, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(RT_CurrentIndirectRadianceHistory, AccumulatedIndirectRadianceTexID);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((TemporalAccumulationPassData data, RasterGraphContext ctx) =>
            {
                RenderTemporalAccumulationPass(data, ctx);
            });
        }

        using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("VoxelisX Composite", out var passData))
        {
            passData.width = cameraData.scaledWidth;
            passData.height = cameraData.scaledHeight;
            passData.DirectRadiance = RT_DirectRadiance;
            passData.AccumulatedIndirectRadiance = RT_CurrentIndirectRadianceHistory;
            passData.Albedo = RT_Albedo;
            passData.material = indirectMaterial;

            builder.UseTexture(passData.DirectRadiance, AccessFlags.Read);
            builder.UseTexture(passData.AccumulatedIndirectRadiance, AccessFlags.Read);
            builder.UseTexture(passData.Albedo, AccessFlags.Read);
            builder.UseGlobalTexture(DirectRadianceTexID);
            builder.UseGlobalTexture(AlbedoTexID);
            builder.UseGlobalTexture(AccumulatedIndirectRadianceTexID);
            builder.SetRenderAttachment(RT, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((CompositePassData data, RasterGraphContext ctx) =>
            {
                RenderCompositePass(data, ctx);
            });
        }

        using (var builder = renderGraph.AddUnsafePass<CopyToCameraPassData>("VoxelisX Copy To Camera", out var passData))
        {
            passData.Color = RT;
            passData.Depth = RT_Depth;
            passData.MotionVector = RT_MotionVector;
            passData.GI_dest = frameData.Get<UniversalResourceData>().activeColorTexture;
            passData.Depth_dest = frameData.Get<UniversalResourceData>().activeDepthTexture;
            passData.copyDSMat = flip;
            passData.debugView = debugView;

            builder.UseTexture(passData.Color, AccessFlags.Read);
            builder.UseTexture(passData.Depth, AccessFlags.Read);
            builder.UseTexture(passData.MotionVector, AccessFlags.Read);
            builder.UseGlobalTexture(DepthTexID);
            builder.UseGlobalTexture(MotionVectorTexID);
            builder.UseTexture(passData.Depth_dest, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((CopyToCameraPassData data, UnsafeGraphContext ctx) =>
            {
                RenderCopyToCameraPass(data, ctx);
            });
        }
    }

    private TextureHandle RecordIndirectDenoising(
        RenderGraph renderGraph,
        int width,
        int height,
        TextureHandle rawIndirectRadiance,
        TextureHandle spatialIndirectRadianceTemp,
        TextureHandle filteredIndirectRadiance,
        TextureHandle normal,
        TextureHandle currentDepthHistory,
        Material indirectMaterial)
    {
        switch (indirectDenoisingSettings.mode)
        {
            case VoxelisXIndirectSpatialFilterMode.Disabled:
                return rawIndirectRadiance;
            case VoxelisXIndirectSpatialFilterMode.ATrous:
                return RecordATrousFilter(
                    renderGraph,
                    width,
                    height,
                    rawIndirectRadiance,
                    spatialIndirectRadianceTemp,
                    filteredIndirectRadiance,
                    normal,
                    currentDepthHistory);
            case VoxelisXIndirectSpatialFilterMode.Separable15Tap:
            default:
                return RecordSeparable15TapFilter(
                    renderGraph,
                    width,
                    height,
                    rawIndirectRadiance,
                    spatialIndirectRadianceTemp,
                    filteredIndirectRadiance,
                    normal,
                    indirectMaterial);
        }
    }

    private TextureHandle RecordSeparable15TapFilter(
        RenderGraph renderGraph,
        int width,
        int height,
        TextureHandle rawIndirectRadiance,
        TextureHandle spatialIndirectRadianceTemp,
        TextureHandle filteredIndirectRadiance,
        TextureHandle normal,
        Material indirectMaterial)
    {
        using (var builder = renderGraph.AddRasterRenderPass<SpatialFilterPassData>("VoxelisX Indirect Spatial Filter X", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.enabled = true;
            passData.settings = indirectDenoisingSettings.separable15Tap;
            passData.RawIndirectRadiance = rawIndirectRadiance;
            passData.Normal = normal;
            passData.material = indirectMaterial;
            passData.passIndex = IndirectPipelinePassSpatialFilterX;

            builder.UseTexture(passData.RawIndirectRadiance, AccessFlags.Read);
            builder.UseTexture(passData.Normal, AccessFlags.Read);
            builder.UseGlobalTexture(IndirectRadianceTexID);
            builder.UseGlobalTexture(NormalTexID);
            builder.SetRenderAttachment(spatialIndirectRadianceTemp, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(spatialIndirectRadianceTemp, IndirectRadianceTexID);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((SpatialFilterPassData data, RasterGraphContext ctx) =>
            {
                RenderSpatialFilterPass(data, ctx);
            });
        }

        using (var builder = renderGraph.AddRasterRenderPass<SpatialFilterPassData>("VoxelisX Indirect Spatial Filter Y", out var passData))
        {
            passData.width = width;
            passData.height = height;
            passData.enabled = true;
            passData.settings = indirectDenoisingSettings.separable15Tap;
            passData.RawIndirectRadiance = spatialIndirectRadianceTemp;
            passData.Normal = normal;
            passData.material = indirectMaterial;
            passData.passIndex = IndirectPipelinePassSpatialFilterY;

            builder.UseTexture(passData.RawIndirectRadiance, AccessFlags.Read);
            builder.UseTexture(passData.Normal, AccessFlags.Read);
            builder.UseGlobalTexture(IndirectRadianceTexID);
            builder.UseGlobalTexture(NormalTexID);
            builder.SetRenderAttachment(filteredIndirectRadiance, 0, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(filteredIndirectRadiance, IndirectRadianceTexID);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((SpatialFilterPassData data, RasterGraphContext ctx) =>
            {
                RenderSpatialFilterPass(data, ctx);
            });
        }

        return filteredIndirectRadiance;
    }

    private TextureHandle RecordATrousFilter(
        RenderGraph renderGraph,
        int width,
        int height,
        TextureHandle rawIndirectRadiance,
        TextureHandle spatialIndirectRadianceTemp,
        TextureHandle filteredIndirectRadiance,
        TextureHandle normal,
        TextureHandle currentDepthHistory)
    {
        Material aTrousMaterial = EnsureIndirectATrousFilterMaterial();
        if (aTrousMaterial == null)
        {
            return rawIndirectRadiance;
        }

        TextureHandle source = rawIndirectRadiance;
        TextureHandle destination = rawIndirectRadiance;
        int iterations = indirectDenoisingSettings.aTrous.iterations;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            destination = (iteration & 1) == 0 ? spatialIndirectRadianceTemp : filteredIndirectRadiance;

            using (var builder = renderGraph.AddRasterRenderPass<ATrousFilterPassData>($"VoxelisX Indirect A-Trous Filter {iteration + 1}", out var passData))
            {
                passData.width = width;
                passData.height = height;
                passData.stepWidth = 1 << iteration;
                passData.settings = indirectDenoisingSettings.aTrous;
                passData.SourceIndirectRadiance = source;
                passData.Normal = normal;
                passData.CurrentDepthHistory = currentDepthHistory;
                passData.material = aTrousMaterial;

                builder.UseTexture(passData.SourceIndirectRadiance, AccessFlags.Read);
                builder.UseTexture(passData.Normal, AccessFlags.Read);
                builder.UseTexture(passData.CurrentDepthHistory, AccessFlags.Read);
                builder.UseGlobalTexture(IndirectRadianceTexID);
                builder.UseGlobalTexture(NormalTexID);
                builder.UseGlobalTexture(CurrentDepthHistoryTexID);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(destination, IndirectRadianceTexID);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ATrousFilterPassData data, RasterGraphContext ctx) =>
                {
                    RenderATrousFilterPass(data, ctx);
                });
            }

            source = destination;
        }

        return destination;
    }

    private static PrevCameraState GetOrCreateCameraState(Camera camera, Matrix4x4 initialCameraMat)
    {
        if (!prevFrameState.TryGetValue(camera, out PrevCameraState state))
        {
            state = new PrevCameraState()
            {
                mat = initialCameraMat,
                frames = 0,
            };
            prevFrameState.Add(camera, state);
        }

        return state;
    }

    private Texture2D GetBlueNoiseTexture()
    {
        return blueNoiseTexture;
    }

    public void Dispose()
    {
        foreach (PrevCameraState state in prevFrameState.Values)
        {
            state.history.Dispose();
        }

        prevFrameState.Clear();
        CoreUtils.Destroy(indirectRadiancePipelineMaterial);
        indirectRadiancePipelineMaterial = null;
        CoreUtils.Destroy(indirectATrousFilterMaterial);
        indirectATrousFilterMaterial = null;
    }

    private Material EnsureIndirectRadiancePipelineMaterial()
    {
        if (indirectRadiancePipelineMaterial != null)
        {
            return indirectRadiancePipelineMaterial;
        }

        Shader shader = Shader.Find(IndirectRadiancePipelineShaderName);
        if (shader == null)
        {
            Debug.LogError($"Cannot find shader '{IndirectRadiancePipelineShaderName}'.");
            return null;
        }

        indirectRadiancePipelineMaterial = CoreUtils.CreateEngineMaterial(shader);
        return indirectRadiancePipelineMaterial;
    }

    private Material EnsureIndirectATrousFilterMaterial()
    {
        if (indirectATrousFilterMaterial != null)
        {
            return indirectATrousFilterMaterial;
        }

        Shader shader = Shader.Find(IndirectATrousFilterShaderName);
        if (shader == null)
        {
            Debug.LogError($"Cannot find shader '{IndirectATrousFilterShaderName}'.");
            return null;
        }

        indirectATrousFilterMaterial = CoreUtils.CreateEngineMaterial(shader);
        return indirectATrousFilterMaterial;
    }

    static void RenderRayTracingPass(RayTracingPassData data, UnsafeGraphContext context)
    {
        var prevCameraView = data.cameraState.mat;
        data.cameraState.mat = data.cameraMat;
        if (data.historyValid)
        {
            data.cameraState.frames = Mathf.Min(data.cameraState.frames + 1, data.avgFrames);
        }
        else
        {
            data.cameraState.frames = 0;
        }

        var natcmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        natcmd.SetRayTracingShaderPass(data.voxShaderRT, "VoxelisX");
        if (data.brickMaterial != null)
        {
            if (data.blueNoiseTexture != null)
            {
                data.brickMaterial.SetTexture(BlueNoiseTextureName, data.blueNoiseTexture);
            }

            data.brickMaterial.SetInt("g_FrameIndex", Time.frameCount);
        }

        context.cmd.BuildRayTracingAccelerationStructure(data.voxAS);

        context.cmd.SetRayTracingAccelerationStructure(data.voxShaderRT, "g_AccelStruct", data.voxAS);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DirectRadianceTarget", data.DirectRadiance);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "IndirectRadianceTarget", data.IndirectRadiance);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "AlbedoTarget", data.Albedo);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "NormalTarget", data.Normal);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DepthTarget", data.Depth);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "MotionVectorTarget", data.MotionVector);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "g_CurrentDepthHistory", data.CurrentDepthHistory);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "g_CurrentNormalHistory", data.CurrentNormalHistory);

        // This works but feels like a super dirty hack ...
        // TODO: FIXME: Maybe PR to PBSky repo so we can use the texture properly ... idk
        Texture tex = Shader.GetGlobalTexture(Shader.PropertyToID("_GlossyEnvironmentCubeMap"));
        // Sometimes we won't be able to obtain the texture
        if (tex == null)
        {
            // Dummy pure white sky
            Debug.LogWarning("Cannot obtain current sky texture. Using dummy pure white sky instead.");
            tex = new Cubemap(1, GraphicsFormat.B8G8R8A8_SRGB, TextureCreationFlags.None);
        }
        natcmd.SetRayTracingTextureParam(data.voxShaderRT, "g_Sky", tex);

        // context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_PrevCameraToWorld", prevCameraView.inverse);
        // context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_PrevViewProjection", prevCameraView);

        if (data.blueNoiseTexture != null)
        {
            natcmd.SetRayTracingTextureParam(data.voxShaderRT, BlueNoiseTextureName, data.blueNoiseTexture);
        }

        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_FrameIndex", Time.frameCount);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_ConvergenceStep", data.cameraState.frames);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BounceCountOpaque", data.bounceCountOpaque);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_BounceCountTransparent", data.bounceCountTransparent);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_spp", data.samplesPerPixel);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_Zoom", Mathf.Tan(Mathf.Deg2Rad * data.fov * 0.5f)); // TODO: Replace this to use camera projection matrix instead
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_AspectRatio", data.width / (float)data.height);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_CameraWorldPosition", data.cameraWorldPosition);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentWorldToCamera", data.cameraMat);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_CurrentCameraToWorld", data.cameraToWorldMat);
        context.cmd.SetRayTracingMatrixParam(data.voxShaderRT, "g_PrevWorldToCamera", prevCameraView);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_mainLightColor",
            data.mainLight.color.linear * data.mainLight.intensity * (data.mainLight.useColorTemperature
                ? Mathf.CorrelatedColorTemperatureToRGB(data.mainLight.colorTemperature)
                : Color.white));

        context.cmd.DispatchRays(data.voxShaderRT, "MainRayGenShader", data.width, data.height, 1, null);
    }

    private static void SetIndirectPipelineFrameParams(Material material, int width, int height)
    {
        material.SetVector("_VoxelisXFrameSize", new Vector4(
            width,
            height,
            width > 0 ? 1.0f / width : 0.0f,
            height > 0 ? 1.0f / height : 0.0f));
    }

    static void RenderSpatialFilterPass(SpatialFilterPassData data, RasterGraphContext context)
    {
        SetIndirectPipelineFrameParams(data.material, data.width, data.height);
        data.material.SetInt("_SpatialFilterEnabled", data.enabled ? 1 : 0);
        data.material.SetInt(SeparableFilterRadiusID, data.settings.radius);
        data.material.SetFloat(SeparableFilterDistanceSigmaID, data.settings.distanceSigma);

        Blitter.BlitTexture(context.cmd, data.RawIndirectRadiance, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
    }

    static void RenderATrousFilterPass(ATrousFilterPassData data, RasterGraphContext context)
    {
        SetIndirectPipelineFrameParams(data.material, data.width, data.height);
        data.material.SetInt(ATrousStepWidthID, data.stepWidth);
        data.material.SetInt(ATrousUseFaceHashID, data.settings.useFaceHash ? 1 : 0);
        data.material.SetFloat(ATrousNormalPowerID, data.settings.normalPower);
        data.material.SetFloat(ATrousDepthSigmaID, data.settings.depthSigma);
        data.material.SetFloat(ATrousRelativeDepthSigmaID, data.settings.relativeDepthSigma);
        data.material.SetFloat(ATrousRadianceSigmaID, data.settings.radianceSigma);

        Blitter.BlitTexture(context.cmd, data.SourceIndirectRadiance, new Vector4(1, 1, 0, 0), data.material, IndirectATrousFilterPass);
    }

    static void RenderTemporalAccumulationPass(TemporalAccumulationPassData data, RasterGraphContext context)
    {
        SetIndirectPipelineFrameParams(data.material, data.width, data.height);
        data.material.SetInt("_IndirectRadianceHistoryValid", data.historyValid ? 1 : 0);
        data.material.SetInt("_TemporalRadianceEnabled", data.enableTemporalRadiance ? 1 : 0);
        data.material.SetInt("_TemporalRadianceBilinearHistory", data.temporalRadianceBilinearHistory ? 1 : 0);
        data.material.SetInt("_TemporalRadianceDepthRejectionEnabled", data.temporalRadianceDepthRejection ? 1 : 0);
        data.material.SetInt("_TemporalRadianceNormalRejectionEnabled", data.temporalRadianceNormalRejection ? 1 : 0);
        data.material.SetFloat("_TemporalRadianceCurrentFrameMinWeight", data.temporalRadianceCurrentFrameMinWeight);
        data.material.SetFloat("_TemporalRadianceDepthTolerance", data.temporalRadianceDepthTolerance);
        data.material.SetFloat("_TemporalRadianceRelativeDepthTolerance", data.temporalRadianceRelativeDepthTolerance);
        data.material.SetFloat("_TemporalRadianceNormalThreshold", data.temporalRadianceNormalThreshold);
        data.material.SetFloat("_ConvergenceStep", data.convergenceStep);
        data.material.SetTexture(PreviousIndirectRadianceHistoryTexID, data.PreviousIndirectRadianceHistory);
        data.material.SetTexture(PreviousDepthHistoryTexID, data.PreviousDepthHistory);
        data.material.SetTexture(PreviousNormalHistoryTexID, data.PreviousNormalHistory);

        Blitter.BlitTexture(context.cmd, data.FilteredIndirectRadiance, new Vector4(1, 1, 0, 0), data.material, IndirectPipelinePassTemporalAccumulation);
        data.history.SwapAfterWrite();
    }

    static void RenderCompositePass(CompositePassData data, RasterGraphContext context)
    {
        SetIndirectPipelineFrameParams(data.material, data.width, data.height);

        Blitter.BlitTexture(context.cmd, data.DirectRadiance, new Vector4(1, 1, 0, 0), data.material, IndirectPipelinePassComposite);
    }

    static void RenderCopyToCameraPass(CopyToCameraPassData data, UnsafeGraphContext context)
    {
        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

        cmd.SetRenderTarget(data.GI_dest, data.Depth_dest);
        data.copyDSMat.SetInt("_DebugView", (int)data.debugView);
        Blitter.BlitTexture(cmd, data.Color, new Vector4(1, 1, 0, 0), data.copyDSMat, 0);
    }
}
