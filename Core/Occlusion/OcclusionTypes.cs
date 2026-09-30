using System;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Occlusion
{
    /// <summary>How one occluder submesh takes part in blocking rays.</summary>
    public enum OccluderMode : byte
    {
        /// <summary>Opaque, unless the submesh carries an <see cref="AlphaMap"/>: then a hit where the map samples see-through does not block.</summary>
        Auto = 0,
        /// <summary>Blocks wherever it is hit; any alpha map is ignored.</summary>
        Opaque = 1,
        /// <summary>Blocks nothing, and is not met at all.</summary>
        NeverOccludes = 2,
    }

    /// <summary>
    /// The material's cull mode. A face's front is the side from which its corners run clockwise, Unity's
    /// convention, in avatar-root space; the extractor flips the winding of a mirrored renderer so this
    /// holds after its transform. A culled side does not render, so a ray that meets it is not blocked.
    /// </summary>
    public enum FaceCulling : byte
    {
        /// <summary>Only the front renders: a ray blocks only on a face whose front looks along the ray, toward the viewer at its far end.</summary>
        Back = 0,
        /// <summary>Both sides render.</summary>
        Off = 1,
        /// <summary>Only the back renders.</summary>
        Front = 2,
    }

    /// <summary>How a texture coordinate outside [0, 1) reaches a texel, as Unity's <c>TextureWrapMode</c> does.</summary>
    public enum AlphaWrap : byte
    {
        Repeat = 0,
        Clamp = 1,
        Mirror = 2,
        MirrorOnce = 3,
    }

    /// <summary>
    /// An occluder material's alpha, sampled at a hit's interpolated UV0. Texels run row by row from the
    /// bottom row, the order <c>Texture2D.GetPixels32</c> returns, so UV (0, 0) is texel 0. UVs outside the
    /// square reach a texel by <see cref="WrapU"/> and <see cref="WrapV"/>.
    /// A hit blocks only when all four texels of its bilinear footprint reach <see cref="Threshold"/>,
    /// so an edge between opaque and see-through reads as see-through.
    /// </summary>
    public sealed class AlphaMap
    {
        public readonly int Width;
        public readonly int Height;
        /// <summary>Alpha in [0, 1] below which a texel is see-through.</summary>
        public readonly float Threshold;
        /// <summary>The material's tiling and offset: the map is sampled at uv * xy + zw.</summary>
        public float4 ScaleOffset = new float4(1, 1, 0, 0);
        public AlphaWrap WrapU = AlphaWrap.Repeat;
        public AlphaWrap WrapV = AlphaWrap.Repeat;
        internal readonly byte[] Alpha;

        /// <summary>Alpha as bytes, 0 to 255.</summary>
        public AlphaMap(int width, int height, byte[] alpha, float threshold)
        {
            Check(width, height, alpha?.Length ?? -1);
            Width = width; Height = height; Threshold = threshold;
            Alpha = (byte[])alpha.Clone();
        }

        /// <summary>Alpha as floats in [0, 1], stored quantised down to bytes, so a texel under the threshold never reads over it.</summary>
        public AlphaMap(int width, int height, float[] alpha, float threshold)
        {
            Check(width, height, alpha?.Length ?? -1);
            Width = width; Height = height; Threshold = threshold;
            Alpha = new byte[alpha.Length];
            for (var i = 0; i < alpha.Length; i++) Alpha[i] = (byte)math.floor(math.saturate(alpha[i]) * 255f);
        }

        static void Check(int width, int height, int length)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException($"alpha map is {width}x{height}; both sides must be positive");
            if (length != width * height) throw new ArgumentException($"alpha map is {width}x{height} but holds {length} texels");
        }
    }

    /// <summary>One submesh's material, as occlusion reads it: how it blocks rays as an occluder, and, by its culling, which sides of its own triangles must be covered to hide them.</summary>
    public sealed class OccluderSubmesh
    {
        public OccluderMode Mode = OccluderMode.Auto;
        public FaceCulling Culling = FaceCulling.Back;
        /// <summary>
        /// Which sides of this submesh's own triangles draw, where that reading differs from <see cref="Culling"/>:
        /// a culling that cannot be read is taken as back-culled when it blocks rays, which blocks less, but as
        /// drawing both sides when its triangles might be hidden, which keeps more. Null reads as <see cref="Culling"/>.
        /// </summary>
        public FaceCulling? CandidateCulling;
        /// <summary>Sampled when <see cref="Mode"/> is Auto; null reads as opaque.</summary>
        public AlphaMap Alpha;
    }

    /// <summary>One renderer's geometry and its part in occlusion.</summary>
    public sealed class OcclusionMesh
    {
        /// <summary>Read, never disposed, by the solver.</summary>
        public EvalMesh Mesh;
        /// <summary>True only for a renderer that is always on. Every mesh has candidates to be hidden; only these block rays.</summary>
        public bool Occludes;
        /// <summary>Per submesh index, for every renderer, occluder or not: culling Off or Front owes a triangle cover on both sides. A missing or null entry reads as Auto, back-culled, opaque.</summary>
        public OccluderSubmesh[] Submeshes;
        /// <summary>
        /// Optional, per triangle: true for one that may be hidden. Any other is never hidden, and a vertex only such
        /// triangles use is never sampled, so a solve costs what its candidates cost. Null reads as every triangle.
        /// </summary>
        public bool[] Candidate;
        /// <summary>
        /// Optional, per triangle: true for one that blocks rays, when <see cref="Occludes"/>; the rule's own limits on
        /// an occluder still hold. Null reads as every triangle. With <see cref="Candidate"/> it asks whether one set of
        /// a renderer's triangles is covered while another set stands, over the same vertices, and the renderer still
        /// never covers itself, since both sets are one mesh.
        /// </summary>
        public bool[] Blocks;
        /// <summary>
        /// How far, in metres, this mesh's faces may have moved inward past where they stood when they covered. A sample
        /// whose cone escapes, and whose <see cref="CoveredBy"/> names this mesh, is still covered when a face of this mesh
        /// lies straight behind it, back along its normal, within this far: the cover sank past it, and a viewer sees the
        /// cover. Zero, the default, reaches nothing behind any sample. The rule's own limits on an occluder still hold.
        /// A mesh at index 63 or later of the solve's list has none, since the bit that would name it names every mesh
        /// from 63 on.
        /// </summary>
        public float Setback;
        /// <summary>
        /// Optional, two entries per vertex, [2v] for its front sample and [2v + 1] for its back one: the meshes that covered
        /// that sample before, bit min(index, 63) for the mesh at that index of this solve's list, as
        /// <see cref="OcclusionResult.CoveredBy"/> reports them. Behind a sample, only the faces of a mesh named for it cover
        /// it (<see cref="Setback"/>). Null names every mesh.
        /// </summary>
        public ulong[] CoveredBy;
        /// <summary>
        /// Optional, two entries per triangle, [2t] for the samples inside it on its front and [2t + 1] for those on its back:
        /// as <see cref="CoveredBy"/>. Null names every mesh.
        /// </summary>
        public ulong[] InteriorCoveredBy;
    }

    /// <summary>The avatar's bone tree, indexed by the avatar-wide bone id <see cref="BoneInfluence.Bone"/> names. Read, never disposed, by the solver.</summary>
    public struct OcclusionBones
    {
        /// <summary>Each bone's parent id, or -1 for a root.</summary>
        public NativeArray<int> Parents;
        /// <summary>
        /// Optional, per bone: true for a bone an animation can collapse or scale to nothing, such as Modular
        /// Avatar's NaNimation bones for conditional mesh deletion or a scale-to-zero toggle. A face with any
        /// weight on one can vanish while its renderer stays on, so it never blocks. Not created reads as none.
        /// </summary>
        public NativeArray<bool> Vanishes;
    }

    /// <summary>
    /// The shared-weights rule's parameters. <see cref="Default"/> holds the starting values; change one only
    /// against a measurement.
    /// </summary>
    public struct OcclusionSettings
    {
        /// <summary>Half-angle of the cone of rays about a sample's normal, in degrees.</summary>
        public float ConeDegrees;
        /// <summary>Angular spacing of the cone's rings, in degrees.</summary>
        public float RingDegrees;
        /// <summary>Weight share a hit face must hold on the sample's allowed bones to block.</summary>
        public float Share;
        /// <summary>Ray length in metres; a ray that meets no blocking face within it escapes.</summary>
        public float Reach;
        /// <summary>Parent-or-child steps across the bone tree that still count as the same bone.</summary>
        public int KinSteps;
        /// <summary>A vertex whose influences sum below this is declined as unweighted.</summary>
        public float MinRawWeight;
        /// <summary>An influence below this weight is not one of the sample's bones.</summary>
        public float MinInfluence;
        /// <summary>How far along its normal a ray starts from its sample, in metres. A face nearer than this does not cover.</summary>
        public float OriginOffset;
        /// <summary>
        /// A triangle whose longest edge exceeds this, in metres, is also sampled inside, on a lattice this fine
        /// (at most 16 steps an edge; a triangle needing more, at rest or in a configuration, is kept), so a hole in
        /// the cover over its middle keeps it. Zero turns interior sampling off.
        /// </summary>
        public float InteriorSpacing;
        /// <summary>A shape delta shorter than this, in metres, does not move its vertex.</summary>
        public float MoveEpsilon;

        internal BvhSplit Split;

        public static OcclusionSettings Default => new OcclusionSettings
        {
            ConeDegrees = 75f,
            RingDegrees = 15f,
            Share = 0.5f,
            Reach = 1f,
            KinSteps = 2,
            MinRawWeight = 0.5f,
            MinInfluence = 0.01f,
            OriginOffset = 1e-4f,
            InteriorSpacing = 0.02f,
            MoveEpsilon = 1e-5f,
            Split = BvhSplit.Sah,
        };

        internal void Validate()
        {
            if (!(ConeDegrees > 0f && ConeDegrees <= 90f)) throw new ArgumentException($"ConeDegrees is {ConeDegrees}; it must be in (0, 90]");
            if (!(RingDegrees > 0f)) throw new ArgumentException($"RingDegrees is {RingDegrees}; it must be positive");
            if (!(Share > 0f && Share <= 1f)) throw new ArgumentException($"Share is {Share}; it must be in (0, 1]");
            if (!(Reach > 0f)) throw new ArgumentException($"Reach is {Reach}; it must be positive");
            if (KinSteps < 0 || KinSteps > MaxKin) throw new ArgumentException($"KinSteps is {KinSteps}; it must be in [0, {MaxKin}]");
            if (!(OriginOffset >= 0f)) throw new ArgumentException($"OriginOffset is {OriginOffset}; it must not be negative");
            if (!(InteriorSpacing >= 0f)) throw new ArgumentException($"InteriorSpacing is {InteriorSpacing}; it must not be negative");
            if (!(MinInfluence >= 0f) || !(MinRawWeight >= 0f) || !(MoveEpsilon >= 0f)) throw new ArgumentException("MinInfluence, MinRawWeight and MoveEpsilon must not be negative");
        }

        internal const int MaxKin = 6;
    }

    /// <summary>
    /// Why a mesh's vertices were not covered, one reason per declined vertex: the first failure met, at rest
    /// before any configuration. Only vertices some candidate triangle uses are counted.
    /// </summary>
    public struct DeclineCounts
    {
        /// <summary>Influences sum under <see cref="OcclusionSettings.MinRawWeight"/>.</summary>
        public int Unweighted;
        /// <summary>Some ray met occluder faces, but none riding the vertex's bones.</summary>
        public int Swings;
        /// <summary>Some ray met a face riding the vertex's bones, but only where it is see-through or culled.</summary>
        public int SeeThrough;
        /// <summary>Some ray met no occluder face within reach, or the vertex has no usable normal.</summary>
        public int Escaped;
        /// <summary>Of the above, how many were covered at rest and uncovered in a configuration.</summary>
        public int ExposedByFrame;
        /// <summary>Of the above, how many were covered only by faces that are themselves hidden and deleted.</summary>
        public int ExposedByDeletion;

        public int Total => Unweighted + Swings + SeeThrough + Escaped;

        /// <summary>Adds another mesh's counts to these, reason by reason.</summary>
        public void Add(in DeclineCounts other)
        {
            Unweighted += other.Unweighted;
            Swings += other.Swings;
            SeeThrough += other.SeeThrough;
            Escaped += other.Escaped;
            ExposedByFrame += other.ExposedByFrame;
            ExposedByDeletion += other.ExposedByDeletion;
        }
    }

    /// <summary>One mesh's result, over its own indices. The caller disposes it.</summary>
    public struct OcclusionResult : IDisposable
    {
        /// <summary>Per triangle: true when it is hidden in every configuration and may be deleted.</summary>
        public NativeArray<bool> HiddenTriangles;
        /// <summary>Per vertex: covered in every configuration it was evaluated in, on each side a triangle using it renders. Informational; a triangle's verdict is <see cref="HiddenTriangles"/>.</summary>
        public NativeArray<bool> CoveredVertices;
        /// <summary>
        /// Per vertex: covered on its front in every configuration it was evaluated in, whatever its back. Informational:
        /// against <see cref="HiddenTriangles"/> it tells what having to cover a triangle's back as well kept.
        /// </summary>
        public NativeArray<bool> FrontCovered;
        /// <summary>
        /// Two entries per vertex, [2v] for its front sample and [2v + 1] for its back one (zero where it has none): every
        /// mesh whose faces blocked a ray of that sample, at rest or in a configuration, one bit per mesh index (meshes from
        /// 63 on share bit 63). A mesh that covered a ray another mesh also covered may be missing, since each ray names the
        /// first blocker met. Informational; what a later solve takes as <see cref="OcclusionMesh.CoveredBy"/>.
        /// </summary>
        public NativeArray<ulong> CoveredBy;
        /// <summary>
        /// Two entries per triangle, [2t] over the samples inside it on its front and [2t + 1] over those on its back: as
        /// <see cref="CoveredBy"/>; zero for a side sampled at its corners only.
        /// </summary>
        public NativeArray<ulong> InteriorCoveredBy;
        public DeclineCounts Declined;

        public int HiddenCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < HiddenTriangles.Length; i++) if (HiddenTriangles[i]) n++;
                return n;
            }
        }

        public void Dispose()
        {
            if (HiddenTriangles.IsCreated) HiddenTriangles.Dispose();
            if (CoveredVertices.IsCreated) CoveredVertices.Dispose();
            if (FrontCovered.IsCreated) FrontCovered.Dispose();
            if (CoveredBy.IsCreated) CoveredBy.Dispose();
            if (InteriorCoveredBy.IsCreated) InteriorCoveredBy.Dispose();
        }
    }

    /// <summary>What a solve cost, for measurement.</summary>
    public struct OcclusionStats
    {
        public int OccluderTriangles;
        public int BvhNodes;
        public int VertexSamples;
        public int InteriorSamples;
        /// <summary>Live frames over every mesh.</summary>
        public int LiveFrames;
        /// <summary>States of the avatar evaluated besides rest: each frame alone, or the frames of one configuration id together.</summary>
        public int Configurations;
        public long Rays;
        public double BuildMs;
        public double RestMs;
        public double FramesMs;
        /// <summary>Of <see cref="FramesMs"/> and <see cref="DeletionPassMs"/>, the single-threaded part: finding what each configuration puts at risk and building its moved-face trees.</summary>
        public double FramePrepMs;
        public double DeletionPassMs;
        /// <summary>Samples re-cast with the hidden occluder faces removed.</summary>
        public int DeletionPassSamples;
        public double TotalMs;
    }
}
