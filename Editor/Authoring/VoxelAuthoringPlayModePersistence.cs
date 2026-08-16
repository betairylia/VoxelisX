using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Voxelis.Authoring;

namespace Voxelis.Authoring.EditorTools
{
    /// <summary>
    /// Stores an authoring tool snapshot in SessionState during Play Mode, restores it after
    /// Play Mode exits, and saves the owning scene. This works with normal and fast Play Mode.
    /// </summary>
    [InitializeOnLoad]
    internal static class VoxelAuthoringPlayModePersistence
    {
        private const string SessionKey = "VoxelisX.Authoring.PendingPlayModeSnapshots";

        [Serializable]
        private sealed class SnapshotCollection
        {
            public List<ToolSnapshot> tools = new();
        }

        [Serializable]
        private sealed class ToolSnapshot
        {
            public string toolId;
            public string displayName;
            public List<ComponentSnapshot> components = new();
        }

        [Serializable]
        private sealed class ComponentSnapshot
        {
            public string globalObjectId;
            public string scenePath;
            public string hierarchyIndices;
            public string assemblyQualifiedType;
            public int componentIndex;
            public string json;
        }

        static VoxelAuthoringPlayModePersistence()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ResumePendingSnapshotsWhenReady();
        }

        public static bool HasPendingSnapshot(VoxelEntityAuthoringTool tool)
        {
            string id = GetId(tool);
            SnapshotCollection collection = Load();
            return collection.tools.Exists(snapshot => snapshot.toolId == id);
        }

        public static void Capture(VoxelEntityAuthoringTool tool)
        {
            if (tool == null || !EditorApplication.isPlaying)
            {
                return;
            }

            var components = new List<Component>();
            tool.CollectAuthoringStateComponents(components);

            string toolId = GetId(tool);
            SnapshotCollection collection = Load();
            collection.tools.RemoveAll(snapshot => snapshot.toolId == toolId);

            var toolSnapshot = new ToolSnapshot
            {
                toolId = toolId,
                displayName = tool.name,
            };

            for (int i = 0; i < components.Count; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }

                toolSnapshot.components.Add(new ComponentSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(),
                    scenePath = component.gameObject.scene.path,
                    hierarchyIndices = GetHierarchyIndices(component.transform),
                    assemblyQualifiedType = component.GetType().AssemblyQualifiedName,
                    componentIndex = GetComponentIndex(component),
                    json = EditorJsonUtility.ToJson(component),
                });
            }

            collection.tools.Add(toolSnapshot);
            Save(collection);
            ResumePendingSnapshotsWhenReady();
            Debug.Log(
                $"Captured current Play Mode changes for '{tool.name}'. They will be applied and saved after Play Mode exits.",
                tool);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += ApplyPendingSnapshots;
            }
        }

        private static void ResumePendingSnapshotsWhenReady()
        {
            if (string.IsNullOrEmpty(SessionState.GetString(SessionKey, string.Empty)))
            {
                return;
            }

            // A normal Play Mode exit can reload editor assemblies before EnteredEditMode is
            // delivered to this class. SessionState survives that reload. Wait on the editor
            // update loop until Unity has fully completed the transition, then apply once.
            EditorApplication.update -= TryApplyPendingSnapshots;
            EditorApplication.update += TryApplyPendingSnapshots;
        }

        private static void TryApplyPendingSnapshots()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            EditorApplication.update -= TryApplyPendingSnapshots;
            ApplyPendingSnapshots();
        }

        private static void ApplyPendingSnapshots()
        {
            SnapshotCollection collection = Load();
            if (collection.tools.Count == 0)
            {
                return;
            }

            var changedScenes = new HashSet<string>();
            int restoredTools = 0;

            for (int toolIndex = 0; toolIndex < collection.tools.Count; toolIndex++)
            {
                ToolSnapshot toolSnapshot = collection.tools[toolIndex];
                bool restoredAny = false;

                for (int componentIndex = 0; componentIndex < toolSnapshot.components.Count; componentIndex++)
                {
                    ComponentSnapshot snapshot = toolSnapshot.components[componentIndex];
                    Component component = Resolve(snapshot);
                    if (component == null)
                    {
                        Debug.LogWarning(
                            $"Could not restore one component for voxel authoring tool '{toolSnapshot.displayName}'.");
                        continue;
                    }

                    Undo.RegisterCompleteObjectUndo(component, "Keep Voxel Authoring Play Mode Changes");
                    EditorJsonUtility.FromJsonOverwrite(snapshot.json, component);
                    EditorUtility.SetDirty(component);
                    changedScenes.Add(snapshot.scenePath);
                    restoredAny = true;
                }

                if (restoredAny)
                {
                    Component first = Resolve(toolSnapshot.components[0]);
                    if (first is VoxelEntityAuthoringTool tool)
                    {
                        tool.ConfigureOwnedComponents();
                        tool.RequestRebuild();
                        EditorUtility.SetDirty(tool.OwnedEntity);
                    }
                    restoredTools++;
                }
            }

            SessionState.EraseString(SessionKey);

            foreach (string scenePath in changedScenes)
            {
                Scene scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    Debug.LogWarning($"Could not save restored authoring changes because scene '{scenePath}' is not loaded.");
                    continue;
                }

                EditorSceneManager.MarkSceneDirty(scene);
                if (!string.IsNullOrEmpty(scene.path))
                {
                    EditorSceneManager.SaveScene(scene);
                }
            }

            if (restoredTools > 0)
            {
                Debug.Log($"Restored and saved Play Mode changes for {restoredTools} voxel authoring tool(s).");
                SceneView.RepaintAll();
            }
        }

        private static Component Resolve(ComponentSnapshot snapshot)
        {
            if (GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId globalId))
            {
                if (GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) is Component component)
                {
                    return component;
                }
            }

            Scene scene = SceneManager.GetSceneByPath(snapshot.scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return null;
            }

            string[] indexParts = snapshot.hierarchyIndices.Split('/');
            if (indexParts.Length == 0 || !int.TryParse(indexParts[0], out int rootIndex))
            {
                return null;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            if (rootIndex < 0 || rootIndex >= roots.Length)
            {
                return null;
            }

            Transform transform = roots[rootIndex].transform;
            for (int i = 1; i < indexParts.Length; i++)
            {
                if (!int.TryParse(indexParts[i], out int childIndex) ||
                    childIndex < 0 || childIndex >= transform.childCount)
                {
                    return null;
                }
                transform = transform.GetChild(childIndex);
            }

            Type componentType = Type.GetType(snapshot.assemblyQualifiedType);
            if (componentType == null)
            {
                return null;
            }

            Component[] components = transform.GetComponents(componentType);
            return snapshot.componentIndex >= 0 && snapshot.componentIndex < components.Length
                ? components[snapshot.componentIndex]
                : null;
        }

        private static int GetComponentIndex(Component component)
        {
            Component[] components = component.GetComponents(component.GetType());
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == component)
                {
                    return i;
                }
            }
            return 0;
        }

        private static string GetHierarchyIndices(Transform transform)
        {
            var indices = new List<int>();
            for (Transform current = transform; current != null; current = current.parent)
            {
                indices.Add(current.GetSiblingIndex());
            }
            indices.Reverse();
            return string.Join("/", indices);
        }

        private static string GetId(VoxelEntityAuthoringTool tool)
        {
            return GlobalObjectId.GetGlobalObjectIdSlow(tool).ToString();
        }

        private static SnapshotCollection Load()
        {
            string json = SessionState.GetString(SessionKey, string.Empty);
            return string.IsNullOrEmpty(json)
                ? new SnapshotCollection()
                : JsonUtility.FromJson<SnapshotCollection>(json) ?? new SnapshotCollection();
        }

        private static void Save(SnapshotCollection collection)
        {
            SessionState.SetString(SessionKey, JsonUtility.ToJson(collection));
        }
    }
}
