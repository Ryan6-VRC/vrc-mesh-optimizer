using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Geometry;
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

        /// <summary>
        /// The cost without the normal term, for the two cases below whose bounds hold on the order the position cost alone
        /// collapses in. The normal term keeps more of a rippled tube whether the rule is on or not, so a count of vertices
        /// kept near a line reads higher; and where layers meet at under about 6 degrees the rule lets a crossing move
        /// farther than the gate's 5 mm (see CrossingDrift), so there another order can read as new crossing.
        /// </summary>
        static DecimationSettings PositionCostOnly(DecimationSettings s)
        {
            s.Normal.Weight = 0f;
            return s;
        }

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
            // The price, bounded so it cannot creep: at the gap's own error level the rule costs about a third more
            // triangles here, and a rule that asked more of a triangle than the gap where it approaches would cost more.
            Assert.That(trisOn, Is.LessThanOrEqualTo(trisOff * 1.4f), "the rule's price at 1 mm");
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

        // A real layer's gap is a millimetre on average and not everywhere. Where it varies, a decimated triangle spans
        // more gap than it approaches the other layer through, so the gap it must keep is read where it approaches, on
        // the other layer's own source triangles, not as the least gap anywhere under it.
        [Test]
        public void ALayerWhoseGapVaries_IsKeptClearAtEveryCut()
        {
            using var scene = new Scene();
            scene.Add(SkinTube());
            scene.Add(LayerTube((a, y) => Skin(a, y) + Gap * (1f + 0.6f * math.sin(5f * a) * math.sin(2f * math.PI * y / 0.06f))));
            scene.BuildSurface();
            Assert.That(Crossing(scene.Builders[0], scene.Builders[1]), Is.Zero, "the source layers do not cross");
            var on = CrossingAtLevels(scene, true, true, true, out _);
            TestContext.WriteLine($"with the rule: {Describe(on)}");
            foreach (var kv in on)
                Assert.That(kv.Value, Is.Zero, $"with the rule, skin cut at {kv.Key.Item1 * 1000} mm and the layer at {kv.Key.Item2 * 1000} mm");
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
            using var skinOff = scene.Decimate(0, false, out _, PositionCostOnly);
            using var layerOff = scene.Decimate(1, false, out _, PositionCostOnly);
            using var skinOn = scene.Decimate(0, true, out var skinStats, PositionCostOnly);
            using var layerOn = scene.Decimate(1, true, out var layerStats, PositionCostOnly);
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

        /// <summary>New crossing, as the gate reads it, between the two meshes cut at each pair of levels.</summary>
        static Dictionary<(float, float), float> NewCrossingAtLevels(Scene scene, bool rule)
        {
            using var a = scene.Decimate(0, rule, out _);
            using var b = scene.Decimate(1, rule, out _);
            var result = new Dictionary<(float, float), float>();
            foreach (var la in Levels)
            foreach (var lb in Levels)
            {
                using var ca = a.Cut(Fixtures.CutAtError(a, la), Allocator.Persistent);
                using var cb = b.Cut(Fixtures.CutAtError(b, lb), Allocator.Persistent);
                result[(la, lb)] = NewCrossing(scene.Builders[0], Fixtures.ToArray(ca.Triangles), scene.Builders[1], Fixtures.ToArray(cb.Triangles));
            }
            return result;
        }

        // Where two layers touch or cross, the surface around the line still has a gap a short way off, and both layers
        // must keep their share of it: judging the surface near the line only against the other layer's source lets
        // both reach the other's source there, and meet each other.
        [TestCase(-0.0015f, 0.0015f, TestName = "{m}(crossing along a ring)")]
        [TestCase(0f, 0.002f, TestName = "{m}(touching along an edge)")]
        public void LayersThatAlreadyCrossOrTouch_GainNoNewCrossingNearTheLine(float from, float to)
        {
            using var scene = SkinAndSlopedLayer(from, to);
            var on = NewCrossingAtLevels(scene, true);
            TestContext.WriteLine($"new crossing with the rule: {Describe(on)}");
            foreach (var kv in on)
                Assert.That(kv.Value, Is.Zero, $"new crossing, skin cut at {kv.Key.Item1 * 1000} mm and the layer at {kv.Key.Item2 * 1000} mm");
        }

        // A coarse layer, a boot of few large triangles, crossing a fine one along a ring. Where the two already cross,
        // a collapse may not lengthen the crossing; it may not move it either, by shortening it against one of the
        // boot's triangles and lengthening it against another.
        // At meetings shallower than about a degree a crossing moves along by millimetres for every hundredth of a
        // millimetre either surface moves, which the gate reads as new by its own stated design; this holds 2 degrees on
        // the position cost's collapse order, below the 6 degrees the rule itself answers for.
        [TestCase(0.004f, TestName = "{m}(meeting at about 2 degrees)")]
        public void AFineLayerCrossingACoarseOne_GainsNoNewCrossing(float reach)
        {
            using var scene = new Scene();
            scene.Add(SkinTube());
            scene.Add(Tube(10, 4, Length, (a, y) => Skin(a, y) + math.lerp(-reach, reach, y / Length), 0.3f));
            scene.BuildSurface();
            using var skin = scene.Decimate(0, true, out _, PositionCostOnly);
            var result = new Dictionary<float, float>();
            foreach (var level in Levels)
            {
                using var cut = skin.Cut(Fixtures.CutAtError(skin, level), Allocator.Persistent);
                result[level] = NewCrossing(scene.Builders[0], Fixtures.ToArray(cut.Triangles), scene.Builders[1], scene.Builders[1].Triangles.ToArray());
            }
            TestContext.WriteLine($"new crossing with the rule: {string.Join(", ", result.Select(kv => $"{kv.Key * 1000} mm: {kv.Value * 100:0.0} cm"))}");
            foreach (var kv in result) Assert.That(kv.Value, Is.Zero, $"new crossing, skin cut at {kv.Key * 1000} mm");
        }

        // Occlusion deletes the skin under a garment, leaving a hole with an open border that the garment passes over.
        // A border collapse straightens the border across the hole, off the skin's source, where only the garment's own
        // source was judged; the garment may sag there. The two must still not meet.
        [TestCase(0.001f)]
        [TestCase(0.0005f)]
        public void ALayerOverAHoleInTheSkin_GainsNoNewCrossingAtTheBorder(float gap)
        {
            var skin = SkinTube();
            // An elliptical hole in angle and height: its grid border is a staircase that decimation straightens.
            var keep = skin.Triangles.Select(t =>
            {
                var c = (skin.Positions[t.x] + skin.Positions[t.y] + skin.Positions[t.z]) / 3f;
                var a = math.atan2(c.z, c.x);
                return math.lengthsq(new float2(a / 1.2f, (c.y - Length / 2f) / 0.06f)) > 1f;
            }).ToArray();
            var holed = new MeshBuilder();
            for (var v = 0; v < skin.VertexCount; v++) holed.Add(skin.Positions[v], skin.Uvs[v]);
            for (var t = 0; t < skin.Triangles.Count; t++)
                if (keep[t]) holed.Tri(skin.Triangles[t].x, skin.Triangles[t].y, skin.Triangles[t].z);
            using var scene = new Scene();
            scene.Add(holed);
            scene.Add(LayerTube(Over(gap)));
            scene.BuildSurface();
            var on = NewCrossingAtLevels(scene, true);
            TestContext.WriteLine($"new crossing with the rule: {Describe(on)}");
            foreach (var kv in on)
                Assert.That(kv.Value, Is.Zero, $"new crossing, skin cut at {kv.Key.Item1 * 1000} mm and the layer at {kv.Key.Item2 * 1000} mm");
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
        [TestCase(0.4f, 0.01f)]
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

        // The rule counts crossing length to decide whether a collapse lengthens a crossing; the gate judges the rule's
        // output with its own measure. They must agree on what crosses: a triangle meeting another along a shared edge,
        // resting an edge or a corner on it, or lying in its plane touches and does not cross.
        [Test]
        public void CrossingLength_AgreesWithTheGate_OnTouchingAndCrossing()
        {
            var o = new float3(0.3f, 1.2f, -0.4f); // avatar-sized coordinates
            var cases = new (string name, float3[] a, float3[] b)[]
            {
                ("crossing", new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0, 0.01f, 0) },
                    new[] { o + new float3(0.002f, 0.002f, -0.005f), o + new float3(0.002f, 0.002f, 0.005f), o + new float3(0.006f, 0.003f, 0) }),
                ("shared edge", new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0, 0.01f, 0) },
                    new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0.004f, 0.003f, 0.006f) }),
                ("edge resting on the face", new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0, 0.01f, 0) },
                    new[] { o + new float3(0.001f, 0.002f, 0), o + new float3(0.006f, 0.002f, 0), o + new float3(0.003f, 0.004f, 0.005f) }),
                ("corner on the face", new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0, 0.01f, 0) },
                    new[] { o + new float3(0.002f, 0.002f, 0), o + new float3(0.004f, 0.002f, 0.005f), o + new float3(0.002f, 0.005f, 0.005f) }),
                ("coplanar overlap", new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0, 0.01f, 0) },
                    new[] { o + new float3(0.002f, 0.002f, 0), o + new float3(0.012f, 0.002f, 0), o + new float3(0.002f, 0.012f, 0) }),
                ("grazing, a micrometre through", new[] { o, o + new float3(0.01f, 0, 0), o + new float3(0, 0.01f, 0) },
                    new[] { o + new float3(0.001f, 0.002f, -1e-6f), o + new float3(0.006f, 0.002f, -1e-6f), o + new float3(0.003f, 0.004f, 0.005f) }),
            };
            foreach (var (name, a, b) in cases)
            {
                var mine = TriangleDistance.IntersectionLength(a[0], a[1], a[2], b[0], b[1], b[2]);
                var gate = Core.Gate.TriangleCrossing.Cross(a[0], a[1], a[2], b[0], b[1], b[2], Core.Gate.CrossingSettings.Default.Touch, out var s) ? s.Length : 0f;
                TestContext.WriteLine($"{name}: {mine * 1000:0.####} mm by the rule, {gate * 1000:0.####} mm by the gate");
                Assert.That(mine, Is.EqualTo(gate).Within(1e-6f), name);
            }
        }

        // The pruning that settles most pairs without the exact distance must never settle one wrongly: slivers and
        // near-degenerate query triangles at avatar-sized coordinates, against a brute-force search.
        [Test]
        public void TriangleClearance_AgreesWithBruteForce_ForSlivers()
        {
            var rng = new Random(11);
            var o = new float3(0.2f, 1.1f, 0.3f);
            var b = new MeshBuilder();
            for (var i = 0; i < 400; i++)
            {
                var c = o + rng.NextFloat3(-0.02f, 0.02f);
                var v0 = b.Add(c);
                var v1 = b.Add(c + rng.NextFloat3(-0.004f, 0.004f));
                var v2 = b.Add(c + rng.NextFloat3(-0.004f, 0.004f));
                b.Tri(v0, v1, v2);
            }
            using var scene = new Scene();
            scene.Add(b);
            scene.BuildSurface();
            var p = b.Positions.ToArray();
            var t = b.Triangles.ToArray();
            var layers = scene.Layers[0];
            for (var i = 0; i < 300; i++)
            {
                var a0 = o + rng.NextFloat3(-0.02f, 0.02f);
                var a1 = a0 + rng.NextFloat3(-0.01f, 0.01f);
                // Three queries in four are slivers: the third corner on the first edge, or a hair off it.
                var off = (i % 4) switch { 0 => 0f, 1 => 1e-9f, 2 => 1e-7f, _ => -1f };
                var a2 = off >= 0f ? math.lerp(a0, a1, rng.NextFloat()) + rng.NextFloat3(-off, off) : a0 + rng.NextFloat3(-0.01f, 0.01f);
                var brute = float.PositiveInfinity;
                for (var k = 0; k < t.Length; k++)
                    brute = math.min(brute, TriangleDistance.TriangleSq(a0, a1, a2, p[t[k].x], p[t[k].y], p[t[k].z]));
                brute = math.min(math.sqrt(brute), 0.02f);
                var s = scene.Surface;
                var tree = s.TriangleClearance(a0, a1, a2, int.MaxValue, 0.02f);
                Assert.That(tree, Is.EqualTo(brute).Within(1e-6f), $"query {i}");
            }
            Assert.That(layers.Length, Is.EqualTo(t.Length));
        }
    }
}
