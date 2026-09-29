using System;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.runtime;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Localization;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Ryan6Vrc.MeshOptimizer.Plugin.MeshOptimizerPlugin))]

namespace Ryan6Vrc.MeshOptimizer.Plugin
{
    /// <summary>
    /// Runs the optimizer once per avatar in NDMF's Optimizing phase: after Modular Avatar, and (through the build's
    /// callback order) after VRCFury, so it sees every merged animator and every generated mesh; before Avatar
    /// Optimizer and TexTransTool, which merge and rename renderers; and before the Limitex texture compressor,
    /// which rewrites the alpha occlusion samples.
    /// </summary>
    internal sealed class MeshOptimizerPlugin : Plugin<MeshOptimizerPlugin>
    {
        public const string Name = "com.ryan6vrc.mesh-optimizer";

        public override string QualifiedName => Name;
        public override string DisplayName => "Mesh Optimizer";

        protected override void Configure()
        {
            // AfterPass is not used: in NDMF 1.14.8 it records its constraint the other way round.
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .BeforePlugin("dev.limitex.avatar-compressor.texture")
                .BeforePlugin("net.rs64.tex-trans-tool")
                // Compatible rather than required: an avatar without the component should not pay for NDMF cloning
                // its animators, and one that has it activates them in the pass. Either way NDMF leaves an active
                // context standing across this pass instead of committing it.
                .WithCompatibleExtension(typeof(AnimatorServicesContext), s => s.Run(MeshOptimizerPass.Instance));
        }
    }

    /// <summary>The door from NDMF: gathers what the pipeline needs from the build, runs it, applies and reports.</summary>
    internal sealed class MeshOptimizerPass : Pass<MeshOptimizerPass>
    {
        public override string QualifiedName => MeshOptimizerPlugin.Name + ".optimize";
        public override string DisplayName => "Optimize meshes";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootTransform;
            var components = root.GetComponentsInChildren<AvatarMeshOptimizer>(true);
            if (components.Length == 0) return;
            try
            {
                var settings = components[0];
                if (components.Length > 1)
                    Report(new Diagnostic(DiagnosticKeys.MultipleComponents, true,
                        string.Join(", ", components.Select(c => RuntimeUtil.RelativePath(root.gameObject, c.gameObject) ?? c.name)), settings));

                var watch = Stopwatch.StartNew();
                var services = context.ActivateExtensionContextRecursive<AnimatorServicesContext>();
                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
                var liveness = LivenessSources.Classify(context, skinned);
                var scaleCurves = UnstableBones.FromAnimatorServices(services, root)
                    .Concat(UnstableBones.FromLegacyAnimations(root)).ToList();
                var livenessMs = watch.Elapsed.TotalMilliseconds;

                var report = OptimizerPipeline.Run(new PipelineInput
                {
                    Root = root,
                    Settings = settings,
                    Liveness = liveness,
                    ScaleCurves = scaleCurves,
                    LivenessMs = livenessMs,
                });
                OptimizerPipeline.Apply(report);
                foreach (var d in report.Diagnostics) Report(d);
                Debug.Log(report.Summary());
            }
            catch (Exception e)
            {
                // Nothing is applied until the pipeline has finished, so the avatar builds as it was.
                Debug.LogException(e);
                Report(new Diagnostic(DiagnosticKeys.PassFailed, true, e.Message));
            }
            finally
            {
                foreach (var c in components) Object.DestroyImmediate(c);
            }
        }

        /// <summary>
        /// NDMF opens its error window for any report at all, whatever its severity, so only problems are reported;
        /// the routine summary goes to the console.
        /// </summary>
        static void Report(Diagnostic d) =>
            ErrorReport.ReportError(L10n.Localizer, d.Warning ? ErrorSeverity.NonFatal : ErrorSeverity.Information, d.Key, d.Args);
    }
}
