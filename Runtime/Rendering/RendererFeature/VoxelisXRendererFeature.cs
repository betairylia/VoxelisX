using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// URP renderer feature that injects ray-traced voxel rendering into the pipeline.
/// </summary>
/// <remarks>
/// This type is only the settings surface and the wiring; the work is split across three stages that
/// hand off through <see cref="VoxelisXFrameResources"/> in the frame's context container:
/// <list type="number">
/// <item><see cref="VoxelisXGBufferPass"/> — DXR trace producing the VoxelisX G-buffer;</item>
/// <item><see cref="VoxelisXDenoisePass"/> — spatial filter, temporal accumulation, composite;</item>
/// <item><see cref="VoxelisXPresentPass"/> — debug view selection and copy to the camera target.</item>
/// </list>
/// All three are enqueued at the same <see cref="RenderPassEvent"/>; URP sorts the queue stably, so
/// enqueue order here is the execution order.
/// <para>
/// Serialized field names are load-bearing — they are what renderer assets store. Renaming one drops
/// the authored value back to its default, so use <c>[FormerlySerializedAs]</c> if one must change.
/// </para>
/// </remarks>
public class VoxelisXRendererFeature : ScriptableRendererFeature
{
    /// <summary>Ray tracing shader used for voxel rendering.</summary>
    [SerializeField] private RayTracingShader tracer;

    [SerializeField] private Shader indirectPipelineShader, indirectATrousShader;

    /// <summary>Material used to copy VoxelisX output onto the camera target and write depth.</summary>
    [SerializeField] private Material postProcessMaterialFlip;

    /// <summary>
    /// Length of the temporal accumulation window. Higher converges smoother but ghosts more on
    /// moving objects.
    /// </summary>
    [SerializeField, Min(1)] private int maximumAverageFrames = 120;

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
    [SerializeField, Tooltip("Which VoxelisX buffer to display. Debug views reuse buffers the frame already produced, so they cost nothing extra to render.")]
    private VoxelisXDebugView debugView = VoxelisXDebugView.Regular;

    private VoxelisXGBufferPass gbufferPass;
    private VoxelisXDenoisePass denoisePass;
    private VoxelisXPresentPass presentPass;

    // Materials are owned here rather than by the passes: Create() re-runs on every inspector edit,
    // so per-pass ownership leaked a material set each time a slider moved.
    private Material indirectMaterial;
    private Material[] aTrousMaterials;
    private Material flipMaterial;

    /// <summary>Cached scene renderer. Resolved lazily because the feature can be created before the scene loads.</summary>
    private VoxelisXRenderer voxelisXRenderer;

    public override void Create()
    {
        DestroyMaterials();
        CreateMaterials();

        gbufferPass = new VoxelisXGBufferPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };
        denoisePass = new VoxelisXDenoisePass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };
        presentPass = new VoxelisXPresentPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };

        denoisePass.Setup(indirectMaterial, aTrousMaterials);
        presentPass.Setup(flipMaterial);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (gbufferPass == null || !TryResolveVoxelisXRenderer())
        {
            return;
        }

        gbufferPass.ConfigureSettings(voxelisXRenderer, tracer, blueNoiseTexture, BuildTraceSettings());
        denoisePass.ConfigureSettings(indirectDenoising, BuildTemporalSettings());
        presentPass.ConfigureSettings(debugView);

        if (!gbufferPass.IsReady)
        {
            return;
        }

        // Same RenderPassEvent for all three; URP's queue sort is stable, so this is the run order.
        renderer.EnqueuePass(gbufferPass);
        renderer.EnqueuePass(denoisePass);
        renderer.EnqueuePass(presentPass);
    }

    protected override void Dispose(bool disposing)
    {
        VoxelisXCameraHistory.ReleaseAll();
        DestroyMaterials();

        gbufferPass = null;
        denoisePass = null;
        presentPass = null;
    }

    private VoxelisXTraceSettings BuildTraceSettings()
    {
        return new VoxelisXTraceSettings
        {
            buildAccelerationStructure = buildAccelerationStructure,
            bounceCountOpaque = bounceCountOpaque,
            bounceCountTransparent = bounceCountTransparent,
            samplesPerPixel = samplesPerPixel,
            enableSkySun = enableSkySun,
            sunDiskRadiusRadians = sunDiskRadiusRadians,
            sunFlareRadiusRadians = sunFlareRadiusRadians,
            maximumAverageFrames = maximumAverageFrames
        };
    }

    private VoxelisXTemporalRadianceSettings BuildTemporalSettings()
    {
        return new VoxelisXTemporalRadianceSettings
        {
            enabled = enableTemporalRadiance,
            currentFrameMinWeight = temporalRadianceCurrentFrameMinWeight,
            depthRejection = temporalRadianceDepthRejection,
            depthTolerance = temporalRadianceDepthTolerance,
            relativeDepthTolerance = temporalRadianceRelativeDepthTolerance,
            normalRejection = temporalRadianceNormalRejection,
            normalThreshold = temporalRadianceNormalThreshold,
            bilinearHistory = temporalRadianceBilinearHistory,
            maximumAverageFrames = maximumAverageFrames
        };
    }

    private bool TryResolveVoxelisXRenderer()
    {
        // Deliberately not VoxelisXRenderer.instance: MonoSingleton spawns a temporary GameObject
        // when none exists, which would litter the scene from a renderer feature.
        if (voxelisXRenderer == null)
        {
            voxelisXRenderer = FindFirstObjectByType<VoxelisXRenderer>();
        }

        return voxelisXRenderer != null;
    }

    private void CreateMaterials()
    {
        if (indirectPipelineShader != null)
        {
            indirectMaterial = CoreUtils.CreateEngineMaterial(indirectPipelineShader);
        }

        if (indirectATrousShader != null)
        {
            aTrousMaterials = new Material[VoxelisXATrousFilterSettings.MaxIterations];
            for (int i = 0; i < aTrousMaterials.Length; i++)
            {
                aTrousMaterials[i] = CoreUtils.CreateEngineMaterial(indirectATrousShader);
            }
        }

        if (postProcessMaterialFlip != null)
        {
            // Instance rather than the asset: the present stage writes _DebugView every frame, which
            // would otherwise dirty the material asset on disk in the editor.
            flipMaterial = new Material(postProcessMaterialFlip)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }
    }

    private void DestroyMaterials()
    {
        CoreUtils.Destroy(indirectMaterial);
        indirectMaterial = null;

        if (aTrousMaterials != null)
        {
            for (int i = 0; i < aTrousMaterials.Length; i++)
            {
                CoreUtils.Destroy(aTrousMaterials[i]);
            }

            aTrousMaterials = null;
        }

        CoreUtils.Destroy(flipMaterial);
        flipMaterial = null;
    }
}
