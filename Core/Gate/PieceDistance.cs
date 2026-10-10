using Ryan6Vrc.MeshOptimizer.Core.Geometry;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Gate
{
    /// <summary>How far a cut's triangles stand from the source pieces they were cut from.</summary>
    internal struct PieceDistanceResult
    {
        /// <summary>The farthest any sample of a written triangle stands from its own piece of the source, in the positions' unit.</summary>
        public float Max;
        /// <summary>The written triangle that reads <see cref="Max"/>, the first on a tie; -1 when nothing was measured.</summary>
        public int MaxTriangle;
        /// <summary>Written triangles with a sample farther than the level they were measured against.</summary>
        public int Over;
    }

    /// <summary>
    /// Measures each triangle of a half-edge-collapse cut against the piece of the source mesh it came from, a piece being
    /// the triangles joined through shared vertices. A collapse never leaves its piece and never moves a vertex, so a
    /// written triangle's corners are source vertices of one piece, and where it stands away from that piece the surface
    /// shows something the source did not: a rim stretched across a face reads here, while the nearest surface of any
    /// piece, a face a hair's breadth under that rim, would call it nothing. Texture coordinates take no part, so how a
    /// mesh's charts are packed cannot move the reading.
    /// </summary>
    internal static class PieceDistance
    {
        /// <param name="source">The source triangles over <paramref name="positions"/>; one that repeats a vertex or names one out of range is left out.</param>
        /// <param name="written">The cut's triangles over the same vertices.</param>
        /// <param name="level">A written triangle farther than this counts in <see cref="PieceDistanceResult.Over"/>.</param>
        public static PieceDistanceResult Measure(NativeArray<float3> positions, NativeArray<int3> source, NativeArray<int3> written, float level)
        {
            var result = new PieceDistanceResult { MaxTriangle = -1 };
            if (written.Length == 0 || source.Length == 0) return result;
            using var piece = new NativeArray<int>(positions.Length, Allocator.TempJob);
            using var ranges = new NativeList<int4>(Allocator.TempJob);
            using var prims = new NativeArray<TreePrim>(source.Length, Allocator.TempJob);
            using var nodes = new NativeList<TreeNode>(Allocator.TempJob);
            new BuildJob { Positions = positions, Source = source, Piece = piece, Ranges = ranges, Prims = prims, Nodes = nodes }.Run();
            using var distance = new NativeArray<float>(written.Length, Allocator.TempJob);
            new QueryJob
            {
                Positions = positions,
                Written = written,
                Piece = piece,
                Ranges = ranges.AsArray(),
                Prims = prims,
                Nodes = nodes.AsArray(),
                Distance = distance,
            }.Schedule(written.Length, 64).Complete();
            for (var t = 0; t < distance.Length; t++)
            {
                var d = distance[t];
                if (d > level) result.Over++;
                if (d > result.Max || result.MaxTriangle < 0)
                {
                    result.Max = d;
                    result.MaxTriangle = t;
                }
            }
            return result;
        }

        /// <summary>Groups the source triangles by piece and builds one tree over each.</summary>
        [BurstCompile(CompileSynchronously = true)]
        struct BuildJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Source;
            /// <summary>Per vertex, its piece's index in <see cref="Ranges"/>, or -1 for a vertex no kept triangle uses.</summary>
            public NativeArray<int> Piece;
            /// <summary>Per piece: where its prims start and how many, where its nodes start and how many.</summary>
            public NativeList<int4> Ranges;
            public NativeArray<TreePrim> Prims;
            public NativeList<TreeNode> Nodes;

            public void Execute()
            {
                var n = Positions.Length;
                var parent = new NativeArray<int>(n, Allocator.Temp);
                for (var v = 0; v < n; v++) parent[v] = v;
                for (var t = 0; t < Source.Length; t++)
                {
                    var tri = Source[t];
                    if (!Usable(tri, n)) continue;
                    parent[Find(ref parent, tri.x)] = Find(ref parent, tri.y);
                    parent[Find(ref parent, tri.y)] = Find(ref parent, tri.z);
                }
                // Pieces in the order their first triangle appears, so the layout is a pure function of the input.
                var pieceOfRoot = new NativeArray<int>(n, Allocator.Temp);
                for (var v = 0; v < n; v++) pieceOfRoot[v] = -1;
                var counts = new NativeList<int>(Allocator.Temp);
                for (var t = 0; t < Source.Length; t++)
                {
                    var tri = Source[t];
                    if (!Usable(tri, n)) continue;
                    var root = Find(ref parent, tri.x);
                    if (pieceOfRoot[root] < 0)
                    {
                        pieceOfRoot[root] = counts.Length;
                        counts.Add(0);
                    }
                    counts[pieceOfRoot[root]]++;
                }
                var start = 0;
                for (var p = 0; p < counts.Length; p++)
                {
                    Ranges.Add(new int4(start, 0, 0, 0));
                    start += counts[p];
                }
                for (var t = 0; t < Source.Length; t++)
                {
                    var tri = Source[t];
                    if (!Usable(tri, n)) continue;
                    var p = pieceOfRoot[Find(ref parent, tri.x)];
                    var r = Ranges[p];
                    float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
                    Prims[r.x + r.y] = new TreePrim { A = a, B = b, C = c, N = math.cross(b - a, c - a), Triangle = t };
                    r.y++;
                    Ranges[p] = r;
                }
                for (var v = 0; v < n; v++) Piece[v] = pieceOfRoot[Find(ref parent, v)];

                var own = new NativeList<TreeNode>(Allocator.Temp);
                for (var p = 0; p < Ranges.Length; p++)
                {
                    var r = Ranges[p];
                    TriangleTree.Build(Prims.GetSubArray(r.x, r.y), own);
                    r.z = Nodes.Length;
                    r.w = own.Length;
                    Nodes.AddRange(own.AsArray());
                    Ranges[p] = r;
                }
            }

            static bool Usable(int3 tri, int n) =>
                math.all(tri >= 0) && math.all(tri < n) && tri.x != tri.y && tri.y != tri.z && tri.x != tri.z;

            static int Find(ref NativeArray<int> parent, int v)
            {
                var root = v;
                while (parent[root] != root) root = parent[root];
                while (parent[v] != root)
                {
                    var next = parent[v];
                    parent[v] = root;
                    v = next;
                }
                return root;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct QueryJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Written;
            [ReadOnly] public NativeArray<int> Piece;
            [ReadOnly] public NativeArray<int4> Ranges;
            [ReadOnly] public NativeArray<TreePrim> Prims;
            [ReadOnly] public NativeArray<TreeNode> Nodes;
            [WriteOnly] public NativeArray<float> Distance;

            public void Execute(int t)
            {
                var tri = Written[t];
                var n = Positions.Length;
                Distance[t] = 0f;
                if (math.any(tri < 0) || math.any(tri >= n)) return;
                var p = Piece[tri.x];
                if (p < 0) return;
                var r = Ranges[p];
                var prims = Prims.GetSubArray(r.x, r.y);
                var nodes = Nodes.GetSubArray(r.z, r.w);
                float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
                // The corners are source vertices and read zero; a triangle strays in its middle and along its edges.
                var worst = 0f;
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, (a + b + c) / 3f, default, false, out _));
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, (a + b) * 0.5f, default, false, out _));
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, (b + c) * 0.5f, default, false, out _));
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, (c + a) * 0.5f, default, false, out _));
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, a * 0.6f + b * 0.2f + c * 0.2f, default, false, out _));
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, a * 0.2f + b * 0.6f + c * 0.2f, default, false, out _));
                worst = math.max(worst, TriangleTree.ClosestSq(nodes, prims, a * 0.2f + b * 0.2f + c * 0.6f, default, false, out _));
                Distance[t] = math.sqrt(worst);
            }
        }
    }
}
