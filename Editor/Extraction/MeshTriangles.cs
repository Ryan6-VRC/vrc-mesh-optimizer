using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>
    /// The one reading of a mesh's triangles: every triangle submesh in submesh order, each index buffer three indices at a
    /// time, a trailing partial triangle left out; a submesh of lines or points holds none. Every per-triangle array passed
    /// between stages (the evaluation mesh's triangles, hidden flags, the gate's exclusion mask, the report's hidden source
    /// triangles) is numbered in this order, so every stage that numbers a mesh's triangles reads them here.
    /// </summary>
    internal static class MeshTriangles
    {
        /// <summary>Whether submesh <paramref name="s"/> holds triangles; any other topology passes through the pass untouched.</summary>
        public static bool IsTriangles(Mesh mesh, int s) => mesh.GetTopology(s) == MeshTopology.Triangles;

        /// <summary>The mesh's triangles, and per triangle the submesh it is drawn in.</summary>
        public static (int3[] triangles, int[] submesh) Read(Mesh mesh)
        {
            var triangles = new List<int3>();
            var submesh = new List<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (!IsTriangles(mesh, s)) continue;
                var indices = mesh.GetIndices(s, true);
                var count = indices.Length / 3;
                for (var t = 0; t < count; t++)
                {
                    triangles.Add(new int3(indices[3 * t], indices[3 * t + 1], indices[3 * t + 2]));
                    submesh.Add(s);
                }
            }
            return (triangles.ToArray(), submesh.ToArray());
        }

        /// <summary>
        /// How many triangles <see cref="Read"/> returns, taken from the index counts alone, which a mesh whose data cannot
        /// be read still gives.
        /// </summary>
        public static int Count(Mesh mesh)
        {
            long count = 0;
            for (var s = 0; s < mesh.subMeshCount; s++)
                if (IsTriangles(mesh, s)) count += mesh.GetIndexCount(s) / 3;
            return (int)count;
        }
    }
}
