using System;
using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // A configuration is a state of the whole avatar: frames of several meshes that carry one id move together, and
    // every other mesh stands at rest. Each scene here is one the solver judges wrongly if it moves a linked frame
    // alone, or moves frames together that are not one state, and each is also handed to CoverChecker.
    public class ConfigurationTests
    {
        static readonly int[] Bones = { -1, 0 };
        static readonly float3 Away = new float3(1f, 0f, 0f);

        Scene scene;

        [SetUp] public void SetUp() => scene = new Scene();
        [TearDown] public void TearDown() => scene.Dispose();

        OcclusionResult[] SolveAndCheck(string context)
        {
            var s = OcclusionSettings.Default;
            var r = scene.Solve(s, Bones);
            var report = CoverChecker.Check(scene.Meshes, Bones, null, s, r);
            Assert.That(report.Ok, Is.True, $"{context}\na hidden triangle is not covered by kept faces:\n{report}");
            return r;
        }

        /// <summary>A plate facing +Y at height <paramref name="y"/> over [x0, x1] by [-half, half], on bone 1.</summary>
        static MeshBuilder Plate(float y, float x0, float x1, float half)
        {
            var b = new MeshBuilder();
            b.Grid(y, x0, x1, -half, half, 0.01f, MeshBuilder.On(1));
            return b;
        }

        static MeshBuilder Plate(float y, float half) => Plate(y, -half, half, half);

        /// <summary>A frame of <paramref name="configuration"/> moving every vertex of the plate by <paramref name="delta"/>.</summary>
        static MeshBuilder Slides(MeshBuilder b, int configuration, float3 delta)
        {
            var f = b.AddFrame(configuration);
            for (var v = 0; v < b.VertexCount; v++) f.Add(new ShapeDelta { Vertex = v, Delta = delta });
            return b;
        }

        // ------------------------------------------------------------------------------------------------
        // Two covers over a patch of skin, each carrying a key that slides it clear. Moved alone, either key leaves
        // the other cover over the skin; moved together, as one clip drives them, nothing covers it.

        [TestCase(1, 1, false, TestName = "LinkedCovers_SlidClearTogether_ExposeTheSkin_SoItIsKept")]
        [TestCase(0, 0, true, TestName = "LinkedCovers_Control_UnlinkedCoversSlidOneAtATime_LeaveItCovered")]
        [TestCase(1, 2, true, TestName = "LinkedCovers_Control_TwoConfigurationsAreNeverCombined")]
        public void CoversSlidClear(int first, int second, bool hidden)
        {
            var skin = Plate(0f, 0.02f);
            scene.Add(skin, occludes: false);
            scene.Add(Slides(Plate(0.01f, 0.2f), first, Away), occludes: true);
            scene.Add(Slides(Plate(0.02f, 0.2f), second, -Away), occludes: true);

            var r = SolveAndCheck($"covers in configurations {first} and {second}");
            Assert.That(r[0].HiddenCount, Is.EqualTo(hidden ? skin.TriangleCount : 0));
            if (!hidden) Assert.That(r[0].Declined.ExposedByFrame, Is.EqualTo(skin.ReferencedVertexCount), "covered at rest, exposed in the configuration");
        }

        // ------------------------------------------------------------------------------------------------
        // The mirror: skin whose own key slides it out from under its cover, and a cover linked to it that slides with
        // it. Alone, the skin's key exposes it; as the avatar takes the state, the cover comes along.

        [TestCase(1, true, TestName = "SkinAndCover_SlidTogether_StayCovered_SoTheSkinIsRemoved")]
        [TestCase(0, false, TestName = "SkinAndCover_Control_SkinSlidAlone_IsExposed")]
        public void SkinSlidWithItsCover(int configuration, bool hidden)
        {
            var skin = Slides(Plate(0f, 0.02f), configuration, Away);
            scene.Add(skin, occludes: false);
            scene.Add(Slides(Plate(0.01f, 0.2f), configuration, Away), occludes: true);

            var r = SolveAndCheck($"skin and cover in configuration {configuration}");
            Assert.That(r[0].HiddenCount, Is.EqualTo(hidden ? skin.TriangleCount : 0));
        }

        [Test]
        public void SkinAndCover_SlidTogether_ByDifferentAmounts_ExposeWhatTheCoverLeaves()
        {
            // Together, but the cover goes 6 cm further than the skin: the skin's -X edge is then out from under it.
            var skin = Slides(Plate(0f, 0.02f), 1, Away);
            scene.Add(skin, occludes: false);
            scene.Add(Slides(Plate(0.01f, 0.08f), 1, Away + new float3(0.06f, 0f, 0f)), occludes: true);

            var r = SolveAndCheck("cover outrunning the skin");
            Assert.That(r[0].HiddenCount, Is.LessThan(skin.TriangleCount));
            Assert.That(r[0].Declined.ExposedByFrame, Is.GreaterThan(0));
        }

        // ------------------------------------------------------------------------------------------------
        // A mesh whose frame in a configuration moves only part of it: the part it leaves where it was is not moved,
        // but a linked cover's frame can still take its cover away.

        [TestCase(1, false, TestName = "UnmovedPartOfAMovedMesh_LosesItsLinkedCover_AndIsKept")]
        [TestCase(0, true, TestName = "UnmovedPartOfAMovedMesh_Control_WithTheCoverAtRest_IsRemoved")]
        public void UnmovedPartOfAMovedMesh(int coverConfiguration, bool hidden)
        {
            // The skin is a patch at the origin, under the cover, and a second patch half a metre off that its own key
            // lifts. The cover slides clear in configuration 1, or, in the control, in a configuration of its own
            // while a second cover keeps the skin covered there.
            var skin = Plate(0f, 0.02f);
            var far = skin.VertexCount;
            skin.Grid(0f, 0.48f, 0.52f, -0.02f, 0.02f, 0.01f, MeshBuilder.On(1));
            var lift = skin.AddFrame(1);
            for (var v = far; v < skin.VertexCount; v++) lift.Add(new ShapeDelta { Vertex = v, Delta = new float3(0, 0.05f, 0) });
            scene.Add(skin, occludes: false);
            scene.Add(Slides(Plate(0.01f, 0.2f), coverConfiguration, Away), occludes: true);
            if (coverConfiguration == 0) scene.Add(Plate(0.02f, 0.2f), occludes: true);

            var r = SolveAndCheck($"cover in configuration {coverConfiguration}");
            var nearTriangles = 32;
            var nearHidden = 0;
            for (var t = 0; t < nearTriangles; t++) if (r[0].HiddenTriangles[t]) nearHidden++;
            Assert.That(nearHidden, Is.EqualTo(hidden ? nearTriangles : 0));
        }

        // ------------------------------------------------------------------------------------------------
        // Three members: skin and two half-covers, each covering one side of the skin's cone. The skin is covered
        // only when both halves are over it, so it is removed only if all three move together.

        [TestCase(true, TestName = "ThreeMembers_SkinAndBothHalves_MoveTogether_AndTheSkinIsRemoved")]
        [TestCase(false, TestName = "ThreeMembers_Control_OneHalfLeftOut_ExposesTheSkin")]
        public void ThreeMembers(bool bothHalves)
        {
            var skin = Slides(Plate(0f, 0.02f), 1, Away);
            scene.Add(skin, occludes: false);
            scene.Add(Slides(Plate(0.01f, -0.2f, 0.01f, 0.2f), 1, Away), occludes: true);
            scene.Add(Slides(Plate(0.012f, -0.01f, 0.2f, 0.2f), bothHalves ? 1 : 2, Away), occludes: true);

            var r = SolveAndCheck(bothHalves ? "three members" : "two members and a half-cover alone");
            Assert.That(r[0].HiddenCount, Is.EqualTo(bothHalves ? skin.TriangleCount : 0));
        }

        // ------------------------------------------------------------------------------------------------
        // Two covers whose rest bounds are apart: each covers its own side of the skin's cone and a static cover the
        // rest. A configuration sliding one clear re-casts every ray toward it, and not only the rays toward the other.

        [TestCase(0, TestName = "DisjointCovers_LeftSlidClear_ExposesTheSkin")]
        [TestCase(1, TestName = "DisjointCovers_RightSlidClear_ExposesTheSkin")]
        public void DisjointCovers(int slid)
        {
            // The skin is 4 cm wide under three plates at 1 cm: a left and a right one, each 20 cm out to its side from
            // 3 cm past the centre, and a static middle strip between them. Both side plates are in configuration 1;
            // one slides clear and the other lifts 1 mm in place.
            var skin = Plate(0f, 0.02f);
            scene.Add(skin, occludes: false);
            scene.Add(Plate(0.01f, -0.04f, 0.04f, 0.2f), occludes: true);
            var left = Plate(0.011f, -0.24f, -0.03f, 0.2f);
            var right = Plate(0.011f, 0.03f, 0.24f, 0.2f);
            Slides(left, 1, slid == 0 ? -Away : new float3(0, 0.001f, 0));
            Slides(right, 1, slid == 1 ? Away : new float3(0, 0.001f, 0));
            // Listed in this order, so the cover that slides is never the first mesh a frame names.
            if (slid == 0) { scene.Add(right, occludes: true); scene.Add(left, occludes: true); }
            else { scene.Add(left, occludes: true); scene.Add(right, occludes: true); }

            // Only the 75-degree rays of samples a centimetre or more toward the slid side reach past the strip to it.
            var r = SolveAndCheck($"side {slid} slid clear");
            Assert.That(r[0].Declined.ExposedByFrame, Is.GreaterThan(0), "covered at rest, exposed with a side plate gone");
            Assert.That(r[0].HiddenCount, Is.InRange(1, skin.TriangleCount - 1), "the half toward the plate that stays is still covered");
            for (var t = 0; t < skin.TriangleCount; t++)
            {
                var tri = skin.Triangle(t);
                var toward = slid == 0 ? -1f : 1f;
                var reachesSlid = math.cmax(new float3(skin.Position(tri.x).x, skin.Position(tri.y).x, skin.Position(tri.z).x) * toward) >= 0.01f - 1e-6f;
                if (reachesSlid) Assert.That(r[0].HiddenTriangles[t], Is.False, $"triangle {t} has a corner whose cone reaches the slid plate");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // A frame of one mesh's that carries an id no other mesh carries is a configuration of that mesh alone.

        [Test]
        public void ConfigurationOfOneMesh_IsThatFrameAlone()
        {
            var skin = Plate(0f, 0.02f);
            scene.Add(skin, occludes: false);
            scene.Add(Slides(Plate(0.01f, 0.2f), 7, Away), occludes: true);
            scene.Add(Plate(0.02f, 0.2f), occludes: true);

            var r = SolveAndCheck("one mesh in configuration 7");
            Assert.That(r[0].HiddenCount, Is.EqualTo(skin.TriangleCount), "the static cover stays over the skin");
        }

        [Test]
        public void TwoFramesOfOneMesh_InOneConfiguration_AreRefusedByName()
        {
            var cover = Plate(0.01f, 0.2f);
            Slides(cover, 3, Away);
            Slides(cover, 3, -Away);
            scene.Add(Plate(0f, 0.02f), occludes: false);
            scene.Add(cover, occludes: true);

            var e = Assert.Throws<ArgumentException>(() => scene.Solve(OcclusionSettings.Default, Bones));
            Assert.That(e.Message, Does.Contain("mesh 1").And.Contain("configuration 3"));
        }

        // ------------------------------------------------------------------------------------------------
        // Randomized layered scenes, as the deletion pass is attacked with, with frames linked across shells and
        // across the sectors of one shell. The checker re-derives every configuration from the ids on its own.

        [Test]
        public void RandomLinkedLayeredScenes([Range(0, 7)] int batch)
        {
            var tally = new DeletionPassTests.Tally();
            var failures = new List<string>();
            var linked = 0;
            for (var i = 0; i < 5; i++)
            {
                var seed = (uint)(1000 + batch * 5 + i);
                // Odd batches split every shell into sectors, each a renderer carrying the shell's frames.
                var why = DeletionPassTests.RunRandom(seed, ref tally, minSectors: batch % 2 == 1 ? 2 : 1, link: shells => linked += Link(shells, seed));
                if (why != null) failures.Add(why);
            }
            TestContext.WriteLine($"[random linked] batch {batch}: {tally}, {linked} linked frames");
            Assert.That(failures, Is.Empty, string.Join("\n\n", failures));
            Assert.That(tally.Hidden, Is.GreaterThan(0), "a batch that hides nothing tests nothing");
            Assert.That(linked, Is.GreaterThan(0), "a batch that links nothing tests nothing");
        }

        /// <summary>Gives most frames one of three ids, at most one frame of each id per shell; returns how many shells' frames it linked.</summary>
        static int Link(List<Shell> shells, uint seed)
        {
            var rng = new Random(seed * 2654435761u + 1u);
            var count = 0;
            foreach (var sh in shells)
            {
                var used = new HashSet<int>();
                for (var f = 0; f < sh.Frames.Count; f++)
                {
                    if (rng.NextFloat() < 0.15f) continue;
                    var id = rng.NextInt(1, 4);
                    if (!used.Add(id)) continue;
                    var mv = sh.Frames[f];
                    mv.Configuration = id;
                    sh.Frames[f] = mv;
                    count++;
                }
            }
            return count;
        }
    }
}
