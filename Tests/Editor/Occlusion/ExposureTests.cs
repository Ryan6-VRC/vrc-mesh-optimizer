using System;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // Ways to make the solver hide a triangle a viewer can see, each with the control that decides the other
    // way, and each scene also handed to CoverChecker.
    public class ExposureTests
    {
        static readonly int[] Bones = { -1, 0 };
        static readonly float3 Up = new float3(0, 1, 0);

        Scene scene;

        [SetUp] public void SetUp() => scene = new Scene();
        [TearDown] public void TearDown() => scene.Dispose();

        OcclusionResult[] SolveAndCheck(string context, OcclusionSettings? settings = null)
        {
            var s = settings ?? OcclusionSettings.Default;
            var r = scene.Solve(s, Bones);
            var report = CoverChecker.Check(scene.Meshes, Bones, null, s, r);
            Assert.That(report.Ok, Is.True, $"{context}\na hidden triangle is not covered by kept faces:\n{report}");
            return r;
        }

        static MeshBuilder Plate(float y, float half)
        {
            var b = new MeshBuilder();
            b.Grid(y, -half, half, -half, half, 0.01f, MeshBuilder.On(1));
            return b;
        }

        // ------------------------------------------------------------------------------------------------
        // A material that renders a face's back shows that side to a viewer behind it. A plate under a cover
        // that hides only its front is still seen from below when it is double-sided.

        [Test]
        public void DoubleSidedCandidate_IsOwedCoverOnItsBackToo([Values(FaceCulling.Back, FaceCulling.Off, FaceCulling.Front)] FaceCulling culling)
        {
            var plate = Plate(0f, 0.1f);
            scene.Add(plate, occludes: false, new OccluderSubmesh { Culling = culling });
            scene.Add(Plate(0.01f, 0.2f), occludes: true);
            var r = SolveAndCheck($"plate culling {culling}");
            if (culling == FaceCulling.Back)
                Assert.That(r[0].HiddenCount, Is.EqualTo(plate.TriangleCount), "control: back-culled, only its covered front renders");
            else
            {
                Assert.That(r[0].HiddenCount, Is.Zero, "its back renders, and nothing is under it");
                Assert.That(r[0].Declined.Escaped, Is.EqualTo(plate.ReferencedVertexCount), "every vertex's back cone escapes downward");
            }
        }

        [Test]
        public void DoubleSidedCandidate_CoveredOnBothSides_IsHidden()
        {
            var plate = Plate(0f, 0.1f);
            scene.Add(plate, occludes: false, new OccluderSubmesh { Culling = FaceCulling.Off });
            scene.Add(Plate(0.01f, 0.2f), occludes: true);
            scene.Add(Plate(-0.01f, 0.2f), occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck("plate between two covers");
            Assert.That(r[0].HiddenCount, Is.EqualTo(plate.TriangleCount), "a cover on each side hides both");
        }

        // ------------------------------------------------------------------------------------------------
        // A ray starts OriginOffset along the normal, so it reaches Reach + OriginOffset from its sample. A face
        // that blocked a ray at rest just inside that reach, and that a frame moves away, must still put the
        // sample at risk in that frame.

        [Test]
        public void FrameMovingABlockerAtTheEdgeOfReach_ReCastsWhatItCovered([Values(1.00005f, 0.9999f)] float plugHeight)
        {
            // A 1 mm triangle at the centre of a closed dome with a 5-degree hole over it, so each corner's
            // straight-up ray alone leaves through the hole. Over each corner, just past a metre up, a tiny plug
            // that its own frame slides away. At 1.00005 m the plug is beyond Reach from the corner but within
            // it from the ray's origin.
            var skin = new MeshBuilder();
            var corners = new[] { new float3(0, 0, 0), new float3(0.001f, 0, 0), new float3(0, 0, 0.001f) };
            foreach (var c in corners) skin.AddVertex(c, Up, float2.zero, MeshBuilder.On(1));
            skin.AddTriangle(0, 2, 1);
            scene.Add(skin, occludes: false);

            var dome = new Shell { Radius = 0.5f, Rings = 36, Segments = 72, HoleDegrees = 4f };
            dome.AddTo(scene);

            var plugs = new MeshBuilder();
            const float size = 1e-5f;
            for (var i = 0; i < 3; i++)
            {
                var c = corners[i] + new float3(0, plugHeight, 0);
                var a = plugs.AddVertex(c + new float3(-size, 0, -size), -Up, float2.zero, MeshBuilder.On(1));
                var b = plugs.AddVertex(c + new float3(size, 0, -size), -Up, float2.zero, MeshBuilder.On(1));
                var d = plugs.AddVertex(c + new float3(0, 0, 2 * size), -Up, float2.zero, MeshBuilder.On(1));
                plugs.AddTriangle(a, b, d);
            }
            for (var i = 0; i < 3; i++)
            {
                var f = plugs.AddFrame();
                for (var k = 0; k < 3; k++) f.Add(new ShapeDelta { Vertex = 3 * i + k, Delta = new float3(0.5f, 0, 0) });
            }
            scene.Add(plugs, occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });

            var r = SolveAndCheck($"plug at {plugHeight}");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "in each plug's frame its corner's straight-up ray leaves through the hole");
            Assert.That(r[0].Declined.ExposedByFrame, Is.EqualTo(3), "each corner is covered at rest and exposed by its plug's frame");
        }

        [Test]
        public void FrameMovingABlockerAtTheEdgeOfReach_Control_RestCoverIsReal()
        {
            // The same scene with no frames: the plugs at 1.00005 m do cover the corners at rest.
            var skin = new MeshBuilder();
            var corners = new[] { new float3(0, 0, 0), new float3(0.001f, 0, 0), new float3(0, 0, 0.001f) };
            foreach (var c in corners) skin.AddVertex(c, Up, float2.zero, MeshBuilder.On(1));
            skin.AddTriangle(0, 2, 1);
            scene.Add(skin, occludes: false);
            new Shell { Radius = 0.5f, Rings = 36, Segments = 72, HoleDegrees = 4f }.AddTo(scene);
            var plugs = new MeshBuilder();
            const float size = 1e-5f;
            for (var i = 0; i < 3; i++)
            {
                var c = corners[i] + new float3(0, 1.00005f, 0);
                var a = plugs.AddVertex(c + new float3(-size, 0, -size), -Up, float2.zero, MeshBuilder.On(1));
                var b = plugs.AddVertex(c + new float3(size, 0, -size), -Up, float2.zero, MeshBuilder.On(1));
                var d = plugs.AddVertex(c + new float3(0, 0, 2 * size), -Up, float2.zero, MeshBuilder.On(1));
                plugs.AddTriangle(a, b, d);
            }
            scene.Add(plugs, occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck("plugs without frames");
            Assert.That(r[0].HiddenTriangles[0], Is.True, "the plugs close the hole within reach of the rays' origins");

            scene.Dispose();
            scene = new Scene();
            skin = new MeshBuilder();
            foreach (var c in corners) skin.AddVertex(c, Up, float2.zero, MeshBuilder.On(1));
            skin.AddTriangle(0, 2, 1);
            scene.Add(skin, occludes: false);
            new Shell { Radius = 0.5f, Rings = 36, Segments = 72, HoleDegrees = 4f }.AddTo(scene);
            r = SolveAndCheck("no plugs");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "control: without the plugs the hole is open");
        }

        // ------------------------------------------------------------------------------------------------
        // Edges. A ray that meets a closed cover exactly on an edge or a vertex it shares must be blocked; a ray
        // that passes through a gap between two faces must not be, however narrow the gap.

        // A fan of eight triangles about (cx, y, 0) out to 20 cm, with spokes on the axes, covering x <= cx
        // (left), x >= cx (right) or all round. Double-sided, so winding does not enter.
        static void Fan(MeshBuilder b, float cx, float y, int firstSpoke, int spokes)
        {
            var dirs = new[]
            {
                new float2(1, 0), new float2(0.70710678f, 0.70710678f), new float2(0, 1), new float2(-0.70710678f, 0.70710678f),
                new float2(-1, 0), new float2(-0.70710678f, -0.70710678f), new float2(0, -1), new float2(0.70710678f, -0.70710678f),
            };
            var centre = b.AddVertex(new float3(cx, y, 0), -Up, float2.zero, MeshBuilder.On(1));
            var ring = new int[spokes + 1];
            for (var k = 0; k <= spokes; k++)
            {
                var d = dirs[(firstSpoke + k) % 8] * 0.2f;
                ring[k] = b.AddVertex(new float3(cx + d.x, y, d.y), -Up, float2.zero, MeshBuilder.On(1));
            }
            for (var k = 0; k < spokes; k++) b.AddTriangle(centre, ring[k], ring[k + 1]);
        }

        // Turns a scene about +Y, so an edge that ran along an axis runs across its faces' bounds.
        static void Turn(MeshBuilder b, float degrees)
        {
            if (degrees != 0f) b.Rotate(quaternion.RotateY(math.radians(degrees)), float3.zero);
        }

        // A 1 mm triangle whose corners stand under the fan's centre vertex, its +X spoke and its +Z spoke.
        static MeshBuilder Skin()
        {
            var skin = new MeshBuilder();
            skin.AddVertex(float3.zero, Up, float2.zero, MeshBuilder.On(1));
            skin.AddVertex(new float3(0.001f, 0, 0), Up, float2.zero, MeshBuilder.On(1));
            skin.AddVertex(new float3(0, 0, 0.001f), Up, float2.zero, MeshBuilder.On(1));
            skin.AddTriangle(0, 2, 1);
            return skin;
        }

        [Test]
        public void RayThroughASharedVertexOrEdge_IsBlocked([Values(0f, 30f, 45f)] float turn)
        {
            var skin = Skin();
            Turn(skin, turn);
            scene.Add(skin, occludes: false);
            var fan = new MeshBuilder();
            Fan(fan, 0f, 0.01f, 0, 8);
            Turn(fan, turn);
            scene.Add(fan, occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck("closed fan");
            Assert.That(r[0].HiddenTriangles[0], Is.True, "each corner's straight-up ray meets the fan on a vertex or an edge two faces share");
        }

        [Test]
        public void RayOnACoincidentUnweldedSeam_IsBlocked([Values(0f, 30f, 45f)] float turn)
        {
            // Two half fans with their own vertices at exactly the same places along x = 0: a UV seam.
            var skin = Skin();
            Turn(skin, turn);
            scene.Add(skin, occludes: false);
            var fan = new MeshBuilder();
            Fan(fan, 0f, 0.01f, 2, 4);
            Fan(fan, 0f, 0.01f, 6, 4);
            Turn(fan, turn);
            scene.Add(fan, occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck("seamed fan");
            Assert.That(r[0].HiddenTriangles[0], Is.True, "the rays on the seam meet one side or the other");
        }

        [Test]
        public void RayThroughAGapBetweenTwoFaces_Escapes([Values(0f, 30f, 45f)] float turn)
        {
            // The two half fans pulled 0.1 micrometre apart each way: the corners on x = 0 look straight up
            // through a gap 0.2 micrometres wide. No renderer shows that gap; the test fixes which way the
            // intersection errs, and it must err toward the ray escaping. Turned, the gap runs across the
            // halves' bounds, so the bounds cannot reject the ray first.
            var skin = Skin();
            Turn(skin, turn);
            scene.Add(skin, occludes: false);
            var fan = new MeshBuilder();
            Fan(fan, -1e-7f, 0.01f, 2, 4);
            Fan(fan, 1e-7f, 0.01f, 6, 4);
            Turn(fan, turn);
            scene.Add(fan, occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck("gapped fan");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "a ray between two faces that do not meet is not blocked");
            Assert.That(r[0].Declined.Escaped, Is.EqualTo(2), "the corners on x = 0 escape; the one at x = 1 mm is under a face");
        }

        // ------------------------------------------------------------------------------------------------
        // A surface is seen from the side its geometry faces, whatever its shading normals say; toon meshes
        // often carry authored normals tilted well off the surface. coverage.py casts about Blender's vertex
        // normal, which is the geometry's and ignores custom normals.

        // A closed double-sided box about the origin, 1 cm over and under it and 3 cm to each side, with a
        // window in its top 1 cm across centred 1.7 cm toward -X: where the 60-degree ring of an upright cone
        // at the origin leaves.
        static MeshBuilder BoxWithWindow(bool window)
        {
            var b = new MeshBuilder();
            Func<float, float, bool> skip = null;
            if (window) skip = (x, z) => math.abs(x + 0.0171f) < 0.005f && math.abs(z) < 0.005f;
            b.Grid(0.01f, -0.03f, 0.03f, -0.03f, 0.03f, 0.002f, MeshBuilder.On(1), skip);
            b.Grid(-0.01f, -0.03f, 0.03f, -0.03f, 0.03f, 0.01f, MeshBuilder.On(1));
            void Wall(float3 a, float3 c)
            {
                // A vertical quad from corner a to corner c, spanning y from -1 cm to 1 cm.
                var i = b.AddVertex(new float3(a.x, -0.01f, a.z), Up, float2.zero, MeshBuilder.On(1));
                b.AddVertex(new float3(a.x, 0.01f, a.z), Up, float2.zero, MeshBuilder.On(1));
                b.AddVertex(new float3(c.x, -0.01f, c.z), Up, float2.zero, MeshBuilder.On(1));
                b.AddVertex(new float3(c.x, 0.01f, c.z), Up, float2.zero, MeshBuilder.On(1));
                b.AddTriangle(i, i + 1, i + 2);
                b.AddTriangle(i + 2, i + 1, i + 3);
            }
            Wall(new float3(-0.03f, 0, -0.03f), new float3(-0.03f, 0, 0.03f));
            Wall(new float3(0.03f, 0, -0.03f), new float3(0.03f, 0, 0.03f));
            Wall(new float3(-0.03f, 0, -0.03f), new float3(0.03f, 0, -0.03f));
            Wall(new float3(-0.03f, 0, 0.03f), new float3(0.03f, 0, 0.03f));
            return b;
        }

        [Test]
        public void TiltedAuthoredNormals_DoNotHideASurfaceSeenThroughAWindow([Values(0f, 30f)] float tilt)
        {
            // The 1 mm skin faces +Y, but its authored normals lean 30 degrees toward +X, so a cone about them
            // reaches no further than 45 degrees toward -X: short of the window, through which a viewer 60
            // degrees off the skin's face sees it.
            MeshBuilder Tilted()
            {
                math.sincos(math.radians(tilt), out var s, out var c);
                var n = new float3(s, c, 0);
                var skin = new MeshBuilder();
                skin.AddVertex(float3.zero, n, float2.zero, MeshBuilder.On(1));
                skin.AddVertex(new float3(0.001f, 0, 0), n, float2.zero, MeshBuilder.On(1));
                skin.AddVertex(new float3(0, 0, 0.001f), n, float2.zero, MeshBuilder.On(1));
                skin.AddTriangle(0, 2, 1);
                return skin;
            }

            scene.Add(Tilted(), occludes: false);
            scene.Add(BoxWithWindow(window: true), occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck($"tilt {tilt}, window");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "a viewer through the window sees the skin's face");
            scene.Dispose();

            scene = new Scene();
            scene.Add(Tilted(), occludes: false);
            scene.Add(BoxWithWindow(window: false), occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            r = SolveAndCheck($"tilt {tilt}, closed");
            Assert.That(r[0].HiddenTriangles[0], Is.True, "control: the closed box hides the skin");
        }

        // ------------------------------------------------------------------------------------------------
        // Alpha wrap modes. The shell's UV v runs 0.5 to 1 from bottom to top; scaled by 2.5 and offset, it
        // crosses several tiles, below 0 or past 1, so each wrap mode reaches different texels. The checker
        // samples the map by its own reading of each mode, so the two must agree on every hidden triangle.

        [Test]
        public void AlphaWrapModes_HideOnlyUnderOpaqueTexels([Values] AlphaWrap wrap, [Values(-2.9f, -1.6f, -0.9f)] float offset)
        {
            const int w = 8, h = 20;
            var alpha = new byte[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    alpha[y * w + x] = y >= 16 ? (byte)0 : (byte)255;
            var map = new AlphaMap(w, h, alpha, 0.5f) { ScaleOffset = new float4(1, 2.5f, 0, offset), WrapU = wrap, WrapV = wrap };
            var inner = new MeshBuilder();
            inner.Sphere(float3.zero, 0.1f, 16, 32, MeshBuilder.On(1));
            var outer = new MeshBuilder();
            outer.Sphere(float3.zero, 0.105f, 24, 48, MeshBuilder.On(1));
            scene.Add(inner, occludes: true);
            scene.Add(outer, occludes: true, new OccluderSubmesh { Alpha = map });
            var r = SolveAndCheck($"wrap {wrap}, offset {offset}");
            TestContext.WriteLine($"[wrap] {wrap} {offset}: hidden {r[0].HiddenCount}/{inner.TriangleCount}");
            Assert.That(r[0].HiddenCount, Is.GreaterThan(0), "the checker needs hidden triangles to check");
        }

        // ------------------------------------------------------------------------------------------------
        // The cone stops at ConeDegrees. A surface can be seen from outside it, at a grazing angle, and the rule
        // accepts that: coverage.py reports such triangles but never keeps one for it.

        [Test]
        public void GrazingView_PastTheCone_DoesNotKeepATriangle()
        {
            // A disc 1 cm over the skin, 4.5 cm across its radius: every ray within 75 degrees meets it, while a
            // ray at 80 degrees passes its rim (tan 80 * 1 cm = 5.7 cm).
            MeshBuilder Disc(float radius)
            {
                var b = new MeshBuilder();
                var c = b.AddVertex(new float3(0, 0.01f, 0), -Up, float2.zero, MeshBuilder.On(1));
                const int n = 32;
                for (var k = 0; k < n; k++)
                {
                    math.sincos(2f * math.PI * k / n, out var s, out var co);
                    b.AddVertex(new float3(radius * co, 0.01f, radius * s), -Up, float2.zero, MeshBuilder.On(1));
                }
                for (var k = 0; k < n; k++) b.AddTriangle(c, 1 + k, 1 + (k + 1) % n);
                return b;
            }

            scene.Add(Skin(), occludes: false);
            scene.Add(Disc(0.045f), occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            var r = SolveAndCheck("disc of 4.5 cm");
            Assert.That(r[0].HiddenTriangles[0], Is.True, "the rule's cone is 75 degrees; the view at 80 degrees is its stated cost");
            math.sincos(math.radians(80f), out var s80, out var c80);
            var reachAt80 = 0.01f / c80 * s80;
            Assert.That(reachAt80, Is.GreaterThan(0.045f + 0.0015f), "an 80-degree ray from any corner clears the disc's rim");
            scene.Dispose();

            scene = new Scene();
            scene.Add(Skin(), occludes: false);
            scene.Add(Disc(0.03f), occludes: true, new OccluderSubmesh { Culling = FaceCulling.Off });
            r = SolveAndCheck("disc of 3 cm");
            Assert.That(r[0].HiddenTriangles[0], Is.False, "control: a 75-degree ray clears a 3 cm disc");
        }
    }
}
