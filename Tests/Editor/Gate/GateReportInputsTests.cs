using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Tests.Integration;
using UnityEngine;
using UnityEngine.TestTools;
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

            var crossing = GateRun.Measure(report, a.Root.transform, null).Crossing;
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
            var crossing = GateRun.Measure(report, a.Root.transform, null).Crossing;

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
            var result = GateRun.Measure(report, a.Root.transform, null);

            Assert.That(result.Crossing.Pairs.Select(p => $"{p.RendererA} x {p.RendererB}"), Does.Contain("Body x Garment"));
            Assert.That(inputs.Geometry.Concat(inputs.Crossing).Select(i => i.Renderer), Has.No.Member(broken));
            StringAssert.Contains("left out of both channels: NonFinite", result.LeftOut.Single(l => l.path == "Broken").reason);
        }

        // A renderer the pass skips before its numbers are read, as Cloth, never gets the non-finite check, so it reaches the
        // crossing channel measured unchanged with its NaN; the channel leaves it out by name and measures the rest.
        [Test]
        public void ARendererSkippedBeforeItsNumbersAreRead_WithANonFinitePosition_IsLeftOutOfCrossingByName()
        {
            var mesh = a.Sphere(0.1f, 16, 32, name: "broken");
            var broken = a.Skinned("Broken", mesh, new[] { a.Bone("Armature/Hips/Chest") });
            // Cloth reads the mesh once, when added; given the NaN then, Unity logs its refusal.
            broken.gameObject.AddComponent<Cloth>();
            var vertices = mesh.vertices;
            vertices[40] = new Vector3(float.NaN, 0f, 0f);
            mesh.vertices = vertices;
            var report = a.Run();
            Assert.That(Of(report, broken).Skip, Is.EqualTo(SkipReason.Cloth), "the fixture must be skipped before its numbers are read");

            var result = GateRun.Measure(report, a.Root.transform, null);

            Assert.That(result.Failures, Is.Empty, "a renderer left out is not a failed channel");
            var crossing = result.Crossing;
            Assert.That(crossing.Pairs.Select(p => $"{p.RendererA} x {p.RendererB}"), Does.Contain("Body x Garment"));
            Assert.That(crossing.Pairs.Where(p => p.RendererA == "Broken" || p.RendererB == "Broken"), Is.Empty);
            var left = crossing.LeftOut.Single();
            Assert.That(result.LeftOut, Has.Member(left), "the runner lists it with the renderers the report's inputs left out");
            Assert.That(left.path, Is.EqualTo("Broken"));
            StringAssert.Contains("in as it stands: surface source vertex 40", left.reason);
            StringAssert.Contains("not a finite position", left.reason);
        }

        // The runner's result is read after the build's clone is gone, so it holds figures and paths, never a Unity object,
        // and measuring leaves every renderer drawing the mesh it drew.
        [Test]
        public void TheRunner_ReturnsFiguresByPath_ThatOutliveTheAvatar_AndLeavesItsMeshesAsTheyWere()
        {
            var report = a.Run();
            var drawn = new[] { body, bowl }.Select(r => r.sharedMesh).ToArray();

            var result = GateRun.Measure(report, a.Root.transform, new GateRunOptions { Geometry = true });

            Assert.That(new[] { body, bowl }.Select(r => r.sharedMesh), Is.EqualTo(drawn));
            Assert.That(CanHoldAUnityObject(typeof(GateResult), new HashSet<Type>()), Is.False);
            Assert.That(result.Failures, Is.Empty);
            Object.DestroyImmediate(a.Root);
            var lines = result.Lines();
            Assert.That(lines[0], Is.EqualTo("avatar: " + report.AvatarName), "no failure, so nothing reads before the avatar");
            Assert.That(lines, Has.Some.StartWith("[as it stands] pairs=3 "));
            Assert.That(lines, Has.Some.StartWith("  Body: as it stands: 1 configurations, worst p99=0.000"));
            Assert.That(lines, Has.Member("leftOut: Empty: left out of both channels: NoMesh"));
            Assert.That(lines.Last(), Does.StartWith("geometryMs="));
        }

        static bool CanHoldAUnityObject(Type type, HashSet<Type> seen)
        {
            if (!seen.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string)) return false;
            if (typeof(Object).IsAssignableFrom(type) || type == typeof(object)) return true;
            if (type.IsArray) return CanHoldAUnityObject(type.GetElementType(), seen);
            if (type.IsGenericType && type.GetGenericArguments().Any(t => CanHoldAUnityObject(t, seen))) return true;
            if (type.Namespace != null && type.Namespace.StartsWith("System")) return false;
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(f => CanHoldAUnityObject(f.FieldType, seen));
        }

        // A stage the pass fell back from and a channel that threw both read before any figure, marked, so a run missing
        // either cannot pass for a clean one.
        [Test]
        public void AStageThatFailedAndAChannelThatThrew_ReadFirst_MarkedFailed()
        {
            LogAssert.Expect(LogType.Exception, new Regex("extraction fails here"));
            var report = a.Run(tweak: input => input.BeforeStage = (r, stage) =>
            {
                if (r == body && stage == PipelineStage.Extraction) throw new InvalidOperationException("extraction fails here");
            });
            var unusable = new BoneRotationPose("unusable", ("No/Such/Bone", Quaternion.identity));

            var result = GateRun.Measure(report, a.Root.transform, new GateRunOptions { Poses = new[] { unusable } });

            Assert.That(result.Crossing, Is.Null);
            var lines = result.Lines();
            Assert.That(lines.Take(2), Has.All.StartWith("FAILED"));
            StringAssert.Contains("renderer-failed", lines[0]);
            StringAssert.Contains("Body, Extraction, extraction fails here", lines[0]);
            StringAssert.Contains("crossing channel", lines[1]);
            StringAssert.Contains("No/Such/Bone", lines[1]);
            Assert.That(lines[2], Does.StartWith("avatar: "));
            Assert.That(lines, Has.Member("not measured: the channel failed (see FAILED above)"));
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
