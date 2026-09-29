using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;
using Unity.Mathematics;
using static Ryan6Vrc.MeshOptimizer.Tests.Decimation.LayerFixtures;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// Two skinned layers a millimetre apart over a joint, and how much of the inner one shows through the outer one
    /// when the joint bends: the protruding area of the inner layer and how deep it protrudes.
    /// </summary>
    internal static class BendFixtures
    {
        public const float TubeLength = 0.3f;
        public const float JointY = 0.15f;
        public const float Gap = 0.001f;

        /// <summary>The child bone's share of a vertex at height y: a smooth ramp <paramref name="halfWidth"/> either side of the joint.</summary>
        public static float Share(float y, float halfWidth) => math.smoothstep(JointY - halfWidth, JointY + halfWidth, y);

        /// <summary>A tube skinned to bone 0 below the joint and bone 1 above it, the weights ramping across it.</summary>
        public static MeshBuilder SkinnedTube(int segments, int rings, float radius, float phase, float halfWidth)
        {
            var b = Tube(segments, rings, TubeLength, (a, y) => radius, phase);
            for (var v = 0; v < b.VertexCount; v++)
            {
                var s = Share(b.Positions[v].y, halfWidth);
                b.SetWeights(v, (0, 1f - s), (1, s));
            }
            return b;
        }

        /// <summary>The builder's vertices with bone 1 turned by <paramref name="degrees"/> about <paramref name="axis"/> through the joint.</summary>
        public static float3[] Bent(MeshBuilder b, float3 axis, float degrees)
        {
            var pivot = new[] { float3.zero, new float3(0f, JointY, 0f) };
            var rotation = new[] { quaternion.identity, quaternion.AxisAngle(math.normalize(axis), math.radians(degrees)) };
            return Fixtures.Skin(b, b.Positions.ToArray(), pivot, rotation);
        }

        /// <summary>The two bones: 0 at the origin, 1 at the joint, both running up Y with their X axes along X.</summary>
        public static BendRig Rig()
        {
            var a = Allocator.Persistent;
            return new BendRig
            {
                Parent = new NativeArray<int>(new[] { -1, 0 }, a),
                Position = new NativeArray<float3>(new[] { float3.zero, new float3(0f, JointY, 0f) }, a),
                Axis = new NativeArray<float3>(new[] { new float3(0f, 1f, 0f), new float3(0f, 1f, 0f) }, a),
                Right = new NativeArray<float3>(new[] { new float3(1f, 0f, 0f), new float3(1f, 0f, 0f) }, a),
            };
        }

        public struct Protrusion
        {
            /// <summary>Square metres of the inner surface that lie outside the outer one.</summary>
            public double Area;
            /// <summary>The deepest of it, in metres.</summary>
            public double Depth;
            /// <summary>Per threshold of <see cref="Depths"/>, the area deeper than it.</summary>
            public double[] Deeper;

            public static readonly float[] Depths = { 0.00025f, 0.0005f, 0.001f, 0.002f };

            public override string ToString()
            {
                var deeper = Deeper;
                return $"{Area * 1e4:0.00} cm2, {Depth * 1000:0.00} mm deep (" +
                       string.Join(", ", Depths.Select((d, i) => $">{d * 1000:0.##} mm {deeper[i] * 1e4:0.00}")) + ")";
            }
        }

        /// <summary>
        /// How much of the inner surface shows through the outer one. Each inner triangle is sampled at four points, each
        /// standing for a quarter of its area; a point protrudes when, looking along the inner surface's outward normal
        /// both ways, the nearest outer surface lies behind it, and it protrudes by that distance. Outer surface farther
        /// than <paramref name="reach"/> is not looked for, so a point with none that near counts as covered.
        /// </summary>
        public static Protrusion Measure(float3[] innerPositions, int3[] innerTriangles, float3[] outerPositions, int3[] outerTriangles, float reach = 0.01f)
        {
            const float cell = 0.01f;
            var grid = new Dictionary<int3, List<int>>();
            for (var j = 0; j < outerTriangles.Length; j++)
            {
                var t = outerTriangles[j];
                float3 a = outerPositions[t.x], b = outerPositions[t.y], c = outerPositions[t.z];
                var lo = (int3)math.floor(math.min(a, math.min(b, c)) / cell);
                var hi = (int3)math.floor(math.max(a, math.max(b, c)) / cell);
                for (var x = lo.x; x <= hi.x; x++)
                for (var y = lo.y; y <= hi.y; y++)
                for (var z = lo.z; z <= hi.z; z++)
                {
                    var key = new int3(x, y, z);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(j);
                }
            }
            var result = new Protrusion { Deeper = new double[Protrusion.Depths.Length] };
            var stamp = new int[outerTriangles.Length];
            var query = 0;
            var samples = new[] { new float3(1f / 3, 1f / 3, 1f / 3), new float3(2f / 3, 1f / 6, 1f / 6), new float3(1f / 6, 2f / 3, 1f / 6), new float3(1f / 6, 1f / 6, 2f / 3) };
            foreach (var t in innerTriangles)
            {
                float3 a = innerPositions[t.x], b = innerPositions[t.y], c = innerPositions[t.z];
                var n = math.cross(b - a, c - a);
                var area = 0.5 * math.length(n);
                if (!(area > 0)) continue;
                n = math.normalize(n);
                foreach (var w in samples)
                {
                    var p = w.x * a + w.y * b + w.z * c;
                    var lo = (int3)math.floor(math.min(p - reach * n, p + reach * n) / cell);
                    var hi = (int3)math.floor(math.max(p - reach * n, p + reach * n) / cell);
                    query++;
                    var nearest = double.PositiveInfinity;
                    var signed = 0.0;
                    for (var x = lo.x; x <= hi.x; x++)
                    for (var y = lo.y; y <= hi.y; y++)
                    for (var z = lo.z; z <= hi.z; z++)
                    {
                        if (!grid.TryGetValue(new int3(x, y, z), out var list)) continue;
                        foreach (var j in list)
                        {
                            if (stamp[j] == query) continue;
                            stamp[j] = query;
                            var o = outerTriangles[j];
                            if (!Ray(p, n, outerPositions[o.x], outerPositions[o.y], outerPositions[o.z], out var along)) continue;
                            if (math.abs(along) > reach || math.abs(along) >= nearest) continue;
                            nearest = math.abs(along);
                            signed = along;
                        }
                    }
                    if (!(signed < 0)) continue;
                    var share = area / 4;
                    result.Area += share;
                    result.Depth = math.max(result.Depth, -signed);
                    for (var k = 0; k < Protrusion.Depths.Length; k++)
                        if (-signed > Protrusion.Depths[k]) result.Deeper[k] += share;
                }
            }
            return result;
        }

        /// <summary>Where the line p + t d meets triangle abc, either side of p.</summary>
        static bool Ray(float3 p, float3 d, float3 a, float3 b, float3 c, out double t)
        {
            t = 0;
            var e1 = (double3)(b - a);
            var e2 = (double3)(c - a);
            var h = math.cross((double3)d, e2);
            var det = math.dot(e1, h);
            if (math.abs(det) < 1e-18) return false;
            var s = (double3)(p - a);
            var u = math.dot(s, h) / det;
            if (u < 0 || u > 1) return false;
            var q = math.cross(s, e1);
            var v = math.dot((double3)d, q) / det;
            if (v < 0 || u + v > 1) return false;
            t = math.dot(e2, q) / det;
            return true;
        }
    }

    // Two skinned layers a millimetre apart over a joint: at rest the clearance rule keeps them apart, but a decimated
    // triangle of the outer layer spanning the joint cuts the chord of the curve the finer source follows once the
    // joint bends, and the inner layer shows through. The target is that the written layers show no protrusion the
    // source layers do not.
    public class BendTests
    {
        /// <summary>
        /// The bends the layers are judged in: the rig's own flexion either way and part of it, and a bend about an axis
        /// halfway between the two the decimator bends about, at an angle it does not use.
        /// </summary>
        static readonly (float3 axis, float degrees)[] Poses =
        {
            (new float3(1f, 0f, 0f), 45f), (new float3(1f, 0f, 0f), 90f), (new float3(1f, 0f, 0f), -90f), (new float3(1f, 0f, 1f), 70f),
        };

        internal sealed class Case : IDisposable
        {
            public Scene Scene;
            public CollapseSequence Inner, Outer;
            public CutResult InnerCut, OuterCut;

            public Case(float halfWidth, bool rig, Func<DecimationSettings, DecimationSettings> tweak = null, float tolerance = 0.001f)
            {
                Scene = new Scene();
                Scene.Add(BendFixtures.SkinnedTube(64, 60, 0.05f, 0f, halfWidth));
                Scene.Add(BendFixtures.SkinnedTube(56, 54, 0.05f + BendFixtures.Gap, 0.5f, halfWidth));
                Scene.BuildSurface();
                if (rig) Scene.Rig = BendFixtures.Rig();
                Inner = Scene.Decimate(0, true, out _, tweak);
                Outer = Scene.Decimate(1, true, out _, tweak);
                InnerCut = Inner.Cut(Fixtures.CutAtError(Inner, tolerance), Allocator.Persistent);
                OuterCut = Outer.Cut(Fixtures.CutAtError(Outer, tolerance), Allocator.Persistent);
            }

            public int Triangles => InnerCut.Triangles.Length + OuterCut.Triangles.Length;

            /// <summary>The four pairings in one bend: source/source, source inner under written outer, written inner under source outer, written/written.</summary>
            public BendFixtures.Protrusion[] Measure(float3 axis, float degrees)
            {
                var inner = Scene.Builders[0];
                var outer = Scene.Builders[1];
                var pi = BendFixtures.Bent(inner, axis, degrees);
                var po = BendFixtures.Bent(outer, axis, degrees);
                var si = inner.Triangles.ToArray();
                var so = outer.Triangles.ToArray();
                var wi = Fixtures.ToArray(InnerCut.Triangles);
                var wo = Fixtures.ToArray(OuterCut.Triangles);
                return new[]
                {
                    BendFixtures.Measure(pi, si, po, so),
                    BendFixtures.Measure(pi, si, po, wo),
                    BendFixtures.Measure(pi, wi, po, so),
                    BendFixtures.Measure(pi, wi, po, wo),
                };
            }

            public void Dispose()
            {
                InnerCut.Dispose();
                OuterCut.Dispose();
                Inner.Dispose();
                Outer.Dispose();
                Scene.Dispose();
            }
        }

        /// <summary>A tenth of a square centimetre: what four samples on a few source triangles add up to, and no patch a viewer sees.</summary>
        const double AreaSlack = 1e-5;

        static List<BendFixtures.Protrusion[]> Report(Case c)
        {
            TestContext.WriteLine($"triangles at 1 mm: {c.InnerCut.Triangles.Length} inner of {c.Scene.Builders[0].Triangles.Count}, {c.OuterCut.Triangles.Length} outer of {c.Scene.Builders[1].Triangles.Count}");
            var all = new List<BendFixtures.Protrusion[]>();
            foreach (var (axis, degrees) in Poses)
            {
                var m = c.Measure(axis, degrees);
                TestContext.WriteLine($"{degrees} degrees about {axis}: source/source {m[0]}; source/written {m[1]}; written/source {m[2]}; written/written {m[3]}");
                all.Add(m);
            }
            return all;
        }

        // What the rule has to fix: measured at rest only, the layers stay apart at rest and the outer one's decimation
        // shows the inner one through it when the joint bends.
        [TestCase(0.03f)]
        [TestCase(0.06f)]
        public void WithoutBends_TheOuterLayersDecimationShowsTheInnerOneWhenTheJointBends(float halfWidth)
        {
            using var c = new Case(halfWidth, false);
            Assert.That(c.Measure(new float3(1f, 0f, 0f), 0f)[3].Area, Is.Zero, "at rest the written layers do not show through each other");
            var all = Report(c);
            Assert.That(all.Max(m => m[1].Area - m[0].Area), Is.GreaterThan(1e-3), "the written outer layer shows over 10 cm2 more of the source inner one than the source does");
        }

        [TestCase(0.03f)]
        [TestCase(0.06f)]
        public void TwoLayersOverAJoint_ShowNoProtrusionWhenBent_ThatTheSourceDoesNotShow(float halfWidth)
        {
            using var c = new Case(halfWidth, true);
            Assert.That(c.Measure(new float3(1f, 0f, 0f), 0f)[3].Area, Is.Zero, "at rest the written layers do not show through each other");
            var all = Report(c);
            for (var i = 0; i < Poses.Length; i++)
            {
                var m = all[i];
                Assert.That(m[3].Area, Is.LessThanOrEqualTo(m[0].Area + AreaSlack), $"{Poses[i].degrees} degrees about {Poses[i].axis}: written/written area over source/source");
                Assert.That(m[1].Area, Is.LessThanOrEqualTo(m[0].Area + AreaSlack), $"{Poses[i].degrees} degrees about {Poses[i].axis}: the written outer layer over the source inner one");
            }
        }

        static CollapseSequence DecimateAlone(MeshBuilder b, bool rig, float degrees = 90f)
        {
            using var mesh = new Disposable<Core.Model.EvalMesh>(b.Build());
            var settings = DecimationSettings.Default;
            settings.Bend.Degrees = degrees;
            var r = rig ? BendFixtures.Rig() : default;
            try
            {
                return Decimator.Run(mesh.Value, settings, default, default, r, Allocator.Persistent, out _);
            }
            finally
            {
                if (rig)
                {
                    r.Parent.Dispose();
                    r.Position.Dispose();
                    r.Axis.Dispose();
                    r.Right.Dispose();
                }
            }
        }

        sealed class Disposable<T> : IDisposable where T : IDisposable
        {
            public readonly T Value;
            public Disposable(T value) => Value = value;
            public void Dispose() => Value.Dispose();
        }

        /// <summary>The farthest a source vertex stands off the written surface with the joint bent 90 degrees, in metres.</summary>
        static float BentDeviation(MeshBuilder b, in CollapseSequence seq)
        {
            using var cut = seq.Cut(Fixtures.CutAtError(seq, 0.001f), Allocator.Persistent);
            var bent = BendFixtures.Bent(b, new float3(1f, 0f, 0f), 90f);
            return Fixtures.SurfaceDistance(bent, Enumerable.Range(0, b.VertexCount), bent, Fixtures.ToArray(cut.Triangles)).max;
        }

        // A bend is measured through each vertex's home, the side it moves with, and where everything a collapse touches
        // moves rigidly with one side the bend is the rest state moved: the sequence is the one recorded without the rig.
        [Test]
        public void ASurfaceOnOneSideOfEveryJoint_DecimatesAsWithoutTheRig()
        {
            var b = BendFixtures.SkinnedTube(56, 54, 0.05f, 0f, 0.03f);
            for (var v = 0; v < b.VertexCount; v++) b.SetWeights(v, (1, 1f));
            using var without = DecimateAlone(b, false);
            using var with = DecimateAlone(b, true);
            Assert.That(with.Length, Is.EqualTo(without.Length));
            for (var i = 0; i < with.Length; i++)
                Assert.That(with.Steps[i].Error, Is.EqualTo(without.Steps[i].Error).Within(1e-7f), $"step {i}");
        }

        // Weights that jump from one bone to the next across a ring of edges leave no vertex split between the bones: the
        // bend is felt only where a collapse joins surface on one side to surface on the other, through their homes.
        [TestCase(0.03f, TestName = "{m}(ramped weights)")]
        [TestCase(0f, TestName = "{m}(weights that jump)")]
        public void OneLayerOverAJoint_FollowsTheJointWhenItBends(float halfWidth)
        {
            var b = BendFixtures.SkinnedTube(56, 54, 0.05f, 0f, halfWidth);
            if (halfWidth == 0f)
                for (var v = 0; v < b.VertexCount; v++)
                    b.SetWeights(v, (b.Positions[v].y > BendFixtures.JointY ? 1 : 0, 1f));
            using var without = DecimateAlone(b, false);
            using var with = DecimateAlone(b, true);
            var off = BentDeviation(b, without);
            var on = BentDeviation(b, with);
            TestContext.WriteLine($"farthest source vertex off the written surface bent 90 degrees: {off * 1000:0.00} mm without bends, {on * 1000:0.00} mm with");
            Assert.That(off, Is.GreaterThan(0.005f), "without bends a written triangle spans the joint");
            Assert.That(on, Is.LessThan(0.002f), "with bends the written surface follows the joint");
        }

        // A share counts as 0 or 1, and its vertex as moving rigidly with its home, only where what that drops moves it by
        // no more than a micrometre in any bend: a small weight far from the joint moves its vertex a visible fraction of
        // a millimetre and is kept, while a weight a float rounding off whole is dropped.
        [Test]
        public void ASmallWeightFarFromTheJoint_IsKept_AndAFloatRoundingIsNot()
        {
            var b = new MeshBuilder();
            var far = b.Add(new float3(0f, -2f, 0f));
            var near = b.Add(new float3(0.01f, BendFixtures.JointY, 0f));
            var whole = b.Add(new float3(0f, BendFixtures.JointY + 0.1f, 0.01f));
            b.Tri(far, near, whole);
            b.SetWeights(far, (0, 1f - 5e-5f), (1, 5e-5f));
            b.SetWeights(near, (0, 1e-7f), (1, 1f - 1e-7f));
            b.SetWeights(whole, (1, 1f));
            using var mesh = new Disposable<Core.Model.EvalMesh>(b.Build());
            var rig = BendFixtures.Rig();
            try
            {
                using var bends = Bends.Build(mesh.Value, rig, BendSettings.Default, Allocator.Persistent);
                // Bone 0 bends everything, so every share on it is whole; bone 1 is the second bent.
                Assert.That(bends.ShareOf(far, 1), Is.EqualTo(5e-5).Within(1e-9), "a small weight two metres from the joint");
                Assert.That(bends.ShareOf(near, 1), Is.EqualTo(1.0), "a float rounding off whole, at the joint");
                Assert.That(bends.ShareOf(whole, 1), Is.EqualTo(1.0));
            }
            finally
            {
                rig.Parent.Dispose();
                rig.Position.Dispose();
                rig.Axis.Dispose();
                rig.Right.Dispose();
            }
        }

        // Bends turned off with the rig given record the very sequence recorded without it, collapse for collapse.
        [Test]
        public void BendsOff_RecordTheSequenceWithoutTheRig()
        {
            var b = BendFixtures.SkinnedTube(56, 54, 0.05f, 0f, 0.03f);
            using var without = DecimateAlone(b, false);
            using var off = DecimateAlone(b, true, 0f);
            Assert.That(off.Length, Is.EqualTo(without.Length));
            Assert.That(off.Records.ToArray(), Is.EqualTo(without.Records.ToArray()));
            for (var i = 0; i < off.Length; i++) Assert.That(off.Steps[i].Error, Is.EqualTo(without.Steps[i].Error), $"step {i}");
        }
    }
}
