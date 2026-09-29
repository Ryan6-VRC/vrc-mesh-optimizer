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
        /// <summary>Per reason, how many keys the pass evaluates moved alone though the same clips drive them with another.</summary>
        public readonly Dictionary<LinkRefusal, int> LinkRefusals = new Dictionary<LinkRefusal, int>();
        public readonly List<Material> UnmatchedMaterialOverrides = new List<Material>();
        public bool OcclusionRan;
        public DeclineCounts Declined;
        public OcclusionStats OcclusionStats;
        public readonly List<string> UnstableBones = new List<string>();
        /// <summary>The problems preparing raised, in the order it raised them.</summary>
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        /// <summary>Preparing's stages; cutting fills in its own.</summary>
        public StageTimes Times;
        /// <summary>Wall-clock preparing took, in milliseconds, without the caller's liveness.</summary>
        public double PrepareMs;

        public PreparedAvatar(PipelineInput input) => Input = input;

        public void Dispose()
        {
            foreach (var r in Renderers) r.Dispose();
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
        /// <summary>Per evaluation triangle, true when occlusion removed it; null when occlusion did not decide on this renderer.</summary>
        public bool[] Hidden;
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
        /// <summary>Per extracted triangle, its layer in the clearance surface, -1 for one occlusion removed; created only while recording with the rule on.</summary>
        public NativeArray<int> Layers;
        public CollapseSequence Sequence;
        public bool Decimated;
        public int SequenceLength;
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
