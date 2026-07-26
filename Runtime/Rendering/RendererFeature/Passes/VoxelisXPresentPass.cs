using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Stage 3 of 3: chooses which VoxelisX buffer to display and copies it onto the camera target.
/// </summary>
/// <remarks>
/// The only stage that touches the camera's colour and depth attachments. It performs no filtering
/// of its own — switching <see cref="VoxelisXDebugView"/> only changes which handle from
/// <see cref="VoxelisXFrameResources"/> is used as the blit source and how the flip shader decodes
/// it, so a debug view costs nothing extra to render. Camera depth always comes from the G-buffer
/// depth target regardless of the view, so scene geometry keeps depth-testing correctly.
/// </remarks>
public class VoxelisXPresentPass : ScriptableRenderPass
{
    private Material flipMaterial;
    private VoxelisXDebugView debugView = VoxelisXDebugView.Regular;

    internal class PassData
    {
        internal VoxelisXDebugView debugView;
        internal TextureHandle Source;
        internal TextureHandle ColorDest;
        internal TextureHandle DepthDest;
        internal Material flipMaterial;
    }

    /// <summary>Binds the flip material owned by the renderer feature. Called when the feature is created.</summary>
    public void Setup(Material flip)
    {
        flipMaterial = flip;
    }

    /// <summary>Pushes this frame's debug view. Called once per camera before enqueueing.</summary>
    public void ConfigureSettings(VoxelisXDebugView view)
    {
        debugView = view;
    }

    /// <summary>True when the stage has everything it needs to record.</summary>
    public bool IsReady => flipMaterial != null;

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        VoxelisXFrameResources resources = frameData.GetOrCreate<VoxelisXFrameResources>();
        if (!resources.IsValid || !IsReady)
        {
            return;
        }

        TextureHandle source = resources.SelectDebugSource(debugView);
        if (!source.IsValid())
        {
            source = resources.Color;
        }

        UniversalResourceData cameraResources = frameData.Get<UniversalResourceData>();

        using (var builder = renderGraph.AddUnsafePass<PassData>("VoxelisX Copy To Camera", out var passData))
        {
            passData.debugView = debugView;
            passData.Source = source;
            passData.ColorDest = cameraResources.activeColorTexture;
            passData.DepthDest = cameraResources.activeDepthTexture;
            passData.flipMaterial = flipMaterial;

            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.UseTexture(resources.Depth, AccessFlags.Read);
            builder.UseGlobalTexture(VoxelisXShaderIDs.DepthTex);
            builder.UseTexture(passData.ColorDest, AccessFlags.Write);
            builder.UseTexture(passData.DepthDest, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) => Execute(data, ctx));
        }
    }

    private static void Execute(PassData data, UnsafeGraphContext context)
    {
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

        cmd.SetRenderTarget(data.ColorDest, data.DepthDest);
        data.flipMaterial.SetInt(VoxelisXShaderIDs.DebugView, (int)data.debugView);
        Blitter.BlitTexture(cmd, data.Source, new Vector4(1, 1, 0, 0), data.flipMaterial, 0);
    }
}
