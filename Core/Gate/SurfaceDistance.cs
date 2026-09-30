using System;
using Ryan6Vrc.MeshOptimizer.Core.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Gate
{
    public struct SurfaceDistanceSettings
    {
        /// <summary>
        /// Metres between neighbouring samples along a triangle's longest edge. A triangle is sampled on a
        /// barycentric lattice of n steps a side, n = ceil(longest edge / Spacing), so sample density follows area.
        /// </summary>
        public float Spacing;

        /// <summary>
        /// The most steps a side one triangle's lattice may take, so one huge triangle cannot swamp the run. A
        /// triangle that hits it is sampled coarser than <see cref="Spacing"/>, and <see cref="DirectedDistance.Step"/>
        /// says by how much.
        /// </summary>
        public int MaxSubdivisions;

        /// <summary>
        /// A sample is matched only to triangles facing within 90 degrees of its own. Without it, a sample on the outer
        /// sheet of a thin double-sided shell finds the lining a millimetre away and reads a missing or displaced sheet
        /// as nearly nothing; with it, a fold or a flipped triangle, which finds no same-facing surface nearby, reads as
        /// far as the nearest surface that faces its way.
        /// </summary>
        public bool MatchFacing;

        /// <summary>
        /// 2 mm spacing: an error that falls between samples is under 1.2 mm larger than the one reported, about a
        /// pixel at <see cref="GateCamera.Default"/>. At most 64 steps a side, which is 2145 samples in one triangle.
        /// </summary>
        public static SurfaceDistanceSettings Default => new SurfaceDistanceSettings
        {
            Spacing = 0.002f,
            MaxSubdivisions = 64,
            MatchFacing = true,
        };
    }

    /// <summary>One side of a comparison: a triangle surface, in the same space and unit as the other side.</summary>
    public struct Surface
    {
        public NativeArray<float3> Positions;
        public NativeArray<int3> Triangles;
        /// <summary>
        /// Optional, one per triangle: true leaves the triangle out of this side entirely, neither sampled nor matched
        /// against. Left uncreated, every triangle takes part.
        /// </summary>
        public NativeArray<bool> Excluded;
    }

    public enum Statistic
    {
        Mean,
        Rms,
        P50,
        P90,
        P99,
        P999,
        Max,
    }

    /// <summary>
    /// Distances in metres, weighted by the surface area each sample stands for. Every distance is NaN when nothing was
    /// sampled (<see cref="Sampled"/>): every triangle on the sampled side had no area or was excluded, as on a mesh
    /// whose bones stand at zero scale.
    /// </summary>
    public struct DistanceStats
    {
        public float Mean;
        public float Rms;
        /// <summary>The distance under which half the sampled area lies.</summary>
        public float P50;
        public float P90;
        public float P99;
        public float P999;
        public float Max;
        /// <summary>Square metres sampled.</summary>
        public double Area;
        /// <summary>
        /// Points measured. A triangle whose three corners the other side also has, in the same order, is exactly on
        /// that surface, so it counts once, at distance zero, for its whole area.
        /// </summary>
        public int Samples;
        /// <summary>
        /// Samples that found no triangle to match: the other side is empty, or nothing on it faces their way. One is
        /// enough to make every statistic here infinity, whatever share of the area it stands for.
        /// </summary>
        public int Unmatched;

        /// <summary>Whether any area was sampled, so the distances above were measured at all.</summary>
        public bool Sampled => Area > 0;

        public float Get(Statistic statistic) => statistic switch
        {
            Statistic.Mean => Mean,
            Statistic.Rms => Rms,
            Statistic.P50 => P50,
            Statistic.P90 => P90,
            Statistic.P99 => P99,
            Statistic.P999 => P999,
            _ => Max,
        };
    }

    /// <summary>Distances from the samples of one side to the surface of the other, and where the largest one is.</summary>
    public struct DirectedDistance
    {
        public DistanceStats Stats;
        /// <summary>The sample that reads <see cref="DistanceStats.Max"/>, on the sampled side. The first in triangle order on a tie.</summary>
        public float3 MaxPosition;
        /// <summary>The sampled side's triangle that sample sits on, or -1 when nothing was sampled.</summary>
        public int MaxTriangle;
        /// <summary>The other side's triangle nearest that sample, or -1 when it matched none.</summary>
        public int MaxMatchedTriangle;
        /// <summary>
        /// The longest lattice step used, in metres: <see cref="SurfaceDistanceSettings.Spacing"/> or less, unless a
        /// triangle hit <see cref="SurfaceDistanceSettings.MaxSubdivisions"/>. Without the facing rule, distance to a
        /// surface changes no faster than position, so the largest distance anywhere on the sampled side exceeds
        /// <see cref="DistanceStats.Max"/> by at most Step / sqrt(3).
        /// </summary>
        public float Step;
    }

    public struct SurfaceDistanceResult
    {
        /// <summary>Sampled on A, measured to B: what B lost of A.</summary>
        public DirectedDistance AToB;
        /// <summary>Sampled on B, measured to A: what B added that A does not have, a spike or a fold.</summary>
        public DirectedDistance BToA;
        /// <summary>Both directions' samples pooled by area; its max is the symmetric Hausdorff distance.</summary>
        public DistanceStats Combined;
    }

    /// <summary>
    /// Two-sided distance between two triangle surfaces in one space, measured from points sampled over every triangle's
    /// interior, edges and corners to the nearest point on the other surface. Point to surface, not vertex to vertex: a
    /// decimated mesh keeps a subset of its source's vertices unmoved, so a vertex-to-vertex distance from it is zero by
    /// construction and its error lives between the vertices. Deterministic: the lattice is fixed and every sum runs in
    /// a fixed order, so the same input gives the same numbers bit for bit. The two-sided, area-sampled measure follows
    /// Metro (Cignoni, Rocchini and Scopigno 1998).
    /// </summary>
    public static class SurfaceDistance
    {
        /// <summary>A run that would sample more points than this is refused: at about 36 bytes a point it would hold over a gigabyte.</summary>
        public const long MaxSamples = 32L << 20;

        public static SurfaceDistanceResult Measure(in Surface a, in Surface b, in SurfaceDistanceSettings settings)
        {
            Validate(a, "a");
            Validate(b, "b");
            if (!(settings.Spacing > 0f) || float.IsInfinity(settings.Spacing))
                throw new ArgumentException($"Spacing must be a positive distance in metres; it is {settings.Spacing}", nameof(settings));
            if (settings.MaxSubdivisions < 1 || settings.MaxSubdivisions > 1024)
                throw new ArgumentException($"MaxSubdivisions must be 1 to 1024; it is {settings.MaxSubdivisions}", nameof(settings));

            using var targetA = Target.Build(a);
            using var targetB = Target.Build(b);
            var result = new SurfaceDistanceResult();
            var sortedA = default(NativeArray<float2>);
            var sortedB = default(NativeArray<float2>);
            try
            {
                result.AToB = Directed(a, targetA, targetB, settings, out sortedA);
                result.BToA = Directed(b, targetB, targetA, settings, out sortedB);
                using var combined = new NativeReference<DistanceStats>(Allocator.Persistent);
                new CombineJob { A = sortedA, B = sortedB, Out = combined }.Run();
                var c = combined.Value;
                c.Samples = result.AToB.Stats.Samples + result.BToA.Stats.Samples;
                c.Unmatched = result.AToB.Stats.Unmatched + result.BToA.Stats.Unmatched;
                result.Combined = c;
                return result;
            }
            finally
            {
                if (sortedA.IsCreated) sortedA.Dispose();
                if (sortedB.IsCreated) sortedB.Dispose();
            }
        }

        /// <summary>Refuses, by name, a surface a gate measure cannot read.</summary>
        internal static void Validate(in Surface s, string side)
        {
            var problem = Unreadable(s, side);
            if (problem != null) throw new ArgumentException(problem, side);
        }

        /// <summary>Why a gate measure cannot read a surface, naming the first thing wrong with it; null when it can.</summary>
        internal static string Unreadable(in Surface s, string side)
        {
            if (!s.Positions.IsCreated || !s.Triangles.IsCreated)
                return $"surface {side} needs Positions and Triangles";
            if (s.Excluded.IsCreated && s.Excluded.Length != s.Triangles.Length)
                return $"surface {side} has {s.Excluded.Length} Excluded flags for {s.Triangles.Length} triangles";
            var n = s.Positions.Length;
            for (var t = 0; t < s.Triangles.Length; t++)
            {
                var tri = s.Triangles[t];
                if (math.any(tri < 0) || math.any(tri >= n))
                    return $"surface {side} triangle {t} names vertex {tri}, outside 0..{n - 1}";
            }
            for (var v = 0; v < n; v++)
                if (!math.all(math.isfinite(s.Positions[v])))
                    return $"surface {side} vertex {v} is at {s.Positions[v]}, which is not a finite position";
            return null;
        }

        /// <summary>
        /// A surface's exclusion flags as a job can hold them, one per triangle: a copy of its own, or none set when it has
        /// none, since a job refuses a container that was never created. The caller owns it.
        /// </summary>
        internal static NativeArray<bool> Mask(in Surface s)
        {
            var mask = new NativeArray<bool>(s.Triangles.Length, Allocator.Persistent);
            if (s.Excluded.IsCreated) mask.CopyFrom(s.Excluded);
            return mask;
        }

        static DirectedDistance Directed(in Surface sampled, in Target from, in Target to, in SurfaceDistanceSettings settings,
            out NativeArray<float2> sorted)
        {
            var triangles = sampled.Triangles.Length;
            using var plan = new NativeArray<TrianglePlan>(triangles, Allocator.Persistent);
            new PlanJob
            {
                Positions = sampled.Positions,
                Triangles = sampled.Triangles,
                Normals = from.Normals,
                Keys = from.Keys,
                OtherKeys = to.Lookup,
                Spacing = settings.Spacing,
                MaxSubdivisions = settings.MaxSubdivisions,
                Plan = plan,
            }.Schedule(triangles, 64).Complete();

            using var offsets = new NativeArray<int>(triangles + 1, Allocator.Persistent);
            using var total = new NativeReference<long>(Allocator.Persistent);
            new OffsetsJob { Plan = plan, Offsets = offsets, Total = total }.Run();
            if (total.Value > MaxSamples)
                throw new ArgumentException(
                    $"sampling at {settings.Spacing * 1000f} mm would take {total.Value} points, over the {MaxSamples} this holds; raise Spacing",
                    nameof(settings));

            var count = (int)total.Value;
            using var samples = new NativeArray<Sample>(count, Allocator.Persistent);
            new SampleJob
            {
                Positions = sampled.Positions,
                Triangles = sampled.Triangles,
                Plan = plan,
                Offsets = offsets,
                Samples = samples,
            }.Schedule(triangles, 64).Complete();

            using var distance = new NativeArray<float>(count, Allocator.Persistent);
            using var matched = new NativeArray<int>(count, Allocator.Persistent);
            new QueryJob
            {
                Samples = samples,
                Plan = plan,
                Normals = from.Normals,
                Nodes = to.Nodes.AsArray(),
                Prims = to.Prims,
                MatchFacing = settings.MatchFacing,
                Distance = distance,
                Matched = matched,
            }.Schedule(count, 256).Complete();

            sorted = new NativeArray<float2>(count, Allocator.Persistent);
            using var result = new NativeReference<DirectedDistance>(Allocator.Persistent);
            new StatsJob
            {
                Samples = samples,
                Plan = plan,
                Distance = distance,
                Matched = matched,
                Sorted = sorted,
                Out = result,
            }.Run();
            return result.Value;
        }
    }

    /// <summary>One surface prepared as the thing samples are measured against.</summary>
    internal struct Target : IDisposable
    {
        /// <summary>Unit face normal per triangle; zero for a degenerate or excluded one.</summary>
        public NativeArray<float3> Normals;
        /// <summary>Per triangle, its corners in a canonical rotation, for finding the same triangle on the other side.</summary>
        public NativeArray<TriangleKey> Keys;
        /// <summary>Taking part triangles by key.</summary>
        public NativeParallelHashMap<TriangleKey, int> Lookup;
        public NativeList<TreeNode> Nodes;
        public NativeArray<TreePrim> Prims;

        public static Target Build(in Surface s)
        {
            var n = s.Triangles.Length;
            var t = new Target
            {
                Normals = new NativeArray<float3>(n, Allocator.Persistent),
                Keys = new NativeArray<TriangleKey>(n, Allocator.Persistent),
                Lookup = new NativeParallelHashMap<TriangleKey, int>(math.max(n, 1), Allocator.Persistent),
                Nodes = new NativeList<TreeNode>(math.max(n / 2, 1), Allocator.Persistent),
            };
            try
            {
                using var excluded = SurfaceDistance.Mask(s);
                using var count = new NativeReference<int>(Allocator.Persistent);
                using var scratch = new NativeArray<TreePrim>(n, Allocator.Persistent);
                new TargetJob
                {
                    Positions = s.Positions,
                    Triangles = s.Triangles,
                    Excluded = excluded,
                    Normals = t.Normals,
                    Keys = t.Keys,
                    Lookup = t.Lookup,
                    Prims = scratch,
                    PrimCount = count,
                    Nodes = t.Nodes,
                }.Run();
                // The tree was built over the first PrimCount prims, in place, so they are already in leaf order.
                t.Prims = new NativeArray<TreePrim>(count.Value, Allocator.Persistent);
                NativeArray<TreePrim>.Copy(scratch, t.Prims, count.Value);
                return t;
            }
            catch
            {
                t.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Normals.IsCreated) Normals.Dispose();
            if (Keys.IsCreated) Keys.Dispose();
            if (Lookup.IsCreated) Lookup.Dispose();
            if (Nodes.IsCreated) Nodes.Dispose();
            if (Prims.IsCreated) Prims.Dispose();
        }
    }

    /// <summary>A triangle's three corners, bit for bit, starting from the least and keeping the winding.</summary>
    internal struct TriangleKey : IEquatable<TriangleKey>
    {
        public uint3 P0;
        public uint3 P1;
        public uint3 P2;

        public static TriangleKey Of(float3 a, float3 b, float3 c)
        {
            var ua = math.asuint(a);
            var ub = math.asuint(b);
            var uc = math.asuint(c);
            if (Less(ub, ua) && !Less(uc, ub)) return new TriangleKey { P0 = ub, P1 = uc, P2 = ua };
            if (Less(uc, ua) && Less(uc, ub)) return new TriangleKey { P0 = uc, P1 = ua, P2 = ub };
            return new TriangleKey { P0 = ua, P1 = ub, P2 = uc };
        }

        static bool Less(uint3 x, uint3 y) => x.x != y.x ? x.x < y.x : x.y != y.y ? x.y < y.y : x.z < y.z;

        public bool Equals(TriangleKey other) => math.all(P0 == other.P0) && math.all(P1 == other.P1) && math.all(P2 == other.P2);

        public override int GetHashCode() => (int)math.hash(new uint3(math.hash(P0), math.hash(P1), math.hash(P2)));
    }

    internal struct TrianglePlan
    {
        /// <summary>Lattice steps a side; 0 when the triangle is not sampled.</summary>
        public int N;
        public int Count;
        public float Area;
        /// <summary>The other side's triangle with the same corners in the same order, or -1.</summary>
        public int Same;
        public float Step;
    }

    internal struct Sample
    {
        public float3 Position;
        public float Weight;
        public int Triangle;
    }
}
