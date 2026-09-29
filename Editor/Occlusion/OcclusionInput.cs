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
        /// One renderer as a re-judge reads it: the triangles it removed, at their authored corners, as candidates only
        /// (<see cref="OcclusionMesh.Candidate"/>), and the triangles it draws as blockers only
        /// (<see cref="OcclusionMesh.Blocks"/>), over its evaluation mesh's own vertices, with the materials and part the
        /// first solve gave it. Each drawn triangle sits beside the evaluation triangle it came from, so a renderer drawn
        /// as occlusion left it is entered in the first solve's own order and every vertex faces the way it did then.
        /// <paramref name="removed"/> null removes nothing. <paramref name="source"/> is, per triangle entered, the
        /// evaluation triangle it is or came from. Its arrays are added to <paramref name="owned"/>.
        /// </summary>
        public static OcclusionMesh Rejudged(EvalMesh eval, bool mirrored, bool occludes, OccluderSubmesh[] submeshes,
            bool[] removed, int3[] drawn, int[] drawnSource, List<IDisposable> owned, out int[] source)
        {
            var n = eval.TriangleCount;
            // The drawn triangles, grouped by the evaluation triangle each came from, in their own order within a group.
            var start = new int[n + 1];
            foreach (var s in drawnSource) start[s + 1]++;
            for (var t = 0; t < n; t++) start[t + 1] += start[t];
            var order = new int[drawnSource.Length];
            var fill = (int[])start.Clone();
            for (var i = 0; i < drawnSource.Length; i++) order[fill[drawnSource[i]]++] = i;

            var count = drawn.Length;
            if (removed != null) foreach (var r in removed) if (r) count++;
            var triangles = new NativeArray<int3>(count, Allocator.Persistent);
            owned.Add(triangles);
            var submesh = new NativeArray<int>(count, Allocator.Persistent);
            owned.Add(submesh);
            var candidate = new bool[count];
            var blocks = new bool[count];
            source = new int[count];
            var e = 0;
            for (var t = 0; t < n; t++)
            {
                if (removed != null && removed[t])
                {
                    triangles[e] = eval.Triangles[t];
                    submesh[e] = eval.TriangleSubmesh[t];
                    candidate[e] = true;
                    source[e++] = t;
                }
                for (var k = start[t]; k < start[t + 1]; k++)
                {
                    triangles[e] = drawn[order[k]];
                    submesh[e] = eval.TriangleSubmesh[t];
                    blocks[e] = true;
                    source[e++] = t;
                }
            }
            // As in Mesh: Unity draws a mirrored renderer with its winding reversed.
            if (mirrored)
                for (var i = 0; i < count; i++) triangles[i] = triangles[i].xzy;
            eval.Triangles = triangles;
            eval.TriangleSubmesh = submesh;
            return new OcclusionMesh { Mesh = eval, Occludes = occludes, Submeshes = submeshes, Candidate = candidate, Blocks = blocks };
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
