using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests
{
    // The core is Burst jobs over native containers. This proves the venue running the suite can
    // schedule one and read its result, so a later failure is the algorithm's and not the venue's.
    public class BurstSmokeTests
    {
        [BurstCompile]
        struct SumOfSquaresJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Points;
            public NativeReference<float> Result;

            public void Execute()
            {
                var sum = 0f;
                for (var i = 0; i < Points.Length; i++) sum += math.lengthsq(Points[i]);
                Result.Value = sum;
            }
        }

        [Test]
        public void BurstJob_RunsAndReturnsItsResult()
        {
            using var points = new NativeArray<float3>(new[] { new float3(1, 2, 2), new float3(0, 3, 4) }, Allocator.TempJob);
            using var result = new NativeReference<float>(Allocator.TempJob);

            new SumOfSquaresJob { Points = points, Result = result }.Schedule().Complete();

            Assert.That(result.Value, Is.EqualTo(34f).Within(1e-6f));
        }

        // In the Editor, Burst runs a job compiled asynchronously as managed code until its compilation finishes, so a
        // build after a cold Burst cache would run the whole collapse loop under Mono, many times slower and rounded
        // differently from every later build. Every job in the core must wait for its compiled code.
        [Test]
        public void EveryCoreBurstJob_CompilesSynchronously()
        {
            var jobs = typeof(Ryan6Vrc.MeshOptimizer.Core.Decimation.Decimator).Assembly.GetTypes()
                .Select(t => (type: t, burst: t.GetCustomAttributes(typeof(BurstCompileAttribute), false).Cast<BurstCompileAttribute>().FirstOrDefault()))
                .Where(x => x.burst != null)
                .ToList();

            Assert.That(jobs, Is.Not.Empty);
            Assert.That(jobs.Where(x => !x.burst.CompileSynchronously).Select(x => x.type.FullName), Is.Empty);
        }

        // The Editor's Burst compiler can serve native code built from an earlier version of the core, which then runs
        // against the current job's fields and records a different sequence, collapses near free, with nothing reported.
        // So the collapse loop, with bends, a multiplier and locks, must record under Burst what it records as managed code.
        [Test]
        public void TheCollapseLoop_RecordsUnderBurstWhatItRecordsAsManagedCode()
        {
            var b = Decimation.BendFixtures.SkinnedTube(40, 36, 0.05f, 0f, 0.03f);
            var mult = new float[b.VertexCount];
            var locked = new byte[b.VertexCount];
            for (var v = 0; v < b.VertexCount; v++)
            {
                mult[v] = b.Positions[v].x > 0f ? 4f : 1f;
                locked[v] = (byte)(b.Positions[v].z > 0.045f ? 1 : 0);
            }
            using var multArray = new NativeArray<float>(mult, Allocator.Persistent);
            using var lockArray = new NativeArray<byte>(locked, Allocator.Persistent);
            var protection = new Ryan6Vrc.MeshOptimizer.Core.Decimation.VertexProtection { CostMultiplier = multArray, Locked = lockArray };
            // Managed code by the job system's switch, which lives and dies with this process. Burst's own option would save an
            // Editor preference every Editor of the user's on the machine reads, so a run killed before the restore would leave
            // them all without Burst.
            var held = JobsUtility.JobCompilerEnabled;
            Assume.That(held, "Burst is off in this Editor, so there is no compiled code to compare");
            Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence burst = default, managed = default;
            try
            {
                burst = Record(b, protection);
                JobsUtility.JobCompilerEnabled = false;
                managed = Record(b, protection);
            }
            finally
            {
                JobsUtility.JobCompilerEnabled = held;
            }
            try
            {
                AssertAlike(burst, managed, "the tube");
            }
            finally
            {
                burst.Dispose();
                managed.Dispose();
            }
        }

        /// <summary>
        /// Burst and managed code round some sums differently, which can swap two collapses of near-equal cost, so the records
        /// may part; every step's error level, a running maximum, stays within a hundredth of the managed one or a micrometre.
        /// </summary>
        static void AssertAlike(in Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence burst, in Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence managed, string name)
        {
            Assert.That(managed.Steps[managed.Length - 1].Error, Is.GreaterThan(1e-4f), $"{name}: the sequence costs something to compare");
            Assert.That(burst.Length, Is.EqualTo(managed.Length).Within(managed.Length / 100), $"{name}: steps");
            for (var s = 0; s < math.min(burst.Length, managed.Length); s++)
            {
                var m = managed.Steps[s].Error;
                Assert.That(burst.Steps[s].Error, Is.EqualTo(m).Within(m * 0.01f + 1e-6f), $"{name}: step {s}'s error");
            }
        }

        /// <summary>Step for step and bit for bit the same sequence: every error level, triangle count and record.</summary>
        static void AssertSame(in Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence x, in Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence y, string name)
        {
            Assert.That(y.Length, Is.EqualTo(x.Length), $"{name}: steps");
            for (var s = 0; s < x.Length; s++)
            {
                Assert.That(math.asuint(y.Steps[s].Error), Is.EqualTo(math.asuint(x.Steps[s].Error)), $"{name} step {s}: error");
                Assert.That(y.Steps[s].TriangleCount, Is.EqualTo(x.Steps[s].TriangleCount), $"{name} step {s}: triangles");
            }
            Assert.That(y.Records.ToArray(), Is.EqualTo(x.Records.ToArray()), $"{name}: records");
        }

        /// <summary>Three tubes a millimetre or less apart over a joint, with the rig they are weighted to.</summary>
        static Decimation.LayerFixtures.Scene BentLayers()
        {
            var bent = new Decimation.LayerFixtures.Scene();
            bent.Add(Decimation.BendFixtures.SkinnedTube(40, 36, 0.05f, 0f, 0.03f));
            bent.Add(Decimation.BendFixtures.SkinnedTube(36, 32, 0.0508f, 0.5f, 0.03f));
            bent.Add(Decimation.BendFixtures.SkinnedTube(30, 30, 0.0495f, 0.25f, 0.02f));
            bent.BuildSurface();
            bent.Rig = Decimation.BendFixtures.Rig();
            return bent;
        }

        /// <summary>A layer sloping through the skin, so the two cross along a ring.</summary>
        static Decimation.LayerFixtures.Scene SlopedCrossing()
        {
            var crossing = new Decimation.LayerFixtures.Scene();
            crossing.Add(Decimation.LayerFixtures.SkinTube());
            crossing.Add(Decimation.LayerFixtures.LayerTube((a, y) => Decimation.LayerFixtures.Skin(a, y) + math.lerp(-0.0015f, 0.0015f, y / Decimation.LayerFixtures.Length)));
            crossing.BuildSurface();
            return crossing;
        }

        // The loop with the clearance rule on, over the scenes the side-by-side test uses: near layers over a joint, measured in
        // bends, and a crossing, where the rule compares crossing lengths Burst and managed code round differently. It runs with a
        // crossing allowance of 1e-7 m. At the default, 1e-9, the two paths part: where a collapse leaves a crossing as it was, the
        // rule compares two sums of segment lengths equal but for rounding, the paths put that margin apart by up to about 1.5e-8 m,
        // and at step 1,381 of the crossing scene's skin Burst refuses a collapse managed code makes, so from there the two record
        // different meshes. At 1e-7 every crossing check decides alike, and the crossing scene must record bit for bit the same on
        // both paths. The bent layers' records part among collapses of near-equal cost with the rule on or off, from rounding in the
        // bends rather than the crossing check, so they are held only to the comparison above.
        const float CrossingAllowance = 1e-7f;

        [Test]
        public void TheCollapseLoop_WithTheClearanceRule_RecordsUnderBurstWhatItRecordsAsManagedCode()
        {
            Ryan6Vrc.MeshOptimizer.Core.Decimation.DecimationSettings Allowed(Ryan6Vrc.MeshOptimizer.Core.Decimation.DecimationSettings s)
            {
                s.Clearance.CrossingAllowance = CrossingAllowance;
                return s;
            }
            using var bent = BentLayers();
            using var crossing = SlopedCrossing();
            var held = JobsUtility.JobCompilerEnabled;
            Assume.That(held, "Burst is off in this Editor, so there is no compiled code to compare");
            foreach (var (scene, label, same) in new[] { (bent, "bent layers", false), (crossing, "crossing", true) })
                for (var m = 0; m < scene.Meshes.Count; m++)
                {
                    Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence burst = default, managed = default;
                    try
                    {
                        Ryan6Vrc.MeshOptimizer.Core.Clearance.ClearanceStats burstStats, managedStats;
                        try
                        {
                            burst = scene.Decimate(m, true, out burstStats, Allowed);
                            JobsUtility.JobCompilerEnabled = false;
                            managed = scene.Decimate(m, true, out managedStats, Allowed);
                        }
                        finally
                        {
                            JobsUtility.JobCompilerEnabled = held;
                        }
                        TestContext.WriteLine($"{label} mesh {m}: {burst.Length} steps under Burst, {managed.Length} managed; refused {burstStats.Refused} and {managedStats.Refused}; last error {burst.Steps[burst.Length - 1].Error * 1000:0.0000} and {managed.Steps[managed.Length - 1].Error * 1000:0.0000} mm");
                        Assert.That(burstStats.Refused, Is.GreaterThan(0), $"{label} mesh {m}: the rule acts");
                        AssertAlike(burst, managed, $"{label} mesh {m}");
                        if (same) AssertSame(burst, managed, $"{label} mesh {m}");
                    }
                    finally
                    {
                        burst.Dispose();
                        managed.Dispose();
                    }
                }
        }

        // Recordings scheduled together run side by side on worker threads, reading one clearance surface. Each must record,
        // step for step and bit for bit, what it records alone: against layers and bends, and against a crossing.
        [Test]
        public void RecordingsScheduledTogether_EachRecordWhatTheyRecordAlone()
        {
            using var bent = BentLayers();
            using var crossing = SlopedCrossing();

            foreach (var scene in new[] { bent, crossing })
            {
                var settings = Ryan6Vrc.MeshOptimizer.Core.Decimation.DecimationSettings.Default;
                settings.Clearance.Enabled = true;
                var alone = new List<Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence>();
                var together = new List<Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence>();
                var pending = new List<Ryan6Vrc.MeshOptimizer.Core.Decimation.PendingSequence>();
                try
                {
                    for (var m = 0; m < scene.Meshes.Count; m++) alone.Add(scene.Decimate(m, true, out _));
                    for (var m = 0; m < scene.Meshes.Count; m++)
                        pending.Add(Ryan6Vrc.MeshOptimizer.Core.Decimation.Decimator.Schedule(scene.Meshes[m], default, settings, default,
                            new Ryan6Vrc.MeshOptimizer.Core.Clearance.ClearanceInput { Surface = scene.Surface, TriangleLayer = scene.Layers[m] }, scene.Rig));
                    foreach (var p in pending) together.Add(p.Complete(Allocator.Persistent, out _));
                    for (var m = 0; m < alone.Count; m++)
                    {
                        var x = alone[m];
                        var y = together[m];
                        Assert.That(x.Length, Is.GreaterThan(100), $"mesh {m} decimates");
                        AssertSame(x, y, $"mesh {m}");
                    }
                }
                finally
                {
                    foreach (var p in pending) p.Dispose();
                    foreach (var q in alone) q.Dispose();
                    foreach (var q in together) q.Dispose();
                }
            }
        }

        static Ryan6Vrc.MeshOptimizer.Core.Decimation.CollapseSequence Record(Decimation.MeshBuilder b, in Ryan6Vrc.MeshOptimizer.Core.Decimation.VertexProtection protection)
        {
            var mesh = b.Build();
            var rig = Decimation.BendFixtures.Rig();
            try
            {
                return Ryan6Vrc.MeshOptimizer.Core.Decimation.Decimator.Run(mesh, Ryan6Vrc.MeshOptimizer.Core.Decimation.DecimationSettings.Default, protection, default, rig, Allocator.Persistent, out _);
            }
            finally
            {
                mesh.Dispose();
                rig.Dispose();
            }
        }
    }
}
