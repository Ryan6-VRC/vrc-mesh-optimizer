using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    /// <summary>One renderer as a gate channel takes it: the renderer, its mesh before and after the optimizer, and the plain facts about its keys.</summary>
    internal sealed class GateInput
    {
        public readonly Renderer Renderer;
        public readonly Mesh Source;
        public readonly Mesh Optimized;
        /// <summary>Optional, one per source triangle in submesh order: true for a triangle removed as hidden.</summary>
        public readonly bool[] RemovedTriangles;
        /// <summary>One entry per shape key of <see cref="Source"/>; may be null only for a mesh without shape keys, or for a MeshRenderer.</summary>
        public readonly IReadOnlyList<ShapeKeyLiveness> Liveness;

        public GateInput(Renderer renderer, Mesh source, Mesh optimized, bool[] removedTriangles, IReadOnlyList<ShapeKeyLiveness> liveness)
        {
            Renderer = renderer;
            Source = source;
            Optimized = optimized;
            RemovedTriangles = removedTriangles;
            Liveness = liveness;
        }

        public SkinnedMeshRenderer Skinned => Renderer as SkinnedMeshRenderer;

        /// <summary>Refuses, naming the fix, anything the gate cannot measure; reads only, so it runs before anything moves.</summary>
        public void Check(Transform root)
        {
            if (Renderer == null) throw new ArgumentNullException("renderer");
            if (Source == null) throw new ArgumentNullException("source", $"'{Renderer.name}' has no source mesh");
            if (Optimized == null) throw new ArgumentNullException("optimized", $"'{Renderer.name}' has no optimized mesh");
            if (!Renderer.transform.IsChildOf(root))
                throw new ArgumentException($"'{Renderer.name}' is not under the avatar root '{root.name}'", "renderer");
            var skinned = Skinned;
            if (skinned == null && !(Renderer is MeshRenderer))
                throw new ArgumentException($"'{Renderer.name}' is a {Renderer.GetType().Name}; the gate measures a SkinnedMeshRenderer or a MeshRenderer", "renderer");
            if (skinned != null)
            {
                if (Optimized.blendShapeCount != Source.blendShapeCount)
                    throw new ArgumentException($"the optimized mesh has {Optimized.blendShapeCount} shape keys and the source {Source.blendShapeCount}; an optimized mesh keeps every one", "optimized");
                if (Optimized.bindposes.Length != Source.bindposes.Length)
                    throw new ArgumentException($"the optimized mesh has {Optimized.bindposes.Length} bind poses and the source {Source.bindposes.Length}; both must skin to the renderer's bones", "optimized");
                var holds = skinned.sharedMesh;
                if (holds != null && holds != Source && holds != Optimized && !SameKeys(holds, Source))
                    throw new ArgumentException(
                        $"'{Renderer.name}' holds '{holds.name}', whose shape keys are not the source's by name and index, so its weights cannot say how the avatar stands; hand the gate the renderer holding the source or the optimized mesh",
                        "renderer");
                var keys = Liveness?.Count ?? 0;
                if (keys != Source.blendShapeCount)
                    throw new ArgumentException($"liveness has {keys} entries for a source mesh with {Source.blendShapeCount} shape keys; pass the renderer's liveness table", "liveness");
                // The gate measures the state the optimizer evaluated as rest, so the renderer must hold the weights liveness recorded.
                if (holds != null)
                    for (var k = 0; k < keys; k++)
                    {
                        var weight = skinned.GetBlendShapeWeight(k);
                        if (Math.Abs(weight - Liveness[k].HeldWeight) > ShapeKeyClassifier.HoldTolerance)
                            throw new ArgumentException(
                                $"'{Renderer.name}' holds shape key '{Source.GetBlendShapeName(k)}' at {weight} and its liveness records {Liveness[k].HeldWeight}, so the avatar as it stands is not the state the optimizer evaluated; pass the liveness classified from the renderer as it stands now",
                                "liveness");
                    }
            }
            if (RemovedTriangles != null)
            {
                var triangles = GeometryGate.ReadTriangles(Source).Length;
                if (RemovedTriangles.Length != triangles)
                    throw new ArgumentException($"{RemovedTriangles.Length} removed-triangle flags for a source mesh with {triangles} triangles", "removedTriangles");
            }
        }

        static bool SameKeys(Mesh a, Mesh b)
        {
            if (a.blendShapeCount != b.blendShapeCount) return false;
            for (var k = 0; k < a.blendShapeCount; k++)
                if (a.GetBlendShapeName(k) != b.GetBlendShapeName(k)) return false;
            return true;
        }

        /// <summary>
        /// The avatar as it stands, the state the optimizer evaluates as rest: every key at the weight liveness records it
        /// held at, which <see cref="Check"/> has confirmed the renderer holds. <paramref name="liveOffZero"/> says whether
        /// any live key is held off zero, which is when <see cref="LiveKeysAtZero"/> is a different state.
        /// </summary>
        public float[] StandingWeights(out bool liveOffZero)
        {
            var weights = LiveKeysAtZero();
            liveOffZero = false;
            for (var k = 0; k < weights.Length; k++)
            {
                if (Liveness[k].State != ShapeKeyState.Live) continue;
                weights[k] = Liveness[k].HeldWeight;
                liveOffZero |= weights[k] != 0f;
            }
            return weights;
        }

        /// <summary>Frozen keys at their held weights, every other key at zero: the avatar with everything a live key does undone.</summary>
        public float[] LiveKeysAtZero()
        {
            if (Skinned == null) return Array.Empty<float>();
            var weights = new float[Source.blendShapeCount];
            for (var k = 0; k < weights.Length; k++)
                if (Liveness[k].State == ShapeKeyState.Frozen) weights[k] = Liveness[k].HeldWeight;
            return weights;
        }

        /// <summary>
        /// Where <paramref name="mesh"/> stands on this renderer in avatar-root space: a skinned renderer is handed the mesh
        /// and the weights and baked by Unity; a MeshRenderer's vertices ride its transform.
        /// </summary>
        public NativeArray<float3> Bake(Mesh mesh, float[] weights, Mesh scratch, Transform root)
        {
            // A mesh the pass emptied has no surface to place; baking it would leave the scratch mesh as it was.
            if (mesh.vertexCount == 0) return new NativeArray<float3>(0, Allocator.Persistent);
            var skinned = Skinned;
            Vector3[] vertices;
            if (skinned != null)
            {
                skinned.sharedMesh = mesh;
                for (var k = 0; k < weights.Length; k++) skinned.SetBlendShapeWeight(k, weights[k]);
                skinned.BakeMesh(scratch, true);
                if (scratch.vertexCount != mesh.vertexCount)
                    throw new InvalidOperationException($"baking '{mesh.name}' on '{Renderer.name}' gave {scratch.vertexCount} vertices for a mesh with {mesh.vertexCount}");
                vertices = scratch.vertices;
            }
            else vertices = mesh.vertices;
            // With its scale, a bake is in the renderer's local space, so its local-to-world matrix places it once.
            var toRoot = RendererExtraction.WorldToRoot(root) * Renderer.transform.localToWorldMatrix;
            var result = new NativeArray<float3>(vertices.Length, Allocator.Persistent);
            for (var v = 0; v < vertices.Length; v++) result[v] = toRoot.MultiplyPoint3x4(vertices[v]);
            return result;
        }
    }

    internal static class GatePoses
    {
        /// <summary>
        /// The poses that can apply to the avatar, each checked before anything moves; a muscle pose on an avatar with no
        /// humanoid Avatar goes to <paramref name="skipped"/> with why. <paramref name="humanoid"/> is the Animator to pose
        /// through, or null when no usable pose needs one.
        /// </summary>
        public static List<GatePose> Usable(Transform root, IReadOnlyList<GatePose> poses, List<SkippedConfiguration> skipped, out Animator humanoid)
        {
            var animator = root.GetComponent<Animator>();
            var isHuman = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;
            var usable = new List<GatePose>();
            humanoid = null;
            foreach (var pose in poses ?? Array.Empty<GatePose>())
            {
                if (pose.NeedsHumanoid && !isHuman)
                {
                    skipped.Add(new SkippedConfiguration($"pose '{pose.Name}'",
                        $"it drives humanoid muscles and '{root.name}' has no Animator with a valid humanoid Avatar; pass a BoneRotationPose for this rig"));
                    continue;
                }
                pose.Check(root);
                if (pose.NeedsHumanoid) humanoid = animator;
                usable.Add(pose);
            }
            return usable;
        }
    }

    /// <summary>What a gate changes on the avatar, as it was found: every transform under the root, and each renderer's mesh and weights.</summary>
    internal sealed class AvatarState
    {
        readonly Transform[] _transforms;
        readonly Vector3[] _positions;
        readonly Quaternion[] _rotations;
        readonly Vector3[] _scales;
        readonly SkinnedMeshRenderer[] _renderers;
        readonly Mesh[] _meshes;
        readonly float[][] _weights;

        public AvatarState(Transform root, IEnumerable<SkinnedMeshRenderer> renderers)
        {
            _transforms = root.GetComponentsInChildren<Transform>(true);
            _positions = new Vector3[_transforms.Length];
            _rotations = new Quaternion[_transforms.Length];
            _scales = new Vector3[_transforms.Length];
            for (var i = 0; i < _transforms.Length; i++)
            {
                _positions[i] = _transforms[i].localPosition;
                _rotations[i] = _transforms[i].localRotation;
                _scales[i] = _transforms[i].localScale;
            }
            _renderers = renderers.Where(r => r != null).Distinct().ToArray();
            _meshes = _renderers.Select(r => r.sharedMesh).ToArray();
            _weights = new float[_renderers.Length][];
            for (var r = 0; r < _renderers.Length; r++)
            {
                _weights[r] = new float[_meshes[r] != null ? _meshes[r].blendShapeCount : 0];
                for (var k = 0; k < _weights[r].Length; k++) _weights[r][k] = _renderers[r].GetBlendShapeWeight(k);
            }
        }

        /// <summary>Writes back only what differs, bit for bit, so an untouched transform is not written at all.</summary>
        public void RestoreTransforms()
        {
            for (var i = 0; i < _transforms.Length; i++)
            {
                var t = _transforms[i];
                if (t == null) continue;
                if (!t.localPosition.Equals(_positions[i])) t.localPosition = _positions[i];
                if (!t.localRotation.Equals(_rotations[i])) t.localRotation = _rotations[i];
                if (!t.localScale.Equals(_scales[i])) t.localScale = _scales[i];
            }
        }

        /// <summary>Transforms first, so they come back even when a renderer is gone; a destroyed renderer is passed over.</summary>
        public void Restore()
        {
            RestoreTransforms();
            for (var r = 0; r < _renderers.Length; r++)
            {
                var renderer = _renderers[r];
                if (renderer == null) continue;
                if (renderer.sharedMesh != _meshes[r]) renderer.sharedMesh = _meshes[r];
                var weights = _weights[r];
                for (var k = 0; k < weights.Length; k++)
                    if (!renderer.GetBlendShapeWeight(k).Equals(weights[k])) renderer.SetBlendShapeWeight(k, weights[k]);
            }
        }
    }
}
