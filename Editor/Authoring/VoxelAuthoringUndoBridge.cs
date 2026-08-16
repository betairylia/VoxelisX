using UnityEditor;
using UnityEngine;
using Voxelis.Authoring;

namespace Voxelis.Authoring.EditorTools
{
    /// <summary>
    /// Rebuilds authoring tools after an undo or redo.
    /// </summary>
    /// <remarks>
    /// Undo restores serialized state (spline knots, tool settings) directly into the component
    /// without going through the property setters, and Unity's spline package does not guarantee a
    /// <c>Spline.Changed</c> callback for it. Without this bridge the Scene view preview and the
    /// generated voxels keep showing the pre-undo shape until the next unrelated edit.
    /// </remarks>
    [InitializeOnLoad]
    internal static class VoxelAuthoringUndoBridge
    {
        static VoxelAuthoringUndoBridge()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        private static void OnUndoRedoPerformed()
        {
            VoxelEntityAuthoringTool[] tools =
                Object.FindObjectsByType<VoxelEntityAuthoringTool>(FindObjectsInactive.Include);

            for (int i = 0; i < tools.Length; i++)
            {
                VoxelEntityAuthoringTool tool = tools[i];
                if (tool == null)
                {
                    continue;
                }

                tool.ConfigureOwnedComponents();
                tool.RequestRebuild();
            }

            if (tools.Length > 0)
            {
                SceneView.RepaintAll();
            }
        }
    }
}
