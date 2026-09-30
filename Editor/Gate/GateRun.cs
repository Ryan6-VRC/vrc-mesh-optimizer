using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    /// <summary>What <see cref="GateRun.Measure"/> measures besides crossing as the avatar stands and in the report's linked states.</summary>
    internal sealed class GateRunOptions
    {
        /// <summary>Poses both channels also measure in, usually <see cref="PoseBank.Default"/>; null for none.</summary>
        public IReadOnlyList<GatePose> Poses;
        /// <summary>Whether to measure the geometric channel on every renderer the pass wrote.</summary>
        public bool Geometry;
    }

    /// <summary>One renderer's row of the report, as figures.</summary>
    internal readonly struct RendererFigures
    {
        public readonly string Path;
        public readonly int TrianglesBefore;
        public readonly int TrianglesAfter;
        public readonly int HiddenRemoved;
        public readonly int HiddenRestored;
        /// <summary>The error the cut reached, in metres, before the renderer's weight.</summary>
        public readonly float Error;
        public readonly SkipReason Skip;

        public RendererFigures(RendererReport r)
        {
            Path = r.Path;
            TrianglesBefore = r.TrianglesBefore;
            TrianglesAfter = r.TrianglesAfter;
            HiddenRemoved = r.HiddenRemoved;
            HiddenRestored = r.HiddenRestored;
            Error = r.Error;
            Skip = r.Skip;
        }
    }

    /// <summary>One renderer's meshes by content (<see cref="MeshContent.Hash"/>): the source, null when it cannot be read, and what the pass wrote, null when it wrote nothing.</summary>
    internal readonly struct MeshHashes
    {
        public readonly string Path;
        public readonly ulong? Source;
        public readonly ulong? Output;

        public MeshHashes(string path, ulong? source, ulong? output)
        {
            Path = path;
            Source = source;
            Output = output;
        }
    }

    /// <summary>
    /// The geometric channel's worst figures on one renderer over the configurations of one kind. A direction that sampled
    /// nothing is counted in <see cref="Unsampled"/> and not read for a worst figure.
    /// </summary>
    internal readonly struct GeometryWorst
    {
        public readonly ConfigurationKind Kind;
        public readonly int Configurations;
        /// <summary>The largest p99 of either direction, in metres, and the configuration reading it.</summary>
        public readonly float P99;
        public readonly string P99At;
        /// <summary>The largest max of either direction, in metres, and the configuration, direction and place reading it.</summary>
        public readonly float Max;
        public readonly string MaxAt;
        public readonly int Unmatched;
        public readonly int Unsampled;

        public GeometryWorst(ConfigurationKind kind, IEnumerable<ConfigurationDistance> configurations)
        {
            Kind = kind;
            Configurations = 0;
            P99 = Max = 0f;
            P99At = MaxAt = "-";
            Unmatched = Unsampled = 0;
            foreach (var c in configurations)
            {
                Configurations++;
                foreach (var (d, direction) in new[] { (c.Distance.AToB, "source-to-optimized"), (c.Distance.BToA, "optimized-to-source") })
                {
                    Unmatched += d.Stats.Unmatched;
                    if (!d.Stats.Sampled)
                    {
                        Unsampled++;
                        continue;
                    }
                    if (d.Stats.P99 > P99 || P99At == "-")
                    {
                        P99 = d.Stats.P99;
                        P99At = c.Configuration.ToString();
                    }
                    if (d.Stats.Max > Max || MaxAt == "-")
                    {
                        Max = d.Stats.Max;
                        var p = d.MaxPosition;
                        MaxAt = string.Format(CultureInfo.InvariantCulture, "{0} {1} at ({2:0.000},{3:0.000},{4:0.000}) tri {5}", c.Configuration, direction, p.x, p.y, p.z, d.MaxTriangle);
                    }
                }
            }
        }
    }

    /// <summary>One renderer the geometric channel measured, by its report row's path.</summary>
    internal sealed class GeometryFigures
    {
        public readonly string Path;
        /// <summary>Per configuration kind measured, in <see cref="ConfigurationKind"/> order.</summary>
        public readonly IReadOnlyList<GeometryWorst> Kinds;
        public readonly IReadOnlyList<SkippedConfiguration> Skipped;

        public GeometryFigures(string path, GeometryReport report)
        {
            Path = path;
            Kinds = report.Configurations.GroupBy(c => c.Configuration.Kind).OrderBy(g => g.Key).Select(g => new GeometryWorst(g.Key, g)).ToArray();
            Skipped = report.Skipped;
        }
    }

    /// <summary>
    /// What <see cref="GateRun.Measure"/> read off one build, as plain data keyed by <see cref="RendererReport.Path"/>: it holds
    /// no Unity object, so it outlives the build's clone. Figures only; no threshold is applied and nothing here is a verdict.
    /// </summary>
    internal sealed class GateResult
    {
        /// <summary>
        /// A diagnostic of a stage that failed, so the pass fell back on part of the avatar, or a channel that threw, so its
        /// figures are missing: each a line starting "FAILED", printed first.
        /// </summary>
        public IReadOnlyList<string> Failures { get; internal set; }
        public string AvatarName { get; internal set; }
        public TargetKind Target { get; internal set; }
        public int TriangleBudget { get; internal set; }
        public float Tolerance { get; internal set; }
        public DecimationSettings Decimation { get; internal set; }
        public bool Rejudge { get; internal set; }
        public OcclusionSettings Occlusion { get; internal set; }
        public float ErrorLevel { get; internal set; }
        public int RejudgePasses { get; internal set; }
        public bool TargetMet { get; internal set; }
        public IReadOnlyList<RendererFigures> Renderers { get; internal set; }
        /// <summary>Per report row, its meshes by content, in the report's order.</summary>
        public IReadOnlyList<MeshHashes> Hashes { get; internal set; }
        /// <summary>
        /// What else a written mesh depends on, as measured: whether jobs ran Burst-compiled, since Burst and managed code round
        /// the clearance rule differently at its margin, and where the avatar root stood, since extraction reads bones in world
        /// space. A hash compares against a build made the same way.
        /// </summary>
        public bool BurstCompiled { get; internal set; }
        public Vector3 RootPosition { get; internal set; }
        public Quaternion RootRotation { get; internal set; }
        /// <summary>Every report diagnostic, failures included, as its key and substitutions, a renderer by its path.</summary>
        public IReadOnlyList<string> Diagnostics { get; internal set; }
        /// <summary>Every renderer left out of a channel, by path, with which and why: the report's inputs first, then crossing's.</summary>
        public IReadOnlyList<(string path, string reason)> LeftOut { get; internal set; }
        /// <summary>The linked configurations the crossing channel was handed.</summary>
        public int LinkedConfigurations { get; internal set; }
        /// <summary>Null when the crossing channel threw; <see cref="Failures"/> says why.</summary>
        public CrossingReport Crossing { get; internal set; }
        /// <summary>Null when the geometric channel was not asked for; a renderer whose measure threw is missing, and named in <see cref="Failures"/>.</summary>
        public IReadOnlyList<GeometryFigures> Geometry { get; internal set; }
        /// <summary>Renderers whose geometric measure threw, each named in <see cref="Failures"/>.</summary>
        public int GeometryFailed { get; internal set; }
        public double CrossingMs { get; internal set; }
        public double GeometryMs { get; internal set; }
        public double HashMs { get; internal set; }

        /// <summary>
        /// One line for the console beside the report's summary, in <see cref="Lines"/>' units and digits: any channel that
        /// failed, first; new crossing as the avatar stands, all of it and the same-facing part, and how much of that is
        /// between different renderers; the pose configuration with the most new crossing; the geometric channel's largest max
        /// and where it was read; and the time the gate took.
        /// </summary>
        public string Summary()
        {
            var c = CultureInfo.InvariantCulture;
            string Cm(double m) => (m * 100.0).ToString("0.0", c);
            var sb = new StringBuilder($"[Mesh Optimizer] {AvatarName} gate:");
            var failed = new List<string>();
            if (Crossing == null) failed.Add("crossing channel");
            if (GeometryFailed > 0) failed.Add($"geometric channel on {GeometryFailed} renderers");
            if (failed.Count > 0) sb.Append($" FAILED {string.Join(", ", failed)}, see the gate's lines.");

            if (Crossing != null)
            {
                var standing = Crossing.Pairs.Where(p => p.Configuration == Crossing.Configurations[0]).ToList();
                sb.Append($" crossing {Crossing.Configurations[0]} new {Cm(standing.Sum(p => p.Change.New))} cm, {Cm(standing.Sum(p => p.Change.NewSameFacing))} cm same-facing, " +
                          $"{Cm(standing.Where(p => p.RendererA != p.RendererB).Sum(p => p.Change.NewSameFacing))} cm of it between renderers;");
                var poses = Crossing.Pairs.Where(p => p.Configuration.StartsWith("pose ", StringComparison.Ordinal))
                    .GroupBy(p => p.Configuration).Select(g => (name: g.Key, added: g.Sum(p => p.Change.New), sameFacing: g.Sum(p => p.Change.NewSameFacing)))
                    .OrderByDescending(x => x.added).ToList();
                sb.Append(poses.Count > 0
                    ? $" worst {poses[0].name} new {Cm(poses[0].added)} cm, {Cm(poses[0].sameFacing)} cm same-facing;"
                    : " no pose measured;");
            }

            if (Geometry == null) sb.Append(" geometry not asked for;");
            else
            {
                var worst = Geometry.SelectMany(g => g.Kinds.Select(k => (g.Path, k))).OrderByDescending(x => x.k.Max).FirstOrDefault();
                sb.Append(worst.Path != null
                    ? $" geometry largest max {(worst.k.Max * 1000f).ToString("0.000", c)} mm on {worst.Path} ({worst.k.MaxAt});"
                    : " geometry measured on no renderer;");
            }
            sb.Append($" {(HashMs + CrossingMs + GeometryMs).ToString("0", c)} ms.");
            return sb.ToString();
        }

        /// <summary>
        /// Stable lines: failures first, then the settings the build ran with, the report's figures, what was left out, how the
        /// meshes were built and each mesh's hash by path, the crossing channel in each configuration and its pairs with new
        /// crossing, and the geometric channel's worst figures per renderer and kind. Every timing is on a line of its own, at
        /// the end.
        /// </summary>
        public string[] Lines()
        {
            var c = CultureInfo.InvariantCulture;
            string Mm(float m) => (m * 1000f).ToString("0.000", c);
            var lines = new List<string>(Failures);
            lines.Add($"avatar: {AvatarName}");
            lines.Add($"settings: target={Target} budget={TriangleBudget} tolerance_mm={Tolerance.ToString(c)} rejudge={Rejudge}");
            lines.Add($"settings decimation: {Fields(Decimation)}");
            lines.Add($"settings occlusion: {Fields(Occlusion)}");
            lines.Add($"report: before={Renderers.Sum(r => r.TrianglesBefore)} after={Renderers.Sum(r => r.TrianglesAfter)} hidden={Renderers.Sum(r => r.HiddenRemoved)} " +
                      $"restored={Renderers.Sum(r => r.HiddenRestored)} rejudgePasses={RejudgePasses} errorLevel_mm={Mm(ErrorLevel)} targetMet={TargetMet}");
            foreach (var r in Renderers)
                lines.Add($"  {r.Path} {r.TrianglesBefore}>{r.TrianglesAfter} hidden={r.HiddenRemoved} restored={r.HiddenRestored} err_mm={Mm(r.Error)} skip={r.Skip}");
            foreach (var d in Diagnostics) lines.Add("diagnostic: " + d);
            foreach (var (path, reason) in LeftOut) lines.Add($"leftOut: {path}: {reason}");

            string F(float x) => x.ToString("0.######", c);
            lines.Add($"build: burst={BurstCompiled} root at ({F(RootPosition.x)},{F(RootPosition.y)},{F(RootPosition.z)}) rotation ({F(RootRotation.x)},{F(RootRotation.y)},{F(RootRotation.z)},{F(RootRotation.w)})");
            var ordered = Hashes.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
            lines.Add("-- mesh hashes");
            foreach (var x in ordered.Where(x => x.Output.HasValue)) lines.Add($"  meshhash {x.Path} {x.Output.Value:x16}");
            lines.Add("-- end mesh hashes");
            lines.Add("-- source hashes");
            foreach (var x in ordered) lines.Add($"  sourcehash {x.Path} {(x.Source.HasValue ? x.Source.Value.ToString("x16") : "not readable")}");
            lines.Add("-- end source hashes");

            lines.Add("-- crossing");
            lines.Add($"linked configurations passed to the crossing channel: {LinkedConfigurations}");
            if (Crossing == null) lines.Add("not measured: the channel failed (see FAILED above)");
            else
            {
                foreach (var s in Crossing.Skipped) lines.Add($"  skipped: {s.Name}: {s.Reason}");
                foreach (var configuration in Crossing.Configurations) CrossingLines(configuration, lines);
            }

            if (Geometry != null)
            {
                lines.Add("-- geometry (mm; source-to-optimized is a feature lost, optimized-to-source a spike added)");
                foreach (var g in Geometry)
                {
                    var kinds = g.Kinds.Select(k => $"{Kind(k.Kind)}: {k.Configurations} configurations, worst p99={Mm(k.P99)} ({k.P99At}), worst max={Mm(k.Max)} ({k.MaxAt})");
                    lines.Add($"  {g.Path}: {string.Join(" | ", kinds)} | unmatched={g.Kinds.Sum(k => k.Unmatched)} unsampled={g.Kinds.Sum(k => k.Unsampled)}");
                    foreach (var s in g.Skipped) lines.Add($"  {g.Path}: not measured, {s.Name}: {s.Reason}");
                }
            }

            lines.Add($"hashMs={HashMs.ToString("0", c)}");
            lines.Add($"crossingMs={CrossingMs.ToString("0", c)}");
            if (Geometry != null) lines.Add($"geometryMs={GeometryMs.ToString("0", c)}");
            return lines.ToArray();
        }

        /// <summary>One configuration's totals, then each pair with new crossing, most first, in the measurement runner's format.</summary>
        void CrossingLines(string configuration, List<string> lines)
        {
            var c = CultureInfo.InvariantCulture;
            string Cm(double m) => (m * 100.0).ToString("0.0", c);
            var rows = Crossing.Pairs.Where(p => p.Configuration == configuration).ToList();
            var between = rows.Where(p => p.RendererA != p.RendererB).ToList();
            var self = rows.Where(p => p.RendererA == p.RendererB).ToList();
            lines.Add($"[{configuration}] pairs={rows.Count} before_cm={Cm(rows.Sum(p => p.Change.Before.Length))} after_cm={Cm(rows.Sum(p => p.Change.After.Length))} " +
                      $"new_cm={Cm(rows.Sum(p => p.Change.New))} newSameFacing_cm={Cm(rows.Sum(p => p.Change.NewSameFacing))}" +
                      $" | between different renderers: new_cm={Cm(between.Sum(p => p.Change.New))} newSameFacing_cm={Cm(between.Sum(p => p.Change.NewSameFacing))}" +
                      $" | a renderer against itself: new_cm={Cm(self.Sum(p => p.Change.New))} newSameFacing_cm={Cm(self.Sum(p => p.Change.NewSameFacing))}");
            foreach (var p in rows.Where(p => p.Change.New > 0).OrderByDescending(p => p.Change.New))
            {
                var x = p.Change;
                var at = x.LongestRunAt;
                lines.Add($"    {p.RendererA} x {p.RendererB} before_cm={Cm(x.Before.Length)} after_cm={Cm(x.After.Length)} new_cm={Cm(x.New)} newSameFacing_cm={Cm(x.NewSameFacing)} " +
                          $"runs={x.Runs} longest_cm={Cm(x.LongestRun)} at=({at.x.ToString("0.000", c)},{at.y.ToString("0.000", c)},{at.z.ToString("0.000", c)})");
            }
        }

        static string Kind(ConfigurationKind kind) => kind switch
        {
            ConfigurationKind.AsItStands => "as it stands",
            ConfigurationKind.LiveKeysAtZero => "live keys at zero",
            ConfigurationKind.ShapeKey => "keys",
            _ => "poses",
        };

        /// <summary>Every public field, a nested settings struct's as Outer.Inner, in declaration order.</summary>
        static string Fields(object settings, string prefix = "")
        {
            var parts = new List<string>();
            foreach (var f in settings.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.MetadataToken))
            {
                var value = f.GetValue(settings);
                if (f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum) parts.Add(Fields(value, prefix + f.Name + "."));
                else parts.Add($"{prefix}{f.Name}={Convert.ToString(value, CultureInfo.InvariantCulture)}");
            }
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// The gate as one call over a build's report: both channels over the renderers the report names, keyed by the report's
    /// paths, with every failure named. It reads the report and the avatar and writes only its result: each channel leaves
    /// every renderer's mesh and weights and every transform as it found them, when it throws too.
    /// </summary>
    internal static class GateRun
    {
        static readonly string[] FailureKeys = { DiagnosticKeys.RendererFailed, DiagnosticKeys.OcclusionFailed, DiagnosticKeys.RejudgeFailed, DiagnosticKeys.PassFailed };

        /// <summary>
        /// Hashes every report row's meshes, and measures crossing once over the report's renderers, as the avatar stands, in
        /// the report's linked configurations and in <see cref="GateRunOptions.Poses"/>, and the geometric channel per written
        /// renderer when <see cref="GateRunOptions.Geometry"/> asks, a linked key in its link's configurations and never alone.
        /// A channel that throws is named in <see cref="GateResult.Failures"/> and the rest still measured. Run it while the
        /// build's clone is open (<see cref="GateReportInputs.From"/> refuses it after).
        /// </summary>
        public static GateResult Measure(OptimizationReport report, Transform root, GateRunOptions options)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            options = options ?? new GateRunOptions();
            var inputs = GateReportInputs.From(report);
            var paths = new Dictionary<UnityEngine.Object, string>();
            foreach (var r in report.Renderers)
                if (r.Renderer != null && !paths.ContainsKey(r.Renderer)) paths[r.Renderer] = r.Path;

            var sw = Stopwatch.StartNew();
            var hashes = report.Renderers.Select(r => new MeshHashes(r.Path,
                r.SourceMesh != null && (r.SourceMesh.isReadable || !Application.isPlaying) ? MeshContent.Hash(r.SourceMesh) : (ulong?)null,
                r.OutputMesh != null ? MeshContent.Hash(r.OutputMesh) : (ulong?)null)).ToArray();
            var hashMs = sw.Elapsed.TotalMilliseconds;

            var failures = new List<string>();
            var diagnostics = new List<string>();
            foreach (var d in report.Diagnostics)
            {
                var line = $"{d.Key}{(d.Warning ? " (warning)" : "")}: {string.Join(", ", (d.Args ?? Array.Empty<object>()).Select(a => Arg(a, paths)))}";
                diagnostics.Add(line);
                if (FailureKeys.Contains(d.Key)) failures.Add("FAILED in the pass, which fell back: " + line);
            }

            sw.Restart();
            CrossingReport crossing = null;
            try
            {
                crossing = CrossingGate.Measure(root, inputs.Crossing, options.Poses, inputs.Configurations, CrossingSettings.Default);
            }
            catch (Exception e)
            {
                failures.Add($"FAILED crossing channel, no crossing figure: {e.GetType().Name}: {e.Message}");
            }
            var crossingMs = sw.Elapsed.TotalMilliseconds;

            List<GeometryFigures> geometry = null;
            var geometryFailed = 0;
            sw.Restart();
            if (options.Geometry)
            {
                geometry = new List<GeometryFigures>();
                foreach (var input in inputs.Geometry)
                {
                    var path = paths[input.Renderer];
                    try
                    {
                        var measured = GeometryGate.MeasureInput(input, root, options.Poses, inputs.Configurations, SurfaceDistanceSettings.Default);
                        geometry.Add(new GeometryFigures(path, measured));
                    }
                    catch (Exception e)
                    {
                        failures.Add($"FAILED geometric channel on {path}, no geometric figure for it: {e.GetType().Name}: {e.Message}");
                        geometryFailed++;
                    }
                }
            }

            return new GateResult
            {
                Failures = failures,
                AvatarName = report.AvatarName,
                Target = report.Target,
                TriangleBudget = report.TriangleBudget,
                Tolerance = report.Tolerance,
                Decimation = report.Decimation,
                Rejudge = report.Rejudge,
                Occlusion = report.Occlusion,
                ErrorLevel = report.ErrorLevel,
                RejudgePasses = report.RejudgePasses,
                TargetMet = report.TargetMet,
                Renderers = report.Renderers.Select(r => new RendererFigures(r)).ToArray(),
                Hashes = hashes,
                BurstCompiled = JobsUtility.JobCompilerEnabled,
                RootPosition = root.position,
                RootRotation = root.rotation,
                Diagnostics = diagnostics,
                LeftOut = inputs.LeftOut.Concat(crossing?.LeftOut ?? Array.Empty<(string, string)>()).ToArray(),
                LinkedConfigurations = inputs.Configurations.Count,
                Crossing = crossing,
                Geometry = geometry,
                GeometryFailed = geometryFailed,
                CrossingMs = crossingMs,
                GeometryMs = sw.Elapsed.TotalMilliseconds,
                HashMs = hashMs,
            };
        }

        /// <summary>A diagnostic's substitution as text: a renderer by its report path, any other Unity object by name.</summary>
        static string Arg(object arg, Dictionary<UnityEngine.Object, string> paths) => arg switch
        {
            UnityEngine.Object o when o != null && paths.TryGetValue(o, out var path) => path,
            UnityEngine.Object o => o != null ? o.name : "<missing>",
            null => "<null>",
            _ => Convert.ToString(arg, CultureInfo.InvariantCulture),
        };
    }
}
