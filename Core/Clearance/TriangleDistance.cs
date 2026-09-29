using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Clearance
{
    /// <summary>Exact distances between triangles and segments, for clearance between surfaces.</summary>
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
        /// plane touches it and reaches past by rounding only. The value and the rule are the gate's crossing measure's
        /// (<c>CrossingSettings.Default.Touch</c>), so the rule and the gate that judges it agree on what crosses.
        /// </summary>
        public const float Touch = 1e-5f;

        /// <summary>
        /// The length of the segment along which two triangles pass through each other; 0 when they only touch, or
        /// do not meet. See <see cref="Touch"/>.
        /// </summary>
        public static float IntersectionLength(float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2)
        {
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
            if (!Interval(a0, a1, a2, nB, b0, dir, out var aLo, out var aHi)) return 0f;
            if (!Interval(b0, b1, b2, nA, a0, dir, out var bLo, out var bHi)) return 0f;
            var overlap = math.min(aHi, bHi) - math.max(aLo, bLo);
            return overlap > Touch ? overlap : 0f;
        }

        /// <summary>Where triangle t0 t1 t2 cuts the plane through o with unit normal n, as an interval along dir; false unless it reaches past the plane by more than <see cref="Touch"/> on both sides.</summary>
        static bool Interval(float3 t0, float3 t1, float3 t2, float3 n, float3 o, float3 dir, out float lo, out float hi)
        {
            float d0 = math.dot(n, t0 - o), d1 = math.dot(n, t1 - o), d2 = math.dot(n, t2 - o);
            lo = float.PositiveInfinity;
            hi = float.NegativeInfinity;
            if (!(math.max(d0, math.max(d1, d2)) > Touch && math.min(d0, math.min(d1, d2)) < -Touch)) return false;
            Edge(t0, t1, d0, d1, dir, ref lo, ref hi);
            Edge(t1, t2, d1, d2, dir, ref lo, ref hi);
            Edge(t2, t0, d2, d0, dir, ref lo, ref hi);
            return lo <= hi;
        }

        static void Edge(float3 p, float3 q, float dp, float dq, float3 dir, ref float lo, ref float hi)
        {
            if (dp == 0f)
            {
                var s = math.dot(p, dir);
                lo = math.min(lo, s);
                hi = math.max(hi, s);
            }
            if (dp > 0f && dq < 0f || dp < 0f && dq > 0f)
            {
                var s = math.dot(p + dp / (dp - dq) * (q - p), dir);
                lo = math.min(lo, s);
                hi = math.max(hi, s);
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
            if (!(sum > 0f)) return a;
            var denom = 1f / sum;
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}
