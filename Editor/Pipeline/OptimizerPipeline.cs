using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.runtime;
using Ryan6Vrc.MeshOptimizer.Core.Allocation;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Decimation;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Ryan6Vrc.MeshOptimizer.Writing;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>What the pipeline needs; nothing here needs a build.</summary>
    internal sealed class PipelineInput
    {
        public Transform Root;
        public AvatarMeshOptimizer Settings;
        /// <summary>Per skinned renderer, per shape key. A renderer missing from it has every key evaluated as live, from the weight it holds to full weight.</summary>
        public IReadOnlyDictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Liveness;
        /// <summary>Groups of keys that cannot be driven apart (<see cref="ShapeKeyLinks"/>). Null reads as none: every live key moves alone.</summary>
        public IReadOnlyList<LinkedKey[]> Links;
        /// <summary>
        /// Keys that link with none though another key is driven by exactly the same set of clips of known identity, each
        /// with why (<see cref="ShapeKeyLinks"/>); the report counts those the pass evaluates as live. Null reads as none.
        /// </summary>
        public IReadOnlyList<(LinkedKey key, LinkRefusal reason)> Unlinked;
        /// <summary>Scale curves any clip can play, for <see cref="UnstableBones"/>. Null reads as none.</summary>
        public IEnumerable<ScaleCurve> ScaleCurves;
        public OcclusionSettings Occlusion = OcclusionSettings.Default;
        public DecimationSettings Decimation = BuildSettings.Decimation;
        /// <summary>
        /// Whether a cut judges the removed triangles again against the meshes it writes (<see cref="OptimizerPipeline.Cut"/>).
        /// Off, every mesh is written with the removals as occlusion first judged them.
        /// </summary>
        public bool Rejudge = BuildSettings.Rejudging;
        /// <summary>Time the caller spent classifying liveness, for the report.</summary>
        public double LivenessMs;
        /// <summary>Called before each renderer enters a stage. A test seam: throwing here is a stage failing on that renderer.</summary>
        public Action<Renderer, PipelineStage> BeforeStage;
        /// <summary>
        /// Called before each step taken for the whole avatar at once. A test seam: throwing here escapes
        /// <see cref="OptimizerPipeline.Prepare"/> or <see cref="OptimizerPipeline.Cut"/>, as a failure no one renderer
        /// can be blamed for would.
        /// </summary>
        public Action<AvatarStep> BeforeStep;
    }

    /// <summary>The steps taken for the whole avatar at once, as <see cref="PipelineInput.BeforeStep"/> names them.</summary>
    internal enum AvatarStep
    {
        Occlusion,
        Clearance,
        Recording,
        Allocation,
        Rejudging,
        Writing,
    }

    /// <summary>
    /// The decimation settings a build runs with, and whether it judges its removals again. The component exposes none of
    /// them, and the decimation defaults live in <see cref="DecimationSettings.Default"/> alone; this is the one place a
    /// test or a measurement can hand a build other values, since a build reaches the pipeline through NDMF and not
    /// through a caller. Nothing in the package overrides them.
    /// </summary>
    internal static class BuildSettings
    {
        internal static DecimationSettings Decimation { get; private set; } = DecimationSettings.Default;

        /// <summary>Whether a build's cut judges the removed triangles again (<see cref="PipelineInput.Rejudge"/>); on unless overridden.</summary>
        internal static bool Rejudging { get; private set; } = true;

        /// <summary>Every build until the returned scope is disposed runs with <paramref name="decimation"/>; disposing restores what was before.</summary>
        internal static IDisposable Override(DecimationSettings decimation)
        {
            var before = Decimation;
            Decimation = decimation;
            return new Scope(() => Decimation = before);
        }

        /// <summary>Every build until the returned scope is disposed re-judges or not as <paramref name="rejudging"/> says; disposing restores what was before.</summary>
        internal static IDisposable OverrideRejudging(bool rejudging)
        {
            var before = Rejudging;
            Rejudging = rejudging;
            return new Scope(() => Rejudging = before);
        }

        sealed class Scope : IDisposable
        {
            readonly Action _restore;
            bool _done;

            public Scope(Action restore) => _restore = restore;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _restore();
            }
        }
    }

    /// <summary>The per-renderer stages, as a failure names them.</summary>
    public enum PipelineStage
    {
        Extraction,
        Decimation,
        Writing,
    }

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
    internal static class OptimizerPipeline
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

        /// <summary>
        /// Spends the component's target over the prepared sequences, cuts each and writes the meshes, and reports it all.
        /// Before writing, every triangle occlusion removed is judged again against what the always-on renderers are written
        /// with, since decimation moves the faces that covered it, and one no longer covered is written back
        /// (<see cref="PipelineInput.Rejudge"/>); a budget pays for those by cutting deeper.
        /// Reads <paramref name="prepared"/> and changes nothing in it; whatever of it the report holds and a cut reads is a
        /// copy, so nothing done to one report changes the next cut. A renderer whose writing fails is left as it was.
        /// Throws once <paramref name="prepared"/> is disposed.
        /// </summary>
        public static OptimizationReport Cut(PreparedAvatar prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (prepared.Disposed) throw new ObjectDisposedException(nameof(PreparedAvatar), "a disposed prepared avatar has nothing left to cut; prepare the avatar again");
            var input = prepared.Input;
            var settings = input.Settings;
            var total = Stopwatch.StartNew();
            var report = new OptimizationReport
            {
                AvatarName = input.Root.name,
                Target = settings.target,
                TriangleBudget = settings.triangleBudget,
                Tolerance = settings.tolerance,
                Decimation = input.Decimation, Rejudge = input.Rejudge, Occlusion = input.Occlusion,
                TargetMet = true,
                OcclusionRan = prepared.OcclusionRan,
                Declined = prepared.Declined,
                OcclusionStats = prepared.OcclusionStats,
                Times = prepared.Times,
            };
            report.Links.AddRange(prepared.Links);
            report.Configurations.AddRange(prepared.Configurations);
            foreach (var kv in prepared.LinkRefusals) report.LinkRefusals[kv.Key] = kv.Value;
            report.UnmatchedMaterialOverrides.AddRange(prepared.UnmatchedMaterialOverrides);
            report.UnstableBones.AddRange(prepared.UnstableBones);
            report.Diagnostics.AddRange(prepared.Diagnostics);

            var jobs = prepared.Renderers;
            var pass = CutAt(prepared, 0, report);
            if (Rejudges(prepared, pass)) pass = Rejudge(prepared, pass, report);
            report.ErrorLevel = pass.Level;
            report.Diagnostics.AddRange(pass.Diagnostics);

            var stage = Stopwatch.StartNew();
            input.BeforeStep?.Invoke(AvatarStep.Writing);
            for (var i = 0; i < jobs.Count; i++)
                if (pass.Kept[i] != null) Write(jobs[i], pass.Written(i, jobs[i]), pass.Outcomes[i], input, report);
            report.Times.Writing += stage.Elapsed.TotalMilliseconds;

            for (var i = 0; i < jobs.Count; i++) report.Renderers.Add(ReportOf(jobs[i], pass.Steps[i], pass.Outcomes[i]));
            if (settings.target == TargetKind.TriangleBudget) report.TargetMet = report.TrianglesAfter <= settings.triangleBudget;
            if (settings.decimate && !report.TargetMet)
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.BudgetNotMet, false,
                    report.TrianglesAfter.ToString(), settings.triangleBudget.ToString()));
            report.Times.Total = prepared.PrepareMs + total.Elapsed.TotalMilliseconds + input.LivenessMs;
            return report;
        }

        /// <summary>
        /// How many times at most a budget is allocated, kept and judged again to pay for the triangles re-judging restores.
        /// Each pass after the first withholds from the allocation what the pass before restored, and at least what it withheld
        /// plus what it went over. When none meets the budget, the pass that came nearest is written and the budget reported
        /// not met.
        /// </summary>
        const int BudgetPasses = 4;

        /// <summary>
        /// One cut of every renderer: the allocation's steps and level, what each renderer keeps at them, the failures met
        /// keeping, and, once judged again, the removed triangles each gets back.
        /// </summary>
        sealed class Pass
        {
            /// <summary>Triangles of the budget withheld from the allocation, for what an earlier pass restored.</summary>
            public int Reserve;
            public float Level;
            public int[] Steps;
            public Outcome[] Outcomes;
            public KeptTriangles[] Kept;
            public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
            /// <summary>Per renderer, per evaluation triangle, true for a removed one no longer covered; null where none is.</summary>
            public bool[][] Restored;
            public int RestoredCount;

            public int RestoredOn(int i)
            {
                var n = 0;
                if (Restored?[i] != null) foreach (var r in Restored[i]) if (r) n++;
                return n;
            }

            /// <summary>The triangles the avatar draws after this pass, every write succeeding.</summary>
            public int TrianglesAfter(List<PreparedRenderer> jobs)
            {
                var n = 0;
                for (var i = 0; i < jobs.Count; i++) n += Kept[i] != null ? Kept[i].Triangles.Length + RestoredOn(i) : jobs[i].TrianglesBefore;
                return n;
            }

            /// <summary>
            /// The triangles renderer <paramref name="i"/> is written with: what it keeps, then each removed triangle it gets
            /// back, at its authored corners and in its own submesh. Records on the renderer's outcome what stays removed.
            /// </summary>
            public KeptTriangles Written(int i, PreparedRenderer job)
            {
                var kept = Kept[i];
                var outcome = Outcomes[i];
                outcome.Restored = RestoredOn(i);
                if (outcome.Restored == 0) return kept;
                var restored = Restored[i];
                outcome.Removed = new bool[job.Hidden.Length];
                var triangles = new List<int3>(kept.Triangles);
                var source = new List<int>(kept.Source);
                for (var t = 0; t < restored.Length; t++)
                {
                    outcome.Removed[t] = job.Hidden[t] && !restored[t];
                    if (!restored[t]) continue;
                    triangles.Add(job.Eval.Triangles[t]);
                    source.Add(t);
                }
                return new KeptTriangles { Triangles = triangles.ToArray(), Source = source.ToArray() };
            }
        }

        /// <summary>
        /// Allocates the target, a budget less <paramref name="reserve"/> triangles, over the recorded sequences, and keeps
        /// each changed renderer's triangles at its step.
        /// </summary>
        static Pass CutAt(PreparedAvatar prepared, int reserve, OptimizationReport report)
        {
            var input = prepared.Input;
            var settings = input.Settings;
            var jobs = prepared.Renderers;
            var pass = new Pass
            {
                Reserve = reserve,
                Steps = new int[jobs.Count],
                Outcomes = new Outcome[jobs.Count],
                Kept = new KeptTriangles[jobs.Count],
            };
            var stage = Stopwatch.StartNew();
            if (settings.decimate)
            {
                input.BeforeStep?.Invoke(AvatarStep.Allocation);
                pass.Level = Allocate(jobs, settings, reserve, pass.Steps);
                report.Times.Allocation += stage.Elapsed.TotalMilliseconds;
            }

            stage.Restart();
            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (!job.Extracted) continue;
                pass.Outcomes[i] = new Outcome();
                if (job.HiddenCount == 0 && (!job.Decimated || pass.Steps[i] == 0))
                {
                    pass.Outcomes[i].Skip = SkipReason.Unchanged;
                    continue;
                }
                pass.Kept[i] = Keep(job, pass.Steps[i], pass.Outcomes[i], pass.Diagnostics);
            }
            report.Times.Writing += stage.Elapsed.TotalMilliseconds;
            return pass;
        }

        /// <summary>
        /// Whether a cut can have taken cover from a removed triangle: re-judging is on, a renderer the cut writes has
        /// triangles occlusion removed, and the cut moved some always-on renderer's triangles. The solve left every removed
        /// triangle covered by what the always-on renderers keep of their source, so a cut that moves none of them leaves
        /// that cover standing.
        /// </summary>
        static bool Rejudges(PreparedAvatar prepared, Pass pass)
        {
            if (!prepared.Input.Rejudge || !prepared.OcclusionRan) return false;
            var jobs = prepared.Renderers;
            bool removed = false, moved = false;
            for (var i = 0; i < jobs.Count; i++)
            {
                if (pass.Kept[i] == null) continue;
                var job = jobs[i];
                removed |= job.HiddenCount > 0;
                moved |= job.Occludes && job.Decimated && pass.Steps[i] > 0;
            }
            return removed && moved;
        }

        /// <summary>
        /// Judges the removals of <paramref name="first"/> again and, under a budget, pays for what that restores by cutting
        /// deeper, a pass at a time, up to <see cref="BudgetPasses"/>; under a tolerance the one pass stands and the count
        /// rises. Returns the pass to write. A re-judge that throws is named, and the pass written is <paramref name="first"/>
        /// with its removals as first judged: what the pipeline writes with re-judging off.
        /// </summary>
        static Pass Rejudge(PreparedAvatar prepared, Pass first, OptimizationReport report)
        {
            var input = prepared.Input;
            var settings = input.Settings;
            var budget = settings.decimate && settings.target == TargetKind.TriangleBudget;
            input.BeforeStep?.Invoke(AvatarStep.Rejudging);
            var pass = first;
            Pass best = null;
            var watch = new Stopwatch();
            try
            {
                while (true)
                {
                    report.RejudgePasses++;
                    watch.Start();
                    RejudgeOnce(prepared, pass);
                    watch.Stop();
                    var after = pass.TrianglesAfter(prepared.Renderers);
                    var over = after - settings.triangleBudget;
                    if (!budget || pass.RestoredCount == 0 || over <= 0) return pass;
                    // Cutting deeper can take more cover than it saves, so a budget no pass meets is written at the pass
                    // that came nearest.
                    if (best == null || after <= best.TrianglesAfter(prepared.Renderers)) best = pass;
                    if (report.RejudgePasses == BudgetPasses) return best;
                    // The allocation can land under what it was given, and withholding only the overage from that would
                    // hand the slack back: the next cut would be this one again. So the next cut is asked for this cut
                    // less the overage, which is the budget less what this pass restored, and never less than before.
                    var deeper = CutAt(prepared, Math.Max(pass.Reserve + over, pass.RestoredCount), report);
                    if (!Rejudges(prepared, deeper)) return deeper;
                    pass = deeper;
                }
            }
            catch (Exception e)
            {
                first.Restored = null;
                first.RestoredCount = 0;
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.RejudgeFailed, true, e.Message));
                Debug.LogException(e);
                return first;
            }
            finally
            {
                report.Times.Rejudging = watch.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>
        /// Judges the pass's removals again (<see cref="Rejudging.Restored"/>) and puts what is no longer hidden into
        /// <see cref="Pass.Restored"/>. Every extracted renderer is entered at its index in the first solve, as the pass
        /// leaves it: a renderer it writes draws what it keeps and leaves out what occlusion removed, and one it leaves as
        /// it was draws its whole source.
        /// </summary>
        static void RejudgeOnce(PreparedAvatar prepared, Pass pass)
        {
            var jobs = prepared.Renderers;
            var live = prepared.Extracted;
            var renderers = new List<CutRenderer>(live.Count);
            foreach (var job in live)
            {
                var i = jobs.IndexOf(job);
                var kept = pass.Kept[i];
                renderers.Add(new CutRenderer
                {
                    Eval = job.Eval,
                    Mirrored = job.Mirrored,
                    Occludes = job.Occludes,
                    Submeshes = job.OccluderSubmeshes,
                    Removed = kept != null && job.HiddenCount > 0 ? job.Hidden : null,
                    Drawn = kept?.Triangles,
                    DrawnSource = kept?.Source,
                    Setback = job.Occludes && job.Decimated ? job.Sequence.ErrorAt(pass.Steps[i]) : 0f,
                    CoveredBy = job.CoveredBy,
                    InteriorCoveredBy = job.InteriorCoveredBy,
                });
            }
            var restored = Rejudging.Restored(renderers, prepared.BoneParents, prepared.VanishingBones, prepared.Input.Occlusion, out var count);
            pass.Restored = new bool[jobs.Count][];
            for (var k = 0; k < live.Count; k++) pass.Restored[jobs.IndexOf(live[k])] = restored[k];
            pass.RestoredCount = count;
        }

        /// <summary>What one cut did to one extracted renderer.</summary>
        sealed class Outcome
        {
            public Mesh Mesh;
            public int[] KeptVertices;
            public int Triangles;
            public int Hidden;
            /// <summary>Of the triangles occlusion removed, those written back because the cut took their cover.</summary>
            public int Restored;
            /// <summary>Per evaluation triangle, true for one the written mesh leaves out; null when that is every triangle occlusion removed.</summary>
            public bool[] Removed;
            public SkipReason Skip;
            public string SkipDetail;
        }

        /// <summary>
        /// The triangles one renderer keeps at one cut, over its evaluation mesh's vertices, each with the evaluation
        /// triangle it came from, whose submesh it is drawn in.
        /// </summary>
        sealed class KeptTriangles
        {
            public int3[] Triangles;
            public int[] Source;
        }

        /// <summary>The renderer's report, written once from what preparing found and what the cut did.</summary>
        static RendererReport ReportOf(PreparedRenderer job, int step, Outcome outcome)
        {
            var written = outcome?.Mesh != null;
            return new RendererReport
            {
                Renderer = job.Renderer,
                Path = job.Path,
                SourceMesh = job.Source,
                OutputMesh = written ? outcome.Mesh : null,
                TrianglesBefore = job.TrianglesBefore,
                HiddenRemoved = written ? outcome.Hidden : 0,
                HiddenRestored = written ? outcome.Restored : 0,
                TrianglesAfter = written ? outcome.Triangles : job.TrianglesBefore,
                Skip = job.Skip != SkipReason.None ? job.Skip : outcome?.Skip ?? SkipReason.None,
                SkipDetail = job.Skip != SkipReason.None ? job.SkipDetail : outcome?.SkipDetail,
                Occludes = job.Occludes,
                Mirrored = job.Mirrored,
                ExcludedFromDecimation = job.ExcludedFromDecimation,
                SequenceLength = job.SequenceLength,
                AllocationCut = step,
                Error = job.Decimated ? job.Sequence.ErrorAt(step) : 0f,
                FloorTriangles = job.FloorTriangles,
                Weight = job.Weight,
                LockedVertices = job.LockedVertices,
                LockedFromZeroScale = job.LockedFromZeroScale,
                LockedForFur = job.LockedForFur,
                CostRaisedForGrowth = job.CostRaisedForGrowth,
                NearVertices = job.NearVertices,
                ClearanceRefused = job.ClearanceRefused,
                KeptOnUnstableBones = job.KeptOnUnstableBones,
                KeptForFur = job.KeptForFur,
                KeptForOutline = job.KeptForOutline,
                Declined = job.Declined,
                // What the written mesh leaves out; otherwise a copy, since the prepared flags are what the next cut reads.
                HiddenSourceTriangles = written && outcome.Removed != null ? outcome.Removed : (bool[])job.Hidden?.Clone(),
                KeptVertices = written ? outcome.KeptVertices : null,
                Liveness = job.Liveness,
            };
        }

        /// <summary>
        /// Puts each written mesh on its renderer, recording it in NDMF's object registry as the source mesh's
        /// replacement first, so an error that names the new mesh leads back to the source.
        /// </summary>
        public static void Apply(OptimizationReport report)
        {
            foreach (var r in report.Renderers)
            {
                if (r.OutputMesh == null || r.Renderer == null) continue;
                ObjectRegistry.RegisterReplacedObject(r.SourceMesh, r.OutputMesh);
                switch (r.Renderer)
                {
                    case SkinnedMeshRenderer skinned:
                        skinned.sharedMesh = r.OutputMesh;
                        break;
                    case MeshRenderer _ when r.Renderer.TryGetComponent<MeshFilter>(out var filter):
                        filter.sharedMesh = r.OutputMesh;
                        break;
                }
            }
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
        /// Spends the target over the recorded sequences at one common error level, writing each renderer's step into
        /// <paramref name="steps"/> (0 for one not decimated), and returns the level. A budget is spent less
        /// <paramref name="reserve"/> triangles, held back for removed triangles re-judging writes back.
        /// </summary>
        static float Allocate(List<PreparedRenderer> all, AvatarMeshOptimizer settings, int reserve, int[] steps)
        {
            var decimated = all.Where(j => j.Decimated).ToList();
            // The budget is the avatar's: whatever is not decimated costs its triangles in full.
            var fixedTriangles = all.Where(j => !j.Decimated).Sum(j => j.TrianglesBefore - j.HiddenCount);
            // A triangle that repeats a vertex is outside the collapse sequence, yet a renderer left uncut still draws
            // it; counting it as fixed keeps the budget, at the cost of cutting that many more where a cut drops it.
            fixedTriangles += decimated.Sum(j => j.Eval.TriangleCount - j.HiddenCount - j.Sequence.InitialTriangleCount);
            var entries = decimated.Select(j => AllocationEntry.From(j.Sequence, j.FloorTriangles, j.Weight)).ToList();
            if (entries.Count == 0) return 0f;
            var result = settings.target == TargetKind.TriangleBudget
                ? BudgetAllocator.ForBudget(entries, Math.Max(0, settings.triangleBudget - fixedTriangles - reserve))
                : BudgetAllocator.ForTolerance(entries, settings.tolerance / 1000f);
            for (var i = 0; i < decimated.Count; i++) steps[all.IndexOf(decimated[i])] = result.Cuts[i];
            return result.Level;
        }

        /// <summary>
        /// Every renderer's kept surface as one clearance surface, each connected piece of each mesh its own layer, and
        /// each renderer's layer per triangle in <see cref="PreparedRenderer.Layers"/>. Kept means what occlusion left: a triangle
        /// it deleted is not there to be passed through. A renderer off at build is in it like any other, since whatever
        /// turns it on shows it against the rest; one excluded from decimation is in it too, unmoved. So is a renderer the
        /// pass left as it was (for Cloth, a failed extraction, or its shape keys' names) whose mesh can be read: it is still
        /// drawn where it stands, whole, so its neighbours keep clear of its source.
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
                    var hidden = HiddenMask(job);
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
        /// dispose, when its mesh cannot be extracted, which leaves it out: as before, it constrains nothing.
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
                return false;
            }
        }

        /// <summary>The triangles occlusion removed, as the mask decimation and the clearance surface leave them out by; unset when it removed none. The caller owns it.</summary>
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

        /// <summary>
        /// The triangles a renderer keeps at <paramref name="step"/>: its cut when it was cut, otherwise every triangle
        /// occlusion left. Null, with the failure recorded, when the cut fails.
        /// </summary>
        static KeptTriangles Keep(PreparedRenderer job, int step, Outcome outcome, List<Diagnostic> diagnostics)
        {
            CutResult cut = default;
            var cutMade = false;
            try
            {
                var eval = job.Eval;
                if (job.Decimated && step > 0)
                {
                    cut = job.Sequence.Cut(step, Allocator.Persistent);
                    cutMade = true;
                    return new KeptTriangles { Triangles = cut.Triangles.AsArray().ToArray(), Source = cut.SourceTriangle.AsArray().ToArray() };
                }
                var triangles = new List<int3>();
                var source = new List<int>();
                for (var t = 0; t < eval.TriangleCount; t++)
                {
                    if (job.Hidden != null && job.Hidden[t]) continue;
                    triangles.Add(eval.Triangles[t]);
                    source.Add(t);
                }
                return new KeptTriangles { Triangles = triangles.ToArray(), Source = source.ToArray() };
            }
            catch (Exception e)
            {
                Fail(job.Renderer, diagnostics, PipelineStage.Writing, e, out outcome.Skip, out outcome.SkipDetail);
                return null;
            }
            finally
            {
                if (cutMade) cut.Dispose();
            }
        }

        static void Write(PreparedRenderer job, KeptTriangles kept, Outcome outcome, PipelineInput input, OptimizationReport report)
        {
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Writing);
                var submesh = new int[kept.Source.Length];
                for (var i = 0; i < submesh.Length; i++) submesh[i] = job.Eval.TriangleSubmesh[kept.Source[i]];
                var mesh = MeshWriter.Write(job.Source, kept.Triangles, submesh, out var keptVertices);
                outcome.Mesh = mesh;
                outcome.KeptVertices = keptVertices;
                outcome.Hidden = job.HiddenCount - outcome.Restored;
                // Every evaluation triangle is on a submesh of triangles, so each kept one is drawn.
                outcome.Triangles = kept.Triangles.Length;
            }
            catch (Exception e)
            {
                Fail(job.Renderer, report.Diagnostics, PipelineStage.Writing, e, out outcome.Skip, out outcome.SkipDetail);
            }
        }
    }
}
