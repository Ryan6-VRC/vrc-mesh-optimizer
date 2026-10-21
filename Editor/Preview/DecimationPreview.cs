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
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
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
    /// Shows decimation in edit mode through NDMF's preview, for the avatars chosen by their component's inspector
    /// (<see cref="Show"/>) and no others: each is one render group, since the target is spent over every renderer at
    /// once. The first preview of an avatar prepares it (extraction, clearance and recording, without occlusion;
    /// <see cref="PreviewInput"/>), which is what costs. The prepared avatar is then kept (<see cref="Kept"/>), so
    /// switching the preview off and on, or a change to the target or to a renderer's protection, only cuts it again.
    /// A change to any other setting preparing reads (<see cref="Keys"/>), to a renderer or to its mesh prepares it anew. NDMF calls a filter only to
    /// preview, so none of this runs in a build.
    /// </summary>
    internal sealed class DecimationPreview : IRenderFilter
    {
        /// <summary>
        /// NDMF's switch for the whole filter: off hides every chosen avatar's preview, and on brings them back from what
        /// was kept. Off until the user turns it on, and off again whenever a scene opens, the Editor reloads or play mode
        /// starts: a first preview holds the Editor while it records, so it never starts on its own. Hence no saved state,
        /// which NDMF keeps under a qualified name and would restore.
        /// </summary>
        public static readonly TogglablePreviewNode Toggle = TogglablePreviewNode.Create(
            () => L10n.Tr(InspectorKeys.Preview), initialState: false);

        /// <summary>
        /// The avatars the preview shows, by their component. A scene of many avatars would otherwise prepare every one at
        /// the first switch-on, to look at one.
        /// </summary>
        static readonly PublishedValue<ImmutableHashSet<AvatarMeshOptimizer>> Chosen =
            new PublishedValue<ImmutableHashSet<AvatarMeshOptimizer>>(ImmutableHashSet<AvatarMeshOptimizer>.Empty, "MeshOptimizer/PreviewedAvatars");

        /// <summary>Whether the avatar the component is on is being previewed: chosen, with the filter on.</summary>
        public static bool Shows(AvatarMeshOptimizer component) => Toggle.IsEnabled.Value && Chosen.Value.Contains(component);

        /// <summary>
        /// Chooses the component's avatar for the preview, or drops it. Choosing one switches the filter on, which also
        /// brings back every other avatar still chosen.
        /// </summary>
        public static void Show(AvatarMeshOptimizer component, bool on)
        {
            Chosen.Value = on ? Chosen.Value.Add(component) : Chosen.Value.Remove(component);
            if (on) Toggle.IsEnabled.Value = true;
        }

        /// <summary>
        /// Switches the preview off, forgets which avatars were chosen and frees every prepared avatar no node still shows.
        /// </summary>
        internal static void Reset()
        {
            Toggle.IsEnabled.Value = false;
            Chosen.Value = ImmutableHashSet<AvatarMeshOptimizer>.Empty;
            FreeKept();
        }

        /// <summary>Forgets every kept prepared avatar; each is freed once no node holds it.</summary>
        static void FreeKept()
        {
            foreach (var component in Kept.Keys.ToList()) Forget(component);
        }

        /// <summary>
        /// Resets the preview (<see cref="Reset"/>) when any scene but NDMF's preview scene opens, and as play mode starts.
        /// NDMF reopens its own hidden preview scene around play mode and reloads, which raises the same event; it learns
        /// that scene only once opening returns, so the check waits a tick to tell the two apart. Play mode resets too:
        /// where it starts without a reload the switch would otherwise still be on at the return to edit mode.
        /// </summary>
        [InitializeOnLoadMethod]
        static void ResetWhenASceneOpensOrPlayStarts()
        {
            EditorSceneManager.sceneOpened += (scene, mode) => EditorApplication.delayCall += () =>
            {
                if (!NDMFPreviewSceneManager.IsPreviewScene(scene)) Reset();
            };
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode) Reset();
            };
        }

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
                if (component == null || !context.Observe(Chosen, chosen => chosen.Contains(component))) continue;
                if (!context.Observe(component, c => c.decimate)) continue;
                var renderers = context.GetComponentsInChildren<Renderer>(root, true).Where(IsMesh).ToList();
                if (renderers.Count > 0) groups.Add(RenderGroup.For(renderers));
            }
            return groups.ToImmutable();
        }

        static bool IsMesh(Renderer renderer) => renderer is SkinnedMeshRenderer || renderer is MeshRenderer;

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var root = context.GetAvatarRoot(group.Renderers[0].gameObject).transform;
            var component = root.GetComponentsInChildren<AvatarMeshOptimizer>(true).First();
            // Refresh is handed the same originals in the same order, so both observe one list.
            var pairs = proxyPairs.ToList();
            var key = Keys.Observe(context, component, pairs);
            Shared shared;
            try
            {
                shared = PreparedFor(root, component, key.Settings, Proxies(pairs));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                var failed = new PreviewStatus { Failure = e.Message };
                PreviewStatus.Set(component, failed);
                return Task.FromResult<IRenderFilterNode>(new Node(component, null, key, failed));
            }
            return Task.FromResult<IRenderFilterNode>(Node.Cut(component, shared.Hold(), key));
        }

        /// <summary>
        /// The latest prepared avatar of each component, held here beside the nodes cut from it so that it outlives them:
        /// NDMF disposes every node when the preview is switched off, and switching it on again would otherwise record the
        /// avatar from nothing. One is freed when its avatar is prepared anew, when its component is gone, and by
        /// <see cref="Reset"/> and a domain reload.
        /// </summary>
        static readonly Dictionary<AvatarMeshOptimizer, Shared> Kept = new Dictionary<AvatarMeshOptimizer, Shared>();

        /// <summary>
        /// Each original's proxy. NDMF hands a filter each proxy as the filters before it in the pipeline left it, so its
        /// mesh and materials carry what they made of the renderer (a Modular Avatar deletion, say). Its bones are the
        /// original's, which NDMF copies onto it, unless such a filter swapped in bones of its own outside the avatar, as
        /// Modular Avatar's scale adjuster does; so preparing reads only the mesh and materials from it.
        /// </summary>
        static Dictionary<Renderer, Renderer> Proxies(IEnumerable<(Renderer, Renderer)> pairs)
        {
            var proxies = new Dictionary<Renderer, Renderer>();
            foreach (var (original, proxy) in pairs)
                if (original != null && proxy != null) proxies[original] = proxy;
            return proxies;
        }

        /// <summary>The avatar's kept prepared avatar where nothing it read has changed, otherwise a new one, kept in its place.</summary>
        static Shared PreparedFor(Transform root, AvatarMeshOptimizer component, string settings, Dictionary<Renderer, Renderer> proxies)
        {
            foreach (var gone in Kept.Keys.Where(c => c == null).ToList()) Forget(gone);
            var meshes = MeshesUnder(root, proxies);
            if (Kept.TryGetValue(component, out var kept) && kept.Fresh(settings, meshes)) return kept;
            Forget(component);
            // Watched from before preparing reads anything, so a change made while it records is not missed.
            var watch = Watch(root, proxies);
            return Kept[component] = new Shared(Prepare(root, component, proxies, out var liveness), liveness, settings, meshes, watch);
        }

        /// <summary>Drops the component's kept prepared avatar, freeing it once no node holds it.</summary>
        static void Forget(AvatarMeshOptimizer component)
        {
            if (!Kept.TryGetValue(component, out var kept)) return;
            Kept.Remove(component);
            kept.Release();
        }

        /// <summary>
        /// Every mesh and material preparing reads under the root, from the proxies, in hierarchy order, as one comparable
        /// value: a proxy's are an earlier filter's, whose change NDMF reports to no node of this filter while it is off.
        /// </summary>
        static string MeshesUnder(Transform root, IReadOnlyDictionary<Renderer, Renderer> proxies) =>
            string.Join(",", root.GetComponentsInChildren<Renderer>(true).Where(IsMesh).Select(r =>
            {
                var drawn = PipelineInput.DrawnBy(proxies, r);
                var mesh = RendererMesh(drawn);
                return (mesh != null ? mesh.GetInstanceID() : 0) + ":" + string.Join(":", drawn.sharedMaterials.Select(m => m != null ? m.GetInstanceID() : 0));
            }));

        /// <summary>The mesh a renderer draws, as <see cref="Extraction.RendererExtraction.MeshOf"/> reads it.</summary>
        static Mesh RendererMesh(Renderer renderer) => renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
            : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;

        /// <summary>
        /// A context NDMF invalidates when anything preparing read is changed: which renderers the avatar has, any of them
        /// (its shape-key weights, bones and materials), the mesh or a material read from its proxy, and the avatar
        /// descriptor the preview takes shape-key liveness from. It is what tells a kept prepared avatar from a stale one
        /// while no node is watching. The proxy itself is not watched, since NDMF writes it every frame.
        /// </summary>
        static ComputeContext Watch(Transform root, IReadOnlyDictionary<Renderer, Renderer> proxies)
        {
            var watch = new ComputeContext("Mesh Optimizer prepared avatar of " + root.name);
            foreach (var renderer in watch.GetComponentsInChildren<Renderer>(root.gameObject, true).Where(IsMesh))
            {
                watch.Observe(renderer);
                if (renderer.TryGetComponent<MeshFilter>(out var filter)) watch.Observe(filter);
                var drawn = PipelineInput.DrawnBy(proxies, renderer);
                var mesh = RendererMesh(drawn);
                if (mesh != null) watch.Observe(mesh);
                foreach (var material in drawn.sharedMaterials)
                    if (material != null) watch.Observe(material);
            }
            if (root.TryGetComponent<VRCAvatarDescriptor>(out var descriptor)) watch.Observe(descriptor);
            return watch;
        }

        /// <summary>Prepares the scene avatar as the preview reads it, behind a progress bar, since this is the step that holds the Editor.</summary>
        static PreparedAvatar Prepare(Transform root, AvatarMeshOptimizer component, IReadOnlyDictionary<Renderer, Renderer> proxies, out PreviewLiveness liveness)
        {
            var title = L10n.Tr(InspectorKeys.PreviewProgress);
            PipelineInput input = null;
            try
            {
                EditorUtility.DisplayProgressBar(title, root.name, 0f);
                input = PreviewInput.For(root, component, out liveness, proxies);
                var renderers = proxies.Count;
                var stages = 0;
                input.BeforeStage = (renderer, stage) =>
                    EditorUtility.DisplayProgressBar(title, string.Format(L10n.Tr(InspectorKeys.PreviewProgressInfo), renderer.name),
                        Mathf.Clamp01(++stages / (2f * Math.Max(renderers, 1))));
                return OptimizerPipeline.Prepare(input);
            }
            finally
            {
                // The prepared avatar keeps this input, and a cut calls BeforeStage as it writes each renderer: left set, every
                // cut would raise a progress bar that nothing clears.
                if (input != null) input.BeforeStage = null;
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// One prepared avatar, with what it was prepared from, shared by <see cref="Kept"/> and every node cut from it;
        /// disposed when the last lets go.
        /// </summary>
        sealed class Shared
        {
            public readonly PreparedAvatar Prepared;
            public readonly PreviewLiveness Liveness;
            readonly string _settings, _meshes;
            readonly ComputeContext _watch;
            bool _spoiled;
            int _holders = 1;

            public Shared(PreparedAvatar prepared, PreviewLiveness liveness, string settings, string meshes, ComputeContext watch)
            {
                Prepared = prepared;
                Liveness = liveness;
                _settings = settings;
                _meshes = meshes;
                _watch = watch;
            }

            /// <summary>Whether a preview from these settings and meshes can be cut from this, with nothing it read changed since.</summary>
            public bool Fresh(string settings, string meshes) =>
                !_spoiled && !_watch.IsInvalidated && _settings == settings && _meshes == meshes;

            /// <summary>Marks it stale, for a change NDMF reported to a node cut from it.</summary>
            public void Spoil() => _spoiled = true;

            /// <summary>Adds a holder; each hold is let go by one <see cref="Release"/>.</summary>
            public Shared Hold()
            {
                _holders++;
                return this;
            }

            /// <summary>Lets go of one hold, disposing the prepared avatar and its watch with the last.</summary>
            public void Release()
            {
                if (--_holders != 0) return;
                Prepared.Dispose();
                _watch.Invalidate();
            }
        }

        /// <summary>
        /// What a node was made from, observed through the compute context so a change to any invalidates it: the settings
        /// preparing reads, each renderer's mesh, and what a cut reads (the target, its values and each renderer's protection).
        /// The mesh recorded is the proxy's, which is what preparing reads, and the original's is the one observed: NDMF
        /// writes the proxy every frame, and reports an earlier filter's change to it as a changed aspect instead.
        /// </summary>
        readonly struct Keys
        {
            public readonly string Settings;
            public readonly string Meshes;
            public readonly (TargetKind, float, int, float, string) Cutting;

            Keys(string settings, string meshes, (TargetKind, float, int, float, string) cutting)
            {
                Settings = settings;
                Meshes = meshes;
                Cutting = cutting;
            }

            public static Keys Observe(ComputeContext context, AvatarMeshOptimizer component, IEnumerable<(Renderer, Renderer)> pairs)
            {
                var settings = context.Observe(component, c => string.Join("|",
                    c.decimate, F(c.faceFloor), c.lockFingers, F(c.fingerWeight), PerRenderer(c, s => s.excludeFromDecimation ? "x" : null)));
                var meshes = string.Join(",", pairs.Select(p =>
                {
                    var (r, proxy) = p;
                    if (r is SkinnedMeshRenderer skinned) context.Observe(skinned, s => s.sharedMesh);
                    else if (r.TryGetComponent<MeshFilter>(out var filter)) context.Observe(filter, f => f.sharedMesh);
                    var mesh = RendererMesh(proxy != null ? proxy : r);
                    return mesh != null ? mesh.GetInstanceID() : 0;
                }));
                var cutting = context.Observe(component, c => (c.target, c.tolerance, c.triangleBudget, c.maxError,
                    PerRenderer(c, s => s.protection != 1f ? F(s.protection) : null)));
                return new Keys(settings, meshes, cutting);
            }

            /// <summary>
            /// What the per-renderer entries come to for one of their fields, the last entry naming a renderer counting as
            /// the pipeline counts it, with the renderers at the field's neutral value (null) left out: an entry that
            /// changes nothing, as a renderer just added to the list is, is no change.
            /// </summary>
            static string PerRenderer(AvatarMeshOptimizer component, Func<RendererSettings, string> field)
            {
                var last = new SortedDictionary<int, string>();
                foreach (var s in component.renderers)
                    if (s != null && s.renderer != null) last[s.renderer.GetInstanceID()] = field(s);
                return string.Join(";", last.Where(e => e.Value != null).Select(e => e.Key + ":" + e.Value));
            }

            static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>The prepared avatar a node of this filter was cut from, or null; for tests of the node's lifecycle.</summary>
        internal static PreparedAvatar PreparedOf(IRenderFilterNode node) => (node as Node)?.Shared?.Prepared;

        /// <summary>
        /// One cut of a prepared avatar: its meshes, put on the proxies each frame, and destroyed with it. NDMF disposes no
        /// preview session before a domain reload, so every live node and every kept prepared avatar is let go then, or
        /// the meshes and the prepared avatars' native memory would outlive the domain that could free them.
        /// </summary>
        sealed class Node : IRenderFilterNode
        {
            static readonly HashSet<Node> Live = new HashSet<Node>();

            [InitializeOnLoadMethod]
            static void DisposeBeforeReload() => AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var node in Live.ToList()) node.Dispose();
                FreeKept();
            };

            readonly AvatarMeshOptimizer _component;
            readonly Shared _shared;
            readonly Keys _keys;
            readonly PreviewStatus _status;
            readonly Dictionary<Renderer, Mesh> _meshes = new Dictionary<Renderer, Mesh>();
            bool _disposed;

            public RenderAspects WhatChanged => RenderAspects.Mesh;

            public Shared Shared => _shared;

            public Node(AvatarMeshOptimizer component, Shared shared, Keys keys, PreviewStatus status)
            {
                _component = component;
                _shared = shared;
                _keys = keys;
                _status = status;
                Live.Add(this);
            }

            /// <summary>
            /// Cuts the prepared avatar at the component's target and protections as they stand, and records what the cut
            /// reached. The node takes over the caller's hold on <paramref name="shared"/>.
            /// </summary>
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
                    {
                        if (r.OutputMesh == null) continue;
                        // A renderer destroyed since preparing has no proxy to show its mesh on, so nothing else would free it.
                        if (r.Renderer != null) node._meshes[r.Renderer] = r.OutputMesh;
                        else Object.DestroyImmediate(r.OutputMesh);
                    }
                PreviewStatus.Set(component, status);
                return node;
            }

            /// <summary>
            /// A change to the target or to a protection cuts the same prepared avatar again. Anything NDMF reports changed
            /// on a renderer (its mesh, materials, textures, shape-key weights or bones) was read by preparing, as was every
            /// setting in <see cref="Keys.Settings"/>, so either prepares the avatar anew.
            /// </summary>
            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context, RenderAspects updatedAspects)
            {
                if (_shared == null) return Task.FromResult<IRenderFilterNode>(null);
                if (updatedAspects != 0)
                {
                    _shared.Spoil();
                    return Task.FromResult<IRenderFilterNode>(null);
                }
                var keys = Keys.Observe(context, _component, proxyPairs.ToList());
                if (keys.Settings != _keys.Settings || keys.Meshes != _keys.Meshes) return Task.FromResult<IRenderFilterNode>(null);
                if (keys.Cutting.Equals(_keys.Cutting)) return Task.FromResult<IRenderFilterNode>(this);
                return Task.FromResult<IRenderFilterNode>(Cut(_component, _shared.Hold(), keys));
            }

            /// <summary>Puts the cut mesh of <paramref name="original"/> on its proxy, which NDMF resets every frame.</summary>
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
                if (_disposed) return;
                _disposed = true;
                Live.Remove(this);
                foreach (var mesh in _meshes.Values) Object.DestroyImmediate(mesh);
                _meshes.Clear();
                _shared?.Release();
                PreviewStatus.Clear(_component, _status);
            }
        }
    }
}
