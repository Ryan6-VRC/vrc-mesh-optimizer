using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>Layered synthetic surfaces, and the crossing between two surfaces as an independent measure.</summary>
    internal static class LayerFixtures
    {
        /// <summary>
        /// An open tube along +Y from 0 to <paramref name="length"/>, wrapping in angle, its radius a function of angle
        /// and height. <paramref name="phase"/> turns the columns, so two tubes of one radius function need not share
        /// vertex angles.
        /// </summary>
        public static MeshBuilder Tube(int segments, int rings, float length, Func<float, float, float> radius, float phase = 0f, MeshBuilder into = null)
        {
            var b = into ?? new MeshBuilder();
            var index = new int[segments, rings + 1];
            for (var r = 0; r <= rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var angle = 2f * math.PI * (s + phase) / segments;
                var y = length * r / rings;
                var rad = radius(angle, y);
                index[s, r] = b.Add(new float3(rad * math.cos(angle), y, rad * math.sin(angle)), new float2(s / (float)segments, y / length));
            }
            for (var r = 0; r < rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var s1 = (s + 1) % segments;
                b.Tri(index[s, r], index[s, r + 1], index[s1, r + 1]);
                b.Tri(index[s, r], index[s1, r + 1], index[s1, r]);
            }
            return b;
        }

        public const float Radius = 0.05f;
        public const float Length = 0.2f;

        /// <summary>Skin: a tube rippled in angle and height, so it has hollows a decimated surface bridges outward and crests it cuts inward.</summary>
        public static float Skin(float angle, float y) => Radius + 0.003f * math.sin(6f * angle) * math.sin(2f * math.PI * y / 0.08f);

        /// <summary>A layer over <see cref="Skin"/> at <paramref name="gap"/> metres, radially.</summary>
        public static Func<float, float, float> Over(float gap) => (a, y) => Skin(a, y) + gap;

        public static MeshBuilder SkinTube() => Tube(64, 40, Length, Skin);
        public static MeshBuilder LayerTube(Func<float, float, float> radius) => Tube(56, 36, Length, radius, 0.5f);

        /// <summary>Each triangle's layer, all <paramref name="layer"/>.</summary>
        public static NativeArray<int> Uniform(int triangles, int layer)
        {
            var a = new NativeArray<int>(triangles, Allocator.Persistent);
            for (var i = 0; i < a.Length; i++) a[i] = layer;
            return a;
        }

        /// <summary>
        /// Several meshes, each with its triangles' layers, and the clearance surface over all of them; disposes all.
        /// </summary>
        internal sealed class Scene : IDisposable
        {
            public readonly List<MeshBuilder> Builders = new List<MeshBuilder>();
            public readonly List<EvalMesh> Meshes = new List<EvalMesh>();
            public readonly List<NativeArray<int>> Layers = new List<NativeArray<int>>();
            /// <summary>Per mesh, which triangles the surface holds; null holds all.</summary>
            public readonly List<bool[]> Kept = new List<bool[]>();
            public ClearanceSurface Surface;
            readonly List<IDisposable> _owned = new List<IDisposable>();

            /// <summary>Adds a mesh whose connected pieces are its layers, numbered after every earlier mesh's.</summary>
            public int Add(MeshBuilder b, bool[] kept = null)
            {
                var mesh = b.Build();
                var next = Layers.Count == 0 ? 0 : Layers.Max(l => l.Length == 0 ? -1 : l.Max()) + 1;
                var layers = Core.Clearance.Layers.Pieces(mesh.Positions, mesh.Triangles, Allocator.Persistent, out _);
                for (var i = 0; i < layers.Length; i++) layers[i] += next;
                Builders.Add(b);
                Meshes.Add(mesh);
                Layers.Add(layers);
                Kept.Add(kept);
                return Meshes.Count - 1;
            }

            public void BuildSurface()
            {
                var sources = new List<ClearanceLayerSource>();
                for (var i = 0; i < Meshes.Count; i++)
                {
                    var tris = Meshes[i].Triangles;
                    var layers = Layers[i];
                    if (Kept[i] != null)
                    {
                        var keep = Enumerable.Range(0, tris.Length).Where(t => Kept[i][t]).ToArray();
                        var kt = new NativeArray<int3>(keep.Select(t => tris[t]).ToArray(), Allocator.Persistent);
                        var kl = new NativeArray<int>(keep.Select(t => layers[t]).ToArray(), Allocator.Persistent);
                        _owned.Add(kt);
                        _owned.Add(kl);
                        tris = kt;
                        layers = kl;
                    }
                    sources.Add(new ClearanceLayerSource { Positions = Meshes[i].Positions, Triangles = tris, TriangleLayer = layers });
                }
                Surface = ClearanceSurface.Build(sources, Core.Decimation.ClearanceSettings.Default.Reach, Allocator.Persistent);
            }

            public CollapseSequence Decimate(int mesh, bool rule, out ClearanceStats stats, Func<DecimationSettings, DecimationSettings> tweak = null)
            {
                var s = DecimationSettings.Default;
                s.Clearance.Enabled = rule;
                if (tweak != null) s = tweak(s);
                return Decimator.Run(Meshes[mesh], s, default, new ClearanceInput { Surface = Surface, TriangleLayer = Layers[mesh] }, Allocator.Persistent, out stats);
            }

            public void Dispose()
            {
                Surface.Dispose();
                foreach (var m in Meshes) m.Dispose();
                foreach (var l in Layers) l.Dispose();
                foreach (var o in _owned) o.Dispose();
            }
        }

        // ------------------------------------------------------------------ crossing

        /// <summary>
        /// The total length of the curves along which two triangle surfaces pass through each other, in metres: the
        /// clipping line a viewer sees as one surface showing through the other. Coplanar overlap is not counted.
        /// </summary>
        public static float CrossingLength(float3[] pa, int3[] ta, float3[] pb, int3[] tb)
        {
            const float cell = 0.005f;
            var grid = new Dictionary<int3, List<int>>();
            for (var j = 0; j < tb.Length; j++)
            {
                var (lo, hi) = Cells(pb[tb[j].x], pb[tb[j].y], pb[tb[j].z], cell);
                for (var x = lo.x; x <= hi.x; x++)
                for (var y = lo.y; y <= hi.y; y++)
                for (var z = lo.z; z <= hi.z; z++)
                {
                    var key = new int3(x, y, z);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(j);
                }
            }
            var total = 0.0;
            var seen = new HashSet<int>();
            for (var i = 0; i < ta.Length; i++)
            {
                float3 a0 = pa[ta[i].x], a1 = pa[ta[i].y], a2 = pa[ta[i].z];
                var (lo, hi) = Cells(a0, a1, a2, cell);
                seen.Clear();
                for (var x = lo.x; x <= hi.x; x++)
                for (var y = lo.y; y <= hi.y; y++)
                for (var z = lo.z; z <= hi.z; z++)
                {
                    if (!grid.TryGetValue(new int3(x, y, z), out var list)) continue;
                    foreach (var j in list)
                        if (seen.Add(j)) total += IntersectionLength(a0, a1, a2, pb[tb[j].x], pb[tb[j].y], pb[tb[j].z]);
                }
            }
            return (float)total;
        }

        static (int3 lo, int3 hi) Cells(float3 a, float3 b, float3 c, float cell) =>
            ((int3)math.floor(math.min(a, math.min(b, c)) / cell), (int3)math.floor(math.max(a, math.max(b, c)) / cell));

        /// <summary>The length of the segment two triangles share, 0 when they do not pass through each other.</summary>
        public static double IntersectionLength(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2)
        {
            var A = new[] { (double3)a0, (double3)a1, (double3)a2 };
            var B = new[] { (double3)b0, (double3)b1, (double3)b2 };
            var nA = math.cross(A[1] - A[0], A[2] - A[0]);
            var nB = math.cross(B[1] - B[0], B[2] - B[0]);
            var dir = math.cross(nA, nB);
            if (math.lengthsq(dir) < 1e-30) return 0;
            dir = math.normalize(dir);
            if (!Interval(A, nB, B[0], dir, out var aLo, out var aHi)) return 0;
            if (!Interval(B, nA, A[0], dir, out var bLo, out var bHi)) return 0;
            return math.max(0, math.min(aHi, bHi) - math.max(aLo, bLo));
        }

        /// <summary>Where triangle t meets the plane (n, o), as an interval along dir; false when it lies to one side.</summary>
        static bool Interval(double3[] t, double3 n, double3 o, double3 dir, out double lo, out double hi)
        {
            lo = double.PositiveInfinity;
            hi = double.NegativeInfinity;
            var d = new double[3];
            for (var k = 0; k < 3; k++) d[k] = math.dot(n, t[k] - o);
            if (d[0] > 0 && d[1] > 0 && d[2] > 0 || d[0] < 0 && d[1] < 0 && d[2] < 0) return false;
            if (d[0] == 0 && d[1] == 0 && d[2] == 0) return false;
            for (var k = 0; k < 3; k++)
            {
                var l = (k + 1) % 3;
                if (d[k] == 0)
                {
                    var p = math.dot(t[k], dir);
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }
                if (d[k] > 0 && d[l] < 0 || d[k] < 0 && d[l] > 0)
                {
                    var x = t[k] + d[k] / (d[k] - d[l]) * (t[l] - t[k]);
                    var p = math.dot(x, dir);
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }
            }
            return lo <= hi;
        }

        /// <summary>Crossing length between two meshes' cuts.</summary>
        public static float Crossing(MeshBuilder a, in CutResult ca, MeshBuilder b, in CutResult cb) =>
            CrossingLength(a.Positions.ToArray(), Fixtures.ToArray(ca.Triangles), b.Positions.ToArray(), Fixtures.ToArray(cb.Triangles));

        public static float Crossing(MeshBuilder a, MeshBuilder b) =>
            CrossingLength(a.Positions.ToArray(), a.Triangles.ToArray(), b.Positions.ToArray(), b.Triangles.ToArray());
    }
}
