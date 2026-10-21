using System;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;

namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>
    /// Splits one line into a flexible first column and fixed columns packed against its right edge, so a list's header
    /// and its rows line up whatever the inspector's width.
    /// </summary>
    internal static class Columns
    {
        const float Gap = 4f;

        public static Rect[] Split(Rect line, params float[] fixedWidths)
        {
            var rects = new Rect[fixedWidths.Length + 1];
            var right = line.xMax;
            for (var i = fixedWidths.Length - 1; i >= 0; i--)
            {
                rects[i + 1] = new Rect(right - fixedWidths[i], line.y, fixedWidths[i], line.height);
                right -= fixedWidths[i] + Gap;
            }
            rects[0] = new Rect(line.x, line.y, Mathf.Max(0f, right - line.x), line.height);
            return rects;
        }

        /// <summary>The width a column needs for its header and for the widest value it shows, with a little slack for fallback fonts.</summary>
        public static float Width(GUIStyle style, float least, params string[] texts) =>
            Mathf.Max(least, texts.Max(t => style.CalcSize(new GUIContent(t)).x) + 4f);
    }

    /// <summary>A renderer's settings on one line: the renderer, then the exclude toggle and the protection factor.</summary>
    [CustomPropertyDrawer(typeof(RendererSettings))]
    internal sealed class RendererSettingsDrawer : PropertyDrawer
    {
        const string Protection = "RendererSettings:" + nameof(RendererSettings.protection);

        public static GUIContent ExcludeHeader => L10n.G(InspectorKeys.ColumnExclude);
        public static GUIContent ProtectionHeader => L10n.G(Protection);

        public static Rect[] Split(Rect line) => Columns.Split(line,
            Columns.Width(EditorStyles.label, 18f, ExcludeHeader.text),
            Columns.Width(EditorStyles.label, 48f, ProtectionHeader.text));

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var columns = Split(position);
            var exclude = columns[1];
            exclude.x += (exclude.width - 16f) / 2f;
            exclude.width = 16f;
            EditorGUI.PropertyField(columns[0], property.FindPropertyRelative(nameof(RendererSettings.renderer)), GUIContent.none);
            EditorGUI.PropertyField(exclude, property.FindPropertyRelative(nameof(RendererSettings.excludeFromDecimation)),
                new GUIContent(string.Empty, L10n.Tooltip(InspectorKeys.ColumnExclude)));
            // Delayed, as the target's numbers are: a value is taken on Enter or leaving the field, not at each digit.
            var protection = property.FindPropertyRelative(nameof(RendererSettings.protection));
            EditorGUI.BeginChangeCheck();
            EditorGUI.DelayedFloatField(columns[2], protection, new GUIContent(string.Empty, L10n.Tooltip(Protection)));
            if (EditorGUI.EndChangeCheck() && !(protection.floatValue >= ProtectionLimit.Min)) protection.floatValue = ProtectionLimit.Min;
        }
    }

    /// <summary>An override on one line: the material, then what to treat it as.</summary>
    [CustomPropertyDrawer(typeof(MaterialOcclusion))]
    internal sealed class MaterialOcclusionDrawer : PropertyDrawer
    {
        public static GUIContent ModeHeader => L10n.G("MaterialOcclusion:" + nameof(MaterialOcclusion.mode));

        static GUIContent[] Options => Enum.GetNames(typeof(OcclusionMode)).Select(n => L10n.G("OcclusionMode:" + n)).ToArray();

        public static Rect[] Split(Rect line) => Columns.Split(line,
            Columns.Width(EditorStyles.popup, 90f, Options.Select(o => o.text).Append(ModeHeader.text).ToArray()) + 8f);

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var columns = Split(position);
            EditorGUI.PropertyField(columns[0], property.FindPropertyRelative(nameof(MaterialOcclusion.material)), GUIContent.none);

            var mode = property.FindPropertyRelative(nameof(MaterialOcclusion.mode));
            using (new EditorGUI.PropertyScope(columns[1], GUIContent.none, mode))
            {
                EditorGUI.showMixedValue = mode.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                // The enum's values run 0, 1, 2 in declaration order, so its value is its index.
                var index = EditorGUI.Popup(columns[1], mode.enumValueIndex, Options);
                if (EditorGUI.EndChangeCheck()) mode.enumValueIndex = index;
                EditorGUI.showMixedValue = false;
            }
        }
    }
}
