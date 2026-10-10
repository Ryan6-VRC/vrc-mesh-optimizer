using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using Ryan6Vrc.MeshOptimizer.Core.Allocation;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Ryan6Vrc.MeshOptimizer.Writing;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    internal static partial class OptimizerPipeline
    {
        /// <summary>
        /// Spends the component's target over the prepared sequences, cuts each and writes the meshes, and reports it all.
        /// The target and each renderer's protection are read from the component as they stand, so a change to either
        /// needs only another cut.
        /// Before writing, every triangle occlusion removed is judged again against what the always-on renderers are written
        /// with, since decimation moves the faces that covered it, and one no longer covered is written back
        /// (<see cref="PipelineInput.Rejudge"/>); a budget pays for those by cutting deeper, never past
        /// the component's <see cref="AvatarMeshOptimizer.maxError"/>, and one the ceiling stops short is reported unmet under
        /// <see cref="DiagnosticKeys.BudgetCeilingHeld"/> rather than <see cref="DiagnosticKeys.BudgetNotMet"/>.
        /// Reads <paramref name="prepared"/> and changes nothing in it; whatever of it the report holds and a cut reads is a
        /// copy, so nothing done to one report changes the next cut. A renderer whose writing fails is left as it was.
        /// Throws once <paramref name="prepared"/> is disposed, and, decimating to a budget, when the max error is negative or
        /// not finite.
        /// </summary>
        public static OptimizationReport Cut(PreparedAvatar prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (prepared.Disposed) throw new ObjectDisposedException(nameof(PreparedAvatar), "a disposed prepared avatar has nothing left to cut; prepare the avatar again");
            var input = prepared.Input;
            var settings = input.Settings;
            if (settings.decimate && settings.target == TargetKind.TriangleBudget && (!(settings.maxError >= 0f) || float.IsPositiveInfinity(settings.maxError)))
                throw new ArgumentException($"max error {settings.maxError} mm must be finite and 0 or more; 0 turns the ceiling off");
            var total = Stopwatch.StartNew();
            var report = new OptimizationReport
            {
                AvatarName = input.Root.name,
                Target = settings.target,
                TriangleBudget = settings.triangleBudget,
                Tolerance = settings.tolerance,
                Decimation = input.Decimation, Rejudge = input.Rejudge, Occlusion = input.Occlusion,
                MaxError = settings.maxError, RemovedAsNeighbours = input.RemovedAsNeighbours, OutlinesShowInside = settings.outlinesShowInside,
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

            for (var i = 0; i < jobs.Count; i++) report.Renderers.Add(ReportOf(jobs[i], pass.Steps[i], pass.Outcomes[i], settings));
            if (settings.target == TargetKind.TriangleBudget) report.TargetMet = report.TrianglesAfter <= settings.triangleBudget;
            // The ceiling is named only when the pass was over the budget before writing: one it held short of an allocation
            // reduced by a reserve can still meet the budget, and a write that fails is then what leaves it unmet.
            if (settings.decimate && !report.TargetMet)
                report.Diagnostics.Add(pass.CeilingHeld && pass.TrianglesAfter(jobs) > settings.triangleBudget
                    ? new Diagnostic(DiagnosticKeys.BudgetCeilingHeld, false, report.TrianglesAfter.ToString(), settings.triangleBudget.ToString(),
                        settings.maxError.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                    : new Diagnostic(DiagnosticKeys.BudgetNotMet, false, report.TrianglesAfter.ToString(), settings.triangleBudget.ToString(), report.HiddenRestored.ToString()));
            report.Times.Total = prepared.PrepareMs + total.Elapsed.TotalMilliseconds + input.LivenessMs;
            return report;
        }

        /// <summary>
        /// How many times at most a budget is allocated, kept and judged again to pay for the triangles re-judging restores.
        /// Each pass after the first withholds from the allocation what the pass before restored, and at least what it withheld
        /// plus what it went over. When none meets the budget, the pass that came nearest is written and the budget reported
        /// not met. A pass the error ceiling held is the last: any later one would take the same steps.
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
            /// <summary>
            /// The error ceiling stopped this pass's allocation short of what it was given, or, on the pass a budget writes,
            /// stopped the deeper pass that would have paid for what this one restored.
            /// </summary>
            public bool CeilingHeld;
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
                pass.Level = Allocate(jobs, settings, reserve, pass.Steps, out pass.CeilingHeld);
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
                    // A pass the error ceiling held took every step at or below it, and a deeper one would take the same.
                    if (pass.CeilingHeld)
                    {
                        best.CeilingHeld = true;
                        return best;
                    }
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
                    Setback = prepared.Input.Setback && job.Occludes && job.Decimated ? job.Sequence.ErrorAt(pass.Steps[i]) : 0f,
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
        static RendererReport ReportOf(PreparedRenderer job, int step, Outcome outcome, AvatarMeshOptimizer settings)
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
                Weight = WeightOf(job, settings),
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

        /// <summary>
        /// When <see cref="PipelineInput.Gate"/> asks, measures the gate (<see cref="GateRun.Measure"/>) into
        /// <see cref="OptimizationReport.Gate"/>; otherwise does nothing. Call it after <see cref="Apply"/>, on the avatar as
        /// this pass leaves it: each renderer holding its written mesh. That is what this pass did, before any later plugin
        /// runs; a runner measuring the finished build reads the same only where no later pass changes a renderer the report
        /// names. The gate restores every mesh, weight and transform it moves, so the avatar is written as without it. A measure
        /// that throws is named under <see cref="DiagnosticKeys.GateFailed"/> and leaves <see cref="OptimizationReport.Gate"/>
        /// null; it never fails the build.
        /// </summary>
        public static void MeasureGate(OptimizationReport report, PipelineInput input)
        {
            if (input.Gate == null) return;
            try
            {
                report.Gate = GateRun.Measure(report, input.Root, input.Gate);
            }
            catch (Exception e)
            {
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.GateFailed, true, e.Message));
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Spends the target over the recorded sequences at one common error level, writing each renderer's step into
        /// <paramref name="steps"/> (0 for one not decimated), and returns the level. A budget is spent less
        /// <paramref name="reserve"/> triangles, held back for removed triangles re-judging writes back, and no higher than
        /// the component's <see cref="AvatarMeshOptimizer.maxError"/> millimetres of weighted error when that is above 0;
        /// <paramref name="ceilingHeld"/> says the ceiling stopped it short.
        /// </summary>
        static float Allocate(List<PreparedRenderer> all, AvatarMeshOptimizer settings, int reserve, int[] steps, out bool ceilingHeld)
        {
            ceilingHeld = false;
            var decimated = all.Where(j => j.Decimated).ToList();
            // The budget is the avatar's: whatever is not decimated costs its triangles in full.
            var fixedTriangles = all.Where(j => !j.Decimated).Sum(j => j.TrianglesBefore - j.HiddenCount);
            // A triangle that repeats a vertex is outside the collapse sequence, yet a renderer left uncut still draws
            // it; counting it as fixed keeps the budget, at the cost of cutting that many more where a cut drops it.
            fixedTriangles += decimated.Sum(j => j.Eval.TriangleCount - j.HiddenCount - j.Sequence.InitialTriangleCount);
            var entries = decimated.Select(j => AllocationEntry.From(j.Sequence, j.FloorTriangles, WeightOf(j, settings))).ToList();
            if (entries.Count == 0) return 0f;
            var result = settings.target == TargetKind.TriangleBudget
                ? BudgetAllocator.ForBudget(entries, Math.Max(0, settings.triangleBudget - fixedTriangles - reserve), settings.maxError / 1000f)
                : BudgetAllocator.ForTolerance(entries, settings.tolerance / 1000f);
            for (var i = 0; i < decimated.Count; i++) steps[all.IndexOf(decimated[i])] = result.Cuts[i];
            ceilingHeld = result.CeilingHeld;
            return result.Level;
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
