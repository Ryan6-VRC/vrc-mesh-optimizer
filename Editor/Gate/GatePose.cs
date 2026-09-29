using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    /// <summary>
    /// A posed configuration the geometric gate bakes both meshes in. A pose only measures: nothing the optimizer
    /// deletes or collapses depends on one.
    /// </summary>
    internal abstract class GatePose
    {
        public readonly string Name;

        protected GatePose(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("a pose needs a name", nameof(name));
            Name = name;
        }

        /// <summary>Whether the pose drives Unity's humanoid muscle space, which needs a humanoid Avatar on the avatar root's Animator.</summary>
        internal abstract bool NeedsHumanoid { get; }

        /// <summary>Throws, naming what is missing, when the pose cannot apply to the avatar under <paramref name="root"/>. Runs before anything moves.</summary>
        internal virtual void Check(Transform root) { }

        /// <summary>Moves bones from wherever they stand. The gate puts every transform back afterwards.</summary>
        internal abstract void Apply(Transform root, HumanPoseHandler humanoid);
    }

    /// <summary>
    /// Sets named humanoid muscles, each from -1 to 1 across the avatar's own limits, and leaves every other muscle
    /// where the avatar stands. Muscle space is normalized per avatar, so one pose bends any humanoid alike.
    /// <see cref="HumanPoseHandler"/> writes the bones directly, in edit mode, without play mode or an Animator
    /// evaluating.
    /// </summary>
    internal sealed class MusclePose : GatePose
    {
        readonly int[] _muscles;
        readonly float[] _values;

        public MusclePose(string name, params (string muscle, float value)[] muscles) : base(name)
        {
            var names = HumanTrait.MuscleName;
            _muscles = new int[muscles.Length];
            _values = new float[muscles.Length];
            for (var i = 0; i < muscles.Length; i++)
            {
                var index = Array.IndexOf(names, muscles[i].muscle);
                if (index < 0) throw new ArgumentException($"pose '{name}': Unity has no humanoid muscle named '{muscles[i].muscle}'; HumanTrait.MuscleName lists them");
                if (!(muscles[i].value >= -1f && muscles[i].value <= 1f))
                    throw new ArgumentException($"pose '{name}': muscle '{muscles[i].muscle}' is set to {muscles[i].value}, outside -1 to 1");
                _muscles[i] = index;
                _values[i] = muscles[i].value;
            }
        }

        internal override bool NeedsHumanoid => true;

        internal override void Apply(Transform root, HumanPoseHandler humanoid)
        {
            var pose = new HumanPose();
            humanoid.GetHumanPose(ref pose);
            for (var i = 0; i < _muscles.Length; i++) pose.muscles[_muscles[i]] = _values[i];
            humanoid.SetHumanPose(ref pose);
        }
    }

    /// <summary>
    /// Turns named bones by a rotation in their parent's frame, on top of where they stand. Works on any rig; bones are
    /// named by their path from the avatar root, as <see cref="Transform.Find(string)"/> takes it.
    /// </summary>
    internal sealed class BoneRotationPose : GatePose
    {
        readonly (string path, Quaternion rotation)[] _bones;

        public BoneRotationPose(string name, params (string path, Quaternion rotation)[] bones) : base(name) => _bones = bones;

        internal override bool NeedsHumanoid => false;

        internal override void Check(Transform root)
        {
            foreach (var (path, _) in _bones)
                if (root.Find(path) == null) throw new ArgumentException($"pose '{Name}' turns '{path}', which is not a path under '{root.name}'");
        }

        internal override void Apply(Transform root, HumanPoseHandler humanoid)
        {
            foreach (var (path, rotation) in _bones)
            {
                var bone = root.Find(path);
                bone.localRotation *= rotation;
            }
        }
    }

    internal static class PoseBank
    {
        /// <summary>
        /// Bends each joint where decimation damage shows: elbows, knees, shoulders up and down, hips, wrists, fingers
        /// into fists, and the neck. Most go to the end of the avatar's own range. Elbows and knees stop at about 100
        /// degrees and hips at a seated 90 (on Unity's default limits), because the end of their range folds the limb
        /// onto itself, where the error sits inside a crush no one sees.
        /// <para>
        /// This bank is a held-out test set. The decimator and the occlusion pass optimize against no pose at all, and
        /// that must stay true of this bank: a pose the optimizer tuned against would stop testing it.
        /// </para>
        /// </summary>
        public static IReadOnlyList<GatePose> Default { get; } = Build();

        static IReadOnlyList<GatePose> Build()
        {
            var fists = new List<(string, float)>();
            foreach (var side in new[] { "Left", "Right" })
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            for (var joint = 1; joint <= 3; joint++)
                fists.Add(($"{side} {finger} {joint} Stretched", -1f));

            return new GatePose[]
            {
                new MusclePose("elbows-bent", Both("Forearm Stretch", -0.25f)),
                new MusclePose("knees-bent", Both("Lower Leg Stretch", -0.25f)),
                new MusclePose("arms-up", Join(Both("Arm Down-Up", 1f), Both("Shoulder Down-Up", 1f))),
                new MusclePose("arms-down", Both("Arm Down-Up", -1f)),
                new MusclePose("hips-flexed", Both("Upper Leg Front-Back", -0.7f)),
                new MusclePose("wrists-bent", Both("Hand Down-Up", -1f)),
                new MusclePose("fists", fists.ToArray()),
                new MusclePose("neck-turned", ("Neck Nod Down-Up", -1f), ("Neck Turn Left-Right", 1f), ("Head Turn Left-Right", 1f)),
            };
        }

        static (string, float)[] Both(string muscle, float value) => new[] { ($"Left {muscle}", value), ($"Right {muscle}", value) };

        static (string, float)[] Join(params (string, float)[][] parts)
        {
            var all = new List<(string, float)>();
            foreach (var p in parts) all.AddRange(p);
            return all.ToArray();
        }
    }
}
