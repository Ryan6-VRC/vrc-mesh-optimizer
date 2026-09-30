using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Geometry
{
    /// <summary>Exact distances between triangles, segments and points, for clearance between surfaces and the gate's measures.</summary>
    internal static class TriangleDistance
    {
        /// <summary>
        /// The squared distance between two triangles; 0 when they touch or pass through each other. Two
        /// triangles that do not meet are closest at a corner of one against the other or at an edge against an
        /// edge, and two that pass through each other have an edge of one crossing the other, so these cases are
        /// the whole answer.
        /// </summary>
        public static float TriangleSq(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2)
        {
            if (Crosses(a0, a1, b0, b1, b2) || Crosses(a1, a2, b0, b1, b2) || Crosses(a2, a0, b0, b1, b2) ||
                Crosses(b0, b1, a0, a1, a2) || Crosses(b1, b2, a0, a1, a2) || Crosses(b2, b0, a0, a1, a2))
                return 0f;
            var best = PointSq(a0, b0, b1, b2);
            best = math.min(best, PointSq(a1, b0, b1, b2));
            best = math.min(best, PointSq(a2, b0, b1, b2));
            best = math.min(best, PointSq(b0, a0, a1, a2));
            best = math.min(best, PointSq(b1, a0, a1, a2));
            best = math.min(best, PointSq(b2, a0, a1, a2));
            best = math.min(best, EdgeSq(a0, a1, b0, b1, b2));
            best = math.min(best, EdgeSq(a1, a2, b0, b1, b2));
            best = math.min(best, EdgeSq(a2, a0, b0, b1, b2));
            return best;
        }

        /// <summary>
        /// Metres a triangle must reach past the other's plane, on both sides, for the two to cross, and the shortest
        /// crossing counted: a triangle sewn to another along an edge, resting an edge or a corner on it, or lying in its
        /// plane touches it and reaches past by rounding only. The gate's crossing measure reads its default from here
        /// (<c>CrossingSettings.Default.Touch</c>), so the rule and the gate that judges it agree on what crosses.
        /// </summary>
        public const float Touch = 1e-5f;

        /// <summary>
        /// Where triangles a and b pass through each other, if they do: each triangle's corners are measured against the
        /// other's plane, and the pair crosses only when each reaches past the other's plane on both sides by more than
        /// <paramref name="touch"/>. That leaves out a triangle lying on the other or meeting it along an edge or at a
        /// corner, whatever the rounding, and a coplanar pair. Each triangle then cuts the other's plane along a segment of
        /// the planes' common line, and the crossing is where the two overlap, when that is longer than
        /// <paramref name="touch"/>. Worked relative to a0, so positions along the line keep their precision far from the origin.
        /// </summary>
        public static bool Intersect(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2, float touch, out Intersection crossing)
        {
            crossing = default;
            var origin = a0;
            a1 -= origin;
            a2 -= origin;
            b0 -= origin;
            b1 -= origin;
            b2 -= origin;
            var na = math.cross(a1, a2);
            var nb = math.cross(b1 - b0, b2 - b0);
            var la = math.length(na);
            var lb = math.length(nb);
            if (!(la > 0f) || !(lb > 0f)) return false;
            na /= la;
            nb /= lb;

            var da = new float3(math.dot(nb, -b0), math.dot(nb, a1 - b0), math.dot(nb, a2 - b0));
            if (!(math.cmax(da) > touch && math.cmin(da) < -touch)) return false;
            var db = new float3(math.dot(na, b0), math.dot(na, b1), math.dot(na, b2));
            if (!(math.cmax(db) > touch && math.cmin(db) < -touch)) return false;

            var dir = math.cross(na, nb);
            var ld = math.length(dir);
            if (!(ld > 0f)) return false;
            dir /= ld;

            Cut(float3.zero, a1, a2, da, out var pa, out var qa);
            Cut(b0, b1, b2, db, out var pb, out var qb);
            Order(dir, ref pa, ref qa, out var ta0, out var ta1);
            Order(dir, ref pb, ref qb, out var tb0, out var tb1);
            var overlap = math.min(ta1, tb1) - math.max(ta0, tb0);
            if (!(overlap > touch)) return false;

            crossing = new Intersection
            {
                Start = origin + (ta0 >= tb0 ? pa : pb),
                End = origin + (ta1 <= tb1 ? qa : qb),
                Length = overlap,
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

        /// <summary>
        /// The length of the segment along which two triangles pass through each other; 0 when they only touch, or
        /// do not meet. See <see cref="Touch"/>.
        /// </summary>
        public static float IntersectionLength(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2) =>
            IntersectionSegment(a0, a1, a2, b0, b1, b2, out _, out _);

        /// <summary>As <see cref="IntersectionLength"/>, with the segment's ends; both are a0 when there is none.</summary>
        public static float IntersectionSegment(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2, out float3 start, out float3 end)
        {
            var origin = a0;
            start = end = origin;
            // Relative to one corner, so positions along the line keep their precision far from the origin.
            a1 -= a0;
            a2 -= a0;
            b0 -= a0;
            b1 -= a0;
            b2 -= a0;
            a0 = float3.zero;
            var nA = math.normalizesafe(math.cross(a1 - a0, a2 - a0));
            var nB = math.normalizesafe(math.cross(b1 - b0, b2 - b0));
            var dir = math.normalizesafe(math.cross(nA, nB));
            if (math.all(dir == 0f)) return 0f;
            if (!Interval(a0, a1, a2, nB, b0, dir, out var aLo, out var aHi, out var aLoAt, out var aHiAt)) return 0f;
            if (!Interval(b0, b1, b2, nA, a0, dir, out var bLo, out var bHi, out var bLoAt, out var bHiAt)) return 0f;
            var overlap = math.min(aHi, bHi) - math.max(aLo, bLo);
            if (!(overlap > Touch)) return 0f;
            start = origin + (aLo >= bLo ? aLoAt : bLoAt);
            end = origin + (aHi <= bHi ? aHiAt : bHiAt);
            return overlap;
        }

        /// <summary>Where triangle t0 t1 t2 cuts the plane through o with unit normal n, as an interval along dir; false unless it reaches past the plane by more than <see cref="Touch"/> on both sides.</summary>
        static bool Interval(float3 t0, float3 t1, float3 t2, float3 n, float3 o, float3 dir, out float lo, out float hi, out float3 loAt, out float3 hiAt)
        {
            float d0 = math.dot(n, t0 - o), d1 = math.dot(n, t1 - o), d2 = math.dot(n, t2 - o);
            lo = float.PositiveInfinity;
            hi = float.NegativeInfinity;
            loAt = hiAt = t0;
            if (!(math.max(d0, math.max(d1, d2)) > Touch && math.min(d0, math.min(d1, d2)) < -Touch)) return false;
            Edge(t0, t1, d0, d1, dir, ref lo, ref hi, ref loAt, ref hiAt);
            Edge(t1, t2, d1, d2, dir, ref lo, ref hi, ref loAt, ref hiAt);
            Edge(t2, t0, d2, d0, dir, ref lo, ref hi, ref loAt, ref hiAt);
            return lo <= hi;
        }

        static void Edge(float3 p, float3 q, float dp, float dq, float3 dir, ref float lo, ref float hi, ref float3 loAt, ref float3 hiAt)
        {
            if (dp == 0f) Extend(p, dir, ref lo, ref hi, ref loAt, ref hiAt);
            if (dp > 0f && dq < 0f || dp < 0f && dq > 0f) Extend(p + dp / (dp - dq) * (q - p), dir, ref lo, ref hi, ref loAt, ref hiAt);
        }

        static void Extend(float3 x, float3 dir, ref float lo, ref float hi, ref float3 loAt, ref float3 hiAt)
        {
            var s = math.dot(x, dir);
            if (s < lo)
            {
                lo = s;
                loAt = x;
            }
            if (s > hi)
            {
                hi = s;
                hiAt = x;
            }
        }

        static float EdgeSq(float3 p, float3 q, float3 b0, float3 b1, float3 b2) =>
            math.min(SegmentSq(p, q, b0, b1), math.min(SegmentSq(p, q, b1, b2), SegmentSq(p, q, b2, b0)));

        public static float PointSq(float3 p, float3 a, float3 b, float3 c) => math.lengthsq(p - ClosestPoint(p, a, b, c));

        /// <summary>
        /// True when segment pq passes through the interior of triangle abc, its ends strictly on opposite sides
        /// of the plane. An end lying in the plane, or a segment in the plane, is left to the corner and edge
        /// distances, which find 0 there.
        /// </summary>
        public static bool Crosses(float3 p, float3 q, float3 a, float3 b, float3 c)
        {
            var n = math.cross(b - a, c - a);
            var dp = math.dot(n, p - a);
            var dq = math.dot(n, q - a);
            if (dp > 0f == dq > 0f || dp == 0f || dq == 0f) return false;
            var x = p + dp / (dp - dq) * (q - p);
            return math.dot(math.cross(b - a, x - a), n) >= 0f &&
                   math.dot(math.cross(c - b, x - b), n) >= 0f &&
                   math.dot(math.cross(a - c, x - c), n) >= 0f;
        }

        /// <summary>The squared distance between segments p1q1 and p2q2 (Ericson, Real-Time Collision Detection, 5.1.9).</summary>
        public static float SegmentSq(float3 p1, float3 q1, float3 p2, float3 q2)
        {
            const float eps = 1e-20f;
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            var a = math.dot(d1, d1);
            var e = math.dot(d2, d2);
            var f = math.dot(d2, r);
            float s, t;
            if (a <= eps && e <= eps) return math.lengthsq(r);
            if (a <= eps)
            {
                s = 0f;
                t = math.saturate(f / e);
            }
            else
            {
                var c = math.dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = math.saturate(-c / a);
                }
                else
                {
                    var b = math.dot(d1, d2);
                    var denom = a * e - b * b;
                    s = denom > 0f ? math.saturate((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = math.saturate(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = math.saturate((b - c) / a);
                    }
                }
            }
            return math.lengthsq(p1 + d1 * s - (p2 + d2 * t));
        }

        static float3 NearestOnEdges(float3 p, float3 a, float3 b, float3 c)
        {
            var best = OnSegment(p, a, b);
            var q = OnSegment(p, b, c);
            if (math.lengthsq(q - p) < math.lengthsq(best - p)) best = q;
            q = OnSegment(p, c, a);
            return math.lengthsq(q - p) < math.lengthsq(best - p) ? q : best;
        }

        static float3 OnSegment(float3 p, float3 a, float3 b)
        {
            var ab = b - a;
            var l = math.lengthsq(ab);
            return l > 0f ? a + math.saturate(math.dot(p - a, ab) / l) * ab : a;
        }

        /// <summary>
        /// The closest point on triangle abc to p (Ericson, Real-Time Collision Detection, 5.1.5). A triangle with no
        /// area has no interior, and the region tests then end on a corner or an edge.
        /// </summary>
        public static float3 ClosestPoint(float3 p, float3 a, float3 b, float3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = math.dot(ab, ap);
            var d2 = math.dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            var bp = p - b;
            var d3 = math.dot(ab, bp);
            var d4 = math.dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + d1 / (d1 - d3) * ab;
            var cp = p - c;
            var d5 = math.dot(ab, cp);
            var d6 = math.dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + d2 / (d2 - d6) * ac;
            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
            var sum = va + vb + vc;
            // A sliver that rounds past every region test has no interior to speak of: its nearest point is on an edge.
            if (!(sum > 0f)) return NearestOnEdges(p, a, b, c);
            var denom = 1f / sum;
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }

    /// <summary>Where two triangles pass through each other, as <see cref="TriangleDistance.Intersect"/> finds it.</summary>
    internal struct Intersection
    {
        public float3 Start;
        public float3 End;
        /// <summary>Metres along the planes' common line from <see cref="Start"/> to <see cref="End"/>, read along the line itself rather than between the two ends, so it keeps the precision of the corner-relative arithmetic.</summary>
        public float Length;
        /// <summary>How far triangle a stands past b's plane on its lesser side; likewise <see cref="ReachB"/>.</summary>
        public float ReachA;
        public float ReachB;
        /// <summary>Whether the two triangles face the same way.</summary>
        public bool SameFacing;
    }
}
