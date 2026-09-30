using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>Why a renderer's mesh was left as it was.</summary>
    internal enum SkipReason
    {
        None,
        /// <summary>The renderer draws no mesh.</summary>
        NoMesh,
        /// <summary>The mesh has no triangles to judge.</summary>
        NoTriangles,
        /// <summary>The mesh's data cannot be read in this session (Read/Write off, in play mode).</summary>
        NotReadable,
        /// <summary>A Cloth component addresses the mesh's vertices by index, so they cannot change.</summary>
        Cloth,
        /// <summary>A stage threw while processing this renderer; <see cref="RendererReport.SkipDetail"/> holds the message.</summary>
        Failed,
        /// <summary>Nothing hid a triangle and decimation cut none.</summary>
        Unchanged,
        /// <summary>
        /// A vertex position or texture coordinate, or a shape key's delta, is not a finite number, which every rule that
        /// measures a distance would read as no distance.
        /// </summary>
        NonFinite,
        /// <summary>Two shape keys share a name, and Unity addresses a key by name, so the mesh cannot be written back with both.</summary>
        DuplicateShapeKey,
    }

    /// <summary>
    /// One renderer's part in an optimization. Triangle indices count the source mesh's triangle submeshes in
    /// submesh order, each submesh's triangles in index order; submeshes of other topologies are passed through
    /// and not counted.
    /// </summary>
    internal sealed class RendererReport
    {
        public Renderer Renderer { get; internal set; }
        /// <summary>The renderer's path from the avatar root, as the hierarchy shows it.</summary>
        public string Path { get; internal set; }
        /// <summary>The mesh the renderer drew before the pass.</summary>
        public Mesh SourceMesh { get; internal set; }
        /// <summary>The mesh the pass wrote; null when the renderer was left as it was.</summary>
        public Mesh OutputMesh { get; internal set; }

        public int TrianglesBefore { get; internal set; }
        /// <summary>Triangles the written mesh leaves out as hidden: those set in <see cref="HiddenSourceTriangles"/>.</summary>
        public int HiddenRemoved { get; internal set; }
        /// <summary>
        /// Triangles occlusion found hidden but the written mesh draws, at their source corners, because decimation took
        /// their cover: judged again against what the always-on renderers are written with, they were no longer hidden.
        /// Not counted in <see cref="HiddenRemoved"/>; counted in <see cref="TrianglesAfter"/>.
        /// </summary>
        public int HiddenRestored { get; internal set; }
        public int TrianglesAfter { get; internal set; }

        public SkipReason Skip { get; internal set; }
        public bool Skipped => Skip != SkipReason.None && Skip != SkipReason.Unchanged;
        public string SkipDetail { get; internal set; }

        /// <summary>True for a renderer listed as always on and active at build: one that may hide others' triangles.</summary>
        public bool Occludes { get; internal set; }
        /// <summary>True when Unity draws the renderer mirrored, so occlusion read its faces with the winding reversed.</summary>
        public bool Mirrored { get; internal set; }
        public bool ExcludedFromDecimation { get; internal set; }
        /// <summary>Collapse steps recorded for this renderer; 0 when it was not decimated.</summary>
        public int SequenceLength { get; internal set; }
        /// <summary>The step its sequence was cut at.</summary>
        public int AllocationCut { get; internal set; }
        /// <summary>The error the cut reached, in metres, before the renderer's weight.</summary>
        public float Error { get; internal set; }
        public int FloorTriangles { get; internal set; }
        public float Weight { get; internal set; }
        /// <summary>
        /// Vertices no collapse could remove: on a finger, on a bone a clip grows from zero scale, or of a triangle drawn
        /// with fur.
        /// </summary>
        public int LockedVertices { get; internal set; }
        /// <summary>
        /// Of <see cref="LockedVertices"/>, those with weight on a bone that stands at zero scale and a clip grows: geometry
        /// with no size as the avatar stands, whose cost it cannot tell.
        /// </summary>
        public int LockedFromZeroScale { get; internal set; }
        /// <summary>Of <see cref="LockedVertices"/>, those of a triangle drawn with fur, whose density is its triangle count.</summary>
        public int LockedForFur { get; internal set; }
        /// <summary>
        /// Vertices whose removal cost was multiplied by how much a clip can grow a bone they ride, so it is charged at the
        /// size the geometry can be shown at.
        /// </summary>
        public int CostRaisedForGrowth { get; internal set; }
        /// <summary>Vertices with another layer near enough for the clearance rule to act on them; 0 when it did not run.</summary>
        public int NearVertices { get; internal set; }
        /// <summary>Collapses the clearance rule refused because they came too near another layer.</summary>
        public long ClearanceRefused { get; internal set; }
        /// <summary>Triangles occlusion found covered but kept because a corner rides an unstable bone.</summary>
        public int KeptOnUnstableBones { get; internal set; }
        /// <summary>Triangles occlusion found covered but kept because their material grows fur past the cover.</summary>
        public int KeptForFur { get; internal set; }
        /// <summary>
        /// Triangles occlusion kept because an outline draws their back, though their front was covered: at most what
        /// judging the outline's side cost this renderer. Zero unless the component's outlines show the inside.
        /// </summary>
        public int KeptForOutline { get; internal set; }
        public DeclineCounts Declined { get; internal set; }

        /// <summary>
        /// Per source triangle, true when occlusion removed it: for a written mesh, those it leaves out, after re-judging put
        /// back any whose cover the cut took. Null when occlusion did not run on this renderer.
        /// </summary>
        public bool[] HiddenSourceTriangles { get; internal set; }
        /// <summary>Output vertex i is source vertex KeptVertices[i]. Null when no mesh was written.</summary>
        public int[] KeptVertices { get; internal set; }
        /// <summary>Per shape key of the source mesh, what the pass evaluated it as. Null for a renderer that applies no shape keys.</summary>
        public ShapeKeyLiveness[] Liveness { get; internal set; }
    }

    /// <summary>One shape key on one renderer: the renderer, its path from the avatar root, and the key by index and name.</summary>
    internal readonly struct ShapeKeyRef
    {
        public readonly Renderer Renderer;
        public readonly string Path;
        public readonly int Key;
        public readonly string Name;

        public ShapeKeyRef(Renderer renderer, string path, int key, string name)
        {
            Renderer = renderer;
            Path = path;
            Key = key;
            Name = name;
        }

        public override string ToString() => $"{Path}:{Name}";
    }

    /// <summary>Shape keys the pass found cannot be driven apart, and so evaluated only together.</summary>
    internal sealed class LinkReport
    {
        public IReadOnlyList<ShapeKeyRef> Keys { get; }

        internal LinkReport(IReadOnlyList<ShapeKeyRef> keys) => Keys = keys;

        public override string ToString() => string.Join(", ", Keys);
    }

    /// <summary>
    /// One state of the whole avatar occlusion evaluated besides rest: a link's keys, each at <see cref="Weight"/> of its full
    /// scale, every other key on every renderer where it is held. <see cref="Id"/> is the id its frames carry.
    /// </summary>
    internal sealed class ConfigurationReport
    {
        public int Id { get; }
        public LinkReport Link { get; }
        /// <summary>The fraction of its full scale every key of the link stands at.</summary>
        public float Weight { get; }
        /// <summary>Per key of <see cref="Link"/>, in order, the weight it stands at, in its renderer's units.</summary>
        public IReadOnlyList<float> Weights { get; }

        internal ConfigurationReport(int id, LinkReport link, float weight, IReadOnlyList<float> weights)
        {
            Id = id;
            Link = link;
            Weight = weight;
            Weights = weights;
        }

        public override string ToString() => $"{Link} at {Weight.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Wall-clock per stage, in milliseconds.</summary>
    internal struct StageTimes
    {
        public double Liveness;
        public double Extraction;
        public double Occlusion;
        /// <summary>Building the surface every layer keeps its clearance from.</summary>
        public double Clearance;
        public double Decimation;
        public double Allocation;
        /// <summary>Judging the removed triangles again, over every pass a budget took; cutting for those passes is in <see cref="Allocation"/> and <see cref="Writing"/>.</summary>
        public double Rejudging;
        /// <summary>Cutting every renderer at its step, over every pass, and writing the meshes.</summary>
        public double Writing;
        public double Total;
    }

    /// <summary>
    /// What one optimization did to one avatar. The latest few builds' stay in memory, in
    /// <see cref="OptimizationReports"/>; nothing is written to disk.
    /// </summary>
    internal sealed class OptimizationReport
    {
        public string AvatarName { get; internal set; }
        public List<RendererReport> Renderers { get; } = new List<RendererReport>();

        public TargetKind Target { get; internal set; }
        public int TriangleBudget { get; internal set; }
        /// <summary>In millimetres, as the component holds it.</summary>
        public float Tolerance { get; internal set; }
        /// <summary>
        /// The settings besides the target that decide a mesh, as the cut ran with them: decimation's (the normal and UV
        /// weights, clearance and its share, bends), whether it re-judged, and occlusion's. A figure read off this build is
        /// read at these.
        /// </summary>
        public DecimationSettings Decimation { get; internal set; }
        public bool Rejudge { get; internal set; }
        public OcclusionSettings Occlusion { get; internal set; }

        public int TrianglesBefore => Renderers.Sum(r => r.TrianglesBefore);
        public int TrianglesAfter => Renderers.Sum(r => r.TrianglesAfter);
        public int HiddenRemoved => Renderers.Sum(r => r.HiddenRemoved);
        /// <summary>Removed triangles written back because the cut took their cover (<see cref="RendererReport.HiddenRestored"/>).</summary>
        public int HiddenRestored => Renderers.Sum(r => r.HiddenRestored);
        /// <summary>
        /// How many times the cut's removals were judged again: 0 when no cut moved an always-on renderer while something
        /// was removed, or re-judging is off; more than 1 only when a budget cut deeper to pay for what was restored.
        /// </summary>
        public int RejudgePasses { get; internal set; }
        /// <summary>The common weighted error level every decimated renderer was cut at, in metres.</summary>
        public float ErrorLevel { get; internal set; }
        public bool TargetMet { get; internal set; }

        /// <summary>Groups of shape keys the pass evaluated only together, over the renderers it extracted.</summary>
        public List<LinkReport> Links { get; } = new List<LinkReport>();
        /// <summary>
        /// The states of the whole avatar the links make, each a link at one weight. Every other live key was evaluated moved
        /// alone, its renderer taking its own frame and every other renderer at rest.
        /// </summary>
        public List<ConfigurationReport> Configurations { get; } = new List<ConfigurationReport>();
        /// <summary>
        /// Per reason, how many shape keys the pass evaluated moved alone though another key is driven by exactly the same set
        /// of clips of known identity, so whatever they kept exposed cannot be told from a real exposure by the links alone. A
        /// key driven only through clips of unknown identity is never counted (<see cref="LinkRefusal"/>).
        /// </summary>
        public Dictionary<LinkRefusal, int> LinkRefusals { get; } = new Dictionary<LinkRefusal, int>();

        public bool OcclusionRan { get; internal set; }
        public DeclineCounts Declined { get; internal set; }
        public OcclusionStats OcclusionStats { get; internal set; }
        /// <summary>
        /// Bones an animation can scale or Modular Avatar can delete from, and every bone under them, by path: faces
        /// on them neither cover nor are deleted as hidden. Filled only when occlusion runs.
        /// </summary>
        public List<string> UnstableBones { get; } = new List<string>();
        /// <summary>Triangles occlusion found covered but kept because a corner rides an unstable bone.</summary>
        public int KeptOnUnstableBones => Renderers.Sum(r => r.KeptOnUnstableBones);
        /// <summary>Triangles occlusion kept because an outline draws their back, though their front was covered.</summary>
        public int KeptForOutline => Renderers.Sum(r => r.KeptForOutline);
        /// <summary>Material overrides on the component that matched no material on any renderer, so did nothing.</summary>
        public List<Material> UnmatchedMaterialOverrides { get; } = new List<Material>();

        public StageTimes Times;

        /// <summary>User-facing problems, which the pass raises through NDMF's error report.</summary>
        public List<Diagnostic> Diagnostics { get; } = new List<Diagnostic>();

        /// <summary>
        /// The gate's figures on this build, measured once its meshes were applied, when the build was switched to measure
        /// them (<see cref="BuildSettings.OverrideGate(bool)"/>); null otherwise, and when the measure threw
        /// (<see cref="DiagnosticKeys.GateFailed"/>). Plain data, so it outlives the build's clone.
        /// </summary>
        internal GateResult Gate { get; set; }

        /// <summary>A few lines for the console.</summary>
        public string Summary()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append($"[Mesh Optimizer] {AvatarName}: {TrianglesBefore} -> {TrianglesAfter} triangles");
            sb.Append($" ({HiddenRemoved} hidden removed, {TrianglesBefore - HiddenRemoved - TrianglesAfter} decimated). ");
            sb.Append(Target == TargetKind.TriangleBudget
                ? $"Budget {TriangleBudget} {(TargetMet ? "met" : "NOT met")}"
                : $"Tolerance {Tolerance.ToString("0.###", c)} mm");
            sb.Append($", error level {(ErrorLevel * 1000f).ToString("0.###", c)} mm, {Times.Total.ToString("0", c)} ms.");
            if (HiddenRestored > 0)
                sb.Append($"\n  {HiddenRestored} hidden triangles kept because decimation took their cover.");
            var refused = Renderers.Sum(r => r.ClearanceRefused);
            if (refused > 0)
                sb.Append($"\n  clearance:{Renderers.Sum(r => r.NearVertices)} vertices near another layer, {refused} collapses refused.");
            foreach (var link in Links)
                sb.Append($"\n  linked {link}: {Configurations.Count(x => x.Link == link)} configurations.");
            foreach (LinkRefusal reason in System.Enum.GetValues(typeof(LinkRefusal)))
                if (LinkRefusals.TryGetValue(reason, out var keys) && keys > 0)
                    sb.Append($"\n  {keys} shape keys driven with another moved alone: {reason}.");
            if (UnstableBones.Count > 0)
                sb.Append($"\n  {UnstableBones.Count} unstable bones kept {KeptOnUnstableBones} covered triangles.");
            var fromZero = Renderers.Sum(r => r.LockedFromZeroScale);
            if (fromZero > 0)
                sb.Append($"\n  {fromZero} vertices on bones a clip grows from zero scale were kept from decimation.");
            var raised = Renderers.Sum(r => r.CostRaisedForGrowth);
            if (raised > 0)
                sb.Append($"\n  {raised} vertices on bones a clip grows were costed at their grown size.");
            var furLocked = Renderers.Sum(r => r.LockedForFur);
            if (furLocked > 0)
                sb.Append($"\n  {furLocked} vertices drawn with fur were kept from decimation.");
            var fur = Renderers.Sum(r => r.KeptForFur);
            if (fur > 0)
                sb.Append($"\n  fur kept {fur} covered triangles.");
            if (KeptForOutline > 0)
                sb.Append($"\n  outlines kept up to {KeptForOutline} triangles whose front was covered.");
            foreach (var m in UnmatchedMaterialOverrides)
                sb.Append($"\n  material override for {(m != null ? m.name : "<missing>")} matched no material on the avatar.");
            foreach (var r in Renderers.Where(r => r.Skipped))
                sb.Append($"\n  skipped {r.Path}: {r.Skip}{(string.IsNullOrEmpty(r.SkipDetail) ? "" : " (" + r.SkipDetail + ")")}");
            return sb.ToString();
        }
    }

    /// <summary>A user-facing problem: a localized key, its severity and its substitutions ({0}, {1}...).</summary>
    internal readonly struct Diagnostic
    {
        public readonly string Key;
        /// <summary>True when the problem left work undone that the user can fix; false for a notice.</summary>
        public readonly bool Warning;
        public readonly object[] Args;

        public Diagnostic(string key, bool warning, params object[] args)
        {
            Key = key;
            Warning = warning;
            Args = args;
        }
    }

    /// <summary>
    /// The most recent optimization reports, newest last. Each holds its meshes, which keeps them loaded, so only a
    /// few are kept; a domain reload or <see cref="Clear"/> lets them go.
    /// </summary>
    internal static class OptimizationReports
    {
        const int Kept = 4;
        static readonly List<OptimizationReport> _recent = new List<OptimizationReport>();

        public static IReadOnlyList<OptimizationReport> Recent => _recent;
        public static OptimizationReport Latest => _recent.Count > 0 ? _recent[_recent.Count - 1] : null;

        /// <summary>The newest report for an avatar by its root's name; a build clone's name ends "(Clone)".</summary>
        public static OptimizationReport For(string avatarName) => _recent.LastOrDefault(r => r.AvatarName == avatarName);

        public static void Clear() => _recent.Clear();

        internal static void Add(OptimizationReport report)
        {
            _recent.Add(report);
            if (_recent.Count > Kept) _recent.RemoveAt(0);
        }
    }
}
