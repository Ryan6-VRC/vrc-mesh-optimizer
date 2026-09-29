using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    /// <summary>A humanoid skeleton in T-pose under the fixture's root, with an Animator whose Avatar maps it.</summary>
    internal sealed class Humanoid
    {
        public readonly Dictionary<HumanBodyBones, Transform> Bones = new Dictionary<HumanBodyBones, Transform>();

        static readonly (HumanBodyBones bone, HumanBodyBones parent, Vector3 local)[] Layout =
        {
            (HumanBodyBones.Hips, HumanBodyBones.LastBone, new Vector3(0, 1, 0)),
            (HumanBodyBones.Spine, HumanBodyBones.Hips, new Vector3(0, 0.1f, 0)),
            (HumanBodyBones.Chest, HumanBodyBones.Spine, new Vector3(0, 0.15f, 0)),
            (HumanBodyBones.Neck, HumanBodyBones.Chest, new Vector3(0, 0.2f, 0)),
            (HumanBodyBones.Head, HumanBodyBones.Neck, new Vector3(0, 0.1f, 0)),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.Hips, new Vector3(-0.1f, -0.05f, 0)),
            (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftUpperLeg, new Vector3(0, -0.45f, 0)),
            (HumanBodyBones.LeftFoot, HumanBodyBones.LeftLowerLeg, new Vector3(0, -0.45f, 0)),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips, new Vector3(0.1f, -0.05f, 0)),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg, new Vector3(0, -0.45f, 0)),
            (HumanBodyBones.RightFoot, HumanBodyBones.RightLowerLeg, new Vector3(0, -0.45f, 0)),
            (HumanBodyBones.LeftUpperArm, HumanBodyBones.Chest, new Vector3(-0.2f, 0.15f, 0)),
            (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftUpperArm, new Vector3(-0.3f, 0, 0)),
            (HumanBodyBones.LeftHand, HumanBodyBones.LeftLowerArm, new Vector3(-0.25f, 0, 0)),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.Chest, new Vector3(0.2f, 0.15f, 0)),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm, new Vector3(0.3f, 0, 0)),
            (HumanBodyBones.RightHand, HumanBodyBones.RightLowerArm, new Vector3(0.25f, 0, 0)),
            (HumanBodyBones.RightIndexProximal, HumanBodyBones.RightHand, new Vector3(0.08f, 0, 0.02f)),
            (HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexProximal, new Vector3(0.03f, 0, 0)),
            (HumanBodyBones.RightIndexDistal, HumanBodyBones.RightIndexIntermediate, new Vector3(0.02f, 0, 0)),
        };

        public Humanoid(AvatarFixture a)
        {
            var armature = a.Bone("Armature");
            foreach (var (bone, parent, local) in Layout)
            {
                var t = new GameObject(bone.ToString()).transform;
                t.SetParent(parent == HumanBodyBones.LastBone ? armature : Bones[parent], false);
                t.localPosition = local;
                Bones[bone] = t;
            }
            var avatar = a.Made(BuildAvatar(a.Root, Bones));
            Assert.That(avatar.isValid && avatar.isHuman, Is.True, "the fixture skeleton must build a humanoid avatar");
            if (!a.Root.TryGetComponent<Animator>(out var animator)) animator = a.Root.AddComponent<Animator>();
            animator.avatar = avatar;
        }

        /// <summary>An Avatar mapping <paramref name="bones"/> under <paramref name="root"/> as they stand, with default limits.</summary>
        internal static Avatar BuildAvatar(GameObject root, IReadOnlyDictionary<HumanBodyBones, Transform> bones)
        {
            var description = new HumanDescription
            {
                human = bones.Select(kv => new HumanBone
                {
                    humanName = HumanTrait.BoneName[(int)kv.Key],
                    boneName = kv.Value.name,
                    limit = new HumanLimit { useDefaultValues = true },
                }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                }).ToArray(),
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f,
            };
            return AvatarBuilder.BuildHumanAvatar(root, description);
        }
    }
}
