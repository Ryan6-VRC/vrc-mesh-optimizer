using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Animators
{
    /// <summary>One float curve in a clip, with the Transform its binding path resolves to.</summary>
    internal readonly struct AnimatedCurve
    {
        public readonly Transform Target;
        public readonly EditorCurveBinding Binding;
        public readonly AnimationCurve Curve;

        public AnimatedCurve(Transform target, EditorCurveBinding binding, AnimationCurve curve)
        {
            Target = target;
            Binding = binding;
            Curve = curve;
        }
    }

    /// <summary>
    /// Every float curve anything on the avatar can play, resolved to the object it animates. By the Optimizing
    /// phase Modular Avatar and VRCFury have merged their animators into the descriptor's playable layers, so
    /// those layers, the child Animators and the legacy Animation components are every clip there is.
    /// </summary>
    internal static class AnimatedCurves
    {
        /// <summary>
        /// Curves in every controller NDMF holds: the descriptor's playable layers and every Animator's controller.
        /// A descriptor layer's paths are virtual paths from the avatar root; a child Animator's are relative to
        /// that Animator. A path that resolves to nothing is dropped.
        /// </summary>
        public static IEnumerable<AnimatedCurve> FromAnimatorServices(AnimatorServicesContext services, Transform root,
            Func<EditorCurveBinding, bool> wanted)
        {
            var remapper = services.ObjectPathRemapper;
            foreach (var pair in services.ControllerContext.Controllers)
            {
                if (pair.Value == null) continue;
                var owner = pair.Key is Animator animator && animator != null ? animator.transform : root;
                var clips = pair.Value.AllReachableNodes().OfType<VirtualClip>().Distinct();
                foreach (var clip in clips)
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    if (!wanted(binding)) continue;
                    var target = owner == root
                        ? (remapper.GetObjectForPath(binding.path)?.transform ?? Resolve(root, binding.path))
                        : Resolve(owner, binding.path);
                    if (target == null) continue;
                    var curve = clip.GetFloatCurve(binding);
                    if (curve == null) continue;
                    yield return new AnimatedCurve(target, binding, curve);
                }
            }
        }

        /// <summary>Legacy Animation components, which NDMF does not virtualize. Paths are relative to the component.</summary>
        public static IEnumerable<AnimatedCurve> FromLegacyAnimations(Transform root, Func<EditorCurveBinding, bool> wanted)
        {
            foreach (var animation in root.GetComponentsInChildren<UnityEngine.Animation>(true))
            foreach (var clip in AnimationUtility.GetAnimationClips(animation.gameObject).Where(c => c != null).Distinct())
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!wanted(binding)) continue;
                var target = Resolve(animation.transform, binding.path);
                if (target == null) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;
                yield return new AnimatedCurve(target, binding, curve);
            }
        }

        /// <summary>A path relative to a component's transform; the empty path is that transform itself.</summary>
        static Transform Resolve(Transform owner, string path) => path.Length == 0 ? owner : owner.Find(path);
    }
}
