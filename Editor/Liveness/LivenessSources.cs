using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.runtime;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Ryan6Vrc.MeshOptimizer.Liveness
{
    /// <summary>
    /// Gathers what drives shape keys on an avatar in the build and hands it to <see cref="ShapeKeyClassifier"/>.
    /// By the Optimizing phase Modular Avatar and VRCFury have merged every animator into the descriptor's
    /// playable layers and turned reactive components into clips or static weights, so the merged layers, the
    /// child Animators, the legacy Animation components and the descriptor are every driver there is.
    /// </summary>
    internal static class LivenessSources
    {
        const string BlendShapePrefix = "blendShape.";

        /// <summary>
        /// Classifies every shape key of each renderer on the avatar being built. The calling pass must hold
        /// <see cref="AnimatorServicesContext"/>, which also keeps NDMF from committing the controllers around it.
        /// </summary>
        public static Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Classify(
            BuildContext context, IReadOnlyList<SkinnedMeshRenderer> renderers)
        {
            var root = context.AvatarRootTransform;
            var inputs = renderers.Select(r => Describe(r, root)).ToList();

            var bindings = FromAnimatorServices(context.Extension<AnimatorServicesContext>(), root)
                .Concat(FromLegacyAnimations(root));
            var driven = FromDescriptor(root).Concat(MmdKeys(root));

            var table = ShapeKeyClassifier.Classify(inputs, bindings, driven);
            var result = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>();
            for (var i = 0; i < renderers.Count; i++) result[renderers[i]] = table[i];
            return result;
        }

        public static RendererShapeKeys Describe(SkinnedMeshRenderer renderer, Transform root)
        {
            var mesh = renderer.sharedMesh;
            var count = mesh != null ? mesh.blendShapeCount : 0;
            var keys = new ShapeKeyInput[count];
            for (var k = 0; k < count; k++)
            {
                var full = mesh.GetBlendShapeFrameWeight(k, mesh.GetBlendShapeFrameCount(k) - 1);
                keys[k] = new ShapeKeyInput(mesh.GetBlendShapeName(k), renderer.GetBlendShapeWeight(k), full);
            }
            return new RendererShapeKeys(PathOf(root, renderer.transform), keys);
        }

        /// <summary>
        /// Every shape-key curve in every controller NDMF holds: the descriptor's playable layers and every
        /// Animator's controller. A descriptor layer's paths are virtual paths from the avatar root; a child
        /// Animator's are relative to that Animator.
        /// </summary>
        public static IEnumerable<ShapeKeyBinding> FromAnimatorServices(AnimatorServicesContext services, Transform root)
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
                    if (!IsShapeKey(binding)) continue;
                    var target = owner == root
                        ? (remapper.GetObjectForPath(binding.path)?.transform ?? root.Find(binding.path))
                        : owner.Find(binding.path);
                    if (target == null) continue;
                    var curve = clip.GetFloatCurve(binding);
                    if (curve == null) continue;
                    yield return new ShapeKeyBinding(PathOf(root, target), binding.propertyName.Substring(BlendShapePrefix.Length),
                        ShapeKeyClassifier.CurveValues(curve));
                }
            }
        }

        /// <summary>Legacy Animation components, which NDMF does not virtualize. Paths are relative to the component.</summary>
        public static IEnumerable<ShapeKeyBinding> FromLegacyAnimations(Transform root)
        {
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
            foreach (var clip in AnimationUtility.GetAnimationClips(animation.gameObject).Where(c => c != null).Distinct())
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsShapeKey(binding)) continue;
                var target = animation.transform.Find(binding.path);
                if (target == null) continue;
                yield return new ShapeKeyBinding(PathOf(root, target), binding.propertyName.Substring(BlendShapePrefix.Length),
                    ShapeKeyClassifier.CurveValues(AnimationUtility.GetEditorCurve(clip, binding)));
            }
        }

        /// <summary>The descriptor's visemes, jaw flap and eyelids, which VRChat drives without a clip.</summary>
        public static IEnumerable<DrivenKey> FromDescriptor(Transform root)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) yield break;

            var viseme = descriptor.VisemeSkinnedMesh;
            if (viseme != null)
            {
                var path = PathOf(root, viseme.transform);
                if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && descriptor.VisemeBlendShapes != null)
                    foreach (var name in descriptor.VisemeBlendShapes)
                        if (!string.IsNullOrEmpty(name)) yield return new DrivenKey(path, name);
                if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && !string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName))
                    yield return new DrivenKey(path, descriptor.MouthOpenBlendShapeName);
            }

            // Eyelids are indices into the mesh, not names. They count whether or not eye look is enabled,
            // because counting a key live that is not costs a little reduction and the reverse costs a face.
            var eyes = descriptor.customEyeLookSettings;
            var eyelids = eyes.eyelidsSkinnedMesh;
            if (eyes.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes && eyelids != null && eyelids.sharedMesh != null && eyes.eyelidsBlendshapes != null)
            {
                var path = PathOf(root, eyelids.transform);
                var mesh = eyelids.sharedMesh;
                foreach (var index in eyes.eyelidsBlendshapes)
                    if (index >= 0 && index < mesh.blendShapeCount) yield return new DrivenKey(path, mesh.GetBlendShapeName(index));
            }
        }

        /// <summary>
        /// MMD worlds play dance motions that drive standard MMD morphs, by name, on a renderer named Body directly
        /// under the avatar root. Nothing in the avatar records that, so those names count as driven.
        /// </summary>
        public static IEnumerable<DrivenKey> MmdKeys(Transform root)
        {
            var body = root.Find("Body");
            if (body == null || !body.TryGetComponent<SkinnedMeshRenderer>(out var renderer) || renderer.sharedMesh == null) yield break;
            var mesh = renderer.sharedMesh;
            for (var k = 0; k < mesh.blendShapeCount; k++)
            {
                var name = mesh.GetBlendShapeName(k);
                if (MmdMorphs.Names.Contains(name)) yield return new DrivenKey("Body", name);
            }
        }

        static bool IsShapeKey(EditorCurveBinding binding) =>
            binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith(BlendShapePrefix);

        static string PathOf(Transform root, Transform target) => RuntimeUtil.RelativePath(root, target) ?? target.name;
    }
}
