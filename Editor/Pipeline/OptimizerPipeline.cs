using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf.runtime;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Decimation;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Collections;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>
    /// Optimizes an avatar's meshes: extracts every renderer, removes the triangles an always-on renderer hides,
    /// decimates what remains to the avatar-wide target and writes new meshes, in that order, so the budget is spent
    /// only on what can be seen. It reads the hierarchy and returns new meshes in a report; it assigns nothing and
    /// never touches a source mesh. <see cref="Apply"/> puts the meshes on the renderers.
    ///
    /// It runs as two calls. <see cref="Prepare"/> does everything up to a recorded collapse sequence per renderer, which
    /// is the costly part; <see cref="Cut"/> spends the target over those sequences and writes, and can be called again on
    /// the same <see cref="PreparedAvatar"/>.
    /// </summary>
    internal static partial class OptimizerPipeline
    {
        /// <summary>A renderer's protection maps to an allocation weight no lower than this, which the allocator needs above 0.</summary>
        const float MinWeight = 1e-3f;

        /// <summary>
        /// Surveys, extracts, removes hidden triangles and records every decimated renderer's collapse sequence. The
        /// caller owns the result and disposes it; if this throws, everything it made is already disposed.
        /// </summary>
        public static PreparedAvatar Prepare(PipelineInput input)
        {
            if (input?.Root == null) throw new ArgumentException("the pipeline needs an avatar root");
            if (input.Settings == null) throw new ArgumentException("the pipeline needs the component's settings");
            var settings = input.Settings;
            var total = Stopwatch.StartNew();
            var prepared = new PreparedAvatar(input);
            prepared.Times.Liveness = input.LivenessMs;
            try
            {
                var jobs = prepared.Renderers;
                var bones = new AvatarBones(input.Root);
                var stage = Stopwatch.StartNew();
                foreach (var renderer in input.Root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    jobs.Add(Survey(renderer, input, prepared));
                }
                Configure(jobs, input, prepared);
                foreach (var job in jobs)
                    if (job.Skip == SkipReason.None) Extract(job, input, bones, prepared);
                prepared.Times.Extraction = stage.Elapsed.TotalMilliseconds;
                prepared.UnmatchedMaterialOverrides.AddRange(new OccluderMaterials(settings.materials).Unmatched(jobs.Select(j => j.Renderer)));
                // A never-occludes override that matched nothing left a surface the user said can vanish free to hide others.
                foreach (var o in settings.materials)
                    if (o != null && o.material != null && o.mode == OcclusionMode.NeverOccludes && prepared.UnmatchedMaterialOverrides.Contains(o.material))
                        prepared.Diagnostics.Add(new Diagnostic(DiagnosticKeys.NeverOccludesUnmatched, true, o.material));

                var live = prepared.Extracted;
                live.AddRange(jobs.Where(j => j.Extracted));
                stage.Restart();
                input.BeforeStep?.Invoke(AvatarStep.Occlusion);
                if (settings.removeHiddenTriangles) RemoveHidden(live, input, bones, prepared);
                prepared.Times.Occlusion = stage.Elapsed.TotalMilliseconds;

                if (settings.decimate) Record(live, jobs, input, bones, prepared);
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
            prepared.PrepareMs = total.Elapsed.TotalMilliseconds;
            return prepared;
        }

        static PreparedRenderer Survey(Renderer renderer, PipelineInput input, PreparedAvatar prepared)
        {
            var root = input.Root;
            var mesh = RendererExtraction.MeshOf(renderer);
            var r = new PreparedRenderer
            {
                Renderer = renderer,
                Source = mesh,
                Path = RuntimeUtil.RelativePath(root.gameObject, renderer.gameObject) ?? renderer.name,
            };
            if (mesh == null)
            {
                r.Skip = SkipReason.NoMesh;
                return r;
            }
            r.TrianglesBefore = MeshTriangles.Count(mesh);
            if (renderer.GetComponent<Cloth>() != null)
            {
                r.Skip = SkipReason.Cloth;
                prepared.Diagnostics.Add(new Diagnostic(DiagnosticKeys.Cloth, true, renderer));
            }
            else if (!CanRead(mesh, Application.isPlaying))
            {
                r.Skip = SkipReason.NotReadable;
                prepared.Diagnostics.Add(new Diagnostic(DiagnosticKeys.NotReadable, true, renderer, mesh.name));
            }
            else if (r.TrianglesBefore == 0)
            {
                r.Skip = SkipReason.NoTriangles;
            }
            else if (DuplicateShapeKey(mesh) is string duplicate)
            {
                r.Skip = SkipReason.DuplicateShapeKey;
                prepared.Diagnostics.Add(new Diagnostic(DiagnosticKeys.DuplicateShapeKey, true, renderer, duplicate));
            }
            else if (renderer is SkinnedMeshRenderer skinned)
            {
                r.Liveness = LivenessOf(skinned, input);
            }
            return r;
        }

        /// <summary>The liveness the caller gave a renderer, or, when it gave none, every key live from where it is held to full weight.</summary>
        static ShapeKeyLiveness[] LivenessOf(SkinnedMeshRenderer skinned, PipelineInput input)
        {
            if (input.Liveness == null || !input.Liveness.TryGetValue(skinned, out var liveness) || liveness == null)
                liveness = LivenessSources.EveryKeyLive(skinned, input.Root);
            return liveness;
        }

        /// <summary>
        /// The first shape key name the mesh gives two keys, or null. Unity addresses a key by name when a mesh is written,
        /// so the second key's frames would land on the first, and a renderer's weights, held by index, on the wrong keys.
        /// </summary>
        static string DuplicateShapeKey(Mesh mesh)
        {
            var names = new HashSet<string>();
            for (var k = 0; k < mesh.blendShapeCount; k++)
                if (!names.Add(mesh.GetBlendShapeName(k))) return mesh.GetBlendShapeName(k);
            return null;
        }

        /// <summary>
        /// Whether the mesh's data can be read: always with Read/Write on; without it, only outside play mode, where
        /// the Editor keeps a copy (an upload build, or a manual bake) and not under NDMF's apply-on-play.
        /// </summary>
        internal static bool CanRead(Mesh mesh, bool playing) => mesh.isReadable || !playing;

        /// <summary>Records a renderer's failure at a stage: it is left as it was, and named.</summary>
        static void Fail(Renderer renderer, List<Diagnostic> diagnostics, PipelineStage stage, Exception e, out SkipReason skip, out string detail)
        {
            skip = SkipReason.Failed;
            detail = $"{stage}: {e.Message}";
            NameFailure(renderer, diagnostics, stage, e);
        }

        /// <summary>Names a renderer's failure at a stage, and logs it.</summary>
        static void NameFailure(Renderer renderer, List<Diagnostic> diagnostics, PipelineStage stage, Exception e)
        {
            diagnostics.Add(new Diagnostic(DiagnosticKeys.RendererFailed, true, renderer, stage.ToString(), e.Message));
            Debug.LogException(e);
        }

        /// <summary>
        /// The avatar's linked configurations, from its links: a link at one normalised weight is one state of the avatar, in
        /// which every member key stands at that fraction of its <see cref="ShapeKeyClassifier.FullScale"/> and every other
        /// key where it is held. The weights are the union, normalised, of what <see cref="MeshExtractor.Configurations"/>
        /// gives each member alone, so each member is evaluated at every corner of its own path; configurations come only
        /// from links, never from a product of them. Each renderer takes one frame per configuration it has a member in.
        /// A member the pass does not evaluate as live, or on a renderer it does not extract, is left out; a link left with
        /// fewer than two members, or whose members no longer stand at one normalised weight, moves its keys alone.
        /// </summary>
        static void Configure(List<PreparedRenderer> jobs, PipelineInput input, PreparedAvatar prepared)
        {
            var byRenderer = new Dictionary<Renderer, PreparedRenderer>();
            foreach (var job in jobs)
                if (job.Skip == SkipReason.None && job.Liveness != null) byRenderer[job.Renderer] = job;
            bool Evaluated(LinkedKey m, out PreparedRenderer job)
            {
                job = null;
                if (m.Renderer == null || !byRenderer.TryGetValue(m.Renderer, out job)) return false;
                var liveness = job.Liveness;
                return m.Key >= 0 && m.Key < liveness.Length && m.Key < job.Source.blendShapeCount && liveness[m.Key].State == ShapeKeyState.Live;
            }
            void Refused(LinkRefusal reason, int keys)
            {
                if (keys == 0) return;
                prepared.LinkRefusals.TryGetValue(reason, out var n);
                prepared.LinkRefusals[reason] = n + keys;
            }
            if (input.Unlinked != null)
                foreach (var (key, reason) in input.Unlinked)
                    if (Evaluated(key, out _)) Refused(reason, 1);
            if (input.Links == null) return;

            var next = 1;
            foreach (var link in input.Links)
            {
                var members = new List<(PreparedRenderer job, int key, float full)>();
                foreach (var m in link ?? Array.Empty<LinkedKey>())
                {
                    if (!Evaluated(m, out var job)) continue;
                    if (members.Any(x => x.job == job && x.key == m.Key)) continue;
                    var frameWeights = ShapeKeyFrames.WeightsOf(job.Source, m.Key);
                    members.Add((job, m.Key, ShapeKeyClassifier.FullScale(frameWeights[frameWeights.Length - 1])));
                }
                if (members.Count < 2)
                {
                    Refused(LinkRefusal.NotEvaluated, members.Count);
                    continue;
                }
                float Held((PreparedRenderer job, int key, float full) m) => m.job.Liveness[m.key].HeldWeight / m.full;
                if (members.Any(m => Math.Abs(Held(m) - Held(members[0])) > ShapeKeyLinks.Tolerance))
                {
                    Refused(LinkRefusal.HeldApart, members.Count);
                    continue;
                }

                var weights = new SortedSet<float>();
                foreach (var (job, key, full) in members)
                {
                    var liveness = job.Liveness[key];
                    foreach (var w in MeshExtractor.Configurations(ShapeKeyFrames.WeightsOf(job.Source, key), liveness, liveness.HeldWeight))
                        weights.Add(w / full);
                }
                if (weights.Count == 0) continue;

                var linkReport = new LinkReport(members.Select(m => new ShapeKeyRef(m.job.Renderer, m.job.Path, m.key, m.job.Source.GetBlendShapeName(m.key))).ToArray());
                prepared.Links.Add(linkReport);
                foreach (var u in weights)
                {
                    var id = next++;
                    foreach (var group in members.GroupBy(m => m.job))
                        group.Key.Linked.Add(new LiveFrame(id, group.Select(m => m.key).ToArray(), group.Select(m => u * m.full).ToArray()));
                    prepared.Configurations.Add(new ConfigurationReport(id, linkReport, u, members.Select(m => u * m.full).ToArray()));
                }
            }
        }

        static void Extract(PreparedRenderer job, PipelineInput input, AvatarBones bones, PreparedAvatar prepared)
        {
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Extraction);
                job.Eval = RendererExtraction.Extract(job.Renderer, bones, job.Liveness, job.Linked, Allocator.Persistent, out _);
                job.Extracted = true;
                job.Mirrored = RendererExtraction.DrawsMirrored(job.Renderer);
            }
            catch (NonFiniteMeshException e)
            {
                // Not a failure but the mesh as authored, so it is named for what it is; nothing was extracted.
                job.Skip = SkipReason.NonFinite;
                prepared.Diagnostics.Add(e.ShapeKey != null
                    ? new Diagnostic(DiagnosticKeys.NonFiniteShapeKey, true, job.Renderer, e.ShapeKey, e.Vertex)
                    : new Diagnostic(DiagnosticKeys.NonFiniteVertex, true, job.Renderer, e.Vertex));
            }
            catch (Exception e)
            {
                job.Dispose();
                Fail(job.Renderer, prepared.Diagnostics, PipelineStage.Extraction, e, out job.Skip, out job.SkipDetail);
            }
        }

        static void RemoveHidden(List<PreparedRenderer> jobs, PipelineInput input, AvatarBones bones, PreparedAvatar prepared)
        {
            var alwaysOn = new HashSet<Renderer>(input.Settings.alwaysOn.Where(r => r != null));
            var materials = new OccluderMaterials(input.Settings.materials);
            var outlines = input.Settings.outlinesShowInside;
            foreach (var job in jobs)
            {
                if (!alwaysOn.Contains(job.Renderer)) continue;
                // Listed as always on but off as the avatar loads: it covers nothing until something turns it on.
                if (!job.Renderer.enabled || !job.Renderer.gameObject.activeInHierarchy)
                {
                    prepared.Diagnostics.Add(new Diagnostic(DiagnosticKeys.AlwaysOnIsOff, false, job.Renderer));
                    continue;
                }
                job.Occludes = true;
            }
            if (!jobs.Any(j => j.Occludes)) return;

            var owned = new List<IDisposable>();
            OcclusionResult[] results = null;
            try
            {
                var meshes = new List<OcclusionMesh>();
                foreach (var job in jobs)
                    meshes.Add(OcclusionInput.Mesh(job.Renderer, job.Source.subMeshCount, job.Eval, job.Mirrored, job.Occludes, materials, outlines, owned));

                var unstable = UnstableBones.Compute(bones, input.ScaleCurves);
                for (var b = 0; b < unstable.Length; b++)
                    if (unstable[b]) prepared.UnstableBones.Add(RuntimeUtil.RelativePath(input.Root.gameObject, bones.Transforms[b].gameObject) ?? bones.Transforms[b].name);
                var boneTable = OcclusionInput.Bones(bones, unstable, owned);

                results = OcclusionSolver.Solve(meshes, boneTable, input.Occlusion, Allocator.Persistent, out var stats);
                prepared.OcclusionRan = true;
                prepared.OcclusionStats = stats;
                prepared.BoneParents = bones.Parents.ToArray();
                prepared.VanishingBones = (bool[])unstable.Clone();
                var declined = new DeclineCounts();
                for (var i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    job.OccluderSubmeshes = meshes[i].Submeshes;
                    job.Hidden = results[i].HiddenTriangles.ToArray();
                    job.CoveredBy = results[i].CoveredBy.ToArray();
                    job.InteriorCoveredBy = results[i].InteriorCoveredBy.ToArray();
                    job.KeptOnUnstableBones = UnstableBones.Keep(job.Eval, job.Hidden, unstable);
                    job.KeptForFur = MaterialFur.Keep(job.Renderer, job.Source.subMeshCount, job.Eval, job.Hidden);
                    job.KeptForOutline = outlines ? OccluderMaterials.KeptForOutline(job.Renderer, job.Source.subMeshCount, job.Eval, results[i]) : 0;
                    job.Declined = results[i].Declined;
                    declined.Add(results[i].Declined);
                }
                prepared.Declined = declined;
            }
            catch (Exception e)
            {
                // Occlusion decides over every renderer at once, so a failure leaves all of them whole.
                foreach (var job in jobs)
                {
                    job.Hidden = null;
                    job.CoveredBy = job.InteriorCoveredBy = null;
                    job.OccluderSubmeshes = null;
                }
                prepared.OcclusionRan = false;
                prepared.Diagnostics.Add(new Diagnostic(DiagnosticKeys.OcclusionFailed, true, e.Message));
                Debug.LogException(e);
            }
            finally
            {
                if (results != null) foreach (var r in results) r.Dispose();
                foreach (var o in owned) o.Dispose();
            }
        }

        /// <summary>Records every extracted renderer's collapse sequence, over the triangles occlusion kept.</summary>
        static void Record(List<PreparedRenderer> live, List<PreparedRenderer> all, PipelineInput input, AvatarBones bones, PreparedAvatar prepared)
        {
            var settings = input.Settings;
            var protection = new Protection(input.Root, bones, input.ScaleCurves);
            var perRenderer = new Dictionary<Renderer, RendererSettings>();
            foreach (var s in settings.renderers)
                if (s != null && s.renderer != null) perRenderer[s.renderer] = s;

            var stage = Stopwatch.StartNew();
            input.BeforeStep?.Invoke(AvatarStep.Clearance);
            var surface = input.Decimation.Clearance.Enabled ? ClearanceSurfaceOf(live, all, input, bones, input.Decimation.Clearance.Reach) : default;
            BendRig rig = default;
            var recordings = new List<Recording>();
            try
            {
                prepared.Times.Clearance = stage.Elapsed.TotalMilliseconds;
                input.BeforeStep?.Invoke(AvatarStep.Recording);
                rig = BendRigs.Of(bones, BendRigs.SkinWeightPerBone(live.Select(j => j.Eval), bones.Count));
                stage.Restart();
                // Each renderer starts recording as it is reached, beside those already started, and each is then waited for in turn.
                foreach (var job in live)
                {
                    perRenderer.TryGetValue(job.Renderer, out var own);
                    job.Weight = Mathf.Max(own?.protection ?? 1f, MinWeight);
                    if (own != null && own.excludeFromDecimation)
                    {
                        job.ExcludedFromDecimation = true;
                        continue;
                    }
                    var recording = StartRecording(job, input, protection, new ClearanceInput { Surface = surface, TriangleLayer = job.Layers }, rig, prepared);
                    if (recording != null) recordings.Add(recording);
                }
                foreach (var recording in recordings) FinishRecording(recording, prepared);
                prepared.Times.Decimation = stage.Elapsed.TotalMilliseconds;
            }
            finally
            {
                // A recording reads the surface and its renderer's layers until it is done, so each is waited for before they go.
                foreach (var recording in recordings) recording.Dispose();
                surface.Dispose();
                rig.Dispose();
                // Only the clearance rule reads a renderer's layers, and only while its sequence is recorded.
                foreach (var job in live)
                {
                    if (job.Layers.IsCreated) job.Layers.Dispose();
                    job.Layers = default;
                }
            }
        }

        /// <summary>
        /// Every renderer's surface as one clearance surface, each connected piece of each mesh its own layer, and each
        /// renderer's layer per triangle in <see cref="PreparedRenderer.Layers"/>; the one place that decides what stands in
        /// it. A triangle occlusion removed stands in it, in the piece it belongs to in the whole mesh: that piece's own
        /// decimation passes it by, as its decimation leaves it out, while every other layer keeps its share of the gap to
        /// it, so one re-judging (<see cref="Cut"/>) writes back where the cut took its cover comes back clear. The layers
        /// are then the whole mesh's pieces: a renderer's other pieces (a lining under its outer) keep clear of its removed
        /// triangles as of kept ones, and pieces joined only through removed triangles are one layer, as in the source.
        /// <see cref="PipelineInput.RemovedAsNeighbours"/> off leaves a removed triangle out, so nothing is held clear of
        /// it and the renderer cut over one written back may cross it. A renderer off at build by its object or its
        /// renderer is in it whole, like any other, since whatever turns it on shows it against the rest. One shown by a
        /// clip growing a bone from zero scale is in it as nothing, since its triangles have no area as the avatar stands,
        /// so its neighbours are held clear of nothing where it appears. One excluded from decimation is in it, unmoved. So is
        /// a renderer the pass left as it was (for Cloth, a failed extraction, or its shape keys' names) whose mesh can be
        /// read: it is still drawn where it stands, whole, so its neighbours keep clear of its source.
        /// </summary>
        static ClearanceSurface ClearanceSurfaceOf(List<PreparedRenderer> live, List<PreparedRenderer> all, PipelineInput input, AvatarBones bones, float reach)
        {
            var sources = new List<ClearanceLayerSource>();
            var owned = new List<IDisposable>();
            try
            {
                var next = 0;
                foreach (var job in live)
                {
                    var eval = job.Eval;
                    var hidden = input.RemovedAsNeighbours ? default : HiddenMask(job);
                    if (hidden.IsCreated) owned.Add(hidden);
                    job.Layers = Layers.Pieces(eval.Positions, eval.Triangles, hidden, Allocator.Persistent, out var pieces);
                    for (var i = 0; i < job.Layers.Length; i++)
                        if (job.Layers[i] >= 0) job.Layers[i] += next;
                    next += pieces;
                    sources.Add(new ClearanceLayerSource { Positions = eval.Positions, Triangles = eval.Triangles, TriangleLayer = job.Layers, Excluded = hidden });
                }
                foreach (var job in all)
                {
                    if (job.Extracted || job.Source == null || job.TrianglesBefore == 0 || !CanRead(job.Source, Application.isPlaying)) continue;
                    if (!TryExtractUnchanged(job, input, bones, out var eval)) continue;
                    owned.Add(eval);
                    var layers = Layers.Pieces(eval.Positions, eval.Triangles, Allocator.Persistent, out var pieces);
                    owned.Add(layers);
                    for (var i = 0; i < layers.Length; i++) layers[i] += next;
                    next += pieces;
                    sources.Add(new ClearanceLayerSource { Positions = eval.Positions, Triangles = eval.Triangles, TriangleLayer = layers });
                }
                return ClearanceSurface.Build(sources, reach, Allocator.Persistent);
            }
            finally
            {
                foreach (var o in owned) o.Dispose();
            }
        }

        /// <summary>
        /// A renderer the pass leaves as it was, extracted only to stand in the clearance surface. False, with nothing to
        /// dispose, when its mesh cannot be extracted, which leaves it out: as before, it constrains nothing, and its
        /// <see cref="PreparedRenderer.SkipDetail"/> says so.
        /// </summary>
        static bool TryExtractUnchanged(PreparedRenderer job, PipelineInput input, AvatarBones bones, out EvalMesh eval)
        {
            eval = default;
            try
            {
                var liveness = job.Renderer is SkinnedMeshRenderer skinned ? LivenessOf(skinned, input) : null;
                eval = RendererExtraction.Extract(job.Renderer, bones, liveness, Allocator.Persistent, out _);
                return true;
            }
            catch (Exception)
            {
                const string unheld = "its neighbours keep no clearance from it, since it could not be extracted for the clearance surface";
                job.SkipDetail = string.IsNullOrEmpty(job.SkipDetail) ? unheld : job.SkipDetail + "; " + unheld;
                return false;
            }
        }

        /// <summary>The triangles occlusion removed, as the mask decimation leaves them out by, and the clearance surface with <see cref="PipelineInput.RemovedAsNeighbours"/> off; unset when it removed none. The caller owns it.</summary>
        static NativeArray<bool> HiddenMask(PreparedRenderer job) => job.Hidden == null ? default : new NativeArray<bool>(job.Hidden, Allocator.Persistent);

        /// <summary>
        /// One renderer's sequence while a worker thread records it, with the arrays the recording reads that the renderer
        /// does not own. Disposing waits for the recording and frees all of it, however far it got.
        /// </summary>
        sealed class Recording : IDisposable
        {
            public readonly PreparedRenderer Job;
            public readonly List<IDisposable> Owned = new List<IDisposable>();
            public PendingSequence Pending;

            public Recording(PreparedRenderer job) => Job = job;

            public void Dispose()
            {
                Pending?.Dispose();
                Pending = null;
                foreach (var o in Owned) o.Dispose();
                Owned.Clear();
            }
        }

        /// <summary>
        /// Starts recording one renderer's sequence, beside any already started; <see cref="FinishRecording"/> waits for it.
        /// Null, with the failure recorded, when it cannot start. A failed renderer is left at what occlusion kept.
        /// </summary>
        static Recording StartRecording(PreparedRenderer job, PipelineInput input, Protection protection, in ClearanceInput clearance, in BendRig rig, PreparedAvatar prepared)
        {
            var recording = new Recording(job);
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Decimation);
                var eval = job.Eval;
                // The decimator sees only what occlusion kept.
                var hidden = HiddenMask(job);
                if (hidden.IsCreated) recording.Owned.Add(hidden);

                var vertexProtection = protection.For(job.Renderer, job.Source.subMeshCount, eval, job.Hidden, recording.Owned,
                    out var lockedCount, out var fromZeroScale, out var lockedForFur, out var grownCount);
                job.LockedVertices = lockedCount;
                job.LockedFromZeroScale = fromZeroScale;
                job.LockedForFur = lockedForFur;
                job.CostRaisedForGrowth = grownCount;
                job.FloorTriangles = protection.FloorFor(job.Renderer, eval.TriangleCount - job.HiddenCount);

                recording.Pending = Decimator.Schedule(eval, hidden, input.Decimation, vertexProtection, clearance, rig);
                return recording;
            }
            catch (Exception e)
            {
                recording.Dispose();
                NameFailure(job.Renderer, prepared.Diagnostics, PipelineStage.Decimation, e);
                return null;
            }
        }

        /// <summary>Waits for one renderer's recording and keeps its sequence. A failure here is that renderer's alone.</summary>
        static void FinishRecording(Recording recording, PreparedAvatar prepared)
        {
            var job = recording.Job;
            try
            {
                job.Sequence = recording.Pending.Complete(Allocator.Persistent, out var clearanceStats);
                job.Decimated = true;
                job.NearVertices = clearanceStats.NearVertices;
                job.ClearanceRefused = clearanceStats.Refused;
            }
            catch (Exception e)
            {
                NameFailure(job.Renderer, prepared.Diagnostics, PipelineStage.Decimation, e);
            }
            finally
            {
                recording.Dispose();
            }
        }
    }
}
