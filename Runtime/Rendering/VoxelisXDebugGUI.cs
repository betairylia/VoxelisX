using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Voxelis;
using Voxelis.Rendering.Meshing;

/// <summary>
/// Debug GUI overlay for VoxelisX engine.
/// Displays rendering information and provides controls for debug visualization.
/// Toggle with P key.
/// </summary>
public class VoxelisXDebugGUI : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private bool showOnStart = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.P;
    [SerializeField] private KeyCode orbitToggleKey = KeyCode.O;

    [Header("References")]
    [SerializeField] private VoxelisXRenderer rayTracingRenderer;
    [SerializeField] private VoxelMeshRendererComponent meshRendererComponent;

    [Header("Debug Visualization")]
    [SerializeField] private bool showSectorBorders = false;
    [SerializeField] private bool showBrickBorders = false;
    [SerializeField] private Color sectorBorderColor = new Color(1f, 0f, 0f, 1.0f);
    [SerializeField] private Color brickBorderColor = new Color(0f, 1f, 0f, 1.0f);
    [SerializeField] private Color brickBorderColorDirty = new Color(0f, 1f, 1f, 1.0f);

    private const float FpsWindowSeconds = 30f;
    private const float PerformanceUpdateIntervalSeconds = 0.1f;
    private const int CurrentSamplesPerPixel = 1;
    private const int FpsGraphWidth = 320;
    private const int FpsGraphHeight = 76;

    private bool isVisible;
    private GUIStyle boxStyle;
    private GUIStyle buttonStyle;
    private GUIStyle labelStyle;
    private GUIStyle graphLabelStyle;
    private GUIStyle tooltipStyle;
    private bool stylesInitialized = false;

    // Runtime line rendering
    private Material lineMaterial;
    private Texture2D graphTexture;
    private Color[] graphPixels;
    private bool graphTextureDirty = true;

    private readonly Queue<FpsSample> fpsSamples = new Queue<FpsSample>();
    private float currentFps;
    private float currentFrameTimeMs;
    private float currentMraysPerSecond;
    private float fpsEma;
    private float fpsWindowMin;
    private float fpsWindowMax;
    private float performanceSampleElapsed;
    private int performanceSampleFrameCount;
    private float performanceSampleMinFps;
    private float performanceSampleMaxFps;
    private bool hasFpsStats;

    // Rendering mode detection
    private enum RenderingMode
    {
        Unknown,
        RayTraced,
        MeshingFallback,
        Hybrid
    }

    private struct FpsSample
    {
        public readonly float Time;
        public readonly float Fps;
        public readonly float MinFps;
        public readonly float MaxFps;

        public FpsSample(float time, float fps, float minFps, float maxFps)
        {
            Time = time;
            Fps = fps;
            MinFps = minFps;
            MaxFps = maxFps;
        }
    }

    private void Start()
    {
        isVisible = showOnStart;

        // Auto-find references if not set
        if (rayTracingRenderer == null)
        {
            rayTracingRenderer = VoxelisXRenderer.instance;
        }
        
        meshRendererComponent = FindObjectOfType<VoxelMeshRendererComponent>();

        // Create material for runtime line rendering
        CreateLineMaterial();
    }

    private void CreateLineMaterial()
    {
        if (lineMaterial == null)
        {
            // Create a simple unlit shader material for line rendering
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader);
            lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }
    }

    private void Update()
    {
        UpdatePerformanceStats();

        // Toggle visibility with P key
        if (Input.GetKeyDown(toggleKey))
        {
            isVisible = !isVisible;
        }
    }

    private void OnDestroy()
    {
        if (lineMaterial != null)
        {
            DestroyImmediate(lineMaterial);
        }

        if (graphTexture != null)
        {
            DestroyImmediate(graphTexture);
        }
    }

    private void InitializeStyles()
    {
        if (stylesInitialized) return;

        // Create semi-transparent background style
        boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.normal.background = MakeTexture(2, 2, new Color(0f, 0f, 0f, 0.7f));
        boxStyle.padding = new RectOffset(10, 10, 10, 10);
        boxStyle.margin = new RectOffset(5, 5, 5, 5);

        // Create button style
        buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.normal.textColor = Color.white;
        buttonStyle.fontSize = 12;
        buttonStyle.padding = new RectOffset(10, 10, 5, 5);

        // Create label style
        labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = Color.white;
        labelStyle.fontSize = 12;
        labelStyle.padding = new RectOffset(5, 5, 2, 2);
        labelStyle.richText = true;

        graphLabelStyle = new GUIStyle(labelStyle);
        graphLabelStyle.fontSize = 10;
        graphLabelStyle.padding = new RectOffset(2, 2, 0, 0);

        tooltipStyle = new GUIStyle(labelStyle);
        tooltipStyle.normal.background = MakeTexture(2, 2, new Color(0f, 0f, 0f, 0.9f));
        tooltipStyle.padding = new RectOffset(8, 8, 5, 5);
        tooltipStyle.wordWrap = true;

        stylesInitialized = true;
    }

    private Texture2D MakeTexture(int width, int height, Color color)
    {
        Color[] pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = color;
        }

        Texture2D texture = new Texture2D(width, height);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    private void UpdatePerformanceStats()
    {
        float deltaTime = Time.unscaledDeltaTime;
        if (deltaTime <= 0f)
        {
            return;
        }

        float instantFps = 1f / deltaTime;
        performanceSampleElapsed += deltaTime;
        performanceSampleFrameCount++;

        if (performanceSampleFrameCount == 1)
        {
            performanceSampleMinFps = instantFps;
            performanceSampleMaxFps = instantFps;
        }
        else
        {
            performanceSampleMinFps = Mathf.Min(performanceSampleMinFps, instantFps);
            performanceSampleMaxFps = Mathf.Max(performanceSampleMaxFps, instantFps);
        }

        float now = Time.unscaledTime;
        if (!hasFpsStats)
        {
            currentFps = instantFps;
            currentFrameTimeMs = deltaTime * 1000f;
            fpsEma = instantFps;
            fpsWindowMin = instantFps;
            fpsWindowMax = instantFps;
            currentMraysPerSecond = CalculateMraysPerSecond(currentFps);
            hasFpsStats = true;
        }
        else
        {
            float alpha = 1f - Mathf.Exp(-deltaTime / FpsWindowSeconds);
            fpsEma = Mathf.Lerp(fpsEma, instantFps, alpha);
        }

        if (performanceSampleElapsed < PerformanceUpdateIntervalSeconds)
        {
            return;
        }

        currentFps = performanceSampleFrameCount / performanceSampleElapsed;
        currentFrameTimeMs = 1000f / currentFps;
        currentMraysPerSecond = CalculateMraysPerSecond(currentFps);

        fpsSamples.Enqueue(new FpsSample(now, currentFps, performanceSampleMinFps, performanceSampleMaxFps));
        while (fpsSamples.Count > 0 && now - fpsSamples.Peek().Time > FpsWindowSeconds)
        {
            fpsSamples.Dequeue();
        }

        fpsWindowMin = performanceSampleMinFps;
        fpsWindowMax = performanceSampleMaxFps;
        foreach (FpsSample sample in fpsSamples)
        {
            fpsWindowMin = Mathf.Min(fpsWindowMin, sample.MinFps);
            fpsWindowMax = Mathf.Max(fpsWindowMax, sample.MaxFps);
        }

        performanceSampleElapsed = 0f;
        performanceSampleFrameCount = 0;
        graphTextureDirty = true;
    }

    private void OnGUI()
    {
        if (!isVisible) return;

        InitializeStyles();

        // Position in upper-left corner
        GUILayout.BeginArea(new Rect(10, 10, 350, Screen.height - 20));
        GUILayout.BeginVertical(boxStyle);

        // Title
        GUILayout.Label("<b>VoxelisX Debug Info</b>", labelStyle);
        GUILayout.Space(5);

        // Rendering Mode
        RenderingMode mode = DetectRenderingMode();
        string modeText = mode switch
        {
            RenderingMode.RayTraced => "<color=#00ff00>Ray Traced</color>",
            RenderingMode.MeshingFallback => "<color=#ffaa00>Meshing Fallback</color>",
            RenderingMode.Hybrid => "<color=#00aaff>Hybrid</color>",
            _ => "<color=#ff0000>Unknown</color>"
        };
        GUILayout.Label($"<b>Rendering Mode:</b> {modeText}", labelStyle);

        GUILayout.Space(10);

        // Ray Tracing Info
        if (rayTracingRenderer != null)
        {
            GUILayout.Label("<b>Ray Tracing:</b>", labelStyle);

            var world = VoxelisXCoreWorld.instance;
            if (world != null)
            {
                ulong hostMemory = 0;
                int totalSectors = 0;
                int totalBricks = 0;

                foreach (var entity in world.entities)
                {
                    if (entity != null)
                    {
                        hostMemory += entity.GetHostMemoryUsageKB();
                        totalSectors += entity.Sectors.Count;

                        // Count total bricks
                        foreach (var kvp in entity.Sectors)
                        {
                            ref Sector sector = ref kvp.Value.Get();
                            totalBricks += sector.NonEmptyBrickCount;
                        }
                    }
                }

                GUILayout.Label($"  Frame: {rayTracingRenderer.frameId}", labelStyle);
                GUILayout.Label($"  Instances: {rayTracingRenderer.instanceCount}", labelStyle);
                GUILayout.Label($"  Sectors: {totalSectors}", labelStyle);
                GUILayout.Label($"  Bricks: {totalBricks}", labelStyle);

                // Memory info
                GUILayout.Label($"  AS Size: {rayTracingRenderer.voxelScene.GetSize() / 1024 / 1024} MB", labelStyle);
                GUILayout.Label($"  Host RAM: {hostMemory / 1024} MB", labelStyle);
            }
        }

        GUILayout.Space(10);

        // Mesh Rendering Info
        if (meshRendererComponent != null && meshRendererComponent.MeshRenderer != null)
        {
            GUILayout.Label("<b>Mesh Rendering:</b>", labelStyle);
            GUILayout.Label($"  Entities: {meshRendererComponent.MeshRenderer.TrackedEntityCount}", labelStyle);
            GUILayout.Label($"  Sector Renderers: {meshRendererComponent.MeshRenderer.SectorRendererCount}", labelStyle);
        }

        GUILayout.Space(10);

        // Debug Visualization Controls
        GUILayout.Label("<b>Debug Visualization:</b>", labelStyle);

        // Sector borders toggle
        if (GUILayout.Button(new GUIContent(
                showSectorBorders ? "Hide Sector Borders" : "Show Sector Borders",
                "Toggle world-space outlines for loaded voxel sectors."), buttonStyle))
        {
            showSectorBorders = !showSectorBorders;
        }

        // Brick borders toggle
        if (GUILayout.Button(new GUIContent(
                showBrickBorders ? "Hide Brick Borders" : "Show Brick Borders",
                "Toggle brick outlines. Dirty bricks use the cyan debug color."), buttonStyle))
        {
            showBrickBorders = !showBrickBorders;
        }

        GUILayout.Space(10);

        // Performance info
        GUILayout.Label("<b>Performance:</b>", labelStyle);
        GUILayout.Label($"  FPS: {currentFps:F1}", labelStyle);
        GUILayout.Label($"  FPS ~30s Avg (EMA): {fpsEma:F1}", labelStyle);
        GUILayout.Label($"  FPS 30s [min,max]: [{fpsWindowMin:F1}, {fpsWindowMax:F1}]", labelStyle);
        GUILayout.Label($"  Frame Time: {currentFrameTimeMs:F2} ms", labelStyle);
        GUILayout.Label($"  Resolution: {Screen.width} x {Screen.height}", labelStyle);
        GUILayout.Label($"  MRays/sec: {currentMraysPerSecond:F2} (SPP {CurrentSamplesPerPixel})", labelStyle);
        DrawFpsGraph();

        GUILayout.Space(5);
        GUILayout.Label($"<i>Press {toggleKey} to toggle GUI, {orbitToggleKey} to toggle orbit</i>", labelStyle);

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private RenderingMode DetectRenderingMode()
    {
        // Match the logic in VoxelisXWorld.Tick (lines 123-124)
        bool hasRayTracing = rayTracingRenderer != null && rayTracingRenderer.enabled;
        bool hasMesh = meshRendererComponent != null && meshRendererComponent.enabled;

        if (hasRayTracing && hasMesh)
            return RenderingMode.Hybrid;
        else if (hasRayTracing)
            return RenderingMode.RayTraced;
        else if (hasMesh)
            return RenderingMode.MeshingFallback;
        else
            return RenderingMode.Unknown;
    }

    private float CalculateMraysPerSecond(float fps)
    {
        long pixelCount = (long)Screen.width * Screen.height;
        return fps * pixelCount * CurrentSamplesPerPixel / 1000000f;
    }

    private void DrawFpsGraph()
    {
        Rect graphRect = GUILayoutUtility.GetRect(FpsGraphWidth, FpsGraphHeight, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint || fpsSamples.Count == 0)
        {
            return;
        }

        if (graphTexture == null)
        {
            graphTexture = new Texture2D(FpsGraphWidth, FpsGraphHeight, TextureFormat.RGBA32, false);
            graphTexture.hideFlags = HideFlags.HideAndDontSave;
            graphTexture.wrapMode = TextureWrapMode.Clamp;
            graphTexture.filterMode = FilterMode.Bilinear;
            graphPixels = new Color[FpsGraphWidth * FpsGraphHeight];
            graphTextureDirty = true;
        }

        float graphMaxFps = Mathf.Max(30f, Mathf.Ceil(fpsWindowMax / 30f) * 30f);
        if (graphTextureDirty)
        {
            RebuildFpsGraphTexture(graphMaxFps);
            graphTextureDirty = false;
        }

        GUI.DrawTexture(graphRect, graphTexture);

        GUI.Label(new Rect(graphRect.x + 4f, graphRect.y + 2f, graphRect.width - 8f, 16f),
            $"FPS over {FpsWindowSeconds:F0}s", graphLabelStyle);

        TextAnchor previousAlignment = graphLabelStyle.alignment;
        graphLabelStyle.alignment = TextAnchor.UpperRight;
        GUI.Label(new Rect(graphRect.x + 4f, graphRect.y + 2f, graphRect.width - 8f, 16f),
            $"{graphMaxFps:F0}", graphLabelStyle);
        GUI.Label(new Rect(graphRect.x + 4f, graphRect.yMax - 16f, graphRect.width - 8f, 16f),
            "0", graphLabelStyle);
        graphLabelStyle.alignment = previousAlignment;
    }

    private void RebuildFpsGraphTexture(float graphMaxFps)
    {
        Color background = new Color(0.04f, 0.04f, 0.04f, 0.85f);
        Color grid = new Color(1f, 1f, 1f, 0.12f);
        Color line = new Color(0.2f, 0.85f, 1f, 1f);

        for (int i = 0; i < graphPixels.Length; i++)
        {
            graphPixels[i] = background;
        }

        DrawGraphGridLine(0.25f, grid);
        DrawGraphGridLine(0.5f, grid);
        DrawGraphGridLine(0.75f, grid);

        float now = Time.unscaledTime;
        bool hasPrevious = false;
        Vector2Int previous = Vector2Int.zero;

        foreach (FpsSample sample in fpsSamples)
        {
            float age = now - sample.Time;
            int x = FpsGraphWidth - 1 - Mathf.RoundToInt(Mathf.Clamp01(age / FpsWindowSeconds) * (FpsGraphWidth - 1));
            int y = Mathf.RoundToInt(Mathf.Clamp01(sample.Fps / graphMaxFps) * (FpsGraphHeight - 1));
            Vector2Int point = new Vector2Int(x, y);

            if (hasPrevious)
            {
                DrawGraphLine(previous, point, line);
            }

            previous = point;
            hasPrevious = true;
        }

        graphTexture.SetPixels(graphPixels);
        graphTexture.Apply(false);
    }

    private void DrawGraphGridLine(float normalizedHeight, Color color)
    {
        int y = Mathf.RoundToInt(Mathf.Clamp01(normalizedHeight) * (FpsGraphHeight - 1));
        for (int x = 0; x < FpsGraphWidth; x++)
        {
            graphPixels[y * FpsGraphWidth + x] = color;
        }
    }

    private void DrawGraphLine(Vector2Int from, Vector2Int to, Color color)
    {
        int x0 = from.x;
        int y0 = from.y;
        int x1 = to.x;
        int y1 = to.y;
        int dx = Mathf.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;

        while (true)
        {
            DrawGraphPoint(x0, y0, color);
            DrawGraphPoint(x0, y0 - 1, color);

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int error2 = 2 * error;
            if (error2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (error2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private void DrawGraphPoint(int x, int y, Color color)
    {
        if (x < 0 || x >= FpsGraphWidth || y < 0 || y >= FpsGraphHeight)
        {
            return;
        }

        graphPixels[y * FpsGraphWidth + x] = color;
    }

    private unsafe void OnRenderObject()
    {
        // if (!isVisible) return;
        if (!showSectorBorders && !showBrickBorders) return;

        var world = VoxelisXCoreWorld.instance;
        if (world == null) return;

        CreateLineMaterial();
        lineMaterial.SetPass(0);

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        // Draw sector and brick borders
        foreach (var entity in world.entities)
        {
            if (entity == null) continue;

            // Get entity's transform matrix (includes position, rotation, and scale)
            Matrix4x4 entityMatrix = entity.transform.localToWorldMatrix;

            foreach (var kvp in entity.Sectors)
            {
                int3 sectorPos = kvp.Key;
                ref Sector sector = ref kvp.Value.Get();

                // Calculate sector local position (in entity's local space)
                float3 sectorLocalPos = (float3)sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;

                // Draw sector borders
                if (showSectorBorders)
                {
                    DrawWireBox(sectorLocalPos, new float3(Sector.SECTOR_SIZE_IN_BLOCKS), sectorBorderColor, entityMatrix);
                }

                // Draw brick borders
                if (showBrickBorders)
                {
                    for (short brickIdxAbs = 0; brickIdxAbs < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS; brickIdxAbs++)
                    {
                        short brickIdx = sector.brickIdx[brickIdxAbs];
                        DirtyFlags requireUpdateFlags = (DirtyFlags)sector.brickRequireUpdateFlags[brickIdxAbs];
                        if (brickIdx != Sector.BRICKID_EMPTY || requireUpdateFlags > 0)
                        {
                            // Calculate brick position within sector
                            int3 brickPos = Sector.ToBrickPos(brickIdxAbs);
                            float3 brickLocalPos = new float3(brickPos) * Sector.SIZE_IN_BLOCKS;
                            float3 brickInSectorPos = sectorLocalPos + brickLocalPos;

                            Color colorToUse = (requireUpdateFlags & DirtyFlags.GeneralAutomata) > 0 ? brickBorderColorDirty : brickBorderColor;
                            
                            DrawWireBox(
                                brickInSectorPos, new float3(Sector.SIZE_IN_BLOCKS), colorToUse, entityMatrix);
                        }
                    }
                }
            }
        }

        GL.End();
        GL.PopMatrix();
    }

    private void DrawWireBox(float3 origin, float3 size, Color color, Matrix4x4 transform)
    {
        GL.Color(color);

        // Define 8 corners in local space
        float3 bottomFrontLeft = origin;
        float3 bottomFrontRight = origin + new float3(size.x, 0, 0);
        float3 bottomBackLeft = origin + new float3(0, 0, size.z);
        float3 bottomBackRight = origin + new float3(size.x, 0, size.z);
        float3 topFrontLeft = origin + new float3(0, size.y, 0);
        float3 topFrontRight = origin + new float3(size.x, size.y, 0);
        float3 topBackLeft = origin + new float3(0, size.y, size.z);
        float3 topBackRight = origin + size;

        // Transform all corners to world space
        Vector3 bfl = transform.MultiplyPoint3x4(bottomFrontLeft);
        Vector3 bfr = transform.MultiplyPoint3x4(bottomFrontRight);
        Vector3 bbl = transform.MultiplyPoint3x4(bottomBackLeft);
        Vector3 bbr = transform.MultiplyPoint3x4(bottomBackRight);
        Vector3 tfl = transform.MultiplyPoint3x4(topFrontLeft);
        Vector3 tfr = transform.MultiplyPoint3x4(topFrontRight);
        Vector3 tbl = transform.MultiplyPoint3x4(topBackLeft);
        Vector3 tbr = transform.MultiplyPoint3x4(topBackRight);

        // Bottom face
        GL.Vertex(bfl); GL.Vertex(bfr);
        GL.Vertex(bfr); GL.Vertex(bbr);
        GL.Vertex(bbr); GL.Vertex(bbl);
        GL.Vertex(bbl); GL.Vertex(bfl);

        // Top face
        GL.Vertex(tfl); GL.Vertex(tfr);
        GL.Vertex(tfr); GL.Vertex(tbr);
        GL.Vertex(tbr); GL.Vertex(tbl);
        GL.Vertex(tbl); GL.Vertex(tfl);

        // Vertical edges
        GL.Vertex(bfl); GL.Vertex(tfl);
        GL.Vertex(bfr); GL.Vertex(tfr);
        GL.Vertex(bbl); GL.Vertex(tbl);
        GL.Vertex(bbr); GL.Vertex(tbr);
    }
}
