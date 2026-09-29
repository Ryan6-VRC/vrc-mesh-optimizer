using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>What the occlusion solver is handed for an avatar, from its renderers and bones.</summary>
    internal static class OcclusionInput
    {
        /// <summary>
        /// One renderer as occlusion reads it: its evaluation mesh as Unity draws it, whether it covers others, and per
        /// submesh the sides it draws and, for an occluder, how it blocks (<see cref="OccluderMaterials.SidesDrawn"/>).
        /// A mesh it has to rewind is allocated here and added to <paramref name="owned"/>, which the caller disposes
        /// after the solve.
        /// </summary>
        public static OcclusionMesh Mesh(Renderer renderer, int submeshCount, EvalMesh eval, bool mirrored, bool occludes,
            OccluderMaterials materials, bool outlines, List<IDisposable> owned)
        {
            if (mirrored)
            {
                // Occlusion reads a face's front by Unity's winding in avatar-root space; Unity draws a
                // mirrored renderer with its winding reversed, so occlusion sees it reversed too.
                var flipped = new NativeArray<int3>(eval.Triangles.Length, Allocator.Persistent);
                owned.Add(flipped);
                for (var t = 0; t < flipped.Length; t++) flipped[t] = eval.Triangles[t].xzy;
                eval.Triangles = flipped;
            }
            return new OcclusionMesh
            {
                Mesh = eval,
                Occludes = occludes,
                Submeshes = OccluderMaterials.SidesDrawn(renderer, submeshCount, occludes ? materials.For(renderer, submeshCount) : null, outlines),
            };
        }

        /// <summary>
        /// The avatar's bone table, with the occluder side of <see cref="UnstableBones"/>: a face on an unstable bone never
        /// covers. Its arrays are added to <paramref name="owned"/>.
        /// </summary>
        public static OcclusionBones Bones(AvatarBones bones, bool[] unstable, List<IDisposable> owned)
        {
            var table = new OcclusionBones
            {
                Parents = bones.ParentArray(Allocator.Persistent),
                Vanishes = new NativeArray<bool>(unstable, Allocator.Persistent),
            };
            owned.Add(table.Parents);
            owned.Add(table.Vanishes);
            return table;
        }
    }
}
