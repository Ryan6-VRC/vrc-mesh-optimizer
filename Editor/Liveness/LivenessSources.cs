using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.runtime;
using Ryan6Vrc.MeshOptimizer.Animators;
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
        /// Classifies every shape key of each renderer on the avatar being built, and finds the keys that cannot be driven
        /// apart (<see cref="ShapeKeyLinks"/>) from the same sources, with the keys that could have linked and did not, each
        /// with why (<see cref="ShapeKeyLinks.Find(IReadOnlyList{RendererShapeKeys}, IEnumerable{ShapeKeyBinding}, IEnumerable{DrivenKey}, out Dictionary{KeyRef, LinkRefusal})"/>).
        /// The calling pass must hold <see cref="AnimatorServicesContext"/>, which also keeps NDMF from committing the
        /// controllers around it.
        /// </summary>
        public static Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Classify(
            BuildContext context, IReadOnlyList<SkinnedMeshRenderer> renderers, out List<LinkedKey[]> links,
            out List<(LinkedKey key, LinkRefusal reason)> unlinked)
        {
            var root = context.AvatarRootTransform;
            var bindings = FromAnimatorServices(context.Extension<AnimatorServicesContext>(), root)
                .Concat(FromLegacyAnimations(root)).ToList();
            return Classify(root, renderers, bindings, out links, out unlinked);
        }

        /// <summary>
        /// Classifies every shape key of each renderer under <paramref name="root"/> against the curves in
        /// <paramref name="bindings"/> and the keys the descriptor and MMD worlds drive, and finds the links, as the build's
        /// overload taking a <see cref="BuildContext"/> does from its animator services.
        /// </summary>
        public static Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Classify(
            Transform root, IReadOnlyList<SkinnedMeshRenderer> renderers, List<ShapeKeyBinding> bindings, out List<LinkedKey[]> links,
            out List<(LinkedKey key, LinkRefusal reason)> unlinked)
        {
            var inputs = renderers.Select(r => Describe(r, root)).ToList();
            var driven = FromDescriptor(root).Concat(MmdKeys(root)).ToList();

            var table = ShapeKeyClassifier.Classify(inputs, bindings, driven);
            var result = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>();
            for (var i = 0; i < renderers.Count; i++) result[renderers[i]] = table[i];
            links = ShapeKeyLinks.Find(inputs, bindings, driven, out var refused)
                .Select(g => g.Select(k => new LinkedKey(renderers[k.Renderer], k.Key)).ToArray()).ToList();
            unlinked = refused.Select(kv => (new LinkedKey(renderers[kv.Key.Renderer], kv.Key.Key), kv.Value)).ToList();
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
            return new RendererShapeKeys(PathOf(root, renderer.transform), keys, renderer.transform);
        }

        /// <summary>
        /// Every key of the renderer evaluated as live from the weight it holds to full weight: the liveness of a renderer
        /// whose drivers are not known, which judges it against every state its keys can reach.
        /// </summary>
        public static ShapeKeyLiveness[] EveryKeyLive(SkinnedMeshRenderer renderer, Transform root) =>
            Describe(renderer, root).Keys.Select(k => ShapeKeyClassifier.ClassifyKey(k, null, true)).ToArray();

        /// <summary>Every shape-key curve in every controller NDMF holds; see <see cref="AnimatedCurves.FromAnimatorServices"/>.</summary>
        public static IEnumerable<ShapeKeyBinding> FromAnimatorServices(AnimatorServicesContext services, Transform root) =>
            AnimatedCurves.FromAnimatorServices(services, root, IsShapeKey).Select(c => Binding(root, c));

        /// <summary>Shape-key curves in controllers as authored, outside a build; see <see cref="AnimatedCurves.FromControllers"/>.</summary>
        public static IEnumerable<ShapeKeyBinding> FromControllers(IEnumerable<AuthoredController> controllers, Transform root) =>
            AnimatedCurves.FromControllers(controllers, IsShapeKey).Select(c => Binding(root, c));

        /// <summary>Shape-key curves in legacy Animation components, which NDMF does not virtualize.</summary>
        public static IEnumerable<ShapeKeyBinding> FromLegacyAnimations(Transform root) =>
            AnimatedCurves.FromLegacyAnimations(root, IsShapeKey).Select(c => Binding(root, c));

        static ShapeKeyBinding Binding(Transform root, AnimatedCurve c) =>
            new ShapeKeyBinding(PathOf(root, c.Target), c.Binding.propertyName.Substring(BlendShapePrefix.Length),
                AnimatedCurves.CurveValues(c.Curve), c.Curve.keys.Select(k => k.time).ToArray(), c.Clip, c.Layer, c.Order, c.Masked, c.Target);

        /// <summary>
        /// The descriptor's visemes, jaw flap, eyelids and native face tracking, which VRChat drives without a clip.
        /// </summary>
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
                        if (!string.IsNullOrEmpty(name)) yield return new DrivenKey(path, name, viseme.transform);
                if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && !string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName))
                    yield return new DrivenKey(path, descriptor.MouthOpenBlendShapeName, viseme.transform);
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
                    if (index >= 0 && index < mesh.blendShapeCount) yield return new DrivenKey(path, mesh.GetBlendShapeName(index), eyelids.transform);
            }

#if VRC_FT_SETS_PRESENT
            // Face tracking drives a mapped name on every Face Tracking Mesh that has it. A set counts at any weight
            // and whether or not face tracking is enabled, as the eyelids do: a parameter can raise a set at runtime.
            if (descriptor.faceTrackingMeshes == null) yield break;
            var names = new HashSet<string>();
            if (descriptor.faceTrackingBlendshapesSetList != null)
                foreach (var set in descriptor.faceTrackingBlendshapesSetList)
                {
                    if (set == null) continue;
                    if (set.faceTrackingBlendshapeNames != null) names.UnionWith(set.faceTrackingBlendshapeNames);
                    if (set.visemeBlendshapeNames != null) names.UnionWith(set.visemeBlendshapeNames);
                }
            if (descriptor.faceTrackingParameterBehaviors != null)
                foreach (var behavior in descriptor.faceTrackingParameterBehaviors)
                    if (behavior != null && (behavior.targetMode == VRCAvatarDescriptor.ParameterBehaviorTargetMode.ApplyToBlendshape
                        || behavior.targetMode == VRCAvatarDescriptor.ParameterBehaviorTargetMode.LimitBlendshapeMax
                        || behavior.targetMode == VRCAvatarDescriptor.ParameterBehaviorTargetMode.LimitBlendshapeMin))
                        names.Add(behavior.targetBlendshapeName);
            names.RemoveWhere(string.IsNullOrEmpty);
            foreach (var face in descriptor.faceTrackingMeshes)
            {
                if (face == null) continue;
                var path = PathOf(root, face.transform);
                foreach (var name in names) yield return new DrivenKey(path, name, face.transform);
            }
#endif
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
                if (MmdMorphs.Names.Contains(name)) yield return new DrivenKey("Body", name, body);
            }
        }

        static bool IsShapeKey(EditorCurveBinding binding) =>
            binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith(BlendShapePrefix);

        static string PathOf(Transform root, Transform target) => RuntimeUtil.RelativePath(root, target) ?? target.name;
    }
}
