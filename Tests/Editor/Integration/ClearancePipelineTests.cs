using System.Collections.Generic;
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
    // garment's chords cut through the body. The pass supplies the rule with what Core cannot know: by default every
    // renderer's kept surface, shown or not; two switches on the input change which.
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
        static float Crossing(Mesh m1, Mesh m2) => LayerFixtures.CrossingLength(Pos(m1), Tris(m1), Pos(m2), Tris(m2));

        static int3[] Tris(Mesh m)
        {
            var t = m.triangles;
            return Enumerable.Range(0, t.Length / 3).Select(i => new int3(t[3 * i], t[3 * i + 1], t[3 * i + 2])).ToArray();
        }

        static float3[] Pos(Mesh m) => m.vertices.Select(v => (float3)v).ToArray();

        // A garment toggled off at build is still a neighbour by default: whatever turns it on shows it against the body.
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

        // With only renderers on at build as neighbours, one off at build, by its object or by its own switch, constrains
        // nothing: the body sees none of it. It is decimated as if it alone were turned on, which here is exactly as with
        // the switch off, where it constrains the body as in the test above.
        [TestCase(false)]
        [TestCase(true)]
        public void ARendererOffAtBuild_ConstrainsNothing_OnlyWithActiveOnlyNeighbours(bool byItsOwnSwitch)
        {
            var garment = Garment();
            if (byItsOwnSwitch) garment.enabled = false;
            else garment.gameObject.SetActive(false);

            var off = a.Run(apply: false);
            var on = a.Run(tweak: i => i.ActiveOnlyNeighbours = true, apply: false);

            TestContext.WriteLine($"body near vertices {Of(off, body).NearVertices} with the switch off, {Of(on, body).NearVertices} on; garment near vertices {Of(off, garment).NearVertices} and {Of(on, garment).NearVertices}, triangles {Of(off, garment).TrianglesAfter} and {Of(on, garment).TrianglesAfter}");
            Assert.That(Of(off, body).NearVertices, Is.GreaterThan(Of(off, body).TrianglesBefore / 4), "off, the body sees the garment");
            Assert.That(Of(on, body).NearVertices, Is.Zero, "on, the body sees nothing of it");
            Assert.That(Of(on, garment).NearVertices, Is.GreaterThan(Of(on, garment).TrianglesBefore / 8), "the garment still sees the body");
            Assert.That(Of(on, garment).TrianglesAfter, Is.LessThan(Of(on, garment).TrianglesBefore), "the garment was decimated");
            Assert.That(Of(on, garment).NearVertices, Is.EqualTo(Of(off, garment).NearVertices));
            Assert.That(Of(on, garment).ClearanceRefused, Is.EqualTo(Of(off, garment).ClearanceRefused));
            Assert.That(Of(on, garment).TrianglesAfter, Is.EqualTo(Of(off, garment).TrianglesAfter), "the garment is decimated as it is with the switch off");
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
        // so it holds nothing back; kept, the same body is a neighbour to the whole garment. Re-judging is off: where the
        // cut garment both uncovers and crosses the deleted body, re-judging writes some of it back where it stands, crossed
        // by the garment, since the rule never saw it; what is measured here is what the rule saw. A build re-judges by
        // default; re-judging, this fixture writes back about 130 body triangles and the last assertion reads
        // about 7 cm of crossing, so what ships is not what this test checks; the written-back twin below does.
        [Test]
        public void BodyDeletedUnderTheGarment_IsNoNeighbour()
        {
            var garment = Garment(60f);
            var optimizer = a.Root.GetComponent<AvatarMeshOptimizer>();
            optimizer.alwaysOn.Add(garment);

            optimizer.removeHiddenTriangles = false;
            var kept = a.Run(apply: false);
            optimizer.removeHiddenTriangles = true;
            var removed = a.Run(tweak: i => i.Rejudge = false);

            Assert.That(Of(removed, body).HiddenRemoved, Is.GreaterThan(0), "occlusion deleted the body under the garment");
            TestContext.WriteLine($"garment near vertices {Of(kept, garment).NearVertices} with the body whole, {Of(removed, garment).NearVertices} with it deleted underneath; refused {Of(kept, garment).ClearanceRefused} and {Of(removed, garment).ClearanceRefused}");
            Assert.That(Of(removed, garment).NearVertices, Is.LessThan(Of(kept, garment).NearVertices / 2));
            Assert.That(Crossing(Drawn(removed, body), Drawn(removed, garment)), Is.Zero, "what is kept of the body is still kept clear of");
        }

        // The test above with removed triangles as neighbours: body deleted under the garment stands in the surface, so it
        // holds the garment back exactly as the whole body does.
        [Test]
        public void BodyDeletedUnderTheGarment_IsANeighbour_WithRemovedAsNeighbours()
        {
            var garment = Garment(60f);
            var optimizer = a.Root.GetComponent<AvatarMeshOptimizer>();
            optimizer.alwaysOn.Add(garment);

            optimizer.removeHiddenTriangles = false;
            var kept = a.Run(apply: false);
            optimizer.removeHiddenTriangles = true;
            var removed = a.Run(tweak: i =>
            {
                i.Rejudge = false;
                i.RemovedAsNeighbours = true;
            });

            Assert.That(Of(removed, body).HiddenRemoved, Is.GreaterThan(0), "occlusion deleted the body under the garment");
            TestContext.WriteLine($"garment near vertices {Of(kept, garment).NearVertices} with the body whole, {Of(removed, garment).NearVertices} with it deleted underneath; refused {Of(kept, garment).ClearanceRefused} and {Of(removed, garment).ClearanceRefused}; garment triangles {Of(kept, garment).TrianglesAfter} and {Of(removed, garment).TrianglesAfter}");
            Assert.That(Of(removed, garment).NearVertices, Is.EqualTo(Of(kept, garment).NearVertices), "the deleted body holds the garment back as the whole body does");
            Assert.That(Of(removed, garment).ClearanceRefused, Is.EqualTo(Of(kept, garment).ClearanceRefused));
            Assert.That(Crossing(Drawn(removed, body), Drawn(removed, garment)), Is.Zero, "what is kept of the body is still kept clear of");
        }

        // The rule's stated limit, on the path a build takes. Re-judging writes back body the cut garment drew away from,
        // at its source corners, and every crossing between the two lies on a triangle written back: what occlusion kept is
        // kept clear of. By default a written-back triangle was never a neighbour, so the garment may cross it: 130 are
        // written back, crossed along about 7.4 cm. With removed triangles as neighbours it was one, and comes back clear:
        // 110 are written back, crossed along 0, and the garment keeps 1,292 triangles where it keeps 492 by default.
        [TestCase(false)]
        [TestCase(true)]
        public void BodyWrittenBackByReJudging_IsTheOnlyBodyTheGarmentCrosses(bool removedAsNeighbours)
        {
            var garment = Garment(60f);
            a.Root.GetComponent<AvatarMeshOptimizer>().alwaysOn.Add(garment);

            var first = a.Run(tweak: i =>
            {
                i.Rejudge = false;
                i.RemovedAsNeighbours = removedAsNeighbours;
            }, apply: false);
            var report = a.Run(tweak: i => i.RemovedAsNeighbours = removedAsNeighbours, apply: false);

            var r = Of(report, body);
            Assert.That(r.HiddenRestored, Is.GreaterThan(0), "re-judging wrote body back");
            var source = body.sharedMesh.triangles;
            var restored = new HashSet<(int, int, int)>();
            for (var t = 0; t < source.Length / 3; t++)
                if (Of(first, body).HiddenSourceTriangles[t] && !r.HiddenSourceTriangles[t])
                    restored.Add((source[3 * t], source[3 * t + 1], source[3 * t + 2]));
            Assert.That(restored.Count, Is.EqualTo(r.HiddenRestored));
            var written = Tris(r.OutputMesh);
            bool Restored(int3 t) => restored.Contains((r.KeptVertices[t.x], r.KeptVertices[t.y], r.KeptVertices[t.z]));
            Assert.That(written.Count(Restored), Is.EqualTo(r.HiddenRestored), "each is drawn at its source corners");

            var bodyAt = Pos(r.OutputMesh);
            var garmentMesh = Drawn(report, garment);
            float CrossingOf(bool onRestored) =>
                LayerFixtures.CrossingLength(bodyAt, written.Where(t => Restored(t) == onRestored).ToArray(), Pos(garmentMesh), Tris(garmentMesh));
            var crossing = CrossingOf(true);
            TestContext.WriteLine($"{r.HiddenRestored} body triangles written back; the garment crosses them along {crossing * 100:0.0} cm and keeps {Of(report, garment).TrianglesAfter} triangles");
            Assert.That(CrossingOf(false), Is.Zero, "the garment crosses no body triangle but those written back");
            if (removedAsNeighbours) Assert.That(crossing, Is.Zero, "a triangle written back was a neighbour, and comes back clear");
        }

        // A build reaches the pipeline through NDMF, so a measurement hands it the two switches from outside, for a scope.
        [Test]
        public void TheNeighbourSwitches_AreOffUnlessOverridden_AndHoldForTheirScope()
        {
            Assert.That(new PipelineInput().RemovedAsNeighbours, Is.False, "off unless overridden");
            Assert.That(new PipelineInput().ActiveOnlyNeighbours, Is.False, "off unless overridden");
            using (BuildSettings.OverrideRemovedAsNeighbours(true))
            {
                Assert.That(new PipelineInput().RemovedAsNeighbours, Is.True);
                Assert.That(new PipelineInput().ActiveOnlyNeighbours, Is.False, "each alone");
                using (BuildSettings.OverrideActiveOnlyNeighbours(true))
                    Assert.That(new PipelineInput().ActiveOnlyNeighbours, Is.True);
                Assert.That(new PipelineInput().ActiveOnlyNeighbours, Is.False);
            }
            Assert.That(new PipelineInput().RemovedAsNeighbours, Is.False);
        }
    }
}
