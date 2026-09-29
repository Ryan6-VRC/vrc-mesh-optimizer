using System;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // Each test builds a body under a cover and states the verdict the shared-weights rule owes it, with a
    // control beside it: the same geometry with the one property under test changed, deciding the other
    // way. A test whose control would decide the same way proves nothing about that property.
    public class OcclusionRuleTests
    {
        const float Inner = 0.1f;
        const float Outer = 0.105f;
        static readonly float3 Centre = float3.zero;

        // Bone 0 is the root. 1 rides it. 2 -> 3 -> 4 is a chain off the root, four steps from 1.
        static readonly int[] Bones = { -1, 0, 0, 2, 3 };

        Scene scene;

        [SetUp] public void SetUp() => scene = new Scene();
        [TearDown] public void TearDown() => scene.Dispose();

        static MeshBuilder InnerSphere(int bone = 1)
        {
            var b = new MeshBuilder();
            b.Sphere(Centre, Inner, 16, 32, MeshBuilder.On(bone));
            return b;
        }

        static MeshBuilder OuterSphere(BoneInfluence[] influences, bool inward = false)
        {
            var b = new MeshBuilder();
            b.Sphere(Centre, Outer, 24, 48, influences, inward);
            return b;
        }

        static float Polar(float3 p) => math.degrees(math.acos(math.clamp(math.normalize(p - Centre).y, -1f, 1f)));

        // Triangles of `b` whose every corner lies within the polar range, in degrees from +Y.
        static int[] TrianglesWithin(MeshBuilder b, float lo, float hi)
        {
            var list = new System.Collections.Generic.List<int>();
            for (var t = 0; t < b.TriangleCount; t++)
            {
                var tri = b.Triangle(t);
                var ok = true;
                for (var c = 0; c < 3; c++)
                {
                    var p = Polar(b.Position(tri[c]));
                    ok &= p >= lo && p <= hi;
                }
                if (ok) list.Add(t);
            }
            Assert.That(list, Is.Not.Empty, $"the fixture has no triangle within {lo}..{hi} degrees");
            return list.ToArray();
        }

        static void AssertAll(OcclusionResult r, int[] tris, bool hidden, string why)
        {
            foreach (var t in tris) Assert.That(r.HiddenTriangles[t], Is.EqualTo(hidden), $"triangle {t}: {why}");
        }

        static OcclusionSettings Defaults => OcclusionSettings.Default;

        [Test]
        public void SphereUnderShellOnSameBone_IsHidden()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            var outer = OuterSphere(MeshBuilder.On(1));
            scene.Add(outer, occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.EqualTo(inner.TriangleCount), "every inner triangle rides the shell's bone inside a closed shell");
            Assert.That(r[0].Declined.Total, Is.Zero);
            Assert.That(r[1].HiddenCount, Is.Zero, "nothing covers the shell");
            Assert.That(r[1].Declined.Escaped, Is.EqualTo(outer.ReferencedVertexCount));
        }

        [Test]
        public void SphereUnderShellOnUnrelatedBone_StaysAndDeclinesAsSwings()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(4)), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero, "a shell four bone steps away swings away in some pose");
            Assert.That(r[0].Declined.Swings, Is.EqualTo(inner.ReferencedVertexCount));
        }

        static MeshBuilder Cap(int bone, float swingDegrees = 0f)
        {
            var b = new MeshBuilder();
            b.SphereBand(Centre, Outer, 0f, 70f, 12, 48, MeshBuilder.On(bone));
            if (swingDegrees != 0f) b.Rotate(quaternion.RotateZ(math.radians(swingDegrees)), Centre);
            return b;
        }

        [Test]
        public void CapOnUnrelatedBone_KeepsWhatItsSwingExposes()
        {
            // The verdict: a cap on a bone that swings relative to the sphere keeps the triangles under it.
            var inner = InnerSphere(bone: 1);
            scene.Add(inner, occludes: false);
            scene.Add(Cap(bone: 4), occludes: true);
            var top = TrianglesWithin(inner, 0f, 20f);
            AssertAll(scene.Solve(Defaults, Bones)[0], top, false, "the cap swings relative to it");
            scene.Dispose();

            // Why: as plain geometry (the cap put on the sphere's own bone, so every face blocks) the cap
            // covers them at rest, so a rest-pose test would delete them; swung 90 degrees it exposes them.
            scene = new Scene();
            scene.Add(InnerSphere(bone: 1), occludes: false);
            scene.Add(Cap(bone: 1), occludes: true);
            AssertAll(scene.Solve(Defaults, Bones)[0], top, true, "at rest the cap covers it geometrically");
            scene.Dispose();

            scene = new Scene();
            scene.Add(InnerSphere(bone: 1), occludes: false);
            scene.Add(Cap(bone: 1, swingDegrees: 90f), occludes: true);
            AssertAll(scene.Solve(Defaults, Bones)[0], top, false, "the swung cap no longer covers it");
        }

        [Test]
        public void ShellOnParentBone_HidesThroughKin_AndNotWithKinZero()
        {
            var inner = InnerSphere(bone: 1);
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(0)), occludes: true);
            var withKin = scene.Solve(Defaults, Bones);
            Assert.That(withKin[0].HiddenCount, Is.EqualTo(inner.TriangleCount), "the parent is one step away, inside the default two");

            var s = Defaults;
            s.KinSteps = 0;
            var noKin = scene.Solve(s, Bones);
            Assert.That(noKin[0].HiddenCount, Is.Zero, "with no kin allowance the parent is another bone");
            Assert.That(noKin[0].Declined.Swings, Is.EqualTo(inner.ReferencedVertexCount));
        }

        [Test]
        public void FaceWeightShare_DecidesAgainstThreshold()
        {
            // The shell's weight is split between the inner's bone and an unrelated one; only the share on
            // the inner's bone counts toward blocking.
            BoneInfluence[] Split(float related) => related >= 0.5f
                ? new[] { new BoneInfluence { Bone = 1, Weight = related }, new BoneInfluence { Bone = 4, Weight = 1f - related } }
                : new[] { new BoneInfluence { Bone = 4, Weight = 1f - related }, new BoneInfluence { Bone = 1, Weight = related } };

            var inner = InnerSphere();
            scene.Add(inner, occludes: false);
            scene.Add(OuterSphere(Split(0.6f)), occludes: true);
            Assert.That(scene.Solve(Defaults, Bones)[0].HiddenCount, Is.EqualTo(inner.TriangleCount), "share 0.6 reaches 0.5");
            scene.Dispose();

            scene = new Scene();
            scene.Add(InnerSphere(), occludes: false);
            scene.Add(OuterSphere(Split(0.4f)), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero, "share 0.4 falls short of 0.5");
            Assert.That(r[0].Declined.Swings, Is.EqualTo(inner.ReferencedVertexCount));
        }

        [Test]
        public void ShellWithHole_KeepsTrianglesUnderTheHole()
        {
            var inner = InnerSphere();
            var outer = new MeshBuilder();
            outer.SphereBand(Centre, Outer, 30f, 180f, 20, 48, MeshBuilder.On(1));
            scene.Add(inner, occludes: true);
            scene.Add(outer, occludes: true);
            var r = scene.Solve(Defaults, Bones);
            AssertAll(r[0], TrianglesWithin(inner, 0f, 20f), false, "it looks straight out through the hole");
            AssertAll(r[0], TrianglesWithin(inner, 90f, 180f), true, "the shell covers the lower half in every direction of the cone");
            Assert.That(r[0].Declined.Escaped, Is.GreaterThan(0));
        }

        // The sphere's UV v is 1 - polar/360, so the cap within 30 degrees of +Y is v above 1 - 30/360.
        static bool InTopCap(float v) => v > 1f - 30f / 360f;

        static AlphaMap TopCapSeeThrough()
        {
            const int w = 16, h = 36;
            var alpha = new byte[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    alpha[y * w + x] = InTopCap((y + 0.5f) / h) ? (byte)0 : (byte)255;
            return new AlphaMap(w, h, alpha, 0.5f);
        }

        [Test]
        public void SeeThroughAlphaPatch_KeepsTrianglesUnderIt()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: true, new OccluderSubmesh { Alpha = TopCapSeeThrough() });
            var r = scene.Solve(Defaults, Bones);
            AssertAll(r[0], TrianglesWithin(inner, 0f, 20f), false, "the shell is see-through over it");
            AssertAll(r[0], TrianglesWithin(inner, 90f, 180f), true, "the shell is opaque over the lower half");
            Assert.That(r[0].Declined.SeeThrough, Is.GreaterThan(0));
        }

        [Test]
        public void SeeThroughAlphaPatch_OpaqueOverride_HidesEverything()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: true, new OccluderSubmesh { Mode = OccluderMode.Opaque, Alpha = TopCapSeeThrough() });
            Assert.That(scene.Solve(Defaults, Bones)[0].HiddenCount, Is.EqualTo(inner.TriangleCount));
        }

        [Test]
        public void SeeThroughAlphaPatch_NeverOccludes_HidesNothing()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: true, new OccluderSubmesh { Mode = OccluderMode.NeverOccludes, Alpha = TopCapSeeThrough() });
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero);
            Assert.That(r[0].Declined.Escaped, Is.EqualTo(inner.ReferencedVertexCount), "a submesh that never occludes is not met at all");
        }

        [Test]
        public void AlphaAsFloats_DecidesAsBytesDo()
        {
            const int w = 16, h = 36;
            var alpha = new float[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    alpha[y * w + x] = InTopCap((y + 0.5f) / h) ? 0.1f : 0.9f;
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: true, new OccluderSubmesh { Alpha = new AlphaMap(w, h, alpha, 0.5f) });
            var r = scene.Solve(Defaults, Bones);
            AssertAll(r[0], TrianglesWithin(inner, 0f, 20f), false, "alpha 0.1 is under the 0.5 threshold");
            AssertAll(r[0], TrianglesWithin(inner, 90f, 180f), true, "alpha 0.9 is over it");
        }

        static MeshBuilder ShellWithDetachedCap(out int capFirst, out int capEnd)
        {
            // The cap within 35 degrees of +Y has its own vertices, so a frame can move it away and open a hole.
            var b = new MeshBuilder();
            capFirst = b.SphereBand(Centre, Outer, 0f, 35f, 6, 48, MeshBuilder.On(1));
            capEnd = b.VertexCount;
            b.SphereBand(Centre, Outer, 35f, 180f, 20, 48, MeshBuilder.On(1));
            return b;
        }

        [Test]
        public void LiveFrameMovingPartOfTheShellAway_KeepsWhatItExposes()
        {
            var inner = InnerSphere();
            var outer = ShellWithDetachedCap(out var capFirst, out var capEnd);
            scene.Add(inner, occludes: true);
            scene.Add(outer, occludes: true);
            var rest = scene.Solve(Defaults, Bones);
            Assert.That(rest[0].HiddenCount, Is.EqualTo(inner.TriangleCount), "control: at rest the split shell is closed");
            scene.Dispose();

            scene = new Scene();
            outer = ShellWithDetachedCap(out capFirst, out capEnd);
            var frame = outer.AddFrame();
            for (var v = capFirst; v < capEnd; v++) frame.Add(new ShapeDelta { Vertex = v, Delta = new float3(0.5f, 0f, 0f) });
            scene.Add(InnerSphere(), occludes: true);
            scene.Add(outer, occludes: true);
            var r = scene.Solve(Defaults, Bones);
            AssertAll(r[0], TrianglesWithin(inner, 0f, 20f), false, "the frame moves their cover half a metre away");
            AssertAll(r[0], TrianglesWithin(inner, 90f, 180f), true, "the frame does not touch their cover");
            Assert.That(r[0].Declined.ExposedByFrame, Is.GreaterThan(0));
            Assert.That(r[0].Declined.ExposedByFrame, Is.EqualTo(r[0].Declined.Total), "every decline is the frame's; the rest pose covers all");
        }

        [Test]
        public void LiveFrameOnTheCoveredMesh_PushingItThroughTheShell_KeepsWhatPokesOut()
        {
            var inner = InnerSphere();
            var frame = inner.AddFrame();
            for (var v = 0; v < inner.VertexCount; v++)
            {
                var p = inner.Position(v);
                if (Polar(p) < 30f) frame.Add(new ShapeDelta { Vertex = v, Delta = math.normalize(p) * 0.1f });
            }
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            AssertAll(r[0], TrianglesWithin(inner, 0f, 25f), false, "the frame carries them outside the shell");
            AssertAll(r[0], TrianglesWithin(inner, 90f, 180f), true, "the frame does not move them");
            Assert.That(r[0].Declined.ExposedByFrame, Is.GreaterThan(0));
        }

        [Test]
        public void NonOccluderShell_CoversNothing()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: false);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero, "a renderer that can be toggled off covers nothing");
            Assert.That(r[0].Declined.Escaped, Is.EqualTo(inner.ReferencedVertexCount));
        }

        [Test]
        public void Renderer_DoesNotHideItsOwnTriangles()
        {
            // The sphere-in-shell of the first test, as one renderer.
            var both = InnerSphere();
            var innerTris = both.TriangleCount;
            both.Sphere(Centre, Outer, 24, 48, MeshBuilder.On(1));
            scene.Add(both, occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero);
            Assert.That(innerTris, Is.GreaterThan(0));
        }

        [Test]
        public void InwardFacingShell_BackCulled_DoesNotCover_ButDoesWithCullingOff()
        {
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1), inward: true), occludes: true);
            var culled = scene.Solve(Defaults, Bones);
            Assert.That(culled[0].HiddenCount, Is.Zero, "seen from outside, an inward-facing back-culled shell does not render");
            Assert.That(culled[0].Declined.SeeThrough, Is.EqualTo(inner.ReferencedVertexCount));
            scene.Dispose();

            scene = new Scene();
            scene.Add(InnerSphere(), occludes: true);
            scene.Add(OuterSphere(MeshBuilder.On(1), inward: true), occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            Assert.That(scene.Solve(Defaults, Bones)[0].HiddenCount, Is.EqualTo(inner.TriangleCount), "double-sided, it renders from outside");
        }

        [Test]
        public void CoincidentShell_DoesNotCover()
        {
            // A copy of the surface on the surface z-fights in game; it starts behind every ray's origin.
            var inner = InnerSphere();
            scene.Add(inner, occludes: true);
            var copy = new MeshBuilder();
            copy.Sphere(Centre, Inner, 16, 32, MeshBuilder.On(1));
            scene.Add(copy, occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero);
            Assert.That(r[0].Declined.Escaped, Is.EqualTo(inner.ReferencedVertexCount));
        }

        // A skin triangle 20 cm across (times scale) facing +Y, under a cover grid 1 cm above it.
        static MeshBuilder SkinTriangle(float scale = 1f)
        {
            var b = new MeshBuilder();
            var up = new float3(0, 1, 0);
            var a = b.AddVertex(scale * new float3(-0.1f, 0, -0.06f), up, float2.zero, MeshBuilder.On(1));
            var c = b.AddVertex(scale * new float3(0f, 0, 0.12f), up, float2.zero, MeshBuilder.On(1));
            var d = b.AddVertex(scale * new float3(0.1f, 0, -0.06f), up, float2.zero, MeshBuilder.On(1));
            b.AddTriangle(a, c, d);
            return b;
        }

        static MeshBuilder Cover(float zMax, float hole)
        {
            var b = new MeshBuilder();
            b.Grid(0.01f, -0.505f, 0.505f, -0.505f, zMax, 0.01f, MeshBuilder.On(1),
                   (x, z) => math.abs(x) < hole && math.abs(z) < hole);
            return b;
        }

        [Test]
        public void LargeTriangle_WithHoleOverItsMiddle_Stays_ThoughItsCornersAreCovered()
        {
            // A 3 cm hole over the centroid, beyond every corner's 75-degree cone.
            scene.Add(SkinTriangle(), occludes: false);
            scene.Add(Cover(0.505f, 0.015f), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].CoveredVertices.ToArray(), Is.All.True, "the fixture's corners must be covered, or it tests nothing");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "an interior sample under the hole sees out");

            var s = Defaults;
            s.InteriorSpacing = 0f;
            Assert.That(scene.Solve(s, Bones)[0].HiddenTriangles[0], Is.True, "control: from its corners alone it reads as covered");
        }

        [Test]
        public void TriangleTooLargeToSampleAtTheSpacing_Stays()
        {
            // 41 cm needs 21 lattice steps at 2 cm, past the cap: it is kept, never sampled coarser.
            scene.Add(SkinTriangle(2f), occludes: false);
            scene.Add(Cover(0.505f, 0f), occludes: true);
            Assert.That(scene.Solve(Defaults, Bones)[0].HiddenTriangles[0], Is.False);
            var s = Defaults;
            s.InteriorSpacing = 0f;
            Assert.That(scene.Solve(s, Bones)[0].HiddenTriangles[0], Is.True, "control: under a whole cover its corners are covered");
        }

        [Test]
        public void TriangleAFrameStretchesPastItsLattice_Stays()
        {
            // 1.5 cm at rest, so sampled at its corners only; the frame stretches it to 4.6 cm, still under cover.
            var skin = SkinTriangle(0.075f);
            scene.Add(skin, occludes: false);
            scene.Add(Cover(0.505f, 0f), occludes: true);
            Assert.That(scene.Solve(Defaults, Bones)[0].HiddenTriangles[0], Is.True, "control: without the frame it is hidden");
            scene.Dispose();

            scene = new Scene();
            skin = SkinTriangle(0.075f);
            var frame = skin.AddFrame();
            for (var v = 0; v < 3; v++) frame.Add(new ShapeDelta { Vertex = v, Delta = skin.Position(v) * 2f });
            scene.Add(skin, occludes: false);
            scene.Add(Cover(0.505f, 0f), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].CoveredVertices.ToArray(), Is.All.True, "the stretched corners stay under the cover");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "the rest lattice cannot vouch for the stretched interior");
        }

        [Test]
        public void Triangle_WithOneCornerOutsideTheCover_Stays()
        {
            scene.Add(SkinTriangle(), occludes: false);
            scene.Add(Cover(0.055f, 0f), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenTriangles[0], Is.False);
            Assert.That(r[0].Declined.Escaped, Is.EqualTo(1), "only the corner past the cover's edge escapes");
            scene.Dispose();

            scene = new Scene();
            scene.Add(SkinTriangle(), occludes: false);
            scene.Add(Cover(0.505f, 0f), occludes: true);
            Assert.That(scene.Solve(Defaults, Bones)[0].HiddenTriangles[0], Is.True, "control: under a whole cover it is hidden");
        }

        [Test]
        public void HiddenOccluder_DoesNotCoverOnceDeleted()
        {
            // A on bone 1, under B on its child 2, under C on 4, which is two steps from 2 but three from 1.
            // B covers A; C covers B but swings relative to A. Deleting B would leave A behind C alone.
            var bones = new[] { -1, 0, 1, 2, 3 };
            var a = InnerSphere(bone: 1);
            var b = OuterSphere(MeshBuilder.On(2));
            var c = new MeshBuilder();
            c.Sphere(Centre, 0.11f, 24, 48, MeshBuilder.On(4));
            scene.Add(a, occludes: true);
            scene.Add(b, occludes: true);
            scene.Add(c, occludes: true);
            var r = scene.Solve(Defaults, bones);
            Assert.That(r[1].HiddenCount, Is.EqualTo(b.TriangleCount), "C covers B on kin bones");
            Assert.That(r[0].HiddenCount, Is.Zero, "A's only co-moving cover is B, which goes");
            Assert.That(r[0].Declined.ExposedByDeletion, Is.EqualTo(a.ReferencedVertexCount));
            Assert.That(r[0].Declined.Swings, Is.EqualTo(a.ReferencedVertexCount), "with B gone its rays meet only C");
        }

        [Test]
        public void DeletedCover_StillCountsForFramesItMadeIrrelevant()
        {
            // A on bone 1 under L (bone 2, its child), under K (bone 1, a shell whose cap a frame moves away),
            // under M (bone 4: kin of L, not of A). At rest L is A's witness, so pass one skips K's frame for
            // A. L is hidden and deleted; A re-cast finds K. K's frame must then be re-run for A, since with L
            // gone and K's cap away only M, which swings, is over A's top.
            var bones = new[] { -1, 0, 1, 2, 3 };
            var a = InnerSphere(bone: 1);
            var l = OuterSphere(MeshBuilder.On(2));
            var k = new MeshBuilder();
            var capFirst = k.SphereBand(Centre, 0.11f, 0f, 35f, 6, 48, MeshBuilder.On(1));
            var capEnd = k.VertexCount;
            k.SphereBand(Centre, 0.11f, 35f, 180f, 20, 48, MeshBuilder.On(1));
            var frame = k.AddFrame();
            for (var v = capFirst; v < capEnd; v++) frame.Add(new ShapeDelta { Vertex = v, Delta = new float3(0.5f, 0f, 0f) });
            var m = new MeshBuilder();
            m.Sphere(Centre, 0.115f, 24, 48, MeshBuilder.On(4));
            scene.Add(a, occludes: false);
            scene.Add(l, occludes: true);
            scene.Add(k, occludes: true);
            scene.Add(m, occludes: true);
            var r = scene.Solve(Defaults, bones);
            Assert.That(r[1].HiddenCount, Is.EqualTo(l.TriangleCount), "L is covered by K, and by M where K's cap leaves");
            AssertAll(r[0], TrianglesWithin(a, 0f, 20f), false, "in K's frame, with L deleted, nothing co-moving is over it");
            AssertAll(r[0], TrianglesWithin(a, 90f, 180f), true, "K covers the lower half in every configuration");
            Assert.That(r[0].Declined.ExposedByDeletion, Is.GreaterThan(0));
        }

        [Test]
        public void ModularAvatarNaNimationChain_IsKinAtTwoSteps_UnlessMarkedVanishing()
        {
            // A conditional mesh deletion re-skins faces onto bone -> buffer -> NaNimated bone: 1 -> 2 -> 3 here.
            var bones = new[] { -1, 0, 1, 2 };
            var inner = InnerSphere(bone: 1);
            scene.Add(inner, occludes: false);
            scene.Add(OuterSphere(MeshBuilder.On(3)), occludes: true);
            Assert.That(scene.Solve(Defaults, bones)[0].HiddenCount, Is.EqualTo(inner.TriangleCount),
                        "two kin steps reach the NaNimated bone, so the rule alone trusts faces that can vanish");
            var r = scene.Solve(Defaults, bones, vanishes: new[] { false, false, false, true });
            Assert.That(r[0].HiddenCount, Is.Zero, "a face on a vanishing bone never blocks");
            Assert.That(r[0].Declined.Escaped, Is.EqualTo(inner.ReferencedVertexCount));
        }

        [Test]
        public void UnweightedVertices_Decline()
        {
            var inner = new MeshBuilder();
            inner.Sphere(Centre, Inner, 16, 32, Array.Empty<BoneInfluence>());
            scene.Add(inner, occludes: false);
            scene.Add(OuterSphere(MeshBuilder.On(1)), occludes: true);
            var r = scene.Solve(Defaults, Bones);
            Assert.That(r[0].HiddenCount, Is.Zero);
            Assert.That(r[0].Declined.Unweighted, Is.EqualTo(inner.ReferencedVertexCount));
        }

        [Test]
        public void BadInput_IsRefusedByName()
        {
            scene.Add(InnerSphere(bone: 9), occludes: true);
            var e = Assert.Throws<ArgumentException>(() => scene.Solve(Defaults, Bones));
            StringAssert.Contains("bone 9", e.Message);
            var s = Defaults;
            s.ConeDegrees = 120f;
            Assert.Throws<ArgumentException>(() => scene.Solve(s, Bones));
        }
    }
}
