using System.Diagnostics;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// What bends cost on a limb-sized mesh: about 100,000 triangles on a rippled tube skinned along a chain of bones,
    /// every vertex near a joint, decimated through its whole sequence with and without the rig. Explicit, so the gate
    /// skips it; run it by name.
    /// </summary>
    public class BendBenchmark
    {
        const float Length = 1.5f;

        [Test, Explicit("a measurement, not a check; run by name")]
        public void OneHundredThousandTriangles([Values(30, 300)] int bones)
        {
            var b = LayerFixtures.Tube(128, 400, Length, (a, y) => 0.05f + 0.003f * math.sin(6f * a) * math.sin(2f * math.PI * y / 0.08f));
            var spacing = Length / bones;
            for (var v = 0; v < b.VertexCount; v++)
            {
                // Bone i sits at i * spacing; each joint's weights ramp over most of the space to the next.
                var t = b.Positions[v].y / spacing;
                var i = math.clamp((int)math.floor(t), 0, bones - 1);
                var f = math.smoothstep(0.1f, 0.9f, t - i);
                if (i == bones - 1) f = 0f;
                b.SetWeights(v, (i, 1f - f), (i + 1 < bones ? i + 1 : i, f));
            }
            var mesh = b.Build();
            var a = Allocator.Persistent;
            var parent = new int[bones];
            var position = new float3[bones];
            var axis = new float3[bones];
            var right = new float3[bones];
            for (var i = 0; i < bones; i++)
            {
                parent[i] = i - 1;
                position[i] = new float3(0f, i * spacing, 0f);
                axis[i] = new float3(0f, 1f, 0f);
                right[i] = new float3(1f, 0f, 0f);
            }
            var rig = new BendRig
            {
                Parent = new NativeArray<int>(parent, a),
                Position = new NativeArray<float3>(position, a),
                Axis = new NativeArray<float3>(axis, a),
                Right = new NativeArray<float3>(right, a),
            };
            try
            {
                var settings = DecimationSettings.Default;
                foreach (var withRig in new[] { false, true, false, true })
                {
                    var sw = Stopwatch.StartNew();
                    using var seq = Decimator.Run(mesh, settings, default, default, withRig ? rig : default, a, out _);
                    var seconds = sw.Elapsed.TotalSeconds;
                    var cut = Fixtures.CutAtError(seq, 0.001f);
                    TestContext.WriteLine($"{bones} bones, {(withRig ? "bends" : "no bends")}: {seconds:F3} s for the whole sequence of {mesh.TriangleCount} triangles; {seq.TriangleCountAt(cut)} at 1 mm");
                }

                // Memory, stage by stage, with the rig.
                using var topology = MeshTopology.Build(mesh, settings.Topology, a);
                using var bends = Bends.Build(mesh, rig, settings.Bend, a);
                var bank = QuadricBank.Build(mesh, topology, settings, bends, a);
                var bankBytes = bank.ByteSize;
                var entries = bank.Sparse.Length;
                CollapseStats stats;
                try
                {
                    using var seq = Decimator.Collapse(mesh, topology, ref bank, settings, default, default, default, bends, a, out stats);
                }
                finally
                {
                    bank.Dispose();
                }
                TestContext.WriteLine($"{bones} bones: {bends.FrameCount} bends, {entries} bend quadrics at build over {topology.WorkingVertexCount} vertices, bank {bankBytes / 1e6:F1} MB, pool peak {stats.PeakPoolBytes / 1e6:F1} MB");
            }
            finally
            {
                mesh.Dispose();
                rig.Parent.Dispose();
                rig.Position.Dispose();
                rig.Axis.Dispose();
                rig.Right.Dispose();
            }
        }
    }
}
