using System.Diagnostics;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// What bends cost on a limb-sized mesh: about 100,000 triangles on a rippled tube skinned along a chain of thirty
    /// bones, decimated through its whole sequence with and without the rig. Explicit, so the gate skips it; run it by name.
    /// </summary>
    public class BendBenchmark
    {
        const int Bones = 30;
        const float Length = 1.5f;

        [Test, Explicit("a measurement, not a check; run by name")]
        public void OneHundredThousandTriangles_ThirtyBones()
        {
            var b = LayerFixtures.Tube(128, 400, Length, (a, y) => 0.05f + 0.003f * math.sin(6f * a) * math.sin(2f * math.PI * y / 0.08f));
            var spacing = Length / Bones;
            for (var v = 0; v < b.VertexCount; v++)
            {
                // Bone i sits at i * spacing; each joint's weights ramp over most of the space to the next.
                var t = b.Positions[v].y / spacing;
                var i = math.clamp((int)math.floor(t), 0, Bones - 1);
                var f = math.smoothstep(0.1f, 0.9f, t - i);
                if (i == Bones - 1) f = 0f;
                b.SetWeights(v, (i, 1f - f), (i + 1 < Bones ? i + 1 : i, f));
            }
            var mesh = b.Build();
            var a = Allocator.Persistent;
            var parent = new int[Bones];
            var position = new float3[Bones];
            var axis = new float3[Bones];
            var right = new float3[Bones];
            for (var i = 0; i < Bones; i++)
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
                    TestContext.WriteLine($"{(withRig ? "bends" : "no bends")}: {seconds:F3} s for the whole sequence of {mesh.TriangleCount} triangles; {seq.TriangleCountAt(cut)} at 1 mm");
                }
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
