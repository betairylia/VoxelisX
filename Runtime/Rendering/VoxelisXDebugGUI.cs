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

    [Header("References")]
    [SerializeField] private VoxelisXRenderer rayTracingRenderer;
    [SerializeField] private VoxelMeshRendererComponent meshRendererComponent;

    [Header("Debug Visualization")]
    [SerializeField] private bool showSectorBorders = false;
    [SerializeField] private bool showBrickBorders = false;
    [SerializeField] private Color sectorBorderColor = new Color(1f, 0f, 0f, 0.5f);
    [SerializeField] private Color brickBorderColor = new Color(0f, 1f, 0f, 0.3f);

    private bool isVisible;
    private GUIStyle boxStyle;
    private GUIStyle buttonStyle;
    private GUIStyle labelStyle;
    private bool stylesInitialized = false;

    // Runtime line rendering
    private Material lineMaterial;

    // Rendering mode detection
    private enum RenderingMode
    {
        Unknown,
        RayTraced,
        MeshingFallback,
        Hybrid
    }

    private void Start()
    {
        isVisible = showOnStart;

        // Auto-find references if not set
        if (rayTracingRenderer == null)
        {
            rayTracingRenderer = VoxelisXRenderer.instance;
        }

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
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
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
                            totalBricks += Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS;
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
        if (GUILayout.Button(showSectorBorders ? "Hide Sector Borders" : "Show Sector Borders", buttonStyle))
        {
            showSectorBorders = !showSectorBorders;
        }

        // Brick borders toggle
        if (GUILayout.Button(showBrickBorders ? "Hide Brick Borders" : "Show Brick Borders", buttonStyle))
        {
            showBrickBorders = !showBrickBorders;
        }

        GUILayout.Space(10);

        // Performance info
        GUILayout.Label("<b>Performance:</b>", labelStyle);
        GUILayout.Label($"  FPS: {(1.0f / Time.deltaTime):F1}", labelStyle);
        GUILayout.Label($"  Frame Time: {(Time.deltaTime * 1000f):F2} ms", labelStyle);

        GUILayout.Space(5);
        GUILayout.Label($"<i>Press {toggleKey} to toggle</i>", labelStyle);

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

    private unsafe void OnPostRender()
    {
        if (!isVisible) return;
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
                    for (int brickIdxAbs = 0; brickIdxAbs < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS; brickIdxAbs++)
                    {
                        short brickIdx = sector.brickIdx[brickIdxAbs];
                        if (brickIdx != Sector.BRICKID_EMPTY)
                        {
                            // Calculate brick position within sector
                            int brickX = brickIdxAbs % Sector.SIZE_IN_BRICKS;
                            int brickY = brickIdxAbs / (Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS);
                            int brickZ = (brickIdxAbs / Sector.SIZE_IN_BRICKS) % Sector.SIZE_IN_BRICKS;

                            float3 brickLocalPos = new float3(brickX, brickY, brickZ) * Sector.SIZE_IN_BLOCKS;
                            float3 brickInSectorPos = sectorLocalPos + brickLocalPos;

                            DrawWireBox(brickInSectorPos, new float3(Sector.SIZE_IN_BLOCKS), brickBorderColor, entityMatrix);
                        }
                    }
                }
            }
        }

        GL.End();
        GL.PopMatrix();
    }

    private void DrawWireBox(float3 center, float3 size, Color color, Matrix4x4 transform)
    {
        GL.Color(color);

        float3 halfSize = size * 0.5f;

        // Define 8 corners in local space
        float3 bottomFrontLeft = center + new float3(-halfSize.x, -halfSize.y, -halfSize.z);
        float3 bottomFrontRight = center + new float3(halfSize.x, -halfSize.y, -halfSize.z);
        float3 bottomBackLeft = center + new float3(-halfSize.x, -halfSize.y, halfSize.z);
        float3 bottomBackRight = center + new float3(halfSize.x, -halfSize.y, halfSize.z);
        float3 topFrontLeft = center + new float3(-halfSize.x, halfSize.y, -halfSize.z);
        float3 topFrontRight = center + new float3(halfSize.x, halfSize.y, -halfSize.z);
        float3 topBackLeft = center + new float3(-halfSize.x, halfSize.y, halfSize.z);
        float3 topBackRight = center + new float3(halfSize.x, halfSize.y, halfSize.z);

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
