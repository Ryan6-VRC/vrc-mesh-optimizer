using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    // An avatar-sized load: eight limbs, each a body sphere under a garment shell on its bone, about 100k
    // body vertices under about 100k garment triangles, with 20 live frames on the garments. Explicit, so
    // the gate skips it; run it by filter and read the numbers from the test output.
    public class OcclusionBenchmark
    {
        const int Limbs = 8;

        // With a coat, each limb's garment is under a third shell, and the garment's culling is given: a double-sided
        // garment is then covered on its front by the coat and on its back by the far side of the closed body.
        // With linkBodies, each garment frame gets a body frame lifting the same cap by the same amount, and the two carry
        // one configuration id when linked is true, or move alone when it is false.
        static Scene Build(bool bodiesOcclude = false, int bodyFramesPerLimb = 0, FaceCulling? garment = null, bool linkBodies = false, bool linked = true)
        {
            var scene = new Scene();
            var rng = new Random(12345);
            var frames = 0;
            for (var limb = 0; limb < Limbs; limb++)
            {
                var centre = new float3((limb % 4) * 0.35f, (limb / 4) * 0.35f, 0f);
                var body = new MeshBuilder();
                body.Sphere(centre, 0.1f, 88, 141, MeshBuilder.On(limb + 1));
                var shell = new MeshBuilder();
                shell.Sphere(centre, 0.105f, 70, 90, MeshBuilder.On(limb + 1));
                // Two or three frames per garment, each lifting a cap about a random direction by 3 cm.
                for (var f = 0; f < (limb < 4 ? 3 : 2); f++, frames++)
                {
                    var axis = rng.NextFloat3Direction();
                    var id = linkBodies && linked ? frames + 1 : 0;
                    var frame = shell.AddFrame(id);
                    for (var v = 0; v < shell.VertexCount; v++)
                    {
                        var dir = math.normalize(shell.Position(v) - centre);
                        if (math.dot(dir, axis) > 0.8f) frame.Add(new ShapeDelta { Vertex = v, Delta = dir * 0.03f });
                    }
                    if (!linkBodies) continue;
                    var under = body.AddFrame(id);
                    for (var v = 0; v < body.VertexCount; v++)
                    {
                        var dir = math.normalize(body.Position(v) - centre);
                        if (math.dot(dir, axis) > 0.8f) under.Add(new ShapeDelta { Vertex = v, Delta = dir * 0.03f });
                    }
                }
                // Face-like frames on the body: each moves a small patch 5 mm.
                for (var f = 0; f < bodyFramesPerLimb; f++)
                {
                    var axis = rng.NextFloat3Direction();
                    var frame = body.AddFrame();
                    for (var v = 0; v < body.VertexCount; v++)
                    {
                        var dir = math.normalize(body.Position(v) - centre);
                        if (math.dot(dir, axis) > 0.98f) frame.Add(new ShapeDelta { Vertex = v, Delta = dir * 0.005f });
                    }
                }
                scene.Add(body, occludes: bodiesOcclude);
                if (garment == null) scene.Add(shell, occludes: true);
                else scene.Add(shell, occludes: true, new OccluderSubmesh { Culling = garment.Value });
            }
            // Coats last, so bodies and garments keep their pairs at the head of the list. A coat clears a garment cap
            // lifted 3 cm.
            if (garment != null)
                for (var limb = 0; limb < Limbs; limb++)
                {
                    var coat = new MeshBuilder();
                    coat.Sphere(new float3((limb % 4) * 0.35f, (limb / 4) * 0.35f, 0f), 0.14f, 70, 90, MeshBuilder.On(limb + 1));
                    scene.Add(coat, occludes: true);
                }
            Assert.That(frames, Is.EqualTo(20));
            if (bodyFramesPerLimb == 0) Assert.That(frames, Is.EqualTo(20));
            return scene;
        }

        static int[] Bones()
        {
            var parents = new int[Limbs + 1];
            parents[0] = -1;
            for (var i = 1; i <= Limbs; i++) parents[i] = 0;
            return parents;
        }

        [Test, Explicit]
        public void HundredThousandVertices_UnderHundredThousandTriangles()
        {
            // Warm Burst on a small scene so compilation is not timed.
            using (var warm = new Scene())
            {
                var b = new MeshBuilder();
                b.Sphere(float3.zero, 0.1f, 8, 16, MeshBuilder.On(1));
                var s = new MeshBuilder();
                s.Sphere(float3.zero, 0.105f, 8, 16, MeshBuilder.On(1));
                var f = s.AddFrame();
                f.Add(new ShapeDelta { Vertex = 20, Delta = new float3(0, 0.05f, 0) });
                warm.Add(b, occludes: true);
                warm.Add(s, occludes: true);
                warm.Solve(OcclusionSettings.Default, new[] { -1, 0 });
                var mid = OcclusionSettings.Default;
                mid.Split = BvhSplit.Midpoint;
                warm.Solve(mid, new[] { -1, 0 });
            }

            Measure("garments", BvhSplit.Sah, Build());
            Measure("garments", BvhSplit.Midpoint, Build());
            // The body is always on too, so it occludes, and it carries a face's worth of live frames.
            Measure("body occludes, 200 body frames", BvhSplit.Sah, Build(bodiesOcclude: true, bodyFramesPerLimb: 25));
            // The back-side sampling's cost: the same coated scene with the garment back-culled, then double-sided.
            Measure("coat over back-culled garment", BvhSplit.Sah, Build(bodiesOcclude: true, garment: FaceCulling.Back));
            Measure("coat over double-sided garment", BvhSplit.Sah, Build(bodiesOcclude: true, garment: FaceCulling.Off));
            // Each garment frame with a body frame under it: 20 configurations of two meshes each, then the same 40 frames alone.
            Measure("body and garment linked", BvhSplit.Sah, Build(linkBodies: true));
            Measure("body and garment unlinked", BvhSplit.Sah, Build(linkBodies: true, linked: false));
        }

        static void Measure(string label, BvhSplit split, Scene scene)
        {
            using (scene)
            {
                var settings = OcclusionSettings.Default;
                settings.Split = split;
                var results = scene.Solve(settings, Bones());
                var st = scene.Stats;
                var bodyVerts = 0;
                var hidden = 0;
                var bodyTris = 0;
                var garmentHidden = 0;
                var garmentTris = 0;
                for (var i = 0; i < 2 * Limbs; i += 2)
                {
                    bodyVerts += results[i].CoveredVertices.Length;
                    bodyTris += results[i].HiddenTriangles.Length;
                    hidden += results[i].HiddenCount;
                    garmentTris += results[i + 1].HiddenTriangles.Length;
                    garmentHidden += results[i + 1].HiddenCount;
                }
                var queryMs = st.RestMs + st.FramesMs + st.DeletionPassMs;
                TestContext.WriteLine(
                    $"[bench] {label} split={split} bodyVerts={bodyVerts} occluderTris={st.OccluderTriangles} bvhNodes={st.BvhNodes} " +
                    $"frames={st.LiveFrames} configurations={st.Configurations} vertexSamples={st.VertexSamples} interiorSamples={st.InteriorSamples} " +
                    $"buildMs={st.BuildMs:F1} restMs={st.RestMs:F1} framesMs={st.FramesMs:F1} framePrepMs={st.FramePrepMs:F1} deletionPassMs={st.DeletionPassMs:F1} deletionPassSamples={st.DeletionPassSamples} " +
                    $"totalMs={st.TotalMs:F1} rays={st.Rays} raysPerSec={st.Rays / (queryMs / 1000.0):F0} hiddenBodyTris={hidden}/{bodyTris} hiddenGarmentTris={garmentHidden}/{garmentTris}");
                Assert.That(hidden, Is.GreaterThan(bodyTris / 2), "the garments cover most of the body; a benchmark that hides nothing measures nothing");
            }
        }
    }
}
