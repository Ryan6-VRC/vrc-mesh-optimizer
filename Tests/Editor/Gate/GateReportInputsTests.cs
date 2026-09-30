using System;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Pipeline;
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

        // Two renderers of one name under different parents, as a base's body and a garment's: both channels name a renderer
        // by the path its report row carries, so each line joins to its row by string equality and the two never merge.
        [Test]
        public void RenderersOfOneName_AreNamedInBothChannels_ByTheirReportRowsPaths()
        {
            var outfit = new GameObject("Outfit").transform;
            outfit.SetParent(a.Root.transform, false);
            outfit.localPosition = new Vector3(1f, 0f, 0f);
            var chest = a.Bone("Armature/Hips/Chest");
            var inner = a.Skinned("Body", a.Sphere(0.1f, 16, 32, name: "outfit body"), new[] { chest }, outfit);
            a.Optimizer().alwaysOn.Add(a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "outfit garment"), new[] { chest }, outfit));
            var report = a.Run();
            var rows = report.Renderers.ToDictionary(r => r.Renderer, r => r.Path);
            Assert.That(rows[inner], Is.EqualTo("Outfit/Body"));

            var inputs = GateReportInputs.From(report);
            Assert.That(inputs.Geometry.Select(i => i.Renderer), Is.EquivalentTo(new Renderer[] { body, inner }), "both bodies lose what their bowls cover");
            var crossing = CrossingGate.Measure(a.Root.transform, inputs.Crossing, null, null, CrossingSettings.Default);

            foreach (var i in inputs.Geometry)
            {
                var lines = GeometryGate.Measure(i.Renderer, a.Root.transform, i.Source, i.Optimized, i.RemovedTriangles, i.Liveness, null, SurfaceDistanceSettings.Default).Lines(GateCamera.Default);
                foreach (var line in lines) Assert.That(line, Does.StartWith(rows[i.Renderer] + ", "));
            }
            Assert.That(crossing.Pairs.Where(p => p.RendererA == p.RendererB).Select(p => p.RendererA),
                Is.EquivalentTo(inputs.Crossing.Select(i => rows[i.Renderer])), "every renderer against itself, once, by its row's path");
        }

        // A renderer the pass left alone for a non-finite position still carries it, and the crossing channel refuses a
        // surface with one, so handed over it would cost every other renderer its figures.
        [Test]
        public void ARendererSkippedForANonFinitePosition_IsLeftOutOfBothChannels_AndTheOthersAreStillMeasured()
        {
            var mesh = a.Sphere(0.1f, 16, 32, name: "broken");
            var vertices = mesh.vertices;
            vertices[40] = new Vector3(float.NaN, 0f, 0f);
            mesh.vertices = vertices;
            var broken = a.Skinned("Broken", mesh, new[] { a.Bone("Armature/Hips/Chest") });
            var report = a.Run();
            Assert.That(Of(report, broken).Skip, Is.EqualTo(SkipReason.NonFinite), "the fixture must be skipped for its NaN");

            var inputs = GateReportInputs.From(report);
            var crossing = CrossingGate.Measure(a.Root.transform, inputs.Crossing, null, null, CrossingSettings.Default);

            Assert.That(crossing.Pairs.Select(p => $"{p.RendererA} x {p.RendererB}"), Does.Contain("Body x Garment"));
            Assert.That(inputs.Geometry.Concat(inputs.Crossing).Select(i => i.Renderer), Has.No.Member(broken));
            StringAssert.Contains("left out of both channels: NonFinite", inputs.LeftOut.Single(l => l.path == "Broken").reason);
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
