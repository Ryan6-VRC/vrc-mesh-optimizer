using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Collections;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.MeshOptimizer.Decimation
{
    /// <summary>What decimation must keep on an avatar, found from the avatar's own descriptor and humanoid mapping.</summary>
    internal sealed class Protection
    {
        /// <summary>The share of its triangles the face renderer keeps whatever the target (<see cref="AvatarMeshOptimizer.faceFloor"/>).</summary>
        readonly float _faceFloor;

        /// <summary>
        /// A vertex with at least this much weight on a finger is a finger vertex (<see cref="AvatarMeshOptimizer.fingerWeight"/>);
        /// infinite when the fingers are not locked, so no weight reaches it.
        /// </summary>
        readonly float _fingerWeight;

        static readonly HumanBodyBones[] FingerRoots =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal,
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal,
        };

        readonly bool[] _finger;
        readonly float[] _growth;

        /// <summary>The renderer the descriptor names for visemes, or null.</summary>
        public Renderer Face { get; }

        /// <param name="faceFloor">The face's share, clamped to 0 to 1 (<see cref="AvatarMeshOptimizer.faceFloor"/>).</param>
        /// <param name="lockFingers">Whether finger vertices are locked at all (<see cref="AvatarMeshOptimizer.lockFingers"/>).</param>
        /// <param name="fingerWeight">The finger weight that locks a vertex, raised to 0.001 at least (<see cref="AvatarMeshOptimizer.fingerWeight"/>).</param>
        /// <param name="scaleCurves">Scale curves any clip can play, for <see cref="UnstableBones.Growth"/>; null reads as none.</param>
        public Protection(Transform root, AvatarBones bones, float faceFloor, bool lockFingers, float fingerWeight, IEnumerable<ScaleCurve> scaleCurves = null)
        {
            _faceFloor = Mathf.Clamp01(faceFloor);
            _fingerWeight = lockFingers ? Mathf.Max(fingerWeight, 0.001f) : float.PositiveInfinity;
            _growth = UnstableBones.Growth(bones, scaleCurves);
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
        }

        /// <summary>The fewest triangles <paramref name="renderer"/> may be cut to, out of <paramref name="triangles"/>.</summary>
        public int FloorFor(Renderer renderer, int triangles) =>
            renderer != null && renderer == Face ? Mathf.CeilToInt(triangles * _faceFloor) : 0;

        /// <summary>
        /// What the decimator keeps of one renderer's vertices: every vertex <see cref="Locks"/> locks, every vertex of a
        /// triangle drawn with fur that occlusion kept (<paramref name="hidden"/>, or null for none removed), and the
        /// costs <see cref="Multipliers"/> raises. Its arrays are added to <paramref name="owned"/>. Of the
        /// <paramref name="locked"/> vertices, <paramref name="fromZeroScale"/> ride a bone grown from zero and
        /// <paramref name="forFur"/> were locked for fur; <paramref name="grown"/> vertices had their cost raised.
        /// </summary>
        public VertexProtection For(Renderer renderer, int submeshCount, in EvalMesh eval, bool[] hidden, List<IDisposable> owned,
            out int locked, out int fromZeroScale, out int forFur, out int grown)
        {
            var locks = Locks(eval, out var lockedCount, out fromZeroScale);
            // Fur is grown per triangle, so its density is the triangle count: every vertex of a furred triangle is locked.
            var fur = MaterialFur.Submeshes(renderer, submeshCount);
            var lockedForFur = 0;
            for (var t = 0; t < eval.TriangleCount; t++)
            {
                if ((hidden != null && hidden[t]) || !fur[eval.TriangleSubmesh[t]]) continue;
                var tri = eval.Triangles[t];
                for (var c = 0; c < 3; c++)
                {
                    locks ??= new byte[eval.VertexCount];
                    if (locks[tri[c]] != 0) continue;
                    locks[tri[c]] = 1;
                    lockedCount++;
                    lockedForFur++;
                }
            }
            var vertexProtection = new VertexProtection();
            if (locks != null)
            {
                vertexProtection.Locked = new NativeArray<byte>(locks, Allocator.Persistent);
                owned.Add(vertexProtection.Locked);
            }
            var multipliers = Multipliers(eval, out grown);
            if (multipliers != null)
            {
                vertexProtection.CostMultiplier = new NativeArray<float>(multipliers, Allocator.Persistent);
                owned.Add(vertexProtection.CostMultiplier);
            }
            locked = lockedCount;
            forFur = lockedForFur;
            return vertexProtection;
        }

        /// <summary>
        /// Per vertex, nonzero for a locked vertex, which no collapse removes: one with at least the finger weight on the
        /// fingers, or any weight on a bone that stands at zero scale and a clip grows (<see cref="UnstableBones.Growth"/>). Geometry there has no size
        /// as the avatar stands, so every collapse on it reads as free, and no factor makes that error what is shown; however
        /// small the weight, the vertex moves with the bone. Null when the mesh has none. <paramref name="count"/> is every
        /// locked vertex, <paramref name="fromZeroScale"/> those locked for a bone grown from zero.
        /// </summary>
        public byte[] Locks(in EvalMesh mesh, out int count, out int fromZeroScale)
        {
            count = fromZeroScale = 0;
            byte[] locks = null;
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var onFinger = 0f;
                var fromZero = float.IsPositiveInfinity(GrowthAt(mesh, v));
                for (var k = mesh.WeightStart[v]; k < mesh.WeightStart[v + 1]; k++)
                {
                    var bone = mesh.Weights[k].Bone;
                    if (bone < _finger.Length && _finger[bone]) onFinger += mesh.Weights[k].Weight;
                }
                if (onFinger < _fingerWeight && !fromZero) continue;
                locks ??= new byte[mesh.VertexCount];
                locks[v] = 1;
                count++;
                if (fromZero) fromZeroScale++;
            }
            return locks;
        }

        /// <summary>
        /// Per vertex, the factor the error of a collapse removing it or landing on it is multiplied by: the most a clip can grow any bone it has weight on
        /// (<see cref="UnstableBones.Growth"/>). Decimation judges the avatar as it stands, and an error on geometry a clip
        /// grows by a factor is shown that factor larger, so the cost is charged at the size the geometry can be shown at.
        /// Null when nothing on the mesh grows. <paramref name="count"/> is how many vertices it raises; a vertex grown from
        /// zero is locked instead (<see cref="Locks"/>) and not counted here.
        /// </summary>
        public float[] Multipliers(in EvalMesh mesh, out int count)
        {
            count = 0;
            float[] result = null;
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var growth = GrowthAt(mesh, v);
                if (!(growth > 1f) || float.IsPositiveInfinity(growth)) continue;
                if (result == null)
                {
                    result = new float[mesh.VertexCount];
                    for (var i = 0; i < result.Length; i++) result[i] = 1f;
                }
                result[v] = growth;
                count++;
            }
            return result;
        }

        /// <summary>The most any bone the vertex has weight on can grow.</summary>
        float GrowthAt(in EvalMesh mesh, int v)
        {
            var growth = 1f;
            for (var k = mesh.WeightStart[v]; k < mesh.WeightStart[v + 1]; k++)
            {
                var bone = mesh.Weights[k].Bone;
                if (mesh.Weights[k].Weight > 0f && bone < _growth.Length) growth = Mathf.Max(growth, _growth[bone]);
            }
            return growth;
        }
    }
}
