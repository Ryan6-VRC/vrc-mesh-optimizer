using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.MeshOptimizer.Animators
{
    /// <summary>
    /// One float curve in a clip, with the Transform its binding path resolves to, the clip it is in, and the layer that
    /// plays it. A clip two layers play is read once per layer.
    /// </summary>
    internal readonly struct AnimatedCurve
    {
        public readonly Transform Target;
        public readonly EditorCurveBinding Binding;
        public readonly AnimationCurve Curve;
        /// <summary>The clip, by identity: NDMF's <see cref="VirtualClip"/> for a controller, the <see cref="AnimationClip"/> for a legacy Animation.</summary>
        public readonly object Clip;
        /// <summary>The layer, by identity: NDMF's <see cref="VirtualLayer"/>, or the legacy Animation component.</summary>
        public readonly object Layer;
        /// <summary>Where the layer plays among all the avatar's layers, later overriding earlier; null when that is not known.</summary>
        public readonly long? Order;
        /// <summary>An avatar mask on the layer may keep the curve from its target.</summary>
        public readonly bool Masked;

        public AnimatedCurve(Transform target, EditorCurveBinding binding, AnimationCurve curve, object clip, object layer, long? order, bool masked)
        {
            Masked = masked;
            Target = target;
            Binding = binding;
            Curve = curve;
            Clip = clip;
            Layer = layer;
            Order = order;
        }
    }

    /// <summary>
    /// Every float curve anything on the avatar can play, resolved to the object it animates. By the Optimizing
    /// phase Modular Avatar and VRCFury have merged their animators into the descriptor's playable layers, so
    /// those layers, the child Animators and the legacy Animation components are every clip there is.
    /// </summary>
    internal static class AnimatedCurves
    {
        /// <summary>Layers per playable layer in <see cref="PlayOrder"/>: far more than any controller holds.</summary>
        const long LayersPerPlayable = 1_000_000;

        /// <summary>
        /// The values a curve takes: its keys, and samples between them, because a curve's tangents can carry it
        /// past both of the keys it runs between.
        /// </summary>
        public static float[] CurveValues(AnimationCurve curve, int samplesPerSegment = 8)
        {
            var keys = curve.keys;
            if (keys.Length == 0) return Array.Empty<float>();
            var values = new List<float>(keys.Length * (samplesPerSegment + 1));
            for (var i = 0; i < keys.Length; i++)
            {
                values.Add(keys[i].value);
                if (i + 1 == keys.Length) break;
                var t0 = keys[i].time;
                var t1 = keys[i + 1].time;
                for (var s = 1; s < samplesPerSegment; s++)
                    values.Add(curve.Evaluate(Mathf.Lerp(t0, t1, s / (float)samplesPerSegment)));
            }
            return values.ToArray();
        }

        /// <summary>
        /// Where a controller's layer plays among the avatar's layers. VRChat plays the descriptor's playable layers in the
        /// order Base, Additive, Gesture, Action, FX, and each controller's layers in index order, a later layer overriding
        /// an earlier one. A special layer (Sitting, TPose, IKPose), a child Animator and anything else runs outside that
        /// order, so its place is not known: null.
        /// </summary>
        public static long? PlayOrder(object controllerKey, int layerIndex)
        {
            if (!(controllerKey is VRCAvatarDescriptor.AnimLayerType type)) return null;
            long rank;
            switch (type)
            {
                case VRCAvatarDescriptor.AnimLayerType.Base: rank = 0; break;
                case VRCAvatarDescriptor.AnimLayerType.Additive: rank = 1; break;
                case VRCAvatarDescriptor.AnimLayerType.Gesture: rank = 2; break;
                case VRCAvatarDescriptor.AnimLayerType.Action: rank = 3; break;
                case VRCAvatarDescriptor.AnimLayerType.FX: rank = 4; break;
                default: return null;
            }
            return rank * LayersPerPlayable + layerIndex;
        }

        /// <summary>
        /// Curves in every controller NDMF holds: the descriptor's playable layers and every Animator's controller, layer by
        /// layer. A synced layer plays its source layer's states, so the clips it does not override count as its own too.
        /// A descriptor layer's paths are virtual paths from the avatar root; a child Animator's are relative to that
        /// Animator. A path that resolves to nothing is dropped.
        /// </summary>
        public static IEnumerable<AnimatedCurve> FromAnimatorServices(AnimatorServicesContext services, Transform root,
            Func<EditorCurveBinding, bool> wanted)
        {
            var remapper = services.ObjectPathRemapper;
            foreach (var pair in services.ControllerContext.Controllers)
            {
                if (pair.Value == null) continue;
                var owner = pair.Key is Animator animator && animator != null ? animator.transform : root;
                var layers = pair.Value.Layers.ToList();
                var descriptorMask = DescriptorMask(root, pair.Key);
                for (var index = 0; index < layers.Count; index++)
                {
                    var layer = layers[index];
                    var clips = layer.AllReachableNodes().OfType<VirtualClip>();
                    // SyncedLayerIndex is a virtual layer index; a source that cannot be found could be any layer.
                    if (layer.SyncedLayerIndex >= 0)
                    {
                        var source = layers.FirstOrDefault(l => l.VirtualLayerIndex == layer.SyncedLayerIndex);
                        clips = clips.Concat((source != null ? new List<VirtualLayer> { source } : layers).SelectMany(l => l.AllReachableNodes().OfType<VirtualClip>()));
                    }
                    // A layer's own mask; for Gesture and FX, VRChat applies the first layer's mask to the whole playable.
                    var masks = new List<Func<string, bool>> { Admits(layer.AvatarMask), descriptorMask };
                    if (index > 0 && pair.Key is VRCAvatarDescriptor.AnimLayerType t &&
                        (t == VRCAvatarDescriptor.AnimLayerType.Gesture || t == VRCAvatarDescriptor.AnimLayerType.FX))
                        masks.Add(Admits(layers[0].AvatarMask));
                    var order = PlayOrder(pair.Key, index);
                    foreach (var clip in clips.Distinct())
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (!wanted(binding)) continue;
                        var target = owner == root
                            ? (remapper.GetObjectForPath(binding.path)?.transform ?? Resolve(root, binding.path))
                            : Resolve(owner, binding.path);
                        if (target == null) continue;
                        var curve = clip.GetFloatCurve(binding);
                        if (curve == null) continue;
                        var masked = masks.Any(admits => !admits(binding.path));
                        yield return new AnimatedCurve(target, binding, curve, clip, layer, order, masked);
                    }
                }
            }
        }

        /// <summary>
        /// Whether a mask lets a curve at <paramref name="path"/> through: a mask with no transforms listed masks only humanoid
        /// muscles; one that lists transforms admits only a path it lists fully weighted.
        /// </summary>
        static Func<string, bool> Admits(VirtualAvatarMask mask)
        {
            if (mask == null || mask.Elements.Count == 0) return _ => true;
            var elements = mask.Elements;
            return path => elements.TryGetValue(path, out var weight) && weight >= 1f;
        }

        /// <summary>The mask the descriptor sets on a playable layer, read the same way.</summary>
        static Func<string, bool> DescriptorMask(Transform root, object controllerKey)
        {
            if (!(controllerKey is VRCAvatarDescriptor.AnimLayerType type) || !root.TryGetComponent<VRCAvatarDescriptor>(out var descriptor)) return _ => true;
            var mask = (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Where(l => l.type == type).Select(l => l.mask).FirstOrDefault(m => m != null);
            if (mask == null || mask.transformCount == 0) return _ => true;
            var active = new HashSet<string>();
            for (var i = 0; i < mask.transformCount; i++)
                if (mask.GetTransformActive(i)) active.Add(mask.GetTransformPath(i));
            return active.Contains;
        }

        /// <summary>
        /// Legacy Animation components, which NDMF does not virtualize. Paths are relative to the component, and the
        /// component is the layer, playing outside the animators' order.
        /// </summary>
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
                yield return new AnimatedCurve(target, binding, curve, clip, animation, null, false);
            }
        }

        /// <summary>A path relative to a component's transform; the empty path is that transform itself.</summary>
        static Transform Resolve(Transform owner, string path) => path.Length == 0 ? owner : owner.Find(path);
    }
}
