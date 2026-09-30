using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>One extracted renderer as a cut leaves it, for <see cref="Rejudging.Restored"/>.</summary>
    internal struct CutRenderer
    {
        public EvalMesh Eval;
        public bool Mirrored;
        public bool Occludes;
        /// <summary>Per submesh, the sides it draws and how it blocks, as the first solve read them.</summary>
        public OccluderSubmesh[] Submeshes;
        /// <summary>Per evaluation triangle, true for one occlusion removed that the cut leaves out; null when it leaves none out.</summary>
        public bool[] Removed;
        /// <summary>The triangles the cut draws, and per triangle the evaluation triangle it came from; both null when it draws its whole source.</summary>
        public int3[] Drawn;
        public int[] DrawnSource;
        /// <summary>
        /// For an always-on renderer the cut decimated, its own recorded error at its step, not the common level: how far
        /// its faces may have moved past a sample they covered (<see cref="OcclusionMesh.Setback"/>). An undecimated layer
        /// did not move, so it never covers from behind, and one with protection below 1 moves further than the level.
        /// Zero otherwise.
        /// </summary>
        public float Setback;
        /// <summary>What covered each side of each sample in the first solve (<see cref="OcclusionResult.CoveredBy"/>, <see cref="OcclusionResult.InteriorCoveredBy"/>).</summary>
        public ulong[] CoveredBy, InteriorCoveredBy;
    }

    /// <summary>
    /// Judging a cut's removals again. Occlusion judges a triangle hidden against the always-on renderers' source, and a
    /// cut then moves those faces, so a removed triangle can lose its cover. Every triangle a cut leaves out as hidden is
    /// judged again by the first solve's rule, settings, bone table and configurations, with the triangles the always-on
    /// renderers are written with as the only blockers; one no longer hidden is written back.
    /// </summary>
    internal static class Rejudging
    {
        /// <summary>
        /// Per renderer, per evaluation triangle, true for a removed one no longer covered, null where none is; the
        /// renderers are every extracted one, each at its index in the first solve, so that solve's names for what covered
        /// each sample hold here. <paramref name="count"/> is how many come back.
        ///
        /// A renderer that draws its whole source removes nothing. A removed triangle comes back when its cover is gone, not
        /// when the cut moved its cover past it. So a sample the rule finds uncovered stays covered when a face of a cut
        /// always-on renderer lies straight behind it within that cut's recorded error (<see cref="CutRenderer.Setback"/>),
        /// and only a renderer that covered it in the first solve (<see cref="CutRenderer.CoveredBy"/>): a layer that lies
        /// just under a triangle and never covered it does not stand in for the cover it lost.
        /// </summary>
        public static bool[][] Restored(IReadOnlyList<CutRenderer> renderers, int[] boneParents, bool[] vanishingBones,
            OcclusionSettings settings, out int count)
        {
            var owned = new List<IDisposable>();
            OcclusionResult[] results = null;
            try
            {
                var meshes = new List<OcclusionMesh>();
                var sources = new List<int[]>();
                foreach (var r in renderers)
                {
                    var removed = r.Removed;
                    // Neither judged nor blocking, a renderer changes no verdict, and holds its index with no triangles.
                    var bystander = !r.Occludes && removed == null;
                    var drawn = bystander ? Array.Empty<int3>() : r.Drawn ?? r.Eval.Triangles.ToArray();
                    var drawnSource = bystander ? Array.Empty<int>() : r.DrawnSource ?? Enumerable.Range(0, drawn.Length).ToArray();
                    var mesh = OcclusionInput.Rejudged(r.Eval, r.Mirrored, r.Occludes, r.Submeshes, removed, drawn, drawnSource, owned, out var source);
                    mesh.Setback = r.Setback;
                    if (removed != null)
                    {
                        mesh.CoveredBy = r.CoveredBy;
                        // Front and back, per triangle entered, from the evaluation triangle it is.
                        mesh.InteriorCoveredBy = new ulong[2 * source.Length];
                        for (var e = 0; e < source.Length; e++)
                            if (mesh.Candidate[e])
                                for (var side = 0; side < 2; side++) mesh.InteriorCoveredBy[2 * e + side] = r.InteriorCoveredBy[2 * source[e] + side];
                    }
                    meshes.Add(mesh);
                    sources.Add(source);
                }
                var bones = new OcclusionBones { Parents = new NativeArray<int>(boneParents, Allocator.Persistent) };
                owned.Add(bones.Parents);
                bones.Vanishes = new NativeArray<bool>(vanishingBones, Allocator.Persistent);
                owned.Add(bones.Vanishes);

                results = OcclusionSolver.Solve(meshes, bones, settings, Allocator.Persistent, out _);
                var restored = new bool[renderers.Count][];
                count = 0;
                for (var k = 0; k < meshes.Count; k++)
                {
                    var candidate = meshes[k].Candidate;
                    var hidden = results[k].HiddenTriangles;
                    var source = sources[k];
                    for (var e = 0; e < candidate.Length; e++)
                    {
                        if (!candidate[e] || hidden[e]) continue;
                        restored[k] ??= new bool[renderers[k].Eval.TriangleCount];
                        restored[k][source[e]] = true;
                        count++;
                    }
                }
                return restored;
            }
            finally
            {
                if (results != null) foreach (var r in results) r.Dispose();
                foreach (var o in owned) o.Dispose();
            }
        }
    }
}
