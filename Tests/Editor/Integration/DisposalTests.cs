using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.TestTools;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // Preparing leaves its native memory in an object the caller disposes, and cutting allocates its own. A failure at
    // any step, of one renderer or of the whole avatar, must leave none of it behind. Unity's leak tracking counts every
    // native allocation made since it last forgave and not yet freed, which is what each case reads after the pipeline
    // has returned or thrown. The avatar exercises every owner: occlusion, the clearance surface with a renderer the pass
    // leaves as it was, recorded sequences and cuts.
    public class DisposalTests
    {
        AvatarFixture a;
        SkinnedMeshRenderer body;
        NativeLeakDetectionMode mode;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            var chest = a.Bone("Armature/Hips/Chest");
            var garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            body = a.Skinned("Body", a.Sphere(0.1f, 16, 32, name: "body"), new[] { chest });
            a.Skinned("Cape", a.Sphere(0.12f, 8, 16, 0f, 90f, name: "cape"), new[] { chest }).gameObject.AddComponent<Cloth>();
            var optimizer = a.Optimizer(decimate: true);
            optimizer.alwaysOn.Add(garment);
            optimizer.target = TargetKind.TriangleBudget;
            optimizer.triangleBudget = 600;

            mode = NativeLeakDetection.Mode;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
            // A first run pays for whatever Burst and the job system keep for good, which is not the pipeline's to free.
            a.Run(apply: false);
            UnsafeUtility.ForgiveLeaks();
        }

        [TearDown]
        public void TearDown()
        {
            NativeLeakDetection.Mode = mode;
            a.Dispose();
        }

        static readonly object[] Steps = Enum.GetValues(typeof(AvatarStep)).Cast<object>().ToArray();

        [TestCaseSource(nameof(Steps))]
        public void AThrowAtAnAvatarStep_Escapes_AndLeavesNoNativeMemory(object step)
        {
            Assert.Throws<InvalidOperationException>(() => a.Run(apply: false, tweak: input => input.BeforeStep = at =>
            {
                if (at == (AvatarStep)step) throw new InvalidOperationException("injected");
            }));

            Assert.That(UnsafeUtility.CheckForLeaks(), Is.Zero);
        }

        [Test]
        public void ARendererFailingAtAStage_LeavesNoNativeMemory([Values] PipelineStage stage)
        {
            LogAssert.Expect(LogType.Exception, new Regex("injected"));

            var report = a.Run(apply: false, tweak: input => input.BeforeStage = (renderer, at) =>
            {
                if (renderer == body && at == stage) throw new InvalidOperationException("injected");
            });

            Assert.That(report.Diagnostics.Any(d => d.Key == DiagnosticKeys.RendererFailed && d.Args.Contains(body)), "the failure happened");
            Assert.That(UnsafeUtility.CheckForLeaks(), Is.Zero);
        }

        // Cutting reads the prepared avatar and changes nothing in it, so a second cut writes the same meshes.
        [Test]
        public void APreparedAvatar_CutTwice_WritesTheSameMeshes_AndDisposingItFreesEverything()
        {
            var input = new PipelineInput
            {
                Root = a.Root.transform,
                Settings = a.Root.GetComponent<AvatarMeshOptimizer>(),
                Liveness = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>(),
            };
            OptimizationReport first, second;
            using (var prepared = OptimizerPipeline.Prepare(input))
            {
                first = OptimizerPipeline.Cut(prepared);
                second = OptimizerPipeline.Cut(prepared);
            }
            foreach (var r in first.Renderers.Concat(second.Renderers)) if (r.OutputMesh != null) a.Made(r.OutputMesh);

            Assert.That(UnsafeUtility.CheckForLeaks(), Is.Zero);
            Assert.That(first.Renderers.Count(r => r.OutputMesh != null), Is.GreaterThanOrEqualTo(2), "both renderers were written");
            Assert.That(second.ErrorLevel, Is.EqualTo(first.ErrorLevel));
            for (var i = 0; i < first.Renderers.Count; i++)
            {
                var (x, y) = (first.Renderers[i], second.Renderers[i]);
                Assert.That(y.Renderer, Is.SameAs(x.Renderer));
                Assert.That((y.Skip, y.AllocationCut, y.Error, y.TrianglesAfter, y.HiddenRemoved), Is.EqualTo((x.Skip, x.AllocationCut, x.Error, x.TrianglesAfter, x.HiddenRemoved)));
                Assert.That(y.KeptVertices, Is.EqualTo(x.KeptVertices));
                if (x.OutputMesh == null) continue;
                Assert.That(y.OutputMesh, Is.Not.SameAs(x.OutputMesh));
                Assert.That(y.OutputMesh.triangles, Is.EqualTo(x.OutputMesh.triangles));
            }
        }
    }
}
