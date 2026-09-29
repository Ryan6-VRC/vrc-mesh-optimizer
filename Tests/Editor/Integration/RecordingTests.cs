using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // Every renderer's collapse sequence is recorded beside the others', each on a worker thread. Each must record what it
    // records alone, and a failure stays with the renderer it happens on. A renderer alone is the same avatar with every
    // other renderer excluded from decimation, which leaves every input of its recording as it was: the others still stand
    // in the clearance surface, the rig and occlusion. The avatar has what a recording reads: layers a millimetre apart, a
    // sheet crossing both, a garment hiding part of the body, a joint between two bones, and a rigid renderer.
    public class RecordingTests
    {
        AvatarFixture a;
        SkinnedMeshRenderer body, garment, skirt;
        MeshRenderer prop;
        Renderer[] decimated;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            var hips = a.Bone("Armature/Hips");
            var chest = a.Bone("Armature/Hips/Chest");
            chest.localPosition = new Vector3(0f, 0.05f, 0f);
            var bones = new[] { hips, chest };
            Mesh Jointed(Mesh m)
            {
                AvatarFixture.Weight(m, v => v.y < 0.03f ? 0 : 1);
                return m;
            }
            body = a.Skinned("Body", Jointed(a.Sphere(0.1f, 48, 96, name: "body")), bones);
            garment = a.Skinned("Garment", Jointed(a.Sphere(0.101f, 56, 104, 30f, 180f, name: "garment")), bones);
            skirt = a.Skinned("Skirt", Jointed(a.Grid(48, 0.005f, name: "skirt")), bones);
            prop = a.Rigid("Prop", a.Sphere(0.03f, 16, 32, name: "prop"), chest);
            prop.transform.localPosition = new Vector3(0.2f, 0f, 0f);
            decimated = new Renderer[] { body, garment, skirt, prop };
            var optimizer = a.Optimizer(decimate: true);
            optimizer.alwaysOn.Add(garment);
            optimizer.target = TargetKind.Tolerance;
            optimizer.tolerance = 2f;
        }

        [TearDown] public void TearDown() => a.Dispose();

        PreparedAvatar Prepare(Action<PipelineInput> tweak = null)
        {
            var input = new PipelineInput
            {
                Root = a.Root.transform,
                Settings = a.Root.GetComponent<AvatarMeshOptimizer>(),
                Liveness = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>(),
            };
            tweak?.Invoke(input);
            return OptimizerPipeline.Prepare(input);
        }

        static PreparedRenderer Of(PreparedAvatar prepared, Renderer renderer) => prepared.Renderers.Single(j => j.Renderer == renderer);

        /// <summary>Every renderer but <paramref name="renderer"/> excluded from decimation.</summary>
        void Alone(Renderer renderer)
        {
            var renderers = a.Root.GetComponent<AvatarMeshOptimizer>().renderers;
            renderers.Clear();
            foreach (var r in decimated)
                if (r != renderer) renderers.Add(new RendererSettings { renderer = r, excludeFromDecimation = true });
        }

        static Action<PipelineInput> FailAt(Renderer failing) => input => input.BeforeStage = (renderer, at) =>
        {
            if (renderer == failing && at == PipelineStage.Decimation) throw new InvalidOperationException("injected");
        };

        [Test]
        public void EveryRenderer_RecordsBesideTheOthers_StepForStepWhatItRecordsAlone()
        {
            using var together = Prepare();
            Assert.That(Of(together, garment).NearVertices, Is.GreaterThan(0), "the clearance rule reads the body");
            Assert.That(Of(together, body).HiddenCount, Is.GreaterThan(0), "the garment hides some of the body");
            foreach (var r in decimated)
            {
                Alone(r);
                using var alone = Prepare();
                Assert.That(decimated.Where(o => o != r).All(o => !Of(alone, o).Decimated), $"{r.name} recorded alone");
                AssertSameRecording(Of(alone, r), Of(together, r));
            }
        }

        // The seam is called on the main thread before the renderer is recorded, so a throw there leaves it unrecorded,
        // named, and the renderers recorded before and after it as they would be.
        [Test]
        public void ARendererFailingToRecord_FailsAlone_AndIsNamed_WhileTheOthersRecordWhatTheyWould()
        {
            using var healthy = Prepare();
            LogAssert.Expect(LogType.Exception, new Regex("injected"));
            using var failed = Prepare(FailAt(garment));

            var g = Of(failed, garment);
            Assert.That((g.Decimated, g.SequenceLength), Is.EqualTo((false, 0)));
            var named = failed.Diagnostics.Where(d => d.Key == DiagnosticKeys.RendererFailed).ToList();
            Assert.That(named.Count, Is.EqualTo(1));
            Assert.That(named[0].Args, Is.EqualTo(new object[] { garment, nameof(PipelineStage.Decimation), "injected" }));
            foreach (var r in decimated)
                if (r != garment) AssertSameRecording(Of(healthy, r), Of(failed, r));
        }

        [Test]
        public void TheDecimationStageSeam_IsCalledOncePerRenderer_OnTheMainThread_InHierarchyOrder()
        {
            a.Root.GetComponent<AvatarMeshOptimizer>().renderers.Add(new RendererSettings { renderer = skirt, excludeFromDecimation = true });
            var main = Thread.CurrentThread.ManagedThreadId;
            var calls = new List<(Renderer renderer, int thread)>();

            using var prepared = Prepare(input => input.BeforeStage = (renderer, at) =>
            {
                if (at == PipelineStage.Decimation) calls.Add((renderer, Thread.CurrentThread.ManagedThreadId));
            });

            var order = a.Root.GetComponentsInChildren<Renderer>(true).Where(r => r != skirt);
            Assert.That(calls.Select(c => c.renderer), Is.EqualTo(order), "an excluded renderer is not recorded");
            Assert.That(calls.Select(c => c.thread), Is.All.EqualTo(main));
        }

        // The stage's time runs from before the first renderer is reached until the last sequence is in hand, which from
        // outside is nearly all of the time from the first renderer's seam until preparing returns.
        [Test]
        public void TheDecimationTime_IsTheTimeRecordingTook()
        {
            var clock = Stopwatch.StartNew();
            double recording = -1, first = -1;
            using var prepared = Prepare(input =>
            {
                input.BeforeStep = step =>
                {
                    if (step == AvatarStep.Recording) recording = clock.Elapsed.TotalMilliseconds;
                };
                input.BeforeStage = (renderer, at) =>
                {
                    if (at == PipelineStage.Decimation && first < 0) first = clock.Elapsed.TotalMilliseconds;
                };
            });
            var end = clock.Elapsed.TotalMilliseconds;

            var time = prepared.Times.Decimation;
            TestContext.WriteLine($"decimation {time:F1} ms; from the recording step {end - recording:F1} ms, from the first renderer {end - first:F1} ms");
            Assert.That(time, Is.LessThanOrEqualTo(end - recording));
            Assert.That(time, Is.GreaterThanOrEqualTo(0.9 * (end - first)));
        }

        [Test]
        public void ARendererFailingWhileTheOthersRecord_LeavesNoNativeMemory()
        {
            AssertNoLeaks(() =>
            {
                LogAssert.Expect(LogType.Exception, new Regex("injected"));
                using var prepared = Prepare(FailAt(skirt));
                Assert.That(decimated.Select(r => Of(prepared, r).Decimated), Is.EqualTo(new[] { true, true, false, true }));
            });
        }

        // Each renderer fails inside the decimator, after its protection is made and its preparation begun.
        [Test]
        public void EveryRendererFailingInsideTheDecimator_LeavesNoNativeMemory()
        {
            AssertNoLeaks(() =>
            {
                foreach (var _ in decimated) LogAssert.Expect(LogType.Exception, new Regex("clearance share"));
                using var prepared = Prepare(input => input.Decimation.Clearance.Share = 0.5f);
                Assert.That(decimated.Select(r => Of(prepared, r).Decimated), Is.All.False);
                Assert.That(prepared.Diagnostics.Count(d => d.Key == DiagnosticKeys.RendererFailed), Is.EqualTo(decimated.Length));
            });
        }

        /// <summary>
        /// Runs <paramref name="run"/> after a first preparation has paid for whatever Burst and the job system keep for good,
        /// and asserts it left no native allocation behind.
        /// </summary>
        void AssertNoLeaks(Action run)
        {
            var mode = NativeLeakDetection.Mode;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
            try
            {
                Prepare().Dispose();
                UnsafeUtility.ForgiveLeaks();
                run();
                Assert.That(UnsafeUtility.CheckForLeaks(), Is.Zero);
            }
            finally
            {
                NativeLeakDetection.Mode = mode;
            }
        }

        /// <summary>The same sequence, step for step and bit for bit, and the same figures from recording it.</summary>
        static void AssertSameRecording(PreparedRenderer expected, PreparedRenderer actual)
        {
            var name = expected.Renderer.name;
            Assert.That(expected.Decimated && actual.Decimated, $"{name} recorded");
            Assert.That(expected.SequenceLength, Is.GreaterThan(100), $"{name} decimates");
            var (x, y) = (expected.Sequence, actual.Sequence);
            Assert.That(y.InitialTriangleCount, Is.EqualTo(x.InitialTriangleCount), $"{name}: triangles");
            Assert.That(y.Steps.Select(s => math.asuint(s.Error)), Is.EqualTo(x.Steps.Select(s => math.asuint(s.Error))), $"{name}: errors");
            Assert.That(y.Steps.Select(s => (s.TriangleCount, s.RecordStart, s.RecordCount)), Is.EqualTo(x.Steps.Select(s => (s.TriangleCount, s.RecordStart, s.RecordCount))), $"{name}: steps");
            Assert.That(y.Records.ToArray(), Is.EqualTo(x.Records.ToArray()), $"{name}: records");
            Assert.That(y.WorkingOrigin.ToArray(), Is.EqualTo(x.WorkingOrigin.ToArray()), $"{name}: working origin");
            Assert.That(y.WorkingTriangles.ToArray(), Is.EqualTo(x.WorkingTriangles.ToArray()), $"{name}: working triangles");
            Assert.That((actual.SequenceLength, actual.NearVertices, actual.ClearanceRefused, actual.FloorTriangles, actual.Weight),
                Is.EqualTo((expected.SequenceLength, expected.NearVertices, expected.ClearanceRefused, expected.FloorTriangles, expected.Weight)), $"{name}: figures");
            Assert.That((actual.LockedVertices, actual.LockedFromZeroScale, actual.LockedForFur, actual.CostRaisedForGrowth),
                Is.EqualTo((expected.LockedVertices, expected.LockedFromZeroScale, expected.LockedForFur, expected.CostRaisedForGrowth)), $"{name}: protection");
        }
    }
}
