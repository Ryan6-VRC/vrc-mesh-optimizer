using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Clearance
{
    /// <summary>One source of layers for <see cref="ClearanceSurface.Build"/>: a mesh's kept triangles and each one's layer.</summary>
    public struct ClearanceLayerSource
    {
        public NativeArray<float3> Positions;
        public NativeArray<int3> Triangles;
        /// <summary>Per triangle, its layer: an id unique across every source.</summary>
        public NativeArray<int> TriangleLayer;
    }

    /// <summary>
    /// Every layer's surface as it was before decimation, in the one space the evaluation meshes share, with a tree
    /// for asking how close one layer comes to the others. A layer is a connected piece of one mesh (see
    /// <see cref="Layers"/>): a skirt and its lining modelled as separate pieces of one mesh are two layers, while
    /// the triangles around a vertex, being one piece, are never each other's neighbours.
    /// </summary>
    public struct ClearanceSurface : IDisposable
    {
        internal NativeArray<TreeNode> Nodes;
        /// <summary>In leaf order; <see cref="TreePrim.Triangle"/> is the prim's index in build order.</summary>
        internal NativeArray<TreePrim> Prims;
        /// <summary>Per prim in leaf order, its layer.</summary>
        internal NativeArray<int> PrimLayer;
        /// <summary>Per node, the one layer every prim under it belongs to, or -1 when they are mixed.</summary>
        internal NativeArray<int> NodeLayer;
        /// <summary>
        /// Per prim in leaf order, its clearance from every other layer, capped at <see cref="Reach"/>: the gap on this
        /// side, where another layer comes near it.
        /// </summary>
        internal NativeArray<float> PrimClearance;
        /// <summary>Per node, the largest <see cref="PrimClearance"/> under it.</summary>
        internal NativeArray<float> NodeClearance;
        /// <summary>The cap on every clearance here, in metres.</summary>
        public float Reach;

        public bool IsCreated => Nodes.IsCreated;
        public int TriangleCount => Prims.IsCreated ? Prims.Length : 0;

        public void Dispose()
        {
            if (Nodes.IsCreated) Nodes.Dispose();
            if (Prims.IsCreated) Prims.Dispose();
            if (PrimLayer.IsCreated) PrimLayer.Dispose();
            if (NodeLayer.IsCreated) NodeLayer.Dispose();
            if (PrimClearance.IsCreated) PrimClearance.Dispose();
            if (NodeClearance.IsCreated) NodeClearance.Dispose();
        }

        /// <summary>
        /// A triangle with no area draws nothing and bounds nothing, so it is left out. <paramref name="reach"/> caps each
        /// triangle's clearance, in metres, and must be the reach decimation runs with.
        /// </summary>
        public static ClearanceSurface Build(IReadOnlyList<ClearanceLayerSource> sources, float reach, Allocator allocator)
        {
            if (!(reach > 0f)) throw new ArgumentException($"reach {reach} m must be above 0");
            var total = 0;
            foreach (var s in sources)
            {
                if (s.TriangleLayer.Length != s.Triangles.Length)
                    throw new ArgumentException($"a source has {s.Triangles.Length} triangles and {s.TriangleLayer.Length} layers; give one layer per triangle");
                total += s.Triangles.Length;
            }
            var surface = new ClearanceSurface { Reach = reach };
            try
            {
                using var prims = new NativeList<TreePrim>(total, Allocator.Persistent);
                using var layers = new NativeList<int>(total, Allocator.Persistent);
                foreach (var s in sources)
                    new GatherJob { Positions = s.Positions, Triangles = s.Triangles, TriangleLayer = s.TriangleLayer, Prims = prims, Layers = layers }.Run();
                surface.Prims = new NativeArray<TreePrim>(prims.AsArray(), allocator);
                using var nodes = new NativeList<TreeNode>(math.max(1, total), Allocator.Persistent);
                new BuildJob { Prims = surface.Prims, Layers = layers.AsArray(), Nodes = nodes }.Run();
                surface.Nodes = new NativeArray<TreeNode>(nodes.AsArray(), allocator);
                surface.PrimLayer = new NativeArray<int>(surface.Prims.Length, allocator);
                surface.NodeLayer = new NativeArray<int>(surface.Nodes.Length, allocator);
                new LayerJob
                {
                    Layers = layers.AsArray(),
                    Prims = surface.Prims,
                    Nodes = surface.Nodes,
                    PrimLayer = surface.PrimLayer,
                    NodeLayer = surface.NodeLayer,
                }.Run();
                surface.PrimClearance = new NativeArray<float>(surface.Prims.Length, allocator);
                surface.NodeClearance = new NativeArray<float>(surface.Nodes.Length, allocator);
                new PrimClearanceJob
                {
                    Nodes = surface.Nodes, Prims = surface.Prims, PrimLayer = surface.PrimLayer, NodeLayer = surface.NodeLayer,
                    Reach = reach, Clearance = surface.PrimClearance,
                }.Schedule(surface.Prims.Length, 128).Complete();
                new NodeClearanceJob { Nodes = surface.Nodes, PrimClearance = surface.PrimClearance, NodeClearance = surface.NodeClearance }.Run();
                return surface;
            }
            catch
            {
                surface.Dispose();
                throw;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct PrimClearanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<TreeNode> Nodes;
            [ReadOnly] public NativeArray<TreePrim> Prims;
            [ReadOnly] public NativeArray<int> PrimLayer;
            [ReadOnly] public NativeArray<int> NodeLayer;
            public float Reach;
            public NativeArray<float> Clearance;

            public void Execute(int i)
            {
                var p = Prims[i];
                Clearance[i] = TriangleClearance(Nodes, Prims, PrimLayer, NodeLayer, p.A, p.B, p.C, PrimLayer[i], Reach);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct NodeClearanceJob : IJob
        {
            [ReadOnly] public NativeArray<TreeNode> Nodes;
            [ReadOnly] public NativeArray<float> PrimClearance;
            public NativeArray<float> NodeClearance;

            public void Execute()
            {
                // A child is always added after its parent, so walking back visits children first.
                for (var i = Nodes.Length - 1; i >= 0; i--)
                {
                    var node = Nodes[i];
                    var most = 0f;
                    if (node.Count > 0)
                        for (var k = node.Start; k < node.Start + node.Count; k++) most = math.max(most, PrimClearance[k]);
                    else most = math.max(NodeClearance[node.Start], NodeClearance[node.Start + 1]);
                    NodeClearance[i] = most;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct GatherJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<int> TriangleLayer;
            public NativeList<TreePrim> Prims;
            public NativeList<int> Layers;

            public void Execute()
            {
                var n = Positions.Length;
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (math.any(tri < 0) || math.any(tri >= n)) continue;
                    float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
                    var normal = math.cross(b - a, c - a);
                    if (!(math.lengthsq(normal) > 0f)) continue;
                    Prims.Add(new TreePrim { A = a, B = b, C = c, N = math.normalize(normal), Triangle = Layers.Length });
                    Layers.Add(TriangleLayer[t]);
                }
            }
        }

        /// <summary>
        /// Builds the tree layer first: the top of the tree splits whole layers, by the median of their centres along
        /// the longest axis, and under each layer's node is that layer's own tree. A query skips its own layer at one
        /// node, where a tree over mixed layers would walk every mixed node the query's own surface runs through.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct BuildJob : IJob
        {
            public NativeArray<TreePrim> Prims;
            [ReadOnly] public NativeArray<int> Layers;
            public NativeList<TreeNode> Nodes;

            struct ByLayer : IComparer<TreePrim>
            {
                [ReadOnly] public NativeArray<int> Layers;

                public int Compare(TreePrim x, TreePrim y)
                {
                    var c = Layers[x.Triangle].CompareTo(Layers[y.Triangle]);
                    return c != 0 ? c : x.Triangle.CompareTo(y.Triangle);
                }
            }

            struct ByCentre : IComparer<int>
            {
                [ReadOnly] public NativeArray<float3> Centre;
                public int Axis;

                public int Compare(int x, int y)
                {
                    var c = Centre[x][Axis].CompareTo(Centre[y][Axis]);
                    return c != 0 ? c : x.CompareTo(y);
                }
            }

            public void Execute()
            {
                Nodes.Clear();
                var n = Prims.Length;
                if (n == 0) return;
                Prims.Sort(new ByLayer { Layers = Layers });

                var runs = new NativeList<int2>(Allocator.Temp);
                for (var i = 0; i < n; i++)
                {
                    if (i == 0 || Layers[Prims[i].Triangle] != Layers[Prims[i - 1].Triangle]) runs.Add(new int2(i, 0));
                    var r = runs[runs.Length - 1];
                    r.y++;
                    runs[runs.Length - 1] = r;
                }
                var lo = new NativeArray<float3>(runs.Length, Allocator.Temp);
                var hi = new NativeArray<float3>(runs.Length, Allocator.Temp);
                var centre = new NativeArray<float3>(runs.Length, Allocator.Temp);
                var order = new NativeArray<int>(runs.Length, Allocator.Temp);
                for (var r = 0; r < runs.Length; r++)
                {
                    var l = new float3(float.MaxValue);
                    var h = new float3(float.MinValue);
                    for (var i = runs[r].x; i < runs[r].x + runs[r].y; i++)
                    {
                        var p = Prims[i];
                        l = math.min(l, math.min(p.A, math.min(p.B, p.C)));
                        h = math.max(h, math.max(p.A, math.max(p.B, p.C)));
                    }
                    lo[r] = l;
                    hi[r] = h;
                    centre[r] = (l + h) * 0.5f;
                    order[r] = r;
                }

                Nodes.Add(default);
                var stack = new NativeList<int3>(64, Allocator.Temp);
                stack.Add(new int3(0, 0, runs.Length));
                var sub = new NativeList<TreeNode>(Allocator.Temp);
                while (stack.Length > 0)
                {
                    var e = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    int slot = e.x, start = e.y, count = e.z;
                    if (count == 1)
                    {
                        var run = runs[order[start]];
                        TriangleTree.Build(Prims.GetSubArray(run.x, run.y), sub);
                        // The layer's root takes the slot; the rest follow, their child and prim indices shifted.
                        var first = Nodes.Length;
                        for (var t = 0; t < sub.Length; t++)
                        {
                            var node = sub[t];
                            if (node.Count > 0) node.Start += run.x;
                            else node.Start = first + node.Start - 1;
                            if (t == 0) Nodes[slot] = node;
                            else Nodes.Add(node);
                        }
                        continue;
                    }
                    var cl = new float3(float.MaxValue);
                    var ch = new float3(float.MinValue);
                    var bl = new float3(float.MaxValue);
                    var bh = new float3(float.MinValue);
                    for (var i = start; i < start + count; i++)
                    {
                        cl = math.min(cl, centre[order[i]]);
                        ch = math.max(ch, centre[order[i]]);
                        bl = math.min(bl, lo[order[i]]);
                        bh = math.max(bh, hi[order[i]]);
                    }
                    var ext = ch - cl;
                    var axis = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
                    order.GetSubArray(start, count).Sort(new ByCentre { Centre = centre, Axis = axis });
                    var half = count / 2;
                    var left = Nodes.Length;
                    Nodes.Add(default);
                    Nodes.Add(default);
                    Nodes[slot] = new TreeNode { Min = bl, Max = bh, Start = left, Count = 0 };
                    stack.Add(new int3(left + 1, start + half, count - half));
                    stack.Add(new int3(left, start, half));
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct LayerJob : IJob
        {
            [ReadOnly] public NativeArray<int> Layers;
            [ReadOnly] public NativeArray<TreePrim> Prims;
            [ReadOnly] public NativeArray<TreeNode> Nodes;
            public NativeArray<int> PrimLayer;
            public NativeArray<int> NodeLayer;

            public void Execute()
            {
                for (var i = 0; i < Prims.Length; i++) PrimLayer[i] = Layers[Prims[i].Triangle];
                // A child is always added after its parent, so walking back visits children first.
                for (var i = Nodes.Length - 1; i >= 0; i--)
                {
                    var node = Nodes[i];
                    if (node.Count > 0)
                    {
                        var layer = PrimLayer[node.Start];
                        for (var k = node.Start + 1; k < node.Start + node.Count; k++)
                            if (PrimLayer[k] != layer) layer = -1;
                        NodeLayer[i] = layer;
                    }
                    else
                    {
                        var l = NodeLayer[node.Start];
                        NodeLayer[i] = l == NodeLayer[node.Start + 1] ? l : -1;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- queries, callable from Burst

        const int StackSize = 64;

        static float BoxPointSq(float3 p, in TreeNode node) => math.lengthsq(math.max(math.max(node.Min - p, p - node.Max), 0f));

        /// <summary>
        /// A query triangle and four axes that often separate it from what is near it: its normal, and in its plane the
        /// direction square to each edge. Along any axis at all, two sets whose projections lie more than a gap apart are
        /// more than that gap apart, so each test compares projections and nothing depends on an axis being exact: a
        /// sliver's normal, rounded into some other direction, only separates less.
        /// </summary>
        struct Query
        {
            public float3 A, B, C, Lo, Hi;
            float3 _n, _m0, _m1, _m2;
            float2 _sn, _s0, _s1, _s2;

            public Query(float3 a, float3 b, float3 c)
            {
                A = a;
                B = b;
                C = c;
                Lo = math.min(a, math.min(b, c));
                Hi = math.max(a, math.max(b, c));
                _n = math.normalizesafe(math.cross(b - a, c - a));
                _m0 = math.normalizesafe(math.cross(b - a, _n));
                _m1 = math.normalizesafe(math.cross(c - b, _n));
                _m2 = math.normalizesafe(math.cross(a - c, _n));
                _sn = Span(_n, a, b, c);
                _s0 = Span(_m0, a, b, c);
                _s1 = Span(_m1, a, b, c);
                _s2 = Span(_m2, a, b, c);
            }

            static float2 Span(float3 axis, float3 a, float3 b, float3 c)
            {
                float da = math.dot(axis, a), db = math.dot(axis, b), dc = math.dot(axis, c);
                return new float2(math.min(da, math.min(db, dc)), math.max(da, math.max(db, dc)));
            }

            static bool Apart(float2 s, float2 t, float gap) => t.x - s.y > gap || s.x - t.y > gap;

            static float2 BoxSpan(float3 axis, float3 centre, float3 half)
            {
                var d = math.dot(axis, centre);
                var e = math.dot(math.abs(axis), half);
                return new float2(d - e, d + e);
            }

            public float BoxBoxSq(in TreeNode node) => math.lengthsq(math.max(math.max(node.Min - Hi, Lo - node.Max), 0f));

            public bool BoxApart(in TreeNode node, float gap)
            {
                if (math.any(node.Min - Hi > gap) || math.any(Lo - node.Max > gap)) return true;
                var centre = (node.Min + node.Max) * 0.5f;
                var half = (node.Max - node.Min) * 0.5f;
                return Apart(_sn, BoxSpan(_n, centre, half), gap) || Apart(_s0, BoxSpan(_m0, centre, half), gap) ||
                       Apart(_s1, BoxSpan(_m1, centre, half), gap) || Apart(_s2, BoxSpan(_m2, centre, half), gap);
            }

            public bool PrimApart(in TreePrim prim, float gap)
            {
                var plo = math.min(prim.A, math.min(prim.B, prim.C));
                var phi = math.max(prim.A, math.max(prim.B, prim.C));
                if (math.any(plo - Hi > gap) || math.any(Lo - phi > gap)) return true;
                if (Apart(_sn, Span(_n, prim.A, prim.B, prim.C), gap) || Apart(_s0, Span(_m0, prim.A, prim.B, prim.C), gap) ||
                    Apart(_s1, Span(_m1, prim.A, prim.B, prim.C), gap) || Apart(_s2, Span(_m2, prim.A, prim.B, prim.C), gap)) return true;
                return Apart(Span(prim.N, prim.A, prim.B, prim.C), Span(prim.N, A, B, C), gap);
            }
        }

        /// <summary>
        /// The distance from <paramref name="p"/> to the nearest triangle of any layer but <paramref name="layer"/>,
        /// or <paramref name="cap"/> when none is nearer.
        /// </summary>
        internal static unsafe float PointClearance(in NativeArray<TreeNode> nodes, in NativeArray<TreePrim> prims, in NativeArray<int> primLayer,
            in NativeArray<int> nodeLayer, float3 p, int layer, float cap)
        {
            var best = cap * cap;
            if (nodes.Length == 0) return cap;
            var stack = stackalloc int[StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                if (nodeLayer[i] == layer) continue;
                var node = nodes[i];
                if (BoxPointSq(p, node) >= best) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (primLayer[k] == layer) continue;
                        var prim = prims[k];
                        best = math.min(best, TriangleDistance.PointSq(p, prim.A, prim.B, prim.C));
                    }
                    continue;
                }
                var l = node.Start;
                if (BoxPointSq(p, nodes[l]) <= BoxPointSq(p, nodes[l + 1]))
                {
                    stack[top++] = l + 1;
                    stack[top++] = l;
                }
                else
                {
                    stack[top++] = l;
                    stack[top++] = l + 1;
                }
            }
            return math.sqrt(best);
        }

        /// <summary>
        /// The distance from triangle abc to the nearest triangle of any layer but <paramref name="layer"/>, or
        /// <paramref name="cap"/> when none is nearer. 0 when one touches or crosses it.
        /// </summary>
        internal static unsafe float TriangleClearance(in NativeArray<TreeNode> nodes, in NativeArray<TreePrim> prims, in NativeArray<int> primLayer,
            in NativeArray<int> nodeLayer, float3 a, float3 b, float3 c, int layer, float cap)
        {
            var best = cap * cap;
            if (nodes.Length == 0) return cap;
            var q = new Query(a, b, c);
            var stack = stackalloc int[StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0 && best > 0f)
            {
                var i = stack[--top];
                if (nodeLayer[i] == layer) continue;
                var node = nodes[i];
                if (q.BoxBoxSq(node) >= best || q.BoxApart(node, math.sqrt(best))) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (primLayer[k] == layer) continue;
                        var prim = prims[k];
                        if (q.PrimApart(prim, math.sqrt(best))) continue;
                        best = math.min(best, TriangleDistance.TriangleSq(a, b, c, prim.A, prim.B, prim.C));
                    }
                    continue;
                }
                var l = node.Start;
                if (q.BoxBoxSq(nodes[l]) <= q.BoxBoxSq(nodes[l + 1]))
                {
                    stack[top++] = l + 1;
                    stack[top++] = l;
                }
                else
                {
                    stack[top++] = l;
                    stack[top++] = l + 1;
                }
            }
            return math.sqrt(best);
        }

        /// <summary>The length of the line along which triangle abc passes through every layer but <paramref name="layer"/>.</summary>
        internal static unsafe float CrossingLength(in NativeArray<TreeNode> nodes, in NativeArray<TreePrim> prims, in NativeArray<int> primLayer,
            in NativeArray<int> nodeLayer, float3 a, float3 b, float3 c, int layer)
        {
            if (nodes.Length == 0) return 0f;
            var total = 0f;
            var q = new Query(a, b, c);
            var stack = stackalloc int[StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                if (nodeLayer[i] == layer) continue;
                var node = nodes[i];
                if (q.BoxApart(node, 0f)) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (primLayer[k] == layer) continue;
                        var prim = prims[k];
                        if (q.PrimApart(prim, 0f)) continue;
                        total += TriangleDistance.IntersectionLength(a, b, c, prim.A, prim.B, prim.C);
                    }
                    continue;
                }
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
            return total;
        }

        /// <summary>
        /// True when some triangle P of a layer other than <paramref name="layer"/> comes nearer to triangle abc than
        /// <paramref name="keep"/> times the larger of P's own clearance and <paramref name="least"/>. P's clearance is
        /// the gap where P is, measured on a triangle of the source as small as the source's own, so it does not shrink
        /// as decimated triangles grow; <paramref name="least"/> is the gap on abc's side.
        /// </summary>
        internal static unsafe bool AnyWithin(in NativeArray<TreeNode> nodes, in NativeArray<TreePrim> prims, in NativeArray<int> primLayer,
            in NativeArray<int> nodeLayer, in NativeArray<float> primClearance, in NativeArray<float> nodeClearance,
            float3 a, float3 b, float3 c, int layer, float keep, float least)
        {
            if (nodes.Length == 0 || !(keep > 0f)) return false;
            var q = new Query(a, b, c);
            var stack = stackalloc int[StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                if (nodeLayer[i] == layer) continue;
                var node = nodes[i];
                var reach = keep * math.max(nodeClearance[i], least);
                if (!(reach > 0f) || q.BoxApart(node, reach)) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (primLayer[k] == layer) continue;
                        var prim = prims[k];
                        var r = keep * math.max(primClearance[k], least);
                        if (!(r > 0f) || q.PrimApart(prim, r)) continue;
                        if (TriangleDistance.TriangleSq(a, b, c, prim.A, prim.B, prim.C) < r * r) return true;
                    }
                    continue;
                }
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
            return false;
        }
    }

    /// <summary>Splits a mesh's triangles into layers: the pieces connected through shared corners, corners at one exact position being one.</summary>
    public static class Layers
    {
        /// <summary>
        /// Per triangle, the piece it belongs to, numbered from 0 in order of first appearance; <paramref name="count"/>
        /// is how many pieces there are. A triangle naming a vertex out of range is a piece of its own.
        /// </summary>
        public static NativeArray<int> Pieces(NativeArray<float3> positions, NativeArray<int3> triangles, Allocator allocator, out int count)
        {
            var result = new NativeArray<int>(triangles.Length, allocator);
            using var countOut = new NativeReference<int>(Allocator.Persistent);
            new PiecesJob { Positions = positions, Triangles = triangles, Piece = result, Count = countOut }.Run();
            count = countOut.Value;
            return result;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct PiecesJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Triangles;
            public NativeArray<int> Piece;
            public NativeReference<int> Count;

            public void Execute()
            {
                var n = Positions.Length;
                var parent = new NativeArray<int>(n, Allocator.Temp);
                var map = new NativeParallelHashMap<int3, int>(n, Allocator.Temp);
                for (var v = 0; v < n; v++)
                {
                    var p = Positions[v];
                    p = math.select(p, float3.zero, p == float3.zero); // -0 welds with +0
                    var key = math.asint(p);
                    if (map.TryGetValue(key, out var first)) parent[v] = first;
                    else
                    {
                        map.Add(key, v);
                        parent[v] = v;
                    }
                }
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (math.any(tri < 0) || math.any(tri >= n)) continue;
                    Union(ref parent, tri.x, tri.y);
                    Union(ref parent, tri.x, tri.z);
                }
                var label = new NativeArray<int>(n, Allocator.Temp);
                for (var v = 0; v < n; v++) label[v] = -1;
                var count = 0;
                for (var t = 0; t < Triangles.Length; t++)
                {
                    var tri = Triangles[t];
                    if (math.any(tri < 0) || math.any(tri >= n))
                    {
                        Piece[t] = count++;
                        continue;
                    }
                    var root = Find(ref parent, tri.x);
                    if (label[root] < 0) label[root] = count++;
                    Piece[t] = label[root];
                }
                Count.Value = count;
            }

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

            static void Union(ref NativeArray<int> parent, int a, int b)
            {
                a = Find(ref parent, a);
                b = Find(ref parent, b);
                if (a == b) return;
                if (a < b) parent[b] = a;
                else parent[a] = b;
            }
        }
    }
}
