using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    public class DecimationTests
    {
        // ------------------------------------------------------------------ skin weights

        /// <summary>A cylinder straight at rest, two bones, weights ramping from one to the other across the joint.</summary>
        static MeshBuilder JointCylinder(bool seam, out int[,] index, out float3[] pivot)
        {
            var b = Fixtures.Cylinder(24, 40, 0.04f, 0.4f, seam, out index);
            for (var v = 0; v < b.VertexCount; v++)
            {
                var t = math.saturate((b.Positions[v].y - 0.17f) / 0.06f);
                b.SetWeights(v, (0, 1f - t), (1, t));
            }
            pivot = new[] { float3.zero, new float3(0, 0.2f, 0) };
            return b;
        }

        static float3[] Bend(MeshBuilder b, float3[] pivot, float radians) =>
            Fixtures.Skin(b, b.Positions.ToArray(), pivot, new[] { quaternion.identity, quaternion.AxisAngle(new float3(1, 0, 0), radians) });

        [Test]
        public void SkinWeightTerm_KeepsTheJointSoItBendsCloserToTheSource()
        {
            var b = JointCylinder(false, out _, out var pivot);
            using var withTerm = Fixtures.Decimate(b, s => { s.SkinWeightPenalty = 1f; return s; });
            using var withoutTerm = Fixtures.Decimate(b, s => { s.SkinWeightPenalty = 0f; return s; });
            var target = withTerm.InitialTriangleCount / 4;
            using var cutWith = withTerm.Cut(Fixtures.CutAtCount(withTerm, target), Allocator.Persistent);
            using var cutWithout = withoutTerm.Cut(Fixtures.CutAtCount(withoutTerm, target), Allocator.Persistent);
            Assert.That(cutWith.Triangles.Length, Is.EqualTo(cutWithout.Triangles.Length).Within(4), "compared at the same triangle count");

            var bent = Bend(b, pivot, math.PI / 2f);
            var all = Enumerable.Range(0, b.VertexCount).ToArray();
            var errWith = Fixtures.SurfaceDistance(bent, all, bent, Fixtures.ToArray(cutWith.Triangles));
            var errWithout = Fixtures.SurfaceDistance(bent, all, bent, Fixtures.ToArray(cutWithout.Triangles));
            TestContext.WriteLine($"bent 90 deg at {cutWith.Triangles.Length} tris: mean {errWith.mean * 1000:F3} mm / max {errWith.max * 1000:F3} mm with the term, mean {errWithout.mean * 1000:F3} mm / max {errWithout.max * 1000:F3} mm without");

            Assert.That(errWith.mean, Is.LessThan(errWithout.mean * 0.7f), "mean distance to the bent source surface");
            Assert.That(errWith.max, Is.LessThan(errWithout.max), "largest distance to the bent source surface");
        }

        // ------------------------------------------------------------------ shape frames

        /// <summary>A flat patch and a live frame that raises a smooth bump out of it, as an eyelid or a cheek
        /// shape does to a surface that is flat at rest.</summary>
        static MeshBuilder BumpPatch(out Dictionary<int, float3> frame)
        {
            var b = Fixtures.Grid(24, 24, 0.1f, 0.1f, out _);
            frame = b.AddFrame();
            for (var v = 0; v < b.VertexCount; v++)
            {
                var p = b.Positions[v];
                var h = 0.01f * math.exp(-(p.x * p.x + p.y * p.y) / (0.015f * 0.015f));
                if (h > 1e-5f) frame[v] = new float3(0, 0, h);
            }
            return b;
        }

        [Test]
        public void ALiveFrame_KeepsTheVerticesItMoves_AndItsAbsenceLetsThePatchCollapse()
        {
            var b = BumpPatch(out var frame);
            using var withTerm = Fixtures.Decimate(b);
            using var withoutTerm = Fixtures.Decimate(b, includeFrames: false);
            const float tolerance = 1e-4f;
            using var cutWith = withTerm.Cut(Fixtures.CutAtError(withTerm, tolerance), Allocator.Persistent);
            using var cutWithout = withoutTerm.Cut(Fixtures.CutAtError(withoutTerm, tolerance), Allocator.Persistent);

            var moved = frame.Where(kv => kv.Value.z > 0.002f).Select(kv => kv.Key).ToArray();
            var keptSetWith = Fixtures.Kept(cutWith);
            var keptSetWithout = Fixtures.Kept(cutWithout);
            var keptWith = moved.Count(v => keptSetWith.Contains(v)) / (float)moved.Length;
            var keptWithout = moved.Count(v => keptSetWithout.Contains(v)) / (float)moved.Length;
            TestContext.WriteLine($"within {tolerance * 1000} mm: {cutWith.Triangles.Length} tris keep {keptWith:P0} of the bump with the frame; {cutWithout.Triangles.Length} tris keep {keptWithout:P0} without");
            Assert.That(cutWithout.Triangles.Length, Is.LessThanOrEqualTo(8), "a flat patch with nothing moving it collapses to its corners");
            Assert.That(keptWith, Is.GreaterThan(0.5f), "the frame keeps the vertices it moves");
            Assert.That(keptWithout, Is.LessThan(0.1f));

            // At a matched triangle count, measure with the frame applied.
            var count = cutWith.Triangles.Length;
            using var matched = withoutTerm.Cut(Fixtures.CutAtCount(withoutTerm, count), Allocator.Persistent);
            var driven = b.PositionsIn(0);
            var all = Enumerable.Range(0, b.VertexCount).ToArray();
            var errWith = Fixtures.SurfaceDistance(driven, all, driven, Fixtures.ToArray(cutWith.Triangles));
            var errWithout = Fixtures.SurfaceDistance(driven, all, driven, Fixtures.ToArray(matched.Triangles));
            TestContext.WriteLine($"frame applied at {count} tris: max {errWith.max * 1000:F3} mm with the frame in the cost, {errWithout.max * 1000:F3} mm without");
            Assert.That(errWith.max, Is.LessThan(errWithout.max / 3f), "largest error with the frame driven");
            Assert.That(errWith.max, Is.LessThan(2f * tolerance), "the recorded error bounds the driven error");
        }

        [Test]
        public void AnErrorLimit_StopsTheSequenceWithinIt()
        {
            var b = BumpPatch(out _);
            const float limit = 1e-4f;
            using var full = Fixtures.Decimate(b);
            using var limited = Fixtures.Decimate(b, s => { s.MaxError = limit; return s; });
            Assert.That(limited.ErrorAt(limited.Length), Is.LessThanOrEqualTo(limit));
            Assert.That(limited.TriangleCountAt(limited.Length), Is.LessThanOrEqualTo(full.TriangleCountAt(Fixtures.CutAtError(full, limit))));
        }

        [Test]
        public void EveryAggregation_KeepsTheBump()
        {
            var b = BumpPatch(out var frame);
            var moved = frame.Where(kv => kv.Value.z > 0.002f).Select(kv => kv.Key).ToArray();
            foreach (var mode in new[] { ShapeAggregation.WorstCase, ShapeAggregation.Sum, ShapeAggregation.Blend })
            {
                using var seq = Fixtures.Decimate(b, s => { s.Aggregation = mode; return s; });
                using var cut = seq.Cut(Fixtures.CutAtError(seq, 1e-4f), Allocator.Persistent);
                var keptSet = Fixtures.Kept(cut);
                var kept = moved.Count(keptSet.Contains) / (float)moved.Length;
                TestContext.WriteLine($"{mode}: {cut.Triangles.Length} tris, {kept:P0} of the bump kept");
                Assert.That(kept, Is.GreaterThan(0.5f), mode.ToString());
            }
        }

        // ------------------------------------------------------------------ seams

        [Test]
        public void ASeamedCylinder_DecimatesAlongTheSeam_WithoutOpeningAGap_AtRestOrBent()
        {
            var b = JointCylinder(true, out var idx, out var pivot);
            using var seq = Fixtures.Decimate(b);
            using var cut = seq.Cut(Fixtures.CutAtCount(seq, seq.InitialTriangleCount * 3 / 10), Allocator.Persistent);
            var kept = Fixtures.Kept(cut);
            var tris = Fixtures.ToArray(cut.Triangles);

            // The seam decimates: its two columns lose vertices, and always the same rows on both sides.
            var rows = idx.GetLength(1);
            var seamKept = 0;
            for (var r = 1; r < rows - 1; r++)
            {
                Assert.That(kept.Contains(idx[0, r]), Is.EqualTo(kept.Contains(idx[24, r])), $"row {r}: both sides of the seam agree");
                if (kept.Contains(idx[0, r])) seamKept++;
            }
            Assert.That(seamKept, Is.LessThan(rows - 2), "the seam was decimated, not locked");

            float3[] rest = b.Positions.ToArray();
            var bent = Bend(b, pivot, math.PI / 2f);
            Assert.That(OpenEdgesOffTheEnds(rest, rest, tris), Is.EqualTo(0), "at rest: welded by position, the only open edges are the two end rings");
            Assert.That(OpenEdgesOffTheEnds(bent, rest, tris), Is.EqualTo(0), "bent: welded by bent position, still closed");
        }

        /// <summary>Welds the output by exact position (in the configuration given) and counts edges used by one
        /// triangle only whose ends are not both on an end ring of the cylinder (judged at rest).</summary>
        static int OpenEdgesOffTheEnds(float3[] positions, float3[] rest, int3[] tris)
        {
            var weld = new Dictionary<float3, int>();
            int Id(int v)
            {
                if (!weld.TryGetValue(positions[v], out var id)) weld[positions[v]] = id = weld.Count;
                return id;
            }
            var edges = new Dictionary<(int, int), int>();
            var ends = new Dictionary<(int, int), (int, int)>();
            foreach (var t in tris)
            for (var k = 0; k < 3; k++)
            {
                var a = t[k];
                var c = t[(k + 1) % 3];
                var ia = Id(a);
                var ic = Id(c);
                var key = (math.min(ia, ic), math.max(ia, ic));
                edges.TryGetValue(key, out var n);
                edges[key] = n + 1;
                ends[key] = (a, c);
            }
            var open = 0;
            foreach (var kv in edges)
            {
                if (kv.Value != 1) continue;
                var (a, c) = ends[kv.Key];
                bool OnEnd(int v) => rest[v].y < 1e-6f || rest[v].y > 0.4f - 1e-6f;
                if (!(OnEnd(a) && OnEnd(c))) open++;
            }
            return open;
        }

        // ------------------------------------------------------------------ hair cards

        [Test]
        public void AHairCard_KeepsItsWidthAndTips_AndItsUVsStayNearLinear()
        {
            const int segments = 40;
            const float width = 0.01f;
            var b = Fixtures.Strip(segments, 0.15f, 1.2f, width, out var left, out var right);
            using var seq = Fixtures.Decimate(b);
            using var cut = seq.Cut(Fixtures.CutAtCount(seq, seq.InitialTriangleCount / 4), Allocator.Persistent);
            var kept = Fixtures.Kept(cut);
            var tris = Fixtures.ToArray(cut.Triangles);
            TestContext.WriteLine($"{seq.InitialTriangleCount} -> {tris.Length} tris at {seq.ErrorAt(Fixtures.CutAtCount(seq, seq.InitialTriangleCount / 4)) * 1000:F3} mm");
            Assert.That(tris.Length, Is.LessThanOrEqualTo(seq.InitialTriangleCount / 4), "the card decimates along its length");

            foreach (var tip in new[] { left[0], right[0], left[segments], right[segments] })
                Assert.That(kept.Contains(tip), $"tip corner {tip} survives");

            var leftSet = new HashSet<int>(left);
            var rightSet = new HashSet<int>(right);
            foreach (var t in tris)
            {
                var corners = new[] { t.x, t.y, t.z };
                Assert.That(corners.Any(leftSet.Contains) && corners.Any(rightSet.Contains), "every triangle spans the card's width");
            }

            // The right edge polyline, as the output keeps it; each kept left vertex stays a card's width from it.
            var rightKept = right.Where(kept.Contains).Select(v => b.Positions[v]).ToArray();
            foreach (var v in left.Where(kept.Contains))
            {
                var best = float.PositiveInfinity;
                for (var i = 0; i + 1 < rightKept.Length; i++)
                    best = math.min(best, DistanceToSegment(b.Positions[v], rightKept[i], rightKept[i + 1]));
                Assert.That(best, Is.InRange(0.9f * width, 1.1f * width), $"width at vertex {v}");
            }

            var shift = UvShift(b, kept, tris);
            TestContext.WriteLine($"largest UV shift: U {shift.x:F4}, V {shift.y:F4}");
            Assert.That(shift.x, Is.LessThan(0.05f), "U across the card");
            Assert.That(shift.y, Is.LessThan(0.02f), "V along the card stays near-linear");
        }

        /// <summary>
        /// Every removed vertex's texel, found again on the decimated surface at the closest point: the largest
        /// difference in U and in V from the texel it had.
        /// </summary>
        static float2 UvShift(MeshBuilder b, HashSet<int> kept, int3[] tris)
        {
            var worst = float2.zero;
            for (var v = 0; v < b.VertexCount; v++)
            {
                if (kept.Contains(v)) continue;
                var best = float.PositiveInfinity;
                var uv = float2.zero;
                foreach (var t in tris)
                {
                    var q = Fixtures.ClosestPoint(b.Positions[v], b.Positions[t.x], b.Positions[t.y], b.Positions[t.z], out var bary);
                    var d = math.lengthsq(q - b.Positions[v]);
                    if (d >= best) continue;
                    best = d;
                    uv = bary.x * b.Uvs[t.x] + bary.y * b.Uvs[t.y] + bary.z * b.Uvs[t.z];
                }
                worst = math.max(worst, math.abs(uv - b.Uvs[v]));
            }
            return worst;
        }

        [Test]
        public void TheUvTerm_KeepsAFlatCardsTextureFromSwimming()
        {
            // A straight card whose texture is squeezed in one stretch near its middle, as an atlas packs a
            // detail: geometry alone sees nothing there and would slide those texels.
            const int segments = 40;
            var b = Fixtures.Strip(segments, 0.15f, 1.2f, 0.01f, out var left, out var right);
            for (var i = 0; i <= segments; i++)
            {
                var t = i / (float)segments;
                b.Positions[left[i]] = new float3(0.2f * t, 0, 0);
                b.Positions[right[i]] = new float3(0.2f * t, 0, 0.01f);
                var v = t + 0.05f * math.exp(-(t - 0.5f) * (t - 0.5f) / (0.08f * 0.08f)) * math.sign(t - 0.5f);
                b.Uvs[left[i]] = new float2(0, v);
                b.Uvs[right[i]] = new float2(1, v);
            }
            using var withUv = Fixtures.Decimate(b);
            using var withoutUv = Fixtures.Decimate(b, s => { s.UvWeight = 0f; return s; });
            var target = withUv.InitialTriangleCount / 4;
            using var a = withUv.Cut(Fixtures.CutAtCount(withUv, target), Allocator.Persistent);
            using var c = withoutUv.Cut(Fixtures.CutAtCount(withoutUv, target), Allocator.Persistent);
            var shiftWith = UvShift(b, Fixtures.Kept(a), Fixtures.ToArray(a.Triangles)).y;
            var shiftWithout = UvShift(b, Fixtures.Kept(c), Fixtures.ToArray(c.Triangles)).y;
            TestContext.WriteLine($"at {a.Triangles.Length} tris, largest V shift {shiftWith:F4} with the UV term, {shiftWithout:F4} without");
            Assert.That(shiftWith, Is.LessThan(shiftWithout / 3f));
            Assert.That(shiftWith, Is.LessThan(0.01f));
        }

        static float DistanceToSegment(float3 p, float3 a, float3 b)
        {
            var ab = b - a;
            var t = math.saturate(math.dot(p - a, ab) / math.max(math.lengthsq(ab), 1e-20f));
            return math.distance(p, a + t * ab);
        }

        // ------------------------------------------------------------------ validity and the sequence

        /// <summary>
        /// A sphere with four live frames: a bump, a dent, a pinch that crowds a region toward a point, and a
        /// frame that shrinks a cap to a single point, as a hide key does.
        /// </summary>
        internal static MeshBuilder FramedSphere()
        {
            var b = Fixtures.Icosphere(3, 0.1f);
            var bump = b.AddFrame();
            var dent = b.AddFrame();
            var pinch = b.AddFrame();
            var shrink = b.AddFrame();
            for (var v = 0; v < b.VertexCount; v++)
            {
                var p = b.Positions[v];
                var n = math.normalize(p);
                var a = math.dot(n, new float3(0, 0, 1));
                if (a > 0.85f) bump[v] = n * 0.03f * (a - 0.85f) / 0.15f;
                a = math.dot(n, new float3(1, 0, 0));
                if (a > 0.8f) dent[v] = -n * 0.04f * (a - 0.8f) / 0.2f;
                var pole = new float3(0, 0.1f, 0);
                a = math.dot(n, new float3(0, 1, 0));
                if (a > 0.7f) pinch[v] = (pole - p) * 0.8f * (a - 0.7f) / 0.3f;
                var south = new float3(0, -0.1f, 0);
                if (math.dot(n, new float3(0, -1, 0)) > 0.9f) shrink[v] = south - p;
            }
            return b;
        }

        [Test]
        public void NoCut_LeavesAFlippedOrZeroAreaTriangle_InAnyConfiguration()
        {
            var b = FramedSphere();
            using var seq = Fixtures.Decimate(b);
            Assert.That(seq.TriangleCountAt(seq.Length), Is.LessThan(seq.InitialTriangleCount / 4), "the run went deep enough to test");
            var configs = Enumerable.Range(-1, b.Frames.Count + 1).Select(b.PositionsIn).ToArray();
            var reference = configs.Select(p => VertexNormals(p, b.Triangles)).ToArray();
            for (var i = 0; i <= 20; i++)
            {
                var k = seq.Length * i / 20;
                using var cut = seq.Cut(k, Allocator.Persistent);
                foreach (var t in Fixtures.ToArray(cut.Triangles))
                {
                    for (var c = 0; c < configs.Length; c++)
                    {
                        var p = configs[c];
                        // Two corners the frame itself brings together (the shrunk cap) are no fault of ours.
                        if (math.distance(p[t.x], p[t.y]) < 1e-7f || math.distance(p[t.y], p[t.z]) < 1e-7f || math.distance(p[t.z], p[t.x]) < 1e-7f) continue;
                        var n = math.cross(p[t.y] - p[t.x], p[t.z] - p[t.x]);
                        Assert.That(math.length(n), Is.GreaterThan(1e-10f), $"cut {k}, configuration {c - 1}: zero-area triangle {t}");
                        var r = reference[c][t.x] + reference[c][t.y] + reference[c][t.z];
                        Assert.That(math.dot(n, r), Is.GreaterThan(0f), $"cut {k}, configuration {c - 1}: flipped triangle {t}");
                    }
                }
            }
        }

        static float3[] VertexNormals(float3[] p, List<int3> tris)
        {
            var n = new float3[p.Length];
            foreach (var t in tris)
            {
                var f = math.cross(p[t.y] - p[t.x], p[t.z] - p[t.x]);
                n[t.x] += f;
                n[t.y] += f;
                n[t.z] += f;
            }
            return n.Select(x => math.normalizesafe(x)).ToArray();
        }

        [Test]
        public void TheSequence_IsMonotone_AndEveryCutIsAValidMeshOfTheRecordedCount()
        {
            var b = FramedSphere();
            using var seq = Fixtures.Decimate(b);
            Assert.That(seq.Length, Is.GreaterThan(100));
            for (var k = 1; k < seq.Length; k++)
                Assert.That(seq.Steps[k].Error, Is.GreaterThanOrEqualTo(seq.Steps[k - 1].Error), $"step {k}");
            for (var k = 0; k <= seq.Length; k++)
            {
                using var cut = seq.Cut(k, Allocator.Persistent);
                Assert.That(cut.Triangles.Length, Is.EqualTo(seq.TriangleCountAt(k)), $"cut {k}: triangle count");
                var seen = new HashSet<(int, int, int)>();
                var used = new HashSet<int>();
                foreach (var t in Fixtures.ToArray(cut.Triangles))
                {
                    Assert.That(t.x != t.y && t.y != t.z && t.x != t.z, $"cut {k}: triangle {t} repeats a corner");
                    var s = new[] { t.x, t.y, t.z }.OrderBy(x => x).ToArray();
                    Assert.That(seen.Add((s[0], s[1], s[2])), $"cut {k}: triangle {t} appears twice");
                    used.Add(t.x);
                    used.Add(t.y);
                    used.Add(t.z);
                }
                Assert.That(cut.KeptVertices.AsArray().ToArray(), Is.EqualTo(used.OrderBy(x => x).ToArray()), $"cut {k}: keep-list");
            }
        }

        [Test]
        public void TheWholeSequence_NeverDecimatesAPieceAway()
        {
            // A sheet and a lone triangle beside it, as a small detached card: run to exhaustion.
            var b = Fixtures.Grid(6, 6, 0.06f, 0.06f, out _);
            var a = b.Add(new float3(0.1f, 0, 0));
            var c = b.Add(new float3(0.11f, 0, 0));
            var d = b.Add(new float3(0.1f, 0.01f, 0));
            b.Tri(a, c, d);
            using var seq = Fixtures.Decimate(b);
            using var cut = seq.Cut(seq.Length, Allocator.Persistent);
            var kept = Fixtures.Kept(cut);
            Assert.That(kept.Contains(a) && kept.Contains(c) && kept.Contains(d), "the lone triangle survives");
            Assert.That(cut.Triangles.Length, Is.GreaterThanOrEqualTo(2), "the sheet keeps at least a triangle");
        }

        // ------------------------------------------------------------------ protection

        [Test]
        public void LockedVertices_SurviveEveryCut()
        {
            var b = Fixtures.Icosphere(3, 0.1f);
            var locked = new byte[b.VertexCount];
            for (var v = 0; v < b.VertexCount; v++)
                if (b.Positions[v].z > 0.07f) locked[v] = 1;
            using var lockArray = new NativeArray<byte>(locked, Allocator.Persistent);
            using var seq = Fixtures.Decimate(b, protection: new VertexProtection { Locked = lockArray });
            using var cut = seq.Cut(seq.Length, Allocator.Persistent);
            var kept = Fixtures.Kept(cut);
            for (var v = 0; v < b.VertexCount; v++)
                if (locked[v] != 0) Assert.That(kept.Contains(v), $"locked vertex {v}");
        }

        [Test]
        public void ACostMultiplier_KeepsItsRegionDenserAtTheSameCount()
        {
            var b = Fixtures.Icosphere(3, 0.1f);
            var mult = new float[b.VertexCount];
            var region = new List<int>();
            for (var v = 0; v < b.VertexCount; v++)
            {
                mult[v] = 1f;
                if (b.Positions[v].z > 0.05f)
                {
                    mult[v] = 10f;
                    region.Add(v);
                }
            }
            using var multArray = new NativeArray<float>(mult, Allocator.Persistent);
            using var plain = Fixtures.Decimate(b);
            using var weighted = Fixtures.Decimate(b, protection: new VertexProtection { CostMultiplier = multArray });
            var target = plain.InitialTriangleCount / 4;
            using var a = plain.Cut(Fixtures.CutAtCount(plain, target), Allocator.Persistent);
            using var c = weighted.Cut(Fixtures.CutAtCount(weighted, target), Allocator.Persistent);
            var keptPlain = region.Count(Fixtures.Kept(a).Contains);
            var keptWeighted = region.Count(Fixtures.Kept(c).Contains);
            TestContext.WriteLine($"region keeps {keptWeighted} vertices with the multiplier, {keptPlain} without");
            Assert.That(keptWeighted, Is.GreaterThan(keptPlain * 2));
        }
    }
}
