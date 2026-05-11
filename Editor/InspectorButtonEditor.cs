using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Voxelis;

namespace Voxelis.EditorTools
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(MonoBehaviour), true)]
    public class InspectorButtonEditor : Editor
    {
        private static readonly Dictionary<Type, List<ButtonMethod>> MethodCache = new();

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            DrawButtons();
        }

        private void DrawButtons()
        {
            List<ButtonMethod> methods = GetButtonMethods(target.GetType());
            if (methods.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space();

            foreach (ButtonMethod method in methods)
            {
                bool disabled = (method.Attribute.PlayModeOnly && !Application.isPlaying)
                                || (method.Attribute.EditModeOnly && Application.isPlaying);

                using (new EditorGUI.DisabledScope(disabled))
                {
                    if (GUILayout.Button(method.Label))
                    {
                        InvokeButton(method);
                    }
                }
            }
        }

        private void InvokeButton(ButtonMethod method)
        {
            serializedObject.ApplyModifiedProperties();

            foreach (UnityEngine.Object selectedTarget in targets)
            {
                try
                {
                    method.Method.Invoke(selectedTarget, null);
                }
                catch (TargetInvocationException ex)
                {
                    Debug.LogException(ex.InnerException ?? ex, selectedTarget);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, selectedTarget);
                }
            }
        }

        private static List<ButtonMethod> GetButtonMethods(Type targetType)
        {
            if (MethodCache.TryGetValue(targetType, out List<ButtonMethod> methods))
            {
                return methods;
            }

            methods = new List<ButtonMethod>();
            foreach (MethodInfo method in EnumerateInstanceMethods(targetType))
            {
                var attribute = method.GetCustomAttribute<InspectorButtonAttribute>(true);
                if (attribute == null)
                {
                    continue;
                }

                if (method.GetParameters().Length != 0)
                {
                    Debug.LogWarning(
                        $"{targetType.Name}.{method.Name} has InspectorButtonAttribute but takes parameters. Inspector buttons only support parameterless methods.");
                    continue;
                }

                methods.Add(new ButtonMethod(method, attribute));
            }

            MethodCache[targetType] = methods;
            return methods;
        }

        private static IEnumerable<MethodInfo> EnumerateInstanceMethods(Type targetType)
        {
            const BindingFlags flags = BindingFlags.Instance
                                       | BindingFlags.Public
                                       | BindingFlags.NonPublic
                                       | BindingFlags.DeclaredOnly;

            var hierarchy = new Stack<Type>();
            for (Type type = targetType; type != null && type != typeof(MonoBehaviour); type = type.BaseType)
            {
                hierarchy.Push(type);
            }

            while (hierarchy.Count > 0)
            {
                Type type = hierarchy.Pop();
                MethodInfo[] methods = type.GetMethods(flags);
                Array.Sort(methods, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                for (int i = 0; i < methods.Length; i++)
                {
                    yield return methods[i];
                }
            }
        }

        private readonly struct ButtonMethod
        {
            public ButtonMethod(MethodInfo method, InspectorButtonAttribute attribute)
            {
                Method = method;
                Attribute = attribute;
                Label = string.IsNullOrWhiteSpace(attribute.Label)
                    ? ObjectNames.NicifyVariableName(method.Name)
                    : attribute.Label;
            }

            public MethodInfo Method { get; }
            public InspectorButtonAttribute Attribute { get; }
            public string Label { get; }
        }
    }
}
