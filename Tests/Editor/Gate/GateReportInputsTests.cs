using System;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Tests.Integration;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;
using Object = UnityEngine.Object;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    // The gate reads the pipeline's own report. These run the real pass on a body under an always-on bowl, so the body
    // loses the triangles the bowl covers, and check the gate channels read that report: the hidden flags line up with
    // the gate's triangle order, a renderer the pass left alone still takes part in crossing, and a report whose clone
    // is gone is refused rather than measured as nothing.
    public class GateReportInputsTests
    {
        AvatarFixture a;
        SkinnedMeshRenderer body;
        SkinnedMeshRenderer bowl;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            var chest = a.Bone("Armature/Hips/Chest");
            body = a.Skinned("Body", a.Sphere(0.1f, 16, 32, name: "body"), new[] { chest });
            bowl = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            a.Optimizer().alwaysOn.Add(bowl);
            var empty = new GameObject("Empty");
            empty.transform.SetParent(a.Root.transform, false);
            empty.AddComponent<SkinnedMeshRenderer>();
        }

        [TearDown]
        public void TearDown() => a.Dispose();

        [Test]
        public void ReportFromThePass_FeedsBothChannels_AndItsHiddenFlagsLineUpWithTheGatesTriangles()
        {
            var report = a.Run();
            Assert.That(Of(report, body).HiddenRemoved, Is.GreaterThan(0), "the fixture must hide part of the body");

            var inputs = GateReportInputs.From(report);

            Assert.That(inputs.Geometry.Select(i => i.Renderer), Is.EqualTo(new Renderer[] { body }));
            Assert.That(inputs.Crossing.Select(i => i.Renderer), Is.EquivalentTo(new Renderer[] { body, bowl }));
            Assert.That(inputs.LeftOut.Select(l => l.path), Is.EquivalentTo(new[] { "Garment", "Empty" }));
            StringAssert.Contains("measured unchanged for crossing: Unchanged", inputs.LeftOut.Single(l => l.path == "Garment").reason);
            StringAssert.Contains("left out of both channels: NoMesh", inputs.LeftOut.Single(l => l.path == "Empty").reason);

            // Masked by the pass's own flags, everything left is exactly the source's; misaligned, holes would read.
            var input = inputs.Geometry[0];
            var masked = GeometryGate.Measure(input.Renderer, a.Root.transform, input.Source, input.Optimized, input.RemovedTriangles, input.Liveness, null, SurfaceDistanceSettings.Default);
            var unmasked = GeometryGate.Measure(input.Renderer, a.Root.transform, input.Source, input.Optimized, null, input.Liveness, null, SurfaceDistanceSettings.Default);
            Assert.That(masked.Stats(0, GateDirection.Combined).Max, Is.Zero);
            Assert.That(unmasked.Stats(0, GateDirection.SourceToOptimized).Max, Is.GreaterThan(0.001f));

            var crossing = CrossingGate.Measure(a.Root.transform, inputs.Crossing, null, null, CrossingSettings.Default);
            Assert.That(crossing.Pairs.Select(p => $"{p.RendererA} x {p.RendererB}"), Does.Contain("Body x Garment"));
            Assert.That(crossing.Pairs.Max(p => p.Change.New), Is.Zero);
        }

        [Test]
        public void ReportWhoseMeshesAreDestroyed_IsRefusedByName()
        {
            var report = a.Run();
            Object.DestroyImmediate(Of(report, body).OutputMesh);

            var e = Assert.Throws<InvalidOperationException>(() => GateReportInputs.From(report));

            StringAssert.Contains("Body", e.Message);
            StringAssert.Contains("inside the build", e.Message);
        }

        [Test]
        public void ReportWhoseRenderersAreDestroyed_IsRefusedByName()
        {
            var report = a.Run();
            Object.DestroyImmediate(bowl.gameObject);

            var e = Assert.Throws<InvalidOperationException>(() => GateReportInputs.From(report));

            StringAssert.Contains("Garment", e.Message);
            StringAssert.DoesNotContain("Body,", e.Message);
        }
    }
}
