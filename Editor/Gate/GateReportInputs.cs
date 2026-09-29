using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    /// <summary>
    /// What an <see cref="OptimizationReport"/> gives the gate channels: the renderers the geometric channel measures, the
    /// renderers the crossing channel measures, and every renderer left out of one or both, with why.
    /// </summary>
    internal sealed class GateReportInputs
    {
        /// <summary>Renderers the pass wrote a mesh for: each against its own source, by <see cref="GeometryGate"/>.</summary>
        public readonly IReadOnlyList<GateInput> Geometry;
        /// <summary>
        /// Every renderer with a readable mesh, written or not, for <see cref="CrossingGate"/>: a renderer the pass left as
        /// it was can still have a changed one driven through it, so it takes part with its source mesh on both sides.
        /// </summary>
        public readonly IReadOnlyList<GateInput> Crossing;
        /// <summary>Each renderer's path, and which channels it is left out of and why.</summary>
        public readonly IReadOnlyList<(string path, string reason)> LeftOut;
        /// <summary>
        /// For <see cref="CrossingGate"/>, one state of the whole avatar per configuration the report's links made: each linked
        /// key at its weight in it, every other key of every renderer in <see cref="Crossing"/> where it stands. A linked key
        /// on a renderer left out of the crossing channel is left out of its configuration.
        /// </summary>
        public readonly IReadOnlyList<AvatarConfiguration> Configurations;

        GateReportInputs(List<GateInput> geometry, List<GateInput> crossing, List<(string, string)> leftOut, List<AvatarConfiguration> configurations)
        {
            Geometry = geometry;
            Crossing = crossing;
            LeftOut = leftOut;
            Configurations = configurations;
        }

        /// <summary>
        /// Reads a report while the avatar it describes is still open. On a build that is the build's clone, whose
        /// renderers and written meshes are destroyed when the build ends, so the gate runs inside the build; a report
        /// read after that is refused, naming what is gone, rather than read as a gate with nothing to measure.
        /// </summary>
        public static GateReportInputs From(OptimizationReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var gone = report.Renderers.Where(Destroyed).Select(r => r.Path).ToArray();
            if (gone.Length > 0)
                throw new InvalidOperationException(
                    $"the report for '{report.AvatarName}' names renderers or meshes that have been destroyed ({string.Join(", ", gone)}): " +
                    "a build destroys its clone when it ends, so run the gate on the report inside the build, while the clone is open");

            var geometry = new List<GateInput>();
            var crossing = new List<GateInput>();
            var leftOut = new List<(string, string)>();
            foreach (var r in report.Renderers)
            {
                if (r.SourceMesh == null || r.Skip == SkipReason.NoMesh || r.Skip == SkipReason.NoTriangles || r.Skip == SkipReason.NotReadable)
                {
                    leftOut.Add((r.Path, $"left out of both channels: {Why(r)}"));
                    continue;
                }
                var liveness = Liveness(r);
                if (r.OutputMesh != null)
                {
                    var input = new GateInput(r.Renderer, r.SourceMesh, r.OutputMesh, r.HiddenSourceTriangles, liveness);
                    geometry.Add(input);
                    crossing.Add(input);
                    continue;
                }
                leftOut.Add((r.Path, $"left out of the geometric channel, measured unchanged for crossing: {Why(r)}"));
                crossing.Add(new GateInput(r.Renderer, r.SourceMesh, r.SourceMesh, null, liveness));
            }
            return new GateReportInputs(geometry, crossing, leftOut, Linked(report, crossing));
        }

        static List<AvatarConfiguration> Linked(OptimizationReport report, List<GateInput> crossing)
        {
            var result = new List<AvatarConfiguration>();
            foreach (var c in report.Configurations)
            {
                var weights = new Dictionary<Renderer, float[]>();
                for (var i = 0; i < c.Link.Keys.Count; i++)
                {
                    var key = c.Link.Keys[i];
                    var input = crossing.FirstOrDefault(x => x.Renderer == key.Renderer);
                    if (input == null || input.Skinned == null) continue;
                    if (!weights.TryGetValue(input.Renderer, out var w)) weights[input.Renderer] = w = input.StandingWeights(out _);
                    w[key.Key] = c.Weights[i];
                }
                if (weights.Count > 0) result.Add(new AvatarConfiguration($"linked {c}", null, weights));
            }
            return result;
        }

        /// <summary>A reference the report holds whose Unity object is gone; a mesh the pass never wrote is null outright and is not gone.</summary>
        static bool Destroyed(RendererReport r) =>
            r.Renderer == null || (!ReferenceEquals(r.SourceMesh, null) && r.SourceMesh == null) || (!ReferenceEquals(r.OutputMesh, null) && r.OutputMesh == null);

        static string Why(RendererReport r) => string.IsNullOrEmpty(r.SkipDetail) ? r.Skip.ToString() : $"{r.Skip} ({r.SkipDetail})";

        /// <summary>
        /// The report's liveness, or, for a skinned renderer the pass never evaluated, every key held at the weight the
        /// renderer holds now, in every configuration.
        /// </summary>
        static IReadOnlyList<ShapeKeyLiveness> Liveness(RendererReport r)
        {
            if (!(r.Renderer is SkinnedMeshRenderer skinned)) return null;
            if (r.Liveness != null && r.Liveness.Length == r.SourceMesh.blendShapeCount) return r.Liveness;
            var held = new ShapeKeyLiveness[r.SourceMesh.blendShapeCount];
            for (var k = 0; k < held.Length; k++)
            {
                var weight = skinned.sharedMesh != null && k < skinned.sharedMesh.blendShapeCount ? skinned.GetBlendShapeWeight(k) : 0f;
                held[k] = weight != 0f ? ShapeKeyLiveness.Frozen(weight) : ShapeKeyLiveness.Dead;
            }
            return held;
        }
    }
}
