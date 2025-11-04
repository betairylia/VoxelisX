using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Voxelis.Utils
{
    /// <summary>
    /// Post-processing render pass for VoxelisX (currently unimplemented).
    /// Intended for temporal anti-aliasing (TAA) and other post-processing effects.
    /// </summary>
    /// <remarks>
    /// This pass is currently not finished and will throw NotImplementedException when executed.
    /// TODO: Implement TAA accumulation and other post-processing effects.
    /// </remarks>
    public class VoxelisXPostProcessPass : ScriptableRenderPass
    {
        private int maximumAverageFrames;

        /// <summary>
        /// Material for temporal anti-aliasing post-processing.
        /// </summary>
        public Material VoxelisX_TAA;

        /// <summary>
        /// Data passed to the render graph for post-processing.
        /// </summary>
        internal class PassData
        {
            internal TextureHandle Color_Current;
            internal TextureHandle Color_Accu;

            internal Material taaMat;
        }

        /// <summary>
        /// Constructs the post-process pass with the specified maximum accumulation frames.
        /// </summary>
        /// <param name="maxAvgFrames">Maximum frames for TAA accumulation (default: 120).</param>
        public VoxelisXPostProcessPass(int maxAvgFrames = 120)
        {
            maximumAverageFrames = maxAvgFrames;
        }

        /// <summary>
        /// Records the post-process pass into the render graph.
        /// </summary>
        /// <param name="renderGraph">The render graph to record into.</param>
        /// <param name="frameData">Frame context data.</param>
        /// <exception cref="NotImplementedException">This pass is not yet implemented.</exception>
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
            // rtDesc.enableRandomWrite = true;

            TextureHandle RT_Acc = UniversalRenderer.CreateRenderGraphTexture(
                renderGraph, rtDesc, "VoxelisX_TAAacc", false);

            string passName = "VoxelisX Post-process";

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData))
            {
                passData.Color_Current = frameData.Get<VoxelisXRenderPass.VoxelisXPassData>().Color;
                passData.Color_Accu = RT_Acc;

                passData.taaMat = VoxelisX_TAA;
                
                builder.UseTexture(passData.Color_Current, AccessFlags.Read);
                builder.UseTexture(passData.Color_Accu, AccessFlags.ReadWrite);
                
                builder.AllowPassCulling(false);
                builder.SetRenderAttachment(frameData.Get<UniversalResourceData>().activeColorTexture, 0);
                
                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    data.taaMat.SetTexture("_Acc", data.Color_Accu);
                    // TODO: This is not finished. Later please work on this.
                    throw new NotImplementedException();
                    Blitter.BlitTexture(ctx.cmd, data.Color_Current, new Vector4(1, 1, 0, 0), data.taaMat, 0);
                });
            }
        }
    }
}