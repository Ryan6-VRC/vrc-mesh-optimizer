using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ryan6Vrc.MeshOptimizer.Writing
{
    /// <summary>
    /// Builds an optimized mesh from its source by keeping a subset of the source's vertices, copied unchanged.
    /// Every vertex attribute keeps its source format, every influence and bind pose is kept, and every frame of
    /// every shape key is kept under its name, in its order, at its frame weight, dead keys included, so anything
    /// that addresses a shape key by index still finds it.
    /// </summary>
    internal static class MeshWriter
    {
        /// <summary>
        /// Writes the mesh a list of triangles leaves: each triangle into its submesh, in list order; every submesh that
        /// is not triangles passed through whole, as <see cref="Write(Mesh, IReadOnlyList{int[]}, int[])"/> does; and
        /// every source vertex any of them uses, ascending, in <paramref name="keptVertices"/>.
        /// </summary>
        /// <param name="triangles">Surviving triangles, each corner naming a source vertex.</param>
        /// <param name="submesh">Per triangle, the source submesh it is drawn in, one of triangles.</param>
        public static Mesh Write(Mesh source, IReadOnlyList<int3> triangles, IReadOnlyList<int> submesh, out int[] keptVertices)
        {
            var perSubmesh = new List<int>[source.subMeshCount];
            for (var s = 0; s < perSubmesh.Length; s++) perSubmesh[s] = new List<int>();
            for (var i = 0; i < triangles.Count; i++)
            {
                var list = perSubmesh[submesh[i]];
                list.Add(triangles[i].x);
                list.Add(triangles[i].y);
                list.Add(triangles[i].z);
            }
            var indices = new int[source.subMeshCount][];
            var used = new bool[source.vertexCount];
            for (var s = 0; s < indices.Length; s++)
            {
                indices[s] = MeshTriangles.IsTriangles(source, s) ? perSubmesh[s].ToArray() : source.GetIndices(s, true);
                foreach (var i in indices[s]) used[i] = true;
            }
            var kept = new List<int>();
            for (var v = 0; v < used.Length; v++) if (used[v]) kept.Add(v);
            keptVertices = kept.ToArray();
            return Write(source, indices, keptVertices);
        }

        /// <param name="source">The mesh the renderer draws now.</param>
        /// <param name="submeshIndices">
        /// Per source submesh, its surviving indices in its own topology, naming source vertices. A submesh that is not
        /// triangles is passed through with its source indices.
        /// </param>
        /// <param name="keptVertices">Source vertex indices to keep, ascending. Output vertex i is source vertex keptVertices[i].</param>
        public static Mesh Write(Mesh source, IReadOnlyList<int[]> submeshIndices, int[] keptVertices)
        {
            var remap = Remap(source.vertexCount, keptVertices);
            if (submeshIndices.Count != source.subMeshCount)
                throw new ArgumentException($"{submeshIndices.Count} submeshes given for a mesh with {source.subMeshCount}", nameof(submeshIndices));

            var count = keptVertices.Length;
            var mesh = new Mesh
            {
                name = source.name,
                indexFormat = count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            try
            {
                CopyVertexData(source, mesh, keptVertices);
                CopySkin(source, mesh, keptVertices);
                mesh.subMeshCount = submeshIndices.Count;
                for (var s = 0; s < submeshIndices.Count; s++)
                {
                    var indices = submeshIndices[s].Select(i => Remapped(remap, i, s)).ToArray();
                    mesh.SetIndices(indices, source.GetTopology(s), s, true);
                }
                CopyShapeKeys(source, mesh, keptVertices);
                // Each submesh's bounds are its own vertices'; the mesh keeps the source's, which may have been
                // widened on purpose and always contain the kept vertices.
                mesh.bounds = source.bounds;
                return mesh;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }

        static int[] Remap(int vertexCount, int[] kept)
        {
            var remap = new int[vertexCount];
            for (var i = 0; i < remap.Length; i++) remap[i] = -1;
            for (var i = 0; i < kept.Length; i++)
            {
                var v = kept[i];
                if (v < 0 || v >= vertexCount || (i > 0 && v <= kept[i - 1]))
                    throw new ArgumentException($"kept vertices must ascend within 0..{vertexCount - 1}; entry {i} is {v}");
                remap[v] = i;
            }
            return remap;
        }

        static int Remapped(int[] remap, int index, int submesh)
        {
            var v = index >= 0 && index < remap.Length ? remap[index] : -1;
            if (v < 0) throw new ArgumentException($"submesh {submesh} names vertex {index}, which is not kept");
            return v;
        }

        /// <summary>
        /// Copies each vertex attribute byte for byte in its source format. Skin weights are left to
        /// <see cref="CopySkin"/>, because a vertex can carry more influences than the vertex buffer holds.
        /// </summary>
        static void CopyVertexData(Mesh source, Mesh mesh, int[] kept)
        {
            var attributes = source.GetVertexAttributes()
                .Where(a => a.attribute != VertexAttribute.BlendWeight && a.attribute != VertexAttribute.BlendIndices)
                .ToArray();
            mesh.SetVertexBufferParams(kept.Length, attributes);

            using var sourceData = Mesh.AcquireReadOnlyMeshData(source);
            var data = sourceData[0];
            foreach (var stream in attributes.Select(a => a.stream).Distinct())
            {
                var from = data.GetVertexData<byte>(stream);
                var fromStride = source.GetVertexBufferStride(stream);
                var toStride = mesh.GetVertexBufferStride(stream);
                using var to = new NativeArray<byte>(kept.Length * toStride, Allocator.Temp);
                foreach (var a in attributes.Where(a => a.stream == stream))
                {
                    var size = FormatSize(a.format) * a.dimension;
                    var fromOffset = source.GetVertexAttributeOffset(a.attribute);
                    var toOffset = mesh.GetVertexAttributeOffset(a.attribute);
                    for (var i = 0; i < kept.Length; i++)
                        NativeArray<byte>.Copy(from, kept[i] * fromStride + fromOffset, to, i * toStride + toOffset, size);
                }
                mesh.SetVertexBufferData(to, 0, 0, to.Length, stream, MeshUpdateFlags.DontRecalculateBounds);
            }
        }

        static void CopySkin(Mesh source, Mesh mesh, int[] kept)
        {
            mesh.bindposes = source.bindposes;
            var bonesPerVertex = source.GetBonesPerVertex();
            if (bonesPerVertex.Length == 0) return;
            var weights = source.GetAllBoneWeights();

            var start = new int[bonesPerVertex.Length + 1];
            for (var v = 0; v < bonesPerVertex.Length; v++) start[v + 1] = start[v] + bonesPerVertex[v];

            var total = 0;
            foreach (var v in kept) total += bonesPerVertex[v];
            var counts = new byte[kept.Length];
            for (var i = 0; i < kept.Length; i++) counts[i] = bonesPerVertex[kept[i]];
            using var keptCounts = new NativeArray<byte>(counts, Allocator.Temp);
            using var keptWeights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
            var cursor = 0;
            foreach (var v in kept)
            {
                NativeArray<BoneWeight1>.Copy(weights, start[v], keptWeights, cursor, bonesPerVertex[v]);
                cursor += bonesPerVertex[v];
            }
            mesh.SetBoneWeights(keptCounts, keptWeights);
        }

        static void CopyShapeKeys(Mesh source, Mesh mesh, int[] kept)
        {
            var shapeCount = source.blendShapeCount;
            if (shapeCount == 0) return;
            var n = source.vertexCount;
            var positions = new Vector3[n];
            var normals = new Vector3[n];
            var tangents = new Vector3[n];
            var keptPositions = new Vector3[kept.Length];
            var keptNormals = new Vector3[kept.Length];
            var keptTangents = new Vector3[kept.Length];
            for (var s = 0; s < shapeCount; s++)
            {
                var name = source.GetBlendShapeName(s);
                var frames = source.GetBlendShapeFrameCount(s);
                for (var f = 0; f < frames; f++)
                {
                    source.GetBlendShapeFrameVertices(s, f, positions, normals, tangents);
                    for (var i = 0; i < kept.Length; i++)
                    {
                        keptPositions[i] = positions[kept[i]];
                        keptNormals[i] = normals[kept[i]];
                        keptTangents[i] = tangents[kept[i]];
                    }
                    mesh.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(s, f), keptPositions, keptNormals, keptTangents);
                }
            }
        }

        static int FormatSize(VertexAttributeFormat format) => format switch
        {
            VertexAttributeFormat.Float32 => 4,
            VertexAttributeFormat.UInt32 => 4,
            VertexAttributeFormat.SInt32 => 4,
            VertexAttributeFormat.Float16 => 2,
            VertexAttributeFormat.UNorm16 => 2,
            VertexAttributeFormat.SNorm16 => 2,
            VertexAttributeFormat.UInt16 => 2,
            VertexAttributeFormat.SInt16 => 2,
            _ => 1,
        };
    }
}
