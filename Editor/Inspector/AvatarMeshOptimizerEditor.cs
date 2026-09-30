using System;
using System.Linq;
using nadena.dev.ndmf.ui;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;

namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>
    /// The component's inspector: every label through <see cref="L10n"/>, and only the fields the chosen settings use. The
    /// target's fields show under the target that reads them, the occlusion lists only with hidden-triangle removal on, and
    /// the protect settings under a collapsed Advanced group with decimation on.
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
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            Field(_removeHidden);
            if (_removeHidden.boolValue || _removeHidden.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_alwaysOn, Label(_alwaysOn), true);
                Field(_outlines);
                EditorGUILayout.PropertyField(_materials, Label(_materials), true);
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
