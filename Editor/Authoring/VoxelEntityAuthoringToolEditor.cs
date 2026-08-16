using UnityEditor;
using UnityEngine;
using Voxelis.Authoring;

namespace Voxelis.Authoring.EditorTools
{
    internal abstract class VoxelEntityAuthoringToolEditor : UnityEditor.Editor
    {
        protected VoxelEntityAuthoringTool AuthoringTool => (VoxelEntityAuthoringTool)target;

        protected void DrawAuthoringStatusAndActions()
        {
            VoxelEntityAuthoringTool tool = AuthoringTool;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Play Mode Authoring", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Generated Voxels", tool.GeneratedVoxelCount.ToString());

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Rebuild Now"))
                    {
                        tool.RebuildNow();
                        SceneView.RepaintAll();
                    }

                    string keepLabel = VoxelAuthoringPlayModePersistence.HasPendingSnapshot(tool)
                        ? "Update Saved Snapshot"
                        : "Keep Changes After Play";
                    if (GUILayout.Button(keepLabel))
                    {
                        VoxelAuthoringPlayModePersistence.Capture(tool);
                    }
                }

                EditorGUILayout.HelpBox(
                    "Keep Changes After Play captures the current controls. The tool restores them and saves the scene after Play Mode exits. Click it again after later edits.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The Scene view shows a preview in Edit Mode. Enter Play Mode to generate collision voxels, bake, or keep Play Mode changes.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Bake as New Saveable Entity"))
                {
                    VoxelEntity baked = tool.BakeCopy();
                    if (baked != null)
                    {
                        Selection.activeGameObject = baked.gameObject;
                        Debug.Log($"Baked '{tool.name}' as saveable entity '{baked.name}'.", baked);
                    }
                }

                using (new EditorGUI.DisabledScope(tool.BakeTarget == null))
                {
                    if (GUILayout.Button("Bake Into Target Entity"))
                    {
                        bool confirmed = EditorUtility.DisplayDialog(
                            "Bake Into Target Entity",
                            "This replaces existing target blocks at generated voxel coordinates.",
                            "Bake",
                            "Cancel");

                        if (confirmed)
                        {
                            if (tool.BakeIntoTarget(out string error, out int count))
                            {
                                Debug.Log($"Baked {count} voxels from '{tool.name}' into '{tool.BakeTarget.name}'.", tool.BakeTarget);
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Cannot Bake", error, "OK");
                            }
                        }
                    }
                }
            }

            Vector3 scale = tool.transform.lossyScale;
            if ((scale - Vector3.one).sqrMagnitude > 0.000001f)
            {
                EditorGUILayout.HelpBox(
                    "Voxel entities must have a world scale of (1, 1, 1). Reset this transform and any scaled parent.",
                    MessageType.Error);
            }
        }
    }
}
