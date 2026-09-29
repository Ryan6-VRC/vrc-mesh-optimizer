using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Tests.Decimation;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    /// <summary>
    /// Wall-clock of one configuration on face-, body- and avatar-sized synthetic meshes, and of a whole gate run on a
    /// body-sized skinned renderer. Explicit, for running by name; the headless runner runs Explicit tests in an
    /// unfiltered run too, so each is one timed pass and a few seconds.
    /// </summary>
    public class GateBenchmark
    {
        static (float3[] p, int3[] t) Tube(int segments, int rings, float radius, float length, int ringStep)
        {
            var b = Fixtures.Cylinder(segments, rings, radius, length, false, out var index);
            var t = new List<int3>();
            for (var r = 0; r + ringStep <= rings; r += ringStep)
            for (var s = 0; s < segments; s++)
            {
                var s1 = (s + 1) % segments;
                t.Add(new int3(index[s, r], index[s, r + ringStep], index[s1, r + ringStep]));
                t.Add(new int3(index[s, r], index[s1, r + ringStep], index[s1, r]));
            }
            return (b.Positions.ToArray(), t.ToArray());
        }

        static void Time(string what, float3[] pa, int3[] ta, float3[] pb, int3[] tb, float spacing)
        {
            var settings = SurfaceDistanceSettings.Default;
            settings.Spacing = spacing;
            var sw = Stopwatch.StartNew();
            var r = SurfaceDistanceTests.Measure(pa, ta, pb, tb, settings: settings);
            var best = sw.Elapsed.TotalMilliseconds;
            var line = $"{what}, {ta.Length} vs {tb.Length} tris, spacing {spacing * 1000:0.#} mm: {best:0} ms, " +
                       $"{r.AToB.Stats.Samples + r.BToA.Stats.Samples} samples, max {r.Combined.Max * 1000:0.###} mm";
            TestContext.WriteLine(line);
            UnityEngine.Debug.Log("[benchmark] " + line);
        }

        [Test, Explicit("a measurement, not a check; run by name")]
        public void OneConfiguration_FaceBodyAvatar()
        {
            // Every optimized triangle differs from the source's, so every sample is searched for: the worst case.
            var fine = Fixtures.Icosphere(5, 0.09f);
            var coarse = Fixtures.Icosphere(4, 0.09f);
            var (body, bodyTris) = Tube(150, 90, 0.15f, 1.5f, 1);
            var (_, bodyHalf) = Tube(150, 90, 0.15f, 1.5f, 2);
            var (avatar, avatarTris) = Tube(350, 200, 0.3f, 1.6f, 1);
            var (_, avatarHalf) = Tube(350, 200, 0.3f, 1.6f, 2);
            // The first call compiles the jobs; time the ones after it.
            SurfaceDistanceTests.Measure(coarse.Positions.ToArray(), coarse.Triangles.ToArray(), coarse.Positions.ToArray(), coarse.Triangles.ToArray());
            const float spacing = 0.002f;
            Time("face", fine.Positions.ToArray(), fine.Triangles.ToArray(), coarse.Positions.ToArray(), coarse.Triangles.ToArray(), spacing);
            Time("body", body, bodyTris, body, bodyHalf, spacing);
            Time("avatar", avatar, avatarTris, avatar, avatarHalf, spacing);
            // A decimation that leaves most triangles as they were: only the changed band is searched.
            var band = bodyTris.Where((_, i) => i / 300 < 40 || i / 300 >= 50).ToList();
            for (var r = 40; r < 50; r += 2)
            for (var s = 0; s < 150; s++)
            {
                var s1 = (s + 1) % 150;
                band.Add(new int3(r * 150 + s, (r + 2) * 150 + s, (r + 2) * 150 + s1));
                band.Add(new int3(r * 150 + s, (r + 2) * 150 + s1, r * 150 + s1));
            }
            Time("body, a tenth changed", body, bodyTris, body, band.ToArray(), spacing);
        }

        [Test, Explicit("a measurement, not a check; run by name")]
        public void WholeRun_BodySizedSkinnedRenderer_ThreeKeysOnePose()
        {
            using var scene = new GateScene();
            var root = scene.Node("Avatar", null, Vector3.zero);
            var lower = scene.Node("Lower", root, Vector3.zero);
            var upper = scene.Node("Upper", lower, new Vector3(0, 0.75f, 0));
            var tube = new Tube(Vector3.zero, Vector3.up, 1.5f, 0.15f, 150, 90);
            var source = scene.Track(tube.Mesh("body", r => (tube.AlongAxis(r) - 0.6f) / 0.3f));
            for (var k = 0; k < 3; k++)
            {
                var d = new Vector3[source.vertexCount];
                for (var s = 0; s < tube.Segments; s++) d[tube.Index(s, 5 + 30 * k)] = Vector3.forward * 0.01f;
                source.AddBlendShapeFrame($"key{k}", 100f, d, null, null);
            }
            var renderer = scene.Skin(scene.Node("Body", root, Vector3.zero), source, lower, upper);
            var optimized = scene.Optimized(source, tube.TrianglesBetween(tube.AllRings().Where(r => r % 2 == 0 || r == tube.Rings).ToArray()));
            var liveness = Enumerable.Repeat(ShapeKeyLiveness.Live(1f), 3).ToArray();
            var poses = new GatePose[] { new BoneRotationPose("bent", ("Lower/Upper", Quaternion.Euler(0, 0, 60))) };

            var sw = Stopwatch.StartNew();
            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, poses, SurfaceDistanceSettings.Default);
            var ms = sw.Elapsed.TotalMilliseconds;
            var line = $"whole run, {source.triangles.Length / 3} vs {optimized.triangles.Length / 3} tris, {report.Configurations.Count} configurations: " +
                       $"{ms:0} ms, {ms / report.Configurations.Count:0} ms each";
            TestContext.WriteLine(line);
            UnityEngine.Debug.Log("[benchmark] " + line);
        }
    }
}
