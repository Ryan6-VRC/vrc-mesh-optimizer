using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.runtime;
using Ryan6Vrc.MeshOptimizer.Core.Allocation;
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
        /// <summary>Per skinned renderer, per shape key. A renderer missing from it has every key evaluated as live at full weight.</summary>
        public IReadOnlyDictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Liveness;
        /// <summary>Scale curves any clip can play, for <see cref="UnstableBones"/>. Null reads as none.</summary>
        public IEnumerable<ScaleCurve> ScaleCurves;
        public OcclusionSettings Occlusion = OcclusionSettings.Default;
        public DecimationSettings Decimation = DecimationSettings.Default;
        /// <summary>Time the caller spent classifying liveness, for the report.</summary>
        public double LivenessMs;
        /// <summary>Called before each renderer enters a stage. A test seam: throwing here is a stage failing on that renderer.</summary>
        public Action<Renderer, PipelineStage> BeforeStage;
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
    /// </summary>
    internal static class OptimizerPipeline
    {
        /// <summary>A renderer's protection maps to an allocation weight no lower than this, which the allocator needs above 0.</summary>
        const float MinWeight = 1e-3f;

        sealed class Job : IDisposable
        {
            public Renderer Renderer;
            public Mesh Source;
            public RendererReport Report;
            public EvalMesh Eval;
            public bool Extracted;
            public bool Occludes;
            public bool[] Hidden;
            public CollapseSequence Sequence;
            public bool Decimated;
            /// <summary>Per triangle the decimator saw, the extracted triangle it is.</summary>
            public int[] Reduced;
            public int Cut;

            public void Dispose()
            {
                if (Extracted) Eval.Dispose();
                Extracted = false;
                if (Decimated) Sequence.Dispose();
                Decimated = false;
            }
        }

        public static OptimizationReport Run(PipelineInput input)
        {
            if (input?.Root == null) throw new ArgumentException("the pipeline needs an avatar root");
            if (input.Settings == null) throw new ArgumentException("the pipeline needs the component's settings");
            var settings = input.Settings;
            var total = Stopwatch.StartNew();
            var report = new OptimizationReport
            {
                AvatarName = input.Root.name,
                Target = settings.target,
                TriangleBudget = settings.triangleBudget,
                Tolerance = settings.tolerance,
                TargetMet = true,
            };
            report.Times.Liveness = input.LivenessMs;

            var jobs = new List<Job>();
            try
            {
                var bones = new AvatarBones(input.Root);
                var stage = Stopwatch.StartNew();
                foreach (var renderer in input.Root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    var job = Survey(renderer, input.Root, report);
                    jobs.Add(job);
                    report.Renderers.Add(job.Report);
                    if (job.Report.Skip == SkipReason.None) Extract(job, input, bones, report);
                }
                report.Times.Extraction = stage.Elapsed.TotalMilliseconds;
                report.UnmatchedMaterialOverrides.AddRange(new OccluderMaterials(settings.materials).Unmatched(jobs.Select(j => j.Renderer)));

                var live = jobs.Where(j => j.Extracted).ToList();
                stage.Restart();
                if (settings.removeHiddenTriangles) RemoveHidden(live, input, bones, report);
                report.Times.Occlusion = stage.Elapsed.TotalMilliseconds;

                if (settings.decimate) Decimate(live, jobs, input, bones, report);

                stage.Restart();
                foreach (var job in live) Write(job, input, report);
                report.Times.Writing = stage.Elapsed.TotalMilliseconds;

                if (settings.target == TargetKind.TriangleBudget) report.TargetMet = report.TrianglesAfter <= settings.triangleBudget;
                if (settings.decimate && !report.TargetMet)
                    report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.BudgetNotMet, false,
                        report.TrianglesAfter.ToString(), settings.triangleBudget.ToString()));
            }
            finally
            {
                foreach (var job in jobs) job.Dispose();
            }
            report.Times.Total = total.Elapsed.TotalMilliseconds + input.LivenessMs;
            OptimizationReports.Add(report);
            return report;
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

        static Job Survey(Renderer renderer, Transform root, OptimizationReport report)
        {
            var mesh = RendererExtraction.MeshOf(renderer);
            var job = new Job
            {
                Renderer = renderer,
                Source = mesh,
                Report = new RendererReport
                {
                    Renderer = renderer,
                    Path = RuntimeUtil.RelativePath(root.gameObject, renderer.gameObject) ?? renderer.name,
                    SourceMesh = mesh,
                    Weight = 1f,
                },
            };
            var r = job.Report;
            if (mesh == null)
            {
                r.Skip = SkipReason.NoMesh;
                return job;
            }
            r.TrianglesBefore = r.TrianglesAfter = TriangleCount(mesh);
            if (renderer.GetComponent<Cloth>() != null)
            {
                r.Skip = SkipReason.Cloth;
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.Cloth, true, renderer));
            }
            else if (!CanRead(mesh, Application.isPlaying))
            {
                r.Skip = SkipReason.NotReadable;
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.NotReadable, true, renderer, mesh.name));
            }
            else if (r.TrianglesBefore == 0)
            {
                r.Skip = SkipReason.NoTriangles;
            }
            return job;
        }

        /// <summary>Triangles in the mesh's triangle submeshes; other topologies are not triangles.</summary>
        static int TriangleCount(Mesh mesh)
        {
            long count = 0;
            for (var s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == UnityEngine.MeshTopology.Triangles) count += mesh.GetIndexCount(s) / 3;
            return (int)count;
        }

        /// <summary>
        /// Whether the mesh's data can be read: always with Read/Write on; without it, only outside play mode, where
        /// the Editor keeps a copy (an upload build, or a manual bake) and not under NDMF's apply-on-play.
        /// </summary>
        internal static bool CanRead(Mesh mesh, bool playing) => mesh.isReadable || !playing;

        static void Fail(Job job, OptimizationReport report, PipelineStage stage, Exception e)
        {
            job.Dispose();
            job.Report.Skip = SkipReason.Failed;
            job.Report.SkipDetail = $"{stage}: {e.Message}";
            job.Report.OutputMesh = null;
            job.Report.KeptVertices = null;
            job.Report.TrianglesAfter = job.Report.TrianglesBefore;
            job.Report.HiddenRemoved = 0;
            report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.RendererFailed, true, job.Renderer, stage.ToString(), e.Message));
            Debug.LogException(e);
        }

        static void Extract(Job job, PipelineInput input, AvatarBones bones, OptimizationReport report)
        {
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Extraction);
                ShapeKeyLiveness[] liveness = null;
                if (job.Renderer is SkinnedMeshRenderer skinned)
                {
                    if (input.Liveness == null || !input.Liveness.TryGetValue(skinned, out liveness) || liveness == null)
                        liveness = Enumerable.Repeat(ShapeKeyLiveness.Live(1f), job.Source.blendShapeCount).ToArray();
                    job.Report.Liveness = liveness;
                }
                job.Eval = RendererExtraction.Extract(job.Renderer, bones, liveness, Allocator.Persistent, out _);
                job.Extracted = true;
                job.Report.Mirrored = RendererExtraction.DrawsMirrored(job.Renderer);
            }
            catch (Exception e)
            {
                Fail(job, report, PipelineStage.Extraction, e);
            }
        }

        static void RemoveHidden(List<Job> jobs, PipelineInput input, AvatarBones bones, OptimizationReport report)
        {
            var alwaysOn = new HashSet<Renderer>(input.Settings.alwaysOn.Where(r => r != null));
            var materials = new OccluderMaterials(input.Settings.materials);
            foreach (var job in jobs)
            {
                if (!alwaysOn.Contains(job.Renderer)) continue;
                // Listed as always on but off as the avatar loads: it covers nothing until something turns it on.
                if (!job.Renderer.enabled || !job.Renderer.gameObject.activeInHierarchy)
                {
                    report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.AlwaysOnIsOff, false, job.Renderer));
                    continue;
                }
                job.Occludes = job.Report.Occludes = true;
            }
            if (!jobs.Any(j => j.Occludes)) return;

            var owned = new List<IDisposable>();
            OcclusionResult[] results = null;
            try
            {
                var meshes = new List<OcclusionMesh>();
                foreach (var job in jobs)
                {
                    var eval = job.Eval;
                    if (job.Report.Mirrored)
                    {
                        // Occlusion reads a face's front by Unity's winding in avatar-root space; Unity draws a
                        // mirrored renderer with its winding reversed, so occlusion sees it reversed too.
                        var flipped = new NativeArray<int3>(eval.Triangles.Length, Allocator.Persistent);
                        owned.Add(flipped);
                        for (var t = 0; t < flipped.Length; t++) flipped[t] = eval.Triangles[t].xzy;
                        eval.Triangles = flipped;
                    }
                    meshes.Add(new OcclusionMesh
                    {
                        Mesh = eval,
                        Occludes = job.Occludes,
                        Submeshes = job.Occludes ? materials.For(job.Renderer, job.Source.subMeshCount) : null,
                    });
                }

                var unstable = UnstableBones.Compute(bones, input.ScaleCurves);
                for (var b = 0; b < unstable.Length; b++)
                    if (unstable[b]) report.UnstableBones.Add(RuntimeUtil.RelativePath(input.Root.gameObject, bones.Transforms[b].gameObject) ?? bones.Transforms[b].name);
                // The occluder side: a face on an unstable bone never covers.
                var boneTable = new OcclusionBones
                {
                    Parents = bones.ParentArray(Allocator.Persistent),
                    Vanishes = new NativeArray<bool>(unstable, Allocator.Persistent),
                };
                owned.Add(boneTable.Parents);
                owned.Add(boneTable.Vanishes);

                results = OcclusionSolver.Solve(meshes, boneTable, input.Occlusion, Allocator.Persistent, out var stats);
                report.OcclusionRan = true;
                report.OcclusionStats = stats;
                var declined = new DeclineCounts();
                for (var i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    job.Hidden = results[i].HiddenTriangles.ToArray();
                    job.Report.KeptOnUnstableBones = KeepUnstable(job, unstable);
                    job.Report.HiddenSourceTriangles = job.Hidden;
                    var d = results[i].Declined;
                    job.Report.Declined = d;
                    declined.Unweighted += d.Unweighted;
                    declined.Swings += d.Swings;
                    declined.SeeThrough += d.SeeThrough;
                    declined.Escaped += d.Escaped;
                    declined.ExposedByFrame += d.ExposedByFrame;
                    declined.ExposedByDeletion += d.ExposedByDeletion;
                }
                report.Declined = declined;
            }
            catch (Exception e)
            {
                // Occlusion decides over every renderer at once, so a failure leaves all of them whole.
                foreach (var job in jobs)
                {
                    job.Hidden = null;
                    job.Report.HiddenSourceTriangles = null;
                }
                report.OcclusionRan = false;
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.OcclusionFailed, true, e.Message));
                Debug.LogException(e);
            }
            finally
            {
                if (results != null) foreach (var r in results) r.Dispose();
                foreach (var o in owned) o.Dispose();
            }
        }

        /// <summary>
        /// The candidate side: a triangle with a corner on an unstable bone can move out from under its cover, so it
        /// is never deleted. This clears flags after the solve and needs no second one, which is why Core does not
        /// know about it: every triangle the solve hides is covered by faces the solve keeps, and keeping more
        /// triangles takes no face away from anyone's cover. Any weight counts, as on the occluder side: however small, it
        /// moves the corner when the bone scales, and to NaN when the bone does. Returns how many triangles it kept.
        /// </summary>
        static int KeepUnstable(Job job, bool[] unstable)
        {
            var eval = job.Eval;
            var kept = 0;
            for (var t = 0; t < job.Hidden.Length; t++)
            {
                if (!job.Hidden[t]) continue;
                var tri = eval.Triangles[t];
                for (var c = 0; c < 3 && job.Hidden[t]; c++)
                    for (var k = eval.WeightStart[tri[c]]; k < eval.WeightStart[tri[c] + 1]; k++)
                    {
                        var w = eval.Weights[k];
                        if (w.Weight <= 0f || w.Bone >= unstable.Length || !unstable[w.Bone]) continue;
                        job.Hidden[t] = false;
                        kept++;
                        break;
                    }
            }
            return kept;
        }

        static void Decimate(List<Job> live, List<Job> all, PipelineInput input, AvatarBones bones, OptimizationReport report)
        {
            var settings = input.Settings;
            var protection = new Protection(input.Root, bones);
            var perRenderer = new Dictionary<Renderer, RendererSettings>();
            foreach (var s in settings.renderers)
                if (s != null && s.renderer != null) perRenderer[s.renderer] = s;

            var stage = Stopwatch.StartNew();
            foreach (var job in live)
            {
                perRenderer.TryGetValue(job.Renderer, out var own);
                job.Report.Weight = Mathf.Max(own?.protection ?? 1f, MinWeight);
                if (own != null && own.excludeFromDecimation)
                {
                    job.Report.ExcludedFromDecimation = true;
                    continue;
                }
                DecimateOne(job, input, protection, report);
            }
            report.Times.Decimation = stage.Elapsed.TotalMilliseconds;

            stage.Restart();
            var decimated = live.Where(j => j.Decimated).ToList();
            // The budget is the avatar's: whatever is not decimated costs its triangles in full.
            var fixedTriangles = all.Where(j => !j.Decimated).Sum(j => j.Report.TrianglesBefore - HiddenCount(j));
            // A triangle that repeats a vertex is outside the collapse sequence, yet a renderer left uncut still draws
            // it; counting it as fixed keeps the budget, at the cost of cutting that many more where a cut drops it.
            fixedTriangles += decimated.Sum(j => j.Reduced.Length - j.Sequence.InitialTriangleCount);
            var entries = decimated.Select(j => new AllocationEntry
            {
                Steps = j.Sequence.Steps,
                InitialTriangleCount = j.Sequence.InitialTriangleCount,
                FloorTriangles = j.Report.FloorTriangles,
                Weight = j.Report.Weight,
            }).ToList();
            if (entries.Count > 0)
            {
                var result = settings.target == TargetKind.TriangleBudget
                    ? BudgetAllocator.ForBudget(entries, Math.Max(0, settings.triangleBudget - fixedTriangles))
                    : BudgetAllocator.ForTolerance(entries, settings.tolerance / 1000f);
                report.ErrorLevel = result.Level;
                for (var i = 0; i < decimated.Count; i++)
                {
                    var job = decimated[i];
                    job.Cut = result.Cuts[i];
                    job.Report.AllocationCut = job.Cut;
                    job.Report.Error = job.Sequence.ErrorAt(job.Cut);
                }
            }
            report.Times.Allocation = stage.Elapsed.TotalMilliseconds;
        }

        static int HiddenCount(Job job)
        {
            if (job.Hidden == null) return 0;
            var n = 0;
            foreach (var h in job.Hidden) if (h) n++;
            return n;
        }

        static void DecimateOne(Job job, PipelineInput input, Protection protection, OptimizationReport report)
        {
            var owned = new List<IDisposable>();
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Decimation);
                var eval = job.Eval;
                var reduced = new List<int>(eval.TriangleCount);
                for (var t = 0; t < eval.TriangleCount; t++)
                    if (job.Hidden == null || !job.Hidden[t]) reduced.Add(t);
                job.Reduced = reduced.ToArray();

                // The decimator sees only what occlusion kept; it shares every vertex array with the extraction.
                var seen = eval;
                seen.Triangles = new NativeArray<int3>(reduced.Count, Allocator.Persistent);
                owned.Add(seen.Triangles);
                seen.TriangleSubmesh = new NativeArray<int>(reduced.Count, Allocator.Persistent);
                owned.Add(seen.TriangleSubmesh);
                for (var i = 0; i < reduced.Count; i++)
                {
                    seen.Triangles[i] = eval.Triangles[reduced[i]];
                    seen.TriangleSubmesh[i] = eval.TriangleSubmesh[reduced[i]];
                }

                var locks = protection.Locks(eval, out var lockedCount);
                var vertexProtection = new VertexProtection();
                if (locks != null)
                {
                    vertexProtection.Locked = new NativeArray<byte>(locks, Allocator.Persistent);
                    owned.Add(vertexProtection.Locked);
                }
                job.Report.LockedVertices = lockedCount;
                job.Report.FloorTriangles = protection.FloorFor(job.Renderer, reduced.Count);

                job.Sequence = Decimator.Run(seen, input.Decimation, vertexProtection, Allocator.Persistent);
                job.Decimated = true;
                job.Report.SequenceLength = job.Sequence.Length;
            }
            catch (Exception e)
            {
                // Decimation failing on one renderer leaves it at what occlusion kept.
                job.Reduced = null;
                job.Report.SequenceLength = 0;
                report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.RendererFailed, true, job.Renderer, PipelineStage.Decimation.ToString(), e.Message));
                Debug.LogException(e);
            }
            finally
            {
                foreach (var o in owned) o.Dispose();
            }
        }

        static void Write(Job job, PipelineInput input, OptimizationReport report)
        {
            if (!job.Extracted) return;
            var hidden = HiddenCount(job);
            if (hidden == 0 && (!job.Decimated || job.Cut == 0))
            {
                job.Report.Skip = SkipReason.Unchanged;
                return;
            }

            CutResult cut = default;
            var cutMade = false;
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Writing);
                var eval = job.Eval;
                var mesh = job.Source;
                var perSubmesh = new List<int>[mesh.subMeshCount];
                for (var s = 0; s < perSubmesh.Length; s++) perSubmesh[s] = new List<int>();

                if (job.Decimated && job.Cut > 0)
                {
                    cut = job.Sequence.Cut(job.Cut, Allocator.Persistent);
                    cutMade = true;
                    for (var i = 0; i < cut.Triangles.Length; i++)
                        Add(perSubmesh[eval.TriangleSubmesh[job.Reduced[cut.SourceTriangle[i]]]], cut.Triangles[i]);
                }
                else
                {
                    for (var t = 0; t < eval.TriangleCount; t++)
                        if (job.Hidden == null || !job.Hidden[t]) Add(perSubmesh[eval.TriangleSubmesh[t]], eval.Triangles[t]);
                }

                var indices = new int[mesh.subMeshCount][];
                var used = new bool[mesh.vertexCount];
                var triangles = 0;
                for (var s = 0; s < indices.Length; s++)
                {
                    // A submesh of lines or points is passed through whole, with its vertices.
                    var isTriangles = mesh.GetTopology(s) == UnityEngine.MeshTopology.Triangles;
                    indices[s] = isTriangles ? perSubmesh[s].ToArray() : mesh.GetIndices(s, true);
                    if (isTriangles) triangles += indices[s].Length / 3;
                    foreach (var i in indices[s]) used[i] = true;
                }
                var keptList = new List<int>();
                for (var v = 0; v < used.Length; v++) if (used[v]) keptList.Add(v);
                var keptArray = keptList.ToArray();
                job.Report.OutputMesh = MeshWriter.Write(mesh, indices, keptArray);
                job.Report.KeptVertices = keptArray;
                job.Report.HiddenRemoved = hidden;
                job.Report.TrianglesAfter = triangles;
            }
            catch (Exception e)
            {
                Fail(job, report, PipelineStage.Writing, e);
            }
            finally
            {
                if (cutMade) cut.Dispose();
            }
        }

        static void Add(List<int> list, int3 t)
        {
            list.Add(t.x);
            list.Add(t.y);
            list.Add(t.z);
        }
    }
}
