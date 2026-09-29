using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // The per-triangle masks a re-judge asks its question with: which of a mesh's triangles may be hidden, and which
    // block. Each test states the verdict a mask owes against the same scene without it, which decides the other way.
    public class MaskTests
    {
        const float Inner = 0.1f;
        const float Middle = 0.105f;
        const float Outer = 0.11f;
        static readonly int[] Bones = { -1, 0 };

        Scene scene;

        [SetUp] public void SetUp() => scene = new Scene();
        [TearDown] public void TearDown() => scene.Dispose();

        static MeshBuilder Sphere(float radius, int rings, int segments)
        {
            var b = new MeshBuilder();
            b.Sphere(float3.zero, radius, rings, segments, MeshBuilder.On(1));
            return b;
        }

        static MeshBuilder Band(float radius, float theta0, float theta1)
        {
            var b = new MeshBuilder();
            b.SphereBand(float3.zero, radius, theta0, theta1, 24, 48, MeshBuilder.On(1));
            return b;
        }

        static float Polar(MeshBuilder b, int t)
        {
            var tri = b.Triangle(t);
            var c = (b.Position(tri.x) + b.Position(tri.y) + b.Position(tri.z)) / 3f;
            return math.degrees(math.acos(math.clamp(math.normalize(c).y, -1f, 1f)));
        }

        static bool[] Where(MeshBuilder b, System.Func<int, bool> pick) => Enumerable.Range(0, b.TriangleCount).Select(pick).ToArray();

        [Test]
        public void ATriangleNotACandidate_IsNeverHidden_AndOnlyCandidatesAreSampled()
        {
            var inner = Sphere(Inner, 16, 32);
            scene.Add(inner, occludes: false);
            scene.Add(Band(Middle, 60f, 180f), occludes: true);
            var whole = scene.Solve(OcclusionSettings.Default, Bones);
            var wholeSamples = scene.Stats.VertexSamples;
            var lower = Where(inner, t => Polar(inner, t) > 100f);
            var upper = Where(inner, t => Polar(inner, t) < 40f);
            Assert.That(Enumerable.Range(0, inner.TriangleCount).All(t => !lower[t] || whole[0].HiddenTriangles[t]), "unmasked, the bowl hides the lower half");
            Assert.That(Enumerable.Range(0, inner.TriangleCount).Any(t => upper[t] && whole[0].HiddenTriangles[t]), Is.False, "and not the open top");
            Assert.That(whole[0].Declined.Total, Is.GreaterThan(0), "the open top's vertices are declined");

            scene.Meshes[0].Candidate = Where(inner, t => lower[t] || upper[t] && t % 2 == 0);
            var masked = scene.Solve(OcclusionSettings.Default, Bones);

            for (var t = 0; t < inner.TriangleCount; t++)
                Assert.That(masked[0].HiddenTriangles[t], Is.EqualTo(lower[t]), $"triangle {t}: hidden exactly when a candidate the bowl covers");
            var onlyLower = Where(inner, t => lower[t]);
            scene.Meshes[0].Candidate = onlyLower;
            var lowerOnly = scene.Solve(OcclusionSettings.Default, Bones);
            for (var t = 0; t < inner.TriangleCount; t++) Assert.That(lowerOnly[0].HiddenTriangles[t], Is.EqualTo(lower[t]), $"triangle {t}");
            Assert.That(scene.Stats.VertexSamples, Is.LessThan(wholeSamples), "a vertex no candidate uses is not sampled");
            Assert.That(lowerOnly[0].Declined.Total, Is.Zero, "nor declined: every candidate is covered");
        }

        [Test]
        public void AFaceThatDoesNotBlock_CoversNothing()
        {
            var inner = Sphere(Inner, 16, 32);
            scene.Add(inner, occludes: false);
            var shell = Sphere(Middle, 24, 48);
            var cover = scene.Add(shell, occludes: true);
            var bottom = Enumerable.Range(0, inner.TriangleCount).Where(t => Polar(inner, t) > 150f).ToArray();
            var top = Enumerable.Range(0, inner.TriangleCount).Where(t => Polar(inner, t) < 60f).ToArray();
            Assert.That(bottom, Is.Not.Empty);

            var whole = scene.Solve(OcclusionSettings.Default, Bones);
            Assert.That(bottom.All(t => whole[0].HiddenTriangles[t]), "every face blocking, the bottom is covered");

            cover.Blocks = Where(shell, t => Polar(shell, t) < 120f);
            var masked = scene.Solve(OcclusionSettings.Default, Bones);

            Assert.That(bottom.Any(t => masked[0].HiddenTriangles[t]), Is.False, "the shell's bottom faces block nothing, so the inner bottom shows");
            Assert.That(top.All(t => masked[0].HiddenTriangles[t]), "the faces that still block still cover the top");
            Assert.That(masked[1].HiddenCount, Is.Zero, "nothing covers the shell");
        }

        // A renderer never covers itself. Entered as one mesh, an inner sphere of candidates under an outer sphere of
        // blockers asks whether the renderer's own faces cover its own, and they may not; as two renderers they do.
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void ABlockerNeverCoversACandidateOfItsOwnMesh(bool oneMesh, bool hidden)
        {
            var inner = Sphere(Inner, 16, 32);
            var outer = Sphere(Middle, 24, 48);
            if (oneMesh)
            {
                var both = new MeshBuilder();
                both.Sphere(float3.zero, Inner, 16, 32, MeshBuilder.On(1));
                both.Sphere(float3.zero, Middle, 24, 48, MeshBuilder.On(1));
                var m = scene.Add(both, occludes: true);
                m.Candidate = Where(both, t => t < inner.TriangleCount);
                m.Blocks = Where(both, t => t >= inner.TriangleCount);
                var r = scene.Solve(OcclusionSettings.Default, Bones);
                Assert.That(r[0].HiddenCount > 0, Is.EqualTo(hidden));
                for (var t = inner.TriangleCount; t < both.TriangleCount; t++) Assert.That(r[0].HiddenTriangles[t], Is.False, "a blocker is not a candidate");
                return;
            }
            scene.Add(inner, occludes: true);
            scene.Add(outer, occludes: true);
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount > 0, Is.EqualTo(hidden));
        }

        [Test]
        public void MasksOfEveryTriangle_DecideAsNoMasks()
        {
            var inner = Sphere(Inner, 16, 32);
            scene.Add(inner, occludes: true);
            var bowl = Band(Middle, 60f, 180f);
            scene.Add(bowl, occludes: true);
            var unmasked = scene.Solve(OcclusionSettings.Default, Bones).Select(r => r.HiddenTriangles.ToArray()).ToArray();
            var unmaskedStats = scene.Stats;

            scene.Meshes[0].Candidate = Where(inner, _ => true);
            scene.Meshes[0].Blocks = Where(inner, _ => true);
            scene.Meshes[1].Candidate = Where(bowl, _ => true);
            scene.Meshes[1].Blocks = Where(bowl, _ => true);
            var masked = scene.Solve(OcclusionSettings.Default, Bones);

            for (var m = 0; m < 2; m++) Assert.That(masked[m].HiddenTriangles.ToArray(), Is.EqualTo(unmasked[m]));
            Assert.That((scene.Stats.VertexSamples, scene.Stats.InteriorSamples, scene.Stats.OccluderTriangles, scene.Stats.Rays),
                Is.EqualTo((unmaskedStats.VertexSamples, unmaskedStats.InteriorSamples, unmaskedStats.OccluderTriangles, unmaskedStats.Rays)));
        }

        // A re-judge's question over a solve's own answer: each mesh's hidden triangles as candidates only, everything
        // else as blockers only. Three layers under a bowl on a key that moves, so the answer rests on the deletion pass
        // (the middle layer is hidden, and the inner is covered past it by the outer) and on a configuration. Nothing
        // moved, so every hidden triangle is hidden again, and the checker agrees on the masked scene.
        [Test]
        public void ASolvesOwnVerdict_JudgedAgainOverWhatItKeeps_StandsWhole()
        {
            var inner = Sphere(Inner, 16, 32);
            var middle = Band(Middle, 40f, 180f);
            var outer = Band(Outer, 60f, 180f);
            var swell = outer.AddFrame();
            for (var v = 0; v < outer.VertexCount; v++)
                swell.Add(new ShapeDelta { Vertex = v, Delta = 0.002f * math.normalize(outer.Position(v)) });
            var builders = new[] { inner, middle, outer };
            foreach (var b in builders) scene.Add(b, occludes: true);
            var first = scene.Solve(OcclusionSettings.Default, Bones).Select(r => r.HiddenTriangles.ToArray()).ToArray();
            Assert.That(first[0].Count(h => h), Is.GreaterThan(0));
            Assert.That(first[1].Count(h => h), Is.GreaterThan(0), "the middle layer is hidden and deleted, so the inner rests on the outer");
            scene.Dispose();

            scene = new Scene();
            for (var m = 0; m < builders.Length; m++)
            {
                var mesh = scene.Add(builders[m], occludes: true);
                var hidden = first[m];
                mesh.Candidate = hidden;
                mesh.Blocks = hidden.Select(h => !h).ToArray();
            }
            var again = scene.Solve(OcclusionSettings.Default, Bones);

            for (var m = 0; m < builders.Length; m++) Assert.That(again[m].HiddenTriangles.ToArray(), Is.EqualTo(first[m]), $"mesh {m}");
            Assert.That(scene.Stats.DeletionPassSamples, Is.Zero, "no candidate blocks, so no deletion pass runs");
            var check = CoverChecker.Check(scene.Meshes, Bones, null, OcclusionSettings.Default, again);
            Assert.That(check.Ok, Is.True, check.ToString());
            Assert.That(check.HiddenChecked, Is.GreaterThan(0));
        }

        // A re-judge enters a renderer it neither judges nor blocks by with its vertices and no triangles, so every other
        // mesh keeps its index. Last in the list, such a mesh starts where the triangles end. It solves, hides nothing, and
        // changes no other mesh's verdict, occluder or not.
        [Test]
        public void AMeshWithNoTriangles_LastInTheList_SolvesAndChangesNothing()
        {
            var inner = Sphere(Inner, 16, 32);
            scene.Add(inner, occludes: false);
            scene.Add(Band(Middle, 60f, 180f), occludes: true);
            var without = scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenTriangles.ToArray();
            Assert.That(without.Count(h => h), Is.GreaterThan(0));

            foreach (var occludes in new[] { false, true })
            {
                scene.Dispose();
                scene = new Scene();
                scene.Add(inner, occludes: false);
                scene.Add(Band(Middle, 60f, 180f), occludes: true);
                var empty = new MeshBuilder();
                empty.AddVertex(new float3(0f, 0.3f, 0f), new float3(0f, 1f, 0f), float2.zero, MeshBuilder.On(1));
                scene.Add(empty, occludes);

                var with = scene.Solve(OcclusionSettings.Default, Bones);

                Assert.That(with[2].HiddenTriangles.Length, Is.Zero, $"occludes {occludes}");
                Assert.That(with[2].CoveredBy.Length, Is.EqualTo(2), $"occludes {occludes}");
                Assert.That(with[0].HiddenTriangles.ToArray(), Is.EqualTo(without), $"occludes {occludes}");
            }
        }
    }
}
