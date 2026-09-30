using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Tests.Decimation;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    // Two surfaces each within a millimetre of their own originals can still pass through each other, as skin through
    // tights. The crossing measure reads the line where they do; each test builds surfaces whose crossing is known by
    // construction, including the ones that touch without crossing.
    public class CrossingTests
    {
        const float Cell = 0.005f;
        static readonly CrossingSettings Settings = CrossingSettings.Default;

        // A 20 x 20 grid of 5 mm cells in the XY plane, facing +Z, at height z; the vertices of the 6 x 6 patch from
        // (7, 7) to (12, 12) stand `patch` higher.
        // Two grids laid exactly over each other put one's edges on the other's, where the crossing's pieces meet at
        // shared points no real pair of meshes has, so the outer sheet is laid off by a fraction of a cell.
        static (float3[] p, int3[] t) Sheet(float z, float patch = 0f, float shiftX = 0f)
        {
            var grid = Fixtures.Grid(20, 20, 20 * Cell, 20 * Cell, out var index);
            var p = grid.Positions.ToArray();
            for (var j = 0; j <= 20; j++)
            for (var i = 0; i <= 20; i++)
            {
                var lift = i >= 7 && i <= 12 && j >= 7 && j <= 12 ? patch : 0f;
                p[index[i, j]] += new float3(shiftX, 0, z + lift);
            }
            return (p, grid.Triangles.ToArray());
        }

        // The 1 mm contour of a 2 mm plateau over 6 x 6 vertices falling to 0 in one cell runs half a cell outside it:
        // a 6-cell square whose two corners the diagonals do not reach are cut by a half-cell chamfer each.
        static (float3[] p, int3[] t) Outer(float z = 0.001f) => Sheet(z, shiftX: 0.37f * Cell);

        static readonly float PatchLoop = (24f - 2f * (1f - math.SQRT2 / 2f)) * Cell;

        internal static (CrossingStats stats, CrossingSegment[] segments) Cross((float3[] p, int3[] t) a, (float3[] p, int3[] t) b)
        {
            using var sa = new SurfaceDistanceTests.Side(a.p, a.t, null);
            using var sb = new SurfaceDistanceTests.Side(b.p, b.t, null);
            using var segments = new NativeList<CrossingSegment>(Allocator.Persistent);
            var stats = Crossing.Measure(sa.Surface, sb.Surface, Settings, segments);
            return (stats, segments.AsArray().ToArray());
        }

        static (CrossingStats stats, CrossingSegment[] segments) CrossSelf((float3[] p, int3[] t) a)
        {
            using var sa = new SurfaceDistanceTests.Side(a.p, a.t, null);
            using var segments = new NativeList<CrossingSegment>(Allocator.Persistent);
            var stats = Crossing.MeasureSelf(sa.Surface, Settings, segments);
            return (stats, segments.AsArray().ToArray());
        }

        static CrossingChange Compare(CrossingSegment[] before, CrossingSegment[] after)
        {
            using var b = new NativeArray<CrossingSegment>(before, Allocator.Persistent);
            using var a = new NativeArray<CrossingSegment>(after, Allocator.Persistent);
            return Crossing.Compare(b, a, Settings);
        }

        /// <summary>Whether every segment end meets another segment's end: a closed line.</summary>
        static bool Closed(CrossingSegment[] segments)
        {
            var ends = segments.SelectMany(s => new[] { s.Start, s.End }).ToArray();
            return ends.Select((e, i) => ends.Where((o, k) => k != i && math.distance(e, o) < 1e-6f).Count()).All(n => n == 1);
        }

        static (float3[], int3[]) Join((float3[] p, int3[] t) a, (float3[] p, int3[] t) b) =>
            (a.p.Concat(b.p).ToArray(), a.t.Concat(b.t.Select(x => x + a.p.Length)).ToArray());

        [Test]
        public void TwoSheetsAMillimetreApart_DoNotCross()
        {
            var (stats, segments) = Cross(Sheet(0f), Outer());

            Assert.That(stats.Length, Is.Zero);
            Assert.That(segments, Is.Empty);
        }

        [Test]
        public void InnerSheetPushedThroughOverAPatch_ReadsAClosedLoopRoundThePatch()
        {
            var (stats, segments) = Cross(Sheet(0f, patch: 0.002f), Outer());

            Assert.That(stats.Length, Is.EqualTo(PatchLoop).Within(1e-5), "the contour where the plateau's slopes pass the outer sheet");
            Assert.That(Closed(segments), "the crossing is a closed loop");
            Assert.That(stats.SameFacingLength, Is.EqualTo(stats.Length), "both sheets face +Z");
            Assert.That(stats.ReachAThroughB, Is.EqualTo(0.001f).Within(1e-6f), "the slopes run from 1 mm under the outer sheet to 1 mm over it");
            Assert.That(segments.All(s => s.TriangleA >= 0 && s.TriangleB >= 0));
        }

        [Test]
        public void SurfacesSewnTogether_TouchWithoutCrossing()
        {
            var body = Sheet(0f);
            // A band standing up off the body along a row of the body's own vertices, sewn on at them.
            var row = Enumerable.Range(0, 21).Select(i => body.p[10 * 21 + i]).ToArray();
            var band = Strip(row, new float3(0, 0, 0.004f));
            // A band lying flat on the body over the same cells: coplanar.
            var lying = (body.p.Select(p => p).ToArray(), body.t.Where((_, k) => k / 2 % 20 >= 5 && k / 2 % 20 < 10).ToArray());
            // The same standing band sunk half a millimetre into the body does cross it.
            // Off the row of body edges, so its foot crosses the body's triangles rather than running along their edges.
            var sunk = Strip(row.Select(p => p + new float3(0, 0.3f * Cell, -0.0005f)).ToArray(), new float3(0, 0, 0.004f));

            Assert.That(Cross(body, band).stats.Length, Is.Zero, "sewn along a line");
            Assert.That(Cross(body, lying).stats.Length, Is.Zero, "lying on it");
            Assert.That(Cross(body, sunk).stats.Length, Is.EqualTo(20 * Cell).Within(1e-5), "sunk in, it crosses along its whole foot");
        }

        static (float3[], int3[]) Strip(float3[] foot, float3 up)
        {
            var p = foot.Concat(foot.Select(x => x + up)).ToArray();
            var n = foot.Length;
            var t = new List<int3>();
            for (var i = 0; i + 1 < n; i++)
            {
                t.Add(new int3(i, i + 1, n + i + 1));
                t.Add(new int3(i, n + i + 1, n + i));
            }
            return (p, t.ToArray());
        }

        [Test]
        public void CrossingThereBeforeAndAfter_MovedHalfAMillimetre_IsNotNew()
        {
            var before = Cross(Sheet(0f, patch: 0.002f), Outer());
            var after = Cross(Sheet(0f, patch: 0.002f, shiftX: 0.0005f), Outer());

            var change = Compare(before.segments, after.segments);

            Assert.That(change.Before.Length, Is.EqualTo(PatchLoop).Within(1e-5));
            Assert.That(change.After.Length, Is.EqualTo(PatchLoop).Within(1e-5));
            Assert.That(change.New, Is.Zero);
            Assert.That(change.Runs, Is.Zero);
        }

        [Test]
        public void CrossingOnlyAfter_IsNew_AndItsLongestRunIsLocated()
        {
            var before = Cross(Sheet(0f), Outer());
            var after = Cross(Sheet(0f, patch: 0.002f), Outer());

            var change = Compare(before.segments, after.segments);

            Assert.That(change.Before.Length, Is.Zero);
            Assert.That(change.New, Is.EqualTo(PatchLoop).Within(1e-5));
            Assert.That(change.NewSameFacing, Is.EqualTo(change.New));
            Assert.That(change.Runs, Is.EqualTo(1), "one closed loop");
            Assert.That(change.LongestRun, Is.EqualTo(change.New).Within(1e-6));
            // On the loop, half a cell outside the patch's 25 mm square centred on (-0.5, -0.5) cells.
            var at = change.LongestRunAt;
            var edge = math.cmax(math.abs(at.xy - new float2(-0.5f, -0.5f) * Cell));
            Assert.That(edge, Is.EqualTo(3f * Cell).Within(0.5f * Cell));
            Assert.That(after.segments.Any(s => s.TriangleA == change.LongestRunTriangleA && s.TriangleB == change.LongestRunTriangleB),
                "the run names a triangle pair that crosses");
        }

        [Test]
        public void TwoLayersOfOneMesh_DrivenThroughEachOther_Cross()
        {
            var flat = CrossSelf(Join(Sheet(0f), Outer()));
            var pushed = CrossSelf(Join(Sheet(0f, patch: 0.002f), Outer()));
            var single = CrossSelf(Sheet(0f, patch: 0.002f));

            Assert.That(flat.stats.Length, Is.Zero, "two layers apart");
            Assert.That(single.stats.Length, Is.Zero, "one sheet's own neighbouring triangles meet without crossing");
            Assert.That(pushed.stats.Length, Is.EqualTo(PatchLoop).Within(1e-5));
            Assert.That(pushed.segments.All(s => s.TriangleA < s.TriangleB), "each pair once");
        }

        [Test]
        public void TwoTrianglesOfOneMeshSharingAVertex_CrossingAwayFromIt_Cross()
        {
            // One triangle in the XY plane, one in the XZ plane, both from the origin: they cross along the X axis for 1 cm.
            var p = new[]
            {
                float3.zero, new float3(0.01f, 0.01f, 0f), new float3(0.01f, -0.01f, 0f), new float3(0.01f, 0f, 0.01f), new float3(0.01f, 0f, -0.01f),
            };
            var t = new[] { new int3(0, 1, 2), new int3(0, 3, 4) };

            var (stats, _) = CrossSelf((p, t));

            Assert.That(stats.Length, Is.EqualTo(0.01).Within(1e-6));
        }

        static CrossingSegment Segment(float3 start, float3 end) => new CrossingSegment { Start = start, End = end, Length = math.distance(start, end), SameFacing = true };

        [Test]
        public void NewCrossingRunningAwayFromAShippedOne_IsNewFromWhereItLeavesNearness()
        {
            var before = new[] { Segment(new float3(-0.01f, 0, 0), float3.zero) };
            var justPast = new[] { Segment(float3.zero, new float3(0.0055f, 0, 0)) };
            var farOut = new[] { Segment(float3.zero, new float3(0.01f, 0, 0)), Segment(new float3(0.01f, 0, 0), new float3(0.02f, 0, 0)) };

            Assert.That(Compare(before, justPast).New, Is.EqualTo(0.0005).Within(1e-6), "the half millimetre past 5 mm");
            var far = Compare(before, farOut);
            Assert.That(far.New, Is.EqualTo(0.015).Within(1e-6));
            Assert.That(far.Runs, Is.EqualTo(1), "one run across the two segments");
            Assert.That(far.LongestRun, Is.EqualTo(0.015).Within(1e-6));
        }

        // New crossing is priced as its share of each segment's own Length, the figure the after total sums, so a wholly
        // new crossing reads exactly that total. At avatar-sized coordinates a segment's two ends lose precision its
        // Length keeps, so the distance between them is not the figure to price by.
        [Test]
        public void NewCrossing_IsPricedAlongEachSegmentsOwnLength()
        {
            var at = new float3(0.3f, 1.2f, -0.4f);
            (float3[] p, int3[] t) Moved((float3[] p, int3[] t) s) => (s.p.Select(x => x + at).ToArray(), s.t);
            var after = Cross(Moved(Sheet(0f, patch: 0.002f)), Moved(Outer())).segments;
            Assert.That(after.Any(s => math.distance(s.Start, s.End) != s.Length),
                "somewhere the distance between a segment's ends differs from its Length by rounding, or this test tells nothing");

            var whole = Compare(new CrossingSegment[0], after);
            TestContext.WriteLine($"wholly new: New - After.Length = {whole.New - whole.After.Length:E3} m over {after.Length} segments");
            Assert.That(whole.New, Is.EqualTo(whole.After.Length), "wholly new, exactly the after total");
            Assert.That(whole.NewSameFacing, Is.EqualTo(whole.After.SameFacingLength));
            Assert.That(whole.Runs, Is.EqualTo(1), "one closed loop");
            Assert.That(whole.LongestRun, Is.EqualTo(whole.New), "its one run is all of it, priced alike");

            // A before segment on the same line, behind the start, ending Nearness short of the middle: the first half of g is old.
            var g = after.OrderByDescending(s => s.Length).First();
            var dir = math.normalize(g.End - g.Start);
            var end = g.Start + dir * (g.Length / 2f - Settings.Nearness);
            var half = Compare(new[] { Segment(end - dir * 0.01f, end) }, new[] { g });
            TestContext.WriteLine($"half new: New - Length / 2 = {half.New - g.Length / 2.0:E3} m of {g.Length * 1000:0.###} mm");
            Assert.That(half.New, Is.EqualTo(g.Length / 2.0).Within(1e-6), "the half past Nearness");
        }

        [Test]
        public void OneShortBeforeSegment_SpanningMoreCellsThanItsShareOfTheGrid_IsFound()
        {
            // Expanded by Nearness, this segment touches 27 cells of the before grid.
            var before = new[] { Segment(new float3(0.0025f, 0.0025f, 0.0025f), new float3(0.003f, 0.0025f, 0.0025f)) };
            var after = new[] { Segment(new float3(0.0025f, 0.004f, 0.0025f), new float3(0.003f, 0.004f, 0.0025f)) };

            var change = Compare(before, after);

            Assert.That(change.New, Is.Zero);
            Assert.That(change.After.Length, Is.EqualTo(0.0005).Within(1e-7));
        }

        [Test]
        public void ExcludedTriangles_TakeNoPart()
        {
            var inner = Sheet(0f, patch: 0.002f);
            var excluded = Enumerable.Repeat(true, inner.t.Length).ToArray();
            using var sa = new SurfaceDistanceTests.Side(inner.p, inner.t, excluded);
            var outer = Outer();
            using var sb = new SurfaceDistanceTests.Side(outer.p, outer.t, null);
            using var segments = new NativeList<CrossingSegment>(Allocator.Persistent);

            Assert.That(Crossing.Measure(sa.Surface, sb.Surface, Settings, segments).Length, Is.Zero);
        }

        [Test]
        public void SameInput_GivesTheSameSegmentsAndNumbers_BitForBit()
        {
            // Noise so the loop meets triangles at odd angles and the parallel search finds pairs in a varying order.
            var inner = Sheet(0f, patch: 0.002f);
            var rng = new Unity.Mathematics.Random(7);
            for (var i = 0; i < inner.p.Length; i++) inner.p[i].z += rng.NextFloat(-0.0003f, 0.0003f);
            var outer = Outer();

            var first = Cross(inner, outer);
            var second = Cross(inner, outer);
            var c1 = Compare(Cross(Sheet(0f), outer).segments, first.segments);
            var c2 = Compare(Cross(Sheet(0f), outer).segments, second.segments);

            Assert.That(first.segments.Length, Is.GreaterThan(20));
            Assert.That(Bits(second.segments), Is.EqualTo(Bits(first.segments)));
            Assert.That(math.asulong(second.stats.Length), Is.EqualTo(math.asulong(first.stats.Length)));
            Assert.That(math.asulong(c2.New), Is.EqualTo(math.asulong(c1.New)));
            Assert.That(math.asuint(c2.LongestRunAt), Is.EqualTo(math.asuint(c1.LongestRunAt)));
        }

        static uint[] Bits(CrossingSegment[] segments) => segments.SelectMany(s => new[]
        {
            math.asuint(s.Start.x), math.asuint(s.Start.y), math.asuint(s.Start.z), math.asuint(s.End.x), math.asuint(s.End.y), math.asuint(s.End.z), math.asuint(s.Length),
            (uint)s.TriangleA, (uint)s.TriangleB, math.asuint(s.ReachA), math.asuint(s.ReachB),
        }).ToArray();
    }
}
