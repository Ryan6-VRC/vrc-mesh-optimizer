using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>Why a renderer's mesh was left as it was.</summary>
    public enum SkipReason
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
    }

    /// <summary>
    /// One renderer's part in an optimization. Triangle indices count the source mesh's triangle submeshes in
    /// submesh order, each submesh's triangles in index order; submeshes of other topologies are passed through
    /// and not counted.
    /// </summary>
    public sealed class RendererReport
    {
        public Renderer Renderer { get; internal set; }
        /// <summary>The renderer's path from the avatar root, as the hierarchy shows it.</summary>
        public string Path { get; internal set; }
        /// <summary>The mesh the renderer drew before the pass.</summary>
        public Mesh SourceMesh { get; internal set; }
        /// <summary>The mesh the pass wrote; null when the renderer was left as it was.</summary>
        public Mesh OutputMesh { get; internal set; }

        public int TrianglesBefore { get; internal set; }
        public int HiddenRemoved { get; internal set; }
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
        public int LockedVertices { get; internal set; }
        /// <summary>Triangles occlusion found covered but kept because a corner rides an unstable bone.</summary>
        public int KeptOnUnstableBones { get; internal set; }
        public DeclineCounts Declined { get; internal set; }

        /// <summary>Per source triangle, true when occlusion removed it. Null when occlusion did not run on this renderer.</summary>
        public bool[] HiddenSourceTriangles { get; internal set; }
        /// <summary>Output vertex i is source vertex KeptVertices[i]. Null when no mesh was written.</summary>
        public int[] KeptVertices { get; internal set; }
        /// <summary>Per shape key of the source mesh, what the pass evaluated it as. Null for a renderer that applies no shape keys.</summary>
        public ShapeKeyLiveness[] Liveness { get; internal set; }
    }

    /// <summary>Wall-clock per stage, in milliseconds.</summary>
    public struct StageTimes
    {
        public double Liveness;
        public double Extraction;
        public double Occlusion;
        public double Decimation;
        public double Allocation;
        public double Writing;
        public double Total;
    }

    /// <summary>
    /// What one optimization did to one avatar. The latest few stay in memory, from the pass or from any direct
    /// run of the pipeline, in <see cref="OptimizationReports"/>; nothing is written to disk.
    /// </summary>
    public sealed class OptimizationReport
    {
        public string AvatarName { get; internal set; }
        public List<RendererReport> Renderers { get; } = new List<RendererReport>();

        public TargetKind Target { get; internal set; }
        public int TriangleBudget { get; internal set; }
        /// <summary>In millimetres, as the component holds it.</summary>
        public float Tolerance { get; internal set; }

        public int TrianglesBefore => Renderers.Sum(r => r.TrianglesBefore);
        public int TrianglesAfter => Renderers.Sum(r => r.TrianglesAfter);
        public int HiddenRemoved => Renderers.Sum(r => r.HiddenRemoved);
        /// <summary>The common weighted error level every decimated renderer was cut at, in metres.</summary>
        public float ErrorLevel { get; internal set; }
        public bool TargetMet { get; internal set; }

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
        /// <summary>Material overrides on the component that matched no material on any renderer, so did nothing.</summary>
        public List<Material> UnmatchedMaterialOverrides { get; } = new List<Material>();

        public StageTimes Times;

        /// <summary>User-facing problems, which the pass raises through NDMF's error report.</summary>
        public List<Diagnostic> Diagnostics { get; } = new List<Diagnostic>();

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
            if (UnstableBones.Count > 0)
                sb.Append($"\n  {UnstableBones.Count} unstable bones kept {KeptOnUnstableBones} covered triangles.");
            foreach (var m in UnmatchedMaterialOverrides)
                sb.Append($"\n  material override for {(m != null ? m.name : "<missing>")} matched no material on the avatar.");
            foreach (var r in Renderers.Where(r => r.Skipped))
                sb.Append($"\n  skipped {r.Path}: {r.Skip}{(string.IsNullOrEmpty(r.SkipDetail) ? "" : " (" + r.SkipDetail + ")")}");
            return sb.ToString();
        }
    }

    /// <summary>A user-facing problem: a localized key, its severity and its substitutions ({0}, {1}...).</summary>
    public readonly struct Diagnostic
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
    public static class OptimizationReports
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
