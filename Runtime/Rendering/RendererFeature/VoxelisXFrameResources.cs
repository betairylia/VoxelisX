using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The per-frame handoff between the three VoxelisX render stages.
/// </summary>
/// <remarks>
/// Stored in the frame's <see cref="ContextContainer"/> so the stages stay decoupled: the G-buffer
/// stage produces, the denoise stage refines and composites, and the present stage only chooses what
/// to display. Consumers must check <see cref="IsValid"/> before touching any handle — the G-buffer
/// stage bails out (leaving every handle null) when the scene has no renderer or a shader is missing.
/// </remarks>
public class VoxelisXFrameResources : ContextItem
{
    /// <summary>True once the G-buffer stage has published a complete set of handles for this frame.</summary>
    public bool IsValid;

    // --- Produced by VoxelisXGBufferPass ---

    /// <summary>All radiance that needs no denoising: primary emission, sky on miss. ARGBFloat.</summary>
    public TextureHandle DeterministicRadiance;
    /// <summary>Stochastic diffuse-path radiance (albedo-demodulated), hit distance in .a. ARGBHalf.</summary>
    public TextureHandle StochasticDiffuse;
    /// <summary>Stochastic specular-path radiance (incl. transparent reflection/refraction), hit distance in .a. ARGBHalf.</summary>
    public TextureHandle StochasticSpecular;
    /// <summary>Surface albedo, used to modulate stochastic radiance at composite time.</summary>
    public TextureHandle Albedo;
    /// <summary>Octahedral-encoded surface normal in .xy ([0,1] range), voxel face hash in .z, linear roughness in .w.</summary>
    public TextureHandle Normal;
    /// <summary>Linear view depth in world units. The present stage converts it to raw depth for SV_Depth.</summary>
    public TextureHandle Depth;
    /// <summary>2.5D motion vectors, NRD convention: .xy = previousUV - currentUV, .z = viewZprev - viewZ, .a reserved. ARGBFloat.</summary>
    public TextureHandle MotionVector;
    /// <summary>This frame's linear depth, persisted for next frame's temporal rejection.</summary>
    public TextureHandle CurrentDepthHistory;
    /// <summary>This frame's normal, persisted for next frame's temporal rejection.</summary>
    public TextureHandle CurrentNormalHistory;

    // --- Produced by VoxelisXDenoisePass ---

    /// <summary>Combined diffuse+specular stochastic radiance — the legacy denoise chain's input signal.</summary>
    public TextureHandle RawIndirectRadiance;
    /// <summary>Indirect radiance after the spatial filter. Aliases <see cref="RawIndirectRadiance"/> when the filter is disabled.</summary>
    public TextureHandle FilteredIndirectRadiance;
    /// <summary>Indirect radiance after temporal accumulation.</summary>
    public TextureHandle AccumulatedIndirectRadiance;
    /// <summary>Final composited color, what the present stage shows in <see cref="VoxelisXDebugView.Regular"/>.</summary>
    public TextureHandle Color;

    /// <summary>
    /// Temporal state for the camera being rendered. Resolved once by the G-buffer stage so that
    /// stages cannot disagree about which history buffer is "previous" this frame.
    /// </summary>
    internal VoxelisXCameraHistory History;

    public override void Reset()
    {
        IsValid = false;

        DeterministicRadiance = TextureHandle.nullHandle;
        StochasticDiffuse = TextureHandle.nullHandle;
        StochasticSpecular = TextureHandle.nullHandle;
        Albedo = TextureHandle.nullHandle;
        Normal = TextureHandle.nullHandle;
        Depth = TextureHandle.nullHandle;
        MotionVector = TextureHandle.nullHandle;
        CurrentDepthHistory = TextureHandle.nullHandle;
        CurrentNormalHistory = TextureHandle.nullHandle;

        RawIndirectRadiance = TextureHandle.nullHandle;
        FilteredIndirectRadiance = TextureHandle.nullHandle;
        AccumulatedIndirectRadiance = TextureHandle.nullHandle;
        Color = TextureHandle.nullHandle;

        History = null;
    }

    /// <summary>
    /// Resolves the debug view to the texture that should be blitted to the camera.
    /// Falls back to <see cref="Color"/> for any view whose source is unavailable.
    /// </summary>
    public TextureHandle SelectDebugSource(VoxelisXDebugView view)
    {
        switch (view)
        {
            case VoxelisXDebugView.MotionVector: return MotionVector;
            case VoxelisXDebugView.Albedo: return Albedo;
            case VoxelisXDebugView.Normal: return Normal;
            case VoxelisXDebugView.Depth: return Depth;
            case VoxelisXDebugView.DeterministicRadiance: return DeterministicRadiance;
            case VoxelisXDebugView.IndirectRadianceRaw: return RawIndirectRadiance;
            case VoxelisXDebugView.IndirectRadianceFiltered: return FilteredIndirectRadiance;
            case VoxelisXDebugView.IndirectRadianceAccumulated: return AccumulatedIndirectRadiance;
            case VoxelisXDebugView.StochasticDiffuse: return StochasticDiffuse;
            case VoxelisXDebugView.StochasticSpecular: return StochasticSpecular;
            case VoxelisXDebugView.Regular:
            default: return Color;
        }
    }

    // --- Shared descriptors -------------------------------------------------
    // All VoxelisX targets are screen-sized and share one base shape, so the stages agree on
    // format/size without having to pass descriptors between each other.

    /// <summary>Base descriptor every VoxelisX target derives from: screen-sized, UAV-capable, no depth.</summary>
    public static RenderTextureDescriptor BaseDescriptor(UniversalCameraData cameraData)
    {
        RenderTextureDescriptor desc = new RenderTextureDescriptor(
            cameraData.cameraTargetDescriptor.width,
            cameraData.cameraTargetDescriptor.height,
            RenderTextureFormat.ARGBFloat);

        // The ray tracing stage writes these as UAVs; harmless for the raster stages.
        desc.enableRandomWrite = true;
        return desc;
    }

    /// <summary>Descriptor for a target of the given format, matching the base shape.</summary>
    public static RenderTextureDescriptor DescriptorWithFormat(UniversalCameraData cameraData, RenderTextureFormat format)
    {
        RenderTextureDescriptor desc = BaseDescriptor(cameraData);
        desc.colorFormat = format;
        return desc;
    }
}
