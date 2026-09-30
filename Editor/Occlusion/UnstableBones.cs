using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Ryan6Vrc.MeshOptimizer.Animators;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Extraction;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>The values one axis of a Transform's local scale can take under some clip.</summary>
    internal readonly struct ScaleCurve
    {
        public readonly Transform Target;
        /// <summary>0, 1 or 2 for x, y or z.</summary>
        public readonly int Axis;
        public readonly float[] Values;

        public ScaleCurve(Transform target, int axis, float[] values)
        {
            Target = target;
            Axis = axis;
            Values = values;
        }
    }

    /// <summary>
    /// Bones whose geometry does not keep its place relative to its kin. The occlusion rule accepts cover from a face
    /// up to a few parent-or-child steps away, on the premise that geometry on neighbouring bones moves together; an
    /// animated scale breaks that in either direction, since kin do not scale together, and Modular Avatar's
    /// conditional deletion removes faces outright. So a face on an unstable bone never covers, and a triangle on one
    /// is never deleted as hidden. Each source below only adds flags, so reading the animators here can keep
    /// triangles but never delete one.
    /// </summary>
    internal static class UnstableBones
    {
        /// <summary>
        /// Modular Avatar's conditional mesh deletion moves the deletable vertices onto new bones with this prefix and
        /// animates their scale to NaN (<c>NaNimationFilter.NaNimatedBonePrefix</c> in Modular Avatar).
        /// </summary>
        public const string ModularAvatarDeletionBonePrefix = "NaNimatedBone for ";

        /// <summary>A curve value further than this from the bone's rest scale, as a fraction of it, moves the bone.</summary>
        public const float ScaleTolerance = 1e-3f;

        /// <summary>
        /// Per avatar bone id, true when the bone or any ancestor is unstable: by name, or because a clip can move its
        /// scale on any axis. A scaled bone carries everything under it, so the flag runs down the tree.
        /// </summary>
        public static bool[] Compute(AvatarBones bones, IEnumerable<ScaleCurve> scaleCurves)
        {
            var scaled = new List<int>();
            if (scaleCurves != null)
                foreach (var curve in scaleCurves)
                    if (curve.Target != null && Moves(curve.Target.localScale[curve.Axis], curve.Values))
                        scaled.Add(bones.Id(curve.Target));
            // Numbering a curve's target can add a Transform outside the root, so the table is sized after.
            var flags = new bool[bones.Count];
            for (var b = 0; b < bones.Count; b++) flags[b] = ByName(bones.Transforms[b]);
            foreach (var b in scaled) flags[b] = true;
            return bones.CarryDown(flags);
        }

        public static bool ByName(Transform bone) => bone != null && bone.name.StartsWith(ModularAvatarDeletionBonePrefix);

        /// <summary>
        /// The candidate side: a triangle with a corner on an unstable bone can move out from under its cover, so it
        /// is never deleted. This clears flags in <paramref name="hidden"/> after the solve and needs no second one, which
        /// is why Core does not know about it: every triangle the solve hides is covered by faces the solve keeps, and
        /// keeping more triangles takes no face away from anyone's cover. Any weight counts, as on the occluder side:
        /// however small, it moves the corner when the bone scales, and to NaN when the bone does. Returns how many
        /// triangles it kept.
        /// </summary>
        public static int Keep(in EvalMesh eval, bool[] hidden, bool[] unstable)
        {
            var kept = 0;
            for (var t = 0; t < hidden.Length; t++)
            {
                if (!hidden[t]) continue;
                var tri = eval.Triangles[t];
                for (var c = 0; c < 3 && hidden[t]; c++)
                    for (var k = eval.WeightStart[tri[c]]; k < eval.WeightStart[tri[c] + 1]; k++)
                    {
                        var w = eval.Weights[k];
                        if (w.Weight <= 0f || w.Bone >= unstable.Length || !unstable[w.Bone]) continue;
                        hidden[t] = false;
                        kept++;
                        break;
                    }
            }
            return kept;
        }

        /// <summary>
        /// A scale curve moves its bone when it can take the axis away from its rest value, either way, or to a
        /// non-finite value. A curve that only holds the rest value, as the defaults layers Modular Avatar and VRCFury
        /// write do, does not.
        /// </summary>
        public static bool Moves(float rest, float[] values)
        {
            if (values == null) return false;
            var tolerance = Mathf.Max(Mathf.Abs(rest) * ScaleTolerance, 1e-6f);
            foreach (var v in values)
                if (float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v - rest) > tolerance) return true;
            return false;
        }

        /// <summary>
        /// Per avatar bone id, the most a clip can grow the geometry on it, as a factor over its size as the avatar stands:
        /// the largest ratio any curve gives the bone's scale, on any axis, to its rest scale, times its parent's, since a
        /// scaled bone carries everything under it. 1 for a bone nothing grows; infinity for one that stands at zero scale
        /// and a clip shows. Decimation judges the avatar as it stands, where an error on grown geometry reads this many
        /// times smaller than it is shown. Unlike <see cref="Compute"/>, a bone that clips only shrink, and Modular Avatar's
        /// deletion bones, grow nothing: they are shown at their rest scale or not at all.
        /// </summary>
        public static float[] Growth(AvatarBones bones, IEnumerable<ScaleCurve> scaleCurves)
        {
            var grown = new List<(int bone, float ratio)>();
            if (scaleCurves != null)
                foreach (var curve in scaleCurves)
                {
                    if (curve.Target == null) continue;
                    var ratio = GrowthOf(curve.Target.localScale[curve.Axis], curve.Values);
                    if (ratio > 1f) grown.Add((bones.Id(curve.Target), ratio));
                }
            // Numbering a curve's target can add a Transform outside the root, so the table is sized after.
            var own = new float[bones.Count];
            for (var b = 0; b < own.Length; b++) own[b] = 1f;
            foreach (var (bone, ratio) in grown) own[bone] = Mathf.Max(own[bone], ratio);
            // Depth-first numbering puts every parent before its children.
            var result = new float[own.Length];
            for (var b = 0; b < own.Length; b++)
            {
                var parent = bones.Parents[b];
                result[b] = own[b] * (parent >= 0 && parent < b ? result[parent] : 1f);
            }
            return result;
        }

        /// <summary>
        /// How many times a scale curve can grow its bone's axis: its largest magnitude over the rest magnitude, or 1 when no
        /// value exceeds rest by more than the tolerance. Any value from a rest of zero grows it without bound. A mirroring
        /// sign alone grows nothing, nor does NaN, which hides the bone's geometry rather than showing it larger.
        /// </summary>
        public static float GrowthOf(float rest, float[] values)
        {
            if (values == null) return 1f;
            var magnitude = Mathf.Abs(rest);
            var limit = magnitude + Mathf.Max(magnitude * ScaleTolerance, 1e-6f);
            var largest = 0f;
            foreach (var v in values)
                if (Mathf.Abs(v) > limit) largest = Mathf.Max(largest, Mathf.Abs(v));
            if (largest == 0f) return 1f;
            return magnitude > 1e-6f ? largest / magnitude : float.PositiveInfinity;
        }

        static bool IsScale(EditorCurveBinding binding) =>
            binding.type == typeof(Transform) && binding.propertyName.StartsWith("m_LocalScale.");

        static IEnumerable<ScaleCurve> ScaleCurves(IEnumerable<AnimatedCurve> curves) =>
            curves.Select(c => new ScaleCurve(c.Target, c.Binding.propertyName[c.Binding.propertyName.Length - 1] - 'x',
                AnimatedCurves.CurveValues(c.Curve)))
                .Where(c => c.Axis >= 0 && c.Axis <= 2);

        /// <summary>Scale curves in every controller NDMF holds; see <see cref="AnimatedCurves.FromAnimatorServices"/>.</summary>
        public static IEnumerable<ScaleCurve> FromAnimatorServices(AnimatorServicesContext services, Transform root) =>
            ScaleCurves(AnimatedCurves.FromAnimatorServices(services, root, IsScale));

        /// <summary>Scale curves in controllers as authored, outside a build; see <see cref="AnimatedCurves.FromControllers"/>.</summary>
        public static IEnumerable<ScaleCurve> FromControllers(IEnumerable<AuthoredController> controllers) =>
            ScaleCurves(AnimatedCurves.FromControllers(controllers, IsScale));

        /// <summary>Scale curves in legacy Animation components.</summary>
        public static IEnumerable<ScaleCurve> FromLegacyAnimations(Transform root) =>
            ScaleCurves(AnimatedCurves.FromLegacyAnimations(root, IsScale));
    }
}
