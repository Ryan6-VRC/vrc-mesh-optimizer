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

        // Two layers 3 mm apart, each with a nearer layer on its far side: tights 0.2 mm over the skin, and a lining with
        // its outer 0.5 mm over it. Each must keep a share of the 3 mm between the pair. Read against its nearest layer of
        // any, each is held only to a share of its 0.2 or 0.5 mm, may cross nearly all 3 mm toward the other, and the two
        // meet at 2 and 4 mm cuts. The rule runs inside Burst jobs; managed code must keep the gap too.
        [TestCase(false, TestName = "{m}(under Burst)")]
        [TestCase(true, TestName = "{m}(as managed code)")]
        public void TwoLayersEachWithANearerLayerBeyond_KeepTheGapBetweenThem(bool managed)
        {
            const float tight = 0.0002f, lining = 0.0032f, outer = 0.0037f;
            using var scene = new Scene();
            scene.Add(SkinTube());
            var a = scene.Add(LayerTube(Over(tight)));
            var b = scene.Add(Tube(64, 40, Length, Over(lining)));
            scene.Add(LayerTube(Over(outer)));
            scene.BuildSurface();
            Assert.That(Crossing(scene.Builders[a], scene.Builders[b]), Is.Zero, "the source layers do not cross");
            // Each layer is judged against the others' sources, so the skin's and the outer's own cuts do not bear on the pair.
            var held = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled;
            if (!managed) Assume.That(held, "Burst is off in this Editor");
            CollapseSequence ta = default, tb = default;
            ClearanceStats sa, sb;
            try
            {
                if (managed) Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled = false;
                ta = scene.Decimate(a, true, out sa);
                tb = scene.Decimate(b, true, out sb);
            }
            finally
            {
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled = held;
            }
            using var _a = ta;
            using var _b = tb;
            TestContext.WriteLine($"refused {sa.Refused} on the tight layer, {sb.Refused} on the lining; by the pair's gap {sa.RefusedByPairGap} and {sb.RefusedByPairGap}");
            Assert.That(sa.RefusedByPairGap, Is.GreaterThan(0).And.LessThanOrEqualTo(sa.Refused), "the tight layer is held to the pair's gap, and each such refusal is one of the refusals");
            Assert.That(sb.RefusedByPairGap, Is.GreaterThan(0).And.LessThanOrEqualTo(sb.Refused), "and so is the lining");
            var levels = new[] { 0.002f, 0.004f };
            var result = new Dictionary<(float, float), float>();
            foreach (var la in levels)
            foreach (var lb in levels)
            {
                using var ca = ta.Cut(Fixtures.CutAtError(ta, la), Allocator.Persistent);
                using var cb = tb.Cut(Fixtures.CutAtError(tb, lb), Allocator.Persistent);
                result[(la, lb)] = NewCrossing(scene.Builders[a], Fixtures.ToArray(ca.Triangles), scene.Builders[b], Fixtures.ToArray(cb.Triangles));
            }
            TestContext.WriteLine($"new crossing between the tight layer and the lining: {string.Join(", ", result.Select(kv => $"{kv.Key.Item1 * 1000}/{kv.Key.Item2 * 1000} mm: {kv.Value * 1000:0.00} mm"))}");
            foreach (var kv in result)
                Assert.That(kv.Value, Is.Zero, $"new crossing, tight layer cut at {kv.Key.Item1 * 1000} mm and the lining at {kv.Key.Item2 * 1000} mm");
        }

        // Two layers and no third: every source triangle's nearest layer is the one asking, so the pair's gap is never read
        // apart from it and no refusal is counted to it, though the rule refuses.
        [Test]
        public void TwoLayersAlone_NoRefusalIsCountedToThePairsGap()
        {
            using var scene = SkinAndLayer(Gap);
            using var skin = scene.Decimate(0, true, out var ss);
            using var layer = scene.Decimate(1, true, out var sl);
            TestContext.WriteLine($"refused {ss.Refused} on the skin, {sl.Refused} on the layer");
            Assert.That(ss.Refused + sl.Refused, Is.GreaterThan(0), "the rule refuses");
            Assert.That(ss.RefusedByPairGap + sl.RefusedByPairGap, Is.Zero);
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

        // Two fine layers crossing along a ring at about 7 degrees, both cut. The gate reads one cut against the other, and
        // each cut may stand CrossingDrift off its own source where it crosses the other's, so the two drifts add: by the
        // drift alone the crossing may move about 8 mm here, past the gate's 5 mm, which it holds from about 11.5 degrees.
        // Read at every pair of cuts from 0.5 to 4 mm, under Burst and as managed code alike, it moves under 4 mm: the new
        // crossing is 0 at the gate's 5 mm and at 4 mm, and at most 1.2 mm of it lies more than 3 mm from the source's.
        // The drift is not what holds it: the same readings come with CrossingDrift at 4 mm, so this case shows the
        // combined case within the gate here and does not test the drift's bound.
        [Test]
        public void TwoFineLayersCrossingAtAbout7Degrees_BothCut_GainNoNewCrossing()
        {
            using var scene = new Scene();
            scene.Add(SkinTube());
            scene.Add(LayerTube((a, y) => Skin(a, y) + math.lerp(-0.012f, 0.012f, y / Length)));
            scene.BuildSurface();
            var held = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled;
            Assume.That(held, "Burst is off in this Editor, so there is no compiled code to compare");
            var off = NewCrossingAtLevels(scene, false);
            var burst = NewCrossingAtLevels(scene, true);
            Dictionary<(float, float), float> managed;
            try
            {
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled = false;
                managed = NewCrossingAtLevels(scene, true);
            }
            finally
            {
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled = held;
            }
            string Mm(Dictionary<(float, float), float> r) =>
                string.Join(", ", r.Select(kv => $"{kv.Key.Item1 * 1000:0.#}/{kv.Key.Item2 * 1000:0.#} mm: {kv.Value * 1000:0.000} mm"));
            TestContext.WriteLine($"new crossing without the rule: {Mm(off)}");
            TestContext.WriteLine($"new crossing with the rule, under Burst: {Mm(burst)}");
            TestContext.WriteLine($"new crossing with the rule, as managed code: {Mm(managed)}");
            Assert.That(off.Values.Max(), Is.GreaterThan(0.005f), "without the rule the cuts cross anew");
            foreach (var (path, on) in new[] { ("under Burst", burst), ("as managed code", managed) })
            foreach (var kv in on)
                Assert.That(kv.Value, Is.Zero, $"new crossing {path}, one layer cut at {kv.Key.Item1 * 1000} mm and the other at {kv.Key.Item2 * 1000} mm");
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

        // The pair's own gap is read with a query restricted to one layer, and the layer each source triangle's clearance
        // came from is kept: each against a brute-force search, over pieces of a few triangles each.
        [Test]
        public void TriangleToLayer_AndEachTrianglesNearestLayer_AgreeWithBruteForce()
        {
            var rng = new Random(13);
            var o = new float3(0.2f, 1.1f, 0.3f);
            var b = new MeshBuilder();
            for (var i = 0; i < 120; i++)
            {
                // A fan of three triangles around one centre is one piece, so one layer.
                var c = o + rng.NextFloat3(-0.02f, 0.02f);
                var v0 = b.Add(c);
                var v1 = b.Add(c + rng.NextFloat3(-0.004f, 0.004f));
                var v2 = b.Add(c + rng.NextFloat3(-0.004f, 0.004f));
                var v3 = b.Add(c + rng.NextFloat3(-0.004f, 0.004f));
                b.Tri(v0, v1, v2);
                b.Tri(v0, v2, v3);
                b.Tri(v0, v3, v1);
            }
            using var scene = new Scene();
            scene.Add(b);
            scene.BuildSurface();
            var p = b.Positions.ToArray();
            var t = b.Triangles.ToArray();
            var layerOf = scene.Layers[0].ToArray();
            const float reach = 0.02f;
            float Brute(float3 a0, float3 a1, float3 a2, System.Func<int, bool> take)
            {
                var best = float.PositiveInfinity;
                for (var k = 0; k < t.Length; k++)
                    if (take(layerOf[k])) best = math.min(best, TriangleDistance.TriangleSq(a0, a1, a2, p[t[k].x], p[t[k].y], p[t[k].z]));
                return math.min(math.sqrt(best), reach);
            }

            var s = scene.Surface;
            for (var i = 0; i < 200; i++)
            {
                var a0 = o + rng.NextFloat3(-0.02f, 0.02f);
                var a1 = a0 + rng.NextFloat3(-0.01f, 0.01f);
                var a2 = a0 + rng.NextFloat3(-0.01f, 0.01f);
                var layer = layerOf[rng.NextInt(t.Length)];
                Assert.That(s.TriangleToLayer(a0, a1, a2, layer, reach), Is.EqualTo(Brute(a0, a1, a2, l => l == layer)).Within(1e-6f), $"query {i}");
            }
            var withNearest = 0;
            for (var k = 0; k < s.Prims.Length; k++)
            {
                var prim = s.Prims[k];
                var own = s.PrimLayer[k];
                var nearest = s.PrimNearest[k];
                Assert.That(s.PrimClearance[k], Is.EqualTo(Brute(prim.A, prim.B, prim.C, l => l != own)).Within(1e-6f), $"prim {k}: its clearance");
                if (nearest < 0)
                {
                    Assert.That(s.PrimClearance[k], Is.EqualTo(reach), $"prim {k}: no nearest layer, so nothing within the reach");
                    continue;
                }
                Assert.That(nearest, Is.Not.EqualTo(own), $"prim {k}: its own layer");
                Assert.That(Brute(prim.A, prim.B, prim.C, l => l == nearest), Is.EqualTo(s.PrimClearance[k]).Within(1e-6f), $"prim {k}: its clearance is to its nearest layer");
                withNearest++;
            }
            Assert.That(withNearest, Is.GreaterThan(s.Prims.Length / 2), "most triangles have a layer within the reach");
        }

        // The tree search, its bounds and the gaps it keeps between calls decide as a search of every source triangle
        // does, with the pair's gap read and without. Five tubes: skin, tights over it, a lining and its outer, and one
        // farther from the rest than the reach. Each query triangle spans several source triangles of one tube, its
        // corners on that tube's source as the caller promises, from every tube in turn with one set of kept gaps. The
        // pair's gap never refuses against the querying layer's own triangles, never beyond the reach, and never against
        // a layer the surface leaves out: with the skin left out, as removed triangles are with the switch off, every
        // answer is that of a surface built without it, and no triangle names it as its nearest layer.
        [Test]
        public void PairGapRefusals_MatchASearchOfEveryTriangle_AndReachNothingLeftOut()
        {
            var tubes = new[]
            {
                Tube(24, 10, Length, Skin),
                Tube(20, 9, Length, Over(0.0002f), 0.5f),
                Tube(24, 10, Length, Over(0.0032f)),
                Tube(20, 9, Length, Over(0.0037f), 0.5f),
                Tube(20, 9, Length, Over(0.03f), 0.5f),
            };
            var segments = new[] { 24, 20, 24, 20, 20 };
            var reach = ClearanceSettings.Default.Reach;
            const float keep = 0.6f;
            var owned = new List<System.IDisposable>();
            var meshes = tubes.Select(t => t.Build()).ToList();
            owned.AddRange(meshes.Cast<System.IDisposable>());
            ClearanceSurface Build(bool skinLeftOut, bool skinAbsent)
            {
                var sources = new List<ClearanceLayerSource>();
                for (var m = skinAbsent ? 1 : 0; m < meshes.Count; m++)
                {
                    var layers = Uniform(meshes[m].Triangles.Length, m);
                    owned.Add(layers);
                    var source = new ClearanceLayerSource { Positions = meshes[m].Positions, Triangles = meshes[m].Triangles, TriangleLayer = layers };
                    if (m == 0 && skinLeftOut)
                    {
                        source.Excluded = new NativeArray<bool>(Enumerable.Repeat(true, meshes[m].Triangles.Length).ToArray(), Allocator.Persistent);
                        owned.Add(source.Excluded);
                    }
                    sources.Add(source);
                }
                var built = ClearanceSurface.Build(sources, reach, Allocator.Persistent);
                owned.Add(built);
                return built;
            }
            var all = Build(false, false);
            var leftOut = Build(true, false);
            var absent = Build(false, true);
            var gapsAll = new PairGaps(all, Allocator.Persistent);
            var gapsLeftOut = new PairGaps(leftOut, Allocator.Persistent);
            var gapsAbsent = new PairGaps(absent, Allocator.Persistent);
            try
            {
                for (var k = 0; k < leftOut.Prims.Length; k++)
                {
                    Assert.That(leftOut.PrimLayer[k], Is.Not.Zero, "the left-out skin is in the surface");
                    Assert.That(leftOut.PrimNearest[k], Is.Not.Zero, $"prim {k}: the left-out skin as its nearest layer");
                }

                // Every source triangle against the rule as written; the tree, its bounds and the kept gaps are what is tested.
                bool Every(ClearanceSurface s, float3 a, float3 b, float3 c, int layer, float least, bool pairGap, out bool onlyPair)
                {
                    var old = false;
                    var pair = false;
                    for (var k = 0; k < s.Prims.Length; k++)
                    {
                        if (s.PrimLayer[k] == layer) continue;
                        var p = s.Prims[k];
                        var d = TriangleDistance.TriangleSq(a, b, c, p.A, p.B, p.C);
                        var r = keep * math.max(s.PrimClearance[k], least);
                        if (r > 0f && d < r * r) old = true;
                        var own = s.PrimNearest[k];
                        if (!pairGap || own == -1 || own == layer) continue;
                        var corner = keep * math.sqrt(math.min(TriangleDistance.PointSq(a, p.A, p.B, p.C),
                            math.min(TriangleDistance.PointSq(b, p.A, p.B, p.C), TriangleDistance.PointSq(c, p.A, p.B, p.C))));
                        if (!(d < corner * corner)) continue;
                        var toLayer = keep * s.TriangleToLayer(p.A, p.B, p.C, layer, reach);
                        var other = s.PrimLayer[k];
                        var side = keep * math.min(s.PointToLayer(a, other, reach), math.min(s.PointToLayer(b, other, reach), s.PointToLayer(c, other, reach)));
                        if (!(d < toLayer * toLayer) && !(d < side * side)) continue;
                        pair = true;
                        Assert.That(math.sqrt(d), Is.LessThan(keep * reach), "a refusal through the pair's gap lies within the reach");
                    }
                    onlyPair = pair && !old;
                    return old || pair;
                }

                var rng = new Random(29);
                int queries = 0, onlyThroughPair = 0, withoutPair = 0;
                for (var i = 0; i < 1500; i++)
                {
                    var m = rng.NextInt(tubes.Length);
                    var seg = segments[m];
                    var rings = tubes[m].VertexCount / seg - 1;
                    int Vertex(int r, int sg) => math.clamp(r, 0, rings) * seg + (sg % seg + seg) % seg;
                    int r0 = rng.NextInt(rings + 1), s0 = rng.NextInt(seg);
                    var a = tubes[m].Positions[Vertex(r0, s0)];
                    var b = tubes[m].Positions[Vertex(r0 + rng.NextInt(0, 4), s0 + rng.NextInt(1, 4))];
                    var c = tubes[m].Positions[Vertex(r0 + rng.NextInt(1, 4), s0 + rng.NextInt(-1, 2))];
                    if (!(math.lengthsq(math.cross(b - a, c - a)) > 1e-12f)) continue;
                    var least = rng.NextInt(4) == 0 ? 0f : math.min(all.PointClearance(a, m, reach), math.min(all.PointClearance(b, m, reach), all.PointClearance(c, m, reach)));
                    queries++;
                    foreach (var pairGap in new[] { false, true })
                    {
                        var expected = Every(all, a, b, c, m, least, pairGap, out var onlyPair);
                        var actual = all.AnyWithin(a, b, c, m, keep, least, pairGap, ref gapsAll, out var byPair);
                        Assert.That(actual, Is.EqualTo(expected), $"query {i} from tube {m}, least {least * 1000:0.###} mm, the pair's gap {(pairGap ? "read" : "not read")}");
                        if (onlyPair) Assert.That(byPair, $"query {i}: refused through the pair's gap alone, and said so");
                        if (!pairGap && expected) withoutPair++;
                        if (onlyPair) onlyThroughPair++;
                        if (m == 0) continue;
                        var withLeftOut = leftOut.AnyWithin(a, b, c, m, keep, least, pairGap, ref gapsLeftOut, out _);
                        var withAbsent = absent.AnyWithin(a, b, c, m, keep, least, pairGap, ref gapsAbsent, out _);
                        Assert.That(withLeftOut, Is.EqualTo(withAbsent), $"query {i} from tube {m}: the left-out skin decides nothing");
                    }
                }
                TestContext.WriteLine($"{queries} queries: {withoutPair} refused without the pair's gap, {onlyThroughPair} only through it");
                Assert.That(onlyThroughPair, Is.GreaterThan(queries / 50), "the pair's gap decides some queries alone");
                Assert.That(withoutPair, Is.GreaterThan(queries / 50));
            }
            finally
            {
                gapsAll.Dispose();
                gapsLeftOut.Dispose();
                gapsAbsent.Dispose();
                foreach (var o in owned) o.Dispose();
            }
        }
    }
}
