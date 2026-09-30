using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using Ryan6Vrc.MeshOptimizer.Inspector;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;
using Object = UnityEngine.Object;

namespace Ryan6Vrc.MeshOptimizer.Preview
{
    /// <summary>What the inspector shows of an avatar's preview.</summary>
    internal sealed class PreviewStatus
    {
        public int TrianglesBefore;
        public int TrianglesAfter;
        /// <summary>The error level the cut reached, in millimetres.</summary>
        public float ErrorLevelMm;
        public PreviewLiveness Liveness;
        /// <summary>Why the preview failed, or null.</summary>
        public string Failure;

        static readonly Dictionary<AvatarMeshOptimizer, PreviewStatus> Latest = new Dictionary<AvatarMeshOptimizer, PreviewStatus>();

        /// <summary>The latest preview of the avatar the component is on, or null when none stands.</summary>
        public static PreviewStatus Of(AvatarMeshOptimizer component) =>
            component != null && Latest.TryGetValue(component, out var status) ? status : null;

        internal static void Set(AvatarMeshOptimizer component, PreviewStatus status)
        {
            Latest[component] = status;
            InternalEditorUtility.RepaintAllViews();
        }

        internal static void Clear(AvatarMeshOptimizer component, PreviewStatus status)
        {
            if (!ReferenceEquals(component, null) && Latest.TryGetValue(component, out var current) && current == status) Latest.Remove(component);
        }
    }

    /// <summary>
    /// Shows decimation in edit mode through NDMF's preview: each avatar with the component and decimation on is one render
    /// group, since the target is spent over every renderer at once. The first preview prepares the avatar (extraction,
    /// clearance and recording, without occlusion; <see cref="PreviewInput"/>), which is what costs; a change to the target
    /// alone cuts the same prepared avatar again. Any other change to the component, or a renderer's mesh, prepares it anew.
    /// NDMF calls a filter only to preview, so none of this runs in a build.
    /// </summary>
    internal sealed class DecimationPreview : IRenderFilter
    {
        /// <summary>Off until the user turns it on: a first preview holds the Editor while it records.</summary>
        public static readonly TogglablePreviewNode Toggle = TogglablePreviewNode.Create(
            () => L10n.Tr(InspectorKeys.Preview), "com.ryan6vrc.mesh-optimizer.decimation-preview", false);

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return Toggle;
        }

        public bool IsEnabled(ComputeContext context) => context.Observe(Toggle.IsEnabled);

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (var root in context.GetAvatarRoots())
            {
                var component = context.GetComponentsInChildren<AvatarMeshOptimizer>(root, true).FirstOrDefault();
                if (component == null || !context.Observe(component, c => c.decimate)) continue;
                var renderers = context.GetComponentsInChildren<Renderer>(root, true)
                    .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
                if (renderers.Count > 0) groups.Add(RenderGroup.For(renderers));
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var root = context.GetAvatarRoot(group.Renderers[0].gameObject).transform;
            var component = root.GetComponentsInChildren<AvatarMeshOptimizer>(true).First();
            // Refresh is handed the same originals in the same order, so both observe one list.
            var originals = proxyPairs.Select(p => p.Item1).ToList();
            var key = Keys.Observe(context, component, originals);
            Shared shared;
            try
            {
                shared = Prepare(root, component, originals.Count);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                var failed = new PreviewStatus { Failure = e.Message };
                PreviewStatus.Set(component, failed);
                return Task.FromResult<IRenderFilterNode>(new Node(component, null, key, failed));
            }
            return Task.FromResult<IRenderFilterNode>(Node.Cut(component, shared, key));
        }

        /// <summary>Prepares the scene avatar as the preview reads it, behind a progress bar, since this is the step that holds the Editor.</summary>
        static Shared Prepare(Transform root, AvatarMeshOptimizer component, int renderers)
        {
            var title = L10n.Tr(InspectorKeys.PreviewProgress);
            try
            {
                EditorUtility.DisplayProgressBar(title, root.name, 0f);
                var input = PreviewInput.For(root, component, out var liveness);
                var stages = 0;
                input.BeforeStage = (renderer, stage) =>
                    EditorUtility.DisplayProgressBar(title, string.Format(L10n.Tr(InspectorKeys.PreviewProgressInfo), renderer.name),
                        Mathf.Clamp01(++stages / (2f * Math.Max(renderers, 1))));
                return new Shared(OptimizerPipeline.Prepare(input), liveness);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>One prepared avatar, shared by every node cut from it; disposed when the last lets go.</summary>
        sealed class Shared
        {
            public readonly PreparedAvatar Prepared;
            public readonly PreviewLiveness Liveness;
            int _holders = 1;

            public Shared(PreparedAvatar prepared, PreviewLiveness liveness)
            {
                Prepared = prepared;
                Liveness = liveness;
            }

            public Shared Hold()
            {
                _holders++;
                return this;
            }

            public void Release()
            {
                if (--_holders == 0) Prepared.Dispose();
            }
        }

        /// <summary>
        /// What a node was made from, observed through the compute context so a change to either invalidates it: the
        /// preparing key (every setting a prepare reads, and each renderer's mesh), and the cut key (the target and its values).
        /// </summary>
        readonly struct Keys
        {
            public readonly string Preparing;
            public readonly (TargetKind, float, int, float) Cutting;

            Keys(string preparing, (TargetKind, float, int, float) cutting)
            {
                Preparing = preparing;
                Cutting = cutting;
            }

            public static Keys Observe(ComputeContext context, AvatarMeshOptimizer component, IEnumerable<Renderer> renderers)
            {
                var preparing = context.Observe(component, c => string.Join("|",
                    c.decimate, F(c.faceFloor), c.lockFingers, F(c.fingerWeight),
                    string.Join(";", c.renderers.Where(s => s != null).Select(s =>
                        $"{(s.renderer != null ? s.renderer.GetInstanceID() : 0)},{s.excludeFromDecimation},{F(s.protection)}"))));
                var meshes = renderers.Select(r => r is SkinnedMeshRenderer skinned
                    ? context.Observe(skinned, s => s.sharedMesh)
                    : r.TryGetComponent<MeshFilter>(out var filter) ? context.Observe(filter, f => f.sharedMesh) : null);
                preparing += "|" + string.Join(",", meshes.Select(m => m != null ? m.GetInstanceID() : 0));
                var cutting = context.Observe(component, c => (c.target, c.tolerance, c.triangleBudget, c.maxError));
                return new Keys(preparing, cutting);
            }

            static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>One cut of a prepared avatar: its meshes, put on the proxies each frame, and destroyed with it.</summary>
        sealed class Node : IRenderFilterNode
        {
            readonly AvatarMeshOptimizer _component;
            readonly Shared _shared;
            readonly Keys _keys;
            readonly PreviewStatus _status;
            readonly Dictionary<Renderer, Mesh> _meshes = new Dictionary<Renderer, Mesh>();

            public RenderAspects WhatChanged => RenderAspects.Mesh;

            public Node(AvatarMeshOptimizer component, Shared shared, Keys keys, PreviewStatus status)
            {
                _component = component;
                _shared = shared;
                _keys = keys;
                _status = status;
            }

            /// <summary>Cuts the prepared avatar at the component's target as it stands, and records what the cut reached.</summary>
            public static Node Cut(AvatarMeshOptimizer component, Shared shared, Keys keys)
            {
                PreviewStatus status;
                OptimizationReport report = null;
                try
                {
                    report = OptimizerPipeline.Cut(shared.Prepared);
                    status = new PreviewStatus
                    {
                        TrianglesBefore = report.TrianglesBefore,
                        TrianglesAfter = report.TrianglesAfter,
                        ErrorLevelMm = report.ErrorLevel * 1000f,
                        Liveness = shared.Liveness,
                    };
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    status = new PreviewStatus { Failure = e.Message, Liveness = shared.Liveness };
                }
                var node = new Node(component, shared, keys, status);
                if (report != null)
                    foreach (var r in report.Renderers)
                        if (r.OutputMesh != null && r.Renderer != null) node._meshes[r.Renderer] = r.OutputMesh;
                PreviewStatus.Set(component, status);
                return node;
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context, RenderAspects updatedAspects)
            {
                if (_shared == null) return Task.FromResult<IRenderFilterNode>(null);
                var keys = Keys.Observe(context, _component, proxyPairs.Select(p => p.Item1).ToList());
                if (keys.Preparing != _keys.Preparing) return Task.FromResult<IRenderFilterNode>(null);
                if (keys.Cutting.Equals(_keys.Cutting)) return Task.FromResult<IRenderFilterNode>(this);
                return Task.FromResult<IRenderFilterNode>(Cut(_component, _shared.Hold(), keys));
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (!_meshes.TryGetValue(original, out var mesh)) return;
                switch (proxy)
                {
                    case SkinnedMeshRenderer skinned:
                        skinned.sharedMesh = mesh;
                        break;
                    case MeshRenderer _ when proxy.TryGetComponent<MeshFilter>(out var filter):
                        filter.sharedMesh = mesh;
                        break;
                }
            }

            public void Dispose()
            {
                foreach (var mesh in _meshes.Values) Object.DestroyImmediate(mesh);
                _meshes.Clear();
                _shared?.Release();
                PreviewStatus.Clear(_component, _status);
            }
        }
    }
}
