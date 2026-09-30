using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.ui;
using Ryan6Vrc.MeshOptimizer.Preview;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;

namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>
    /// The component's inspector: every label through <see cref="L10n"/>, and only the fields the chosen settings use. The
    /// target's fields show under the target that reads them, the occlusion lists only with hidden-triangle removal on, each
    /// under a help paragraph that stays in view, and the protect settings under a collapsed Advanced group with decimation on.
    /// </summary>
    [CustomEditor(typeof(AvatarMeshOptimizer))]
    [CanEditMultipleObjects]
    internal sealed class AvatarMeshOptimizerEditor : Editor
    {
        const string AdvancedOpen = "Ryan6Vrc.MeshOptimizer.Inspector.AdvancedOpen";

        SerializedProperty _decimate, _removeHidden, _target, _budget, _maxError, _tolerance, _alwaysOn, _outlines,
            _materials, _renderers, _faceFloor, _lockFingers, _fingerWeight;

        void OnEnable()
        {
            _decimate = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.decimate));
            _removeHidden = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.removeHiddenTriangles));
            _target = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.target));
            _budget = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.triangleBudget));
            _maxError = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.maxError));
            _tolerance = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.tolerance));
            _alwaysOn = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.alwaysOn));
            _outlines = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.outlinesShowInside));
            _materials = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.materials));
            _renderers = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.renderers));
            _faceFloor = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.faceFloor));
            _lockFingers = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.lockFingers));
            _fingerWeight = serializedObject.FindProperty(nameof(AvatarMeshOptimizer.fingerWeight));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            LanguageSwitcher.DrawImmediate();
            EditorGUILayout.Space();

            Field(_decimate);
            if (_decimate.boolValue || _decimate.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                DrawTarget();
                EditorGUILayout.PropertyField(_renderers, Label(_renderers), true);
                DrawPreview();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            Field(_removeHidden);
            if (_removeHidden.boolValue || _removeHidden.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                Help(InspectorKeys.AlwaysOnHelp);
                EditorGUILayout.PropertyField(_alwaysOn, Label(_alwaysOn), true);
                Field(_outlines);
                EditorGUILayout.Space();
                Help(InspectorKeys.MaterialsHelp);
                EditorGUILayout.PropertyField(_materials, Label(_materials), true);
                DrawCheck();
                EditorGUI.indentLevel--;
            }

            if (_decimate.boolValue || _decimate.hasMultipleDifferentValues)
            {
                EditorGUILayout.Space();
                var open = EditorGUILayout.Foldout(SessionState.GetBool(AdvancedOpen, false), L10n.G(InspectorKeys.Advanced), true);
                SessionState.SetBool(AdvancedOpen, open);
                if (open)
                {
                    EditorGUI.indentLevel++;
                    Field(_faceFloor);
                    Field(_lockFingers);
                    using (new EditorGUI.DisabledScope(!_lockFingers.boolValue && !_lockFingers.hasMultipleDifferentValues))
                        Field(_fingerWeight);
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>The target, then the fields it reads. Numbers are delayed, so a value is taken on Enter or leaving the field.</summary>
        void DrawTarget()
        {
            var kinds = (TargetKind[])Enum.GetValues(typeof(TargetKind));
            EditorGUI.showMixedValue = _target.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            var index = EditorGUILayout.Popup(Label(_target), _target.enumValueIndex, kinds.Select(k => L10n.G("TargetKind:" + k)).ToArray());
            if (EditorGUI.EndChangeCheck()) _target.enumValueIndex = index;
            EditorGUI.showMixedValue = false;
            if (_target.hasMultipleDifferentValues) return;

            if ((TargetKind)_target.enumValueIndex == TargetKind.Tolerance)
            {
                var presets = (TolerancePreset[])Enum.GetValues(typeof(TolerancePreset));
                var current = _tolerance.hasMultipleDifferentValues ? TolerancePreset.Custom : TolerancePresets.Of(_tolerance.floatValue);
                EditorGUI.BeginChangeCheck();
                var picked = (TolerancePreset)EditorGUILayout.Popup(L10n.G(InspectorKeys.Preset), (int)current,
                    presets.Select(p => L10n.G(TolerancePresets.Key(p))).ToArray());
                if (EditorGUI.EndChangeCheck() && TolerancePresets.TryTolerance(picked, out var millimetres)) _tolerance.floatValue = millimetres;
                DelayedFloat(_tolerance);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.DelayedIntField(_budget, Label(_budget));
                if (EditorGUI.EndChangeCheck() && _budget.intValue < 0) _budget.intValue = 0;
                DelayedFloat(_maxError);
            }
        }

        /// <summary>
        /// The preview's switch, NDMF's own for this filter, and what the latest preview of this avatar reached, with where
        /// its shape-key liveness came from.
        /// </summary>
        void DrawPreview()
        {
            EditorGUILayout.Space();
            var on = DecimationPreview.Toggle.IsEnabled.Value;
            EditorGUI.BeginChangeCheck();
            on = GUILayout.Toggle(on, L10n.G(InspectorKeys.Preview), "Button");
            if (EditorGUI.EndChangeCheck()) DecimationPreview.Toggle.IsEnabled.Value = on;

            string text;
            var status = targets.Length == 1 ? PreviewStatus.Of((AvatarMeshOptimizer)target) : null;
            if (!on) text = L10n.Tr(InspectorKeys.PreviewOff);
            else if (status == null) text = L10n.Tr(InspectorKeys.PreviewPending);
            else if (status.Failure != null) text = string.Format(L10n.Tr(InspectorKeys.PreviewFailed), status.Failure);
            else
                text = string.Format(L10n.Tr(InspectorKeys.PreviewResult), status.TrianglesBefore.ToString("N0"), status.TrianglesAfter.ToString("N0"),
                           status.ErrorLevelMm.ToString("0.00")) + "\n" +
                       L10n.Tr(status.Liveness == PreviewLiveness.Descriptor ? InspectorKeys.PreviewNoteDescriptor : InspectorKeys.PreviewNoteEveryKeyLive);
            EditorGUILayout.HelpBox(text, status?.Failure != null ? MessageType.Warning : MessageType.None);
        }

        /// <summary>
        /// The check button, then each material of the always-on renderers with what Automatic reads it as and a button
        /// that overrides it to the opposite. Checking adds nothing to the overrides list; only the button beside a
        /// misread material does.
        /// </summary>
        void DrawCheck()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUI.indentLevel * IndentWidth);
                using (new EditorGUI.DisabledScope(targets.Length != 1))
                    if (GUILayout.Button(L10n.G(InspectorKeys.Check)))
                    {
                        serializedObject.ApplyModifiedProperties();
                        CheckAlwaysOn();
                        // The progress bar broke this event's layout, so it ends here and the next one draws the verdicts.
                        GUIUtility.ExitGUI();
                    }
            }
            if (_checked == null || targets.Length != 1) return;

            var indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            foreach (var material in _checked)
            {
                if (material == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(indent * IndentWidth);
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(material, typeof(Material), false, GUILayout.MinWidth(60f));
                    EditorGUILayout.LabelField(MaterialVerdicts.Describe(material), GUILayout.MinWidth(60f));
                    var known = MaterialVerdicts.TryGet(material, out var verdict);
                    var opposite = MaterialVerdicts.Opposite(verdict.Kind);
                    var set = new GUIContent(string.Format(L10n.Tr(InspectorKeys.Override), L10n.Tr("OcclusionMode:" + opposite)),
                        L10n.Tooltip(InspectorKeys.Override));
                    using (new EditorGUI.DisabledScope(!known || IsListed(material)))
                        if (GUILayout.Button(set, GUILayout.ExpandWidth(false))) AddOverride(material, opposite);
                }
            }
            EditorGUI.indentLevel = indent;
        }

        const float IndentWidth = 15f;

        /// <summary>The always-on renderers' materials last checked, in list order, or null before the first check.</summary>
        List<Material> _checked;

        /// <summary>Reads what Automatic decides for every always-on material and every overridden one.</summary>
        void CheckAlwaysOn()
        {
            var optimizer = (AvatarMeshOptimizer)target;
            _checked = optimizer.alwaysOn.Where(r => r != null).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            var all = _checked.Concat(optimizer.materials.Where(o => o != null && o.material != null).Select(o => o.material)).Distinct().ToList();
            var title = L10n.Tr(InspectorKeys.Check);
            try
            {
                for (var i = 0; i < all.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(title, all[i].name, (float)i / all.Count);
                    MaterialVerdicts.Check(all[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        bool IsListed(Material material)
        {
            for (var i = 0; i < _materials.arraySize; i++)
                if (_materials.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(MaterialOcclusion.material)).objectReferenceValue == material)
                    return true;
            return false;
        }

        void AddOverride(Material material, OcclusionMode mode)
        {
            var index = _materials.arraySize;
            _materials.arraySize++;
            var entry = _materials.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative(nameof(MaterialOcclusion.material)).objectReferenceValue = material;
            // The enum's values run 0, 1, 2 in declaration order, so its value is its index.
            entry.FindPropertyRelative(nameof(MaterialOcclusion.mode)).enumValueIndex = (int)mode;
        }

        /// <summary>A paragraph of help above a list, at the list's indent, where it reads without hovering.</summary>
        static void Help(string key)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUI.indentLevel * IndentWidth);
                EditorGUILayout.HelpBox(L10n.Tr(key), MessageType.None);
            }
        }

        static void DelayedFloat(SerializedProperty property)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.DelayedFloatField(property, Label(property));
            if (EditorGUI.EndChangeCheck() && !(property.floatValue >= 0f)) property.floatValue = 0f;
        }

        static void Field(SerializedProperty property) => EditorGUILayout.PropertyField(property, Label(property));

        /// <summary>A component field's label and tooltip, under its key <c>MeshOptimizer:&lt;field&gt;</c>.</summary>
        static GUIContent Label(SerializedProperty property) => L10n.G("MeshOptimizer:" + property.name);
    }
}
