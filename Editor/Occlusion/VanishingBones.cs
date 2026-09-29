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
    /// Which bones an animation can collapse, so that a face riding one never counts as cover: the renderer stays
    /// on while the face goes, and whatever it covered shows. Each source below only adds flags, so reading the
    /// animators here can stop a face from covering but can never make one cover.
    /// </summary>
    internal static class VanishingBones
    {
        /// <summary>
        /// Modular Avatar's conditional mesh deletion moves the deletable vertices onto new bones with this prefix and
        /// animates their scale to NaN (<c>NaNimationFilter.NaNimatedBonePrefix</c> in Modular Avatar).
        /// </summary>
        public const string ModularAvatarDeletionBonePrefix = "NaNimatedBone for ";

        /// <summary>A curve value this far under the bone's rest scale, as a fraction of it, shrinks the bone.</summary>
        public const float ShrinkTolerance = 1e-3f;

        /// <summary>
        /// Per avatar bone id, true when the bone or any ancestor can vanish: by name, or because a clip can shrink,
        /// collapse, mirror or NaN its scale. A scaled bone carries everything under it, so the flag runs down the tree.
        /// </summary>
        public static bool[] Compute(AvatarBones bones, IEnumerable<ScaleCurve> scaleCurves)
        {
            var scaled = new List<int>();
            if (scaleCurves != null)
                foreach (var curve in scaleCurves)
                    if (curve.Target != null && Collapses(curve.Target.localScale[curve.Axis], curve.Values))
                        scaled.Add(bones.Id(curve.Target));
            // Numbering a curve's target can add a Transform outside the root, so the table is sized after.
            var flags = new bool[bones.Count];
            for (var b = 0; b < bones.Count; b++) flags[b] = ByName(bones.Transforms[b]);
            foreach (var b in scaled) flags[b] = true;
            return bones.CarryDown(flags);
        }

        public static bool ByName(Transform bone) => bone != null && bone.name.StartsWith(ModularAvatarDeletionBonePrefix);

        /// <summary>
        /// A scale curve can make faces on its bone uncover what they covered at rest when it takes the axis to a
        /// non-finite value, to or through zero, or below its rest magnitude: each of those pulls the faces inward,
        /// mirrors them or removes them. Growing only moves them outward, with everything that rides the bone.
        /// </summary>
        public static bool Collapses(float rest, float[] values)
        {
            if (values == null) return false;
            foreach (var v in values)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) return true;
                if (rest == 0f || v == 0f || Mathf.Sign(v) != Mathf.Sign(rest)) return true;
                if (Mathf.Abs(v) < Mathf.Abs(rest) * (1f - ShrinkTolerance)) return true;
            }
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
