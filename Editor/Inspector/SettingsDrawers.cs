using System;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;

namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>A list entry drawn one field per line, each labelled through <see cref="L10n"/> under <c>&lt;type&gt;:&lt;field&gt;</c>.</summary>
    internal abstract class LocalizedEntryDrawer : PropertyDrawer
    {
        protected abstract Type Entry { get; }
        protected abstract string[] Fields { get; }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            Fields.Length * (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing);

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            foreach (var name in Fields)
            {
                var field = property.FindPropertyRelative(name);
                var content = L10n.G(Entry.Name + ":" + name);
                if (field.propertyType == SerializedPropertyType.Enum) EnumPopup(line, field, content, Entry.GetField(name).FieldType);
                else EditorGUI.PropertyField(line, field, content);
                line.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            }
        }

        /// <summary>An enum field whose options are labelled <c>&lt;enum&gt;:&lt;value&gt;</c>, with their tooltips.</summary>
        static void EnumPopup(Rect line, SerializedProperty field, GUIContent content, Type enumType)
        {
            var options = Enum.GetNames(enumType).Select(n => L10n.G(enumType.Name + ":" + n)).ToArray();
            using (new EditorGUI.PropertyScope(line, content, field))
            {
                EditorGUI.BeginChangeCheck();
                var index = EditorGUI.Popup(line, content, field.enumValueIndex, options);
                if (EditorGUI.EndChangeCheck()) field.enumValueIndex = index;
            }
        }
    }

    [CustomPropertyDrawer(typeof(RendererSettings))]
    internal sealed class RendererSettingsDrawer : LocalizedEntryDrawer
    {
        protected override Type Entry => typeof(RendererSettings);
        protected override string[] Fields => new[]
        {
            nameof(RendererSettings.renderer), nameof(RendererSettings.excludeFromDecimation), nameof(RendererSettings.protection),
        };
    }

    [CustomPropertyDrawer(typeof(MaterialOcclusion))]
    internal sealed class MaterialOcclusionDrawer : LocalizedEntryDrawer
    {
        protected override Type Entry => typeof(MaterialOcclusion);
        protected override string[] Fields => new[] { nameof(MaterialOcclusion.material), nameof(MaterialOcclusion.mode) };
    }
}
