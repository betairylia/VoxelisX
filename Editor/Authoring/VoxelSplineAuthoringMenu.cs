using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Splines;
using UnityEngine;
using Voxelis.Authoring;

namespace Voxelis.Authoring.EditorTools
{
    internal static class VoxelSplineAuthoringMenu
    {
        [MenuItem("GameObject/VoxelisX/Authoring/Voxel Spline", false, 10)]
        private static void Create(MenuCommand command)
        {
            var gameObject = new GameObject("Voxel Spline");
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Voxel Spline");
            Undo.AddComponent<VoxelSplineAuthoring>(gameObject);
            Selection.activeGameObject = gameObject;

            EditorApplication.delayCall += () =>
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
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
                catch (System.InvalidOperationException exception)
                {
                    Debug.LogWarning(
                        $"Unity could not activate the spline context automatically. Select Spline in the Scene Tools overlay. {exception.Message}",
                        gameObject);
                }
            };
        }
    }
}
