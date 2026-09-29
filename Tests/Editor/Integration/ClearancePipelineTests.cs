using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Ryan6Vrc.MeshOptimizer.Tests.Decimation;
using Unity.Mathematics;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // A body sphere and a garment a millimetre over it, decimated to two millimetres: without the clearance rule the
    // garment's chords cut through the body. The pass supplies the rule with what Core cannot know: every renderer's
    // kept surface, shown or not.
    public class ClearancePipelineTests
    {
        const float BodyRadius = 0.1f;
        const float GarmentRadius = 0.101f;

        AvatarFixture a;
        Transform chest;
        SkinnedMeshRenderer body;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            body = a.Skinned("Body", a.Sphere(BodyRadius, 32, 64, name: "body"), new[] { chest });
            var optimizer = a.Optimizer(decimate: true);
            optimizer.target = TargetKind.Tolerance;
            optimizer.tolerance = 2f;
        }

        [TearDown]
        public void TearDown() => a.Dispose();

        SkinnedMeshRenderer Garment(float theta0 = 0f) =>
            a.Skinned("Garment", a.Sphere(GarmentRadius, 40, 72, theta0, 180f, name: "garment"), new[] { chest });

        static Mesh Drawn(OptimizationReport report, Renderer r) => Of(report, r).OutputMesh != null ? Of(report, r).OutputMesh : Of(report, r).SourceMesh;

        /// <summary>The renderers sit at the root, so mesh space is avatar space for both.</summary>
        static float Crossing(Mesh m1, Mesh m2)
        {
            int3[] Tris(Mesh m)
            {
                var t = m.triangles;
                return Enumerable.Range(0, t.Length / 3).Select(i => new int3(t[3 * i], t[3 * i + 1], t[3 * i + 2])).ToArray();
            }
            float3[] Pos(Mesh m) => m.vertices.Select(v => (float3)v).ToArray();
            return LayerFixtures.CrossingLength(Pos(m1), Tris(m1), Pos(m2), Tris(m2));
        }

        // A garment toggled off at build is still a neighbour: whatever turns it on shows it against the body.
        [TestCase(true)]
        [TestCase(false)]
        public void AGarmentOffAtBuild_IsKeptClearOfLikeOneThatIsOn(bool active)
        {
            var garment = Garment();
            garment.gameObject.SetActive(active);

            var without = RunWithoutTheRule();
            var report = a.Run();

            TestContext.WriteLine($"near vertices: body {Of(report, body).NearVertices}, garment {Of(report, garment).NearVertices}; refused {Of(report, body).ClearanceRefused} and {Of(report, garment).ClearanceRefused}");
            TestContext.WriteLine($"triangles {report.TrianglesAfter} with the rule, {without.report.TrianglesAfter} without; crossing without {without.crossing * 100:0.0} cm");
            Assert.That(Of(report, garment).TrianglesAfter, Is.LessThan(Of(report, garment).TrianglesBefore), "the garment was decimated");
            Assert.That(Of(report, body).NearVertices, Is.GreaterThan(Of(report, body).TrianglesBefore / 4), "the body sees the garment");
            Assert.That(without.crossing, Is.GreaterThan(0.01f), "without the rule the garment cuts through the body");
            Assert.That(Crossing(Drawn(report, body), Drawn(report, garment)), Is.Zero, "with it, it does not");
        }

        (OptimizationReport report, float crossing) RunWithoutTheRule()
        {
            var report = a.Run(tweak: i => i.Decimation.Clearance.Enabled = false, apply: false);
            var garment = a.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "Garment");
            Assert.That(Of(report, body).NearVertices, Is.Zero, "the rule off measures nothing");
            return (report, Crossing(Drawn(report, body), Drawn(report, garment)));
        }

        // A renderer the pass leaves as it was is still drawn where it stands: a body under Cloth is kept clear of by the
        // garment decimated over it, from the body's source, though the body itself is never changed.
        [Test]
        public void ARendererLeftAsItWas_IsStillKeptClearOf()
        {
            body.gameObject.AddComponent<Cloth>();
            var source = body.sharedMesh;
            var garment = Garment();

            var report = a.Run();

            Assert.That(Of(report, body).Skip, Is.EqualTo(SkipReason.Cloth));
            Assert.That(body.sharedMesh, Is.SameAs(source));
            Assert.That(Of(report, garment).TrianglesAfter, Is.LessThan(Of(report, garment).TrianglesBefore), "the garment was decimated");
            Assert.That(Of(report, garment).NearVertices, Is.GreaterThan(Of(report, garment).TrianglesBefore / 8), "the garment sees the body under Cloth");
            Assert.That(Crossing(source, Drawn(report, garment)), Is.Zero, "and does not cut through it");
        }

        [Test]
        public void WithNoOtherLayer_NothingIsNear()
        {
            var report = a.Run();
            Assert.That(Of(report, body).NearVertices, Is.Zero);
            Assert.That(Of(report, body).ClearanceRefused, Is.Zero);
        }

        // Neighbours are what occlusion kept. Body deleted under an always-on garment is not there to be passed through,
        // so it holds nothing back; kept, the same body is a neighbour to the whole garment.
        [Test]
        public void BodyDeletedUnderTheGarment_IsNoNeighbour()
        {
            var garment = Garment(60f);
            var optimizer = a.Root.GetComponent<AvatarMeshOptimizer>();
            optimizer.alwaysOn.Add(garment);

            optimizer.removeHiddenTriangles = false;
            var kept = a.Run(apply: false);
            optimizer.removeHiddenTriangles = true;
            var removed = a.Run();

            Assert.That(Of(removed, body).HiddenRemoved, Is.GreaterThan(0), "occlusion deleted the body under the garment");
            TestContext.WriteLine($"garment near vertices {Of(kept, garment).NearVertices} with the body whole, {Of(removed, garment).NearVertices} with it deleted underneath; refused {Of(kept, garment).ClearanceRefused} and {Of(removed, garment).ClearanceRefused}");
            Assert.That(Of(removed, garment).NearVertices, Is.LessThan(Of(kept, garment).NearVertices / 2));
            Assert.That(Crossing(Drawn(removed, body), Drawn(removed, garment)), Is.Zero, "what is kept of the body is still kept clear of");
        }
    }
}
