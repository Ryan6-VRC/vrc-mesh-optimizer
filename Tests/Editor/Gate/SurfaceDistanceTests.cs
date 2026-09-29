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
    // The gate's geometric channel claims a distance in metres between an optimized surface and its source. Each test
    // builds a surface pair whose true distance is known by construction and checks the channel reads it, in the
    // direction the damage lives in.
    public class SurfaceDistanceTests
    {
        internal static SurfaceDistanceResult Measure(float3[] pa, int3[] ta, float3[] pb, int3[] tb,
            bool[] excludedA = null, bool[] excludedB = null, SurfaceDistanceSettings? settings = null)
        {
            using var a = new Side(pa, ta, excludedA);
            using var b = new Side(pb, tb, excludedB);
            return SurfaceDistance.Measure(a.Surface, b.Surface, settings ?? SurfaceDistanceSettings.Default);
        }

        internal sealed class Side : IDisposable
        {
            public Surface Surface;

            public Side(float3[] positions, int3[] triangles, bool[] excluded)
            {
                Surface.Positions = new NativeArray<float3>(positions, Allocator.Persistent);
                Surface.Triangles = new NativeArray<int3>(triangles, Allocator.Persistent);
                if (excluded != null) Surface.Excluded = new NativeArray<bool>(excluded, Allocator.Persistent);
            }

            public void Dispose()
            {
                Surface.Positions.Dispose();
                Surface.Triangles.Dispose();
                if (Surface.Excluded.IsCreated) Surface.Excluded.Dispose();
            }
        }

        static readonly Statistic[] AllStatistics = (Statistic[])Enum.GetValues(typeof(Statistic));

        static void AssertAll(DistanceStats s, float expected, float tolerance, string what)
        {
            foreach (var st in AllStatistics)
                Assert.That(s.Get(st), Is.EqualTo(expected).Within(tolerance), $"{what} {st}");
        }

        [Test]
        public void MeshAgainstItself_IsExactlyZero_InBothDirections()
        {
            var sphere = Fixtures.Icosphere(3, 0.1f);
            var p = sphere.Positions.ToArray();
            var t = sphere.Triangles.ToArray();
            // The same triangles, each named from a different corner and listed in reverse: still the same surface.
            var rotated = t.Select(x => new int3(x.y, x.z, x.x)).Reverse().ToArray();

            foreach (var other in new[] { t, rotated })
            {
                var r = Measure(p, t, p, other);
                AssertAll(r.AToB.Stats, 0f, 0f, "A to B");
                AssertAll(r.BToA.Stats, 0f, 0f, "B to A");
                AssertAll(r.Combined, 0f, 0f, "combined");
                Assert.That(r.AToB.Stats.Unmatched + r.BToA.Stats.Unmatched, Is.Zero);
                Assert.That(r.AToB.Stats.Area, Is.GreaterThan(0.1));
            }
        }

        [Test]
        public void Plane_AgainstACopyDisplacedByD_ReadsD_Everywhere()
        {
            const float d = 0.003f;
            var grid = Fixtures.Grid(20, 20, 0.2f, 0.2f, out _);
            var p = grid.Positions.ToArray();
            var t = grid.Triangles.ToArray();
            var moved = p.Select(x => x + new float3(0, 0, d)).ToArray();

            var r = Measure(p, t, moved, t);

            AssertAll(r.AToB.Stats, d, 1e-6f, "A to B");
            AssertAll(r.BToA.Stats, d, 1e-6f, "B to A");
            AssertAll(r.Combined, d, 1e-6f, "combined");
            // The weights are the surface's area: 0.2 m square.
            Assert.That(r.AToB.Stats.Area, Is.EqualTo(0.04).Within(1e-6));
            // A 1 cm cell's triangles are sampled 2 mm apart along their longest edge, 1.41 cm: 8 steps, 45 points.
            Assert.That(r.AToB.Step, Is.LessThanOrEqualTo(SurfaceDistanceSettings.Default.Spacing));
            Assert.That(r.AToB.Stats.Samples, Is.EqualTo(t.Length * 45));
        }

        [Test]
        public void OneVertexSpiked_ReadsTheSpikeOptimizedToSource_AndNamesWhereItIs()
        {
            // 2 mm cells under a 5 cm spike: the source under it is about a cell from the spike's steep sides.
            const float spike = 0.05f;
            var grid = Fixtures.Grid(10, 10, 0.02f, 0.02f, out var index);
            var p = grid.Positions.ToArray();
            var t = grid.Triangles.ToArray();
            var v = index[5, 5];
            var spiked = (float3[])p.Clone();
            spiked[v] += new float3(0, 0, spike);

            var r = Measure(p, t, spiked, t);

            Assert.That(r.BToA.Stats.Max, Is.EqualTo(spike).Within(1e-5f), "the spike's tip stands its height off the source");
            Assert.That(r.AToB.Stats.Max, Is.LessThan(0.1f * spike), "the source under a narrow spike is close to its sides");
            Assert.That(math.distance(r.BToA.MaxPosition, spiked[v]), Is.LessThan(1e-6f), "the max is the tip");
            var tri = t[r.BToA.MaxTriangle];
            Assert.That(tri.x == v || tri.y == v || tri.z == v, $"triangle {r.BToA.MaxTriangle} {tri} does not touch the spiked vertex {v}");
            Assert.That(r.BToA.MaxMatchedTriangle, Is.GreaterThanOrEqualTo(0));
            Assert.That(r.Combined.Max, Is.EqualTo(r.BToA.Stats.Max));
        }

        [Test]
        public void TriangleRemoved_ReadsAsAHole_UnlessMasked()
        {
            const float cell = 0.01f;
            var grid = Fixtures.Grid(10, 10, 0.1f, 0.1f, out _);
            var p = grid.Positions.ToArray();
            var t = grid.Triangles.ToArray();
            // The second triangle of the cell at (5, 5), away from the border.
            const int hole = (5 * 10 + 5) * 2 + 1;
            var without = t.Where((_, i) => i != hole).ToArray();

            var r = Measure(p, t, p, without);
            // The deepest point of the hole is its incentre, 2.9 mm from every edge of a right triangle with 1 cm legs;
            // the lattice need not land on it, and comes within the stated sampling bound.
            var incentre = cell * (2f - math.sqrt(2f)) / 2f;
            Assert.That(r.AToB.Stats.Max, Is.InRange(incentre - r.AToB.Step / math.sqrt(3f), incentre + 1e-6f),
                "a missing triangle is a hole the source's samples fall into");
            Assert.That(r.AToB.MaxTriangle, Is.EqualTo(hole));
            Assert.That(r.BToA.Stats.Max, Is.Zero, "nothing was added");

            var excluded = new bool[t.Length];
            excluded[hole] = true;
            var masked = Measure(p, t, p, without, excludedA: excluded);
            AssertAll(masked.Combined, 0f, 0f, "a triangle removed as hidden and masked out");
            Assert.That(masked.AToB.Stats.Area, Is.EqualTo(0.01 - 0.5 * cell * cell).Within(1e-7), "the masked triangle's area is not sampled");
        }

        [Test]
        public void CoarserSphere_ReadsWithinTheAnalyticBound()
        {
            const float radius = 0.1f;
            var fine = Fixtures.Icosphere(3, radius);
            var coarse = Fixtures.Icosphere(1, radius);
            var pf = fine.Positions.ToArray();
            var tf = fine.Triangles.ToArray();
            var pc = coarse.Positions.ToArray();
            var tc = coarse.Triangles.ToArray();

            // A flat triangle inscribed in a sphere sags furthest at its circumcentre: r - sqrt(r^2 - R^2).
            float Sag(float circumradius) => radius - math.sqrt(radius * radius - circumradius * circumradius);
            var sagCoarse = tc.Max(x => Sag(Circumradius(pc[x.x], pc[x.y], pc[x.z])));
            var sagFine = tf.Max(x => Sag(Circumradius(pf[x.x], pf[x.y], pf[x.z])));
            // The fine sphere has a vertex on the sphere over every coarse edge's midpoint, which is nearest that point.
            var sagEdge = tc.SelectMany(x => new[] { (x.x, x.y), (x.y, x.z), (x.z, x.x) })
                .Max(e => Sag(0.5f * math.distance(pc[e.Item1], pc[e.Item2])));

            var r = Measure(pf, tf, pc, tc);

            // Both surfaces lie between the sphere and the coarse polyhedron, so no point is further apart than the coarse sag.
            Assert.That(r.AToB.Stats.Max, Is.InRange(sagEdge - 1e-6f, sagCoarse + 1e-6f), "fine to coarse");
            // A coarse triangle's circumcentre is its sag inside the sphere; the fine surface is within its own sag of it.
            var sampling = r.BToA.Step / math.sqrt(3f);
            Assert.That(r.BToA.Stats.Max, Is.InRange(sagCoarse - sagFine - sampling, sagCoarse + 1e-6f), "coarse to fine");
            Assert.That(r.BToA.Stats.Mean, Is.GreaterThan(0f).And.LessThan(r.BToA.Stats.Max));
        }

        static float Circumradius(float3 a, float3 b, float3 c)
        {
            var ab = math.distance(a, b);
            var bc = math.distance(b, c);
            var ca = math.distance(c, a);
            return ab * bc * ca / (4f * 0.5f * math.length(math.cross(b - a, c - a)));
        }

        // Two sheets of one garment, facing apart: the outer faces +Z, the lining -Z, `gap` below it.
        static (float3[] p, int3[] t, int outerCount) Shell(float gap, float outerOffset, Func<int, bool> keepOuter = null)
        {
            var outer = Fixtures.Grid(10, 10, 0.1f, 0.1f, out _);
            var p = new List<float3>();
            var t = new List<int3>();
            foreach (var x in outer.Positions) p.Add(x + new float3(0, 0, gap * 0.5f + outerOffset));
            for (var i = 0; i < outer.Triangles.Count; i++)
                if (keepOuter == null || keepOuter(i)) t.Add(outer.Triangles[i]);
            var outerCount = t.Count;
            var n = p.Count;
            foreach (var x in outer.Positions) p.Add(x - new float3(0, 0, gap * 0.5f));
            foreach (var x in outer.Triangles) t.Add(new int3(x.x + n, x.z + n, x.y + n));
            return (p.ToArray(), t.ToArray(), outerCount);
        }

        [Test]
        public void ThinShell_OneSheetDisplaced_ReadsTheDisplacement_NotTheGapToTheOtherSheet()
        {
            const float gap = 0.001f;
            const float push = -0.003f;
            var (pa, ta, _) = Shell(gap, 0f);
            var (pb, tb, _) = Shell(gap, push);

            var r = Measure(pa, ta, pb, tb);
            Assert.That(r.AToB.Stats.Max, Is.EqualTo(-push).Within(1e-6f), "the outer sheet moved 3 mm, through its lining");
            Assert.That(r.BToA.Stats.Max, Is.EqualTo(-push).Within(1e-6f));

            // Matched to whichever sheet is nearest, the same pair reads the gap to the lining instead.
            var naive = SurfaceDistanceSettings.Default;
            naive.MatchFacing = false;
            var n = Measure(pa, ta, pb, tb, settings: naive);
            Assert.That(n.Combined.Max, Is.LessThan(-push - 0.0009f));
        }

        [Test]
        public void CoincidentShell_PatchOfOneSheetDeleted_DoesNotReadZero()
        {
            // A double-sided surface as avatars ship it: two sheets on the same vertices' positions, wound apart.
            var (pa, ta, _) = Shell(0f, 0f);
            // The optimized mesh lost a 4 x 4 cell patch of the outer sheet, and nothing masks it.
            var (pb, tb, _) = Shell(0f, 0f, i =>
            {
                var cell = i / 2;
                int cx = cell % 10, cy = cell / 10;
                return !(cx >= 3 && cx < 7 && cy >= 3 && cy < 7);
            });

            var r = Measure(pa, ta, pb, tb);
            Assert.That(r.AToB.Stats.Max, Is.GreaterThan(0.015f), "the patch's middle is two cells from any surviving outer sheet");

            var naive = SurfaceDistanceSettings.Default;
            naive.MatchFacing = false;
            var n = Measure(pa, ta, pb, tb, settings: naive);
            Assert.That(n.AToB.Stats.Max, Is.LessThan(1e-6f), "matched to the lining under it, the hole would read as nothing");
        }

        [Test]
        public void NothingFacingTheSampleWay_IsUnmatched_AndReadsInfinity()
        {
            var grid = Fixtures.Grid(2, 2, 0.1f, 0.1f, out _);
            var p = grid.Positions.ToArray();
            var t = grid.Triangles.ToArray();
            var flipped = t.Select(x => new int3(x.x, x.z, x.y)).ToArray();

            var r = Measure(p, t, p, flipped);

            Assert.That(r.AToB.Stats.Unmatched, Is.EqualTo(r.AToB.Stats.Samples));
            Assert.That(r.AToB.Stats.Max, Is.EqualTo(float.PositiveInfinity));
            Assert.That(r.AToB.MaxMatchedTriangle, Is.EqualTo(-1));
        }

        [Test]
        public void SameInput_GivesTheSameNumbers_BitForBit()
        {
            var fine = Fixtures.Icosphere(4, 0.1f);
            var coarse = Fixtures.Icosphere(2, 0.1f);
            var pf = fine.Positions.ToArray();
            var pc = coarse.Positions.ToArray();
            // Nudge the coarse sphere off centre so no triangle is shared and every sample is searched for.
            for (var i = 0; i < pc.Length; i++) pc[i] += new float3(0.0013f, -0.0007f, 0.0004f);

            var first = Measure(pf, fine.Triangles.ToArray(), pc, coarse.Triangles.ToArray());
            var second = Measure(pf, fine.Triangles.ToArray(), pc, coarse.Triangles.ToArray());

            Assert.That(first.AToB.Stats.Samples, Is.GreaterThan(fine.Triangles.Count));
            Assert.That(Bits(second), Is.EqualTo(Bits(first)));
        }

        static uint[] Bits(SurfaceDistanceResult r)
        {
            var bits = new List<uint>();
            void Stats(DistanceStats s)
            {
                foreach (var st in AllStatistics) bits.Add(math.asuint(s.Get(st)));
                var area = math.asulong(s.Area);
                bits.Add((uint)area);
                bits.Add((uint)(area >> 32));
                bits.Add((uint)s.Samples);
                bits.Add((uint)s.Unmatched);
            }
            void Directed(DirectedDistance d)
            {
                Stats(d.Stats);
                bits.AddRange(new[] { math.asuint(d.MaxPosition.x), math.asuint(d.MaxPosition.y), math.asuint(d.MaxPosition.z) });
                bits.Add((uint)d.MaxTriangle);
                bits.Add((uint)d.MaxMatchedTriangle);
                bits.Add(math.asuint(d.Step));
            }
            Directed(r.AToB);
            Directed(r.BToA);
            Stats(r.Combined);
            return bits.ToArray();
        }

        [Test]
        public void BadInput_IsRefusedByName()
        {
            var grid = Fixtures.Grid(2, 2, 0.1f, 0.1f, out _);
            var p = grid.Positions.ToArray();
            var t = grid.Triangles.ToArray();
            var bad = (int3[])t.Clone();
            bad[3] = new int3(0, 1, 99);

            var e = Assert.Throws<ArgumentException>(() => Measure(p, t, p, bad));
            StringAssert.Contains("triangle 3", e.Message);
            e = Assert.Throws<ArgumentException>(() => Measure(p, t, p, t, excludedA: new bool[2]));
            StringAssert.Contains("Excluded", e.Message);
            var zero = SurfaceDistanceSettings.Default;
            zero.Spacing = 0f;
            e = Assert.Throws<ArgumentException>(() => Measure(p, t, p, t, settings: zero));
            StringAssert.Contains("Spacing", e.Message);
        }

        [Test]
        public void Pixels_AreTheOffsetOverThePixelFootprintAtTheCameraDistance()
        {
            var camera = GateCamera.Default;
            // 60 degrees over 1080 rows at 1 m: 2 tan 30 / 1080 = 1.069 mm a pixel.
            Assert.That(camera.MetresPerPixel, Is.EqualTo(2f * math.tan(math.radians(30f)) / 1080f).Within(1e-9f));
            Assert.That(camera.Pixels(camera.MetresPerPixel), Is.EqualTo(1f).Within(1e-6f));
            var further = camera;
            further.Distance = 2f;
            Assert.That(further.Pixels(0.001f), Is.EqualTo(camera.Pixels(0.001f) / 2f).Within(1e-6f));
            var sharper = camera;
            sharper.VerticalPixels = 2160;
            Assert.That(sharper.Pixels(0.001f), Is.EqualTo(camera.Pixels(0.001f) * 2f).Within(1e-5f));

            var broken = camera;
            broken.VerticalFovDegrees = 0f;
            Assert.Throws<InvalidOperationException>(() => broken.Pixels(0.001f));
        }
    }
}
