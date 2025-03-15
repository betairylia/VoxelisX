using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using Voxelis.Utils;

public class VoxelisXRendererFeature : ScriptableRendererFeature
{
    [SerializeField] private RayTracingShader tracer;
    [SerializeField] private Material postProcessMaterialFlip;
    [SerializeField] private int maximumAverageFrames;

    private VoxelisXRenderPass _voxelisXRenderPass;
    private VoxelisXPostProcessPass _voxelisXPostPass;
    
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
