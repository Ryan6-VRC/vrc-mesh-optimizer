using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;
using Unity.Mathematics;
using static Ryan6Vrc.MeshOptimizer.Tests.Decimation.LayerFixtures;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    // Two layers a millimetre apart, each decimated on its own to about their gap, cross; the clearance rule keeps
    // them apart at every cut. Each case below is set against the one that decides the other way.
    public class ClearanceTests
    {
        const float Gap = 0.001f;
        static readonly float[] Levels = { 0.0005f, 0.001f, 0.002f, 0.004f };

        static Scene SkinAndLayer(float gap)
        {
            var scene = new Scene();
            scene.Add(SkinTube());
            scene.Add(LayerTube(Over(gap)));
            scene.BuildSurface();
            return scene;
        }

        /// <summary>Crossing between the two meshes cut at each pair of levels; the originals when a level is 0.</summary>
        static Dictionary<(float, float), float> CrossingAtLevels(Scene scene, bool rule, bool decimateSkin, bool decimateLayer, out int trianglesAtOneMm)
        {
            using var skin = scene.Decimate(0, rule, out _);
            using var layer = scene.Decimate(1, rule, out _);
            var result = new Dictionary<(float, float), float>();
            trianglesAtOneMm = skin.TriangleCountAt(Fixtures.CutAtError(skin, 0.001f)) + layer.TriangleCountAt(Fixtures.CutAtError(layer, 0.001f));
            foreach (var ls in decimateSkin ? Levels : new[] { 0f })
            foreach (var ll in decimateLayer ? Levels : new[] { 0f })
            {
                using var cs = skin.Cut(ls > 0 ? Fixtures.CutAtError(skin, ls) : 0, Allocator.Persistent);
                using var cl = layer.Cut(ll > 0 ? Fixtures.CutAtError(layer, ll) : 0, Allocator.Persistent);
                result[(ls, ll)] = Crossing(scene.Builders[0], cs, scene.Builders[1], cl);
            }
            return result;
        }

        static string Describe(Dictionary<(float, float), float> crossings) =>
            string.Join(", ", crossings.Select(kv => $"{kv.Key.Item1 * 1000:0.#}/{kv.Key.Item2 * 1000:0.#} mm: {kv.Value * 100:0.0} cm"));

        [Test]
        public void TwoLayersAMillimetreApart_CrossWhenDecimatedAlone_AndNotAtAnyCutWithTheRule()
        {
            using var scene = SkinAndLayer(Gap);
            Assert.That(Crossing(scene.Builders[0], scene.Builders[1]), Is.Zero, "the source layers do not cross");

            var off = CrossingAtLevels(scene, false, true, true, out var trisOff);
            var on = CrossingAtLevels(scene, true, true, true, out var trisOn);
            TestContext.WriteLine($"without the rule: {Describe(off)}");
            TestContext.WriteLine($"with the rule: {Describe(on)}");
            TestContext.WriteLine($"triangles at 1 mm: {trisOff} without, {trisOn} with");

            Assert.That(off[(0.002f, 0.002f)], Is.GreaterThan(0.01f), "without the rule the layers cross");
            // The price, bounded so it cannot creep: each triangle the rule makes carries the least clearance of what
            // it replaced, no more, and at the gap's own error level that costs about a fifth more triangles here.
            Assert.That(trisOn, Is.LessThanOrEqualTo(trisOff * 1.25f), "the rule's price at 1 mm");
            foreach (var kv in on)
                Assert.That(kv.Value, Is.Zero, $"with the rule, skin cut at {kv.Key.Item1 * 1000} mm and the layer at {kv.Key.Item2 * 1000} mm");
        }

        // Skin bridging a hollow bulges outward; a layer cutting across a crest sags inward. The rule is the same
        // for both, so each is kept clear when it alone is decimated against the other as it stands.
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void EitherLayerAlone_BulgingOutOrSaggingIn_IsKeptClear(bool decimateSkin, bool decimateLayer)
        {
            using var scene = SkinAndLayer(Gap);
            var off = CrossingAtLevels(scene, false, decimateSkin, decimateLayer, out _);
            var on = CrossingAtLevels(scene, true, decimateSkin, decimateLayer, out _);
            TestContext.WriteLine($"without the rule: {Describe(off)}");
            TestContext.WriteLine($"with the rule: {Describe(on)}");
            Assert.That(off.Values.Max(), Is.GreaterThan(0.01f), "without the rule the decimated layer passes through the other");
            foreach (var kv in on) Assert.That(kv.Value, Is.Zero, $"with the rule, at {kv.Key.Item1 * 1000}/{kv.Key.Item2 * 1000} mm");
        }

        /// <summary>
        /// A layer whose gap to the skin runs linearly from <paramref name="from"/> at the bottom to <paramref name="to"/>
        /// at the top: through zero, it crosses the skin along a ring, as a boot crosses the tights inside it; from
        /// zero, it touches the skin along its bottom edge, as a waistband sewn to the body does.
        /// </summary>
        static Scene SkinAndSlopedLayer(float from, float to)
        {
            var scene = new Scene();
            scene.Add(SkinTube());
            scene.Add(LayerTube((a, y) => Skin(a, y) + math.lerp(from, to, y / Length)));
            scene.BuildSurface();
            return scene;
        }

        static int KeptNear(MeshBuilder b, in CutResult cut, float y, float band)
        {
            var n = 0;
            foreach (var v in cut.KeptVertices)
                if (math.abs(b.Positions[v].y - y) < band) n++;
            return n;
        }

        // Where layers already cross or touch there is no clearance to keep. The rule does not count the crossing against
        // the surface there, so the region still decimates, and asks only that no collapse lengthen the crossing: without
        // the rule the crossing grows, with it the crossing does not.
        [TestCase(-0.0015f, 0.0015f, 0.5f, TestName = "{m}(crossing along a ring)")]
        [TestCase(0f, 0.002f, 0f, TestName = "{m}(touching along an edge)")]
        public void LayersThatAlreadyCrossOrTouch_AreNotLocked_AndTheirCrossingDoesNotGrow(float from, float to, float lineAt)
        {
            using var scene = SkinAndSlopedLayer(from, to);
            var before = Crossing(scene.Builders[0], scene.Builders[1]);
            using var skinOff = scene.Decimate(0, false, out _);
            using var layerOff = scene.Decimate(1, false, out _);
            using var skinOn = scene.Decimate(0, true, out var skinStats);
            using var layerOn = scene.Decimate(1, true, out var layerStats);
            var line = lineAt * Length;
            const float band = 0.006f;
            var sourceNear = Enumerable.Range(0, scene.Builders[1].VertexCount).Count(v => math.abs(scene.Builders[1].Positions[v].y - line) < band);
            TestContext.WriteLine($"source crossing {before * 100:0.0} cm; layer vertices within {band * 1000} mm of the line: {sourceNear}; refused {skinStats.Refused} on the skin, {layerStats.Refused} on the layer");
            foreach (var level in Levels)
            {
                using var cso = skinOff.Cut(Fixtures.CutAtError(skinOff, level), Allocator.Persistent);
                using var clo = layerOff.Cut(Fixtures.CutAtError(layerOff, level), Allocator.Persistent);
                using var cs = skinOn.Cut(Fixtures.CutAtError(skinOn, level), Allocator.Persistent);
                using var cl = layerOn.Cut(Fixtures.CutAtError(layerOn, level), Allocator.Persistent);
                var crossOff = Crossing(scene.Builders[0], cso, scene.Builders[1], clo);
                var crossOn = Crossing(scene.Builders[0], cs, scene.Builders[1], cl);
                var nearOff = KeptNear(scene.Builders[1], clo, line, band);
                var nearOn = KeptNear(scene.Builders[1], cl, line, band);
                TestContext.WriteLine($"{level * 1000} mm: crossing {crossOff * 100:0.0} cm without, {crossOn * 100:0.0} cm with; layer vertices kept near the line {nearOff} without, {nearOn} with");
                // Against a locked region, which would keep every source vertex along the line.
                Assert.That(nearOn, Is.LessThanOrEqualTo(level >= 0.002f ? sourceNear / 4 : sourceNear / 2), $"at {level * 1000} mm the region along the line decimates");
                Assert.That(crossOff, Is.GreaterThan(before), $"at {level * 1000} mm without the rule the crossing grows");
                Assert.That(crossOn, Is.LessThanOrEqualTo(before), $"at {level * 1000} mm with the rule it does not");
            }
        }

        // Neighbours are the surface that remains. Skin deleted under the layer is not there to be passed through, so
        // the layer decimates exactly as if it were alone; kept, the same skin holds it back.
        [TestCase(true)]
        [TestCase(false)]
        public void OnlyKeptSurfaceIsANeighbour(bool deleteUnderLayer)
        {
            var skin = Tube(64, 80, 0.4f, (a, y) => Skin(a, y - 0.1f));
            for (var v = 0; v < skin.VertexCount; v++) skin.Positions[v] -= new float3(0, 0.1f, 0);
            // Deleted: every skin triangle within 3 cm of the layer's height range, beyond the rule's 2 cm reach.
            var kept = skin.Triangles.Select(t => !deleteUnderLayer ||
                math.abs((skin.Positions[t.x].y + skin.Positions[t.y].y + skin.Positions[t.z].y) / 3f - Length / 2f) > Length / 2f + 0.03f).ToArray();
            using var scene = new Scene();
            scene.Add(skin, kept);
            scene.Add(LayerTube(Over(Gap)));
            scene.BuildSurface();

            using var off = scene.Decimate(1, false, out _);
            using var on = scene.Decimate(1, true, out var stats);
            var same = off.Length == on.Length && Enumerable.Range(0, off.Records.Length).All(i => off.Records[i].Equals(on.Records[i]));
            TestContext.WriteLine($"near vertices {stats.NearVertices}, refused {stats.Refused}, steps {off.Length} without and {on.Length} with");
            Assert.That(same, Is.EqualTo(deleteUnderLayer), "the layer's decimation is its decimation alone exactly when the skin under it is gone");
            Assert.That(stats.NearVertices == 0, Is.EqualTo(deleteUnderLayer));
        }

        // A skirt and its lining, or stacked frills, are often one mesh in separate pieces: each piece is a layer.
        [Test]
        public void TwoPiecesOfOneMesh_AreKeptApart()
        {
            var b = SkinTube();
            var skinVertices = b.VertexCount;
            Tube(56, 36, Length, Over(Gap), 0.5f, b);
            using var scene = new Scene();
            scene.Add(b);
            scene.BuildSurface();
            Assert.That(scene.Layers[0].Distinct().Count(), Is.EqualTo(2), "one mesh, two pieces");
            var p = b.Positions.ToArray();

            float CrossingInCut(CollapseSequence seq, float level)
            {
                using var cut = seq.Cut(Fixtures.CutAtError(seq, level), Allocator.Persistent);
                var tris = Fixtures.ToArray(cut.Triangles);
                return CrossingLength(p, tris.Where(t => t.x < skinVertices).ToArray(), p, tris.Where(t => t.x >= skinVertices).ToArray());
            }

            using var off = scene.Decimate(0, false, out _);
            using var on = scene.Decimate(0, true, out var stats);
            var crossOff = Levels.Select(l => CrossingInCut(off, l)).ToArray();
            var crossOn = Levels.Select(l => CrossingInCut(on, l)).ToArray();
            TestContext.WriteLine($"crossing without the rule {string.Join(", ", crossOff.Select(x => (x * 100).ToString("0.0")))} cm; with {string.Join(", ", crossOn.Select(x => (x * 100).ToString("0.0")))} cm; refused {stats.Refused}");
            Assert.That(crossOff.Max(), Is.GreaterThan(0.01f), "without the rule one piece passes through the other");
            Assert.That(crossOn, Is.All.Zero);
        }

        // The triangles around a vertex are one piece, never each other's neighbours, so a lone sheet decimates exactly
        // as it does without the rule.
        [Test]
        public void ALoneSheet_IsNotItsOwnNeighbour()
        {
            using var scene = new Scene();
            scene.Add(SkinTube());
            scene.BuildSurface();
            using var off = scene.Decimate(0, false, out _);
            using var on = scene.Decimate(0, true, out var stats);
            Assert.That(stats.NearVertices, Is.Zero);
            Assert.That(stats.Refused, Is.Zero);
            Assert.That(on.Length, Is.EqualTo(off.Length));
            Assert.That(Enumerable.Range(0, off.Records.Length).All(i => off.Records[i].Equals(on.Records[i])), Is.True);
        }

        // ------------------------------------------------------------------ the switch and the settings

        [Test]
        public void TheRuleOff_RecordsWhatTheDecimatorRecordsWithoutLayers()
        {
            using var scene = SkinAndLayer(Gap);
            using var off = scene.Decimate(1, false, out var stats);
            using var plain = Decimator.Run(scene.Meshes[1], DecimationSettings.Default, default, Allocator.Persistent);
            Assert.That(stats.Refused, Is.Zero);
            Assert.That(off.Length, Is.EqualTo(plain.Length));
            Assert.That(Enumerable.Range(0, off.Records.Length).All(i => off.Records[i].Equals(plain.Records[i])), Is.True);
        }

        [TestCase(0.5f, 0.02f)]
        [TestCase(-0.1f, 0.02f)]
        [TestCase(0.4f, 0f)]
        public void ASettingThatBreaksTheGuarantee_IsRefused(float share, float reach)
        {
            using var scene = SkinAndLayer(Gap);
            var e = Assert.Throws<System.ArgumentException>(() => scene.Decimate(1, true, out _, s =>
            {
                s.Clearance.Share = share;
                s.Clearance.Reach = reach;
                return s;
            }).Dispose());
            TestContext.WriteLine(e.Message);
        }

        // ------------------------------------------------------------------ distance

        [Test]
        public void TriangleDistance_AgreesWithDenseSampling()
        {
            var rng = new Random(7);
            for (var i = 0; i < 300; i++)
            {
                var shift = rng.NextFloat3(-1.5f, 1.5f);
                float3 a0 = rng.NextFloat3(-1f, 1f), a1 = rng.NextFloat3(-1f, 1f), a2 = rng.NextFloat3(-1f, 1f);
                float3 b0 = rng.NextFloat3(-1f, 1f) + shift, b1 = rng.NextFloat3(-1f, 1f) + shift, b2 = rng.NextFloat3(-1f, 1f) + shift;
                var exact = math.sqrt(TriangleDistance.TriangleSq(a0, a1, a2, b0, b1, b2));
                var crossing = IntersectionLength(a0, a1, a2, b0, b1, b2) > 0;
                // Points of a against b: never nearer than the true distance, and close to it when dense.
                var sampled = float.PositiveInfinity;
                const int n = 60;
                for (var u = 0; u <= n; u++)
                for (var v = 0; v <= n - u; v++)
                {
                    var p = a0 + (a1 - a0) * (u / (float)n) + (a2 - a0) * (v / (float)n);
                    sampled = math.min(sampled, math.sqrt(TriangleDistance.PointSq(p, b0, b1, b2)));
                }
                if (crossing) Assert.That(exact, Is.Zero, $"pair {i} crosses");
                Assert.That(exact, Is.LessThanOrEqualTo(sampled + 1e-5f), $"pair {i}: exact {exact} above a sampled {sampled}");
                Assert.That(exact, Is.GreaterThan(sampled - 0.06f), $"pair {i}: exact {exact} far below sampled {sampled}");
            }
        }
    }
}
