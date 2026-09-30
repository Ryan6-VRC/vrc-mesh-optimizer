using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>
    /// An avatar as <see cref="OptimizerPipeline.Prepare"/> leaves it for <see cref="OptimizerPipeline.Cut"/>: every
    /// renderer surveyed, the extracted ones' evaluation meshes, the triangles occlusion removed and each decimated
    /// renderer's recorded collapse sequence, with what preparing found about the avatar as a whole. Cutting reads it and
    /// changes nothing in it, so it can be cut more than once. It owns native memory: dispose it.
    /// </summary>
    internal sealed class PreparedAvatar : IDisposable
    {
        public readonly PipelineInput Input;
        /// <summary>Every mesh renderer and skinned renderer under the root, in hierarchy order.</summary>
        public readonly List<PreparedRenderer> Renderers = new List<PreparedRenderer>();
        /// <summary>Of <see cref="Renderers"/>, those extracted, in the same order.</summary>
        public readonly List<PreparedRenderer> Extracted = new List<PreparedRenderer>();

        public readonly List<LinkReport> Links = new List<LinkReport>();
        public readonly List<ConfigurationReport> Configurations = new List<ConfigurationReport>();
        /// <summary>Per reason, the count <see cref="OptimizationReport.LinkRefusals"/> reports.</summary>
        public readonly Dictionary<LinkRefusal, int> LinkRefusals = new Dictionary<LinkRefusal, int>();
        public readonly List<Material> UnmatchedMaterialOverrides = new List<Material>();
        public bool OcclusionRan;
        public DeclineCounts Declined;
        public OcclusionStats OcclusionStats;
        /// <summary>
        /// The avatar's bone table as the occlusion solve read it, with the unstable bones marked as vanishing, kept so a cut
        /// judges its removals again by the same rule. Set only once occlusion ran.
        /// </summary>
        public int[] BoneParents;
        public bool[] VanishingBones;
        public readonly List<string> UnstableBones = new List<string>();
        /// <summary>The problems preparing raised, in the order it raised them.</summary>
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        /// <summary>Preparing's stages; cutting fills in its own.</summary>
        public StageTimes Times;
        /// <summary>Wall-clock preparing took, in milliseconds, without the caller's liveness.</summary>
        public double PrepareMs;

        /// <summary>True once disposed, when nothing is left to cut.</summary>
        public bool Disposed { get; private set; }

        public PreparedAvatar(PipelineInput input) => Input = input;

        public void Dispose()
        {
            foreach (var r in Renderers) r.Dispose();
            Disposed = true;
        }
    }

    /// <summary>
    /// One renderer as preparing leaves it: what it is, why it was left as it was if it was, and what each stage found on
    /// it. Stages read these as their inputs; the renderer's report is written from them, once, by the cut.
    /// </summary>
    internal sealed class PreparedRenderer : IDisposable
    {
        public Renderer Renderer;
        public Mesh Source;
        public string Path;
        public int TrianglesBefore;
        public SkipReason Skip;
        public string SkipDetail;
        public ShapeKeyLiveness[] Liveness;
        /// <summary>This renderer's frames in the avatar's linked configurations.</summary>
        public readonly List<LiveFrame> Linked = new List<LiveFrame>();

        public EvalMesh Eval;
        public bool Extracted;
        public bool Mirrored;

        public bool Occludes;
        /// <summary>Per submesh, the sides it draws and how it blocks, as the occlusion solve read them; null when occlusion did not run.</summary>
        public OccluderSubmesh[] OccluderSubmeshes;
        /// <summary>Per evaluation triangle, true when occlusion removed it; null when occlusion did not decide on this renderer.</summary>
        public bool[] Hidden;
        /// <summary>
        /// Per evaluation vertex, and per evaluation triangle for the samples inside it, front then back, the renderers whose
        /// faces covered that side in the occlusion solve, by their index in <see cref="PreparedAvatar.Extracted"/>
        /// (<see cref="OcclusionResult.CoveredBy"/>); null when occlusion did not decide on this renderer.
        /// </summary>
        public ulong[] CoveredBy, InteriorCoveredBy;
        public int KeptOnUnstableBones;
        public int KeptForFur;
        public int KeptForOutline;
        public DeclineCounts Declined;

        public float Weight = 1f;
        public bool ExcludedFromDecimation;
        public int LockedVertices;
        public int LockedFromZeroScale;
        public int LockedForFur;
        public int CostRaisedForGrowth;
        public int FloorTriangles;
        /// <summary>
        /// Per extracted triangle, its layer in the clearance surface; one occlusion removed carries its piece's layer, and -1
        /// only with <see cref="PipelineInput.RemovedAsNeighbours"/> off. Created only while recording with the rule on.
        /// </summary>
        public NativeArray<int> Layers;
        public CollapseSequence Sequence;
        public bool Decimated;
        public int SequenceLength => Decimated ? Sequence.Length : 0;
        public int NearVertices;
        public long ClearanceRefused;

        public int HiddenCount
        {
            get
            {
                if (Hidden == null) return 0;
                var n = 0;
                foreach (var h in Hidden) if (h) n++;
                return n;
            }
        }

        public void Dispose()
        {
            if (Extracted) Eval.Dispose();
            Extracted = false;
            if (Decimated) Sequence.Dispose();
            Decimated = false;
            if (Layers.IsCreated) Layers.Dispose();
            Layers = default;
        }
    }
}
