using System.Diagnostics;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// Wall-clock of each stage on a face-sized synthetic mesh: about 50,000 triangles, 150 sparse live frames,
    /// four bones. Explicit, so the gate skips it; run it by name.
    /// </summary>
    public class DecimationBenchmark
    {
        [Test, Explicit("a measurement, not a check; run by name")]
        public void FiftyThousandTriangles_OneHundredFiftyFrames()
        {
            const int n = 158;
            var b = Fixtures.Grid(n, n, 0.5f, 0.5f, out _);
            for (var v = 0; v < b.VertexCount; v++)
            {
                var p = b.Positions[v];
                b.Positions[v] = new float3(p.x, p.y, 0.01f * math.sin(p.x * 25f) * math.cos(p.y * 19f));
                var t = math.saturate((p.x + 0.25f) * 2f) * 3f;
                var bone = (int)math.min(t, 2.999f);
                var f = t - bone;
                b.SetWeights(v, (bone, 1f - f), (bone + 1, f));
            }
            var rng = new Random(12345);
            for (var fi = 0; fi < 150; fi++)
            {
                var frame = b.AddFrame();
                var c = rng.NextFloat2(-0.22f, 0.22f);
                var dir = math.normalize(rng.NextFloat3Direction() + new float3(0, 0, 1));
                for (var v = 0; v < b.VertexCount; v++)
                {
                    var d2 = math.lengthsq(b.Positions[v].xy - c);
                    var h = 0.006f * math.exp(-d2 / (0.012f * 0.012f));
                    if (h > 1e-5f) frame[v] = dir * h;
                }
            }
            var mesh = b.Build();
            using var memory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
            try
            {
                var settings = DecimationSettings.Default;
                settings.TargetTriangleCount = mesh.TriangleCount / 4;
                var baseline = memory.CurrentValue;

                var sw = Stopwatch.StartNew();
                using var topology = MeshTopology.Build(mesh, settings.Topology, Allocator.Persistent);
                var tTopology = sw.Elapsed.TotalSeconds;
                var afterTopology = memory.CurrentValue;

                sw.Restart();
                var bank = QuadricBank.Build(mesh, topology, settings, Allocator.Persistent);
                var tBank = sw.Elapsed.TotalSeconds;
                var afterBank = memory.CurrentValue;
                var bankBytes = bank.ByteSize;
                var bankEntries = bank.Sparse.Length;

                sw.Restart();
                CollapseSequence seq;
                CollapseStats stats;
                try
                {
                    seq = Decimator.Collapse(mesh, topology, ref bank, settings, default, Allocator.Persistent, out stats);
                }
                finally
                {
                    bank.Dispose();
                }
                var tCollapse = sw.Elapsed.TotalSeconds;
                using (seq)
                {
                    var deltas = mesh.Deltas.Length;
                    TestContext.WriteLine($"mesh: {mesh.TriangleCount} tris, {mesh.VertexCount} verts, {mesh.Frames.Length} frames, {deltas} deltas");
                    TestContext.WriteLine($"topology {tTopology:F3} s, quadric bank {tBank:F3} s, collapse to {seq.TriangleCountAt(seq.Length)} tris {tCollapse:F3} s ({seq.Length} steps, error {seq.ErrorAt(seq.Length) * 1000:F3} mm)");
                    TestContext.WriteLine($"bank {bankBytes / 1e6:F1} MB ({bankEntries} frame quadrics at build), topology {topology.ByteSize / 1e6:F1} MB, pool peak {stats.PeakPoolBytes / 1e6:F1} MB, blocked pops {stats.Blocked}");
                    TestContext.WriteLine($"total used memory: +{(afterTopology - baseline) / 1e6:F1} MB after topology, +{(afterBank - baseline) / 1e6:F1} MB after the bank");
                    UnityEngine.Debug.Log($"[benchmark] topology {tTopology:F3} s, bank {tBank:F3} s, collapse {tCollapse:F3} s, bank {bankBytes / 1e6:F1} MB, pool peak {stats.PeakPoolBytes / 1e6:F1} MB");
                    Assert.That(seq.TriangleCountAt(seq.Length), Is.LessThanOrEqualTo(settings.TargetTriangleCount));
                }
            }
            finally
            {
                mesh.Dispose();
            }
        }
    }
}
