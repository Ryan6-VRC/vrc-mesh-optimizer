using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Writing;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ryan6Vrc.MeshOptimizer.Tests.Writing
{
    // The written mesh replaces the original on the avatar, so everything the renderer, the animator and the
    // descriptor read from it must survive: attributes in their formats, every influence, and every shape key
    // under the index it had, since eyelid settings name keys by index.
    public class MeshWriterTests
    {
        readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void DestroyMade()
        {
            foreach (var o in _made) Object.DestroyImmediate(o);
            _made.Clear();
        }

        T Made<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Stream0
        {
            public Vector3 Position;
            public Vector3 Normal;
            public half4 Tangent;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Stream1
        {
            public Color32 Color;
            public Vector2 Uv0;
            public half2 Uv1;
        }

        static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float16, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2, 1),
        };

        const int Bones = 7;

        /// <summary>
        /// Twelve vertices in two streams with half-precision and byte attributes; vertex v has v % 7 + 1 influences,
        /// up to seven; two triangle submeshes over the even vertices and one line submesh; a two-frame key, a key
        /// that moves nothing, and a key with unnormalized delta normals and tangents.
        /// </summary>
        Mesh RichMesh()
        {
            const int n = 12;
            var mesh = Made(new Mesh { name = "rich" });
            mesh.SetVertexBufferParams(n, Layout);
            var s0 = new Stream0[n];
            var s1 = new Stream1[n];
            for (var v = 0; v < n; v++)
            {
                s0[v] = new Stream0
                {
                    Position = new Vector3(v, v * 0.5f, -v),
                    Normal = new Vector3(0, v % 2, 1 - v % 2),
                    Tangent = new half4(new float4(1, 0, 0, v % 2 == 0 ? 1 : -1)),
                };
                s1[v] = new Stream1
                {
                    Color = new Color32((byte)(v * 20), (byte)(255 - v), 7, 200),
                    Uv0 = new Vector2(v / 12f, 1 - v / 12f),
                    Uv1 = new half2(new float2(v * 0.25f, 0.125f)),
                };
            }
            mesh.SetVertexBufferData(s0, 0, 0, n, 0);
            mesh.SetVertexBufferData(s1, 0, 0, n, 1);

            var perVertex = new byte[n];
            var weights = new List<BoneWeight1>();
            for (var v = 0; v < n; v++)
            {
                var count = v % Bones + 1;
                perVertex[v] = (byte)count;
                // Descending weights summing to one.
                var total = count * (count + 1) / 2f;
                for (var i = 0; i < count; i++)
                    weights.Add(new BoneWeight1 { boneIndex = (v + i) % Bones, weight = (count - i) / total });
            }
            using (var p = new NativeArray<byte>(perVertex, Allocator.Temp))
            using (var w = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(p, w);
            mesh.bindposes = Enumerable.Range(0, Bones).Select(b => Matrix4x4.Translate(new Vector3(0, -b, 0))).ToArray();

            mesh.subMeshCount = 3;
            mesh.SetIndices(new[] { 0, 2, 4, 4, 2, 6 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 6, 8, 10 }, MeshTopology.Triangles, 1);
            mesh.SetIndices(new[] { 1, 3, 3, 5 }, MeshTopology.Lines, 2);

            mesh.AddBlendShapeFrame("grow", 50f, Deltas(n, v => new Vector3(v, 0, 0)), null, null);
            mesh.AddBlendShapeFrame("grow", 100f, Deltas(n, v => new Vector3(2 * v, 0, 0)), null, null);
            mesh.AddBlendShapeFrame("unused", 100f, new Vector3[n], null, null);
            mesh.AddBlendShapeFrame("bend", 100f, Deltas(n, v => new Vector3(0, v % 3, 0)),
                Deltas(n, v => new Vector3(0, 0, v * 0.3f)), Deltas(n, v => new Vector3(v * 2f, 0, 0)));
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector3[] Deltas(int n, System.Func<int, Vector3> f) => Enumerable.Range(0, n).Select(f).ToArray();

        static int[][] SourceIndices(Mesh mesh) => Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetIndices(s)).ToArray();

        static VertexAttributeDescriptor[] NonSkinAttributes(Mesh mesh) => mesh.GetVertexAttributes()
            .Where(a => a.attribute != VertexAttribute.BlendWeight && a.attribute != VertexAttribute.BlendIndices).ToArray();

        /// <summary>One vertex's bytes for one attribute.</summary>
        static byte[] AttributeBytes(Mesh mesh, VertexAttributeDescriptor a, int vertex)
        {
            using var data = Mesh.AcquireReadOnlyMeshData(mesh);
            var bytes = data[0].GetVertexData<byte>(a.stream);
            var stride = mesh.GetVertexBufferStride(a.stream);
            var offset = mesh.GetVertexAttributeOffset(a.attribute);
            var size = a.format switch
            {
                VertexAttributeFormat.Float32 => 4,
                VertexAttributeFormat.Float16 => 2,
                _ => 1,
            } * a.dimension;
            return bytes.GetSubArray(vertex * stride + offset, size).ToArray();
        }

        static List<BoneWeight1> InfluencesOf(Mesh mesh, int vertex)
        {
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var start = 0;
            for (var v = 0; v < vertex; v++) start += perVertex[v];
            return Enumerable.Range(start, perVertex[vertex]).Select(i => weights[i]).ToList();
        }

        static void AssertSameVertex(Mesh source, int from, Mesh written, int to)
        {
            foreach (var a in NonSkinAttributes(source))
                Assert.That(AttributeBytes(written, a, to), Is.EqualTo(AttributeBytes(source, a, from)), $"{a.attribute} of vertex {from}");
            Assert.That(InfluencesOf(written, to), Is.EqualTo(InfluencesOf(source, from)), $"influences of vertex {from}");
        }

        static void AssertSameShapeKeys(Mesh source, Mesh written, int[] kept)
        {
            Assert.That(written.blendShapeCount, Is.EqualTo(source.blendShapeCount));
            var fromP = new Vector3[source.vertexCount];
            var fromN = new Vector3[source.vertexCount];
            var fromT = new Vector3[source.vertexCount];
            var toP = new Vector3[written.vertexCount];
            var toN = new Vector3[written.vertexCount];
            var toT = new Vector3[written.vertexCount];
            for (var s = 0; s < source.blendShapeCount; s++)
            {
                Assert.That(written.GetBlendShapeName(s), Is.EqualTo(source.GetBlendShapeName(s)), $"name of key {s}");
                Assert.That(written.GetBlendShapeFrameCount(s), Is.EqualTo(source.GetBlendShapeFrameCount(s)), $"frames of key {s}");
                for (var f = 0; f < source.GetBlendShapeFrameCount(s); f++)
                {
                    Assert.That(written.GetBlendShapeFrameWeight(s, f), Is.EqualTo(source.GetBlendShapeFrameWeight(s, f)));
                    source.GetBlendShapeFrameVertices(s, f, fromP, fromN, fromT);
                    written.GetBlendShapeFrameVertices(s, f, toP, toN, toT);
                    for (var i = 0; i < kept.Length; i++)
                    {
                        var what = $"key {s} frame {f} vertex {kept[i]}";
                        Assert.That(toP[i], Is.EqualTo(fromP[kept[i]]), what);
                        Assert.That(toN[i], Is.EqualTo(fromN[kept[i]]), what);
                        Assert.That(toT[i], Is.EqualTo(fromT[kept[i]]), what);
                    }
                }
            }
        }

        [Test]
        public void KeepingEveryVertex_RoundTripsEveryAttributeInfluenceAndShapeKeyFrame()
        {
            var source = RichMesh();
            var all = Enumerable.Range(0, source.vertexCount).ToArray();

            var written = Made(MeshWriter.Write(source, SourceIndices(source), all));

            Assert.That(written.name, Is.EqualTo(source.name));
            Assert.That(written.vertexCount, Is.EqualTo(source.vertexCount));
            Assert.That(NonSkinAttributes(written), Is.EqualTo(NonSkinAttributes(source)), "attribute formats and streams");
            for (var v = 0; v < source.vertexCount; v++) AssertSameVertex(source, v, written, v);
            Assert.That(written.GetBonesPerVertex().ToArray().Max(), Is.EqualTo(Bones), "a vertex with more than four influences");
            Assert.That(written.bindposes, Is.EqualTo(source.bindposes));
            Assert.That(written.subMeshCount, Is.EqualTo(source.subMeshCount));
            for (var s = 0; s < source.subMeshCount; s++)
            {
                Assert.That(written.GetTopology(s), Is.EqualTo(source.GetTopology(s)), $"topology of submesh {s}");
                Assert.That(written.GetIndices(s), Is.EqualTo(source.GetIndices(s)), $"indices of submesh {s}");
            }
            AssertSameShapeKeys(source, written, all);
            Assert.That(written.bounds, Is.EqualTo(source.bounds));
        }

        [Test]
        public void KeepingHalfTheVertices_KeepsTheirDataExactly_AndEveryShapeKeyInOrder()
        {
            var source = RichMesh();
            var even = Enumerable.Range(0, source.vertexCount).Where(v => v % 2 == 0).ToArray();
            // The first triangle submesh loses its second triangle; the line submesh is passed through, so its odd
            // vertices would have to be kept too, and is emptied here instead.
            var indices = new[] { new[] { 0, 2, 4 }, new[] { 6, 8, 10 }, new int[0] };

            var written = Made(MeshWriter.Write(source, indices, even));

            Assert.That(written.vertexCount, Is.EqualTo(even.Length));
            for (var i = 0; i < even.Length; i++) AssertSameVertex(source, even[i], written, i);
            Assert.That(written.GetIndices(0), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(written.GetIndices(1), Is.EqualTo(new[] { 3, 4, 5 }));
            Assert.That(written.GetIndices(2), Is.Empty);
            Assert.That(written.GetTopology(2), Is.EqualTo(MeshTopology.Lines));
            AssertSameShapeKeys(source, written, even);
        }

        [Test]
        public void AnIndexToAVertexNotKept_IsRefused()
        {
            var source = RichMesh();
            var indices = new[] { new[] { 0, 1, 2 }, new int[0], new int[0] };

            Assert.That(() => MeshWriter.Write(source, indices, new[] { 0, 2 }), Throws.ArgumentException);
        }

        [Test]
        public void MeshShrinkingUnder65536Vertices_Uses16BitIndices_AndDrawsTheSameTriangles()
        {
            // 70,002 vertices, one triangle per three; keep every other triangle.
            const int n = 70002;
            var source = Made(new Mesh { indexFormat = IndexFormat.UInt32 });
            source.vertices = Enumerable.Range(0, n).Select(v => new Vector3(v % 3, v / 3, v % 7)).ToArray();
            source.triangles = Enumerable.Range(0, n).ToArray();
            var keptTriangles = Enumerable.Range(0, n / 3).Where(t => t % 2 == 0).ToArray();
            var kept = keptTriangles.SelectMany(t => new[] { 3 * t, 3 * t + 1, 3 * t + 2 }).ToArray();

            var written = Made(MeshWriter.Write(source, new[] { kept }, kept));

            Assert.That(written.vertexCount, Is.LessThanOrEqualTo(65535));
            Assert.That(written.indexFormat, Is.EqualTo(IndexFormat.UInt16));
            var fromPositions = source.vertices;
            var toPositions = written.vertices;
            var toIndices = written.GetIndices(0);
            Assert.That(toIndices.Length, Is.EqualTo(kept.Length));
            for (var i = 0; i < kept.Length; i++)
                Assert.That(toPositions[toIndices[i]], Is.EqualTo(fromPositions[kept[i]]), $"corner {i}");
        }

        [Test]
        public void MeshThatIsNotReadable_IsStillWritten()
        {
            var source = RichMesh();
            var expected = Made(Object.Instantiate(source));
            source.UploadMeshData(true);
            Assert.That(source.isReadable, Is.False, "the fixture must be a mesh that is not readable");
            var all = Enumerable.Range(0, 12).ToArray();

            var written = Made(MeshWriter.Write(source, SourceIndices(expected), all));

            for (var v = 0; v < 12; v++) AssertSameVertex(expected, v, written, v);
            AssertSameShapeKeys(expected, written, all);
        }
    }
}
