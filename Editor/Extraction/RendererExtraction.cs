using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>Reads a live renderer's mesh and pose for <see cref="MeshExtractor"/>.</summary>
    internal static class RendererExtraction
    {
        /// <summary>The mesh a renderer draws: a skinned renderer's shared mesh, or its MeshFilter's.</summary>
        public static Mesh MeshOf(Renderer renderer) => renderer switch
        {
            SkinnedMeshRenderer skinned => skinned.sharedMesh,
            MeshRenderer _ when renderer.TryGetComponent<MeshFilter>(out var filter) => filter.sharedMesh,
            _ => null,
        };

        /// <summary>
        /// Whether Unity draws the renderer with its winding reversed, as it does for a transform that mirrors. A
        /// MeshRenderer's own transform decides; a SkinnedMeshRenderer's root bone decides, or its own transform when
        /// it has none, and not the bones: a mirroring bone mirrors the skinned faces without reversing the winding.
        /// </summary>
        public static bool DrawsMirrored(Renderer renderer)
        {
            var drawnBy = renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null ? skinned.rootBone : renderer.transform;
            return drawnBy.localToWorldMatrix.determinant < 0f;
        }

        /// <summary>
        /// Avatar-root space: the root's position and rotation, in metres. The root's own scale is left out so an
        /// error measured in it is a distance in the world.
        /// </summary>
        public static Matrix4x4 WorldToRoot(Transform root) => Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse;

        /// <summary>
        /// Builds the evaluation mesh for a SkinnedMeshRenderer or a MeshRenderer, at the pose the avatar stands in.
        /// <paramref name="liveness"/> is ignored for a MeshRenderer, which never applies shape keys.
        /// </summary>
        public static EvalMesh Extract(Renderer renderer, AvatarBones bones, IReadOnlyList<ShapeKeyLiveness> liveness,
            Allocator allocator, out LiveFrame[] liveFrames, IReadOnlyList<float> heldWeights = null)
        {
            var mesh = MeshOf(renderer);
            if (mesh == null) throw new ArgumentException($"{renderer.name} draws no mesh", nameof(renderer));

            var worldToRoot = WorldToRoot(bones.Root);
            var rendererToRoot = worldToRoot * renderer.transform.localToWorldMatrix;
            var rendererId = bones.Id(renderer.transform);

            if (!(renderer is SkinnedMeshRenderer skinned))
                return MeshExtractor.Extract(mesh, SkinningPose.Unskinned(rendererToRoot, rendererId), null, allocator, out liveFrames);

            // A missing bone leaves its slot on the renderer's own transform.
            var slots = skinned.bones;
            var boneToRoot = new Matrix4x4[slots.Length];
            var ids = new int[slots.Length];
            for (var b = 0; b < slots.Length; b++)
            {
                var bone = slots[b] != null ? slots[b] : renderer.transform;
                boneToRoot[b] = worldToRoot * bone.localToWorldMatrix;
                ids[b] = bones.Id(bone);
            }
            var pose = new SkinningPose(boneToRoot, ids, rendererToRoot, rendererId);
            return MeshExtractor.Extract(mesh, pose, liveness, allocator, out liveFrames, heldWeights);
        }
    }
}
