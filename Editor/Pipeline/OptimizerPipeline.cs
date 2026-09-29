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
            /// <summary>This renderer's frames in the avatar's linked configurations.</summary>
            public readonly List<LiveFrame> Linked = new List<LiveFrame>();
            public EvalMesh Eval;
            public bool Extracted;
            public bool Occludes;
            public bool[] Hidden;
            public CollapseSequence Sequence;
            public bool Decimated;
            /// <summary>Per triangle occlusion kept, its layer in the clearance surface; not created when the rule is off.</summary>
            public NativeArray<int> Layers;
            /// <summary>Per triangle the decimator saw, the extracted triangle it is.</summary>
            public int[] Reduced;
            public int Cut;

            public void Dispose()
            {
                if (Extracted) Eval.Dispose();
                Extracted = false;
                if (Decimated) Sequence.Dispose();
                Decimated = false;
                if (Layers.IsCreated) Layers.Dispose();
                Layers = default;
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
                    var job = Survey(renderer, input, report);
                    jobs.Add(job);
                    report.Renderers.Add(job.Report);
                }
                Configure(jobs, input, report);
                foreach (var job in jobs)
                    if (job.Report.Skip == SkipReason.None) Extract(job, input, bones, report);
                report.Times.Extraction = stage.Elapsed.TotalMilliseconds;
                report.UnmatchedMaterialOverrides.AddRange(new OccluderMaterials(settings.materials).Unmatched(jobs.Select(j => j.Renderer)));
                // A never-occludes override that matched nothing left a surface the user said can vanish free to hide others.
                foreach (var o in settings.materials)
                    if (o != null && o.material != null && o.mode == OcclusionMode.NeverOccludes && report.UnmatchedMaterialOverrides.Contains(o.material))
                        report.Diagnostics.Add(new Diagnostic(DiagnosticKeys.NeverOccludesUnmatched, true, o.material));

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

        static Job Survey(Renderer renderer, PipelineInput input, OptimizationReport report)
        {
            var root = input.Root;
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
            else if (renderer is SkinnedMeshRenderer skinned)
            {
                if (input.Liveness == null || !input.Liveness.TryGetValue(skinned, out var liveness) || liveness == null)
                    liveness = LivenessSources.Describe(skinned, root).Keys.Select(k => ShapeKeyClassifier.ClassifyKey(k, null, true)).ToArray();
                r.Liveness = liveness;
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

        /// <summary>
        /// The avatar's linked configurations, from its links: a link at one normalised weight is one state of the avatar, in
        /// which every member key stands at that fraction of its <see cref="ShapeKeyClassifier.FullScale"/> and every other
        /// key where it is held. The weights are the union, normalised, of what <see cref="MeshExtractor.Configurations"/>
        /// gives each member alone, so each member is evaluated at every corner of its own path; configurations come only
        /// from links, never from a product of them. Each renderer takes one frame per configuration it has a member in.
        /// A member the pass does not evaluate as live, or on a renderer it does not extract, is left out; a link left with
        /// fewer than two members, or whose members no longer stand at one normalised weight, moves its keys alone.
        /// </summary>
        static void Configure(List<Job> jobs, PipelineInput input, OptimizationReport report)
        {
            if (input.Links == null) return;
            var byRenderer = new Dictionary<Renderer, Job>();
            foreach (var job in jobs)
                if (job.Report.Skip == SkipReason.None && job.Report.Liveness != null) byRenderer[job.Renderer] = job;

            var next = 1;
            foreach (var link in input.Links)
            {
                var members = new List<(Job job, int key, float full)>();
                foreach (var m in link ?? Array.Empty<LinkedKey>())
                {
                    if (m.Renderer == null || !byRenderer.TryGetValue(m.Renderer, out var job)) continue;
                    var liveness = job.Report.Liveness;
                    if (m.Key < 0 || m.Key >= liveness.Length || m.Key >= job.Source.blendShapeCount || liveness[m.Key].State != ShapeKeyState.Live) continue;
                    if (members.Any(x => x.job == job && x.key == m.Key)) continue;
                    var frameWeights = ShapeKeyFrames.WeightsOf(job.Source, m.Key);
                    members.Add((job, m.Key, ShapeKeyClassifier.FullScale(frameWeights[frameWeights.Length - 1])));
                }
                if (members.Count < 2) continue;
                float Held((Job job, int key, float full) m) => m.job.Report.Liveness[m.key].HeldWeight / m.full;
                if (members.Any(m => Math.Abs(Held(m) - Held(members[0])) > ShapeKeyLinks.Tolerance)) continue;

                var weights = new SortedSet<float>();
                foreach (var (job, key, full) in members)
                {
                    var liveness = job.Report.Liveness[key];
                    foreach (var w in MeshExtractor.Configurations(ShapeKeyFrames.WeightsOf(job.Source, key), liveness, liveness.HeldWeight))
                        weights.Add(w / full);
                }
                if (weights.Count == 0) continue;

                var linkReport = new LinkReport(members.Select(m => new ShapeKeyRef(m.job.Renderer, m.job.Report.Path, m.key, m.job.Source.GetBlendShapeName(m.key))).ToArray());
                report.Links.Add(linkReport);
                foreach (var u in weights)
                {
                    var id = next++;
                    foreach (var group in members.GroupBy(m => m.job))
                        group.Key.Linked.Add(new LiveFrame(id, group.Select(m => m.key).ToArray(), group.Select(m => u * m.full).ToArray()));
                    report.Configurations.Add(new ConfigurationReport(id, linkReport, u, members.Select(m => u * m.full).ToArray()));
                }
            }
        }

        static void Extract(Job job, PipelineInput input, AvatarBones bones, OptimizationReport report)
        {
            try
            {
                input.BeforeStage?.Invoke(job.Renderer, PipelineStage.Extraction);
                job.Eval = RendererExtraction.Extract(job.Renderer, bones, job.Report.Liveness, job.Linked, Allocator.Persistent, out _);
                job.Extracted = true;
                job.Report.Mirrored = RendererExtraction.DrawsMirrored(job.Renderer);
            }
            catch (Exception e)
            {
                Fail(job, report, PipelineStage.Extraction, e);
            }
        }

        /// <summary>
        /// Every renderer owes occlusion the sides its own triangles draw: one whose material draws its back is hidden only
        /// when both sides are covered. Read toward drawing more, the opposite of how <paramref name="blocking"/> (an
        /// occluder's submeshes, or null) reads toward blocking less; Unity draws a material slot past the last submesh
        /// over the last submesh again, so those slots count there. A renderer that blocks nothing has no alpha read.
        /// Each material's outline joins in only with <paramref name="outlines"/>, which follows
        /// <see cref="AvatarMeshOptimizer.outlinesShowInside"/>.
        /// </summary>
        static OccluderSubmesh[] SidesDrawn(Renderer renderer, int submeshCount, OccluderSubmesh[] blocking, bool outlines)
        {
            var materials = renderer.sharedMaterials;
            var result = new OccluderSubmesh[submeshCount];
            for (var s = 0; s < submeshCount; s++)
            {
                var sides = s < materials.Length ? SidesOf(materials[s], outlines) : FaceCulling.Back;
                if (s == submeshCount - 1)
                    for (var k = submeshCount; k < materials.Length; k++)
                        if (materials[k] != null && SidesOf(materials[k], outlines) != sides) sides = FaceCulling.Off;
                var b = blocking?[s];
                result[s] = b == null
                    ? new OccluderSubmesh { Culling = sides }
                    : new OccluderSubmesh { Mode = b.Mode, Culling = b.Culling, Alpha = b.Alpha, CandidateCulling = sides };
            }
            return result;
        }

        /// <summary>
        /// The sides a material draws, as something that might be hidden: its main pass's, joined by a two-pass material's
        /// second pass (see <see cref="MaterialSecondPass"/>), which shows its back in ordinary view through its own
        /// front. With <paramref name="outlines"/>, its outline's sides join too (see <see cref="MaterialOutline"/>): an
        /// outline culled front, the default, draws the back of a triangle whose main pass culls it, so that triangle
        /// draws both sides. That back shows only from inside the garment, so by default the outline is left out.
        /// </summary>
        static FaceCulling SidesOf(Material material, bool outlines)
        {
            var sides = Join(MainSidesOf(material), MaterialSecondPass.SidesOf(material));
            return outlines ? Join(sides, MaterialOutline.SidesOf(material)) : sides;
        }

        /// <summary>Every side either reading draws: one side each, unless they are the same side, draws both.</summary>
        static FaceCulling Join(FaceCulling sides, FaceCulling? more) => more == null || more == sides ? sides : FaceCulling.Off;

        /// <summary>
        /// The sides a material's main pass draws: from the <see cref="OccluderMaterials.CullProperty"/> convention, or
        /// MToon and UTS's <c>_CullMode</c>, or back only for Unity's built-in shaders; any other shader might draw both,
        /// so it is taken to. An empty slot draws nothing.
        /// </summary>
        static FaceCulling MainSidesOf(Material material)
        {
            if (material == null) return FaceCulling.Back;
            if (material.HasProperty(OccluderMaterials.CullProperty)) return OccluderMaterials.CullingOf(material);
            if (material.HasProperty("_CullMode")) return OccluderMaterials.CullingOf(material, "_CullMode");
            var name = material.shader != null ? material.shader.name : "";
            var builtIn = name == "Standard" || name == "Standard (Specular setup)" || name == "Autodesk Interactive" ||
                          name.StartsWith("Legacy Shaders/") || name.StartsWith("Unlit/") || name.StartsWith("Mobile/");
            return builtIn ? FaceCulling.Back : FaceCulling.Off;
        }

        /// <summary>
        /// Triangles the solve kept on a submesh that draws its back only because of an outline, though every corner was
        /// covered on its front: what judging the outline's side cost. At most that, since a front judged alone could still
        /// have been exposed by a live frame, a deletion or a lattice sample inside a large triangle. Counted only when
        /// outlines join the sides drawn; otherwise they kept nothing.
        /// </summary>
        static int KeptForOutline(Job job, in OcclusionResult result)
        {
            var count = job.Source.subMeshCount;
            var blind = SidesDrawn(job.Renderer, count, null, outlines: false);
            var drawn = SidesDrawn(job.Renderer, count, null, outlines: true);
            var eval = job.Eval;
            var kept = 0;
            for (var t = 0; t < eval.TriangleCount; t++)
            {
                var s = eval.TriangleSubmesh[t];
                if (result.HiddenTriangles[t] || blind[s].Culling != FaceCulling.Back || drawn[s].Culling == FaceCulling.Back) continue;
                var tri = eval.Triangles[t];
                if (result.FrontCovered[tri.x] && result.FrontCovered[tri.y] && result.FrontCovered[tri.z]) kept++;
            }
            return kept;
        }

        static void RemoveHidden(List<Job> jobs, PipelineInput input, AvatarBones bones, OptimizationReport report)
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
                        Submeshes = SidesDrawn(job.Renderer, job.Source.subMeshCount, job.Occludes ? materials.For(job.Renderer, job.Source.subMeshCount) : null, outlines),
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
                    job.Report.KeptForOutline = outlines ? KeptForOutline(job, results[i]) : 0;
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
            var surface = input.Decimation.Clearance.Enabled ? ClearanceSurfaceOf(live, input.Decimation.Clearance.Reach) : default;
            report.Times.Clearance = stage.Elapsed.TotalMilliseconds;
            try
            {
                stage.Restart();
                foreach (var job in live)
                {
                    perRenderer.TryGetValue(job.Renderer, out var own);
                    job.Report.Weight = Mathf.Max(own?.protection ?? 1f, MinWeight);
                    if (own != null && own.excludeFromDecimation)
                    {
                        job.Report.ExcludedFromDecimation = true;
                        continue;
                    }
                    DecimateOne(job, input, protection, new ClearanceInput { Surface = surface, TriangleLayer = job.Layers }, report);
                }
                report.Times.Decimation = stage.Elapsed.TotalMilliseconds;
            }
            finally
            {
                surface.Dispose();
            }

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

        /// <summary>
        /// Every renderer's kept surface as one clearance surface, each connected piece of each mesh its own layer, and
        /// each renderer's layer per kept triangle in <see cref="Job.Layers"/>. Kept means what occlusion left: a triangle
        /// it deleted is not there to be passed through. A renderer off at build is in it like any other, since whatever
        /// turns it on shows it against the rest; one excluded from decimation is in it too, unmoved.
        /// </summary>
        static ClearanceSurface ClearanceSurfaceOf(List<Job> live, float reach)
        {
            var sources = new List<ClearanceLayerSource>();
            var owned = new List<IDisposable>();
            try
            {
                var next = 0;
                foreach (var job in live)
                {
                    var eval = job.Eval;
                    var kept = new NativeArray<int3>(eval.TriangleCount - HiddenCount(job), Allocator.Persistent);
                    owned.Add(kept);
                    for (int t = 0, i = 0; t < eval.TriangleCount; t++)
                        if (job.Hidden == null || !job.Hidden[t]) kept[i++] = eval.Triangles[t];
                    job.Layers = Layers.Pieces(eval.Positions, kept, Allocator.Persistent, out var pieces);
                    for (var i = 0; i < job.Layers.Length; i++) job.Layers[i] += next;
                    next += pieces;
                    sources.Add(new ClearanceLayerSource { Positions = eval.Positions, Triangles = kept, TriangleLayer = job.Layers });
                }
                return ClearanceSurface.Build(sources, reach, Allocator.Persistent);
            }
            finally
            {
                foreach (var o in owned) o.Dispose();
            }
        }

        static int HiddenCount(Job job)
        {
            if (job.Hidden == null) return 0;
            var n = 0;
            foreach (var h in job.Hidden) if (h) n++;
            return n;
        }

        static void DecimateOne(Job job, PipelineInput input, Protection protection, in ClearanceInput clearance, OptimizationReport report)
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

                job.Sequence = Decimator.Run(seen, input.Decimation, vertexProtection, clearance, Allocator.Persistent, out var clearanceStats);
                job.Decimated = true;
                job.Report.SequenceLength = job.Sequence.Length;
                job.Report.NearVertices = clearanceStats.NearVertices;
                job.Report.ClearanceRefused = clearanceStats.Refused;
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
