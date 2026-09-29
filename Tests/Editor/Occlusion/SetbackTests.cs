using System;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // The setback a re-judge asks its question with: a cover that moved past a sample, by less than it was allowed to move,
    // still covers it, but only a sample it covered before. Each test states the verdict against the same scene without
    // the setback, or without the name, which decides the other way.
    public class SetbackTests
    {
        const float Sunk = 0.0005f;
        static readonly int[] Bones = { -1, 0 };

        Scene scene;

        [SetUp] public void SetUp() => scene = new Scene();
        [TearDown] public void TearDown() => scene.Dispose();

        /// <summary>A patch 0.02 mm across at the origin, facing +Y: every sample of it stands at the origin, to float noise.</summary>
        static MeshBuilder Speck()
        {
            var b = new MeshBuilder();
            b.Grid(0f, -1e-5f, 1e-5f, -1e-5f, 1e-5f, 1e-5f, MeshBuilder.On(1));
            return b;
        }

        /// <summary>A sheet 2 cm across facing +Y, <see cref="Sunk"/> under the speck, less the cells <paramref name="skip"/> names.</summary>
        static MeshBuilder Sheet(Func<float, float, bool> skip = null)
        {
            var b = new MeshBuilder();
            b.Grid(-Sunk, -0.01f, 0.01f, -0.01f, 0.01f, 0.0005f, MeshBuilder.On(1), skip);
            return b;
        }

        /// <summary>An always-on mesh that only blocks, so every verdict asked is the speck's.</summary>
        OcclusionMesh Cover(MeshBuilder b)
        {
            var m = scene.Add(b, occludes: true);
            m.Candidate = new bool[b.TriangleCount];
            return m;
        }

        static ulong[] Naming(MeshBuilder b, int mesh) => Enumerable.Repeat(1UL << mesh, 2 * b.VertexCount).ToArray();

        // A sheet that sank half a millimetre under the speck covers it only with a setback deeper than that, and only when
        // the speck names the sheet as what covered it; a mesh it does not name covers it only in front.
        [Test]
        public void AFaceBehindASample_CoversItWithinTheSetback_OnlyFromAMeshItNames()
        {
            var speck = Speck();
            var candidate = scene.Add(speck, occludes: false);
            var sheet = Cover(Sheet());
            var far = new MeshBuilder();
            far.Grid(1f, -0.01f, 0.01f, -0.01f, 0.01f, 0.005f, MeshBuilder.On(1));
            Cover(far);

            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.Zero, "behind the speck, the sheet covers nothing");

            sheet.Setback = 0.001f;
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.EqualTo(speck.TriangleCount), "within its setback, it covers a speck that names every mesh");
            candidate.CoveredBy = Naming(speck, 1);
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.EqualTo(speck.TriangleCount), "and one that names it");
            candidate.CoveredBy = Naming(speck, 2);
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.Zero, "but not one that names another mesh");

            candidate.CoveredBy = null;
            sheet.Setback = 0.0003f;
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.Zero, "nor from past its setback");
        }

        // The first solve names what covered each vertex, and each large triangle's inside, so a re-judge can ask for it.
        [Test]
        public void TheResult_NamesTheMeshesThatCoveredEachSample()
        {
            var inner = new MeshBuilder();
            inner.Sphere(float3.zero, 0.1f, 12, 24, MeshBuilder.On(1));
            scene.Add(inner, occludes: false);
            var upper = new MeshBuilder();
            upper.SphereBand(float3.zero, 0.105f, 50f, 115f, 24, 48, MeshBuilder.On(1));
            scene.Add(upper, occludes: true);
            var lower = new MeshBuilder();
            lower.SphereBand(float3.zero, 0.105f, 110f, 180f, 24, 48, MeshBuilder.On(1));
            scene.Add(lower, occludes: true);

            var r = scene.Solve(OcclusionSettings.Default, Bones);

            Assert.That(scene.Stats.InteriorSamples, Is.GreaterThan(0), "some triangle is sampled inside");
            const ulong both = 0b110;
            var sawUpper = false;
            var sawLower = false;
            for (var t = 0; t < inner.TriangleCount; t++)
            {
                if (!r[0].HiddenTriangles[t]) continue;
                var tri = inner.Triangle(t);
                for (var c = 0; c < 3; c++)
                {
                    var by = r[0].CoveredBy[2 * tri[c]];
                    Assert.That(by, Is.Not.Zero, $"triangle {t} corner {c} was covered by something");
                    Assert.That(by & ~both, Is.Zero, $"triangle {t} corner {c} names only the bands");
                    var polar = math.degrees(math.acos(math.clamp(math.normalize(inner.Position(tri[c])).y, -1f, 1f)));
                    if (polar > 165f) Assert.That(by, Is.EqualTo(0b100UL), $"triangle {t} corner {c}, under the lower band alone");
                    if (polar > 75f && polar < 90f) Assert.That(by, Is.EqualTo(0b010UL), $"triangle {t} corner {c}, under the upper band alone");
                    sawUpper |= by == 0b010UL;
                    sawLower |= by == 0b100UL;
                }
                Assert.That(r[0].InteriorCoveredBy[2 * t] & ~both, Is.Zero, $"triangle {t} inside names only the bands");
            }
            Assert.That(sawUpper && sawLower, "each band alone covered some vertex");
            Assert.That(Enumerable.Range(0, inner.TriangleCount).Any(t => r[0].HiddenTriangles[t] && r[0].InteriorCoveredBy[2 * t] != 0), "some hidden triangle's inside names its cover");
            Assert.That(Enumerable.Range(0, r[1].CoveredBy.Length).All(v => r[1].CoveredBy[v] == 0), "nothing covers the upper band");
        }

        // A setback of zero, or names of no mesh, leave every ray where the rule starts it: the solve is the one without, to
        // the verdict and the ray.
        [TestCase(0f, ulong.MaxValue)]
        [TestCase(0.002f, 0UL)]
        public void NoSetbackOrNoName_DecidesAsWithout(float setback, ulong names)
        {
            var inner = new MeshBuilder();
            inner.Sphere(float3.zero, 0.1f, 16, 32, MeshBuilder.On(1));
            scene.Add(inner, occludes: true);
            var bowl = new MeshBuilder();
            bowl.SphereBand(float3.zero, 0.105f, 60f, 180f, 24, 48, MeshBuilder.On(1));
            var swell = bowl.AddFrame();
            for (var v = 0; v < bowl.VertexCount; v++) swell.Add(new ShapeDelta { Vertex = v, Delta = 0.002f * math.normalize(bowl.Position(v)) });
            scene.Add(bowl, occludes: true);
            var without = scene.Solve(OcclusionSettings.Default, Bones).Select(x => x.HiddenTriangles.ToArray()).ToArray();
            var stats = scene.Stats;

            foreach (var (m, b) in new[] { (scene.Meshes[0], inner), (scene.Meshes[1], bowl) })
            {
                m.Setback = setback;
                m.CoveredBy = Enumerable.Repeat(names, 2 * b.VertexCount).ToArray();
                m.InteriorCoveredBy = Enumerable.Repeat(names, 2 * b.TriangleCount).ToArray();
            }
            var with = scene.Solve(OcclusionSettings.Default, Bones);

            Assert.That(without[0].Count(h => h), Is.GreaterThan(0));
            for (var m = 0; m < 2; m++) Assert.That(with[m].HiddenTriangles.ToArray(), Is.EqualTo(without[m]), $"mesh {m}");
            Assert.That((scene.Stats.VertexSamples, scene.Stats.InteriorSamples, scene.Stats.Rays, scene.Stats.DeletionPassSamples),
                Is.EqualTo((stats.VertexSamples, stats.InteriorSamples, stats.Rays, stats.DeletionPassSamples)));
        }

        // A cover that sank half a millimetre past the speck is holed off to one side, where the ray 75 degrees off the normal
        // toward +X, run back past the speck, crosses its plane, 2.24 mm away on -X. A viewer at the speck sees the sheet
        // straight behind where it stood, so the cover moved and did not go: the speck stays covered. Holed straight under
        // it instead, the sheet is gone from behind it, and the speck is not covered.
        [Test]
        public void ACoverThatSankPastASample_CoversIt_ThoughItIsOpenOffToOneSide()
        {
            var speck = Speck();
            scene.Add(speck, occludes: false);
            Cover(Sheet((x, z) => x > -0.0025f && x < -0.002f && math.abs(z) < 0.0005f)).Setback = 0.001f;

            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.EqualTo(speck.TriangleCount), "open to one side, the sheet under the speck covers it");

            scene.Dispose();
            scene = new Scene();
            scene.Add(speck, occludes: false);
            Cover(Sheet((x, z) => math.abs(x) < 0.0005f && math.abs(z) < 0.0005f)).Setback = 0.001f;

            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.Zero, "open straight under it, it does not");
        }

        // Covered at rest from behind, through a hole in the sheet straight under the speck, by a tile a fifth of a millimetre
        // across under the hole, which a configuration then sinks past the setback. The tile's bounds lie behind the speck,
        // outside the cone a ray leaves it by and too small to hold it, so only a risk test that knows a sample looks behind
        // itself re-casts it in the configuration, and only a full re-cast finds it uncovered: no ray of its cone was blocked.
        [Test]
        public void ACoverAConfigurationSinksPastTheSetback_Uncovers_ASampleItCoveredFromBehind()
        {
            var speck = Speck();
            scene.Add(speck, occludes: false);
            bool Hole(float x, float z) => math.abs(x) < 0.0005f && math.abs(z) < 0.0005f;
            MeshBuilder Tile()
            {
                var b = new MeshBuilder();
                b.Grid(-Sunk - 0.00005f, -0.0001f, 0.0001f, -0.0001f, 0.0001f, 0.0001f, MeshBuilder.On(1));
                return b;
            }
            var sheet = Cover(Sheet(Hole));
            var tileBuilder = Tile();
            var sink = tileBuilder.AddFrame();
            for (var v = 0; v < tileBuilder.VertexCount; v++) sink.Add(new ShapeDelta { Vertex = v, Delta = new float3(0, -0.003f, 0) });
            var tile = Cover(tileBuilder);
            sheet.Setback = tile.Setback = 0.001f;

            var r = scene.Solve(OcclusionSettings.Default, Bones);

            Assert.That(r[0].HiddenCount, Is.Zero, "sunk past the setback in the configuration, the tile no longer covers the speck");
            Assert.That(r[0].Declined.ExposedByFrame, Is.GreaterThan(0), "which it covered at rest");

            scene.Dispose();
            scene = new Scene();
            scene.Add(speck, occludes: false);
            Cover(Sheet(Hole)).Setback = 0.001f;
            Cover(Tile()).Setback = 0.001f;
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.EqualTo(speck.TriangleCount), "at rest, the tile covers the speck through the hole");
        }

        // A speck drawn on both sides, under a sheet 2 mm over it and over one 2 mm under it, both drawn on both sides: each
        // side is covered by the sheet it faces, and names that sheet alone. Judged again with the sheet under it gone and
        // the one over it free to cover from 3 mm behind, the speck's back is uncovered: the sheet over it stands behind the
        // back's samples, but it never covered them.
        [Test]
        public void EachSideOfATwoSidedTriangle_NamesItsOwnCover()
        {
            var speck = Speck();
            var both = new OccluderSubmesh { Culling = FaceCulling.Off };
            var candidate = scene.Add(speck, occludes: false, both);
            var over = new MeshBuilder();
            over.Grid(0.002f, -0.01f, 0.01f, -0.01f, 0.01f, 0.0005f, MeshBuilder.On(1));
            var above = Cover(over);
            above.Submeshes = new[] { both };
            var under = new MeshBuilder();
            under.Grid(-0.002f, -0.01f, 0.01f, -0.01f, 0.01f, 0.0005f, MeshBuilder.On(1));
            var below = Cover(under);
            below.Submeshes = new[] { both };

            var first = scene.Solve(OcclusionSettings.Default, Bones);
            Assert.That(first[0].HiddenCount, Is.EqualTo(speck.TriangleCount), "each side is covered by the sheet it faces");
            var names = first[0].CoveredBy.ToArray();
            for (var v = 0; v < speck.VertexCount; v++)
            {
                Assert.That(names[2 * v], Is.EqualTo(1UL << 1), $"vertex {v}'s front names the sheet over it");
                Assert.That(names[2 * v + 1], Is.EqualTo(1UL << 2), $"vertex {v}'s back names the sheet under it");
            }

            below.Blocks = new bool[under.TriangleCount];
            above.Setback = 0.003f;
            candidate.CoveredBy = names;
            candidate.InteriorCoveredBy = first[0].InteriorCoveredBy.ToArray();
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.Zero, "the back's cover is gone, and the front's does not stand in for it");
            candidate.CoveredBy = Enumerable.Repeat((1UL << 1) | (1UL << 2), names.Length).ToArray();
            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.EqualTo(speck.TriangleCount), "as it would were each side named for both");
        }

        // Bit 63 names every mesh from index 63 on, which it cannot tell apart, so none of them has a setback: a sheet sunk
        // under the speck at index 64 covers nothing from behind, though the speck names bit 63.
        [Test]
        public void AMeshFromIndex63On_HasNoSetback()
        {
            var speck = Speck();
            var candidate = scene.Add(speck, occludes: false);
            for (var m = 1; m < 64; m++)
            {
                var far = new MeshBuilder();
                far.Grid(1f + m, -0.001f, 0.001f, -0.001f, 0.001f, 0.001f, MeshBuilder.On(1));
                Cover(far);
            }
            Cover(Sheet()).Setback = 0.001f;
            Assert.That(scene.Meshes.Count, Is.EqualTo(65));
            candidate.CoveredBy = Naming(speck, 63);

            Assert.That(scene.Solve(OcclusionSettings.Default, Bones)[0].HiddenCount, Is.Zero);
        }

        [Test]
        public void ASetbackOrNamesOfTheWrongShape_AreRefusedByName()
        {
            var speck = Speck();
            var m = scene.Add(speck, occludes: true);
            m.Setback = -0.001f;
            StringAssert.Contains("Setback", Assert.Throws<ArgumentException>(() => scene.Solve(OcclusionSettings.Default, Bones)).Message);
            m.Setback = 0f;
            m.CoveredBy = new ulong[2 * speck.VertexCount + 1];
            StringAssert.Contains("CoveredBy", Assert.Throws<ArgumentException>(() => scene.Solve(OcclusionSettings.Default, Bones)).Message);
            m.CoveredBy = null;
            m.InteriorCoveredBy = new ulong[2 * speck.TriangleCount + 1];
            StringAssert.Contains("InteriorCoveredBy", Assert.Throws<ArgumentException>(() => scene.Solve(OcclusionSettings.Default, Bones)).Message);
        }
    }
}
