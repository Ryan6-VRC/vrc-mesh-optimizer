using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
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
        /// The cost without the normal term, for the case below whose bound holds on the order the position cost alone
        /// collapses in: where layers meet at under about 6 degrees the rule lets a crossing move farther than the gate's
        /// 5 mm (see CrossingDrift), so there another order can read as new crossing.
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
            foreach (var v in Fixtures.Kept(cut))
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
                // Against a locked region, which would keep every source vertex along the line: the rule keeps no more than
                // half of what the same cost without it removes there, so what is kept is the rule's and not the cost's.
                Assert.That(nearOn, Is.LessThanOrEqualTo(nearOff + (sourceNear - nearOff) / 2), $"at {level * 1000} mm the region along the line decimates");
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
        // Both scenes meet at under 1 degree, below the 6 degrees CrossingDrift's guarantee is stated for, and there a
        // collapse that shortens a crossing may still move it along its own source, within the drift, past the gate's 5 mm.
        // The ring does: one skin collapse that shortens its fan's crossing reads 0.63 to 0.65 mm of new crossing against
        // the layer cut at 0.5 mm, at skin cuts from 4 to 8 mm under Burst and as managed code, one collapse's worth that
        // does not grow with the cut. It read 0 at these cuts only on the one collapse order Burst's rounding chose, and
        // that order reaches the same at an 8 mm cut, so the ring is held to under a millimetre. The edge reads 0 on both.
        [TestCase(-0.0015f, 0.0015f, 0.001f, TestName = "{m}(crossing along a ring)")]
        [TestCase(0f, 0.002f, 0f, TestName = "{m}(touching along an edge)")]
        public void LayersThatAlreadyCrossOrTouch_GainNoNewCrossingNearTheLine(float from, float to, float bound)
        {
            using var scene = SkinAndSlopedLayer(from, to);
            var on = NewCrossingAtLevels(scene, true);
            TestContext.WriteLine($"new crossing with the rule: {Describe(on)}");
            foreach (var kv in on)
                Assert.That(kv.Value, bound > 0f ? (NUnit.Framework.Constraints.IResolveConstraint)Is.LessThan(bound) : Is.Zero, $"new crossing, skin cut at {kv.Key.Item1 * 1000} mm and the layer at {kv.Key.Item2 * 1000} mm");
        }

        // A coarse layer, a boot of few large triangles, crossing a fine one along a ring. Where the two already cross,
        // a collapse may not lengthen the crossing; it may not move it either, by shortening it against one of the
        // boot's triangles and lengthening it against another. Held within CrossingDrift of its own source, a crossing
        // stays within the gate's 5 mm wherever the layers meet at over 6 degrees: the rule's guarantee, checked here at
        // about 7 degrees on the cost as it ships.
        // Below the guarantee a crossing moves along by millimetres for every hundredth of a millimetre either surface
        // moves, which the gate reads as new by its own stated design. The 2-degree case is a stated limit under it: it
        // holds on the position cost's collapse order, and the normal term's order moves the crossing past the gate's 5 mm.
        [TestCase(0.012f, false, TestName = "{m}(meeting at about 7 degrees)")]
        [TestCase(0.004f, true, TestName = "{m}(meeting at about 2 degrees)")]
        public void AFineLayerCrossingACoarseOne_GainsNoNewCrossing(float reach, bool positionCostOnly)
        {
            using var scene = new Scene();
            scene.Add(SkinTube());
            scene.Add(Tube(10, 4, Length, (a, y) => Skin(a, y) + math.lerp(-reach, reach, y / Length), 0.3f));
            scene.BuildSurface();
            var cost = positionCostOnly ? PositionCostOnly : (System.Func<DecimationSettings, DecimationSettings>)null;
            Dictionary<float, float> NewCrossingOfTheSkin(bool rule)
            {
                using var skin = scene.Decimate(0, rule, out _, cost);
                var result = new Dictionary<float, float>();
                foreach (var level in Levels)
                {
                    using var cut = skin.Cut(Fixtures.CutAtError(skin, level), Allocator.Persistent);
                    result[level] = NewCrossing(scene.Builders[0], Fixtures.ToArray(cut.Triangles), scene.Builders[1], scene.Builders[1].Triangles.ToArray());
                }
                return result;
            }
            string Describe(Dictionary<float, float> r) => string.Join(", ", r.Select(kv => $"{kv.Key * 1000} mm: {kv.Value * 1000:0.0} mm"));
            var off = NewCrossingOfTheSkin(false);
            var on = NewCrossingOfTheSkin(true);
            TestContext.WriteLine($"new crossing without the rule: {Describe(off)}");
            TestContext.WriteLine($"new crossing with the rule: {Describe(on)}");
            Assert.That(off.Values.Max(), Is.GreaterThan(0.005f), "without the rule the cut skin crosses the boot anew");
            foreach (var kv in on) Assert.That(kv.Value, Is.Zero, $"new crossing, skin cut at {kv.Key * 1000} mm");
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
            var skin = SkinLongerThanTheLayer(out var deleted);
            var kept = deleted.Select(d => !deleteUnderLayer || !d).ToArray();
            using var scene = new Scene();
            scene.Add(skin, kept);
            scene.Add(LayerTube(Over(Gap)));
            scene.BuildSurface();

            using var off = scene.Decimate(1, false, out _);
            using var on = scene.Decimate(1, true, out var stats);
            var same = SameRecords(off, on);
            TestContext.WriteLine($"near vertices {stats.NearVertices}, refused {stats.Refused}, steps {off.Length} without and {on.Length} with");
            Assert.That(same, Is.EqualTo(deleteUnderLayer), "the layer's decimation is its decimation alone exactly when the skin under it is gone");
            Assert.That(stats.NearVertices == 0, Is.EqualTo(deleteUnderLayer));
        }

        // The same skin deleted under the layer, handed in as the pipeline hands it with removed triangles as neighbours:
        // in the surface, in the piece it belongs to in the whole skin, and left out of the skin's own decimation. It holds
        // the layer back as kept skin does, and the skin's own decimation passes it by: the kept skin bordering it, one
        // piece with it, is no neighbour of it.
        [Test]
        public void DeletedSurfaceHandedIn_IsANeighbourOfOtherLayers_AndNotOfItsOwn()
        {
            var skin = SkinLongerThanTheLayer(out var deleted);
            using var scene = new Scene();
            scene.Add(skin);
            scene.Add(LayerTube(Over(Gap)));
            scene.BuildSurface();

            using var layerOff = scene.Decimate(1, false, out _);
            using var layerOn = scene.Decimate(1, true, out var layerStats);
            using var skinOff = scene.Decimate(0, false, out _, excluded: deleted);
            using var skinOn = scene.Decimate(0, true, out var skinStats, excluded: deleted);
            TestContext.WriteLine($"layer: near vertices {layerStats.NearVertices}, refused {layerStats.Refused}; skin: near vertices {skinStats.NearVertices}, steps {skinOff.Length} without the rule and {skinOn.Length} with");
            Assert.That(deleted.Count(d => d), Is.GreaterThan(0).And.LessThan(deleted.Length), "some of the skin is deleted, not all");
            Assert.That(layerStats.NearVertices, Is.GreaterThan(0), "the deleted skin holds the layer back");
            Assert.That(SameRecords(layerOff, layerOn), Is.False);
            Assert.That(skinStats.NearVertices, Is.Zero, "the kept skin sees nothing of its own deleted piece");
            Assert.That(SameRecords(skinOff, skinOn), Is.True, "the skin decimates as if alone");
        }

        /// <summary>A skin tube reaching 10 cm past each end of the layer's; <paramref name="deleted"/> flags every triangle within 3 cm of the layer's height range, beyond the rule's 2 cm reach.</summary>
        static MeshBuilder SkinLongerThanTheLayer(out bool[] deleted)
        {
            var skin = Tube(64, 80, 0.4f, (a, y) => Skin(a, y - 0.1f));
            for (var v = 0; v < skin.VertexCount; v++) skin.Positions[v] -= new float3(0, 0.1f, 0);
            deleted = skin.Triangles.Select(t =>
                math.abs((skin.Positions[t.x].y + skin.Positions[t.y].y + skin.Positions[t.z].y) / 3f - Length / 2f) <= Length / 2f + 0.03f).ToArray();
            return skin;
        }

        static bool SameRecords(CollapseSequence a, CollapseSequence b) =>
            a.Length == b.Length && Enumerable.Range(0, a.Records.Length).All(i => a.Records[i].Equals(b.Records[i]));

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

        [TestCase(0.5f, 0.02f, 1e-7f)]
        [TestCase(-0.1f, 0.02f, 1e-7f)]
        [TestCase(0.4f, 0f, 1e-7f)]
        [TestCase(0.4f, 0.01f, 1e-7f)]
        [TestCase(0.4f, 0.02f, -1e-9f)]
        [TestCase(0.4f, 0.02f, float.PositiveInfinity)]
        public void ASettingThatBreaksTheGuarantee_IsRefused(float share, float reach, float allowance)
        {
            using var scene = SkinAndLayer(Gap);
            var e = Assert.Throws<System.ArgumentException>(() => scene.Decimate(1, true, out _, s =>
            {
                s.Clearance.Share = share;
                s.Clearance.Reach = reach;
                s.Clearance.CrossingAllowance = allowance;
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

        // The clearance rule and the gate find crossings with one routine. A triangle meeting another along a shared edge,
        // resting an edge or a corner on it, lying in its plane, or reaching a micrometre through it touches and does not
        // cross; a pair passing through each other crosses along the length the double-precision oracle gives.
        static readonly float3 At = new float3(0.3f, 1.2f, -0.4f); // avatar-sized coordinates
        static readonly float3[] Face = { At, At + new float3(0.01f, 0, 0), At + new float3(0, 0.01f, 0) };

        static readonly Dictionary<string, float3[]> Against = new Dictionary<string, float3[]>
        {
            ["crossing"] = new[] { At + new float3(0.002f, 0.002f, -0.005f), At + new float3(0.002f, 0.002f, 0.005f), At + new float3(0.006f, 0.003f, 0) },
            ["shared edge"] = new[] { At, At + new float3(0.01f, 0, 0), At + new float3(0.004f, 0.003f, 0.006f) },
            ["edge resting on the face"] = new[] { At + new float3(0.001f, 0.002f, 0), At + new float3(0.006f, 0.002f, 0), At + new float3(0.003f, 0.004f, 0.005f) },
            ["corner on the face"] = new[] { At + new float3(0.002f, 0.002f, 0), At + new float3(0.004f, 0.002f, 0.005f), At + new float3(0.002f, 0.005f, 0.005f) },
            ["coplanar overlap"] = new[] { At + new float3(0.002f, 0.002f, 0), At + new float3(0.012f, 0.002f, 0), At + new float3(0.002f, 0.012f, 0) },
            ["grazing, a micrometre through"] = new[] { At + new float3(0.001f, 0.002f, -1e-6f), At + new float3(0.006f, 0.002f, -1e-6f), At + new float3(0.003f, 0.004f, 0.005f) },
        };

        [TestCase("shared edge")]
        [TestCase("edge resting on the face")]
        [TestCase("corner on the face")]
        [TestCase("coplanar overlap")]
        [TestCase("grazing, a micrometre through")]
        public void Intersect_TouchingIsNotCrossing(string name)
        {
            var b = Against[name];
            Assert.That(TriangleDistance.Intersect(Face[0], Face[1], Face[2], b[0], b[1], b[2], TriangleDistance.Touch, out _), Is.False);
            Assert.That(TriangleDistance.Intersect(b[0], b[1], b[2], Face[0], Face[1], Face[2], TriangleDistance.Touch, out _), Is.False, "either way round");
        }

        [Test]
        public void Intersect_CrossingPair_HasTheOraclesLength()
        {
            var b = Against["crossing"];
            var oracle = IntersectionLength(Face[0], Face[1], Face[2], b[0], b[1], b[2]);

            Assert.That(TriangleDistance.Intersect(Face[0], Face[1], Face[2], b[0], b[1], b[2], TriangleDistance.Touch, out var x), Is.True);
            TestContext.WriteLine($"{x.Length * 1000:0.######} mm, the oracle {oracle * 1000:0.######} mm");
            Assert.That(oracle, Is.GreaterThan(0.001), "a crossing of millimetres");
            Assert.That(x.Length, Is.EqualTo(oracle).Within(1e-6));
            Assert.That(math.distance(x.Start, x.End), Is.EqualTo(oracle).Within(1e-6));
        }

        /// <summary>
        /// Pairs of centimetre triangles at avatar-sized coordinates: the first <paramref name="count"/> at random around one
        /// point, the next <paramref name="count"/> moved to sit within a few <see cref="TriangleDistance.Touch"/> of the
        /// crossing line on one of its three conditions (b reaching past a's plane, a past b's, the two cuts overlapping),
        /// half of them within float resolution of it.
        /// </summary>
        static IEnumerable<(float3[] a, float3[] b)> CrossingPairs(int count)
        {
            var rng = new Random(23);
            for (var i = 0; i < 2 * count;)
            {
                var o = new float3(rng.NextFloat(-0.8f, 0.8f), rng.NextFloat(0f, 1.8f), rng.NextFloat(-0.8f, 0.8f));
                var a = new[] { o + rng.NextFloat3(-0.01f, 0.01f), o + rng.NextFloat3(-0.01f, 0.01f), o + rng.NextFloat3(-0.01f, 0.01f) };
                var b = new[] { o + rng.NextFloat3(-0.01f, 0.01f), o + rng.NextFloat3(-0.01f, 0.01f), o + rng.NextFloat3(-0.01f, 0.01f) };
                if (i < count)
                {
                    i++;
                    yield return (a, b);
                    continue;
                }
                var h = TriangleDistance.Touch * (1.0 + (rng.NextBool() ? rng.NextDouble(-1.0, 3.0) : rng.NextDouble(-0.01, 0.01)));
                var A = new[] { (double3)a[0], (double3)a[1], (double3)a[2] };
                var B = new[] { (double3)b[0], (double3)b[1], (double3)b[2] };
                var nA = math.normalize(math.cross(A[1] - A[0], A[2] - A[0]));
                var nB = math.normalize(math.cross(B[1] - B[0], B[2] - B[0]));
                double3 shift;
                var moveB = true;
                switch (rng.NextInt(3))
                {
                    case 0: // b reaches past a's plane by h on its lesser side
                        shift = nA * Onto(B, nA, A[0], h, rng.NextBool());
                        break;
                    case 1: // a reaches past b's plane by h
                        shift = nB * Onto(A, nB, B[0], h, rng.NextBool());
                        moveB = false;
                        break;
                    default: // the two cuts overlap by h along the common line
                        var dir = math.normalize(math.cross(nA, nB));
                        if (!Interval(A, nB, B[0], dir, out var aLo, out var aHi) || !Interval(B, nA, A[0], dir, out var bLo, out var bHi)) continue;
                        shift = dir * (rng.NextBool() ? aHi - h - bLo : aLo + h - bHi);
                        break;
                }
                var moved = moveB ? b : a;
                for (var k = 0; k < 3; k++) moved[k] = (float3)((double3)moved[k] + shift);
                i++;
                yield return (a, b);
            }
        }

        /// <summary>The move along unit n that leaves triangle t standing past the plane (n, o) by h on its lesser side, below it or above.</summary>
        static double Onto(double3[] t, double3 n, double3 o, double h, bool below)
        {
            double d0 = math.dot(n, t[0] - o), d1 = math.dot(n, t[1] - o), d2 = math.dot(n, t[2] - o);
            return below ? -h - math.min(d0, math.min(d1, d2)) : h - math.max(d0, math.max(d1, d2));
        }

        // The one crossing routine against the double-precision oracle, over random pairs and pairs built to sit within a
        // few Touch of the crossing line: the same verdict except where the pair lies within rounding of Touch on some
        // condition, and the same length and reaches within rounding.
        [Test]
        public void Intersect_AgreesWithTheDoubleOracle_OnRandomAndNearTouchingPairs()
        {
            const double rounding = 1e-7; // a hundredth of Touch, above the rounding of corner-relative arithmetic on centimetre triangles
            const double tolerance = 1e-6;
            var touch = TriangleDistance.Touch;
            int pairs = 0, crossings = 0, flips = 0;
            double worstFlip = 0, dLength = 0, dReach = 0;
            foreach (var (a, b) in CrossingPairs(4000))
            {
                pairs++;
                var one = TriangleDistance.Intersect(a[0], a[1], a[2], b[0], b[1], b[2], touch, out var x);
                var reachA = Reach(a[0], a[1], a[2], b[0], b[1], b[2]);
                var reachB = Reach(b[0], b[1], b[2], a[0], a[1], a[2]);
                var oracle = IntersectionLength(a[0], a[1], a[2], b[0], b[1], b[2]);
                if (one != (reachA > touch && reachB > touch && oracle > touch))
                {
                    flips++;
                    worstFlip = math.max(worstFlip, math.min(math.abs(reachA - touch), math.min(math.abs(reachB - touch), math.abs(oracle - touch))));
                }
                if (!one) continue;
                crossings++;
                dLength = math.max(dLength, math.abs(x.Length - oracle));
                dReach = math.max(dReach, math.max(math.abs(x.ReachA - reachA), math.abs(x.ReachB - reachB)));
            }
            TestContext.WriteLine($"{pairs} pairs, {crossings} crossing; {flips} verdicts off the oracle's, the farthest {worstFlip:E3} m from Touch; largest deviation {dLength:E3} m in length, {dReach:E3} m in reach");
            Assert.That(crossings, Is.GreaterThan(pairs / 10));
            Assert.That(worstFlip, Is.LessThanOrEqualTo(rounding), "a verdict off the oracle's farther from Touch than rounding");
            Assert.That(dLength, Is.LessThanOrEqualTo(tolerance));
            Assert.That(dReach, Is.LessThanOrEqualTo(tolerance));
        }

        [BurstCompile(CompileSynchronously = true)]
        struct IntersectJob : IJob
        {
            /// <summary>Six corners per pair, a's then b's.</summary>
            [ReadOnly] public NativeArray<float3> Corners;
            public NativeArray<bool> Crosses;
            public NativeArray<Intersection> Found;

            public void Execute()
            {
                for (var i = 0; i < Crosses.Length; i++)
                {
                    var k = 6 * i;
                    Crosses[i] = TriangleDistance.Intersect(Corners[k], Corners[k + 1], Corners[k + 2], Corners[k + 3], Corners[k + 4], Corners[k + 5], TriangleDistance.Touch, out var x);
                    Found[i] = x;
                }
            }
        }

        // The clearance rule and the gate both run the one routine inside Burst jobs, and the tests above call it as
        // managed code. Over the same pairs, Burst must give the managed verdict except within rounding of Touch, and the
        // same crossing within rounding.
        [Test]
        public void Intersect_GivesUnderBurstWhatItGivesAsManagedCode()
        {
            Assume.That(Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled, "Burst is off in this Editor, so there is no compiled code to compare");
            const double rounding = 1e-7;
            const double tolerance = 1e-6;
            var touch = TriangleDistance.Touch;
            var pairs = CrossingPairs(4000).ToArray();
            using var corners = new NativeArray<float3>(pairs.SelectMany(p => p.a.Concat(p.b)).ToArray(), Allocator.Persistent);
            using var crosses = new NativeArray<bool>(pairs.Length, Allocator.Persistent);
            using var found = new NativeArray<Intersection>(pairs.Length, Allocator.Persistent);
            new IntersectJob { Corners = corners, Crosses = crosses, Found = found }.Run();

            int crossings = 0, flips = 0, identical = 0;
            double worstFlip = 0, dLength = 0, dEnds = 0, dReach = 0;
            for (var i = 0; i < pairs.Length; i++)
            {
                var (a, b) = pairs[i];
                var managed = TriangleDistance.Intersect(a[0], a[1], a[2], b[0], b[1], b[2], touch, out var m);
                var x = found[i];
                if (managed != crosses[i])
                {
                    flips++;
                    var reachA = Reach(a[0], a[1], a[2], b[0], b[1], b[2]);
                    var reachB = Reach(b[0], b[1], b[2], a[0], a[1], a[2]);
                    var oracle = IntersectionLength(a[0], a[1], a[2], b[0], b[1], b[2]);
                    worstFlip = math.max(worstFlip, math.min(math.abs(reachA - touch), math.min(math.abs(reachB - touch), math.abs(oracle - touch))));
                    continue;
                }
                if (!managed) continue;
                crossings++;
                if (math.all(math.asuint(x.Start) == math.asuint(m.Start)) && math.all(math.asuint(x.End) == math.asuint(m.End)) &&
                    math.asuint(x.Length) == math.asuint(m.Length) && math.asuint(x.ReachA) == math.asuint(m.ReachA) && math.asuint(x.ReachB) == math.asuint(m.ReachB))
                    identical++;
                Assert.That(x.SameFacing, Is.EqualTo(m.SameFacing), $"pair {i}: facing");
                dLength = math.max(dLength, math.abs(x.Length - m.Length));
                dEnds = math.max(dEnds, math.max(math.distance(x.Start, m.Start), math.distance(x.End, m.End)));
                dReach = math.max(dReach, math.max(math.abs(x.ReachA - m.ReachA), math.abs(x.ReachB - m.ReachB)));
            }
            TestContext.WriteLine($"{pairs.Length} pairs, {crossings} crossing both ways, {identical} of them bit for bit; {flips} verdicts differ, the farthest {worstFlip:E3} m from Touch; largest deviation {dLength:E3} m in length, {dEnds:E3} m in the ends, {dReach:E3} m in reach");
            Assert.That(crossings, Is.GreaterThan(pairs.Length / 10));
            Assert.That(worstFlip, Is.LessThanOrEqualTo(rounding), "a verdict Burst gives otherwise farther from Touch than rounding");
            Assert.That(dLength, Is.LessThanOrEqualTo(tolerance));
            Assert.That(dEnds, Is.LessThanOrEqualTo(tolerance));
            Assert.That(dReach, Is.LessThanOrEqualTo(tolerance));
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
        }
    }
}
