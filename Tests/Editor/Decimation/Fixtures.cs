using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>Builds an evaluation mesh in code: synthetic geometry only.</summary>
    internal sealed class MeshBuilder
    {
        public readonly List<float3> Positions = new List<float3>();
        public readonly List<float2> Uvs = new List<float2>();
        public readonly List<int3> Triangles = new List<int3>();
        public readonly List<int> Submesh = new List<int>();
        public readonly List<BoneInfluence[]> Weights = new List<BoneInfluence[]>();
        /// <summary>Live frames: per frame, vertex to delta.</summary>
        public readonly List<Dictionary<int, float3>> Frames = new List<Dictionary<int, float3>>();

        /// <summary>Authored vertex normals, one per vertex, as an artist sets them; null takes the area-weighted face normals.</summary>
        public float3[] Normals;

        public int VertexCount => Positions.Count;

        /// <summary>The normals the built mesh carries: <see cref="Normals"/> normalized, or else the area-weighted face normals.</summary>
        public float3[] VertexNormals()
        {
            if (Normals != null) return Normals.Select(n => math.normalizesafe(n)).ToArray();
            var normals = new float3[Positions.Count];
            foreach (var t in Triangles)
            {
                var n = math.cross(Positions[t.y] - Positions[t.x], Positions[t.z] - Positions[t.x]);
                normals[t.x] += n;
                normals[t.y] += n;
                normals[t.z] += n;
            }
            return normals.Select(n => math.normalizesafe(n)).ToArray();
        }

        public int Add(float3 p, float2 uv = default)
        {
            Positions.Add(p);
            Uvs.Add(uv);
            Weights.Add(new[] { new BoneInfluence { Bone = 0, Weight = 1f } });
            return Positions.Count - 1;
        }

        public void SetWeights(int v, params (int bone, float weight)[] influences)
        {
            Weights[v] = influences.Where(i => i.weight > 0f).OrderByDescending(i => i.weight)
                .Select(i => new BoneInfluence { Bone = i.bone, Weight = i.weight }).ToArray();
        }

        public void Tri(int a, int b, int c, int submesh = 0)
        {
            Triangles.Add(new int3(a, b, c));
            Submesh.Add(submesh);
        }

        public Dictionary<int, float3> AddFrame()
        {
            var f = new Dictionary<int, float3>();
            Frames.Add(f);
            return f;
        }

        public float3[] PositionsIn(int frame)
        {
            var p = Positions.ToArray();
            if (frame < 0) return p;
            foreach (var kv in Frames[frame]) p[kv.Key] += kv.Value;
            return p;
        }

        public EvalMesh Build(bool includeFrames = true)
        {
            var a = Allocator.Persistent;
            var mesh = new EvalMesh
            {
                Positions = new NativeArray<float3>(Positions.ToArray(), a),
                Uv0 = new NativeArray<float2>(Uvs.ToArray(), a),
                Triangles = new NativeArray<int3>(Triangles.ToArray(), a),
                TriangleSubmesh = new NativeArray<int>(Submesh.ToArray(), a),
            };
            mesh.Normals = new NativeArray<float3>(VertexNormals(), a);

            var start = new int[Positions.Count + 1];
            var weights = new List<BoneInfluence>();
            for (var v = 0; v < Positions.Count; v++)
            {
                start[v] = weights.Count;
                weights.AddRange(Weights[v]);
            }
            start[Positions.Count] = weights.Count;
            mesh.WeightStart = new NativeArray<int>(start, a);
            mesh.Weights = new NativeArray<BoneInfluence>(weights.ToArray(), a);

            var frames = new List<ShapeFrame>();
            var deltas = new List<ShapeDelta>();
            if (includeFrames)
            {
                foreach (var f in Frames)
                {
                    frames.Add(new ShapeFrame { DeltaStart = deltas.Count, DeltaCount = f.Count });
                    deltas.AddRange(f.OrderBy(kv => kv.Key).Select(kv => new ShapeDelta { Vertex = kv.Key, Delta = kv.Value }));
                }
            }
            mesh.Frames = new NativeArray<ShapeFrame>(frames.ToArray(), a);
            mesh.Deltas = new NativeArray<ShapeDelta>(deltas.ToArray(), a);
            return mesh;
        }
    }

    internal static class Fixtures
    {
        /// <summary>A flat grid in the XY plane facing +Z, centred on the origin, UVs 0..1.</summary>
        public static MeshBuilder Grid(int nx, int ny, float sizeX, float sizeY, out int[,] index)
        {
            var b = new MeshBuilder();
            index = new int[nx + 1, ny + 1];
            for (var j = 0; j <= ny; j++)
            for (var i = 0; i <= nx; i++)
                index[i, j] = b.Add(new float3((i / (float)nx - 0.5f) * sizeX, (j / (float)ny - 0.5f) * sizeY, 0f), new float2(i / (float)nx, j / (float)ny));
            for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                b.Tri(index[i, j], index[i + 1, j], index[i + 1, j + 1]);
                b.Tri(index[i, j], index[i + 1, j + 1], index[i, j + 1]);
            }
            return b;
        }

        /// <summary>
        /// An open cylinder along +Y from 0 to length, facing outward. With a seam, the first column is
        /// duplicated as a last column at U = 1, so a UV seam runs up one side; without, the ring wraps.
        /// </summary>
        public static MeshBuilder Cylinder(int segments, int rings, float radius, float length, bool seam, out int[,] index)
        {
            var b = new MeshBuilder();
            var columns = seam ? segments + 1 : segments;
            index = new int[columns, rings + 1];
            for (var r = 0; r <= rings; r++)
            for (var s = 0; s < columns; s++)
            {
                // The seam column repeats column 0's angle exactly: sin(2 pi) is not sin(0) in float, and a
                // seam's two sides must coincide bit for bit to weld.
                var angle = 2f * math.PI * (s % segments) / segments;
                var y = length * r / rings;
                index[s, r] = b.Add(new float3(radius * math.cos(angle), y, radius * math.sin(angle)), new float2(s / (float)segments, y / length));
            }
            for (var r = 0; r < rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var s1 = seam ? s + 1 : (s + 1) % segments;
                var a = index[s, r];
                var bb = index[s1, r];
                var c = index[s1, r + 1];
                var d = index[s, r + 1];
                b.Tri(a, d, c);
                b.Tri(a, c, bb);
            }
            return b;
        }

        /// <summary>An icosphere facing outward, UVs a planar projection so they stay continuous.</summary>
        public static MeshBuilder Icosphere(int subdivisions, float radius)
        {
            var t = (1f + math.sqrt(5f)) / 2f;
            var verts = new List<float3>
            {
                new float3(-1, t, 0), new float3(1, t, 0), new float3(-1, -t, 0), new float3(1, -t, 0),
                new float3(0, -1, t), new float3(0, 1, t), new float3(0, -1, -t), new float3(0, 1, -t),
                new float3(t, 0, -1), new float3(t, 0, 1), new float3(-t, 0, -1), new float3(-t, 0, 1),
            };
            var faces = new List<int3>
            {
                new int3(0, 11, 5), new int3(0, 5, 1), new int3(0, 1, 7), new int3(0, 7, 10), new int3(0, 10, 11),
                new int3(1, 5, 9), new int3(5, 11, 4), new int3(11, 10, 2), new int3(10, 7, 6), new int3(7, 1, 8),
                new int3(3, 9, 4), new int3(3, 4, 2), new int3(3, 2, 6), new int3(3, 6, 8), new int3(3, 8, 9),
                new int3(4, 9, 5), new int3(2, 4, 11), new int3(6, 2, 10), new int3(8, 6, 7), new int3(9, 8, 1),
            };
            for (var s = 0; s < subdivisions; s++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int c)
                {
                    var key = ((long)math.min(a, c) << 32) | (uint)math.max(a, c);
                    if (mid.TryGetValue(key, out var m)) return m;
                    verts.Add((verts[a] + verts[c]) * 0.5f);
                    mid[key] = verts.Count - 1;
                    return verts.Count - 1;
                }
                var next = new List<int3>();
                foreach (var f in faces)
                {
                    var ab = Mid(f.x, f.y);
                    var bc = Mid(f.y, f.z);
                    var ca = Mid(f.z, f.x);
                    next.Add(new int3(f.x, ab, ca));
                    next.Add(new int3(f.y, bc, ab));
                    next.Add(new int3(f.z, ca, bc));
                    next.Add(new int3(ab, bc, ca));
                }
                faces = next;
            }
            var b = new MeshBuilder();
            foreach (var v in verts)
            {
                var p = math.normalize(v) * radius;
                b.Add(p, new float2(p.x, p.y) / radius * 0.5f + 0.5f);
            }
            foreach (var f in faces)
            {
                // The icosahedron above winds clockwise seen from outside; flip to face outward.
                b.Tri(f.x, f.z, f.y);
            }
            return b;
        }

        /// <summary>
        /// A one-quad-wide strip, a hair card: its centreline bends through an arc in the XY plane and its
        /// width runs along Z. U is 0 on one long edge and 1 on the other; V runs 0..1 along the card.
        /// </summary>
        public static MeshBuilder Strip(int segments, float arcRadius, float arcAngle, float width, out int[] left, out int[] right)
        {
            var b = new MeshBuilder();
            left = new int[segments + 1];
            right = new int[segments + 1];
            for (var i = 0; i <= segments; i++)
            {
                var v = i / (float)segments;
                var a = arcAngle * v;
                var c = new float3(arcRadius * math.sin(a), arcRadius * (1f - math.cos(a)), 0f);
                left[i] = b.Add(c, new float2(0f, v));
                right[i] = b.Add(c + new float3(0, 0, width), new float2(1f, v));
            }
            for (var i = 0; i < segments; i++)
            {
                b.Tri(left[i], left[i + 1], right[i + 1]);
                b.Tri(left[i], right[i + 1], right[i]);
            }
            return b;
        }

        /// <summary>Linear blend skinning: bone b rotates by rotation[b] about pivot[b].</summary>
        public static float3[] Skin(MeshBuilder b, float3[] positions, float3[] pivot, quaternion[] rotation)
        {
            var result = new float3[positions.Length];
            for (var v = 0; v < positions.Length; v++)
            {
                var p = float3.zero;
                foreach (var inf in b.Weights[v])
                    p += inf.Weight * (math.mul(rotation[inf.Bone], positions[v] - pivot[inf.Bone]) + pivot[inf.Bone]);
                result[v] = p;
            }
            return result;
        }

        public static int3[] ToArray(NativeList<int3> list) => list.AsArray().ToArray();

        /// <summary>The source vertices some triangle of the cut uses.</summary>
        public static HashSet<int> Kept(in CutResult cut) => new HashSet<int>(ToArray(cut.Triangles).SelectMany(t => new[] { t.x, t.y, t.z }));

        /// <summary>
        /// Distance from each reference point to the nearest point of a triangle surface: the mean and the
        /// largest. The surface is the decimated mesh in some configuration, the points the source vertices in
        /// the same configuration, so this is how far the surface strayed from what it replaced.
        /// </summary>
        public static (float mean, float max) SurfaceDistance(float3[] points, IEnumerable<int> which, float3[] surface, int3[] triangles)
        {
            var sum = 0.0;
            var max = 0f;
            var n = 0;
            foreach (var i in which)
            {
                var best = float.PositiveInfinity;
                foreach (var t in triangles)
                    best = math.min(best, math.lengthsq(points[i] - ClosestPoint(points[i], surface[t.x], surface[t.y], surface[t.z])));
                var d = math.sqrt(best);
                sum += d;
                max = math.max(max, d);
                n++;
            }
            return ((float)(sum / math.max(n, 1)), max);
        }

        /// <summary>The closest point on triangle abc to p (Ericson, Real-Time Collision Detection, 5.1.5).</summary>
        public static float3 ClosestPoint(float3 p, float3 a, float3 b, float3 c)
        {
            return ClosestPoint(p, a, b, c, out _);
        }

        public static float3 ClosestPoint(float3 p, float3 a, float3 b, float3 c, out float3 bary)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = math.dot(ab, ap);
            var d2 = math.dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) { bary = new float3(1, 0, 0); return a; }
            var bp = p - b;
            var d3 = math.dot(ab, bp);
            var d4 = math.dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) { bary = new float3(0, 1, 0); return b; }
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                var v = d1 / (d1 - d3);
                bary = new float3(1 - v, v, 0);
                return a + v * ab;
            }
            var cp = p - c;
            var d5 = math.dot(ab, cp);
            var d6 = math.dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) { bary = new float3(0, 0, 1); return c; }
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                var w = d2 / (d2 - d6);
                bary = new float3(1 - w, 0, w);
                return a + w * ac;
            }
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            {
                var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
                bary = new float3(0, 1 - w, w);
                return b + w * (c - b);
            }
            var denom = 1f / (va + vb + vc);
            var vv = vb * denom;
            var ww = vc * denom;
            bary = new float3(1 - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        public static void DisposeMesh(ref EvalMesh mesh) => mesh.Dispose();

        /// <summary>Records a whole sequence with default settings, overridden by <paramref name="tweak"/>.</summary>
        public static CollapseSequence Decimate(MeshBuilder b, Func<DecimationSettings, DecimationSettings> tweak = null, bool includeFrames = true, VertexProtection protection = default)
        {
            var mesh = b.Build(includeFrames);
            try
            {
                var s = DecimationSettings.Default;
                if (tweak != null) s = tweak(s);
                return Decimator.Run(mesh, s, protection, Allocator.Persistent);
            }
            finally
            {
                mesh.Dispose();
            }
        }

        /// <summary>The cut that leaves at most <paramref name="triangles"/> triangles, as close as the sequence allows.</summary>
        public static int CutAtCount(in CollapseSequence seq, int triangles)
        {
            var k = 0;
            while (k < seq.Length && seq.TriangleCountAt(k) > triangles) k++;
            return k;
        }

        /// <summary>The deepest cut whose error level stays within <paramref name="tolerance"/> metres.</summary>
        public static int CutAtError(in CollapseSequence seq, float tolerance)
        {
            var k = 0;
            while (k < seq.Length && seq.Steps[k].Error <= tolerance) k++;
            return k;
        }
    }
}
