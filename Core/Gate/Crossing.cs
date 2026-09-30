using System;
using Ryan6Vrc.MeshOptimizer.Core.Geometry;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Gate
{
    public struct CrossingSettings
    {
        /// <summary>
        /// Metres a triangle must reach past the other's plane, on both sides, for the pair to cross. A surface sewn to
        /// another, touching it along a line or lying on it, reaches past it by nothing but rounding, so it reads as
        /// touching; so do coplanar surfaces, which fight for the same pixels rather than cross. A crossing shorter than
        /// this is dropped too.
        /// </summary>
        public float Touch;

        /// <summary>
        /// Metres within which a crossing after the optimizer counts as one that was there before. The optimizer moves each
        /// surface by up to its error, and a crossing between two surfaces meeting at an angle theta moves along them by
        /// that error over sin(theta), so a crossing the avatar shipped with shifts a little and must not read as new.
        /// </summary>
        public float Nearness;

        /// <summary>
        /// 10 micrometres to cross, the clearance rule's own line (<see cref="TriangleDistance.Touch"/>): far above float
        /// rounding on an avatar-sized mesh, far below anything a pixel shows. 5 mm to be the same crossing: a 1 mm error
        /// moves a crossing between surfaces meeting at 12 degrees or more by less than that. At shallower meetings a moved
        /// crossing reads partly as new, which is a false alarm, never a false pass; the cost is that a new crossing within
        /// 5 mm of one the avatar shipped with reads as old.
        /// </summary>
        public static CrossingSettings Default => new CrossingSettings { Touch = TriangleDistance.Touch, Nearness = 0.005f };
    }

    /// <summary>One piece of the line where a triangle of A crosses a triangle of B, in the surfaces' space.</summary>
    public struct CrossingSegment
    {
        public float3 Start;
        public float3 End;
        /// <summary>
        /// Metres from <see cref="Start"/> to <see cref="End"/>, as the clearance rule reads the same crossing: taken along
        /// the planes' common line relative to a corner, so it keeps a precision the two ends lose far from the origin, and
        /// <c>distance(Start, End)</c> matches it only to that rounding.
        /// </summary>
        public float Length;
        public int TriangleA;
        public int TriangleB;
        /// <summary>
        /// How far triangle A stands past B's plane on its lesser side, the part of it that pokes through; likewise
        /// <see cref="ReachB"/>. Local to the two triangles: a deep pass-through reads by the triangles at its rim.
        /// </summary>
        public float ReachA;
        public float ReachB;
        /// <summary>
        /// Whether the two triangles face the same way as drawn. Under back-face culling only a same-facing crossing shows, as skin
        /// through tights does; one surface poking through an opposite-facing one, a skirt through its own lining, shows
        /// only where both sides are drawn.
        /// </summary>
        public bool SameFacing;
    }

    public struct CrossingStats
    {
        /// <summary>Metres of crossing line.</summary>
        public double Length;
        /// <summary>The part of <see cref="Length"/> between same-facing triangles.</summary>
        public double SameFacingLength;
        /// <summary>The deepest <see cref="CrossingSegment.ReachA"/>, how far A reaches through B; likewise B through A.</summary>
        public float ReachAThroughB;
        public float ReachBThroughA;
        public int Segments;
    }

    /// <summary>What an optimizer pass did to the crossing between one pair of surfaces in one configuration.</summary>
    public struct CrossingChange
    {
        public CrossingStats Before;
        public CrossingStats After;
        /// <summary>Metres of crossing after with no crossing before within <see cref="CrossingSettings.Nearness"/>.</summary>
        public double New;
        public double NewSameFacing;
        /// <summary>Connected runs of new crossing.</summary>
        public int Runs;
        /// <summary>The longest run's length, and where it is: the middle of its longest piece, and that piece's two triangles in the after surfaces.</summary>
        public double LongestRun;
        public float3 LongestRunAt;
        public int LongestRunTriangleA;
        public int LongestRunTriangleB;
    }

    /// <summary>
    /// Where two triangle surfaces cross: the line along which one's triangles pass through the other's. A pair of
    /// triangles crosses only when each reaches past the other's plane on both sides by more than
    /// <see cref="CrossingSettings.Touch"/>, and their two cuts along the planes' common line overlap. Deterministic:
    /// the segments come sorted by triangle pair, and every sum runs in that order.
    /// </summary>
    public static class Crossing
    {
        /// <summary>Fills <paramref name="segments"/> with where A's triangles cross B's. Excluded triangles take no part.</summary>
        /// <param name="drawnOpposite">
        /// True when exactly one of the two is drawn with its winding reversed, as Unity draws a mirrored renderer: that
        /// one's front is the side its winding faces away from, so <see cref="CrossingSegment.SameFacing"/> reads the
        /// other way from the winding.
        /// </param>
        public static CrossingStats Measure(in Surface a, in Surface b, in CrossingSettings settings, NativeList<CrossingSegment> segments, bool drawnOpposite = false) =>
            Run(a, b, false, drawnOpposite, settings, segments);

        /// <summary>
        /// Where a surface crosses itself: every pair of its triangles, each pair once, the lower triangle index as A.
        /// A skirt and its lining are often one mesh. Neighbours meet without crossing under the same rule as any pair.
        /// </summary>
        public static CrossingStats MeasureSelf(in Surface a, in CrossingSettings settings, NativeList<CrossingSegment> segments) =>
            Run(a, a, true, false, settings, segments);

        static CrossingStats Run(in Surface a, in Surface b, bool self, bool drawnOpposite, in CrossingSettings settings, NativeList<CrossingSegment> segments)
        {
            SurfaceDistance.Validate(a, "a");
            SurfaceDistance.Validate(b, "b");
            if (!(settings.Touch > 0f) || !(settings.Nearness > 0f))
                throw new ArgumentException($"Touch and Nearness must be positive distances in metres; they are {settings.Touch} and {settings.Nearness}", nameof(settings));
            segments.Clear();

            var excludedA = SurfaceDistance.Mask(a);
            var excludedB = SurfaceDistance.Mask(b);
            var prims = new NativeArray<TreePrim>(b.Triangles.Length, Allocator.Persistent);
            var nodes = new NativeList<TreeNode>(math.max(b.Triangles.Length / 2, 1), Allocator.Persistent);
            var found = new NativeQueue<CrossingSegment>(Allocator.Persistent);
            var count = new NativeReference<int>(Allocator.Persistent);
            try
            {
                new CrossingTreeJob
                {
                    Positions = b.Positions,
                    Triangles = b.Triangles,
                    Excluded = excludedB,
                    Prims = prims,
                    Count = count,
                    Nodes = nodes,
                }.Run();
                new CrossingJob
                {
                    PositionsA = a.Positions,
                    TrianglesA = a.Triangles,
                    ExcludedA = excludedA,
                    Nodes = nodes.AsArray(),
                    Prims = prims.GetSubArray(0, count.Value),
                    Self = self,
                    DrawnOpposite = drawnOpposite,
                    Touch = settings.Touch,
                    Found = found.AsParallelWriter(),
                }.Schedule(a.Triangles.Length, 64).Complete();
                using var stats = new NativeReference<CrossingStats>(Allocator.Persistent);
                new CrossingCollectJob { Found = found, Segments = segments, Stats = stats }.Run();
                return stats.Value;
            }
            finally
            {
                prims.Dispose();
                nodes.Dispose();
                found.Dispose();
                count.Dispose();
                excludedA.Dispose();
                excludedB.Dispose();
            }
        }

        /// <summary>
        /// Compares the crossing between one pair of surfaces before and after an optimizer pass, in one configuration and
        /// one space: how much of the after crossing has nothing before within <see cref="CrossingSettings.Nearness"/>, and
        /// where its longest connected run is. The new part of each after segment is found exactly, not sampled.
        /// </summary>
        public static CrossingChange Compare(NativeArray<CrossingSegment> before, NativeArray<CrossingSegment> after, in CrossingSettings settings)
        {
            if (!(settings.Nearness > 0f)) throw new ArgumentException($"Nearness must be a positive distance in metres; it is {settings.Nearness}", nameof(settings));
            using var change = new NativeReference<CrossingChange>(Allocator.Persistent);
            new CrossingChangeJob { Before = before, After = after, Nearness = settings.Nearness, Out = change }.Run();
            return change.Value;
        }
    }
}
