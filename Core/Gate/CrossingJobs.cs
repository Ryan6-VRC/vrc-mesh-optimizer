using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Gate
{
    internal static class TriangleCrossing
    {
        /// <summary>
        /// Where triangles a and b cross, if they do. Each triangle's corners are measured against the other's plane; a
        /// pair crosses only when each triangle reaches past the other's plane on both sides by more than
        /// <paramref name="touch"/>. That leaves out a triangle lying on the other or meeting it along an edge or at a
        /// corner, whatever the rounding, and a coplanar pair. Each triangle then cuts the other's plane along a
        /// segment of the planes' common line; the crossing is where the two segments overlap.
        /// </summary>
        public static bool Cross(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2, float touch, out CrossingSegment segment)
        {
            segment = default;
            var na = math.cross(a1 - a0, a2 - a0);
            var nb = math.cross(b1 - b0, b2 - b0);
            var la = math.length(na);
            var lb = math.length(nb);
            if (!(la > 0f) || !(lb > 0f)) return false;
            na /= la;
            nb /= lb;

            var da = new float3(math.dot(nb, a0 - b0), math.dot(nb, a1 - b0), math.dot(nb, a2 - b0));
            if (!(math.cmax(da) > touch && math.cmin(da) < -touch)) return false;
            var db = new float3(math.dot(na, b0 - a0), math.dot(na, b1 - a0), math.dot(na, b2 - a0));
            if (!(math.cmax(db) > touch && math.cmin(db) < -touch)) return false;

            var dir = math.cross(na, nb);
            var ld = math.length(dir);
            if (!(ld > 0f)) return false;
            dir /= ld;

            Cut(a0, a1, a2, da, out var pa, out var qa);
            Cut(b0, b1, b2, db, out var pb, out var qb);
            Order(dir, ref pa, ref qa, out var ta0, out var ta1);
            Order(dir, ref pb, ref qb, out var tb0, out var tb1);
            var start = ta0 >= tb0 ? pa : pb;
            var end = ta1 <= tb1 ? qa : qb;
            if (!(math.min(ta1, tb1) - math.max(ta0, tb0) > touch)) return false;

            segment = new CrossingSegment
            {
                Start = start,
                End = end,
                ReachA = math.min(math.cmax(da), -math.cmin(da)),
                ReachB = math.min(math.cmax(db), -math.cmin(db)),
                SameFacing = math.dot(na, nb) > 0f,
            };
            return true;
        }

        /// <summary>The two points where a triangle straddling a plane meets it, from the corner alone on its side.</summary>
        static void Cut(float3 v0, float3 v1, float3 v2, float3 d, out float3 p, out float3 q)
        {
            var above = d > 0f;
            var count = (above.x ? 1 : 0) + (above.y ? 1 : 0) + (above.z ? 1 : 0);
            // The lone corner is the one whose side the other two do not share.
            var lone = count == 1 ? above : !above;
            float3 k, u, w;
            float dk, du, dw;
            if (lone.x) { k = v0; dk = d.x; u = v1; du = d.y; w = v2; dw = d.z; }
            else if (lone.y) { k = v1; dk = d.y; u = v2; du = d.z; w = v0; dw = d.x; }
            else { k = v2; dk = d.z; u = v0; du = d.x; w = v1; dw = d.y; }
            p = k + (u - k) * (dk / (dk - du));
            q = k + (w - k) * (dk / (dk - dw));
        }

        static void Order(float3 dir, ref float3 p, ref float3 q, out float tp, out float tq)
        {
            tp = math.dot(dir, p);
            tq = math.dot(dir, q);
            if (tp <= tq) return;
            (p, q) = (q, p);
            (tp, tq) = (tq, tp);
        }

        public static float DistanceToSegment(float3 p, float3 a, float3 b)
        {
            var ab = b - a;
            var l = math.lengthsq(ab);
            var t = l > 0f ? math.saturate(math.dot(p - a, ab) / l) : 0f;
            return math.distance(p, a + t * ab);
        }

        /// <summary>Totals over segments already sorted by triangle pair, summed in that order.</summary>
        public static CrossingStats Stats(NativeArray<CrossingSegment> segments)
        {
            var s = new CrossingStats { Segments = segments.Length };
            for (var i = 0; i < segments.Length; i++)
            {
                var g = segments[i];
                double length = g.Length;
                s.Length += length;
                if (g.SameFacing) s.SameFacingLength += length;
                s.ReachAThroughB = math.max(s.ReachAThroughB, g.ReachA);
                s.ReachBThroughA = math.max(s.ReachBThroughA, g.ReachB);
            }
            return s;
        }
    }

    /// <summary>The target surface's taking-part, non-degenerate triangles, and a tree over them.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CrossingTreeJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<int3> Triangles;
        [ReadOnly] public NativeArray<bool> Excluded;
        public NativeArray<TreePrim> Prims;
        public NativeReference<int> Count;
        public NativeList<TreeNode> Nodes;

        public void Execute()
        {
            var n = 0;
            for (var t = 0; t < Triangles.Length; t++)
            {
                if (Excluded[t]) continue;
                var tri = Triangles[t];
                float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
                var normal = math.cross(b - a, c - a);
                if (!(math.lengthsq(normal) > 0f)) continue;
                Prims[n++] = new TreePrim { A = a, B = b, C = c, N = math.normalize(normal), Triangle = t };
            }
            Count.Value = n;
            TriangleTree.Build(Prims.GetSubArray(0, n), Nodes);
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct CrossingJob : IJobParallelFor
    {
        const int StackSize = 64;

        [ReadOnly] public NativeArray<float3> PositionsA;
        [ReadOnly] public NativeArray<int3> TrianglesA;
        [ReadOnly] public NativeArray<bool> ExcludedA;
        [ReadOnly] public NativeArray<TreeNode> Nodes;
        [ReadOnly] public NativeArray<TreePrim> Prims;
        /// <summary>
        /// A against itself, each pair once, lower index as A. Neighbours sharing a vertex are measured like any pair:
        /// two triangles fanned round a shared vertex cut the common line from that vertex in opposite directions, so the
        /// overlap is the vertex alone and reads as touching, while two that fold through each other overlap beyond it.
        /// </summary>
        public bool Self;
        public float Touch;
        public NativeQueue<CrossingSegment>.ParallelWriter Found;

        public unsafe void Execute(int i)
        {
            if (ExcludedA[i] || Nodes.Length == 0) return;
            var tri = TrianglesA[i];
            float3 a0 = PositionsA[tri.x], a1 = PositionsA[tri.y], a2 = PositionsA[tri.z];
            var lo = math.min(a0, math.min(a1, a2)) - Touch;
            var hi = math.max(a0, math.max(a1, a2)) + Touch;
            var stack = stackalloc int[StackSize];
            var top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var node = Nodes[stack[--top]];
                if (math.any(node.Min > hi) || math.any(node.Max < lo)) continue;
                if (node.Count == 0)
                {
                    stack[top++] = node.Start;
                    stack[top++] = node.Start + 1;
                    continue;
                }
                for (var p = node.Start; p < node.Start + node.Count; p++)
                {
                    var prim = Prims[p];
                    if (Self && prim.Triangle <= i) continue;
                    var pmin = math.min(prim.A, math.min(prim.B, prim.C));
                    var pmax = math.max(prim.A, math.max(prim.B, prim.C));
                    if (math.any(pmin > hi) || math.any(pmax < lo)) continue;
                    if (!TriangleCrossing.Cross(a0, a1, a2, prim.A, prim.B, prim.C, Touch, out var segment)) continue;
                    segment.TriangleA = i;
                    segment.TriangleB = prim.Triangle;
                    Found.Enqueue(segment);
                }
            }
        }
    }

    struct ByTrianglePair : System.Collections.Generic.IComparer<CrossingSegment>
    {
        public int Compare(CrossingSegment x, CrossingSegment y) =>
            x.TriangleA != y.TriangleA ? x.TriangleA.CompareTo(y.TriangleA) : x.TriangleB.CompareTo(y.TriangleB);
    }

    /// <summary>The found segments in triangle-pair order, whatever order the parallel search found them in, and their totals.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CrossingCollectJob : IJob
    {
        public NativeQueue<CrossingSegment> Found;
        public NativeList<CrossingSegment> Segments;
        public NativeReference<CrossingStats> Stats;

        public void Execute()
        {
            while (Found.TryDequeue(out var s)) Segments.Add(s);
            Segments.Sort(new ByTrianglePair());
            Stats.Value = TriangleCrossing.Stats(Segments.AsArray());
        }
    }

    /// <summary>A stretch of one after segment with no before crossing within Nearness, from parameter T0 to T1 along it.</summary>
    internal struct CrossingPiece
    {
        public float3 A;
        public float3 B;
        public int Segment;
        public float T0;
        public float T1;
    }

    /// <summary>
    /// Finds the new stretches of each after segment exactly: the points of a line within Nearness of a segment form one
    /// interval along it, because distance to a segment is convex along a line, so the new part of an after segment is
    /// what the union of those intervals leaves. Before segments are found through a grid of Nearness cells holding each
    /// in every cell within Nearness of it. New stretches join into runs where they reach a segment end that meets another's.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CrossingChangeJob : IJob
    {
        /// <summary>Metres within which two segment ends are one point of the crossing line: the ends two neighbouring triangle pairs compute for it differ by rounding only.</summary>
        const float JoinTolerance = 1e-4f;

        [ReadOnly] public NativeArray<CrossingSegment> Before;
        [ReadOnly] public NativeArray<CrossingSegment> After;
        public float Nearness;
        public NativeReference<CrossingChange> Out;

        public void Execute()
        {
            var r = new CrossingChange
            {
                Before = TriangleCrossing.Stats(Before),
                After = TriangleCrossing.Stats(After),
                LongestRunTriangleA = -1,
                LongestRunTriangleB = -1,
            };

            var grid = new NativeParallelMultiHashMap<int3, int>(math.max(Before.Length * 16, 16), Allocator.Temp);
            for (var i = 0; i < Before.Length; i++)
            {
                var s = Before[i];
                var lo = Cell(math.min(s.Start, s.End) - Nearness);
                var hi = Cell(math.max(s.Start, s.End) + Nearness);
                for (var x = lo.x; x <= hi.x; x++)
                for (var y = lo.y; y <= hi.y; y++)
                for (var z = lo.z; z <= hi.z; z++)
                    grid.Add(new int3(x, y, z), i);
            }

            var pieces = new NativeList<CrossingPiece>(math.max(After.Length, 1), Allocator.Temp);
            var candidates = new NativeParallelHashSet<int>(64, Allocator.Temp);
            var near = new NativeList<float2>(16, Allocator.Temp);
            for (var s = 0; s < After.Length; s++)
            {
                var g = After[s];
                // Every before segment within Nearness of any point of g sits in the cell of that point.
                candidates.Clear();
                var lo = Cell(math.min(g.Start, g.End));
                var hi = Cell(math.max(g.Start, g.End));
                for (var x = lo.x; x <= hi.x; x++)
                for (var y = lo.y; y <= hi.y; y++)
                for (var z = lo.z; z <= hi.z; z++)
                {
                    if (!grid.TryGetFirstValue(new int3(x, y, z), out var b, out var it)) continue;
                    do candidates.Add(b);
                    while (grid.TryGetNextValue(out b, ref it));
                }
                near.Clear();
                foreach (var b in candidates)
                    if (Within(g.Start, g.End, Before[b].Start, Before[b].End, Nearness, out var t0, out var t1)) near.Add(new float2(t0, t1));
                near.Sort(new ByStart());

                // What the union of the near intervals leaves of [0, 1].
                var from = 0f;
                for (var k = 0; k <= near.Length; k++)
                {
                    var to = k < near.Length ? near[k].x : 1f;
                    if (to > from) AddPiece(pieces, g, s, from, to, ref r);
                    if (k < near.Length) from = math.max(from, near[k].y);
                }
            }

            var parent = new NativeArray<int>(pieces.Length, Allocator.Temp);
            for (var i = 0; i < parent.Length; i++) parent[i] = i;
            var ends = new NativeList<float3>(Allocator.Temp);
            var endPiece = new NativeList<int>(Allocator.Temp);
            var endGrid = new NativeParallelMultiHashMap<int3, int>(math.max(pieces.Length * 2, 16), Allocator.Temp);
            for (var i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i];
                if (piece.T0 == 0f) AddEnd(piece.A, i, ends, endPiece, endGrid);
                if (piece.T1 == 1f) AddEnd(piece.B, i, ends, endPiece, endGrid);
            }
            for (var e = 0; e < ends.Length; e++)
            {
                var c = JoinCell(ends[e]);
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                {
                    if (!endGrid.TryGetFirstValue(c + new int3(x, y, z), out var o, out var it)) continue;
                    do
                    {
                        if (o > e && math.distance(ends[e], ends[o]) <= JoinTolerance) Union(parent, endPiece[e], endPiece[o]);
                    } while (endGrid.TryGetNextValue(out o, ref it));
                }
            }

            var runLength = new NativeArray<double>(pieces.Length, Allocator.Temp);
            for (var i = 0; i < pieces.Length; i++) runLength[Find(parent, i)] += math.distance(pieces[i].A, pieces[i].B);
            var longest = -1;
            for (var i = 0; i < pieces.Length; i++)
            {
                if (Find(parent, i) != i) continue;
                r.Runs++;
                if (longest < 0 || runLength[i] > runLength[longest]) longest = i;
            }
            if (longest >= 0)
            {
                r.LongestRun = runLength[longest];
                var best = -1;
                for (var i = 0; i < pieces.Length; i++)
                {
                    if (Find(parent, i) != longest) continue;
                    if (best < 0 || math.distance(pieces[i].A, pieces[i].B) > math.distance(pieces[best].A, pieces[best].B)) best = i;
                }
                var piece = pieces[best];
                r.LongestRunAt = (piece.A + piece.B) * 0.5f;
                r.LongestRunTriangleA = After[piece.Segment].TriangleA;
                r.LongestRunTriangleB = After[piece.Segment].TriangleB;
            }
            Out.Value = r;

            grid.Dispose();
            pieces.Dispose();
            candidates.Dispose();
            near.Dispose();
            parent.Dispose();
            ends.Dispose();
            endPiece.Dispose();
            endGrid.Dispose();
            runLength.Dispose();
        }

        static void AddPiece(NativeList<CrossingPiece> pieces, CrossingSegment g, int s, float t0, float t1, ref CrossingChange r)
        {
            var a = t0 == 0f ? g.Start : math.lerp(g.Start, g.End, t0);
            var b = t1 == 1f ? g.End : math.lerp(g.Start, g.End, t1);
            pieces.Add(new CrossingPiece { A = a, B = b, Segment = s, T0 = t0, T1 = t1 });
            double length = math.distance(a, b);
            r.New += length;
            if (g.SameFacing) r.NewSameFacing += length;
        }

        /// <summary>
        /// The parameters along p0-p1 within <paramref name="radius"/> of segment q0-q1, as [t0, t1], if any. The distance
        /// is convex in the parameter, so its nearest point splits the line into a falling side and a rising side, and each
        /// boundary is found by bisection on its side.
        /// </summary>
        static bool Within(float3 p0, float3 p1, float3 q0, float3 q1, float radius, out float t0, out float t1)
        {
            t0 = t1 = 0f;
            var tn = Nearest(p0, p1, q0, q1);
            if (Distance(p0, p1, q0, q1, tn) > radius) return false;
            t0 = Distance(p0, p1, q0, q1, 0f) <= radius ? 0f : Boundary(p0, p1, q0, q1, radius, 0f, tn);
            t1 = Distance(p0, p1, q0, q1, 1f) <= radius ? 1f : Boundary(p0, p1, q0, q1, radius, 1f, tn);
            return true;
        }

        static float Distance(float3 p0, float3 p1, float3 q0, float3 q1, float t) =>
            TriangleCrossing.DistanceToSegment(math.lerp(p0, p1, t), q0, q1);

        /// <summary>Between <paramref name="outside"/>, beyond the radius, and <paramref name="inside"/>, within it, the parameter where the distance is the radius.</summary>
        static float Boundary(float3 p0, float3 p1, float3 q0, float3 q1, float radius, float outside, float inside)
        {
            for (var k = 0; k < 40; k++)
            {
                var mid = 0.5f * (outside + inside);
                if (Distance(p0, p1, q0, q1, mid) <= radius) inside = mid;
                else outside = mid;
            }
            return inside;
        }

        /// <summary>The parameter along p0-p1 of its point nearest segment q0-q1 (Ericson, Real-Time Collision Detection, 5.1.9).</summary>
        static float Nearest(float3 p0, float3 p1, float3 q0, float3 q1)
        {
            var d1 = p1 - p0;
            var d2 = q1 - q0;
            var rr = p0 - q0;
            var a = math.dot(d1, d1);
            var e = math.dot(d2, d2);
            var f = math.dot(d2, rr);
            if (!(a > 0f)) return 0f;
            var c = math.dot(d1, rr);
            if (!(e > 0f)) return math.saturate(-c / a);
            var b = math.dot(d1, d2);
            var denom = a * e - b * b;
            var s = denom > 0f ? math.saturate((b * f - c * e) / denom) : 0f;
            var t = (b * s + f) / e;
            if (t < 0f) s = math.saturate(-c / a);
            else if (t > 1f) s = math.saturate((b - c) / a);
            return s;
        }

        int3 Cell(float3 p) => (int3)math.floor(p / Nearness);

        static int3 JoinCell(float3 p) => (int3)math.floor(p / JoinTolerance);

        static void AddEnd(float3 at, int piece, NativeList<float3> ends, NativeList<int> endPiece, NativeParallelMultiHashMap<int3, int> endGrid)
        {
            endGrid.Add(JoinCell(at), ends.Length);
            ends.Add(at);
            endPiece.Add(piece);
        }

        static int Find(NativeArray<int> parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        /// <summary>The lower index is the root, so the result does not depend on the order joins happen in.</summary>
        static void Union(NativeArray<int> parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a == b) return;
            if (a < b) parent[b] = a;
            else parent[a] = b;
        }
    }

    struct ByStart : System.Collections.Generic.IComparer<float2>
    {
        public int Compare(float2 x, float2 y) => x.x != y.x ? x.x.CompareTo(y.x) : x.y.CompareTo(y.y);
    }
}
