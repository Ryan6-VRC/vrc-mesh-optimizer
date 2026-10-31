using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Animators;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.MeshOptimizer.Preview
{
    /// <summary>Where the preview's shape-key liveness came from, which the inspector's note names.</summary>
    internal enum PreviewLiveness
    {
        /// <summary>
        /// Classified from what the avatar descriptor holds: its playable layers' controllers
        /// (<see cref="AuthoredControllers"/>), its viseme and eyelid shape keys.
        /// </summary>
        Descriptor,
        /// <summary>The root has no descriptor, so every key is evaluated live from its held weight to full.</summary>
        EveryKeyLive,
    }

    /// <summary>
    /// What the preview hands the pipeline in place of a build's: the one seam where a preview's input differs from a
    /// build's. A build classifies shape keys and finds scale curves through NDMF's animator services, which exist only
    /// inside a build; the preview reads what the avatar descriptor holds, as authored, and removes no hidden triangles.
    /// Each renderer's mesh and materials are read from its proxy, as <see cref="For"/> is handed them (original to proxy), which
    /// carries what an earlier preview filter made of it, such as a Modular Avatar deletion; everything else is read from
    /// the scene avatar. Nothing another tool adds only at build is read, so the preview's counts are an estimate and the
    /// build's report is exact.
    /// </summary>
    internal static class PreviewInput
    {
        /// <summary>
        /// The preview's input for the avatar at <paramref name="root"/>, with <paramref name="source"/> naming where its
        /// liveness came from and <paramref name="proxies"/> mapping each original renderer to its proxy.
        /// </summary>
        public static PipelineInput For(Transform root, AvatarMeshOptimizer settings, out PreviewLiveness source,
            IReadOnlyDictionary<Renderer, Renderer> proxies = null)
        {
            var input = new PipelineInput { Root = root, Settings = settings, Occlude = false, Drawn = proxies };
            var scaleCurves = UnstableBones.FromLegacyAnimations(root);
            if (!root.TryGetComponent<VRCAvatarDescriptor>(out var descriptor))
            {
                // No liveness is every key live (OptimizerPipeline.LivenessOf).
                source = PreviewLiveness.EveryKeyLive;
            }
            else
            {
                source = PreviewLiveness.Descriptor;
                var controllers = AuthoredControllers.Of(descriptor);
                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
                // The classifier adds the descriptor's visemes, eyelids and face tracking (LivenessSources.FromDescriptor), as a build's does.
                var bindings = LivenessSources.FromControllers(controllers, root).Concat(LivenessSources.FromLegacyAnimations(root)).ToList();
                input.Liveness = LivenessSources.Classify(root, skinned, bindings, out var links, out var unlinked);
                input.Links = links;
                input.Unlinked = unlinked;
                scaleCurves = scaleCurves.Concat(UnstableBones.FromControllers(controllers));
            }
            input.ScaleCurves = scaleCurves.ToList();
            return input;
        }
    }
}
