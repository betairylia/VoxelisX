using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Splines;
using UnityEngine;
using Voxelis.Authoring;

namespace Voxelis.Authoring.EditorTools
{
    [CustomEditor(typeof(VoxelSplineAuthoring))]
    internal sealed class VoxelSplineAuthoringEditor : VoxelEntityAuthoringToolEditor
    {
        private readonly List<List<Vector3>> previewPaths = new();

        private SerializedProperty mode;
        private SerializedProperty radius;
        private SerializedProperty pixelPerfect;
        private SerializedProperty parallelSeparation;
        private SerializedProperty cornerMiterLimit;
        private SerializedProperty sampleSpacing;
        private SerializedProperty block;
        private SerializedProperty autoRebuild;
        private SerializedProperty bakeTarget;

        private void OnEnable()
        {
            mode = serializedObject.FindProperty("mode");
            radius = serializedObject.FindProperty("radius");
            pixelPerfect = serializedObject.FindProperty("pixelPerfect");
            parallelSeparation = serializedObject.FindProperty("parallelSeparation");
            cornerMiterLimit = serializedObject.FindProperty("cornerMiterLimit");
            sampleSpacing = serializedObject.FindProperty("sampleSpacing");
            block = serializedObject.FindProperty("block");
            autoRebuild = serializedObject.FindProperty("autoRebuild");
            bakeTarget = serializedObject.FindProperty("bakeTarget");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Voxel Spline", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(mode);
            EditorGUILayout.PropertyField(radius, new GUIContent("Radius (Voxels)"));
            EditorGUILayout.PropertyField(
                pixelPerfect,
                new GUIContent("Pixel Perfect", "Snap the pencil path to one digital voxel line before applying its radius."));

            if ((VoxelSplineMode)mode.enumValueIndex == VoxelSplineMode.ParallelRails)
            {
                EditorGUILayout.PropertyField(parallelSeparation, new GUIContent("Rail Center Spacing"));
                EditorGUILayout.PropertyField(
                    cornerMiterLimit,
                    new GUIContent("Corner Miter Limit", "Long sharp-corner miters fall back to a connected bevel."));
            }

            EditorGUILayout.PropertyField(
                sampleSpacing,
                new GUIContent("Curve Sample Spacing", "Lower values follow tight curves more closely but cost more to rebuild."));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Block Material", EditorStyles.boldLabel);
            DrawBlockMaterial(block);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(autoRebuild, new GUIContent("Auto Rebuild in Play Mode"));
            EditorGUILayout.PropertyField(bakeTarget, new GUIContent("Bake Target"));

            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            if (changed)
            {
                AuthoringTool.RequestRebuild();
                EditorUtility.SetDirty(AuthoringTool);
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Edit Spline in Scene View"))
            {
                GameObject gameObject = AuthoringTool.gameObject;
                Selection.activeGameObject = gameObject;
                EditorApplication.delayCall += () => ActivateSplineTools(gameObject);
            }

            DrawAuthoringStatusAndActions();
        }

        private void OnSceneGUI()
        {
            var tool = (VoxelSplineAuthoring)target;
            tool.BuildPreviewPaths(previewPaths);

            Matrix4x4 previousMatrix = Handles.matrix;
            Color previousColor = Handles.color;
            Handles.matrix = tool.transform.localToWorldMatrix;
            Handles.color = DecodeColor(tool.Block);

            float width = Mathf.Clamp(2f + tool.Radius, 2f, 8f);
            for (int pathIndex = 0; pathIndex < previewPaths.Count; pathIndex++)
            {
                List<Vector3> path = previewPaths[pathIndex];
                if (path.Count > 1)
                {
                    Handles.DrawAAPolyLine(width, path.ToArray());
                }
            }

            Handles.matrix = previousMatrix;
            Handles.color = previousColor;
        }

        private static void DrawBlockMaterial(SerializedProperty blockProperty)
        {
            SerializedProperty data = blockProperty.FindPropertyRelative("data");
            ushort packed = (ushort)data.intValue;

            Color color = DecodeColor(new Block(packed));
            bool emission = (packed & 1) != 0;

            EditorGUI.BeginChangeCheck();
            color = EditorGUILayout.ColorField(new GUIContent("Color"), color, true, false, false);
            emission = EditorGUILayout.Toggle(new GUIContent("Emission"), emission);
            if (EditorGUI.EndChangeCheck())
            {
                int red = Mathf.Clamp(Mathf.RoundToInt(color.r * 31f), 0, 31);
                int green = Mathf.Clamp(Mathf.RoundToInt(color.g * 31f), 0, 31);
                int blue = Mathf.Clamp(Mathf.RoundToInt(color.b * 31f), 0, 31);
                data.intValue = new Block(red, green, blue, emission).data;
                packed = (ushort)data.intValue;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField(new GUIContent("Packed Block Value"), packed);
            }

            if (packed == 0)
            {
                EditorGUILayout.HelpBox("Block value 0 is empty. The spline will generate no visible voxels.", MessageType.Warning);
            }
        }

        private static Color DecodeColor(Block block)
        {
            int packed = block.data;
            return new Color(
                ((packed >> 11) & 31) / 31f,
                ((packed >> 6) & 31) / 31f,
                ((packed >> 1) & 31) / 31f,
                1f);
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
