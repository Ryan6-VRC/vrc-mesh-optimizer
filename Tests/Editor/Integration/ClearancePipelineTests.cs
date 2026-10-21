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
    // garment's chords cut through the body. The pass supplies the rule with what Core cannot know: every renderer's
    // surface, shown or not, the triangles occlusion removed included.
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

        // Body deleted under an always-on garment stands in the surface as a neighbour, so it holds the garment back
        // exactly as the whole body does. Re-judging is off: what is measured here is what the rule saw.
        [Test]
        public void BodyDeletedUnderTheGarment_IsANeighbour()
        {
            var garment = Garment(60f);
            var optimizer = a.Root.GetComponent<AvatarMeshOptimizer>();
            optimizer.alwaysOn.Add(garment);

            optimizer.removeHiddenTriangles = false;
            var kept = a.Run(apply: false);
            optimizer.removeHiddenTriangles = true;
            var removed = a.Run(tweak: i => i.Rejudge = false);

            Assert.That(Of(removed, body).HiddenRemoved, Is.GreaterThan(0), "occlusion deleted the body under the garment");
            TestContext.WriteLine($"garment near vertices {Of(kept, garment).NearVertices} with the body whole, {Of(removed, garment).NearVertices} with it deleted underneath; refused {Of(kept, garment).ClearanceRefused} and {Of(removed, garment).ClearanceRefused}; garment triangles {Of(kept, garment).TrianglesAfter} and {Of(removed, garment).TrianglesAfter}");
            Assert.That(Of(removed, garment).NearVertices, Is.EqualTo(Of(kept, garment).NearVertices), "the deleted body holds the garment back as the whole body does");
            Assert.That(Of(removed, garment).ClearanceRefused, Is.EqualTo(Of(kept, garment).ClearanceRefused));
            Assert.That(Crossing(Drawn(removed, body), Drawn(removed, garment)), Is.Zero, "what is kept of the body is still kept clear of");
        }

        // The test above with the switch off: neighbours are what occlusion kept, and body deleted under the garment is not
        // there to be passed through, so it holds nothing back. Where the cut garment both uncovers and crosses the deleted
        // body, re-judging then writes some of it back where it stands, crossed; the written-back twin below reads that.
        [Test]
        public void BodyDeletedUnderTheGarment_IsNoNeighbour_WithRemovedAsNeighboursOff()
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
                i.RemovedAsNeighbours = false;
            });

            Assert.That(Of(removed, body).HiddenRemoved, Is.GreaterThan(0), "occlusion deleted the body under the garment");
            TestContext.WriteLine($"garment near vertices {Of(kept, garment).NearVertices} with the body whole, {Of(removed, garment).NearVertices} with it deleted underneath; refused {Of(kept, garment).ClearanceRefused} and {Of(removed, garment).ClearanceRefused}");
            Assert.That(Of(removed, garment).NearVertices, Is.LessThan(Of(kept, garment).NearVertices / 2));
            Assert.That(Crossing(Drawn(removed, body), Drawn(removed, garment)), Is.Zero, "what is kept of the body is still kept clear of");
        }

        // On the path a build takes, re-judging writes back body the cut garment drew away from, at its source corners, and
        // what occlusion kept is kept clear of. A triangle written back was a neighbour, so it comes back clear. What the
        // switch off costs: a triangle written back was never a neighbour, so the garment crosses it, and only it.
        [TestCase(true)]
        [TestCase(false)]
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
            else Assert.That(crossing, Is.GreaterThan(0.01f), "off, a triangle written back was no neighbour, and the garment crosses it");
        }

        // A build reaches the pipeline through NDMF, so a measurement turns the switch off from outside, for a scope.
        [Test]
        public void RemovedAsNeighbours_IsOnUnlessOverridden_AndHoldsForItsScope()
        {
            Assert.That(new PipelineInput().RemovedAsNeighbours, Is.True, "on unless overridden");
            using (BuildSettings.OverrideRemovedAsNeighbours(false))
                Assert.That(new PipelineInput().RemovedAsNeighbours, Is.False);
            Assert.That(new PipelineInput().RemovedAsNeighbours, Is.True);
        }
    }
}
