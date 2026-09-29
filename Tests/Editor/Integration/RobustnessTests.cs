using System;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using UnityEngine;
using UnityEngine.TestTools;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // Real avatars bring renderers the pass cannot or need not change. Each one is left exactly as it was, and
    // named when it is a problem the user can see, while the rest of the avatar is still optimized.
    public class RobustnessTests
    {
        AvatarFixture a;
        Transform chest;
        SkinnedMeshRenderer garment;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            a.Optimizer().alwaysOn.Add(garment);
        }

        [TearDown] public void TearDown() => a.Dispose();

        SkinnedMeshRenderer Body(string name = "Body", Mesh mesh = null) =>
            a.Skinned(name, mesh ?? a.Sphere(0.1f, 16, 32, name: name), new[] { chest });

        static bool Optimized(RendererReport r) => r.OutputMesh != null && r.HiddenRemoved > 0;

        [Test]
        public void AFailingRenderer_IsLeftAsItWas_AndNamed_WhileTheOthersAreStillOptimized([Values] PipelineStage stage)
        {
            var broken = Body("Broken");
            var brokenMesh = broken.sharedMesh;
            var healthy = Body("Healthy");
            var optimizer = a.Optimizer(decimate: true);
            optimizer.target = Runtime.TargetKind.TriangleBudget;
            optimizer.triangleBudget = 100000;
            LogAssert.Expect(LogType.Exception, new Regex("injected"));

            var report = a.Run(tweak: input => input.BeforeStage = (renderer, at) =>
            {
                if (renderer == broken && at == stage) throw new InvalidOperationException("injected");
            });

            var r = Of(report, broken);
            Assert.That(report.Diagnostics.Any(d => d.Key == DiagnosticKeys.RendererFailed && d.Args.Contains(broken)), "the failure names the renderer");
            if (stage == PipelineStage.Decimation)
            {
                // Decimation failing still leaves occlusion's verdict, which is whole on its own.
                Assert.That(Optimized(r), Is.True);
                Assert.That(r.SequenceLength, Is.Zero);
            }
            else
            {
                Assert.That(broken.sharedMesh, Is.SameAs(brokenMesh));
                Assert.That(r.Skip, Is.EqualTo(SkipReason.Failed));
                Assert.That(r.OutputMesh, Is.Null);
            }
            Assert.That(Optimized(Of(report, healthy)), Is.True);
        }

        [Test]
        public void ARendererWithCloth_IsLeftAsItWas_AndNamed()
        {
            var body = Body();
            var mesh = body.sharedMesh;
            body.gameObject.AddComponent<Cloth>();

            var report = a.Run();

            Assert.That(body.sharedMesh, Is.SameAs(mesh));
            Assert.That(Of(report, body).Skip, Is.EqualTo(SkipReason.Cloth));
            Assert.That(report.Diagnostics.Any(d => d.Key == DiagnosticKeys.Cloth && d.Args.Contains(body)));
        }

        // Without Read/Write the Editor still holds the data outside play mode, which is where an upload builds, so
        // such a mesh is optimized there; under apply-on-play Unity refuses the read, and the renderer is skipped.
        [Test]
        public void AMeshWithoutReadWrite_IsOptimizedInEditMode_AndRefusedInPlayMode()
        {
            var body = Body();
            var mesh = body.sharedMesh;
            mesh.UploadMeshData(true);
            Assume.That(mesh.isReadable, Is.False);

            var report = a.Run();

            Assert.That(Optimized(Of(report, body)), Is.True);
            Assert.That(OptimizerPipeline.CanRead(mesh, playing: true), Is.False);
            Assert.That(OptimizerPipeline.CanRead(a.Sphere(0.1f, 4, 4), playing: true), Is.True, "a readable mesh reads in play mode");
        }

        [Test]
        public void AMeshSharedByTwoRenderers_GivesEachItsOwnMesh_AndTheSourceIsUntouched()
        {
            var shared = a.Sphere(0.1f, 16, 32, name: "shared");
            var triangles = shared.triangles;
            var one = Body("One", shared);
            var two = Body("Two", shared);

            var report = a.Run();

            Assert.That(Optimized(Of(report, one)) && Optimized(Of(report, two)), Is.True);
            Assert.That(one.sharedMesh, Is.Not.SameAs(two.sharedMesh));
            Assert.That(shared.triangles, Is.EqualTo(triangles));
        }

        [Test]
        public void RenderersWithNoMesh_OrNoTriangles_AreSkippedQuietly()
        {
            var empty = a.Skinned("Empty", a.Sphere(0.1f, 4, 4), new[] { chest });
            empty.sharedMesh = null;
            var lines = a.Made(new Mesh { name = "lines" });
            lines.vertices = new[] { Vector3.zero, Vector3.up };
            lines.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
            var lineRenderer = a.Rigid("Lines", lines, chest);
            var body = Body();

            var report = a.Run();

            Assert.That(Of(report, empty).Skip, Is.EqualTo(SkipReason.NoMesh));
            Assert.That(Of(report, lineRenderer).Skip, Is.EqualTo(SkipReason.NoTriangles));
            Assert.That(report.Diagnostics.Where(d => d.Args.Contains(empty) || d.Args.Contains(lineRenderer)), Is.Empty);
            Assert.That(Optimized(Of(report, body)), Is.True);
        }

        [Test]
        public void ALineSubmesh_IsPassedThrough_BesideItsTrimmedTriangles()
        {
            var mesh = a.Sphere(0.1f, 16, 32, name: "body");
            var triangles = mesh.triangles;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(triangles, 0);
            mesh.SetIndices(new[] { 0, 5, 7, 9 }, MeshTopology.Lines, 1);
            var body = Body("Body", mesh);
            body.sharedMaterials = new[] { a.Opaque(), a.Opaque() };

            var report = a.Run();

            var r = Of(report, body);
            Assert.That(Optimized(r), Is.True);
            var output = body.sharedMesh;
            Assert.That(output.GetTopology(1), Is.EqualTo(MeshTopology.Lines));
            Assert.That(output.GetIndices(1).Select(i => r.KeptVertices[i]), Is.EqualTo(new[] { 0, 5, 7, 9 }));
        }

        [Test]
        public void AMeshWithoutNormalsOrUVs_IsStillJudged()
        {
            var sphere = a.Sphere(0.1f, 16, 32);
            var mesh = a.Made(new Mesh { name = "bare" });
            mesh.vertices = sphere.vertices;
            mesh.triangles = sphere.triangles;
            Assume.That(mesh.normals, Is.Empty);
            Assume.That(mesh.uv, Is.Empty);
            var body = Body("Bare", mesh);

            Assert.That(Optimized(Of(a.Run(), body)), Is.True);
        }

        [Test]
        public void AMissingBoneSlot_RidesTheRenderer_AndDoesNotStopTheRest()
        {
            var body = Body();
            body.bones = new Transform[] { null };
            var other = Body("Other");

            var report = a.Run();

            Assert.That(Of(report, body).Skip, Is.Not.EqualTo(SkipReason.Failed));
            Assert.That(Optimized(Of(report, other)), Is.True);
        }

        // Under a parent that doubles it, a 0.05 sphere stands inside the 0.105 bowl and a 0.06 one outside it; read
        // without the parent's scale, both would be inside.
        [TestCase(0.05f, true)]
        [TestCase(0.06f, false)]
        public void ARendererUnderAScaledParent_IsJudgedWhereItDraws(float radius, bool covered)
        {
            var scaled = a.Bone("Armature/Hips/Chest/Scaled");
            scaled.localScale = Vector3.one * 2f;
            var body = a.Rigid("Body", a.Sphere(radius, 16, 32, name: "body"), scaled);

            var report = a.Run();

            Assert.That(Of(report, body).HiddenRemoved > 0, Is.EqualTo(covered));
        }

        [Test]
        public void AFullyCoveredMesh_BecomesAnEmptyMesh()
        {
            var shell = a.Skinned("Shell", a.Sphere(0.105f, 24, 48, name: "shell"), new[] { chest });
            a.Optimizer().alwaysOn.Add(shell);
            var body = Body();

            var report = a.Run();

            var r = Of(report, body);
            Assert.That(r.TrianglesAfter, Is.Zero);
            Assert.That(Triangles(body.sharedMesh), Is.Zero);
            Assert.That(body.sharedMesh.vertexCount, Is.Zero);
        }

        [Test]
        public void MoreThanSixtyThreeRenderers_AreEachJudged()
        {
            var bodies = Enumerable.Range(0, 70).Select(i => Body($"Body {i}", a.Sphere(0.1f, 6, 8, name: $"body {i}"))).ToList();

            var report = a.Run();

            Assert.That(bodies.Where(b => !Optimized(Of(report, b))).Select(b => b.name), Is.Empty);
        }

        [Test]
        public void InactiveAndDisabledRenderers_AreOptimizedLikeAnyOther()
        {
            var inactive = Body("Inactive");
            inactive.gameObject.SetActive(false);
            var disabled = Body("Disabled");
            disabled.enabled = false;

            var report = a.Run();

            Assert.That(Optimized(Of(report, inactive)) && Optimized(Of(report, disabled)), Is.True);
        }
    }
}
