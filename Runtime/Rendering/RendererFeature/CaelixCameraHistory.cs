using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Temporal state that survives between frames for one camera: the double-buffered history targets
/// and the previous view matrix.
/// </summary>
/// <remarks>
/// Owned by the registry rather than by a pass, because two stages need the same view of it within a
/// frame — the G-buffer stage writes this frame's depth/normal history, the denoise stage reads last
/// frame's. <see cref="BeginFrame"/> is called exactly once per camera per frame by the G-buffer
/// stage while recording, so "previous" and "current" cannot drift apart between stages.
/// </remarks>
public sealed class CaelixCameraHistory
{
    private static readonly Dictionary<Camera, CaelixCameraHistory> s_Cameras =
        new Dictionary<Camera, CaelixCameraHistory>();

    private RTHandle indirectRadianceA;
    private RTHandle indirectRadianceB;
    private RTHandle depthA;
    private RTHandle depthB;
    private RTHandle normalA;
    private RTHandle normalB;
    private bool useAAsPrevious = true;

    /// <summary>
    /// Frame this history was last advanced on, so <see cref="BeginFrame"/> and
    /// <see cref="EndFrame"/> are idempotent within a frame. Two Caelix features enabled on the
    /// same renderer would otherwise each roll the view matrix and flip the double buffer, leaving
    /// "previous" pointing at the frame before last and reprojection permanently one frame stale.
    /// </summary>
    private int lastAdvancedFrame = -1;
    private bool flippedThisFrame;

    /// <summary>False on the first frame and after any reallocation, i.e. history must not be sampled.</summary>
    public bool IsValid { get; private set; }

    /// <summary>View matrix of the frame before this one, used for reprojection.</summary>
    public Matrix4x4 PreviousViewMatrix { get; private set; }

    /// <summary>View matrix of the frame currently being recorded.</summary>
    public Matrix4x4 ViewMatrix { get; private set; }

    /// <summary>How many frames have accumulated without a history reset, clamped to the accumulation window.</summary>
    public int ConvergedFrames { get; private set; }

    public RTHandle PreviousIndirectRadiance => useAAsPrevious ? indirectRadianceA : indirectRadianceB;
    public RTHandle CurrentIndirectRadiance => useAAsPrevious ? indirectRadianceB : indirectRadianceA;
    public RTHandle PreviousDepth => useAAsPrevious ? depthA : depthB;
    public RTHandle CurrentDepth => useAAsPrevious ? depthB : depthA;
    public RTHandle PreviousNormal => useAAsPrevious ? normalA : normalB;
    public RTHandle CurrentNormal => useAAsPrevious ? normalB : normalA;

    /// <summary>
    /// Fetches (creating on first use) the history for <paramref name="camera"/> and advances it into
    /// the frame being recorded: reallocates on resize, rolls the view matrix forward, and updates the
    /// convergence counter.
    /// </summary>
    public static CaelixCameraHistory BeginFrame(
        Camera camera, Matrix4x4 viewMatrix, int width, int height, int maximumAverageFrames)
    {
        if (!s_Cameras.TryGetValue(camera, out CaelixCameraHistory history))
        {
            history = new CaelixCameraHistory
            {
                ViewMatrix = viewMatrix,
                PreviousViewMatrix = viewMatrix
            };
            s_Cameras.Add(camera, history);
        }

        history.EnsureAllocated(width, height);

        if (history.lastAdvancedFrame == Time.frameCount)
        {
            return history;
        }

        history.lastAdvancedFrame = Time.frameCount;
        history.flippedThisFrame = false;

        history.PreviousViewMatrix = history.ViewMatrix;
        history.ViewMatrix = viewMatrix;
        history.ConvergedFrames = history.IsValid
            ? Mathf.Min(history.ConvergedFrames + 1, Mathf.Max(1, maximumAverageFrames))
            : 0;

        return history;
    }

    /// <summary>
    /// Flips which half of each double buffer is "previous". Called once per frame by the denoise
    /// stage after it has recorded its write into <see cref="CurrentIndirectRadiance"/>.
    /// </summary>
    public void EndFrame()
    {
        if (flippedThisFrame)
        {
            return;
        }

        flippedThisFrame = true;
        useAAsPrevious = !useAAsPrevious;
        IsValid = true;
    }

    /// <summary>Releases every camera's history. Call when the renderer feature is torn down.</summary>
    public static void ReleaseAll()
    {
        foreach (CaelixCameraHistory history in s_Cameras.Values)
        {
            history.Release();
        }

        s_Cameras.Clear();
    }

    private void EnsureAllocated(int width, int height)
    {
        RenderTextureDescriptor indirectDesc = MakeDescriptor(width, height, RenderTextureFormat.ARGBHalf);
        RenderTextureDescriptor depthDesc = MakeDescriptor(width, height, RenderTextureFormat.RFloat);
        RenderTextureDescriptor normalDesc = MakeDescriptor(width, height, RenderTextureFormat.ARGBHalf);

        bool reallocated = false;
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref indirectRadianceA, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Caelix_HistoryIndirectRadiance_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref indirectRadianceB, indirectDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Caelix_HistoryIndirectRadiance_B");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref depthA, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryDepth_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref depthB, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryDepth_B");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref normalA, normalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryNormal_A");
        reallocated |= RenderingUtils.ReAllocateIfNeeded(
            ref normalB, normalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "Caelix_HistoryNormal_B");

        if (reallocated)
        {
            IsValid = false;
            useAAsPrevious = true;
        }
    }

    private void Release()
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

    private static RenderTextureDescriptor MakeDescriptor(int width, int height, RenderTextureFormat format)
    {
        return new RenderTextureDescriptor(width, height, format, 0)
        {
            enableRandomWrite = true,
            msaaSamples = 1,
            volumeDepth = 1,
            useMipMap = false,
            autoGenerateMips = false,
            sRGB = false
        };
    }
}
