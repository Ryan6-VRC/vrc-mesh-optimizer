using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Geometry;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Clearance
{
    /// <summary>One source of layers for <see cref="ClearanceSurface.Build"/>: a mesh's triangles, which of them it keeps, and each kept one's layer.</summary>
    internal struct ClearanceLayerSource
    {
        public NativeArray<float3> Positions;
        public NativeArray<int3> Triangles;
        /// <summary>Per triangle, its layer: an id unique across every source. An excluded triangle's is never read.</summary>
        public NativeArray<int> TriangleLayer;
        /// <summary>Per triangle, true for one the surface leaves out; unset leaves none out.</summary>
        public NativeArray<bool> Excluded;
    }

    /// <summary>
    /// Every layer's surface as it was before decimation, in the one space the evaluation meshes share, with a tree
    /// for asking how close one layer comes to the others. A layer is a connected piece of one mesh (see
    /// <see cref="Layers"/>): a skirt and its lining modelled as separate pieces of one mesh are two layers, while
    /// the triangles around a vertex, being one piece, are never each other's neighbours. Read only once built, so a job
    /// holds it as one field and asks it the queries below.
    /// </summary>
    internal struct ClearanceSurface : IDisposable
    {
        [ReadOnly] internal NativeArray<TreeNode> Nodes;
        /// <summary>In leaf order; <see cref="TreePrim.Triangle"/> is the prim's index in build order.</summary>
        [ReadOnly] internal NativeArray<TreePrim> Prims;
        /// <summary>Per prim in leaf order, its layer.</summary>
        [ReadOnly] internal NativeArray<int> PrimLayer;
        /// <summary>Per node, the one layer every prim under it belongs to, or -1 when they are mixed.</summary>
        [ReadOnly] internal NativeArray<int> NodeLayer;
        /// <summary>
        /// Per prim in leaf order, its clearance from every other layer, capped at <see cref="Reach"/>: the gap on this
        /// side, where another layer comes near it. It is the gap to whichever layer is nearest, which need not be the
        /// layer a query asks for; <see cref="PrimNearest"/> says which.
        /// </summary>
        [ReadOnly] internal NativeArray<float> PrimClearance;
        /// <summary>Per node, the largest <see cref="PrimClearance"/> under it.</summary>
        [ReadOnly] internal NativeArray<float> NodeClearance;
        /// <summary>Per prim in leaf order, the layer its <see cref="PrimClearance"/> came from, or -1 when none is within <see cref="Reach"/>.</summary>
        [ReadOnly] internal NativeArray<int> PrimNearest;
        /// <summary>
        /// Per node, the one layer every prim under it has as <see cref="PrimNearest"/>, prims with none aside: -1 when
        /// none has one, -2 when they differ.
        /// </summary>
        [ReadOnly] internal NativeArray<int> NodeNearest;
        /// <summary>The cap on every clearance here, in metres.</summary>
        public float Reach;

        public bool IsCreated => Nodes.IsCreated;
        public int TriangleCount => Prims.IsCreated ? Prims.Length : 0;

        /// <summary>A surface of no layers, for a job that must hold one when the rule is off; every query answers "nothing near".</summary>
        internal static ClearanceSurface None(Allocator allocator) => new ClearanceSurface
        {
            Nodes = new NativeArray<TreeNode>(0, allocator),
            Prims = new NativeArray<TreePrim>(0, allocator),
            PrimLayer = new NativeArray<int>(0, allocator),
            NodeLayer = new NativeArray<int>(0, allocator),
            PrimClearance = new NativeArray<float>(0, allocator),
            NodeClearance = new NativeArray<float>(0, allocator),
            PrimNearest = new NativeArray<int>(0, allocator),
            NodeNearest = new NativeArray<int>(0, allocator),
        };

        public void Dispose()
        {
            if (Nodes.IsCreated) Nodes.Dispose();
            if (Prims.IsCreated) Prims.Dispose();
            if (PrimLayer.IsCreated) PrimLayer.Dispose();
            if (NodeLayer.IsCreated) NodeLayer.Dispose();
            if (PrimClearance.IsCreated) PrimClearance.Dispose();
            if (NodeClearance.IsCreated) NodeClearance.Dispose();
            if (PrimNearest.IsCreated) PrimNearest.Dispose();
            if (NodeNearest.IsCreated) NodeNearest.Dispose();
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
                if (s.Excluded.IsCreated && s.Excluded.Length != s.Triangles.Length)
                    throw new ArgumentException($"a source has {s.Triangles.Length} triangles and {s.Excluded.Length} exclusion flags; give one flag per triangle");
                total += s.Triangles.Length;
            }
            var surface = new ClearanceSurface { Reach = reach };
            try
            {
                using var prims = new NativeList<TreePrim>(total, Allocator.Persistent);
                using var layers = new NativeList<int>(total, Allocator.Persistent);
                foreach (var s in sources)
                {
                    using var excluded = new OptionalArray<bool>(s.Excluded);
                    new GatherJob { Positions = s.Positions, Triangles = s.Triangles, TriangleLayer = s.TriangleLayer, Excluded = excluded.Array, Prims = prims, Layers = layers }.Run();
                }
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
                surface.PrimNearest = new NativeArray<int>(surface.Prims.Length, allocator);
                surface.NodeNearest = new NativeArray<int>(surface.Nodes.Length, allocator);
                new PrimClearanceJob
                {
                    Nodes = surface.Nodes, Prims = surface.Prims, PrimLayer = surface.PrimLayer, NodeLayer = surface.NodeLayer,
                    Reach = reach, Clearance = surface.PrimClearance, Nearest = surface.PrimNearest,
                }.Schedule(surface.Prims.Length, 128).Complete();
                new NodeClearanceJob
                {
                    Nodes = surface.Nodes, PrimClearance = surface.PrimClearance, PrimNearest = surface.PrimNearest,
                    NodeClearance = surface.NodeClearance, NodeNearest = surface.NodeNearest,
                }.Run();
                return surface;
            }
            catch
            {
                surface.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Writes the surface's own <see cref="PrimClearance"/>, so it holds the tree's arrays apart rather than the surface: a
        /// job may not hold one container as both read-only and written.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct PrimClearanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<TreeNode> Nodes;
            [ReadOnly] public NativeArray<TreePrim> Prims;
            [ReadOnly] public NativeArray<int> PrimLayer;
            [ReadOnly] public NativeArray<int> NodeLayer;
            public float Reach;
            public NativeArray<float> Clearance;
            public NativeArray<int> Nearest;

            public void Execute(int i)
            {
                var tree = new ClearanceSurface { Nodes = Nodes, Prims = Prims, PrimLayer = PrimLayer, NodeLayer = NodeLayer };
                var p = Prims[i];
                Clearance[i] = tree.TriangleClearance(p.A, p.B, p.C, PrimLayer[i], Reach, out var nearest);
                Nearest[i] = nearest;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct NodeClearanceJob : IJob
        {
            [ReadOnly] public NativeArray<TreeNode> Nodes;
            [ReadOnly] public NativeArray<float> PrimClearance;
            [ReadOnly] public NativeArray<int> PrimNearest;
            public NativeArray<float> NodeClearance;
            public NativeArray<int> NodeNearest;

            public void Execute()
            {
                // A child is always added after its parent, so walking back visits children first.
                for (var i = Nodes.Length - 1; i >= 0; i--)
                {
                    var node = Nodes[i];
                    var most = 0f;
                    var nearest = -1;
                    if (node.Count > 0)
                        for (var k = node.Start; k < node.Start + node.Count; k++)
                        {
                            most = math.max(most, PrimClearance[k]);
                            nearest = Join(nearest, PrimNearest[k]);
                        }
                    else
                    {
                        most = math.max(NodeClearance[node.Start], NodeClearance[node.Start + 1]);
                        nearest = Join(NodeNearest[node.Start], NodeNearest[node.Start + 1]);
                    }
                    NodeClearance[i] = most;
                    NodeNearest[i] = nearest;
                }
            }

            static int Join(int x, int y) => x == -1 ? y : y == -1 || y == x ? x : -2;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct GatherJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<int> TriangleLayer;
            /// <summary>Per triangle, or empty for none excluded.</summary>
            [ReadOnly] public NativeArray<bool> Excluded;
            public NativeList<TreePrim> Prims;
            public NativeList<int> Layers;

            public void Execute()
            {
                var n = Positions.Length;
                for (var t = 0; t < Triangles.Length; t++)
                {
                    if (Excluded.Length > 0 && Excluded[t]) continue;
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
        internal unsafe float PointClearance(float3 p, int layer, float cap)
        {
            var best = cap * cap;
            if (Nodes.Length == 0) return cap;
            var stack = stackalloc int[TriangleTree.StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                if (NodeLayer[i] == layer) continue;
                var node = Nodes[i];
                if (TriangleTree.BoxPointSq(p, node) >= best) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (PrimLayer[k] == layer) continue;
                        var prim = Prims[k];
                        best = math.min(best, TriangleDistance.PointSq(p, prim.A, prim.B, prim.C));
                    }
                    continue;
                }
                var l = node.Start;
                if (TriangleTree.BoxPointSq(p, Nodes[l]) <= TriangleTree.BoxPointSq(p, Nodes[l + 1]))
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
        internal float TriangleClearance(float3 a, float3 b, float3 c, int layer, float cap) => TriangleClearance(a, b, c, layer, cap, out _);

        /// <summary>As the overload above; <paramref name="nearest"/> is the layer of the triangle found nearest, or -1 when none is nearer than <paramref name="cap"/>.</summary>
        internal unsafe float TriangleClearance(float3 a, float3 b, float3 c, int layer, float cap, out int nearest)
        {
            nearest = -1;
            var best = cap * cap;
            if (Nodes.Length == 0) return cap;
            var q = new Query(a, b, c);
            var stack = stackalloc int[TriangleTree.StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0 && best > 0f)
            {
                var i = stack[--top];
                if (NodeLayer[i] == layer) continue;
                var node = Nodes[i];
                if (q.BoxBoxSq(node) >= best || q.BoxApart(node, math.sqrt(best))) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (PrimLayer[k] == layer) continue;
                        var prim = Prims[k];
                        if (q.PrimApart(prim, math.sqrt(best))) continue;
                        var d = TriangleDistance.TriangleSq(a, b, c, prim.A, prim.B, prim.C);
                        if (d < best)
                        {
                            best = d;
                            nearest = PrimLayer[k];
                        }
                    }
                    continue;
                }
                var l = node.Start;
                if (q.BoxBoxSq(Nodes[l]) <= q.BoxBoxSq(Nodes[l + 1]))
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

        /// <summary>The distance from <paramref name="p"/> to <paramref name="layer"/>'s triangles alone, or <paramref name="cap"/> when none is nearer.</summary>
        internal unsafe float PointToLayer(float3 p, int layer, float cap)
        {
            var best = cap * cap;
            if (Nodes.Length == 0) return cap;
            var stack = stackalloc int[TriangleTree.StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                var only = NodeLayer[i];
                if (only >= 0 && only != layer) continue;
                var node = Nodes[i];
                if (TriangleTree.BoxPointSq(p, node) >= best) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (PrimLayer[k] != layer) continue;
                        var prim = Prims[k];
                        best = math.min(best, TriangleDistance.PointSq(p, prim.A, prim.B, prim.C));
                    }
                    continue;
                }
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
            return math.sqrt(best);
        }

        /// <summary>The distance from triangle abc to <paramref name="layer"/>'s triangles alone, or <paramref name="cap"/> when none is nearer.</summary>
        internal unsafe float TriangleToLayer(float3 a, float3 b, float3 c, int layer, float cap)
        {
            var best = cap * cap;
            if (Nodes.Length == 0) return cap;
            var q = new Query(a, b, c);
            var stack = stackalloc int[TriangleTree.StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0 && best > 0f)
            {
                var i = stack[--top];
                var only = NodeLayer[i];
                if (only >= 0 && only != layer) continue;
                var node = Nodes[i];
                if (q.BoxBoxSq(node) >= best || q.BoxApart(node, math.sqrt(best))) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (PrimLayer[k] != layer) continue;
                        var prim = Prims[k];
                        if (q.PrimApart(prim, math.sqrt(best))) continue;
                        best = math.min(best, TriangleDistance.TriangleSq(a, b, c, prim.A, prim.B, prim.C));
                    }
                    continue;
                }
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
            return math.sqrt(best);
        }

        /// <summary>The length of the line along which triangle abc passes through every layer but <paramref name="layer"/>.</summary>
        internal float CrossingLength(float3 a, float3 b, float3 c, int layer) => CrossingLength(a, b, c, layer, 0f, out _);

        /// <summary>
        /// As the overload without <paramref name="drift"/>; with it above 0, also whether some point of that line lies
        /// farther than <paramref name="drift"/> from <paramref name="layer"/>'s own source: a crossing where this layer
        /// is no longer where it was, rather than where it already crossed.
        /// </summary>
        internal unsafe float CrossingLength(float3 a, float3 b, float3 c, int layer, float drift, out bool strays)
        {
            strays = false;
            if (Nodes.Length == 0) return 0f;
            var total = 0f;
            var q = new Query(a, b, c);
            var stack = stackalloc int[TriangleTree.StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                if (NodeLayer[i] == layer) continue;
                var node = Nodes[i];
                if (q.BoxApart(node, 0f)) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (PrimLayer[k] == layer) continue;
                        var prim = Prims[k];
                        if (q.PrimApart(prim, 0f)) continue;
                        if (!TriangleDistance.Intersect(a, b, c, prim.A, prim.B, prim.C, TriangleDistance.Touch, out var x)) continue;
                        total += x.Length;
                        if (drift > 0f && !strays &&
                            (PointToLayer(x.Start, layer, drift * 2f) > drift ||
                             PointToLayer(x.End, layer, drift * 2f) > drift ||
                             PointToLayer((x.Start + x.End) * 0.5f, layer, drift * 2f) > drift))
                            strays = true;
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
        /// <paramref name="keep"/> times the gap between the two layers there. The gap is read on P's side, measured on a
        /// triangle of the source as small as the source's own, so it does not shrink as decimated triangles grow, and on
        /// abc's side, <paramref name="least"/>, the clearance of the source around abc's corners; the larger counts.
        /// Where P's own clearance is to a third layer nearer it than abc's, that clearance and <paramref name="least"/>
        /// are the gaps to other layers, not between this pair: P's distance to <paramref name="layer"/> alone and abc's
        /// corners' distance to P's layer alone then count as well. abc's corners must lie on <paramref name="layer"/>'s
        /// source, which bounds every gap read here by P's distance to a corner.
        /// </summary>
        internal bool AnyWithin(float3 a, float3 b, float3 c, int layer, float keep, float least)
        {
            var gaps = new PairGaps(this, Allocator.Temp);
            try
            {
                return AnyWithin(a, b, c, layer, keep, least, true, ref gaps, out _);
            }
            finally
            {
                gaps.Dispose();
            }
        }

        /// <summary>
        /// As the overload above, with <paramref name="pairGap"/> off reading only the gaps to the nearest layer of any, as
        /// the rule did before it read the pair's; <paramref name="byPair"/> is true when the answer is true only because
        /// of the pair's gap at the triangle P that decided it.
        /// </summary>
        /// <param name="gaps">The pair's gaps read so far, kept for the next call.</param>
        internal unsafe bool AnyWithin(float3 a, float3 b, float3 c, int layer, float keep, float least, bool pairGap, ref PairGaps gaps, out bool byPair)
        {
            byPair = false;
            if (Nodes.Length == 0 || !(keep > 0f)) return false;
            var q = new Query(a, b, c);
            // The pair's gap at P is no more than P's distance to the corner nearest it, which is no more than d, P's
            // distance to abc, plus the farthest any point of abc lies from its nearest corner: under the longest edge
            // over sqrt(3). And no gap is more than the reach. So P is refused through the pair's gap only when
            // d < keep * (d + edge / sqrt(3)), and never beyond this.
            var edge = math.max(math.distance(a, b), math.max(math.distance(b, c), math.distance(c, a)));
            var pairReach = keep * math.min(Reach, edge * 0.5774f / (1f - keep));
            var sides = new CornerGaps();
            var stack = stackalloc int[TriangleTree.StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var i = stack[--top];
                if (NodeLayer[i] == layer) continue;
                var node = Nodes[i];
                var nearest = NodeNearest[i];
                var reach = keep * math.max(NodeClearance[i], least);
                if (pairGap && nearest != -1 && nearest != layer)
                {
                    // Under one layer's node, the pair's gaps are known: the corners' to that layer, and the most any
                    // of its triangles read through the pair lies from this one.
                    var only = NodeLayer[i];
                    var pair = only < 0 ? Reach : math.max(gaps.NodeToLayer(this, i, layer), sides.Get(this, ref gaps, a, b, c, only));
                    reach = math.max(reach, math.min(pairReach, keep * pair));
                }
                if (!(reach > 0f) || q.BoxApart(node, reach)) continue;
                if (node.Count > 0)
                {
                    for (var k = node.Start; k < node.Start + node.Count; k++)
                    {
                        if (PrimLayer[k] == layer) continue;
                        var prim = Prims[k];
                        var r = keep * math.max(PrimClearance[k], least);
                        var own = PrimNearest[k];
                        if (!pairGap || own == -1 || own == layer)
                        {
                            if (!(r > 0f) || q.PrimApart(prim, r)) continue;
                            if (TriangleDistance.TriangleSq(a, b, c, prim.A, prim.B, prim.C) < r * r) return true;
                            continue;
                        }
                        if (q.PrimApart(prim, reach)) continue;
                        var side = keep * sides.Get(this, ref gaps, a, b, c, PrimLayer[k]);
                        var bound = math.max(r, side);
                        var pair = keep * gaps.PrimToLayer(this, k, layer);
                        bound = math.max(bound, pair);
                        if (!(bound > 0f) || q.PrimApart(prim, bound)) continue;
                        var d = TriangleDistance.TriangleSq(a, b, c, prim.A, prim.B, prim.C);
                        if (d < r * r) return true;
                        if (!(d < bound * bound)) continue;
                        // Both gaps are at most P's distance to the corner nearest it, for corners on this layer's source,
                        // which the caller promises; tested too, P is refused exactly where the bound alone refused it.
                        var corner = keep * math.sqrt(math.min(TriangleDistance.PointSq(a, prim.A, prim.B, prim.C),
                            math.min(TriangleDistance.PointSq(b, prim.A, prim.B, prim.C), TriangleDistance.PointSq(c, prim.A, prim.B, prim.C))));
                        if (d < corner * corner) return byPair = true;
                    }
                    continue;
                }
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
            return false;
        }
    }

    /// <summary>The corners of one query's distance to each layer asked of, the first few kept for the rest of the query.</summary>
    internal unsafe struct CornerGaps
    {
        const int Slots = 4;
        fixed int _layer[Slots];
        fixed float _gap[Slots];
        int _count;

        /// <summary>The least of the distances from a, b and c to <paramref name="layer"/>, capped at the surface's reach.</summary>
        public float Get(in ClearanceSurface surface, ref PairGaps gaps, float3 a, float3 b, float3 c, int layer)
        {
            for (var s = 0; s < _count; s++)
                if (_layer[s] == layer) return _gap[s];
            var gap = math.min(gaps.PointToLayer(surface, a, layer), math.min(gaps.PointToLayer(surface, b, layer), gaps.PointToLayer(surface, c, layer)));
            if (_count < Slots)
            {
                _layer[_count] = layer;
                _gap[_count++] = gap;
            }
            return gap;
        }
    }

    /// <summary>
    /// The pair's gaps <see cref="ClearanceSurface.AnyWithin(float3, float3, float3, int, float, float, bool, ref PairGaps, out bool)"/>
    /// has read, each capped at the surface's reach: a source triangle's distance to a layer, the most of those under a
    /// node, and a point's distance to a layer. A value depends only on the surface and its key, so reading it here
    /// decides exactly as searching again would. One per collapse job, which asks of the same source triangles and
    /// corners many times over.
    /// </summary>
    internal struct PairGaps : IDisposable
    {
        // Per node and per prim, the last layer read for it and what was read, in front of the maps that hold every one.
        NativeArray<int> _nodeLayer, _primLayer;
        NativeArray<float> _nodeGap, _primGap;
        NativeParallelHashMap<int2, float> _prim;
        NativeParallelHashMap<int4, float> _point;
        NativeParallelHashMap<int2, float> _node;

        public PairGaps(in ClearanceSurface surface, Allocator allocator)
        {
            _nodeLayer = new NativeArray<int>(surface.Nodes.Length, allocator, NativeArrayOptions.UninitializedMemory);
            _primLayer = new NativeArray<int>(surface.Prims.Length, allocator, NativeArrayOptions.UninitializedMemory);
            _nodeGap = new NativeArray<float>(surface.Nodes.Length, allocator, NativeArrayOptions.UninitializedMemory);
            _primGap = new NativeArray<float>(surface.Prims.Length, allocator, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < _nodeLayer.Length; i++) _nodeLayer[i] = -1;
            for (var i = 0; i < _primLayer.Length; i++) _primLayer[i] = -1;
            _prim = new NativeParallelHashMap<int2, float>(64, allocator);
            _point = new NativeParallelHashMap<int4, float>(64, allocator);
            _node = new NativeParallelHashMap<int2, float>(64, allocator);
        }

        /// <summary>
        /// The most <see cref="PrimToLayer"/> reads for <paramref name="layer"/> under node <paramref name="i"/>, over the
        /// triangles whose nearest layer is a third one; 0 for none.
        /// </summary>
        public float NodeToLayer(in ClearanceSurface surface, int i, int layer)
        {
            if (_nodeLayer[i] == layer) return _nodeGap[i];
            var key = new int2(i, layer);
            if (_node.TryGetValue(key, out var most))
            {
                _nodeLayer[i] = layer;
                return _nodeGap[i] = most;
            }
            var node = surface.Nodes[i];
            most = 0f;
            if (node.Count > 0)
            {
                for (var k = node.Start; k < node.Start + node.Count; k++)
                {
                    var own = surface.PrimNearest[k];
                    if (own != -1 && own != layer && surface.PrimLayer[k] != layer) most = math.max(most, PrimToLayer(surface, k, layer));
                }
            }
            else if (surface.NodeNearest[i] != -1 && surface.NodeNearest[i] != layer)
                most = math.max(NodeToLayer(surface, node.Start, layer), NodeToLayer(surface, node.Start + 1, layer));
            _node.Add(key, most);
            _nodeLayer[i] = layer;
            return _nodeGap[i] = most;
        }

        /// <summary>Prim <paramref name="k"/>'s distance to <paramref name="layer"/>, capped at the reach, searched once per prim and layer.</summary>
        public float PrimToLayer(in ClearanceSurface surface, int k, int layer)
        {
            if (_primLayer[k] == layer) return _primGap[k];
            var key = new int2(k, layer);
            if (!_prim.TryGetValue(key, out var gap))
            {
                var prim = surface.Prims[k];
                gap = surface.TriangleToLayer(prim.A, prim.B, prim.C, layer, surface.Reach);
                _prim.Add(key, gap);
            }
            _primLayer[k] = layer;
            return _primGap[k] = gap;
        }

        /// <summary><paramref name="p"/>'s distance to <paramref name="layer"/>, capped at the reach, searched once per point and layer.</summary>
        public float PointToLayer(in ClearanceSurface surface, float3 p, int layer)
        {
            var key = new int4(math.asint(p), layer);
            if (_point.TryGetValue(key, out var gap)) return gap;
            gap = surface.PointToLayer(p, layer, surface.Reach);
            _point.Add(key, gap);
            return gap;
        }

        public void Dispose()
        {
            if (_nodeLayer.IsCreated) _nodeLayer.Dispose();
            if (_primLayer.IsCreated) _primLayer.Dispose();
            if (_nodeGap.IsCreated) _nodeGap.Dispose();
            if (_primGap.IsCreated) _primGap.Dispose();
            if (_prim.IsCreated) _prim.Dispose();
            if (_point.IsCreated) _point.Dispose();
            if (_node.IsCreated) _node.Dispose();
        }
    }

    /// <summary>Splits a mesh's triangles into layers: the pieces connected through shared corners, corners at one exact position being one.</summary>
    internal static class Layers
    {
        /// <summary>
        /// Per triangle, the piece it belongs to, numbered from 0 in order of first appearance; <paramref name="count"/>
        /// is how many pieces there are. A triangle naming a vertex out of range is a piece of its own.
        /// </summary>
        public static NativeArray<int> Pieces(NativeArray<float3> positions, NativeArray<int3> triangles, Allocator allocator, out int count) =>
            Pieces(positions, triangles, default, allocator, out count);

        /// <summary>
        /// As the overload without <paramref name="excluded"/>, but a triangle it flags joins nothing and is in no piece, -1,
        /// so the others are numbered as if it were absent.
        /// </summary>
        /// <param name="excluded">Per triangle; unset excludes none.</param>
        public static NativeArray<int> Pieces(NativeArray<float3> positions, NativeArray<int3> triangles, NativeArray<bool> excluded, Allocator allocator, out int count)
        {
            if (excluded.IsCreated && excluded.Length != triangles.Length)
                throw new ArgumentException($"{excluded.Length} exclusion flags for {triangles.Length} triangles; give one per triangle", nameof(excluded));
            var result = new NativeArray<int>(triangles.Length, allocator);
            using var exclude = new OptionalArray<bool>(excluded);
            using var countOut = new NativeReference<int>(Allocator.Persistent);
            new PiecesJob { Positions = positions, Triangles = triangles, Excluded = exclude.Array, Piece = result, Count = countOut }.Run();
            count = countOut.Value;
            return result;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct PiecesJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Triangles;
            /// <summary>Per triangle, or empty for none excluded.</summary>
            [ReadOnly] public NativeArray<bool> Excluded;
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
                    if ((Excluded.Length > 0 && Excluded[t]) || math.any(tri < 0) || math.any(tri >= n)) continue;
                    Union(ref parent, tri.x, tri.y);
                    Union(ref parent, tri.x, tri.z);
                }
                var label = new NativeArray<int>(n, Allocator.Temp);
                for (var v = 0; v < n; v++) label[v] = -1;
                var count = 0;
                for (var t = 0; t < Triangles.Length; t++)
                {
                    if (Excluded.Length > 0 && Excluded[t])
                    {
                        Piece[t] = -1;
                        continue;
                    }
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
