using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Gate;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    // The hash stands in for a mesh when two builds are compared, so a change to any field it covers must move it, or two
    // different meshes would read as the same one.
    public class MeshContentTests
    {
        readonly List<Mesh> _made = new List<Mesh>();

        [TearDown]
        public void TearDown()
        {
            foreach (var m in _made) Object.DestroyImmediate(m);
            _made.Clear();
        }

        /// <summary>A quad and a line, with every field the hash covers set to something a change can be seen in.</summary>
        Mesh Make()
        {
            var m = new Mesh { name = "hashed" };
            _made.Add(m);
            m.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f) };
            m.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
            m.tangents = Enumerable.Repeat(new Vector4(1f, 0f, 0f, 1f), 4).ToArray();
            m.colors32 = Enumerable.Repeat(new Color32(10, 20, 30, 40), 4).ToArray();
            for (var ch = 0; ch < 4; ch++) m.SetUVs(ch, m.vertices.Select(v => new Vector4(v.x + ch, v.y, 0.1f * ch, 1f)).ToList());
            m.subMeshCount = 2;
            m.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0);
            m.SetIndices(new[] { 0, 3 }, MeshTopology.Lines, 1);
            m.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.up), Matrix4x4.Translate(Vector3.right) };
            m.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 0.75f, boneIndex1 = 1, weight1 = 0.25f }, 4).ToArray();
            Shape(m, "swell", 100f, Vector3.forward, Vector3.up, Vector3.right);
            return m;
        }

        static void Shape(Mesh m, string name, float weight, Vector3 delta, Vector3 normal, Vector3 tangent)
        {
            m.ClearBlendShapes();
            m.AddBlendShapeFrame(name, weight, Enumerable.Repeat(0.01f * delta, 4).ToArray(), Enumerable.Repeat(0.1f * normal, 4).ToArray(),
                Enumerable.Repeat(0.1f * tangent, 4).ToArray());
        }

        static void Change(Mesh m, string field)
        {
            var weights = m.boneWeights;
            switch (field)
            {
                case "positions": { var v = m.vertices; v[0].z = 0.001f; m.vertices = v; break; }
                case "normals": { var n = m.normals; n[0] = Vector3.forward; m.normals = n; break; }
                case "tangents": { var t = m.tangents; t[0].w = -1f; m.tangents = t; break; }
                case "colours": { var c = m.colors32; c[0].r = 11; m.colors32 = c; break; }
                case "uv0": case "uv1": case "uv2": case "uv3":
                {
                    var ch = field[2] - '0';
                    var uv = new List<Vector4>();
                    m.GetUVs(ch, uv);
                    uv[0] += new Vector4(0.5f, 0f, 0f, 0f);
                    m.SetUVs(ch, uv);
                    break;
                }
                case "bones per vertex": weights[0] = new BoneWeight { boneIndex0 = 0, weight0 = 1f }; m.boneWeights = weights; break;
                case "bone index": weights[0].boneIndex1 = 2; m.boneWeights = weights; break;
                case "bone weight": weights[0].weight0 = 0.6f; weights[0].weight1 = 0.4f; m.boneWeights = weights; break;
                case "bind poses": { var p = m.bindposes; p[1] = Matrix4x4.Translate(Vector3.down); m.bindposes = p; break; }
                case "topology": m.SetIndices(new[] { 0, 3 }, MeshTopology.Points, 1); break;
                case "indices": m.SetIndices(new[] { 0, 2, 1, 2, 1, 3 }, MeshTopology.Triangles, 0); break;
                case "shape key name": Shape(m, "swelled", 100f, Vector3.forward, Vector3.up, Vector3.right); break;
                case "frame weight": Shape(m, "swell", 50f, Vector3.forward, Vector3.up, Vector3.right); break;
                case "frame positions": Shape(m, "swell", 100f, Vector3.back, Vector3.up, Vector3.right); break;
                case "frame normals": Shape(m, "swell", 100f, Vector3.forward, Vector3.down, Vector3.right); break;
                case "frame tangents": Shape(m, "swell", 100f, Vector3.forward, Vector3.up, Vector3.left); break;
                default: Assert.Fail($"no change for '{field}'"); break;
            }
        }

        [Test]
        public void TheSameContent_HashesTheSame()
        {
            Assert.That(MeshContent.Hash(Make()), Is.EqualTo(MeshContent.Hash(Make())));
        }

        [TestCase("positions"), TestCase("normals"), TestCase("tangents"), TestCase("colours")]
        [TestCase("uv0"), TestCase("uv1"), TestCase("uv2"), TestCase("uv3")]
        [TestCase("bones per vertex"), TestCase("bone index"), TestCase("bone weight"), TestCase("bind poses")]
        [TestCase("topology"), TestCase("indices")]
        [TestCase("shape key name"), TestCase("frame weight"), TestCase("frame positions"), TestCase("frame normals"), TestCase("frame tangents")]
        public void EachHashedField_MovesTheHash(string field)
        {
            var before = MeshContent.Hash(Make());
            var changed = Make();
            Change(changed, field);

            Assert.That(MeshContent.Hash(changed), Is.Not.EqualTo(before));
        }
    }
}
