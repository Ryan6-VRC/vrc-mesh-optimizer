using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.runtime;
using nadena.dev.ndmf.ui;
using Ryan6Vrc.MeshOptimizer.Preview;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;
using Object = UnityEngine.Object;

namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>
    /// The component's inspector: every label through <see cref="L10n"/>, and only the fields the chosen settings use.
    /// Each of the two features is a section whose toggle is its header, with its fields flush beneath it; the lists draw
    /// one line per entry under a header naming the columns. The material analysis sits under the always-on list whose
    /// materials it reads. Advanced comes last, below a rule, and is the only indent. Text is body size: each occlusion
    /// list keeps one or two visible sentences saying what it does, and the detail sits in tooltips.
    /// </summary>
    [CustomEditor(typeof(AvatarMeshOptimizer))]
    [CanEditMultipleObjects]
    internal sealed class AvatarMeshOptimizerEditor : Editor
    {
        const string AdvancedOpen = "Ryan6Vrc.MeshOptimizer.Inspector.AdvancedOpen";

        SerializedProperty _decimate, _removeHidden, _target, _budget, _maxError, _tolerance, _alwaysOn, _outlines,
            _materials, _renderers, _faceFloor, _lockFingers, _fingerWeight;

        ReorderableList _rendererList, _alwaysOnList, _materialList;
        Drop _rendererDrop, _alwaysOnDrop, _materialDrop;

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

            _rendererList = List(_renderers, rect =>
            {
                var columns = RendererSettingsDrawer.Split(rect);
                EditorGUI.LabelField(columns[0], Label(_renderers));
                EditorGUI.LabelField(columns[1], RendererSettingsDrawer.ExcludeHeader);
                EditorGUI.LabelField(columns[2], RendererSettingsDrawer.ProtectionHeader);
            });
            _alwaysOnList = List(_alwaysOn, rect => EditorGUI.LabelField(rect, Label(_alwaysOn)));
            _materialList = List(_materials, rect =>
            {
                var columns = MaterialOcclusionDrawer.Split(rect);
                EditorGUI.LabelField(columns[0], Label(_materials));
                EditorGUI.LabelField(columns[1], MaterialOcclusionDrawer.ModeHeader);
            });

            _rendererDrop = new Drop(Renderers, nameof(RendererSettings.renderer), entry =>
            {
                entry.FindPropertyRelative(nameof(RendererSettings.excludeFromDecimation)).boolValue = false;
                entry.FindPropertyRelative(nameof(RendererSettings.protection)).floatValue = 1f;
            });
            _alwaysOnDrop = new Drop(Renderers, null, null);
            _materialDrop = new Drop(Materials, nameof(MaterialOcclusion.material),
                entry => entry.FindPropertyRelative(nameof(MaterialOcclusion.mode)).enumValueIndex = (int)OcclusionMode.Automatic);
        }

        /// <summary>
        /// What dropping objects on a list adds: what the dropped objects resolve to, the field of an entry that holds one
        /// (null when the entry is the object), and what a new entry's other fields start as, since growing an array copies
        /// its last entry.
        /// </summary>
        sealed class Drop
        {
            public readonly Func<Object[], IEnumerable<Object>> Resolve;
            public readonly string Field;
            public readonly Action<SerializedProperty> Reset;

            public Drop(Func<Object[], IEnumerable<Object>> resolve, string field, Action<SerializedProperty> reset)
            {
                Resolve = resolve;
                Field = field;
                Reset = reset;
            }
        }

        /// <summary>
        /// The renderers dropped objects stand for: a renderer itself, a GameObject's own renderer, or, for a GameObject
        /// with none such as an outfit's root, every renderer under it.
        /// </summary>
        static IEnumerable<Object> Renderers(Object[] dropped) => dropped.SelectMany(o =>
        {
            if (o is Renderer renderer) return IsMesh(renderer) ? new[] { renderer } : Array.Empty<Renderer>();
            if (!(o is GameObject go)) return Array.Empty<Renderer>();
            var own = go.GetComponents<Renderer>().Where(IsMesh).ToArray();
            return own.Length > 0 ? own : go.GetComponentsInChildren<Renderer>(true).Where(IsMesh).ToArray();
        });

        static bool IsMesh(Renderer renderer) => renderer is SkinnedMeshRenderer || renderer is MeshRenderer;

        /// <summary>Dropped materials, and every material of the renderers the other dropped objects stand for.</summary>
        static IEnumerable<Object> Materials(Object[] dropped) =>
            dropped.OfType<Material>().Cast<Object>()
                .Concat(Renderers(dropped).SelectMany(r => ((Renderer)r).sharedMaterials)).Where(m => m != null);

        /// <summary>
        /// A list of one-line entries under a header drawn by <paramref name="header"/>. The header's rect is widened to the
        /// entries' left edge past the drag handle's gutter, so its columns sit over the entries' columns.
        /// </summary>
        ReorderableList List(SerializedProperty property, Action<Rect> header) =>
            new ReorderableList(serializedObject, property, true, true, true, true)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 2f,
                drawHeaderCallback = rect =>
                {
                    rect.xMin += HandleGutter;
                    header(rect);
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    rect.y += 1f;
                    rect.height = EditorGUIUtility.singleLineHeight;
                    EditorGUI.PropertyField(rect, property.GetArrayElementAtIndex(index), GUIContent.none);
                },
                drawNoneElementCallback = rect => EditorGUI.LabelField(rect, L10n.G(InspectorKeys.ListEmpty)),
            };

        /// <summary>How much further right a list's entries start than its header, for the drag handle.</summary>
        const float HandleGutter = 14f;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            LanguageSwitcher.DrawImmediate();

            Header(_decimate);
            var decimate = _decimate.boolValue || _decimate.hasMultipleDifferentValues;
            if (decimate)
            {
                DrawTarget();
                Gap();
                DrawList(_rendererList, _renderers, _rendererDrop);
                Gap();
                DrawPreview();
            }

            Header(_removeHidden);
            var removeHidden = _removeHidden.boolValue || _removeHidden.hasMultipleDifferentValues;
            if (removeHidden)
            {
                Intro(InspectorKeys.AlwaysOnHelp);
                DrawList(_alwaysOnList, _alwaysOn, _alwaysOnDrop);
                DrawOutsideAvatar();
                DrawCheck();
                Gap();
                Intro(InspectorKeys.MaterialsHelp);
                DrawList(_materialList, _materials, _materialDrop);
            }

            DrawAdvanced(decimate, removeHidden);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// A warning naming the always-on entries that are not part of the avatar the component is on: a build matches the
        /// list against that avatar's renderers only, so such an entry covers nothing.
        /// </summary>
        void DrawOutsideAvatar()
        {
            if (serializedObject.isEditingMultipleObjects) return;
            var optimizer = (AvatarMeshOptimizer)target;
            var root = RuntimeUtil.FindAvatarInParents(optimizer.transform) ?? optimizer.transform.root;
            var outside = optimizer.alwaysOn.Where(r => r != null && !r.transform.IsChildOf(root))
                // Named with the root each comes from, since this avatar's own renderer usually has the same name.
                .Select(r => r.transform.root.name + "/" + r.name).ToList();
            if (outside.Count > 0) Warning(string.Format(L10n.Tr(InspectorKeys.AlwaysOnOutside), string.Join(", ", outside), root.name));
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
        /// The preview's switch, NDMF's own for this filter, then a quiet box with what the latest preview of this avatar
        /// reached and where its shape-key liveness came from. A failed preview is the one warning.
        /// </summary>
        void DrawPreview()
        {
            var on = DecimationPreview.Toggle.IsEnabled.Value;
            EditorGUI.BeginChangeCheck();
            on = GUILayout.Toggle(on, L10n.G(InspectorKeys.Preview), "Button");
            if (EditorGUI.EndChangeCheck()) DecimationPreview.Toggle.IsEnabled.Value = on;

            var status = targets.Length == 1 ? PreviewStatus.Of((AvatarMeshOptimizer)target) : null;
            if (on && status?.Failure != null)
            {
                Warning(string.Format(L10n.Tr(InspectorKeys.PreviewFailed), status.Failure));
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!on) EditorGUILayout.LabelField(L10n.Tr(InspectorKeys.PreviewOff), Styles.Body);
                else if (status == null) EditorGUILayout.LabelField(L10n.Tr(InspectorKeys.PreviewPending), Styles.Body);
                else
                {
                    EditorGUILayout.LabelField(string.Format(L10n.Tr(InspectorKeys.PreviewResult), status.TrianglesBefore.ToString("N0"),
                        status.TrianglesAfter.ToString("N0"), status.ErrorLevelMm.ToString("0.00")), Styles.Body);
                    EditorGUILayout.LabelField(L10n.Tr(status.Liveness == PreviewLiveness.Descriptor
                        ? InspectorKeys.PreviewNoteDescriptor
                        : InspectorKeys.PreviewNoteEveryKeyLive), Styles.Note);
                }
            }
        }

        /// <summary>
        /// The settings most avatars leave alone, last and below a rule, collapsed by default; the one group drawn indented.
        /// It holds only the fields of the features that are on, and is not drawn when both are off.
        /// </summary>
        void DrawAdvanced(bool decimate, bool removeHidden)
        {
            if (!decimate && !removeHidden) return;
            EditorGUILayout.Space(6f);
            Rule();
            var open = EditorGUILayout.Foldout(SessionState.GetBool(AdvancedOpen, false), L10n.G(InspectorKeys.Advanced), true);
            SessionState.SetBool(AdvancedOpen, open);
            if (!open) return;

            var fields = new List<SerializedProperty>();
            if (decimate) fields.AddRange(new[] { _faceFloor, _lockFingers, _fingerWeight });
            if (removeHidden) fields.Add(_outlines);

            EditorGUI.indentLevel++;
            // The indent eats into the label column, so widen it to the longest label, up to a little over half the width.
            var labelWidth = EditorGUIUtility.labelWidth;
            var needed = fields.Max(p => EditorStyles.label.CalcSize(Label(p)).x) + 15f * EditorGUI.indentLevel + 8f;
            EditorGUIUtility.labelWidth = Mathf.Max(labelWidth, Mathf.Min(needed, EditorGUIUtility.currentViewWidth * 0.55f));
            if (decimate)
            {
                Percent(_faceFloor);
                Field(_lockFingers);
                using (new EditorGUI.DisabledScope(!_lockFingers.boolValue && !_lockFingers.hasMultipleDifferentValues))
                    Field(_fingerWeight);
            }
            if (removeHidden) Field(_outlines);
            EditorGUIUtility.labelWidth = labelWidth;
            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// A 0 to 1 share drawn as a whole-percent slider with a % sign after it. The stored value is written only when the
        /// slider is moved, so a value between whole percents survives being looked at.
        /// </summary>
        static void Percent(SerializedProperty property)
        {
            var rect = EditorGUILayout.GetControlRect();
            using (var scope = new EditorGUI.PropertyScope(rect, Label(property), property))
            {
                var sign = new GUIContent("%");
                var signWidth = EditorStyles.label.CalcSize(sign).x;
                var slider = new Rect(rect.x, rect.y, rect.width - signWidth - 2f, rect.height);
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                var percent = EditorGUI.IntSlider(slider, scope.content, Mathf.RoundToInt(property.floatValue * 100f), 0, 100);
                if (EditorGUI.EndChangeCheck()) property.floatValue = percent / 100f;
                EditorGUI.showMixedValue = false;
                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                EditorGUI.LabelField(new Rect(slider.xMax + 2f, rect.y, signWidth, rect.height), sign);
                EditorGUI.indentLevel = indent;
            }
        }

        /// <summary>
        /// The check button, then one line per material of the always-on renderers and of the overrides: what Automatic
        /// reads it as, and a button that overrides it to the opposite. Checking adds nothing to the overrides list; only
        /// the button beside a misread material does, and it is disabled once the material is listed.
        /// </summary>
        void DrawCheck()
        {
            using (new EditorGUI.DisabledScope(targets.Length != 1))
                if (GUILayout.Button(L10n.G(InspectorKeys.Check)))
                {
                    serializedObject.ApplyModifiedProperties();
                    CheckAlwaysOn();
                    // The progress bar broke this event's layout, so it ends here and the next one draws the verdicts.
                    GUIUtility.ExitGUI();
                }
            if (_checked == null || targets.Length != 1) return;

            var shown = _checked.Where(m => m != null).ToList();
            if (shown.Count == 0) return;
            var sets = new[] { OcclusionMode.ForceOpaque, OcclusionMode.NeverOccludes }.Select(SetLabel).ToArray();
            // Sized to the verdicts alone: the header runs on over the buttons' column, which has none, so a long header
            // does not take width from the material names.
            var verdictWidth = Columns.Width(EditorStyles.label, 60f,
                new[] { InspectorKeys.VerdictOpaque, InspectorKeys.VerdictSeeThrough, InspectorKeys.VerdictUnchecked }
                    .Select(L10n.Tr).Append(string.Format(L10n.Tr(InspectorKeys.VerdictByAlpha), 100)).ToArray());
            var buttonWidth = Columns.Width(GUI.skin.button, 60f, sets.Select(s => s.text).ToArray());

            // Drawn in the lists' own header and box, so the table reads as one more list, only without reordering.
            const float Pad = 6f;
            var line = EditorGUIUtility.singleLineHeight + 2f;
            var headerRect = GUILayoutUtility.GetRect(0f, line + 2f, GUILayout.ExpandWidth(true));
            var bodyRect = GUILayoutUtility.GetRect(0f, shown.Count * line + 8f, GUILayout.ExpandWidth(true));
            var behaviours = ReorderableList.defaultBehaviours;
            if (Event.current.type == EventType.Repaint && behaviours != null)
            {
                behaviours.DrawHeaderBackground(headerRect);
                behaviours.boxBackground.Draw(bodyRect, false, false, false, false);
            }

            var header = Columns.Split(new Rect(headerRect.x + Pad, headerRect.y + 1f, headerRect.width - 2f * Pad, EditorGUIUtility.singleLineHeight),
                verdictWidth, buttonWidth);
            EditorGUI.LabelField(header[0], L10n.G("MaterialOcclusion:" + nameof(MaterialOcclusion.material)));
            EditorGUI.LabelField(new Rect(header[1].x, header[1].y, header[2].xMax - header[1].x, header[1].height), L10n.G(InspectorKeys.Verdict));
            for (var i = 0; i < shown.Count; i++)
            {
                var material = shown[i];
                var row = Columns.Split(new Rect(bodyRect.x + Pad, bodyRect.y + 4f + i * line, bodyRect.width - 2f * Pad, EditorGUIUtility.singleLineHeight),
                    verdictWidth, buttonWidth);
                // A label rather than a disabled object field: full-strength text, no picker eating the name's width.
                // The icon drawn apart, since a label drops its image once its text is clipped.
                var icon = new Rect(row[0].x, row[0].y + 1f, 16f, 16f);
                GUI.DrawTexture(icon, AssetPreview.GetMiniTypeThumbnail(typeof(Material)), ScaleMode.ScaleToFit);
                EditorGUI.LabelField(new Rect(icon.xMax + 2f, row[0].y, Mathf.Max(0f, row[0].width - 18f), row[0].height),
                    new GUIContent(material.name, material.name));
                if (GUI.Button(row[0], GUIContent.none, GUIStyle.none)) EditorGUIUtility.PingObject(material);
                EditorGUI.LabelField(row[1], MaterialVerdicts.Describe(material));
                var known = MaterialVerdicts.TryGet(material, out var verdict);
                var opposite = MaterialVerdicts.Opposite(verdict.Kind);
                using (new EditorGUI.DisabledScope(!known || IsListed(material)))
                    if (GUI.Button(row[2], SetLabel(opposite))) AddOverride(material, opposite);
            }
        }

        static GUIContent SetLabel(OcclusionMode mode) =>
            new GUIContent(string.Format(L10n.Tr(InspectorKeys.Override), L10n.Tr("OcclusionMode:" + mode)), L10n.Tooltip(InspectorKeys.Override));

        /// <summary>The materials last checked, always-on ones first in list order then the overridden ones, or null before the first check.</summary>
        List<Material> _checked;

        /// <summary>Reads what Automatic decides for every always-on material and every overridden one.</summary>
        void CheckAlwaysOn()
        {
            var optimizer = (AvatarMeshOptimizer)target;
            _checked = optimizer.alwaysOn.Where(r => r != null).SelectMany(r => r.sharedMaterials)
                .Concat(optimizer.materials.Where(o => o != null).Select(o => o.material))
                .Where(m => m != null).Distinct().ToList();
            var title = L10n.Tr(InspectorKeys.Check);
            try
            {
                for (var i = 0; i < _checked.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(title, _checked[i].name, (float)i / _checked.Count);
                    MaterialVerdicts.Check(_checked[i]);
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

        /// <summary>
        /// A list as one-line entries under its column header, taking objects dropped anywhere on it. With several components
        /// selected Unity's own array field draws it instead, since a reorderable list edits one array; its entries still
        /// draw one line each.
        /// </summary>
        void DrawList(ReorderableList list, SerializedProperty property, Drop drop)
        {
            if (serializedObject.isEditingMultipleObjects)
            {
                EditorGUILayout.PropertyField(property, Label(property), true);
                return;
            }
            // The rect is the one the layout pass gave the list, so a drop is taken before an entry's field can take only
            // its first object.
            var rect = EditorGUILayout.BeginVertical();
            AcceptDrop(rect, property, drop);
            list.DoLayoutList();
            EditorGUILayout.EndVertical();
        }

        /// <summary>Takes a drag over the list when anything dragged resolves to an entry, and appends on the drop.</summary>
        static void AcceptDrop(Rect rect, SerializedProperty property, Drop drop)
        {
            var e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform || !rect.Contains(e.mousePosition)) return;
            if (!drop.Resolve(DragAndDrop.objectReferences).Any()) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                Append(property, drop, DragAndDrop.objectReferences);
            }
            e.Use();
        }

        /// <summary>Appends what the dropped objects resolve to, each once and only if the list does not hold it already.</summary>
        static void Append(SerializedProperty property, Drop drop, Object[] dropped)
        {
            var held = new HashSet<Object>();
            for (var i = 0; i < property.arraySize; i++) held.Add(Reference(property.GetArrayElementAtIndex(i), drop).objectReferenceValue);
            foreach (var o in drop.Resolve(dropped).Where(held.Add))
            {
                property.arraySize++;
                var entry = property.GetArrayElementAtIndex(property.arraySize - 1);
                Reference(entry, drop).objectReferenceValue = o;
                drop.Reset?.Invoke(entry);
            }
        }

        static SerializedProperty Reference(SerializedProperty entry, Drop drop) =>
            drop.Field == null ? entry : entry.FindPropertyRelative(drop.Field);

        /// <summary>A feature's toggle drawn as its section's header: a rule above, then the toggle in bold with the box on the left.</summary>
        static void Header(SerializedProperty property)
        {
            EditorGUILayout.Space(6f);
            Rule();
            var rect = EditorGUILayout.GetControlRect();
            using (var scope = new EditorGUI.PropertyScope(rect, Label(property), property))
            {
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                var value = EditorGUI.ToggleLeft(rect, scope.content, property.boolValue, EditorStyles.boldLabel);
                if (EditorGUI.EndChangeCheck()) property.boolValue = value;
                EditorGUI.showMixedValue = false;
            }
        }

        /// <summary>The thin line above a section's header, the separator Limitex draws.</summary>
        static void Rule()
        {
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 1f), Styles.Rule);
            EditorGUILayout.Space(2f);
        }

        /// <summary>What a list does and when to use it, in body-size text above it, where it reads without hovering.</summary>
        static void Intro(string key) => EditorGUILayout.LabelField(L10n.Tr(key), Styles.Body);

        static void Warning(string text) =>
            EditorGUILayout.LabelField(new GUIContent(text, EditorGUIUtility.IconContent("console.warnicon").image), Styles.Warning);

        static void Gap() => EditorGUILayout.Space(4f);

        static void DelayedFloat(SerializedProperty property)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.DelayedFloatField(property, Label(property));
            if (EditorGUI.EndChangeCheck() && !(property.floatValue >= 0f)) property.floatValue = 0f;
        }

        static void Field(SerializedProperty property) => EditorGUILayout.PropertyField(property, Label(property));

        /// <summary>A component field's label and tooltip, under its key <c>MeshOptimizer:&lt;field&gt;</c>.</summary>
        static GUIContent Label(SerializedProperty property) => L10n.G("MeshOptimizer:" + property.name);

        /// <summary>Body-size styles: the Inspector's help box draws its text smaller than a field's label.</summary>
        static class Styles
        {
            public static readonly GUIStyle Body = new GUIStyle(EditorStyles.wordWrappedLabel);

            public static readonly Color Rule = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            public static readonly GUIStyle Note = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                normal = { textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor },
            };

            public static readonly GUIStyle Warning = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = EditorStyles.label.fontSize,
            };
        }
    }
}
