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

        /// <summary>The normal term: what a collapse does to the shading the mesh's own vertex normals give.</summary>
        public NormalSettings Normal;

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

        /// <summary>Keeps each layer clear of the others; acts only when the decimator is given the other layers.</summary>
        public ClearanceSettings Clearance;

        /// <summary>Measures each collapse's error in synthetic bends of the rig too; acts only when the decimator is given the rig.</summary>
        public BendSettings Bend;

        public static DecimationSettings Default => new DecimationSettings
        {
            Clearance = ClearanceSettings.Default,
            Bend = BendSettings.Default,
            Topology = TopologySettings.Default,
            Aggregation = ShapeAggregation.WorstCase,
            AggregationBlend = 0.5f,
            UvWeight = 1f,
            Normal = NormalSettings.Default,
            SkinWeightPenalty = 1f,
            BorderWeight = 10f,
            SeamWeight = 1f,
            FlipCosine = 0.25f,
            DegenerateRatio = 1e-3f,
            TargetTriangleCount = 0,
        };
    }

    /// <summary>
    /// The normal term. A collapse moves no vertex and keeps every surviving vertex's normal as authored, but the larger
    /// triangles it leaves interpolate between vertices whose normals differ, so the surface can shade differently where it
    /// has not moved. Each vertex holds the linear normal field of every source triangle it stands for, three attribute
    /// quadrics (Hoppe 1999) over the normals the mesh carries, and a collapse is priced at how far the target's normal
    /// lies from those fields where the target stands, over the area they cover: <see cref="Weight"/> × sqrt(∫|Δn|² dA)
    /// metres, where |Δn| is the chord between unit normals, near the angle in radians up to a right angle. A bump of
    /// height h across a span L tilts the normal by about 2h/L, so a tilt θ over a span L reads like a displacement near
    /// θL/2, and a large region is priced before a small one showing the same tilt. A length that grows with the surface,
    /// it joins the position error as the UV term does, inside a vertex's cost multiplier
    /// (<see cref="VertexProtection.CostMultiplier"/>), so the error level stays the one figure the tolerance is read against.
    ///
    /// Limits: normals are read at rest, and a live shape frame or a bend is priced with the rest normal field, so a shape
    /// key that changes only the shading is not seen. A field is extended beyond its triangle across the triangle's plane,
    /// so a sliver whose normals differ across its short side prices distant collapses high: that keeps triangles and
    /// never loses shading. A smooth surface whose normals are not linear in position (anything but a sphere or a
    /// cylinder) pays for the triangles its shading needs; decimation without the term mis-shades such a surface too, and
    /// a strongly rippled one can cost twice as many triangles at a given tolerance.
    /// </summary>
    public struct NormalSettings
    {
        /// <summary>
        /// A plain factor; 0 turns the term off, and the sequence is then exactly the one without it. The default, 0.5, is
        /// the θL/2 reading's.
        /// </summary>
        public float Weight;

        public static NormalSettings Default => new NormalSettings { Weight = 0.5f };
    }

    /// <summary>
    /// The clearance rule. Layers of clothing sit a millimetre or less apart, and a collapse that moves one layer's
    /// surface by less than the error level can still carry it through the next. So a collapse is refused when a
    /// triangle it makes comes nearer a source triangle of another layer than (1 - Share) of the gap there: the larger
    /// of that triangle's own clearance and the clearance around the new triangle's corners. The gap is read on source
    /// triangles, which stay as small as the source's own however far decimation goes, so it is the gap where the two
    /// layers approach rather than the least gap anywhere under a grown triangle. The other layers are judged as they
    /// were before decimation, which is what lets each mesh be decimated alone and in any order: with Share under one
    /// half, two layers that each keep to their share of the gap between them cannot meet. Surface that already touches
    /// or crosses another layer has no clearance to keep; there a collapse is refused only when it lengthens the line
    /// along which the layers cross.
    ///
    /// Limits: clearance is measured at rest; in a bend of the rig a collapse is held only to a share of that gap (see
    /// <see cref="BendSettings.ClearanceShare"/>), and a shape key that moves two layers differently is not checked;
    /// a layer is a connected piece of a mesh, so two layers joined into one piece (a lining sewn to its skirt's hem, or
    /// pieces meeting at one vertex) are one layer; the gap is read per source triangle, so where it changes by a
    /// large part of itself across one source triangle, the least of it stands for the whole; and the other layers are
    /// the triangles the caller hands in, so one left out is held clear of by nothing, and if it is drawn after all it was
    /// never a neighbour and a layer may cross it. The pipeline hands in the triangles occlusion removed, which re-judging
    /// can write back, as neighbours of every other layer; its RemovedAsNeighbours switch off leaves them out, and a
    /// layer may then cross one written back.
    /// </summary>
    public struct ClearanceSettings
    {
        /// <summary>Off: collapses ignore the other layers.</summary>
        public bool Enabled;
        /// <summary>
        /// The fraction of its clearance a surface may give up; under one half, so two layers each giving up their
        /// share leave the rest between them. The default, 0.4, leaves a fifth of every gap as margin for what the rule
        /// does not check (poses, shape keys); a smaller share costs markedly more triangles, a larger one saves few.
        /// </summary>
        public float Share;
        /// <summary>
        /// In metres. Clearance beyond this counts as this, so a layer farther than the reach from every other only
        /// has to stay (1 - Share) * Reach away from them, and the search for neighbours stops here.
        /// </summary>
        public float Reach;

        public static ClearanceSettings Default => new ClearanceSettings
        {
            Enabled = true,
            Share = 0.4f,
            Reach = 0.02f,
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
        /// <summary>Multiplies the error of a collapse that removes the vertex or lands on it, and passes to the vertex that absorbs it. 1 is neutral; larger keeps it and its neighbourhood longer; under 1, or NaN, reads as 1.</summary>
        public NativeArray<float> CostMultiplier;
    }
}
