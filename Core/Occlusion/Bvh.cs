using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Occlusion
{
    /// <summary>A leaf when Count > 0, over prims [First, First + Count); otherwise its children sit at First and First + 1.</summary>
    internal struct BvhNode
    {
        public float3 Min;
        public int First;
        public float3 Max;
        public int Count;
    }

    /// <summary>
    /// One occluder triangle as traversal reads it: its corners x, y and z exactly as the configuration the
    /// tree was built for places them, so faces that share an edge share its floats, plus the occluder id
    /// the rest of its data hangs off.
    /// </summary>
    internal struct BvhTri
    {
        public float3 V0;
        public float3 V1;
        public float3 V2;
        public int Occ;
    }

    /// <summary>How <see cref="Bvh.Build"/> splits a node. The default settings use <see cref="Sah"/>; <see cref="Midpoint"/> exists for benchmarks to compare against.</summary>
    internal enum BvhSplit : byte
    {
        /// <summary>Binned surface-area heuristic.</summary>
        Sah,
        /// <summary>Midpoint of the centroid bounds' longest axis: cheaper to build, slower to query.</summary>
        Midpoint,
    }

    internal static class Bvh
    {
        const int MaxLeaf = 4;
        const int Bins = 12;
        // Traversal keeps a fixed stack; depth + 1 entries bound it.
        internal const int MaxDepth = 48;

        /// <summary>
        /// Builds a tree over the prims listed in <paramref name="order"/>, whose bounds are
        /// <paramref name="bmin"/>/<paramref name="bmax"/> indexed by prim id, appending nodes to
        /// <paramref name="nodes"/>. On return <paramref name="order"/> is permuted into leaf order, and a
        /// leaf's First is a position in it plus <paramref name="primBase"/>. Returns the root, or -1 when
        /// there is nothing to build.
        /// </summary>
        public static int Build(NativeArray<float3> bmin, NativeArray<float3> bmax, NativeArray<int> order,
                                NativeList<BvhNode> nodes, int primBase, BvhSplit split)
        {
            var n = order.Length;
            if (n == 0) return -1;
            var root = nodes.Length;
            nodes.Add(Bounds(bmin, bmax, order, 0, n));
            var stack = new NativeList<int4>(64, Allocator.Temp);
            stack.Add(new int4(root, 0, n, 0));
            while (stack.Length > 0)
            {
                var e = stack[stack.Length - 1];
                stack.RemoveAt(stack.Length - 1);
                int start = e.y, count = e.z, depth = e.w;
                var node = nodes[e.x];
                if (count <= MaxLeaf || depth >= MaxDepth)
                {
                    node.First = primBase + start;
                    node.Count = count;
                    nodes[e.x] = node;
                    continue;
                }
                var mid = Partition(bmin, bmax, order, start, count, split);
                var left = nodes.Length;
                nodes.Add(Bounds(bmin, bmax, order, start, mid - start));
                nodes.Add(Bounds(bmin, bmax, order, mid, start + count - mid));
                node.First = left;
                node.Count = 0;
                nodes[e.x] = node;
                stack.Add(new int4(left + 1, mid, start + count - mid, depth + 1));
                stack.Add(new int4(left, start, mid - start, depth + 1));
            }
            stack.Dispose();
            return root;
        }

        static BvhNode Bounds(NativeArray<float3> bmin, NativeArray<float3> bmax, NativeArray<int> order, int start, int count)
        {
            var lo = new float3(float.MaxValue);
            var hi = new float3(float.MinValue);
            for (var i = start; i < start + count; i++)
            {
                lo = math.min(lo, bmin[order[i]]);
                hi = math.max(hi, bmax[order[i]]);
            }
            return new BvhNode { Min = lo, Max = hi, First = 0, Count = 0 };
        }

        static float Area(float3 lo, float3 hi)
        {
            var d = math.max(hi - lo, 0f);
            return d.x * d.y + d.y * d.z + d.z * d.x;
        }

        static float3 Centroid(NativeArray<float3> bmin, NativeArray<float3> bmax, int prim) => (bmin[prim] + bmax[prim]) * 0.5f;

        /// <summary>Splits [start, start + count) in place and returns the first index of the right half, never an empty side.</summary>
        static unsafe int Partition(NativeArray<float3> bmin, NativeArray<float3> bmax, NativeArray<int> order, int start, int count, BvhSplit split)
        {
            var cmin = new float3(float.MaxValue);
            var cmax = new float3(float.MinValue);
            for (var i = start; i < start + count; i++)
            {
                var c = Centroid(bmin, bmax, order[i]);
                cmin = math.min(cmin, c);
                cmax = math.max(cmax, c);
            }
            var ext = cmax - cmin;
            var half = start + count / 2;
            if (math.cmax(ext) <= 1e-9f) return half;

            if (split == BvhSplit.Midpoint)
            {
                var axis = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
                var pivot = (cmin[axis] + cmax[axis]) * 0.5f;
                var m = PartitionBy(bmin, bmax, order, start, count, axis, pivot);
                return m == start || m == start + count ? half : m;
            }

            var binCount = stackalloc int[Bins];
            var binLo = stackalloc float3[Bins];
            var binHi = stackalloc float3[Bins];
            var rightArea = stackalloc float[Bins];
            var bestCost = float.MaxValue;
            var bestAxis = -1;
            var bestBin = -1;
            for (var a = 0; a < 3; a++)
            {
                if (ext[a] <= 1e-9f) continue;
                for (var b = 0; b < Bins; b++)
                {
                    binCount[b] = 0;
                    binLo[b] = new float3(float.MaxValue);
                    binHi[b] = new float3(float.MinValue);
                }
                var scale = Bins / ext[a];
                for (var i = start; i < start + count; i++)
                {
                    var p = order[i];
                    var b = math.clamp((int)((Centroid(bmin, bmax, p)[a] - cmin[a]) * scale), 0, Bins - 1);
                    binCount[b]++;
                    binLo[b] = math.min(binLo[b], bmin[p]);
                    binHi[b] = math.max(binHi[b], bmax[p]);
                }
                var lo = new float3(float.MaxValue);
                var hi = new float3(float.MinValue);
                for (var b = Bins - 1; b > 0; b--)
                {
                    lo = math.min(lo, binLo[b]);
                    hi = math.max(hi, binHi[b]);
                    rightArea[b] = Area(lo, hi);
                }
                lo = new float3(float.MaxValue);
                hi = new float3(float.MinValue);
                var leftCount = 0;
                for (var b = 0; b < Bins - 1; b++)
                {
                    lo = math.min(lo, binLo[b]);
                    hi = math.max(hi, binHi[b]);
                    leftCount += binCount[b];
                    var rightCount = count - leftCount;
                    if (leftCount == 0 || rightCount == 0) continue;
                    var cost = Area(lo, hi) * leftCount + rightArea[b + 1] * rightCount;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestAxis = a;
                        bestBin = b;
                    }
                }
            }
            if (bestAxis < 0) return half;
            var mid = PartitionByBin(bmin, bmax, order, start, count, bestAxis, cmin[bestAxis], Bins / ext[bestAxis], bestBin);
            return mid == start || mid == start + count ? half : mid;
        }

        static int PartitionBy(NativeArray<float3> bmin, NativeArray<float3> bmax, NativeArray<int> order, int start, int count, int axis, float pivot)
        {
            int i = start, j = start + count - 1;
            while (i <= j)
            {
                if (Centroid(bmin, bmax, order[i])[axis] < pivot) i++;
                else
                {
                    (order[i], order[j]) = (order[j], order[i]);
                    j--;
                }
            }
            return i;
        }

        // Partitions by the same bin arithmetic the cost sweep used, so float rounding cannot move a prim across the chosen boundary.
        static int PartitionByBin(NativeArray<float3> bmin, NativeArray<float3> bmax, NativeArray<int> order, int start, int count, int axis, float lo, float scale, int lastLeftBin)
        {
            int i = start, j = start + count - 1;
            while (i <= j)
            {
                var b = math.clamp((int)((Centroid(bmin, bmax, order[i])[axis] - lo) * scale), 0, Bins - 1);
                if (b <= lastLeftBin) i++;
                else
                {
                    (order[i], order[j]) = (order[j], order[i]);
                    j--;
                }
            }
            return i;
        }
    }
}
