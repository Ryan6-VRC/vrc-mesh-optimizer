using System;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Topology
{
    /// <summary>How a wedge may move under decimation.</summary>
    public enum VertexKind : byte
    {
        /// <summary>No open edge: may collapse onto any neighbour.</summary>
        Interior = 0,
        /// <summary>On an open boundary: collapses only along it. Wedges that coincide at rest but part under
        /// deformation are borders too; when they mirror each other they still collapse as a pair.</summary>
        Border = 1,
        /// <summary>One of two wedges that agree in skin influences and in every live delta and continue one
        /// surface: collapses only along the seam, together with its partner.</summary>
        Seam = 2,
        /// <summary>Never removed; may still be a collapse target.</summary>
        Locked = 3,
    }

    public struct TopologySettings
    {
        /// <summary>Two wedges' influences agree when every bone's weight differs by at most this.</summary>
        public float WeightTolerance;
        /// <summary>Two wedges' deltas agree in a frame when they differ by at most this many metres.</summary>
        public float DeltaTolerance;
        /// <summary>A delta at or below this length, in metres, is treated as not moving its vertex.</summary>
        public float DeltaEpsilon;

        public static TopologySettings Default => new TopologySettings
        {
            WeightTolerance = 1e-3f,
            DeltaTolerance = 1e-4f,
            DeltaEpsilon = 1e-5f,
        };
    }

    /// <summary>
    /// A mesh's connectivity as decimation sees it. Vertices are "working" vertices: the source vertices,
    /// index for index, followed by one copy per extra submesh for any source vertex that several submeshes
    /// share, so a material boundary through a shared vertex becomes a seam between the submeshes' uses of it.
    /// Wedges are working vertices at one exact position.
    /// </summary>
    public struct MeshTopology : IDisposable
    {
        /// <summary>Working vertex to source vertex. Identity over the first source-count entries.</summary>
        public NativeArray<int> Origin;
        /// <summary>Per source triangle, over working vertices; int3(-1) where the source triangle repeats an
        /// index, names a vertex out of range or is excluded, and is dropped.</summary>
        public NativeArray<int3> Triangles;
        /// <summary>Per working vertex: the lowest working vertex at the same exact position.</summary>
        public NativeArray<int> Position;
        /// <summary>Per working vertex: the wedge it collapses together with, or itself.</summary>
        public NativeArray<int> Partner;
        public NativeArray<VertexKind> Kind;
        /// <summary>Per triangle: bit k set when edge k (corner k to corner k+1) is open; bit 3+k when that open
        /// edge is a seam rather than a border.</summary>
        public NativeArray<byte> EdgeFlags;

        /// <summary>Per source vertex, length VertexCount + 1: its live deltas are DeltaFrame/DeltaValue[DeltaStart[v] ..
        /// DeltaStart[v + 1]), frame ascending, deltas at or below the settings' epsilon dropped.</summary>
        public NativeArray<int> DeltaStart;
        public NativeArray<int> DeltaFrame;
        public NativeArray<float3> DeltaValue;

        public int SourceVertexCount;
        public int LiveTriangleCount;
        public int WorkingVertexCount => Origin.Length;

        public long ByteSize =>
            (long)Origin.Length * 4 * 3 + Kind.Length + (long)Triangles.Length * 13 +
            (long)DeltaStart.Length * 4 + (long)DeltaFrame.Length * 16;

        public void Dispose()
        {
            if (Origin.IsCreated) Origin.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (Position.IsCreated) Position.Dispose();
            if (Partner.IsCreated) Partner.Dispose();
            if (Kind.IsCreated) Kind.Dispose();
            if (EdgeFlags.IsCreated) EdgeFlags.Dispose();
            if (DeltaStart.IsCreated) DeltaStart.Dispose();
            if (DeltaFrame.IsCreated) DeltaFrame.Dispose();
            if (DeltaValue.IsCreated) DeltaValue.Dispose();
        }

        public static MeshTopology Build(in EvalMesh mesh, in TopologySettings settings, Allocator allocator) =>
            Build(mesh, default, settings, allocator);

        /// <summary>
        /// As the overload without <paramref name="excluded"/>, but a triangle it flags is dropped as a degenerate one is.
        /// Working vertices depend only on the source vertices and on which triangles are live, so the topology is the one
        /// built from a copy of the mesh without those triangles, while every per-triangle array keeps the mesh's numbering.
        /// </summary>
        /// <param name="excluded">Per triangle of <paramref name="mesh"/>; unset excludes none.</param>
        public static MeshTopology Build(in EvalMesh mesh, NativeArray<bool> excluded, in TopologySettings settings, Allocator allocator)
        {
            if (excluded.IsCreated && excluded.Length != mesh.TriangleCount)
                throw new ArgumentException($"{excluded.Length} exclusion flags for a mesh with {mesh.TriangleCount} triangles; give one per triangle", nameof(excluded));
            var topo = new MeshTopology { SourceVertexCount = mesh.VertexCount };
            try
            {
                Fill(ref topo, mesh, excluded, settings, allocator);
                return topo;
            }
            catch
            {
                topo.Dispose();
                throw;
            }
        }

        static void Fill(ref MeshTopology topo, in EvalMesh mesh, NativeArray<bool> excluded, in TopologySettings settings, Allocator allocator)
        {
            var sourceCount = mesh.VertexCount;
            var triCount = mesh.TriangleCount;
            using var exclude = new OptionalArray<bool>(excluded);
            using var submesh = new OptionalArray<int>(mesh.TriangleSubmesh);
            using var frames = new OptionalArray<ShapeFrame>(mesh.Frames);
            using var deltas = new OptionalArray<ShapeDelta>(mesh.Deltas);
            using var weightStart = new OptionalArray<int>(mesh.WeightStart);
            using var weights = new OptionalArray<BoneInfluence>(mesh.Weights);

            using var origin = new NativeList<int>(sourceCount, Allocator.Persistent);
            using var live = new NativeReference<int>(Allocator.Persistent);
            topo.Triangles = new NativeArray<int3>(triCount, allocator);
            new SplitSubmeshesJob
            {
                SourceTriangles = mesh.Triangles,
                Excluded = exclude.Array,
                TriangleSubmesh = submesh.Array,
                SourceVertexCount = sourceCount,
                Origin = origin,
                Triangles = topo.Triangles,
                LiveCount = live,
            }.Run();
            topo.LiveTriangleCount = live.Value;
            topo.Origin = new NativeArray<int>(origin.AsArray(), allocator);
            var w = topo.Origin.Length;

            topo.Position = new NativeArray<int>(w, allocator);
            using var groupNext = new NativeArray<int>(w, Allocator.Persistent);
            new WeldJob { Positions = mesh.Positions, Origin = topo.Origin, Position = topo.Position, GroupNext = groupNext }.Run();

            var deltaCount = deltas.Array.Length;
            topo.DeltaStart = new NativeArray<int>(sourceCount + 1, allocator);
            topo.DeltaFrame = new NativeArray<int>(deltaCount, allocator);
            topo.DeltaValue = new NativeArray<float3>(deltaCount, allocator);
            new DeltaIndexJob
            {
                Frames = frames.Array,
                Deltas = deltas.Array,
                Epsilon = settings.DeltaEpsilon,
                SourceVertexCount = sourceCount,
                DeltaStart = topo.DeltaStart,
                DeltaFrame = topo.DeltaFrame,
                DeltaValue = topo.DeltaValue,
            }.Run();

            using var openOut = new NativeArray<int>(w, Allocator.Persistent);
            using var openIn = new NativeArray<int>(w, Allocator.Persistent);
            using var openOutTri = new NativeArray<int>(w, Allocator.Persistent);
            using var openInTri = new NativeArray<int>(w, Allocator.Persistent);
            using var nonManifold = new NativeArray<byte>(w, Allocator.Persistent);
            topo.EdgeFlags = new NativeArray<byte>(triCount, allocator);
            new EdgeJob
            {
                Triangles = topo.Triangles,
                OpenOut = openOut,
                OpenIn = openIn,
                OpenOutTriangle = openOutTri,
                OpenInTriangle = openInTri,
                NonManifold = nonManifold,
                EdgeFlags = topo.EdgeFlags,
            }.Run();

            topo.Kind = new NativeArray<VertexKind>(w, allocator);
            topo.Partner = new NativeArray<int>(w, allocator);
            new ClassifyJob
            {
                Positions = mesh.Positions,
                WeightStart = weightStart.Array,
                Weights = weights.Array,
                Origin = topo.Origin,
                Triangles = topo.Triangles,
                Position = topo.Position,
                GroupNext = groupNext,
                OpenOut = openOut,
                OpenIn = openIn,
                OpenOutTriangle = openOutTri,
                OpenInTriangle = openInTri,
                NonManifold = nonManifold,
                DeltaStart = topo.DeltaStart,
                DeltaFrame = topo.DeltaFrame,
                DeltaValue = topo.DeltaValue,
                WeightTolerance = settings.WeightTolerance,
                DeltaTolerance = settings.DeltaTolerance,
                Kind = topo.Kind,
                Partner = topo.Partner,
            }.Schedule(w, 256).Complete();

            new FinishJob { Triangles = topo.Triangles, Kind = topo.Kind, Partner = topo.Partner, EdgeFlags = topo.EdgeFlags }.Run();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct SplitSubmeshesJob : IJob
        {
            [ReadOnly] public NativeArray<int3> SourceTriangles;
            /// <summary>Per source triangle, or empty for none excluded.</summary>
            [ReadOnly] public NativeArray<bool> Excluded;
            [ReadOnly] public NativeArray<int> TriangleSubmesh;
            public int SourceVertexCount;
            public NativeList<int> Origin;
            public NativeArray<int3> Triangles;
            public NativeReference<int> LiveCount;

            public void Execute()
            {
                var owner = new NativeArray<int>(SourceVertexCount, Allocator.Temp);
                for (var v = 0; v < SourceVertexCount; v++)
                {
                    owner[v] = -1;
                    Origin.Add(v);
                }
                var copies = new NativeParallelHashMap<int2, int>(64, Allocator.Temp);
                var hasSubmesh = TriangleSubmesh.IsCreated && TriangleSubmesh.Length == SourceTriangles.Length;
                var live = 0;
                for (var t = 0; t < SourceTriangles.Length; t++)
                {
                    var src = SourceTriangles[t];
                    if ((Excluded.Length > 0 && Excluded[t]) ||
                        math.any(src < 0) || math.any(src >= SourceVertexCount) || src.x == src.y || src.y == src.z || src.x == src.z)
                    {
                        Triangles[t] = new int3(-1);
                        continue;
                    }
                    var s = hasSubmesh ? TriangleSubmesh[t] : 0;
                    var tri = src;
                    for (var k = 0; k < 3; k++)
                    {
                        var o = src[k];
                        if (owner[o] == -1) owner[o] = s;
                        else if (owner[o] != s)
                        {
                            var key = new int2(o, s);
                            if (!copies.TryGetValue(key, out var c))
                            {
                                c = Origin.Length;
                                Origin.Add(o);
                                copies.Add(key, c);
                            }
                            tri[k] = c;
                        }
                    }
                    Triangles[t] = tri;
                    live++;
                }
                LiveCount.Value = live;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct WeldJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int> Origin;
            public NativeArray<int> Position;
            public NativeArray<int> GroupNext;

            public void Execute()
            {
                var map = new NativeParallelHashMap<int3, int>(Origin.Length, Allocator.Temp);
                for (var w = 0; w < Origin.Length; w++)
                {
                    var p = Positions[Origin[w]];
                    p = math.select(p, float3.zero, p == float3.zero); // -0 welds with +0
                    var key = math.asint(p);
                    if (map.TryGetValue(key, out var first))
                    {
                        Position[w] = first;
                        GroupNext[w] = GroupNext[first];
                        GroupNext[first] = w;
                    }
                    else
                    {
                        map.Add(key, w);
                        Position[w] = w;
                        GroupNext[w] = w;
                    }
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct DeltaIndexJob : IJob
        {
            [ReadOnly] public NativeArray<ShapeFrame> Frames;
            [ReadOnly] public NativeArray<ShapeDelta> Deltas;
            public float Epsilon;
            public int SourceVertexCount;
            public NativeArray<int> DeltaStart;
            public NativeArray<int> DeltaFrame;
            public NativeArray<float3> DeltaValue;

            public void Execute()
            {
                if (!Frames.IsCreated || !Deltas.IsCreated) return;
                var eps2 = Epsilon * Epsilon;
                for (var f = 0; f < Frames.Length; f++)
                {
                    var fr = Frames[f];
                    for (var i = fr.DeltaStart; i < fr.DeltaStart + fr.DeltaCount; i++)
                    {
                        var d = Deltas[i];
                        if (d.Vertex < 0 || d.Vertex >= SourceVertexCount || math.lengthsq(d.Delta) <= eps2) continue;
                        DeltaStart[d.Vertex + 1]++;
                    }
                }
                for (var v = 0; v < SourceVertexCount; v++) DeltaStart[v + 1] += DeltaStart[v];
                var cursor = new NativeArray<int>(SourceVertexCount, Allocator.Temp);
                NativeArray<int>.Copy(DeltaStart, cursor, SourceVertexCount);
                for (var f = 0; f < Frames.Length; f++)
                {
                    var fr = Frames[f];
                    for (var i = fr.DeltaStart; i < fr.DeltaStart + fr.DeltaCount; i++)
                    {
                        var d = Deltas[i];
                        if (d.Vertex < 0 || d.Vertex >= SourceVertexCount || math.lengthsq(d.Delta) <= eps2) continue;
                        var at = cursor[d.Vertex]++;
                        DeltaFrame[at] = f;
                        DeltaValue[at] = d.Delta;
                    }
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct EdgeJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Triangles;
            public NativeArray<int> OpenOut;
            public NativeArray<int> OpenIn;
            public NativeArray<int> OpenOutTriangle;
            public NativeArray<int> OpenInTriangle;
            public NativeArray<byte> NonManifold;
            public NativeArray<byte> EdgeFlags;

            public void Execute()
            {
                for (var w = 0; w < OpenOut.Length; w++)
                {
                    OpenOut[w] = -1;
                    OpenIn[w] = -1;
                    OpenOutTriangle[w] = -1;
                    OpenInTriangle[w] = -1;
                }
                var count = new NativeParallelHashMap<int2, int>(Triangles.Length * 3, Allocator.Temp);
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (tri.x < 0) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        var key = new int2(tri[k], tri[(k + 1) % 3]);
                        count.TryGetValue(key, out var c);
                        count[key] = c + 1;
                    }
                }
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (tri.x < 0) continue;
                    byte flags = 0;
                    for (var k = 0; k < 3; k++)
                    {
                        var a = tri[k];
                        var b = tri[(k + 1) % 3];
                        count.TryGetValue(new int2(a, b), out var ab);
                        count.TryGetValue(new int2(b, a), out var ba);
                        if (ab > 1 || ab + ba > 2)
                        {
                            NonManifold[a] = 1;
                            NonManifold[b] = 1;
                        }
                        if (ba != 0) continue;
                        flags |= (byte)(1 << k);
                        OpenOut[a] = OpenOut[a] == -1 || OpenOut[a] == b ? b : -2;
                        OpenOutTriangle[a] = t;
                        OpenIn[b] = OpenIn[b] == -1 || OpenIn[b] == a ? a : -2;
                        OpenInTriangle[b] = t;
                    }
                    EdgeFlags[t] = flags;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct ClassifyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int> WeightStart;
            [ReadOnly] public NativeArray<BoneInfluence> Weights;
            [ReadOnly] public NativeArray<int> Origin;
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<int> Position;
            [ReadOnly] public NativeArray<int> GroupNext;
            [ReadOnly] public NativeArray<int> OpenOut;
            [ReadOnly] public NativeArray<int> OpenIn;
            [ReadOnly] public NativeArray<int> OpenOutTriangle;
            [ReadOnly] public NativeArray<int> OpenInTriangle;
            [ReadOnly] public NativeArray<byte> NonManifold;
            [ReadOnly] public NativeArray<int> DeltaStart;
            [ReadOnly] public NativeArray<int> DeltaFrame;
            [ReadOnly] public NativeArray<float3> DeltaValue;
            public float WeightTolerance;
            public float DeltaTolerance;
            public NativeArray<VertexKind> Kind;
            public NativeArray<int> Partner;

            public void Execute(int w)
            {
                Partner[w] = w;
                var o = OpenOut[w];
                var i = OpenIn[w];
                if (NonManifold[w] != 0 || o == -2 || i == -2 || (o == -1) != (i == -1))
                {
                    Kind[w] = VertexKind.Locked;
                    return;
                }
                if (o == -1)
                {
                    // No open edge. Other wedges at this position with no open edge are independent sheets
                    // (a double-sided shell), which decimate independently.
                    Kind[w] = VertexKind.Interior;
                    return;
                }
                if (Position[o] == Position[i])
                {
                    Kind[w] = VertexKind.Locked;
                    return;
                }
                if (!FindMirror(w, out var partner))
                {
                    Kind[w] = VertexKind.Locked;
                    return;
                }
                if (partner == -1)
                {
                    Kind[w] = VertexKind.Border;
                    return;
                }
                if (!FindMirror(partner, out var back) || back != w)
                {
                    Kind[w] = VertexKind.Locked;
                    return;
                }
                Partner[w] = partner;
                var agree = InfluencesAgree(Origin[w], Origin[partner]) && DeltasAgree(Origin[w], Origin[partner]);
                // A seam continues one surface across the edge; wedges whose open-edge triangles face apart
                // are a fold (a double-sided hem), which is a border in every configuration.
                var n1 = TriangleNormal(OpenOutTriangle[w]);
                var n2 = TriangleNormal(OpenInTriangle[partner]);
                var fold = math.dot(n1, n2) < 0f;
                Kind[w] = agree && !fold ? VertexKind.Seam : VertexKind.Border;
            }

            /// <summary>
            /// Finds the one wedge at w's position whose open edges run opposite to w's. False when the position
            /// is ambiguous: another open wedge touches w's open edges without mirroring them, or more than one
            /// mirrors them, as where a seam crosses a hard edge. Partner is -1 when nothing touches.
            /// </summary>
            bool FindMirror(int w, out int partner)
            {
                partner = -1;
                var wOut = Position[OpenOut[w]];
                var wIn = Position[OpenIn[w]];
                var mirrors = 0;
                for (var x = GroupNext[w]; x != w; x = GroupNext[x])
                {
                    var ox = OpenOut[x];
                    var ix = OpenIn[x];
                    if (ox == -1 && ix == -1) continue;
                    if (ox < 0 || ix < 0) return false;
                    var xOut = Position[ox];
                    var xIn = Position[ix];
                    if (xOut == wIn && xIn == wOut)
                    {
                        mirrors++;
                        partner = x;
                    }
                    else if (xOut == wOut || xOut == wIn || xIn == wOut || xIn == wIn)
                    {
                        return false;
                    }
                }
                return mirrors <= 1;
            }

            float3 TriangleNormal(int t)
            {
                var tri = Triangles[t];
                var p0 = Positions[Origin[tri.x]];
                var p1 = Positions[Origin[tri.y]];
                var p2 = Positions[Origin[tri.z]];
                return math.cross(p1 - p0, p2 - p0);
            }

            bool InfluencesAgree(int a, int b)
            {
                if (a == b) return true;
                if (!WeightStart.IsCreated || WeightStart.Length <= math.max(a, b) + 1) return true;
                for (var i = WeightStart[a]; i < WeightStart[a + 1]; i++)
                {
                    var inf = Weights[i];
                    if (math.abs(inf.Weight - WeightOf(b, inf.Bone)) > WeightTolerance) return false;
                }
                for (var i = WeightStart[b]; i < WeightStart[b + 1]; i++)
                {
                    var inf = Weights[i];
                    if (math.abs(inf.Weight - WeightOf(a, inf.Bone)) > WeightTolerance) return false;
                }
                return true;
            }

            float WeightOf(int v, int bone)
            {
                var sum = 0f;
                for (var i = WeightStart[v]; i < WeightStart[v + 1]; i++)
                    if (Weights[i].Bone == bone) sum += Weights[i].Weight;
                return sum;
            }

            bool DeltasAgree(int a, int b)
            {
                if (a == b) return true;
                var tol2 = DeltaTolerance * DeltaTolerance;
                var i = DeltaStart[a];
                var j = DeltaStart[b];
                var ie = DeltaStart[a + 1];
                var je = DeltaStart[b + 1];
                while (i < ie || j < je)
                {
                    var fa = i < ie ? DeltaFrame[i] : int.MaxValue;
                    var fb = j < je ? DeltaFrame[j] : int.MaxValue;
                    float3 da = 0, db = 0;
                    if (fa <= fb) da = DeltaValue[i++];
                    if (fb <= fa) db = DeltaValue[j++];
                    if (math.lengthsq(da - db) > tol2) return false;
                }
                return true;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct FinishJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Triangles;
            public NativeArray<VertexKind> Kind;
            public NativeArray<int> Partner;
            public NativeArray<byte> EdgeFlags;

            public void Execute()
            {
                // A pair moves together or not at all.
                for (var w = 0; w < Kind.Length; w++)
                {
                    var p = Partner[w];
                    if (p == w) continue;
                    if (Kind[w] == VertexKind.Locked || Kind[p] == VertexKind.Locked || Kind[p] != Kind[w] || Partner[p] != w)
                    {
                        Kind[w] = VertexKind.Locked;
                        Kind[p] = VertexKind.Locked;
                    }
                }
                for (var w = 0; w < Kind.Length; w++)
                    if (Kind[w] == VertexKind.Locked) Partner[w] = w;

                // An open edge weighs as a seam where it runs between seam wedges (or a seam wedge and a locked
                // seam end), and as a border otherwise.
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (tri.x < 0) continue;
                    var flags = EdgeFlags[t];
                    for (var k = 0; k < 3; k++)
                    {
                        if ((flags & (1 << k)) == 0) continue;
                        var ka = Kind[tri[k]];
                        var kb = Kind[tri[(k + 1) % 3]];
                        var seam = (ka == VertexKind.Seam || kb == VertexKind.Seam) && ka != VertexKind.Border && kb != VertexKind.Border;
                        if (seam) flags |= (byte)(1 << (3 + k));
                    }
                    EdgeFlags[t] = flags;
                }
            }
        }
    }
}
