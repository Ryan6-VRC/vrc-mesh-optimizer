using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Ryan6Vrc.MeshOptimizer.Animators;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
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
        /// Per avatar bone id, true when a clip can grow the bone or any ancestor above its rest scale on any axis: what
        /// decimation must not judge as the avatar stands, since geometry on such a bone is smaller there than it can be
        /// shown, and at a rest scale of zero has no size at all, so every collapse on it would read as free. A scaled bone
        /// carries everything under it, so the flag runs down the tree. Unlike <see cref="Compute"/>, a bone that clips only
        /// shrink, and Modular Avatar's deletion bones, are not flagged: they are shown at their rest scale or not at all.
        /// </summary>
        public static bool[] Growing(AvatarBones bones, IEnumerable<ScaleCurve> scaleCurves)
        {
            var grown = new List<int>();
            if (scaleCurves != null)
                foreach (var curve in scaleCurves)
                    if (curve.Target != null && Grows(curve.Target.localScale[curve.Axis], curve.Values))
                        grown.Add(bones.Id(curve.Target));
            // Numbering a curve's target can add a Transform outside the root, so the table is sized after.
            var flags = new bool[bones.Count];
            foreach (var b in grown) flags[b] = true;
            return bones.CarryDown(flags);
        }

        /// <summary>
        /// A scale curve grows its bone when it can take the axis's magnitude above its rest magnitude by more than the
        /// tolerance, which any nonzero value does from a rest of zero. A mirroring sign alone grows nothing, nor does NaN,
        /// which hides the bone's geometry rather than showing it larger; infinity grows it.
        /// </summary>
        public static bool Grows(float rest, float[] values)
        {
            if (values == null) return false;
            var magnitude = Mathf.Abs(rest);
            var limit = magnitude + Mathf.Max(magnitude * ScaleTolerance, 1e-6f);
            foreach (var v in values)
                if (Mathf.Abs(v) > limit) return true;
            return false;
        }

        static bool IsScale(EditorCurveBinding binding) =>
            binding.type == typeof(Transform) && binding.propertyName.StartsWith("m_LocalScale.");

        static IEnumerable<ScaleCurve> ScaleCurves(IEnumerable<AnimatedCurve> curves) =>
            curves.Select(c => new ScaleCurve(c.Target, c.Binding.propertyName[c.Binding.propertyName.Length - 1] - 'x',
                ShapeKeyClassifier.CurveValues(c.Curve)))
                .Where(c => c.Axis >= 0 && c.Axis <= 2);

        /// <summary>Scale curves in every controller NDMF holds; see <see cref="AnimatedCurves.FromAnimatorServices"/>.</summary>
        public static IEnumerable<ScaleCurve> FromAnimatorServices(AnimatorServicesContext services, Transform root) =>
            ScaleCurves(AnimatedCurves.FromAnimatorServices(services, root, IsScale));

        /// <summary>Scale curves in legacy Animation components.</summary>
        public static IEnumerable<ScaleCurve> FromLegacyAnimations(Transform root) =>
            ScaleCurves(AnimatedCurves.FromLegacyAnimations(root, IsScale));
    }
}
