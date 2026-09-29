using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Collections;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>
    /// How a collapse's error in each live frame's configuration combines with its error at rest. Frame
    /// excess is max(0, frame error squared - rest error squared): what a frame adds beyond the rest error, so
    /// a frame that only translates a region adds nothing.
    /// </summary>
    public enum ShapeAggregation
    {
        /// <summary>The largest error over the rest state and every frame.</summary>
        WorstCase = 0,
        /// <summary>Rest error squared plus every frame's excess: favours density where many frames act.</summary>
        Sum = 1,
        /// <summary>(1 - AggregationBlend) * WorstCase + AggregationBlend * Sum, on squared errors.</summary>
        Blend = 2,
    }

    public struct DecimationSettings
    {
        public TopologySettings Topology;

        public ShapeAggregation Aggregation;
        /// <summary>Blend only: 0 is worst case, 1 is sum.</summary>
        public float AggregationBlend;

        /// <summary>Scale on the UV0 term. At 1, a texel displaced d metres across the surface costs as much as
        /// the surface itself moving d metres.</summary>
        public float UvWeight;

        /// <summary>
        /// The skin-weight-gradient term: collapsing u onto v costs SkinWeightPenalty * d * |u - v| metres, where
        /// d is half the L1 distance between their influence distributions. Linear blend skinning bends a weight
        /// ramp of d across an edge of length L by about angle * d * L under a relative bone rotation, so this
        /// is a representative bend in radians. 0 disables the term.
        /// </summary>
        public float SkinWeightPenalty;

        /// <summary>Weight of an open border edge's perpendicular plane, relative to its squared length.</summary>
        public float BorderWeight;
        /// <summary>Weight of a seam edge's perpendicular plane, relative to its squared length.</summary>
        public float SeamWeight;

        /// <summary>A collapse is rejected when any moved triangle's normal turns by an angle whose cosine is at or
        /// below this, in any configuration.</summary>
        public float FlipCosine;
        /// <summary>A collapse is rejected when any moved triangle's area falls to this fraction of its area
        /// before the collapse, in any configuration.</summary>
        public float DegenerateRatio;

        /// <summary>Stop once the live triangle count reaches this. 0 records the whole sequence.</summary>
        public int TargetTriangleCount;
        /// <summary>Stop before a collapse whose error, in metres, would exceed this.</summary>
        public float MaxError;

        public static DecimationSettings Default => new DecimationSettings
        {
            Topology = TopologySettings.Default,
            Aggregation = ShapeAggregation.WorstCase,
            AggregationBlend = 0.5f,
            UvWeight = 1f,
            SkinWeightPenalty = 1f,
            BorderWeight = 10f,
            SeamWeight = 1f,
            FlipCosine = 0.25f,
            DegenerateRatio = 1e-3f,
            TargetTriangleCount = 0,
            MaxError = float.PositiveInfinity,
        };
    }

    /// <summary>
    /// Per-source-vertex protection, supplied by whoever knows what a vertex is (a hand, a face). Either array
    /// may be left unassigned; when assigned, its length is the source vertex count.
    /// </summary>
    public struct VertexProtection
    {
        /// <summary>Nonzero: the vertex is never removed. It may still be a collapse target.</summary>
        public NativeArray<byte> Locked;
        /// <summary>Multiplies the error of removing the vertex. 1 is neutral; larger keeps it longer.</summary>
        public NativeArray<float> CostMultiplier;
    }
}
