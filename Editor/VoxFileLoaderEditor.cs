using UnityEditor;
using UnityEngine;

namespace Caelix.EditorTools
{
    [CustomEditor(typeof(VoxFileLoader))]
    public sealed class VoxFileLoaderEditor : Editor
    {
        private const float PaletteIdColumnWidth = 92f;

        private SerializedProperty scriptProperty;
        private SerializedProperty voxFilePathProperty;
        private SerializedProperty loadAsStoneProperty;
        private SerializedProperty paletteProperty;

        private void OnEnable()
        {
            scriptProperty = serializedObject.FindProperty("m_Script");
            voxFilePathProperty = serializedObject.FindProperty("voxFilePath");
            loadAsStoneProperty = serializedObject.FindProperty("LoadAsStone");
            paletteProperty = serializedObject.FindProperty("blockConversionPalette");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(scriptProperty);
            }

            EditorGUILayout.PropertyField(voxFilePathProperty);
            EditorGUILayout.PropertyField(loadAsStoneProperty);

            using (new EditorGUI.DisabledScope(
                       Application.isPlaying || string.IsNullOrWhiteSpace(voxFilePathProperty.stringValue)))
            {
                if (GUILayout.Button("Read VOX Palette"))
                {
                    ReadPalette();
                }
            }

            EditorGUILayout.Space();
            BlockConversionPaletteEditorGUI.DrawPaletteTable(
                paletteProperty,
                "Read the VOX palette to list the palette IDs used by this file.",
                "VOX Palette ID",
                "voxColor",
                "voxPaletteId",
                paletteId => paletteId.ToString(),
                PaletteIdColumnWidth);

            serializedObject.ApplyModifiedProperties();
        }

        private void ReadPalette()
        {
            var loader = (VoxFileLoader)target;
            BlockConversionPaletteEditorGUI.RefreshPalette(
                serializedObject,
                loader,
                loader.RefreshBlockConversionPalette,
                "Read VOX Palette",
                "Unable to Read VOX Palette");
        }
    }
}
