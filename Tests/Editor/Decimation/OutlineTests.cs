using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    public class OutlineTests
    {
        /// <summary>
        /// A small bevelled plate, a pendant: a flat front, a flat back and a bevel strip between them, each with its own
        /// UV chart over its own vertices, so the front and the back outline are each a closed loop of paired wedges. The
        /// outline is two lobes and one straight run; both faces have interior vertices, which a cut absorbs onto the loops.
        /// </summary>
        static MeshBuilder Plate(out float3[] frontLoop, out float3[] backLoop, out int[] straight)
        {
            const int n = 40;
            const int run = 6;
            var outline = new float2[n];
            for (var i = 0; i < n; i++)
            {
                var a = 2f * math.PI * i / n;
                outline[i] = (0.004f + 0.001f * math.cos(2f * a)) * new float2(math.cos(a), math.sin(a));
            }
            for (var i = 1; i < run; i++) outline[i] = math.lerp(outline[0], outline[run], i / (float)run);
            straight = Enumerable.Range(1, run - 1).ToArray();

            backLoop = outline.Select(p => new float3(p, 0f)).ToArray();
            frontLoop = outline.Select(p => new float3(0.88f * p, 0.0008f)).ToArray();

            var b = new MeshBuilder();
            Face(b, frontLoop, new float2(0.25f, 0.25f), false);
            Face(b, backLoop, new float2(0.75f, 0.25f), true);
            // The strip wraps, so its chart closes on a second column at the first station.
            var low = new int[n + 1];
            var high = new int[n + 1];
            for (var i = 0; i <= n; i++)
            {
                var u = 0.05f + 0.9f * i / n;
                low[i] = b.Add(backLoop[i % n], new float2(u, 0.90f));
                high[i] = b.Add(frontLoop[i % n], new float2(u, 0.92f));
            }
            for (var i = 0; i < n; i++)
            {
                b.Tri(low[i], low[i + 1], high[i + 1]);
                b.Tri(low[i], high[i + 1], high[i]);
            }
            return b;
        }

        static void Face(MeshBuilder b, float3[] loop, float2 uvCentre, bool flip)
        {
            var n = loop.Length;
            float2 Uv(float3 p) => uvCentre + 30f * p.xy;
            var z = new float3(0, 0, loop[0].z);
            var outer = loop.Select(p => b.Add(p, Uv(p))).ToArray();
            var inner = loop.Select(p => b.Add(0.5f * new float3(p.xy, 0) + z, Uv(0.5f * p))).ToArray();
            var centre = b.Add(z, Uv(float3.zero));
            void Tri(int x, int y, int w)
            {
                if (flip) b.Tri(x, w, y);
                else b.Tri(x, y, w);
            }
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                Tri(outer[i], outer[j], inner[j]);
                Tri(outer[i], inner[j], inner[i]);
                Tri(inner[i], inner[j], centre);
            }
        }

        static float DistanceToSegment(float3 p, float3 a, float3 b)
        {
            var ab = b - a;
            var t = math.saturate(math.dot(p - a, ab) / math.max(math.lengthsq(ab), 1e-30f));
            return math.distance(p, a + t * ab);
        }

        /// <summary>How far the loop's source vertices stand from the closed polyline through those of them the cut keeps.</summary>
        static float LoopDeviation(MeshBuilder b, float3[] loop, HashSet<int> kept, out int stations)
        {
            var positions = new HashSet<float3>(kept.Select(v => b.Positions[v]));
            var live = Enumerable.Range(0, loop.Length).Where(i => positions.Contains(loop[i])).ToArray();
            stations = live.Length;
            var worst = 0f;
            for (var k = 0; k < live.Length; k++)
            {
                var from = live[k];
                var to = live[(k + 1) % live.Length];
                for (var i = (from + 1) % loop.Length; i != to; i = (i + 1) % loop.Length)
                    worst = math.max(worst, DistanceToSegment(loop[i], loop[from], loop[to]));
            }
            return worst;
        }

        [Test]
        public void AnOutline_MovesNoFartherThanTheErrorLevel()
        {
            var b = Plate(out var front, out var back, out var straight);
            using var seq = Fixtures.Decimate(b);
            const float tolerance = 1e-3f;
            var at = Fixtures.CutAtError(seq, tolerance);
            using var cut = seq.Cut(at, Allocator.Persistent);
            var kept = Fixtures.Kept(cut);
            var level = seq.ErrorAt(at);

            var frontMoved = LoopDeviation(b, front, kept, out var frontStations);
            var backMoved = LoopDeviation(b, back, kept, out var backStations);
            TestContext.WriteLine($"cut at {level * 1000:F3} mm: {seq.InitialTriangleCount} to {cut.Triangles.Length} triangles; front loop {front.Length} to {frontStations} stations, moved {frontMoved * 1000:F3} mm; back loop {back.Length} to {backStations}, moved {backMoved * 1000:F3} mm");

            Assert.That(frontMoved, Is.LessThanOrEqualTo(level * 1.001f), "the front outline against the error level");
            Assert.That(backMoved, Is.LessThanOrEqualTo(level * 1.001f), "the back outline against the error level");

            // A straight run costs the outline nothing, so a rule that holds the outline still by refusing it fails here.
            var positions = new HashSet<float3>(kept.Select(v => b.Positions[v]));
            Assert.That(straight.Count(i => !positions.Contains(front[i])), Is.GreaterThan(0), "the front loop's straight run loses vertices");
            Assert.That(straight.Count(i => !positions.Contains(back[i])), Is.GreaterThan(0), "the back loop's straight run loses vertices");
        }
    }
}
