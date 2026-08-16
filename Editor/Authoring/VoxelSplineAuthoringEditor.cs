using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Splines;
using UnityEngine;
using Voxelis.Authoring;

namespace Voxelis.Authoring.EditorTools
{
    [CustomEditor(typeof(VoxelSplineAuthoring))]
    [CanEditMultipleObjects]
    internal sealed class VoxelSplineAuthoringEditor : VoxelEntityAuthoringToolEditor
    {
        /// <summary>Above this many preview cells the Scene view draws the smooth path only.</summary>
        private const int MaxPreviewCells = 20000;

        private static readonly Color PathColor = new(0.35f, 0.85f, 1f, 1f);
        private static readonly Color VoxelPreviewColor = new(1f, 0.75f, 0.2f, 0.9f);

        private readonly List<List<Vector3>> previewPaths = new();
        private readonly List<Vector3Int> previewCells = new();
        private readonly List<Vector3> previewCellCenters = new();

        private SerializedProperty mode;
        private SerializedProperty radius;
        private SerializedProperty pixelPerfect;
        private SerializedProperty parallelSeparation;
        private SerializedProperty snapRailsToGrid;
        private SerializedProperty cornerMiterLimit;
        private SerializedProperty sampleSpacing;
        private SerializedProperty blockId;
        private SerializedProperty autoRebuild;
        private SerializedProperty bakeTarget;

        private void OnEnable()
        {
            mode = serializedObject.FindProperty("mode");
            radius = serializedObject.FindProperty("radius");
            pixelPerfect = serializedObject.FindProperty("pixelPerfect");
            parallelSeparation = serializedObject.FindProperty("parallelSeparation");
            snapRailsToGrid = serializedObject.FindProperty("snapRailsToGrid");
            cornerMiterLimit = serializedObject.FindProperty("cornerMiterLimit");
            sampleSpacing = serializedObject.FindProperty("sampleSpacing");
            blockId = serializedObject.FindProperty("blockId");
            autoRebuild = serializedObject.FindProperty("autoRebuild");
            bakeTarget = serializedObject.FindProperty("bakeTarget");
        }

        public override void OnInspectorGUI()
        {
            if (target == null)
            {
                return;
            }

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Voxel Spline", EditorStyles.boldLabel);
            DrawProperty(mode, null);
            DrawProperty(radius, new GUIContent("Radius (Voxels)"));
            DrawProperty(
                pixelPerfect,
                new GUIContent("Pixel Perfect", "Snap the path to one clean digital voxel line, with no doubled corner cells, before applying the radius."));

            if (mode != null && !mode.hasMultipleDifferentValues &&
                (VoxelSplineMode)mode.enumValueIndex == VoxelSplineMode.ParallelRails)
            {
                DrawProperty(parallelSeparation, new GUIContent("Rail Center Spacing"));
                DrawProperty(
                    snapRailsToGrid,
                    new GUIContent(
                        "Snap Rails To Grid",
                        "Place both rails exactly on voxel centers, mirrored around the spline. Voxel centers sit on half-integers, so the spacing snaps to an odd number of voxels."));

                var splineTool = target as VoxelSplineAuthoring;
                if (splineTool != null && !parallelSeparation.hasMultipleDifferentValues)
                {
                    EditorGUILayout.LabelField(
                        " ",
                        $"Effective spacing: {splineTool.EffectiveRailSeparation:0.##} voxels",
                        EditorStyles.miniLabel);
                }

                DrawProperty(
                    cornerMiterLimit,
                    new GUIContent("Corner Miter Limit", "Long sharp-corner miters fall back to a connected bevel."));
            }

            DrawProperty(
                sampleSpacing,
                new GUIContent("Curve Sample Spacing", "Lower values follow tight curves more closely but cost more to rebuild."));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Block Material", EditorStyles.boldLabel);
            DrawBlockId(blockId);

            EditorGUILayout.Space();
            DrawProperty(autoRebuild, new GUIContent("Auto Rebuild in Play Mode"));
            DrawProperty(bakeTarget, new GUIContent("Bake Target"));

            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] is VoxelEntityAuthoringTool tool)
                    {
                        tool.RequestRebuild();
                        EditorUtility.SetDirty(tool);
                    }
                }

                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Edit Spline in Scene View"))
            {
                GameObject gameObject = AuthoringTool != null ? AuthoringTool.gameObject : null;
                if (gameObject != null)
                {
                    Selection.activeGameObject = gameObject;
                    EditorApplication.delayCall += () => ActivateSplineTools(gameObject);
                }
            }

            DrawAuthoringStatusAndActions();
        }

        private void OnSceneGUI()
        {
            var tool = target as VoxelSplineAuthoring;
            if (tool == null)
            {
                return;
            }

            tool.BuildPreviewPaths(previewPaths);
            if (previewPaths.Count == 0)
            {
                return;
            }

            Matrix4x4 previousMatrix = Handles.matrix;
            Color previousColor = Handles.color;
            Handles.matrix = tool.transform.localToWorldMatrix;

            float width = Mathf.Clamp(2f + tool.Radius, 2f, 8f);
            for (int pathIndex = 0; pathIndex < previewPaths.Count; pathIndex++)
            {
                List<Vector3> path = previewPaths[pathIndex];
                if (path == null || path.Count < 2)
                {
                    continue;
                }

                Handles.color = PathColor;
                Handles.DrawAAPolyLine(width, path.ToArray());

                // The voxels land on cell centers, so the smooth path alone hides the half-voxel
                // staircase the user actually gets. Draw it whenever it is cheap enough.
                if (!tool.PixelPerfect)
                {
                    continue;
                }

                VoxelSplineVoxelizer.BuildPixelPerfectLine(path, previewCells);
                if (previewCells.Count < 2 || previewCells.Count > MaxPreviewCells)
                {
                    continue;
                }

                previewCellCenters.Clear();
                for (int cellIndex = 0; cellIndex < previewCells.Count; cellIndex++)
                {
                    previewCellCenters.Add(VoxelSplineVoxelizer.CellCenter(previewCells[cellIndex]));
                }

                Handles.color = VoxelPreviewColor;
                Handles.DrawAAPolyLine(2f, previewCellCenters.ToArray());
            }

            Handles.matrix = previousMatrix;
            Handles.color = previousColor;
        }

        private static void DrawProperty(SerializedProperty property, GUIContent label)
        {
            if (property == null)
            {
                return;
            }

            if (label == null)
            {
                EditorGUILayout.PropertyField(property);
                return;
            }

            EditorGUILayout.PropertyField(property, label);
        }

        private static void DrawBlockId(SerializedProperty property)
        {
            if (property == null)
            {
                return;
            }

            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int value = EditorGUILayout.DelayedIntField(
                new GUIContent(
                    "Block ID",
                    "Raw 16-bit block id written into every generated voxel. Bit 15 (0x8000) marks an opaque block; 0 is empty."),
                property.intValue);
            if (EditorGUI.EndChangeCheck())
            {
                property.intValue = Mathf.Clamp(value, 0, ushort.MaxValue);
            }

            EditorGUI.showMixedValue = false;

            if (!property.hasMultipleDifferentValues)
            {
                EditorGUILayout.LabelField(" ", $"0x{property.intValue:X4}", EditorStyles.miniLabel);

                if (property.intValue == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Block ID 0 is empty. The spline will generate no visible voxels.",
                        MessageType.Warning);
                }
            }
        }

        private static void ActivateSplineTools(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            Selection.activeGameObject = gameObject;
            try
            {
                ToolManager.SetActiveContext<SplineToolContext>();
                ToolManager.SetActiveTool<SplineMoveTool>();
                SceneView.lastActiveSceneView?.Focus();
            }
            catch (System.InvalidOperationException exception)
            {
                Debug.LogWarning(
                    $"Unity could not activate the spline context automatically. Select Spline in the Scene Tools overlay. {exception.Message}",
                    gameObject);
            }
        }
    }
}
