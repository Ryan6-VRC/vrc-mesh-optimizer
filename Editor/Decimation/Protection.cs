using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Extraction;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.MeshOptimizer.Decimation
{
    /// <summary>What decimation must keep on an avatar, found from the avatar's own descriptor and humanoid mapping.</summary>
    internal sealed class Protection
    {
        /// <summary>
        /// The share of its triangles the face renderer keeps whatever the target: lip sync and expressions deform it
        /// most and it is the surface other players watch, so an aggressive budget is spent elsewhere first.
        /// </summary>
        public const float FaceFloor = 0.5f;

        /// <summary>A vertex with at least this much weight on a finger is a finger vertex.</summary>
        public const float FingerWeight = 0.01f;

        static readonly HumanBodyBones[] FingerRoots =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal,
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal,
        };

        readonly bool[] _finger;

        /// <summary>The renderer the descriptor names for visemes, or null.</summary>
        public Renderer Face { get; }

        /// <summary>How many avatar bones are fingers or ride one.</summary>
        public int FingerBoneCount { get; }

        public Protection(Transform root, AvatarBones bones)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            Face = descriptor != null ? descriptor.VisemeSkinnedMesh : null;

            // Fingers come from the humanoid mapping, not bone names, which vary by rig and language. Every bone
            // under a finger's first joint rides it: the later joints, tips and anything a ring hangs from.
            var roots = new List<int>();
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                foreach (var bone in FingerRoots)
                {
                    var t = animator.GetBoneTransform(bone);
                    if (t != null) roots.Add(bones.Id(t));
                }
            // Numbering a finger can add a Transform outside the root, so the table is sized after.
            var seeds = new bool[bones.Count];
            foreach (var b in roots) seeds[b] = true;
            _finger = bones.CarryDown(seeds);
            foreach (var f in _finger) if (f) FingerBoneCount++;
        }

        /// <summary>The fewest triangles <paramref name="renderer"/> may be cut to, out of <paramref name="triangles"/>.</summary>
        public int FloorFor(Renderer renderer, int triangles) =>
            renderer != null && renderer == Face ? Mathf.CeilToInt(triangles * FaceFloor) : 0;

        /// <summary>
        /// Per vertex, nonzero for a vertex with weight on a finger: locked, so no collapse removes it. Null when the
        /// mesh has none.
        /// </summary>
        public byte[] Locks(in EvalMesh mesh, out int count)
        {
            count = 0;
            byte[] locks = null;
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var onFinger = 0f;
                for (var k = mesh.WeightStart[v]; k < mesh.WeightStart[v + 1]; k++)
                {
                    var bone = mesh.Weights[k].Bone;
                    if (bone < _finger.Length && _finger[bone]) onFinger += mesh.Weights[k].Weight;
                }
                if (onFinger < FingerWeight) continue;
                locks ??= new byte[mesh.VertexCount];
                locks[v] = 1;
                count++;
            }
            return locks;
        }
    }
}
