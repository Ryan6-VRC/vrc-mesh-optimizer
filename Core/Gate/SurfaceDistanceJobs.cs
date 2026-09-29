using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Gate
{
    /// <summary>Normals, keys and the tree of one surface as a target.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct TargetJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<int3> Triangles;
        [ReadOnly] public NativeArray<bool> Excluded;
        public bool HasExcluded;
        public NativeArray<float3> Normals;
        public NativeArray<TriangleKey> Keys;
        public NativeParallelHashMap<TriangleKey, int> Lookup;
        public NativeArray<TreePrim> Prims;
        public NativeReference<int> PrimCount;

        public void Execute()
        {
            var count = 0;
            for (var t = 0; t < Triangles.Length; t++)
            {
                var tri = Triangles[t];
                float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
                Keys[t] = TriangleKey.Of(a, b, c);
                if (HasExcluded && Excluded[t]) continue;
                var n = math.normalizesafe(math.cross(b - a, c - a));
                Normals[t] = n;
                // A repeated triangle keeps its first index.
                Lookup.TryAdd(Keys[t], t);
                Prims[count++] = new TreePrim { A = a, B = b, C = c, N = n, Triangle = t };
            }
            PrimCount.Value = count;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct TreeJob : IJob
    {
        public NativeArray<TreePrim> Prims;
        public NativeList<TreeNode> Nodes;

        public void Execute() => TriangleTree.Build(Prims, Nodes);
    }

    /// <summary>Per sampled triangle: whether it takes part, its lattice, its area, and its twin on the other side.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct PlanJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<int3> Triangles;
        /// <summary>The sampled side's normals, zero where a triangle is excluded or degenerate.</summary>
        [ReadOnly] public NativeArray<float3> Normals;
        [ReadOnly] public NativeArray<TriangleKey> Keys;
        [ReadOnly] public NativeParallelHashMap<TriangleKey, int> OtherKeys;
        public float Spacing;
        public int MaxSubdivisions;
        public NativeArray<TrianglePlan> Plan;

        public void Execute(int t)
        {
            var plan = new TrianglePlan { Same = -1 };
            var tri = Triangles[t];
            float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
            plan.Area = 0.5f * math.length(math.cross(b - a, c - a));
            // An excluded triangle has a zero normal, as a degenerate one does, and neither has area to stand for.
            if (math.all(Normals[t] == 0f) || !(plan.Area > 0f))
            {
                plan.Area = 0f;
                Plan[t] = plan;
                return;
            }
            if (OtherKeys.TryGetValue(Keys[t], out var same))
            {
                plan.Same = same;
                plan.N = 1;
                plan.Count = 1;
                Plan[t] = plan;
                return;
            }
            var longest = math.sqrt(math.max(math.lengthsq(b - a), math.max(math.lengthsq(c - b), math.lengthsq(a - c))));
            var n = (int)math.clamp(math.ceil(longest / Spacing), 1f, MaxSubdivisions);
            plan.N = n;
            plan.Count = (n + 1) * (n + 2) / 2;
            plan.Step = longest / n;
            Plan[t] = plan;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct OffsetsJob : IJob
    {
        [ReadOnly] public NativeArray<TrianglePlan> Plan;
        public NativeArray<int> Offsets;
        public NativeReference<long> Total;

        public void Execute()
        {
            long sum = 0;
            for (var t = 0; t < Plan.Length; t++)
            {
                Offsets[t] = (int)math.min(sum, int.MaxValue);
                sum += Plan[t].Count;
            }
            Offsets[Plan.Length] = (int)math.min(sum, int.MaxValue);
            Total.Value = sum;
        }
    }

    /// <summary>
    /// Lays each triangle's lattice: the points a + j/n (b - a) + k/n (c - a) for j + k at most n, corners and edges
    /// included. Each point stands for a share of the area by how many of the lattice's n^2 small triangles touch it
    /// (a corner one, an edge point three, an inner point six, each a third of a small triangle), so the weighted mean is
    /// the integral of the distance's piecewise-linear interpolant over the triangle. A triangle the other side also has
    /// is one point, its centroid, for its whole area.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct SampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<int3> Triangles;
        [ReadOnly] public NativeArray<TrianglePlan> Plan;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<Sample> Samples;

        public void Execute(int t)
        {
            var plan = Plan[t];
            if (plan.Count == 0) return;
            var tri = Triangles[t];
            float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
            var o = Offsets[t];
            if (plan.Same >= 0)
            {
                Samples[o] = new Sample { Position = (a + b + c) / 3f, Weight = plan.Area, Triangle = t };
                return;
            }
            var n = plan.N;
            var unit = plan.Area / (n * n);
            var ab = b - a;
            var ac = c - a;
            for (var j = 0; j <= n; j++)
            for (var k = 0; k <= n - j; k++)
            {
                var i = n - j - k;
                var zeros = (i == 0 ? 1 : 0) + (j == 0 ? 1 : 0) + (k == 0 ? 1 : 0);
                var share = zeros == 2 ? 1f / 3f : zeros == 1 ? 1f : 2f;
                // Corners are the vertices themselves, exactly.
                var p = i == n ? a : j == n ? b : k == n ? c : a + ab * ((float)j / n) + ac * ((float)k / n);
                Samples[o++] = new Sample { Position = p, Weight = share * unit, Triangle = t };
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct QueryJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Sample> Samples;
        [ReadOnly] public NativeArray<TrianglePlan> Plan;
        [ReadOnly] public NativeArray<float3> Normals;
        [ReadOnly] public NativeArray<TreeNode> Nodes;
        [ReadOnly] public NativeArray<TreePrim> Prims;
        public bool MatchFacing;
        public NativeArray<float> Distance;
        public NativeArray<int> Matched;

        public void Execute(int i)
        {
            var s = Samples[i];
            var same = Plan[s.Triangle].Same;
            if (same >= 0)
            {
                Distance[i] = 0f;
                Matched[i] = same;
                return;
            }
            var d = TriangleTree.ClosestSq(Nodes, Prims, s.Position, Normals[s.Triangle], MatchFacing, out var tri);
            Distance[i] = math.sqrt(d);
            Matched[i] = tri;
        }
    }

    struct ByFirst : System.Collections.Generic.IComparer<float2>
    {
        public int Compare(float2 x, float2 y) => x.x.CompareTo(y.x);
    }

    /// <summary>Area-weighted statistics of one direction, its largest sample, and its samples sorted by distance.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct StatsJob : IJob
    {
        [ReadOnly] public NativeArray<Sample> Samples;
        [ReadOnly] public NativeArray<TrianglePlan> Plan;
        [ReadOnly] public NativeArray<float> Distance;
        [ReadOnly] public NativeArray<int> Matched;
        /// <summary>Out: (distance, weight) per sample, ascending by distance.</summary>
        public NativeArray<float2> Sorted;
        public NativeReference<DirectedDistance> Out;

        public void Execute()
        {
            var r = new DirectedDistance { MaxTriangle = -1, MaxMatchedTriangle = -1 };
            var best = -1;
            var unmatched = 0;
            for (var i = 0; i < Samples.Length; i++)
            {
                Sorted[i] = new float2(Distance[i], Samples[i].Weight);
                if (Matched[i] < 0) unmatched++;
                if (best < 0 || Distance[i] > Distance[best]) best = i;
            }
            for (var t = 0; t < Plan.Length; t++) r.Step = math.max(r.Step, Plan[t].Step);
            if (best >= 0)
            {
                r.MaxPosition = Samples[best].Position;
                r.MaxTriangle = Samples[best].Triangle;
                r.MaxMatchedTriangle = Matched[best];
            }
            Sorted.Sort(new ByFirst());
            r.Stats = Pool.Stats(Sorted, default);
            r.Stats.Samples = Samples.Length;
            r.Stats.Unmatched = unmatched;
            Out.Value = r;
        }
    }

    /// <summary>Both directions' samples pooled: merges the two sorted runs.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CombineJob : IJob
    {
        [ReadOnly] public NativeArray<float2> A;
        [ReadOnly] public NativeArray<float2> B;
        public NativeReference<DistanceStats> Out;

        public void Execute() => Out.Value = Pool.Stats(A, B);
    }

    internal static class Pool
    {
        /// <summary>
        /// Statistics over the union of two runs of (distance, weight), each ascending by distance. A percentile is
        /// the least distance at which the weight at or below it reaches that fraction of the whole. Sums are in
        /// double, in ascending order. One unmatched sample makes every statistic infinity.
        /// </summary>
        public static DistanceStats Stats(NativeArray<float2> a, NativeArray<float2> b)
        {
            var s = new DistanceStats();
            var na = a.IsCreated ? a.Length : 0;
            var nb = b.IsCreated ? b.Length : 0;
            double total = 0;
            for (var i = 0; i < na; i++) total += a[i].y;
            for (var i = 0; i < nb; i++) total += b[i].y;
            s.Area = total;
            if (na + nb == 0 || !(total > 0)) return s;

            double sum = 0, sumSq = 0, running = 0;
            var q = new float4(0.5f, 0.9f, 0.99f, 0.999f);
            var p = new float4(float.NaN);
            var next = 0;
            int ia = 0, ib = 0;
            var last = 0f;
            while (ia < na || ib < nb)
            {
                float2 e;
                if (ib >= nb || (ia < na && a[ia].x <= b[ib].x)) e = a[ia++];
                else e = b[ib++];
                double d = e.x;
                sum += e.y * d;
                sumSq += e.y * d * d;
                running += e.y;
                last = e.x;
                while (next < 4 && running >= q[next] * total) p[next++] = e.x;
            }
            // Rounding can leave the last fraction a hair short of its target; the largest distance answers it.
            while (next < 4) p[next++] = last;
            // An unmatched sample is ground the other surface does not cover, however little area it has, so no
            // statistic of a run holding one may read as a pass.
            if (float.IsPositiveInfinity(last))
            {
                p = new float4(float.PositiveInfinity);
                sum = sumSq = double.PositiveInfinity;
            }
            s.Mean = (float)(sum / total);
            s.Rms = (float)math.sqrt(sumSq / total);
            s.P50 = p.x;
            s.P90 = p.y;
            s.P99 = p.z;
            s.P999 = p.w;
            s.Max = last;
            return s;
        }
    }
}
