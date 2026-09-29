using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Geometry
{
    /// <summary>A leaf when Count > 0, over prims [Start, Start + Count); otherwise its children sit at Start and Start + 1.</summary>
    internal struct TreeNode
    {
        public float3 Min;
        public int Start;
        public float3 Max;
        public int Count;
    }

    /// <summary>One target triangle: its corners, its unit face normal (zero when degenerate), and its index in its surface.</summary>
    internal struct TreePrim
    {
        public float3 A;
        public float3 B;
        public float3 C;
        public float3 N;
        public int Triangle;
    }

    /// <summary>
    /// A bounding volume hierarchy for closest-point and overlap queries against one surface in one configuration. Built
    /// by median split on the longest axis of the centroids, so the tree is balanced and its depth is about
    /// log2(triangles / leaf size), and the build is a pure function of its input.
    /// </summary>
    internal static class TriangleTree
    {
        const int MaxLeaf = 4;
        /// <summary>A balanced tree over fewer than 2^31 prims is under 32 deep; traversal holds one entry per level plus one.</summary>
        public const int StackSize = 64;

        struct ByCentroid : System.Collections.Generic.IComparer<TreePrim>
        {
            public int Axis;

            public int Compare(TreePrim x, TreePrim y)
            {
                var cx = x.A[Axis] + x.B[Axis] + x.C[Axis];
                var cy = y.A[Axis] + y.B[Axis] + y.C[Axis];
                if (cx < cy) return -1;
                if (cx > cy) return 1;
                return x.Triangle.CompareTo(y.Triangle);
            }
        }

        /// <summary>Permutes <paramref name="prims"/> into leaf order and fills <paramref name="nodes"/>, root first.</summary>
        public static void Build(NativeArray<TreePrim> prims, NativeList<TreeNode> nodes)
        {
            nodes.Clear();
            var n = prims.Length;
            if (n == 0) return;
            nodes.Add(Bounds(prims, 0, n));
            var stack = new NativeList<int3>(StackSize, Allocator.Temp);
            stack.Add(new int3(0, 0, n));
            while (stack.Length > 0)
            {
                var e = stack[stack.Length - 1];
                stack.RemoveAt(stack.Length - 1);
                int index = e.x, start = e.y, count = e.z;
                var node = nodes[index];
                if (count <= MaxLeaf)
                {
                    node.Start = start;
                    node.Count = count;
                    nodes[index] = node;
                    continue;
                }
                var lo = new float3(float.MaxValue);
                var hi = new float3(float.MinValue);
                for (var i = start; i < start + count; i++)
                {
                    var c = prims[i].A + prims[i].B + prims[i].C;
                    lo = math.min(lo, c);
                    hi = math.max(hi, c);
                }
                var ext = hi - lo;
                var axis = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
                prims.GetSubArray(start, count).Sort(new ByCentroid { Axis = axis });
                var half = count / 2;
                var left = nodes.Length;
                nodes.Add(Bounds(prims, start, half));
                nodes.Add(Bounds(prims, start + half, count - half));
                node.Start = left;
                node.Count = 0;
                nodes[index] = node;
                stack.Add(new int3(left + 1, start + half, count - half));
                stack.Add(new int3(left, start, half));
            }
            stack.Dispose();
        }

        static TreeNode Bounds(NativeArray<TreePrim> prims, int start, int count)
        {
            var lo = new float3(float.MaxValue);
            var hi = new float3(float.MinValue);
            for (var i = start; i < start + count; i++)
            {
                var p = prims[i];
                lo = math.min(lo, math.min(p.A, math.min(p.B, p.C)));
                hi = math.max(hi, math.max(p.A, math.max(p.B, p.C)));
            }
            return new TreeNode { Min = lo, Max = hi };
        }

        /// <summary>The squared distance from <paramref name="p"/> to a node's box; 0 inside it.</summary>
        public static float BoxPointSq(float3 p, in TreeNode node) => math.lengthsq(math.max(math.max(node.Min - p, p - node.Max), 0f));

        /// <summary>
        /// The squared distance from <paramref name="p"/> to the nearest prim, and that prim's triangle, or infinity
        /// and -1 when none qualifies. With <paramref name="matchFacing"/>, a prim qualifies only when its face normal
        /// is within 90 degrees of <paramref name="facing"/>. Ties keep the prim met first, and traversal order is
        /// fixed, so the answer is a pure function of the tree and the query.
        /// </summary>
        public static unsafe float ClosestSq(NativeArray<TreeNode> nodes, NativeArray<TreePrim> prims, float3 p, float3 facing,
            bool matchFacing, out int triangle)
        {
            triangle = -1;
            var best = float.PositiveInfinity;
            if (nodes.Length == 0) return best;
            var stack = stackalloc int[StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var node = nodes[stack[--top]];
                if (BoxPointSq(p, node) >= best) continue;
                if (node.Count > 0)
                {
                    for (var i = node.Start; i < node.Start + node.Count; i++)
                    {
                        var prim = prims[i];
                        if (matchFacing && math.dot(prim.N, facing) <= 0f) continue;
                        var d = TriangleDistance.PointSq(p, prim.A, prim.B, prim.C);
                        if (d < best)
                        {
                            best = d;
                            triangle = prim.Triangle;
                        }
                    }
                    continue;
                }
                var l = node.Start;
                var dl = BoxPointSq(p, nodes[l]);
                var dr = BoxPointSq(p, nodes[l + 1]);
                // The nearer child is pushed last so it is searched first and tightens the bound for the other.
                if (dl <= dr)
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
            return best;
        }
    }
}
