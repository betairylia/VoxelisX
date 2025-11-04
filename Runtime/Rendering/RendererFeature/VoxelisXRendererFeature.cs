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
    /// <summary>
    /// Ray tracing shader used for voxel rendering.
    /// </summary>
    [SerializeField] private RayTracingShader tracer;

    /// <summary>
    /// Material used for post-processing, including depth buffer copying and flip operations.
    /// </summary>
    [SerializeField] private Material postProcessMaterialFlip;

    /// <summary>
    /// Maximum number of frames to average for temporal anti-aliasing (TAA).
    /// Higher values produce smoother results but increase ghosting on moving objects.
    /// </summary>
    [SerializeField] private int maximumAverageFrames;

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

        _voxelisXRenderPass.InitVoxelisX(tracer, vox, postProcessMaterialFlip);
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

        renderer.EnqueuePass(_voxelisXRenderPass);

        // _voxelisXPostPass.ConfigureInput(ScriptableRenderPassInput.Motion);
        // renderer.EnqueuePass(_voxelisXPostPass);
    }
}
