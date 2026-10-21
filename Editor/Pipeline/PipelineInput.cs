using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>What the pipeline needs; nothing here needs a build.</summary>
    internal sealed class PipelineInput
    {
        /// <summary>The avatar root; every mesh and skinned renderer under it is surveyed.</summary>
        public Transform Root;
        /// <summary>The component whose settings the pass runs with; a cut reads its target and protections as they stand.</summary>
        public AvatarMeshOptimizer Settings;
        /// <summary>Per skinned renderer, per shape key. A renderer missing from it has every key evaluated as live, from the weight it holds to full weight.</summary>
        public IReadOnlyDictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Liveness;
        /// <summary>Groups of keys that cannot be driven apart (<see cref="ShapeKeyLinks"/>). Null reads as none: every live key moves alone.</summary>
        public IReadOnlyList<LinkedKey[]> Links;
        /// <summary>
        /// Keys that link with none though another key is driven by exactly the same set of clips of known identity, each
        /// with why (<see cref="ShapeKeyLinks"/>); the report counts those the pass evaluates as live. Null reads as none.
        /// </summary>
        public IReadOnlyList<(LinkedKey key, LinkRefusal reason)> Unlinked;
        /// <summary>Scale curves any clip can play, for <see cref="UnstableBones"/>. Null reads as none.</summary>
        public IEnumerable<ScaleCurve> ScaleCurves;
        /// <summary>
        /// Per renderer, the one whose mesh and materials are read in its place: the edit-mode preview's proxies, which carry
        /// what an earlier preview filter made of the renderer. Its bones, transforms, shape-key weights and settings are
        /// still read from the renderer itself. Null, or a renderer missing from it, reads the renderer itself.
        /// </summary>
        public IReadOnlyDictionary<Renderer, Renderer> Drawn;
        /// <summary>The occlusion solve's settings, which the cut's re-judging reads too.</summary>
        public OcclusionSettings Occlusion = OcclusionSettings.Default;
        /// <summary>
        /// Whether <see cref="OptimizerPipeline.Prepare"/> removes hidden triangles when the component asks it to. On for a
        /// build; the edit-mode preview turns it off, since occlusion is the costly step and the preview shows decimation.
        /// </summary>
        public bool Occlude = true;
        /// <summary>The settings each renderer's collapse sequence is recorded with (<see cref="BuildSettings.Decimation"/>).</summary>
        public DecimationSettings Decimation = BuildSettings.Decimation;
        /// <summary>
        /// Whether a cut judges the removed triangles again against the meshes it writes (<see cref="OptimizerPipeline.Cut"/>).
        /// Off, every mesh is written with the removals as occlusion first judged them.
        /// </summary>
        public bool Rejudge = BuildSettings.Rejudging;
        /// <summary>
        /// Whether a triangle occlusion removed stands in the clearance surface as a neighbour of the other layers
        /// (<see cref="OptimizerPipeline.ClearanceSurfaceOf"/>), so one re-judging writes back comes back clear. Off, the
        /// surface holds only what occlusion kept, and a layer may cross a triangle written back.
        /// </summary>
        public bool RemovedAsNeighbours = BuildSettings.RemovedAsNeighbours;
        /// <summary>
        /// Whether the re-judge lets an always-on renderer the cut decimated cover a removed triangle from as far behind as
        /// its recorded error (<see cref="CutRenderer.Setback"/>). Off, every renderer's setback is 0: a triangle comes back
        /// wherever the rule, judging from where it stood, finds it uncovered.
        /// </summary>
        public bool Setback = BuildSettings.Setback;
        /// <summary>
        /// What the gate measures on the avatar once the meshes are applied (<see cref="OptimizerPipeline.MeasureGate"/>),
        /// into <see cref="OptimizationReport.Gate"/>; null, the default, measures nothing.
        /// </summary>
        public GateRunOptions Gate = BuildSettings.Gate;
        /// <summary>Time the caller spent classifying liveness, for the report.</summary>
        public double LivenessMs;
        /// <summary>Called before each renderer enters a stage. A test seam: throwing here is a stage failing on that renderer.</summary>
        public Action<Renderer, PipelineStage> BeforeStage;
        /// <summary>
        /// Called before each step taken for the whole avatar at once. A test seam: throwing here escapes
        /// <see cref="OptimizerPipeline.Prepare"/> or <see cref="OptimizerPipeline.Cut"/>, as a failure no one renderer
        /// can be blamed for would.
        /// </summary>
        public Action<AvatarStep> BeforeStep;

        /// <summary>The renderer whose mesh and materials are read for <paramref name="renderer"/> (<see cref="Drawn"/>).</summary>
        public Renderer DrawnBy(Renderer renderer) => DrawnBy(Drawn, renderer);

        /// <summary>As the instance method, from a map that is not yet on an input.</summary>
        public static Renderer DrawnBy(IReadOnlyDictionary<Renderer, Renderer> drawn, Renderer renderer) =>
            drawn != null && renderer != null && drawn.TryGetValue(renderer, out var proxy) && proxy != null ? proxy : renderer;
    }

    /// <summary>The steps taken for the whole avatar at once, as <see cref="PipelineInput.BeforeStep"/> names them.</summary>
    internal enum AvatarStep
    {
        Occlusion,
        Clearance,
        Recording,
        Allocation,
        Rejudging,
        Writing,
    }

    /// <summary>The per-renderer stages, as a failure names them.</summary>
    internal enum PipelineStage
    {
        Extraction,
        Decimation,
        Writing,
    }
}
