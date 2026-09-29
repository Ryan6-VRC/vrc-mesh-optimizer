using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // A sample records which renderers covered it in 64 bits, one per renderer, and renderers from the 64th on
    // share the last bit. Sharing can only make a sample look covered by more renderers than it was, so it is
    // re-cast more often; it must never skip a re-cast it needs. These scenes put the renderers the deletion
    // pass depends on past the 63rd and check the verdicts are the ones they get below it.
    public class CoverRecordTests
    {
        const int Renderers = 70;
        // 0 is the root, 1 rides it, and 2 -> 3 -> 4 hang below 1: bone 4 is kin of 2 at two steps but not of 1.
        static readonly int[] Chain = { -1, 0, 1, 2, 3 };

        // The deletion-pass scene: A on 1 under L on 2, under K on 1 whose top a frame lifts clear, under M on
        // 4. L is hidden and deleted; in K's frame nothing co-moving is over A's top.
        static List<Shell> KeyShells()
        {
            var a = new Shell { Name = "A", Radius = 0.1f, Weights = MeshBuilder.On(1), Occludes = false };
            var l = new Shell { Name = "L", Radius = 0.106f, Weights = MeshBuilder.On(2) };
            var k = new Shell { Name = "K", Radius = 0.112f, Weights = MeshBuilder.On(1), IslandDegrees = 50f };
            k.Frames.Add(new Move { Kind = MoveKind.Away, IslandOnly = true, Direction = new float3(1, 0, 0), Amount = 0.5f });
            var m = new Shell { Name = "M", Radius = 0.118f, Weights = MeshBuilder.On(4) };
            return new List<Shell> { a, l, k, m };
        }

        // Pairs of a small sphere under its shell, 3 m apart and so out of each other's reach: every inner one
        // hidden, every shell kept, and the shells some inner samples' only witnesses.
        static List<Shell> Fillers(int count)
        {
            var list = new List<Shell>();
            for (var i = 0; list.Count < count; i++)
            {
                var c = new float3(3f * (i + 1), 0, 0);
                list.Add(new Shell { Name = $"inner{i}", Centre = c, Radius = 0.03f, Rings = 6, Segments = 12, Occludes = false });
                if (list.Count < count) list.Add(new Shell { Name = $"shell{i}", Centre = c, Radius = 0.036f, Rings = 6, Segments = 12 });
            }
            return list;
        }

        static (OcclusionResult[] results, OcclusionStats stats, int[] keyIndex) Solve(Scene scene, bool keyLast)
        {
            var key = KeyShells();
            var fill = Fillers(Renderers - key.Count);
            var order = new List<Shell>();
            if (keyLast) { order.AddRange(fill); order.AddRange(key); }
            else { order.AddRange(key); order.AddRange(fill); }
            var index = new Dictionary<Shell, int>();
            foreach (var s in order) index[s] = s.AddTo(scene)[0].index;
            Assert.That(scene.Meshes.Count, Is.EqualTo(Renderers));
            var r = scene.Solve(OcclusionSettings.Default, Chain);
            var report = CoverChecker.Check(scene.Meshes, Chain, null, OcclusionSettings.Default, r);
            Assert.That(report.Ok, Is.True, $"key renderers {(keyLast ? "past the 63rd" : "first")}:\n{report}");
            var keyIndex = new int[key.Count];
            for (var i = 0; i < key.Count; i++) keyIndex[i] = index[key[i]];
            return (r, scene.Stats, keyIndex);
        }

        [Test]
        public void RenderersPastTheSixtyThird_ShareABit_AndDecideAsBelowIt()
        {
            using var low = new Scene();
            using var high = new Scene();
            var (rl, sl, kl) = Solve(low, keyLast: false);
            var (rh, sh, kh) = Solve(high, keyLast: true);
            Assert.That(kh[0], Is.GreaterThanOrEqualTo(63), "the fixture must put the key renderers past the 63rd");

            // A, the renderer whose cover the deletion takes, decides the same; its top is kept in both.
            for (var i = 0; i < kl.Length; i++)
                Assert.That(rh[kh[i]].HiddenTriangles.ToArray(), Is.EqualTo(rl[kl[i]].HiddenTriangles.ToArray()), $"key renderer {i}");
            Assert.That(rl[kl[0]].Declined.ExposedByDeletion, Is.GreaterThan(0), "the scene exercises the deletion pass");
            Assert.That(rh[kh[0]].Declined.ExposedByDeletion, Is.EqualTo(rl[kl[0]].Declined.ExposedByDeletion));
            Assert.That(rl[kl[0]].HiddenCount, Is.GreaterThan(0));
            Assert.That(rl[kl[0]].HiddenCount, Is.LessThan(rl[kl[0]].HiddenTriangles.Length), "K's frame keeps A's top");

            // Every filler decides the same too.
            var hiddenLow = 0;
            var hiddenHigh = 0;
            foreach (var r in rl) hiddenLow += r.HiddenCount;
            foreach (var r in rh) hiddenHigh += r.HiddenCount;
            Assert.That(hiddenHigh, Is.EqualTo(hiddenLow));

            // What sharing costs: past the 63rd, a filler shell shares L's bit, so its inner samples are re-cast
            // after L loses faces though nothing they rest on changed.
            TestContext.WriteLine($"[record] deletion-pass samples: key first {sl.DeletionPassSamples}, key past the 63rd {sh.DeletionPassSamples}");
            Assert.That(sh.DeletionPassSamples, Is.GreaterThan(sl.DeletionPassSamples), "shared bits re-cast more, never less");
        }

        [Test]
        public void ManyRenderers_RandomSectoredScenes([Values(101u, 102u, 103u)] uint seed)
        {
            // Every shell split into 12 to 16 renderers: three to six shells make 36 to 96.
            var tally = new DeletionPassTests.Tally();
            var why = DeletionPassTests.RunRandom(seed, ref tally, minSectors: 12, maxSectors: 16);
            TestContext.WriteLine($"[record] seed {seed}: {tally}");
            Assert.That(why, Is.Null, why);
            Assert.That(tally.Hidden, Is.GreaterThan(0));
        }
    }
}
