using System;
using UnityEditor;
using UnityEngine;

namespace Voxelis.EditorTools
{
    internal static class BlockConversionPaletteEditorGUI
    {
        private const float ColorColumnWidth = 54f;
        private const float ColumnSpacing = 6f;

        public static void RefreshPalette(
            SerializedObject serializedObject,
            UnityEngine.Object loader,
            Action refresh,
            string undoName,
            string errorDialogTitle)
        {
            serializedObject.ApplyModifiedProperties();
            Undo.RecordObject(loader, undoName);

            try
            {
                refresh();
                EditorUtility.SetDirty(loader);
                PrefabUtility.RecordPrefabInstancePropertyModifications(loader);
                serializedObject.Update();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, loader);
                EditorUtility.DisplayDialog(errorDialogTitle, exception.Message, "OK");
            }
        }

        public static void DrawPaletteTable(
            SerializedProperty paletteProperty,
            string emptyMessage,
            string sourceIdHeader,
            string colorPropertyName,
            string sourceIdPropertyName,
            Func<int, string> formatSourceId,
            float sourceIdColumnWidth)
        {
            int entryCount = paletteProperty.arraySize;
            EditorGUILayout.LabelField($"Block Conversion Palette ({entryCount})", EditorStyles.boldLabel);

            if (entryCount == 0)
            {
                EditorGUILayout.HelpBox(emptyMessage, MessageType.Info);
                return;
            }

            DrawHeader(sourceIdHeader, sourceIdColumnWidth);

            for (int i = 0; i < entryCount; i++)
            {
                SerializedProperty entry = paletteProperty.GetArrayElementAtIndex(i);
                DrawEntry(
                    entry.FindPropertyRelative(colorPropertyName),
                    entry.FindPropertyRelative(sourceIdPropertyName),
                    entry.FindPropertyRelative("targetBlockId"),
                    formatSourceId,
                    sourceIdColumnWidth);
            }
        }

        private static void DrawHeader(string sourceIdHeader, float sourceIdColumnWidth)
        {
            Rect row = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            GetColumnRects(
                row,
                sourceIdColumnWidth,
                out Rect colorRect,
                out Rect sourceIdRect,
                out Rect blockIdRect);

            EditorGUI.LabelField(colorRect, "Color", EditorStyles.miniBoldLabel);
            EditorGUI.LabelField(sourceIdRect, sourceIdHeader, EditorStyles.miniBoldLabel);
            EditorGUI.LabelField(blockIdRect, "VoxelisX Block ID", EditorStyles.miniBoldLabel);
        }

        private static void DrawEntry(
            SerializedProperty colorProperty,
            SerializedProperty sourceIdProperty,
            SerializedProperty targetBlockIdProperty,
            Func<int, string> formatSourceId,
            float sourceIdColumnWidth)
        {
            Rect row = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            GetColumnRects(
                row,
                sourceIdColumnWidth,
                out Rect colorRect,
                out Rect sourceIdRect,
                out Rect blockIdRect);

            Rect swatchRect = new Rect(
                colorRect.x,
                colorRect.y + 1f,
                colorRect.width,
                colorRect.height - 2f);
            DrawColorSwatch(swatchRect, colorProperty.colorValue);

            EditorGUI.LabelField(sourceIdRect, formatSourceId(sourceIdProperty.intValue));

            int editedBlockId = EditorGUI.IntField(blockIdRect, targetBlockIdProperty.intValue);
            targetBlockIdProperty.intValue = Mathf.Clamp(editedBlockId, ushort.MinValue, ushort.MaxValue);
        }

        private static void GetColumnRects(
            Rect row,
            float sourceIdColumnWidth,
            out Rect colorRect,
            out Rect sourceIdRect,
            out Rect blockIdRect)
        {
            colorRect = new Rect(row.x, row.y, ColorColumnWidth, row.height);
            sourceIdRect = new Rect(
                colorRect.xMax + ColumnSpacing,
                row.y,
                sourceIdColumnWidth,
                row.height);

            float blockIdX = sourceIdRect.xMax + ColumnSpacing;
            blockIdRect = new Rect(blockIdX, row.y, Mathf.Max(50f, row.xMax - blockIdX), row.height);
        }

        private static void DrawColorSwatch(Rect rect, Color color)
        {
            EditorGUI.DrawRect(rect, color);

            Color border = EditorGUIUtility.isProSkin
                ? new Color(0f, 0f, 0f, 0.8f)
                : new Color(0f, 0f, 0f, 0.45f);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), border);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
        }
    }
}
