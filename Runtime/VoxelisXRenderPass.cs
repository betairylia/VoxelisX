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

public class VoxelisXRenderPass : ScriptableRenderPass
{
    public RayTracingShader rayTracingShader = null;
    private VoxelisXRenderer voxelisX;

    private int maximumAverageFrames;
    
    // Post process
    private Material flip;

    internal struct PrevCameraState
    {
        internal Matrix4x4 mat;
        internal int frames;
    }
    
    private static Dictionary<Camera, PrevCameraState> prevFrameState = new Dictionary<Camera, PrevCameraState>();
    private int EnvMapID = Shader.PropertyToID("_GlossyEnvironmentCubeMap");

    internal class PassData
    {
        internal uint width;
        internal uint height;
        internal uint viewCount;
        internal float fov;

        internal Matrix4x4 cameraMat;
        internal Camera camera;

        internal uint frameId;
        internal int avgFrames;

        internal Light mainLight;
        
        internal TextureHandle GI;
        internal TextureHandle Albedo;
        internal TextureHandle Normal;
        internal TextureHandle ShadowMask;
        internal TextureHandle Depth;

        internal TextureHandle Albedo_dest;
        internal TextureHandle Normal_dest;
        internal TextureHandle GI_dest;
        internal TextureHandle Depth_dest;

        internal Material copyDSMat;

        internal RayTracingShader voxShaderRT;
        internal RayTracingAccelerationStructure voxAS;
    }

    public class VoxelisXPassData : ContextItem
    {
        public TextureHandle Color;
        
        public override void Reset()
        {
            Color = TextureHandle.nullHandle; 
        }
    }

    internal class PostPassData
    {
        internal TextureHandle RT;
        internal TextureHandle Depth;
        internal bool needsFlip;
    }

    public VoxelisXRenderPass(int maxAvgFrames = 120)
    {
        maximumAverageFrames = maxAvgFrames;
    }
    
    public void InitVoxelisX(RayTracingShader rtShader, VoxelisXRenderer vox, Material flip)
    {
        rayTracingShader = rtShader;
        voxelisX = vox;
        this.flip = flip;
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
        rtDesc.enableRandomWrite = true;

        TextureHandle RT = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, rtDesc, "VoxelisX_outColor", false);
        
        RenderTextureDescriptor albedoDesc = rtDesc;
        albedoDesc.colorFormat = RenderTextureFormat.Default;
        TextureHandle RT_Albedo = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, albedoDesc, "VoxelisX_outAlbedo", false);
        
        RenderTextureDescriptor normalDesc = rtDesc;
        albedoDesc.colorFormat = RenderTextureFormat.RGB111110Float;
        TextureHandle RT_Normal = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, normalDesc, "VoxelisX_outNormal", false);

        RenderTextureDescriptor depthDesc = rtDesc;
        depthDesc.colorFormat = RenderTextureFormat.RGFloat;
        
        TextureHandle RT_Depth = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, depthDesc, "VoxelisX_NormalDepth", false);
        
        // TextureHandle RT_TAA = UniversalRenderer.CreateRenderGraphTexture(
        //     renderGraph, rtDesc, "VoxelisX_accumulateColor", false);

        string passName = "VoxelisX DXR";

        using (var builder = renderGraph.AddUnsafePass<PassData>(passName, out var passData))
        {
            passData.width = (uint)cameraData.scaledWidth;
            passData.height = (uint)cameraData.scaledHeight;
            passData.fov = 60.0f;
            
            passData.GI = RT;
            passData.Albedo = RT_Albedo;
            passData.Normal = RT_Normal;
            passData.Depth = RT_Depth;

            passData.copyDSMat = flip;
            
            passData.GI_dest = frameData.Get<UniversalResourceData>().activeColorTexture;
            passData.Depth_dest = frameData.Get<UniversalResourceData>().activeDepthTexture;

            passData.mainLight = frameData.Get<UniversalLightData>().visibleLights[frameData.Get<UniversalLightData>().mainLightIndex].light;
            
            passData.cameraMat = cameraData.GetViewMatrix();
            passData.camera = cameraData.camera;
            passData.avgFrames = maximumAverageFrames;

            passData.frameId = voxelisX.frameId;

            passData.voxShaderRT = rayTracingShader;
            passData.voxAS = voxelisX.voxelScene;

            var vxPassData = frameData.GetOrCreate<VoxelisXPassData>();
            vxPassData.Color = RT;

            // Set output
            builder.UseTexture(RT, AccessFlags.ReadWrite);
            builder.UseTexture(RT_Albedo, AccessFlags.Write);
            builder.UseTexture(RT_Normal, AccessFlags.Write);
            builder.UseTexture(passData.Depth, AccessFlags.Write);
            builder.UseTexture(passData.Depth_dest, AccessFlags.Write);
            
            // RenderGraph does not see this properly set so ...
            // builder.UseGlobalTexture(EnvMapID);
            
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
            {
                RenderPass(data, ctx);
            });
        }

        // using (var builder = renderGraph.AddRasterRenderPass<PostPassData>("VoxelisX post-process", out var passData))
        // {
        //     passData.RT = RT;
        //     // passData.Depth = RT_Depth;
        //     
        //     builder.UseTexture(RT, AccessFlags.Read);
        //     
        //     builder.AllowPassCulling(false);
        //     
        //     builder.SetRenderAttachment(frameData.Get<UniversalResourceData>().activeColorTexture, 0);
        //     
        //     builder.SetRenderFunc((PostPassData data, RasterGraphContext rgContext) =>
        //     {
        //         Blitter.BlitTexture(rgContext.cmd, data.RT, new Vector4(1, 1, 0, 0), 0, false);
        //     });
        // }
    }

    static void RenderPass(PassData data, UnsafeGraphContext context)
    {
        if (!prevFrameState.ContainsKey(data.camera))
        {
            prevFrameState.Add(data.camera, new PrevCameraState(){
                mat = data.cameraMat,
                frames = 0,
            });
        }

        var prevCameraView = prevFrameState[data.camera].mat;
        if (data.cameraMat != prevFrameState[data.camera].mat)
        {
            prevFrameState[data.camera] = new PrevCameraState()
            {
                mat = data.cameraMat,
                frames = 0
            };
        }
        else
        {
            prevFrameState[data.camera] = new PrevCameraState()
            {
                mat = data.cameraMat,
                frames = Mathf.Min(prevFrameState[data.camera].frames + 1, data.avgFrames),
            };
        }
        
        var natcmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        natcmd.SetRayTracingShaderPass(data.voxShaderRT, "VoxelisX");
        context.cmd.BuildRayTracingAccelerationStructure(data.voxAS);
        
        context.cmd.SetRayTracingAccelerationStructure(data.voxShaderRT, "g_AccelStruct", data.voxAS);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "RenderTarget", data.GI);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "AlbedoTarget", data.Albedo);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "NormalTarget", data.Normal);
        context.cmd.SetRayTracingTextureParam(data.voxShaderRT, "DepthTarget", data.Depth);

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
        
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_FrameIndex", Time.frameCount);
        context.cmd.SetRayTracingIntParam(data.voxShaderRT, "g_ConvergenceStep", prevFrameState[data.camera].frames);
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_Zoom", Mathf.Tan(Mathf.Deg2Rad * data.fov * 0.5f)); // TODO: Replace this to use camera projection matrix instead
        context.cmd.SetRayTracingFloatParam(data.voxShaderRT, "g_AspectRatio", data.width / (float)data.height);
        context.cmd.SetRayTracingVectorParam(data.voxShaderRT, "g_mainLightColor",
            data.mainLight.color.linear * data.mainLight.intensity * (data.mainLight.useColorTemperature
                ? Mathf.CorrelatedColorTemperatureToRGB(data.mainLight.colorTemperature)
                : Color.white));
        
        context.cmd.DispatchRays(data.voxShaderRT, "MainRayGenShader", data.width, data.height, 1, null);
        
        // Copy buffers
        // Blitter.BlitTexture(
        //     CommandBufferHelpers.GetNativeCommandBuffer(context.cmd),
        //     );
        // CommandBufferHelpers.GetNativeCommandBuffer(context.cmd).Blit(
        //     data.Depth,
        //     data.Depth_dest
        //     );
        
        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        
        cmd.SetRenderTarget(data.GI_dest, data.Depth_dest);
        data.copyDSMat.SetTexture("_DepthTex", data.Depth);
        Blitter.BlitTexture(cmd, data.GI, new Vector4(1, 1, 0, 0), data.copyDSMat, 0);
    }
}
