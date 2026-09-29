using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    /// <summary>Builds an <see cref="EvalMesh"/> from synthetic geometry, in avatar-root space, one bone per vertex unless given more.</summary>
    internal sealed class MeshBuilder
    {
        readonly List<float3> pos = new List<float3>();
        readonly List<float3> nrm = new List<float3>();
        readonly List<float2> uv = new List<float2>();
        readonly List<BoneInfluence[]> weights = new List<BoneInfluence[]>();
        readonly List<int3> tris = new List<int3>();
        readonly List<int> sub = new List<int>();
        readonly List<List<ShapeDelta>> frames = new List<List<ShapeDelta>>();
        readonly List<int> frameConfiguration = new List<int>();

        public int VertexCount => pos.Count;

        /// <summary>Vertices some triangle uses: the ones a verdict and a decline count are about.</summary>
        public int ReferencedVertexCount
        {
            get
            {
                var used = new HashSet<int>();
                foreach (var t in tris) { used.Add(t.x); used.Add(t.y); used.Add(t.z); }
                return used.Count;
            }
        }
        public int TriangleCount => tris.Count;
        public float3 Position(int v) => pos[v];
        public int3 Triangle(int t) => tris[t];

        public int AddVertex(float3 p, float3 n, float2 texcoord, params BoneInfluence[] influences)
        {
            pos.Add(p);
            nrm.Add(n);
            uv.Add(texcoord);
            weights.Add(influences);
            return pos.Count - 1;
        }

        public void AddTriangle(int a, int b, int c, int submesh = 0)
        {
            tris.Add(new int3(a, b, c));
            sub.Add(submesh);
        }

        /// <summary>
        /// A new live frame; add one delta per moved vertex, in ascending vertex order. A nonzero
        /// <paramref name="configuration"/> moves it together with every other mesh's frame of that id.
        /// </summary>
        public List<ShapeDelta> AddFrame(int configuration = 0)
        {
            var f = new List<ShapeDelta>();
            frames.Add(f);
            frameConfiguration.Add(configuration);
            return f;
        }

        public static BoneInfluence[] On(int bone) => new[] { new BoneInfluence { Bone = bone, Weight = 1f } };

        /// <summary>
        /// A band of a UV sphere between polar angles <paramref name="theta0"/> and <paramref name="theta1"/>
        /// (degrees from +Y), wound clockwise seen from outside, or from inside when <paramref name="inward"/>.
        /// UV is (azimuth fraction, 1 - polar degrees / 360), so v runs from 1 at the top pole to 0.5 at the
        /// bottom and a wrapped bilinear footprint at the bottom never reaches the top rows. Returns its first vertex.
        /// </summary>
        public int SphereBand(float3 centre, float radius, float theta0, float theta1, int rings, int segments,
                              BoneInfluence[] influences, bool inward = false, int submesh = 0)
        {
            var first = pos.Count;
            for (var i = 0; i <= rings; i++)
            {
                var theta = math.radians(math.lerp(theta0, theta1, (float)i / rings));
                for (var j = 0; j <= segments; j++)
                {
                    var phi = 2f * math.PI * j / segments;
                    var dir = new float3(math.sin(theta) * math.cos(phi), math.cos(theta), math.sin(theta) * math.sin(phi));
                    AddVertex(centre + radius * dir, inward ? -dir : dir,
                              new float2((float)j / segments, 1f - math.degrees(theta) / 360f), influences);
                }
            }
            for (var i = 0; i < rings; i++)
            {
                var topPole = i == 0 && theta0 <= 0f;
                var bottomPole = i == rings - 1 && theta1 >= 180f;
                for (var j = 0; j < segments; j++)
                {
                    var a = first + i * (segments + 1) + j;
                    int b = a + 1, c = a + segments + 1, d = c + 1;
                    if (!topPole) { if (inward) AddTriangle(a, c, b, submesh); else AddTriangle(a, b, c, submesh); }
                    if (!bottomPole) { if (inward) AddTriangle(b, c, d, submesh); else AddTriangle(b, d, c, submesh); }
                }
            }
            return first;
        }

        /// <summary>Rotates every vertex from <paramref name="first"/> on about <paramref name="pivot"/>: a bone swinging.</summary>
        public void Rotate(quaternion q, float3 pivot, int first = 0)
        {
            for (var v = first; v < pos.Count; v++)
            {
                pos[v] = pivot + math.mul(q, pos[v] - pivot);
                nrm[v] = math.mul(q, nrm[v]);
            }
        }

        public void Sphere(float3 centre, float radius, int rings, int segments, BoneInfluence[] influences,
                           bool inward = false, int submesh = 0) =>
            SphereBand(centre, radius, 0f, 180f, rings, segments, influences, inward, submesh);

        /// <summary>
        /// A horizontal grid at height <paramref name="y"/> facing +Y, cells <paramref name="cell"/> wide over
        /// [x0, x1] by [z0, z1], less every cell <paramref name="skip"/> names by its centre.
        /// </summary>
        public void Grid(float y, float x0, float x1, float z0, float z1, float cell, BoneInfluence[] influences,
                         Func<float, float, bool> skip = null)
        {
            var nx = (int)math.round((x1 - x0) / cell);
            var nz = (int)math.round((z1 - z0) / cell);
            var first = pos.Count;
            for (var i = 0; i <= nx; i++)
                for (var k = 0; k <= nz; k++)
                    AddVertex(new float3(x0 + i * cell, y, z0 + k * cell), new float3(0, 1, 0), new float2((float)i / nx, (float)k / nz), influences);
            for (var i = 0; i < nx; i++)
                for (var k = 0; k < nz; k++)
                {
                    if (skip != null && skip(x0 + (i + 0.5f) * cell, z0 + (k + 0.5f) * cell)) continue;
                    var p00 = first + i * (nz + 1) + k;
                    int p01 = p00 + 1, p10 = p00 + nz + 1, p11 = p10 + 1;
                    AddTriangle(p00, p01, p10);
                    AddTriangle(p10, p01, p11);
                }
        }

        public EvalMesh Build()
        {
            const Allocator a = Allocator.Persistent;
            var m = new EvalMesh
            {
                Positions = new NativeArray<float3>(pos.ToArray(), a),
                Normals = new NativeArray<float3>(nrm.ToArray(), a),
                Uv0 = new NativeArray<float2>(uv.ToArray(), a),
                Triangles = new NativeArray<int3>(tris.ToArray(), a),
                TriangleSubmesh = new NativeArray<int>(sub.ToArray(), a),
                WeightStart = new NativeArray<int>(pos.Count + 1, a),
            };
            var flat = new List<BoneInfluence>();
            for (var v = 0; v < pos.Count; v++)
            {
                m.WeightStart[v] = flat.Count;
                flat.AddRange(weights[v]);
            }
            m.WeightStart[pos.Count] = flat.Count;
            m.Weights = new NativeArray<BoneInfluence>(flat.ToArray(), a);
            var fr = new ShapeFrame[frames.Count];
            var deltas = new List<ShapeDelta>();
            for (var f = 0; f < frames.Count; f++)
            {
                fr[f] = new ShapeFrame { DeltaStart = deltas.Count, DeltaCount = frames[f].Count, Configuration = frameConfiguration[f] };
                deltas.AddRange(frames[f]);
            }
            m.Frames = new NativeArray<ShapeFrame>(fr, a);
            m.Deltas = new NativeArray<ShapeDelta>(deltas.ToArray(), a);
            return m;
        }
    }

    /// <summary>Owns the native memory a test allocates and runs the solver over it.</summary>
    internal sealed class Scene : IDisposable
    {
        readonly List<IDisposable> owned = new List<IDisposable>();
        readonly List<OcclusionMesh> meshes = new List<OcclusionMesh>();
        public OcclusionStats Stats;
        public IReadOnlyList<OcclusionMesh> Meshes => meshes;

        public OcclusionMesh Add(MeshBuilder b, bool occludes, params OccluderSubmesh[] submeshes)
        {
            var e = b.Build();
            owned.Add(e);
            var m = new OcclusionMesh { Mesh = e, Occludes = occludes, Submeshes = submeshes.Length > 0 ? submeshes : null };
            meshes.Add(m);
            return m;
        }

        public OcclusionResult[] Solve(OcclusionSettings settings, int[] parents, bool[] vanishes = null)
        {
            var bones = new OcclusionBones { Parents = new NativeArray<int>(parents, Allocator.Persistent) };
            owned.Add(bones.Parents);
            if (vanishes != null)
            {
                bones.Vanishes = new NativeArray<bool>(vanishes, Allocator.Persistent);
                owned.Add(bones.Vanishes);
            }
            var results = OcclusionSolver.Solve(meshes, bones, settings, Allocator.Persistent, out Stats);
            foreach (var r in results) owned.Add(r);
            return results;
        }

        public void Dispose()
        {
            foreach (var d in owned) d.Dispose();
            owned.Clear();
        }
    }
}
