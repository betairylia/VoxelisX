using UnityEditor;
using UnityEngine;

namespace Caelix.EditorTools
{
    [CustomEditor(typeof(BmpFileLoader))]
    public sealed class BmpFileLoaderEditor : Editor
    {
        private const float PaletteIdColumnWidth = 108f;

        private SerializedProperty scriptProperty;
        private SerializedProperty bmpFilePathProperty;
        private SerializedProperty loadAsStoneProperty;
        private SerializedProperty paletteProperty;

        private void OnEnable()
        {
            scriptProperty = serializedObject.FindProperty("m_Script");
            bmpFilePathProperty = serializedObject.FindProperty("bmpFilePath");
            loadAsStoneProperty = serializedObject.FindProperty("loadAsStone");
            paletteProperty = serializedObject.FindProperty("blockConversionPalette");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(scriptProperty);
            }

            EditorGUILayout.PropertyField(bmpFilePathProperty);
            EditorGUILayout.PropertyField(loadAsStoneProperty);

            using (new EditorGUI.DisabledScope(
                       Application.isPlaying || string.IsNullOrWhiteSpace(bmpFilePathProperty.stringValue)))
            {
                if (GUILayout.Button("Read BMP Palette"))
                {
                    ReadPalette();
                }
            }

            EditorGUILayout.Space();
            BlockConversionPaletteEditorGUI.DrawPaletteTable(
                paletteProperty,
                "Read the BMP palette to list the indexed entries or direct colors used by this file.",
                "BMP Palette ID",
                "bmpColor",
                "bmpPaletteIndex",
                paletteId => paletteId >= 0 ? paletteId.ToString() : "Direct color",
                PaletteIdColumnWidth);

            serializedObject.ApplyModifiedProperties();
        }

        private void ReadPalette()
        {
            var loader = (BmpFileLoader)target;
            BlockConversionPaletteEditorGUI.RefreshPalette(
                serializedObject,
                loader,
                loader.RefreshBlockConversionPalette,
                "Read BMP Palette",
                "Unable to Read BMP Palette");
        }
    }
}
