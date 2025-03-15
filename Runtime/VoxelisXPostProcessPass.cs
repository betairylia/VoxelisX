using System;
using Unity.Android.Gradle.Manifest;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Voxelis.Utils
{
    public class VoxelisXPostProcessPass : ScriptableRenderPass
    {
        private int maximumAverageFrames;
        public Material VoxelisX_TAA;

        internal class PassData
        {
            internal TextureHandle Color_Current;
            internal TextureHandle Color_Accu;

            internal Material taaMat;
        }

        public VoxelisXPostProcessPass(int maxAvgFrames = 120)
        {
            maximumAverageFrames = maxAvgFrames;
        }

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