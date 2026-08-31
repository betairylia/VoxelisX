using UnityEditor;
using UnityEngine;
using Caelix.Authoring;

namespace Caelix.Authoring.EditorTools
{
    internal abstract class VoxelEntityAuthoringToolEditor : UnityEditor.Editor
    {
        protected VoxelEntityAuthoringTool AuthoringTool => target as VoxelEntityAuthoringTool;

        protected void DrawAuthoringStatusAndActions()
        {
            VoxelEntityAuthoringTool tool = AuthoringTool;
            if (tool == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Play Mode Authoring", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Generated Voxels", tool.GeneratedVoxelCount.ToString());

                if (!tool.CanWriteVoxels)
                {
                    EditorGUILayout.HelpBox(
                        "This tool's Voxel Entity has no live voxel storage, so generating and baking are disabled. " +
                        "Make sure the Voxel Entity component is enabled, then exit and re-enter Play Mode.",
                        MessageType.Warning);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Rebuild Now"))
                    {
                        tool.RebuildNow();
                        SceneView.RepaintAll();
                    }

                    bool armed = VoxelAuthoringPlayModePersistence.HasPendingSnapshot(tool);
                    if (GUILayout.Button(armed ? "Cancel Keep Changes" : "Keep Changes After Play"))
                    {
                        if (armed)
                        {
                            VoxelAuthoringPlayModePersistence.Cancel(tool);
                        }
                        else
                        {
                            VoxelAuthoringPlayModePersistence.Capture(tool);
                        }
                    }
                }

                EditorGUILayout.HelpBox(
                    "Keep Changes After Play marks this tool. Caelix reads its live values as Play Mode exits, " +
                    "applies them to the scene object, and saves the scene, so later edits are included automatically.",
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

            using (new EditorGUI.DisabledScope(!Application.isPlaying || !tool.CanWriteVoxels))
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
