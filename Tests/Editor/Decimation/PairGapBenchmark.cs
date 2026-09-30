using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Ryan6Vrc.MeshOptimizer.Tests.Integration;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Decimation.LayerFixtures;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// What reading the pair's gap costs in time, on the two scenes it is priced on: four concentric tubes, every layer
    /// with a nearer layer beyond, and the recording tests' avatar. The median of several runs, under Burst. A
    /// measurement: a whole gate run leaves out every fixture named *Benchmark; run it by filter.
    /// </summary>
    public class PairGapBenchmark
    {
        const int Runs = 7;

        static double Median(List<double> xs) => xs.OrderBy(x => x).ElementAt(xs.Count / 2);

        [Test, Explicit("a measurement, not a check; run by name")]
        public void FourTubes_SurfaceAndDecimation()
        {
            var build = new List<double>();
            var decimate = new List<double>();
            var perMesh = new List<double>[4];
            for (var m = 0; m < perMesh.Length; m++) perMesh[m] = new List<double>();
            var counts = "";
            for (var run = 0; run <= Runs; run++)
            {
                using var scene = new Scene();
                scene.Add(SkinTube());
                scene.Add(LayerTube(Over(0.0002f)));
                scene.Add(Tube(64, 40, Length, Over(0.0032f)));
                scene.Add(LayerTube(Over(0.0037f)));
                var sw = Stopwatch.StartNew();
                scene.BuildSurface();
                var tb = sw.Elapsed.TotalMilliseconds;
                var times = new double[scene.Meshes.Count];
                counts = "";
                for (var m = 0; m < scene.Meshes.Count; m++)
                {
                    sw.Restart();
                    scene.Decimate(m, true, out var stats).Dispose();
                    times[m] = sw.Elapsed.TotalMilliseconds;
                    counts += $" {stats.Refused}/{stats.RefusedByPairGap}";
                }
                var td = times.Sum();
                // The first run compiles and warms; it is not counted.
                if (run == 0) continue;
                for (var m = 0; m < times.Length; m++) perMesh[m].Add(times[m]);
                build.Add(tb);
                decimate.Add(td);
            }
            TestContext.WriteLine($"four tubes (Burst {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled}): surface {Median(build):F1} ms, decimation {Median(decimate):F1} ms (median of {Runs}; runs {string.Join(", ", decimate.Select(x => x.ToString("F0")))}); per mesh {string.Join(", ", perMesh.Select(x => Median(x).ToString("F0")))} ms; refused/by the pair's gap per mesh:{counts}");
        }

        [Test, Explicit("a measurement, not a check; run by name")]
        public void RecordingAvatar_ClearanceAndDecimation()
        {
            var clearance = new List<double>();
            var decimate = new List<double>();
            string refused = "";
            for (var run = 0; run <= Runs; run++)
            {
                using var a = new AvatarFixture();
                var hips = a.Bone("Armature/Hips");
                var chest = a.Bone("Armature/Hips/Chest");
                chest.localPosition = new Vector3(0f, 0.05f, 0f);
                var bones = new[] { hips, chest };
                Mesh Jointed(Mesh m)
                {
                    AvatarFixture.Weight(m, v => v.y < 0.03f ? 0 : 1);
                    return m;
                }
                var body = a.Skinned("Body", Jointed(a.Sphere(0.1f, 48, 96, name: "body")), bones);
                var garment = a.Skinned("Garment", Jointed(a.Sphere(0.101f, 56, 104, 30f, 180f, name: "garment")), bones);
                a.Skinned("Skirt", Jointed(a.Grid(48, 0.005f, name: "skirt")), bones);
                var prop = a.Rigid("Prop", a.Sphere(0.03f, 16, 32, name: "prop"), chest);
                prop.transform.localPosition = new Vector3(0.2f, 0f, 0f);
                var optimizer = a.Optimizer(decimate: true);
                optimizer.alwaysOn.Add(garment);
                optimizer.target = TargetKind.Tolerance;
                optimizer.tolerance = 2f;
                using var prepared = OptimizerPipeline.Prepare(new PipelineInput
                {
                    Root = a.Root.transform,
                    Settings = optimizer,
                    Liveness = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>(),
                });
                refused = string.Join(", ", prepared.Renderers.Select(r => $"{r.Renderer.name} {r.ClearanceRefused}/{r.ClearanceRefusedByPairGap}"));
                if (run == 0) continue;
                clearance.Add(prepared.Times.Clearance);
                decimate.Add(prepared.Times.Decimation);
            }
            TestContext.WriteLine($"recording avatar: clearance stage {Median(clearance):F1} ms, decimation {Median(decimate):F1} ms (median of {Runs}; decimation runs {string.Join(", ", decimate.Select(x => x.ToString("F0")))}); refused/by the pair's gap: {refused}");
        }
    }
}
