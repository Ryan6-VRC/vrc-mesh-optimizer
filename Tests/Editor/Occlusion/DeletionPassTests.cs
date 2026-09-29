using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // The deletion pass under attack. Every scene is solved and then handed to CoverChecker, which re-derives
    // from scratch, by brute force against the kept faces only, that each hidden triangle is covered at rest
    // and in every live frame. The structured scenes each also state the verdict the mechanism under attack
    // owes, so a scene that stops exercising it fails rather than passing empty.
    public class DeletionPassTests
    {
        // 0 is the root, 1 rides it, and 2 -> 3 -> 4 hang below 1: bone 4 is kin of 2 at two steps but not of 1.
        static readonly int[] Chain = { -1, 0, 1, 2, 3 };

        Scene scene;

        [SetUp] public void SetUp() => scene = new Scene();
        [TearDown] public void TearDown() => scene.Dispose();

        static OcclusionSettings Defaults => OcclusionSettings.Default;

        static OcclusionResult[] SolveAndCheck(Scene scene, int[] parents, string context, bool[] vanishes = null, OcclusionSettings? settings = null)
        {
            var s = settings ?? Defaults;
            var r = scene.Solve(s, parents, vanishes);
            var report = CoverChecker.Check(scene.Meshes, parents, vanishes, s, r);
            Assert.That(report.Ok, Is.True, $"{context}\na hidden triangle is not covered by kept faces:\n{report}");
            return r;
        }

        static float Angle(float3 p, float3 axis) => math.degrees(math.acos(math.clamp(math.dot(math.normalize(p), math.normalize(axis)), -1f, 1f)));

        // Triangles of b whose every corner lies within lo..hi degrees of the axis, seen from the origin.
        static int[] Within(MeshBuilder b, float3 axis, float lo, float hi)
        {
            var list = new List<int>();
            for (var t = 0; t < b.TriangleCount; t++)
            {
                var tri = b.Triangle(t);
                var ok = true;
                for (var c = 0; c < 3; c++)
                {
                    var a = Angle(b.Position(tri[c]), axis);
                    ok &= a >= lo && a <= hi;
                }
                if (ok) list.Add(t);
            }
            Assert.That(list, Is.Not.Empty, $"the fixture has no triangle within {lo}..{hi} degrees of {axis}");
            return list.ToArray();
        }

        static void AssertAll(OcclusionResult r, int[] tris, bool hidden, string why)
        {
            foreach (var t in tris) Assert.That(r.HiddenTriangles[t], Is.EqualTo(hidden), $"triangle {t}: {why}");
        }

        static readonly float3 Up = new float3(0, 1, 0);

        static Shell Layer(string name, float radius, int bone) => new Shell { Name = name, Radius = radius, Weights = MeshBuilder.On(bone) };

        static Move CapAway(float3 axis, float capDegrees) => new Move { Kind = MoveKind.Away, Axis = axis, CapDegrees = capDegrees, Amount = 0.3f };

        // A detached cap about +Y, and a frame lifting it half a metre clear along +X.
        static void LiftableTop(Shell s, float degrees)
        {
            s.IslandDegrees = degrees;
            s.Frames.Add(new Move { Kind = MoveKind.Away, IslandOnly = true, Direction = new float3(1, 0, 0), Amount = 0.5f });
        }

        // ------------------------------------------------------------------------------------------------
        // A chain: C innermost on bone 1; B over it on 2; A over B on 4, which covers B but swings against C;
        // D outermost on 1, covering C and B. B is hidden and deleted, so C rests on D alone. Each layer can
        // carry a frame, alone or with others, and the meshes go in either order, so a deleted face is the
        // first witness in some cases and the last in others.

        static IEnumerable<TestCaseData> ChainCases()
        {
            for (var mask = 0; mask < 16; mask++)
                foreach (var reversed in new[] { false, true })
                {
                    var on = ((mask & 1) != 0 ? "C" : "") + ((mask & 2) != 0 ? "B" : "") + ((mask & 4) != 0 ? "A" : "") + ((mask & 8) != 0 ? "D" : "");
                    yield return new TestCaseData(mask, reversed).SetName("ChainOfCovers_FramesOn(" + on + ")" + (reversed ? "_Reversed" : ""));
                }
        }

        [TestCaseSource(nameof(ChainCases))]
        public void ChainOfCovers(int mask, bool reversed)
        {
            var c = Layer("C", 0.1f, 1);
            var b = Layer("B", 0.106f, 2);
            var a = Layer("A", 0.112f, 4);
            var d = Layer("D", 0.118f, 1);
            // C pushed out through B, under A and D; B's side slid away; A's underside slid away; D's top slid away.
            if ((mask & 1) != 0) c.Frames.Add(new Move { Kind = MoveKind.Push, Axis = Up, CapDegrees = 40f, Amount = 0.009f });
            if ((mask & 2) != 0) b.Frames.Add(CapAway(new float3(1, 0, 0), 40f));
            if ((mask & 4) != 0) a.Frames.Add(CapAway(-Up, 40f));
            if ((mask & 8) != 0) LiftableTop(d, 50f);
            var order = reversed ? new[] { d, a, b, c } : new[] { c, b, a, d };
            var built = new Dictionary<Shell, (int index, MeshBuilder mesh)>();
            foreach (var s in order) built[s] = s.AddTo(scene)[0];
            var r = SolveAndCheck(scene, Chain, $"chain mask {mask} reversed {reversed}");

            var (ci, cm) = built[c];
            var (bi, bm) = built[b];
            Assert.That(r[built[a].index].HiddenCount, Is.Zero, "nothing co-moving covers A");
            Assert.That(r[built[d].index].HiddenCount, Is.Zero, "nothing covers D");
            if ((mask & 2) == 0) Assert.That(r[bi].HiddenCount, Is.EqualTo(bm.TriangleCount), "A and D each cover B, and no frame moves both");
            else AssertAll(r[bi], Within(bm, new float3(1, 0, 0), 0f, 30f), false, "B's own frame carries it out of all cover");
            if ((mask & 8) == 0)
            {
                if ((mask & 1) == 0) Assert.That(r[ci].HiddenCount, Is.EqualTo(cm.TriangleCount), "D covers C once B is gone");
            }
            else
            {
                AssertAll(r[ci], Within(cm, Up, 0f, 25f), false, "in D's frame, with B deleted, only A is over C's top, and A swings");
                AssertAll(r[ci], Within(cm, Up, 120f, 180f), true, "D's frame leaves C's underside covered");
                Assert.That(r[ci].Declined.ExposedByDeletion, Is.GreaterThan(0), "C's top is lost to the deletion, not to the rest pose");
            }
        }

        // ------------------------------------------------------------------------------------------------

        [Test]
        public void CoverWhoseOwnCoverIsDeleted_TwoStepsDeep([Values(false, true)] bool outerFrame)
        {
            // C on 1 under B on 2 under A on 3 under Z on 5. Z is kin of A only; A is kin of B and C; B of C.
            // Pass one hides C, B and A. A stays hidden under Z; B, with A gone, rests on nothing co-moving.
            var bones = new[] { -1, 0, 1, 2, 3, 4 };
            var c = Layer("C", 0.1f, 1);
            var b = Layer("B", 0.106f, 2);
            var a = Layer("A", 0.112f, 3);
            var z = Layer("Z", 0.118f, 5);
            if (outerFrame) LiftableTop(z, 40f);
            var (ci, cm) = c.AddTo(scene)[0];
            var (bi, bm) = b.AddTo(scene)[0];
            var (ai, am) = a.AddTo(scene)[0];
            z.AddTo(scene);
            var r = SolveAndCheck(scene, bones, $"two steps deep, outer frame {outerFrame}");
            if (!outerFrame)
            {
                Assert.That(r[ai].HiddenCount, Is.EqualTo(am.TriangleCount), "Z covers A");
                Assert.That(r[bi].HiddenCount, Is.Zero, "B's only co-moving cover is A, which goes");
                Assert.That(r[bi].Declined.ExposedByDeletion, Is.EqualTo(bm.ReferencedVertexCount));
            }
            else
            {
                AssertAll(r[ai], Within(am, Up, 0f, 25f), false, "Z's frame opens over A's top");
                AssertAll(r[ai], Within(am, Up, 90f, 180f), true, "Z's frame leaves A's underside covered");
                AssertAll(r[bi], Within(bm, Up, 0f, 25f), true, "A's top is kept, and covers B's");
                AssertAll(r[bi], Within(bm, Up, 90f, 180f), false, "B's underside rested on A's, which goes");
            }
            TestContext.WriteLine($"C hidden {r[ci].HiddenCount}/{cm.TriangleCount}: C is covered by B, which is kept, but was re-cast with B's pass-one faces removed");
        }

        [Test]
        public void MutualCover_CrossingShells([Values(false, true)] bool frame)
        {
            // P is outside Q over the top and inside it underneath; each hides part of the other.
            var p = new Shell { Name = "P", Radius = 0.106f, Bulge = 0.008f, Rings = 12, Segments = 24 };
            var q = new Shell { Name = "Q", Radius = 0.106f, Bulge = -0.008f, Rings = 12, Segments = 24 };
            if (frame) LiftableTop(p, 35f);
            var (pi, pm) = p.AddTo(scene)[0];
            var (qi, qm) = q.AddTo(scene)[0];
            var r = SolveAndCheck(scene, Chain, $"mutual cover, frame {frame}");
            AssertAll(r[pi], Within(pm, -Up, 0f, 30f), true, "Q covers P's underside");
            if (!frame) AssertAll(r[qi], Within(qm, Up, 0f, 30f), true, "P covers Q's top");
            else AssertAll(r[qi], Within(qm, Up, 0f, 20f), false, "P's frame lifts its top off Q's");
        }

        [Test]
        public void OccluderPartlyHiddenPartlyVisible()
        {
            // A on 1 under L on 2, whose top is under a cap M on 4 (kin of L, not of A). L's top goes; A's top
            // then has only M over it, which swings; A's underside keeps L's visible half.
            var a = Layer("A", 0.1f, 1);
            a.Occludes = false;
            var l = Layer("L", 0.106f, 2);
            var m = Layer("M", 0.112f, 4);
            m.HoleAxis = -Up;
            m.HoleDegrees = 110f;
            var (ai, am) = a.AddTo(scene)[0];
            var (li, lm) = l.AddTo(scene)[0];
            m.AddTo(scene);
            var r = SolveAndCheck(scene, Chain, "partly hidden occluder");
            AssertAll(r[li], Within(lm, Up, 0f, 25f), true, "M covers L's top");
            AssertAll(r[li], Within(lm, Up, 90f, 180f), false, "nothing covers L's underside");
            AssertAll(r[ai], Within(am, Up, 0f, 25f), false, "L's top goes, and M swings against A");
            AssertAll(r[ai], Within(am, Up, 120f, 180f), true, "L's kept underside covers A's");
            Assert.That(r[ai].Declined.ExposedByDeletion, Is.GreaterThan(0));
        }

        [Test]
        public void SampleCoveredByTwoMeshes_OneLosesFaces([Values(false, true)] bool frame, [Values(false, true)] bool reversed)
        {
            // A on 1 under L on 2 and K on 1. L's top is hidden under a cap M on 4 and goes; K still covers A,
            // until K's frame slides its top away.
            var a = Layer("A", 0.1f, 1);
            a.Occludes = false;
            var l = Layer("L", 0.106f, 2);
            var m = Layer("M", 0.112f, 4);
            m.HoleAxis = -Up;
            m.HoleDegrees = 110f;
            var k = Layer("K", 0.118f, 1);
            if (frame) LiftableTop(k, 50f);
            var order = reversed ? new[] { k, m, l, a } : new[] { a, l, m, k };
            var built = new Dictionary<Shell, (int index, MeshBuilder mesh)>();
            foreach (var s in order) built[s] = s.AddTo(scene)[0];
            var r = SolveAndCheck(scene, Chain, $"two covers, frame {frame}, reversed {reversed}");
            var (ai, am) = built[a];
            var (li, lm) = built[l];
            AssertAll(r[li], Within(lm, Up, 0f, 25f), true, "M covers L's top in every configuration");
            if (!frame) Assert.That(r[ai].HiddenCount, Is.EqualTo(am.TriangleCount), "K covers all of A");
            else
            {
                AssertAll(r[ai], Within(am, Up, 0f, 25f), false, "in K's frame, with L's top gone, only M is over A's top");
                AssertAll(r[ai], Within(am, Up, 120f, 180f), true, "K's frame leaves A's underside under L and K");
            }
        }

        [Test]
        public void LatticeOverAHoleTheDeletionOpens()
        {
            // A 20 cm skin triangle on 1 under a cover grid G on 2, 1 cm up. A patch P on 4, 1 cm over G's middle,
            // hides G there, so deleting it opens a hole over the skin's middle that no corner can see: only the
            // interior lattice finds it. P is kin of G but not of the skin.
            MeshBuilder Skin()
            {
                var s = new MeshBuilder();
                var a = s.AddVertex(new float3(-0.1f, 0, -0.06f), Up, float2.zero, MeshBuilder.On(1));
                var b = s.AddVertex(new float3(0f, 0, 0.12f), Up, float2.zero, MeshBuilder.On(1));
                var c = s.AddVertex(new float3(0.1f, 0, -0.06f), Up, float2.zero, MeshBuilder.On(1));
                s.AddTriangle(a, b, c);
                return s;
            }
            MeshBuilder Grid(float y, float half, int bone)
            {
                var g = new MeshBuilder();
                g.Grid(y, -half, half, -half, half, 0.01f, MeshBuilder.On(bone));
                return g;
            }

            scene.Add(Skin(), occludes: false);
            var g1 = Grid(0.01f, 0.2f, 2);
            scene.Add(g1, occludes: true);
            scene.Add(Grid(0.02f, 0.06f, 4), occludes: true);
            var r = SolveAndCheck(scene, Chain, "lattice over a deleted hole");
            Assert.That(r[1].HiddenCount, Is.GreaterThan(0), "P hides the middle of G");
            Assert.That(r[0].CoveredVertices.ToArray(), Is.All.True, "the skin's corners stay under G's kept faces");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "an interior sample sees out through the hole the deletion opens");
            scene.Dispose();

            // Control: P on the skin's own bone covers the hole too, and the skin goes.
            scene = new Scene();
            scene.Add(Skin(), occludes: false);
            scene.Add(Grid(0.01f, 0.2f, 2), occludes: true);
            scene.Add(Grid(0.02f, 0.06f, 1), occludes: true);
            r = SolveAndCheck(scene, Chain, "lattice control");
            Assert.That(r[1].HiddenCount, Is.GreaterThan(0));
            Assert.That(r[0].HiddenTriangles[0], Is.True, "control: P is co-moving with the skin, so it closes the hole");
        }

        // ------------------------------------------------------------------------------------------------
        // Randomized layered scenes. Each seed builds three to six shells about one centre on a small bone
        // tree, with random bones and blends, holes, culling, inward winding, orientation, occluder roles,
        // a vanishing bone, sector splits into several renderers, and zero to two live frames per shell,
        // then shuffles the renderers. The failure names the seed; the scene is a pure function of it.

        static readonly int[] RandomBones = { -1, 0, 1, 2, 3, 0, 5 };
        const int PerBatch = 5;

        internal static List<Shell> RandomShells(uint seed, out bool[] vanishes, int minSectors = 1, int maxSectors = 4)
        {
            var rng = new Random(seed * 747796405u + 2891336453u);
            var layers = rng.NextInt(3, 7);
            var sectors = rng.NextFloat() < 0.25f || minSectors > 1 ? rng.NextInt(math.max(2, minSectors), maxSectors + 1) : 1;
            int[] picks = { 1, 1, 1, 2, 2, 3, 4, 5, 6 };
            var shells = new List<Shell>();
            for (var i = 0; i < layers; i++)
            {
                var res = rng.NextInt(0, 3);
                var sh = new Shell
                {
                    Name = $"L{i}",
                    Radius = 0.1f + 0.007f * i + rng.NextFloat(-0.001f, 0.001f),
                    Rings = 6 + 2 * res,
                    Segments = 12 + 4 * res,
                    Sectors = sectors,
                };
                var bone = picks[rng.NextInt(picks.Length)];
                if (rng.NextFloat() < 0.25f)
                {
                    var other = picks[rng.NextInt(picks.Length)];
                    if (other == bone) other = bone == 1 ? 5 : 1;
                    var w = rng.NextBool() ? 0.65f : 0.35f;
                    sh.Weights = w > 0.5f
                        ? new[] { new BoneInfluence { Bone = bone, Weight = w }, new BoneInfluence { Bone = other, Weight = 1f - w } }
                        : new[] { new BoneInfluence { Bone = other, Weight = 1f - w }, new BoneInfluence { Bone = bone, Weight = w } };
                }
                else sh.Weights = MeshBuilder.On(bone);
                sh.Occludes = rng.NextFloat() < (i == 0 ? 0.5f : 0.85f);
                var cull = rng.NextFloat();
                sh.Culling = cull < 0.75f ? FaceCulling.Back : cull < 0.9f ? FaceCulling.Off : FaceCulling.Front;
                sh.Inward = rng.NextFloat() < 0.08f;
                if (rng.NextFloat() < 0.4f)
                {
                    sh.HoleAxis = rng.NextFloat3Direction();
                    sh.HoleDegrees = rng.NextFloat(10f, 45f);
                }
                if (rng.NextFloat() < 0.6f) sh.Orientation = rng.NextQuaternionRotation();
                if (rng.NextFloat() < 0.25f)
                {
                    sh.IslandAxis = rng.NextFloat3Direction();
                    sh.IslandDegrees = rng.NextFloat(15f, 50f);
                }
                var roll = rng.NextFloat();
                var frames = roll < 0.45f ? 0 : roll < 0.8f ? 1 : 2;
                for (var f = 0; f < frames; f++)
                {
                    var kind = (MoveKind)rng.NextInt(3);
                    var mv = new Move { Kind = kind, Axis = rng.NextFloat3Direction(), CapDegrees = rng.NextFloat(15f, 70f) };
                    if (sh.IslandDegrees > 0f && rng.NextFloat() < 0.5f) mv.IslandOnly = true;
                    switch (kind)
                    {
                        case MoveKind.Away:
                            mv.Amount = rng.NextFloat(0.05f, 0.4f);
                            if (rng.NextBool()) mv.Direction = rng.NextFloat3Direction();
                            break;
                        case MoveKind.Push: mv.Amount = rng.NextFloat(0.004f, 0.025f); break;
                        default:
                            mv.Amount = rng.NextFloat(0.004f, 0.025f);
                            if (rng.NextFloat() < 0.3f) mv.CapDegrees = 180f;
                            break;
                    }
                    sh.Frames.Add(mv);
                }
                shells.Add(sh);
            }
            vanishes = rng.NextFloat() < 0.3f ? new[] { false, false, false, false, false, false, true } : null;
            return shells;
        }

        internal struct Tally
        {
            public int Scenes, Meshes, Hidden, Triangles, ExposedByFrame, ExposedByDeletion, DeletionPassSamples;
            public long Rays;

            public override string ToString() =>
                $"{Scenes} scenes, {Meshes} renderers, hidden {Hidden}/{Triangles} triangles, vertices exposed by a frame {ExposedByFrame}, " +
                $"by the deletion {ExposedByDeletion}, deletion-pass samples {DeletionPassSamples}, checker rays {Rays}";
        }

        /// <summary>Solves one random scene and checks it; returns the failure, or null.</summary>
        internal static string RunRandom(uint seed, ref Tally tally, int minSectors = 1, int maxSectors = 4)
        {
            var shells = RandomShells(seed, out var vanishes, minSectors, maxSectors);
            var context = new StringBuilder($"seed {seed}:");
            foreach (var s in shells) context.Append("\n  ").Append(s);
            using var scene = new Scene();
            var rng = new Random(seed + 0x9E3779B9u);
            var parts = new List<(MeshBuilder b, Shell s)>();
            foreach (var s in shells)
                foreach (var b in s.Build()) parts.Add((b, s));
            for (var i = parts.Count - 1; i > 0; i--)
            {
                var j = rng.NextInt(i + 1);
                (parts[i], parts[j]) = (parts[j], parts[i]);
            }
            foreach (var (b, s) in parts)
            {
                if (s.Culling == FaceCulling.Back) scene.Add(b, s.Occludes);
                else scene.Add(b, s.Occludes, new OccluderSubmesh { Culling = s.Culling });
            }
            var settings = Defaults;
            var r = scene.Solve(settings, RandomBones, vanishes);
            var report = CoverChecker.Check(scene.Meshes, RandomBones, vanishes, settings, r);
            tally.Scenes++;
            tally.Meshes += parts.Count;
            tally.Rays += report.RaysCast;
            tally.DeletionPassSamples += scene.Stats.DeletionPassSamples;
            for (var m = 0; m < r.Length; m++)
            {
                tally.Hidden += r[m].HiddenCount;
                tally.Triangles += r[m].HiddenTriangles.Length;
                tally.ExposedByFrame += r[m].Declined.ExposedByFrame;
                tally.ExposedByDeletion += r[m].Declined.ExposedByDeletion;
            }
            return report.Ok ? null : $"{context}\n{report}";
        }

        [Test]
        public void RandomLayeredScenes([Range(0, 7)] int batch)
        {
            var tally = new Tally();
            var failures = new List<string>();
            for (var i = 0; i < PerBatch; i++)
            {
                var why = RunRandom((uint)(1 + batch * PerBatch + i), ref tally);
                if (why != null) failures.Add(why);
            }
            TestContext.WriteLine($"[random] batch {batch}: {tally}");
            Assert.That(failures, Is.Empty, string.Join("\n\n", failures));
            Assert.That(tally.Hidden, Is.GreaterThan(0), "a batch that hides nothing tests nothing");
        }
    }
}
