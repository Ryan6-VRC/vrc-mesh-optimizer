using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Unity.Collections;
using Unity.Mathematics;
using static Ryan6Vrc.MeshOptimizer.Tests.Decimation.LayerFixtures;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// What the clearance rule costs, in time and in triangles. Explicit, so the gate skips it; run it by name.
    /// </summary>
    public class ClearanceBenchmark
    {
        static readonly float[] Levels = { 0.0005f, 0.001f, 0.002f };

        /// <summary>Skin, a layer at 1 mm and one at 2.5 mm: triangles reached at each level, and collapses refused, per share.</summary>
        [Test, Explicit("a measurement, not a check; run by name")]
        public void Reduction_BySharePerLevel()
        {
            foreach (var stack in new[] { new[] { 0.001f }, new[] { 0.001f, 0.0025f } })
            {
                using var scene = new Scene();
                scene.Add(SkinTube());
                foreach (var gap in stack) scene.Add(LayerTube(Over(gap)));
                scene.BuildSurface();
                var source = scene.Meshes.Sum(m => m.TriangleCount);
                TestContext.WriteLine($"skin and layers at {string.Join(", ", stack.Select(g => $"{g * 1000} mm"))}: {source} triangles");
                foreach (var share in new[] { -1f, 0.25f, 1f / 3f, 0.4f, 0.45f })
                {
                    var seqs = new List<Core.Decimation.CollapseSequence>();
                    long refused = 0;
                    try
                    {
                        for (var m = 0; m < scene.Meshes.Count; m++)
                        {
                            seqs.Add(scene.Decimate(m, share >= 0f, out var stats, s =>
                            {
                                s.Clearance.Share = math.max(share, 0f);
                                return s;
                            }));
                            refused += stats.Refused;
                        }
                        var counts = Levels.Select(l => seqs.Sum(q => q.TriangleCountAt(Fixtures.CutAtError(q, l))));
                        TestContext.WriteLine($"  {(share < 0 ? "rule off" : $"share {share:0.00}")}: triangles at {string.Join(" / ", Levels.Select(l => $"{l * 1000}"))} mm = {string.Join(" / ", counts)}; refused {refused}");
                    }
                    finally
                    {
                        foreach (var q in seqs) q.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// An avatar-sized scene: eight limbs, each skin with a layer at 1 mm, four with a second at 2.5 mm, 20 meshes and
        /// about 200,000 triangles, every garment vertex near another layer. Time to record every sequence with the rule
        /// and without, and the triangles reached at 1 mm.
        /// </summary>
        [Test, Explicit("a measurement, not a check; run by name")]
        public void AvatarSizedScene_TimeAndTriangles()
        {
            using (var warm = new Scene())
            {
                warm.Add(SkinTube());
                warm.Add(LayerTube(Over(0.001f)));
                warm.BuildSurface();
                warm.Decimate(1, true, out _).Dispose();
                warm.Decimate(1, false, out _).Dispose();
            }

            using var scene = new Scene();
            for (var limb = 0; limb < 8; limb++)
            {
                var offset = new float3(0.3f * (limb % 4), 0f, 0.3f * (limb / 4));
                var r = 0.04f + 0.005f * limb;
                float Skin(float a, float y) => r + 0.003f * math.sin(6f * a) * math.sin(2f * math.PI * y / 0.08f);
                MeshBuilder Shift(MeshBuilder b)
                {
                    for (var v = 0; v < b.VertexCount; v++) b.Positions[v] += offset;
                    return b;
                }
                scene.Add(Shift(Tube(96, 64, 0.4f, Skin)));
                scene.Add(Shift(Tube(80, 56, 0.4f, (a, y) => Skin(a, y) + 0.001f, 0.5f)));
                if (limb % 2 == 0) scene.Add(Shift(Tube(64, 56, 0.4f, (a, y) => Skin(a, y) + 0.0025f, 0.25f)));
            }
            var total = scene.Meshes.Sum(m => m.TriangleCount);

            var sw = Stopwatch.StartNew();
            scene.BuildSurface();
            var tSurface = sw.Elapsed.TotalSeconds;

            sw.Restart();
            for (var m = 0; m < scene.Meshes.Count; m++)
            {
                using var live = new NativeArray<bool>(Enumerable.Repeat(true, scene.Meshes[m].TriangleCount).ToArray(), Allocator.Persistent);
                using var f = ClearanceField.Measure(scene.Meshes[m].Positions, scene.Meshes[m].Triangles, live,
                    new ClearanceInput { Surface = scene.Surface, TriangleLayer = scene.Layers[m] }, 0.02f, Allocator.Persistent);
            }
            TestContext.WriteLine($"measuring every mesh's clearance: {sw.Elapsed.TotalSeconds:F3} s");
            foreach (var rule in new[] { false, true })
            {
                var seqs = new List<Core.Decimation.CollapseSequence>();
                long refused = 0;
                long near = 0;
                try
                {
                    sw.Restart();
                    for (var m = 0; m < scene.Meshes.Count; m++)
                    {
                        seqs.Add(scene.Decimate(m, rule, out var stats));
                        refused += stats.Refused;
                        near += stats.NearVertices;
                    }
                    var t = sw.Elapsed.TotalSeconds;
                    var counts = Levels.Select(l => seqs.Sum(q => q.TriangleCountAt(Fixtures.CutAtError(q, l))));
                    TestContext.WriteLine($"{scene.Meshes.Count} meshes, {total} triangles, {(rule ? "with" : "without")} the rule: {t:F3} s to record every sequence" +
                                          $"{(rule ? $" (+{tSurface:F3} s building the surface)" : "")}; triangles at {string.Join(" / ", Levels.Select(l => $"{l * 1000}"))} mm = {string.Join(" / ", counts)}; near vertices {near}, refused {refused}");
                }
                finally
                {
                    foreach (var q in seqs) q.Dispose();
                }
            }
        }
    }
}
