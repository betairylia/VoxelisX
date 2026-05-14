using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using Voxelis.Utils;

/// <summary>
/// Unity URP (Universal Render Pipeline) renderer feature that integrates VoxelisX ray tracing into the rendering pipeline.
/// </summary>
/// <remarks>
/// This feature creates and manages the VoxelisX render passes, injecting ray-traced voxel rendering
/// into Unity's URP rendering pipeline. It finds the VoxelisXRenderer in the scene and configures
/// render passes with the necessary shaders and materials.
/// </remarks>
public class VoxelisXRendererFeature : ScriptableRendererFeature
{
    public enum DebugView
    {
        Regular = 0,
        MotionVector = 1
    }

    /// <summary>
    /// Ray tracing shader used for voxel rendering.
    /// </summary>
    [SerializeField] private RayTracingShader tracer;

    [SerializeField] private Shader indirectPipelineShader, indirectATrousShader;

    /// <summary>
    /// Material used for post-processing, including depth buffer copying and flip operations.
    /// </summary>
    [SerializeField] private Material postProcessMaterialFlip;

    /// <summary>
    /// Maximum number of frames to average for temporal anti-aliasing (TAA).
    /// Higher values produce smoother results but increase ghosting on moving objects.
    /// </summary>
    [SerializeField] private int maximumAverageFrames;

    [Header("Ray Tracing")]
    [SerializeField, Tooltip("Build the VoxelisX ray tracing acceleration structure inside the render pass each frame. Disable only if the structure is built elsewhere before tracing.")]
    private bool buildAccelerationStructure = true;
    [SerializeField, Min(0)] private int bounceCountOpaque = 4;
    [SerializeField, Min(0)] private int bounceCountTransparent = 5;
    [SerializeField, Min(1)] private int samplesPerPixel = 1;
    [SerializeField, Tooltip("Optional 128x8192 R8G8 UInt spatiotemporal blue-noise texture bound as stbnTexture.")]
    private Texture2D blueNoiseTexture;

    [Header("Sky Sun")]
    [SerializeField] private bool enableSkySun = true;
    [SerializeField, Min(0.0f)] private float sunDiskRadiusRadians = 0.004363323f;
    [SerializeField, Min(0.0f)] private float sunFlareRadiusRadians = 0.03490659f;

    [Header("Indirect Denoising")]
    [SerializeField] private VoxelisXIndirectDenoisingSettings indirectDenoising = VoxelisXIndirectDenoisingSettings.Default;

    [Header("Temporal Radiance")]
    [SerializeField] private bool enableTemporalRadiance = true;
    [SerializeField, Range(0.0f, 1.0f)] private float temporalRadianceCurrentFrameMinWeight = 0.0f;
    [SerializeField] private bool temporalRadianceDepthRejection = true;
    [SerializeField, Min(0.0f)] private float temporalRadianceDepthTolerance = 0.05f;
    [SerializeField, Min(0.0f)] private float temporalRadianceRelativeDepthTolerance = 0.01f;
    [SerializeField] private bool temporalRadianceNormalRejection = true;
    [SerializeField, Range(-1.0f, 1.0f)] private float temporalRadianceNormalThreshold = 0.85f;
    [SerializeField] private bool temporalRadianceBilinearHistory = true;

    [Header("Debug")]
    [SerializeField] private DebugView debugView = DebugView.Regular;

    private VoxelisXRenderPass _voxelisXRenderPass;
    private VoxelisXPostProcessPass _voxelisXPostPass;

    /// <summary>
    /// Called when the renderer feature is created. Finds the VoxelisXRenderer and initializes render passes.
    /// </summary>
    public override void Create()
    {
        VoxelisXRenderer vox = GameObject.FindFirstObjectByType<VoxelisXRenderer>();
        Debug.Log(vox);
        if (vox == null)
        {
            return;
        }

        _voxelisXRenderPass = new VoxelisXRenderPass(maximumAverageFrames)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            // renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
        };

        _voxelisXPostPass = new VoxelisXPostProcessPass(maximumAverageFrames)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing,
        };
        // new CopyDepthPass()

        _voxelisXRenderPass.InitVoxelisX(
            tracer, vox, postProcessMaterialFlip, blueNoiseTexture, indirectPipelineShader, indirectATrousShader);
    }

    /// <summary>
    /// Enqueues the VoxelisX render passes into the rendering pipeline.
    /// </summary>
    /// <param name="renderer">The scriptable renderer.</param>
    /// <param name="renderingData">Current rendering data and configuration.</param>
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_voxelisXRenderPass == null)
        {
            return;
        }

        _voxelisXRenderPass.ConfigureRayTracingSettings(
            buildAccelerationStructure,
            bounceCountOpaque,
            bounceCountTransparent,
            samplesPerPixel);
        _voxelisXRenderPass.ConfigureSkySunSettings(enableSkySun, sunDiskRadiusRadians, sunFlareRadiusRadians);
        _voxelisXRenderPass.ConfigureBlueNoiseTexture(blueNoiseTexture);
        _voxelisXRenderPass.ConfigureIndirectDenoisingSettings(indirectDenoising);
        _voxelisXRenderPass.ConfigureTemporalRadianceSettings(
            enableTemporalRadiance,
            temporalRadianceCurrentFrameMinWeight,
            temporalRadianceDepthRejection,
            temporalRadianceDepthTolerance,
            temporalRadianceRelativeDepthTolerance,
            temporalRadianceNormalRejection,
            temporalRadianceNormalThreshold,
            temporalRadianceBilinearHistory);
        _voxelisXRenderPass.ConfigureDebugView(debugView);
        renderer.EnqueuePass(_voxelisXRenderPass);

        // _voxelisXPostPass.ConfigureInput(ScriptableRenderPassInput.Motion);
        // renderer.EnqueuePass(_voxelisXPostPass);
    }

    protected override void Dispose(bool disposing)
    {
        _voxelisXRenderPass?.Dispose();
    }
}
