using System;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>
    /// Every working vertex's error quadrics: one for the rest state, one per live frame that moves any vertex
    /// of its one-ring (stored sparsely, since elsewhere a frame's quadric equals the rest one), and one for
    /// UV0. Plane quadrics are area-weighted; open edges add a perpendicular plane weighted by squared length.
    /// </summary>
    internal struct QuadricBank : IDisposable
    {
        public NativeArray<Quadric> Rest;
        public NativeArray<UvQuadric> Uv;
        public NativeArray<int> SparseStart;
        public NativeArray<int> SparseCount;
        /// <summary>Vertex w's frame quadrics are Sparse[SparseStart[w] .. + SparseCount[w]), frame ascending.</summary>
        public NativeArray<FrameQuadric> Sparse;

        /// <summary>The source surface's area-weighted vertex normal at rest, per working vertex. Never merged:
        /// it is what a decimated triangle at this corner must still face along.</summary>
        public NativeArray<float3> NormalRest;
        /// <summary>The same in each frame that moves the vertex's one-ring, laid out as the frame quadrics were at
        /// build: NormalFrames[NormalStart[w] .. + NormalCount[w]), frame ascending. A frame not listed leaves the
        /// normal as at rest.</summary>
        public NativeArray<int> NormalStart;
        public NativeArray<int> NormalCount;
        public NativeArray<FrameNormal> NormalFrames;

        public long ByteSize =>
            (long)Rest.Length * UnsafeUtility.SizeOf<Quadric>() + (long)Uv.Length * UnsafeUtility.SizeOf<UvQuadric>() +
            (long)SparseStart.Length * 8 + (long)Sparse.Length * UnsafeUtility.SizeOf<FrameQuadric>() +
            (long)NormalRest.Length * 20 + (long)NormalFrames.Length * UnsafeUtility.SizeOf<FrameNormal>();

        public void Dispose()
        {
            if (Rest.IsCreated) Rest.Dispose();
            if (Uv.IsCreated) Uv.Dispose();
            if (SparseStart.IsCreated) SparseStart.Dispose();
            if (SparseCount.IsCreated) SparseCount.Dispose();
            if (Sparse.IsCreated) Sparse.Dispose();
            if (NormalRest.IsCreated) NormalRest.Dispose();
            if (NormalStart.IsCreated) NormalStart.Dispose();
            if (NormalCount.IsCreated) NormalCount.Dispose();
            if (NormalFrames.IsCreated) NormalFrames.Dispose();
        }

        public static QuadricBank Build(in EvalMesh mesh, in MeshTopology topo, in DecimationSettings settings, Allocator allocator)
        {
            var w = topo.WorkingVertexCount;
            using var uv0 = new OptionalArray<float2>(mesh.Uv0);
            var bank = new QuadricBank
            {
                Rest = new NativeArray<Quadric>(w, allocator),
                Uv = new NativeArray<UvQuadric>(w, allocator),
                SparseStart = new NativeArray<int>(w, allocator),
                SparseCount = new NativeArray<int>(w, allocator),
                NormalRest = new NativeArray<float3>(w, allocator),
            };

            using var triStart = new NativeArray<int>(w + 1, Allocator.Persistent);
            using var tris = new NativeArray<int>(topo.LiveTriangleCount * 3, Allocator.Persistent);
            new VertexTrianglesJob { Triangles = topo.Triangles, Start = triStart, List = tris }.Run();

            var common = new Common
            {
                Positions = mesh.Positions,
                Uv0 = uv0.Array,
                Origin = topo.Origin,
                Triangles = topo.Triangles,
                EdgeFlags = topo.EdgeFlags,
                DeltaStart = topo.DeltaStart,
                DeltaFrame = topo.DeltaFrame,
                DeltaValue = topo.DeltaValue,
                TriStart = triStart,
                Tris = tris,
                BorderWeight = settings.BorderWeight,
                SeamWeight = settings.SeamWeight,
            };

            new CountJob { C = common, SparseCount = bank.SparseCount }.ScheduleBatch(w, 64).Complete();
            using var total = new NativeReference<int>(Allocator.Persistent);
            new PrefixJob { Count = bank.SparseCount, Start = bank.SparseStart, Total = total }.Run();
            bank.Sparse = new NativeArray<FrameQuadric>(total.Value, allocator);
            bank.NormalFrames = new NativeArray<FrameNormal>(total.Value, allocator);
            new FillJob
            {
                C = common,
                SparseStart = bank.SparseStart,
                Rest = bank.Rest,
                Uv = bank.Uv,
                Sparse = bank.Sparse,
                NormalRest = bank.NormalRest,
                NormalFrames = bank.NormalFrames,
            }.ScheduleBatch(w, 64).Complete();
            bank.NormalStart = new NativeArray<int>(bank.SparseStart, allocator);
            bank.NormalCount = new NativeArray<int>(bank.SparseCount, allocator);
            return bank;
        }

        /// <summary>Working vertex to incident live triangles, as offsets into a flat list.</summary>
        [BurstCompile]
        struct VertexTrianglesJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Triangles;
            public NativeArray<int> Start;
            public NativeArray<int> List;

            public void Execute()
            {
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (tri.x < 0) continue;
                    Start[tri.x + 1]++;
                    Start[tri.y + 1]++;
                    Start[tri.z + 1]++;
                }
                for (var i = 1; i < Start.Length; i++) Start[i] += Start[i - 1];
                var cursor = new NativeArray<int>(Start.Length, Allocator.Temp);
                NativeArray<int>.Copy(Start, cursor);
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (tri.x < 0) continue;
                    List[cursor[tri.x]++] = t;
                    List[cursor[tri.y]++] = t;
                    List[cursor[tri.z]++] = t;
                }
            }
        }

        struct Common
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<float2> Uv0;
            [ReadOnly] public NativeArray<int> Origin;
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<byte> EdgeFlags;
            [ReadOnly] public NativeArray<int> DeltaStart;
            [ReadOnly] public NativeArray<int> DeltaFrame;
            [ReadOnly] public NativeArray<float3> DeltaValue;
            [ReadOnly] public NativeArray<int> TriStart;
            [ReadOnly] public NativeArray<int> Tris;
            public float BorderWeight;
            public float SeamWeight;

            /// <summary>The sorted distinct frames that move any corner of w's incident triangles.</summary>
            public void GatherFrames(int w, ref NativeList<int> frames)
            {
                frames.Clear();
                for (var i = TriStart[w]; i < TriStart[w + 1]; i++)
                {
                    var tri = Triangles[Tris[i]];
                    for (var k = 0; k < 3; k++)
                    {
                        var o = Origin[tri[k]];
                        for (var j = DeltaStart[o]; j < DeltaStart[o + 1]; j++) frames.Add(DeltaFrame[j]);
                    }
                }
                if (frames.Length < 2) return;
                frames.Sort();
                var n = 1;
                for (var i = 1; i < frames.Length; i++)
                    if (frames[i] != frames[n - 1]) frames[n++] = frames[i];
                frames.Length = n;
            }

            public double3 PositionIn(int working, int frame)
            {
                var o = Origin[working];
                var p = (double3)Positions[o];
                if (frame < 0) return p;
                return p + (double3)DeltaLookup.Find(DeltaStart, DeltaFrame, DeltaValue, o, frame);
            }

            /// <summary>w's quadric with every corner placed as in the given frame (-1: rest).</summary>
            public Quadric ConfigurationQuadric(int w, int frame, out float3 normal)
            {
                var q = default(Quadric);
                var nsum = double3.zero;
                for (var i = TriStart[w]; i < TriStart[w + 1]; i++)
                {
                    var t = Tris[i];
                    var tri = Triangles[t];
                    var p0 = PositionIn(tri.x, frame);
                    var p1 = PositionIn(tri.y, frame);
                    var p2 = PositionIn(tri.z, frame);
                    var n = math.cross(p1 - p0, p2 - p0);
                    var len = math.length(n);
                    nsum += n;
                    if (len > 1e-30)
                    {
                        var nn = n / len;
                        q.Add(Quadric.FromPlane(nn, -math.dot(nn, p0), 0.5 * len));
                    }
                    var flags = EdgeFlags[t];
                    if ((flags & 7) == 0 || len <= 1e-30) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        if ((flags & (1 << k)) == 0) continue;
                        var a = tri[k];
                        var b = tri[(k + 1) % 3];
                        if (a != w && b != w) continue;
                        var pa = k == 0 ? p0 : k == 1 ? p1 : p2;
                        var pb = k == 0 ? p1 : k == 1 ? p2 : p0;
                        var e = pb - pa;
                        var el2 = math.lengthsq(e);
                        var en = math.cross(e, n);
                        var enl = math.length(en);
                        if (enl <= 1e-30 || el2 <= 0) continue;
                        en /= enl;
                        var factor = (flags & (1 << (3 + k))) != 0 ? SeamWeight : BorderWeight;
                        q.Add(Quadric.FromPlane(en, -math.dot(en, pa), factor * el2));
                    }
                }
                var nl = math.length(nsum);
                normal = nl > 1e-30 ? (float3)(nsum / nl) : float3.zero;
                return q;
            }

            /// <summary>
            /// w's UV0 error: each incident triangle's UV gradient, scaled by the triangle's metres per UV unit,
            /// so a deviation reads as the distance a texel lands from where it was.
            /// </summary>
            public UvQuadric UvQuadricOf(int w)
            {
                var q = default(UvQuadric);
                if (Uv0.Length == 0) return q;
                for (var i = TriStart[w]; i < TriStart[w + 1]; i++)
                {
                    var tri = Triangles[Tris[i]];
                    var p0 = PositionIn(tri.x, -1);
                    var e1 = PositionIn(tri.y, -1) - p0;
                    var e2 = PositionIn(tri.z, -1) - p0;
                    var t0 = (double2)Uv0[Origin[tri.x]];
                    var d1 = (double2)Uv0[Origin[tri.y]] - t0;
                    var d2 = (double2)Uv0[Origin[tri.z]] - t0;
                    var a = math.dot(e1, e1);
                    var b = math.dot(e1, e2);
                    var c = math.dot(e2, e2);
                    var det = a * c - b * b;
                    if (det <= 1e-40) continue;
                    var areaUv = 0.5 * math.abs(d1.x * d2.y - d1.y * d2.x);
                    if (areaUv <= 1e-12) continue;
                    var area3 = 0.5 * math.sqrt(det);
                    var scale = math.sqrt(area3 / areaUv);
                    for (var ch = 0; ch < 2; ch++)
                    {
                        var r1 = d1[ch];
                        var r2 = d2[ch];
                        var alpha = (c * r1 - b * r2) / det;
                        var beta = (a * r2 - b * r1) / det;
                        var g = alpha * e1 + beta * e2;
                        var d = t0[ch] - math.dot(g, p0);
                        // (s g.p + s d - s a)^2 = s^2 (g.p + d - a)^2: the scale rides in the weight.
                        var aq = AttributeQuadric.FromGradient(g, d, area3 * scale * scale);
                        if (ch == 0) q.U.Add(aq);
                        else q.V.Add(aq);
                    }
                }
                return q;
            }
        }

        [BurstCompile]
        struct CountJob : IJobParallelForBatch
        {
            public Common C;
            [NativeDisableParallelForRestriction] public NativeArray<int> SparseCount;

            public void Execute(int start, int count)
            {
                var frames = new NativeList<int>(64, Allocator.Temp);
                for (var w = start; w < start + count; w++)
                {
                    C.GatherFrames(w, ref frames);
                    SparseCount[w] = frames.Length;
                }
            }
        }

        [BurstCompile]
        struct PrefixJob : IJob
        {
            [ReadOnly] public NativeArray<int> Count;
            public NativeArray<int> Start;
            public NativeReference<int> Total;

            public void Execute()
            {
                var sum = 0;
                for (var i = 0; i < Count.Length; i++)
                {
                    Start[i] = sum;
                    sum += Count[i];
                }
                Total.Value = sum;
            }
        }

        [BurstCompile]
        struct FillJob : IJobParallelForBatch
        {
            public Common C;
            [ReadOnly] public NativeArray<int> SparseStart;
            [NativeDisableParallelForRestriction] public NativeArray<Quadric> Rest;
            [NativeDisableParallelForRestriction] public NativeArray<UvQuadric> Uv;
            [NativeDisableParallelForRestriction] public NativeArray<FrameQuadric> Sparse;
            [NativeDisableParallelForRestriction] public NativeArray<float3> NormalRest;
            [NativeDisableParallelForRestriction] public NativeArray<FrameNormal> NormalFrames;

            public void Execute(int start, int count)
            {
                var frames = new NativeList<int>(64, Allocator.Temp);
                for (var w = start; w < start + count; w++)
                {
                    Rest[w] = C.ConfigurationQuadric(w, -1, out var restNormal);
                    NormalRest[w] = restNormal;
                    Uv[w] = C.UvQuadricOf(w);
                    C.GatherFrames(w, ref frames);
                    var at = SparseStart[w];
                    for (var i = 0; i < frames.Length; i++)
                    {
                        Sparse[at + i] = new FrameQuadric { Frame = frames[i], Q = C.ConfigurationQuadric(w, frames[i], out var n) };
                        NormalFrames[at + i] = new FrameNormal { Frame = frames[i], N = n };
                    }
                }
            }
        }
    }

    internal static class DeltaLookup
    {
        /// <summary>Source vertex o's delta in a frame, or zero where the frame does not move it.</summary>
        public static float3 Find(in NativeArray<int> start, in NativeArray<int> frame, in NativeArray<float3> value, int o, int f)
        {
            var lo = start[o];
            var hi = start[o + 1] - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var m = frame[mid];
                if (m == f) return value[mid];
                if (m < f) lo = mid + 1;
                else hi = mid - 1;
            }
            return float3.zero;
        }
    }
}
