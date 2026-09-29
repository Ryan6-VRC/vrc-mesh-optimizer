using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ryan6Vrc.MeshOptimizer.Tests.Extraction
{
    // The algorithms judge error, occlusion and shape motion in avatar-root space, so the evaluation mesh
    // must stand where the renderer draws it: skinned by the bones' current pose with every key at the weight
    // the renderer holds, and each live key's motion carried as the displacement it causes from that weight to
    // each other weight it reaches.
    public class ExtractionTests
    {
        readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void DestroyMade()
        {
            foreach (var o in _made) Object.DestroyImmediate(o);
            _made.Clear();
        }

        Mesh Made(Mesh mesh)
        {
            _made.Add(mesh);
            return mesh;
        }

        static readonly Quaternion QuarterTurnZ = Quaternion.Euler(0, 0, 90);

        // Bone 0 stands at the origin; bone 1 at (0, 1, 0), turned a quarter about Z in the pose.
        // Vertex 0 is shared evenly, vertex 1 rides bone 1 alone, vertex 2 rides bone 0 alone.
        Mesh TwoBoneMesh()
        {
            var mesh = Made(new Mesh { name = "two-bone" });
            mesh.vertices = new[] { new Vector3(1, 1, 0), new Vector3(0, 2, 0), new Vector3(1, 0, 0) };
            mesh.normals = new[] { Vector3.forward, Vector3.right, Vector3.forward };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(0.5f, 1), new Vector2(1, 0) };
            mesh.triangles = new[] { 0, 1, 2 };
            using var perVertex = new NativeArray<byte>(new byte[] { 2, 1, 1 }, Allocator.Temp);
            using var weights = new NativeArray<BoneWeight1>(new[]
            {
                new BoneWeight1 { boneIndex = 0, weight = 0.5f }, new BoneWeight1 { boneIndex = 1, weight = 0.5f },
                new BoneWeight1 { boneIndex = 1, weight = 1f },
                new BoneWeight1 { boneIndex = 0, weight = 1f },
            }, Allocator.Temp);
            mesh.SetBoneWeights(perVertex, weights);
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(0, -1, 0)) };
            return mesh;
        }

        static SkinningPose TwoBonePose() => new SkinningPose(
            new[] { Matrix4x4.identity, Matrix4x4.TRS(new Vector3(0, 1, 0), QuarterTurnZ, Vector3.one) },
            new[] { 10, 11 }, Matrix4x4.identity, 99);

        static void AssertNear(float3 actual, Vector3 expected, string what) =>
            Assert.That(math.distance(actual, expected), Is.LessThan(1e-5f), $"{what}: {actual} is not {expected}");

        [Test]
        public void TwoBoneMesh_LandsWhereLinearBlendSkinningPutsIt()
        {
            using var eval = MeshExtractor.Extract(TwoBoneMesh(), TwoBonePose(), null, Allocator.Temp, out _);

            // Vertex 0: bone 0 leaves it at (1, 1, 0); bone 1 turns it to (0, 2, 0); half of each.
            AssertNear(eval.Positions[0], new Vector3(0.5f, 1.5f, 0), "shared vertex");
            AssertNear(eval.Positions[1], new Vector3(-1, 1, 0), "vertex on the turned bone");
            AssertNear(eval.Positions[2], new Vector3(1, 0, 0), "vertex on the still bone");
            AssertNear(eval.Normals[1], Vector3.up, "normal on the turned bone");
            AssertNear(eval.Normals[0], Vector3.forward, "normal along the turn axis");
        }

        [Test]
        public void Influences_NameAvatarWideBones_HeaviestFirst_SummingToOne()
        {
            using var eval = MeshExtractor.Extract(TwoBoneMesh(), TwoBonePose(), null, Allocator.Temp, out _);

            Assert.That(eval.WeightStart.ToArray(), Is.EqualTo(new[] { 0, 2, 3, 4 }));
            Assert.That(Influence(eval, 0), Is.EqualTo((10, 0.5f)));
            Assert.That(Influence(eval, 1), Is.EqualTo((11, 0.5f)));
            Assert.That(Influence(eval, 2), Is.EqualTo((11, 1f)));
            Assert.That(Influence(eval, 3), Is.EqualTo((10, 1f)));
        }

        static (int, float) Influence(EvalMesh eval, int i) => (eval.Weights[i].Bone, eval.Weights[i].Weight);

        [Test]
        public void MoreThanFourInfluences_AreAllKept_AndSlotsNamingOneBoneMerge()
        {
            var mesh = Made(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var weights = new[] { 0.3f, 0.2f, 0.15f, 0.15f, 0.1f, 0.1f };
            var list = new List<BoneWeight1>();
            for (var b = 0; b < weights.Length; b++) list.Add(new BoneWeight1 { boneIndex = b, weight = weights[b] });
            list.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
            list.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
            using var perVertex = new NativeArray<byte>(new byte[] { 6, 1, 1 }, Allocator.Temp);
            using var boneWeights = new NativeArray<BoneWeight1>(list.ToArray(), Allocator.Temp);
            mesh.SetBoneWeights(perVertex, boneWeights);
            var identity = new Matrix4x4[6];
            for (var b = 0; b < 6; b++) identity[b] = Matrix4x4.identity;
            mesh.bindposes = identity;
            // Slots 4 and 5 are the same Transform.
            var pose = new SkinningPose(identity, new[] { 0, 1, 2, 3, 4, 4 }, Matrix4x4.identity, 9);

            using var eval = MeshExtractor.Extract(mesh, pose, null, Allocator.Temp, out _);

            var expected = new[] { (0, 0.3f), (1, 0.2f), (4, 0.2f), (2, 0.15f), (3, 0.15f) };
            Assert.That(eval.WeightStart[1] - eval.WeightStart[0], Is.EqualTo(expected.Length));
            for (var i = 0; i < expected.Length; i++)
            {
                var (bone, weight) = Influence(eval, eval.WeightStart[0] + i);
                Assert.That(bone, Is.EqualTo(expected[i].Item1), $"influence {i}");
                // Unity stores skin weights at about 16-bit precision.
                Assert.That(weight, Is.EqualTo(expected[i].Item2).Within(1e-4f), $"influence {i}");
            }
        }

        [Test]
        public void UnskinnedMesh_CarriesOneInfluence_ItsRenderersOwnTransform()
        {
            var mesh = Made(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var pose = SkinningPose.Unskinned(Matrix4x4.TRS(new Vector3(0, 0, 1), Quaternion.identity, Vector3.one * 2), 7);

            using var eval = MeshExtractor.Extract(mesh, pose, null, Allocator.Temp, out _);

            Assert.That(eval.WeightStart.ToArray(), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            for (var v = 0; v < 3; v++) Assert.That(Influence(eval, v), Is.EqualTo((7, 1f)));
            AssertNear(eval.Positions[1], new Vector3(2, 0, 1), "vertex placed by the renderer's transform");
            AssertNear(eval.Normals[0], Vector3.forward, "normal computed from the faces when the mesh has none");
        }

        [Test]
        public void FrozenKey_MovesTheEvaluatedPositions_BeforeSkinning()
        {
            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("push", 100f, new[] { Vector3.zero, Vector3.right, Vector3.zero }, null, null);

            using var eval = MeshExtractor.Extract(mesh, TwoBonePose(), new[] { ShapeKeyLiveness.Frozen(50f) }, Allocator.Temp, out var frames);

            // Half of (1, 0, 0) in the bone's space, then turned with it: the vertex rises by 0.5.
            AssertNear(eval.Positions[1], new Vector3(-1, 1.5f, 0), "vertex under a frozen key");
            Assert.That(eval.Frames.Length, Is.Zero);
            Assert.That(frames, Is.Empty);
        }

        [Test]
        public void LiveKey_IsASparseFrame_ThatReproducesTheKeyAtItsDrivenMaximum()
        {
            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("push", 100f, new[] { Vector3.zero, Vector3.right, Vector3.zero }, null, null);
            var liveness = new[] { ShapeKeyLiveness.Live(0.6f) };

            using var eval = MeshExtractor.Extract(mesh, TwoBonePose(), liveness, Allocator.Temp, out var frames);

            AssertNear(eval.Positions[1], new Vector3(-1, 1, 0), "rest ignores a live key");
            Assert.That(eval.Frames.Length, Is.EqualTo(1));
            Assert.That(eval.Deltas.Length, Is.EqualTo(1), "only the vertex the key moves");
            Assert.That(eval.Deltas[0].Vertex, Is.EqualTo(1));
            // Rest plus delta is where the renderer draws the vertex with the key at 60.
            AssertNear(eval.Positions[1] + eval.Deltas[0].Delta, new Vector3(-1, 1.6f, 0), "vertex at the driven maximum");
            Assert.That(frames.Length, Is.EqualTo(1));
            Assert.That(frames[0].ShapeKey, Is.Zero);
            Assert.That(frames[0].Weight, Is.EqualTo(60f).Within(1e-4f));
        }

        [Test]
        public void MultiFrameKey_GivesEachFrameItPassesAndItsMaximum_AsConfigurations()
        {
            var mesh = Made(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("grow", 50f, new[] { Vector3.zero, Vector3.zero, new Vector3(1, 0, 0) }, null, null);
            mesh.AddBlendShapeFrame("grow", 100f, new[] { Vector3.zero, Vector3.zero, new Vector3(3, 0, 0) }, null, null);
            var pose = SkinningPose.Unskinned(Matrix4x4.identity, 0);

            using var eval = MeshExtractor.Extract(mesh, pose, new[] { ShapeKeyLiveness.Live(0.8f) }, Allocator.Temp, out var frames);

            Assert.That(frames, Is.EqualTo(new[] { new LiveFrame(0, 50f), new LiveFrame(0, 80f) }));
            Assert.That(eval.Frames.Length, Is.EqualTo(2));
            AssertNear(eval.Deltas[eval.Frames[0].DeltaStart].Delta, new Vector3(1, 0, 0), "the frame at 50");
            // At 80, three fifths of the way from the frame at 50 to the frame at 100.
            AssertNear(eval.Deltas[eval.Frames[1].DeltaStart].Delta, new Vector3(2.2f, 0, 0), "the key at 80");
        }

        // A frame is where the key takes the vertex from where it is held, not from zero.
        static Vector3 Displaced(EvalMesh eval, int frame, int vertex)
        {
            var f = eval.Frames[frame];
            for (var i = f.DeltaStart; i < f.DeltaStart + f.DeltaCount; i++)
                if (eval.Deltas[i].Vertex == vertex) return eval.Positions[vertex] + eval.Deltas[i].Delta;
            return eval.Positions[vertex];
        }

        [Test]
        public void LiveKeyHeldAtFullWeight_StandsThereAtRest_AndIsEvaluatedAtZero()
        {
            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("push", 100f, new[] { Vector3.zero, Vector3.right, Vector3.zero }, null, null);
            var liveness = new[] { ShapeKeyLiveness.Live(0f, 1f, 100f) };

            using var eval = MeshExtractor.Extract(mesh, TwoBonePose(), liveness, Allocator.Temp, out var frames);

            AssertNear(eval.Positions[1], new Vector3(-1, 2, 0), "rest has the key where the renderer holds it");
            Assert.That(frames, Is.EqualTo(new[] { new LiveFrame(0, 0f) }), "the one other weight it reaches");
            AssertNear(Displaced(eval, 0, 1), new Vector3(-1, 1, 0), "the key taken off");
        }

        [Test]
        public void LiveKeyHeldPartway_IsEvaluatedOnBothSidesOfIt()
        {
            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("push", 100f, new[] { Vector3.zero, Vector3.right, Vector3.zero }, null, null);
            // Held at 40 and driven anywhere from -50 to 100.
            var liveness = new[] { ShapeKeyLiveness.Live(-0.5f, 1f, 40f) };

            using var eval = MeshExtractor.Extract(mesh, TwoBonePose(), liveness, Allocator.Temp, out var frames);

            AssertNear(eval.Positions[1], new Vector3(-1, 1.4f, 0), "rest at 40");
            Assert.That(frames.Select(f => f.Weight), Is.EqualTo(new[] { -50f, 0f, 100f }));
            AssertNear(Displaced(eval, 0, 1), new Vector3(-1, 0.5f, 0), "at -50");
            AssertNear(Displaced(eval, 1, 1), new Vector3(-1, 1, 0), "at 0");
            AssertNear(Displaced(eval, 2, 1), new Vector3(-1, 2, 0), "at 100");
        }

        [Test]
        public void MultiFrameKeyHeldBetweenItsFrames_MovesAlongTheFramesFromWhereItIsHeld()
        {
            var mesh = Made(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame("grow", 50f, new[] { Vector3.zero, Vector3.zero, new Vector3(1, 0, 0) }, null, null);
            mesh.AddBlendShapeFrame("grow", 100f, new[] { Vector3.zero, Vector3.zero, new Vector3(3, 0, 0) }, null, null);
            var pose = SkinningPose.Unskinned(Matrix4x4.identity, 0);

            using var eval = MeshExtractor.Extract(mesh, pose, new[] { ShapeKeyLiveness.Live(0f, 1f, 75f) }, Allocator.Temp, out var frames);

            // At 75, halfway from the frame at 50 to the frame at 100.
            AssertNear(eval.Positions[2], new Vector3(2, 1, 0), "rest at 75");
            Assert.That(frames.Select(f => f.Weight), Is.EqualTo(new[] { 0f, 50f, 100f }));
            AssertNear(Displaced(eval, 0, 2), new Vector3(0, 1, 0), "off");
            AssertNear(Displaced(eval, 1, 2), new Vector3(1, 1, 0), "the frame at 50");
            AssertNear(Displaced(eval, 2, 2), new Vector3(3, 1, 0), "the frame at 100");
        }

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(75f)]
        [TestCase(100f)]
        public void EveryConfiguration_StandsWhereTheRendererBakesIt(float held)
        {
            // Two bones, the second turned and moved, a renderer under a scaled parent; a multi-frame live key, a frozen
            // key, and a live key held at 60.
            var root = new GameObject("Avatar");
            _made.Add(root);
            root.transform.SetPositionAndRotation(new Vector3(0.3f, 0.1f, -0.2f), Quaternion.Euler(0, 30, 0));
            var bone0 = new GameObject("Bone0").transform;
            bone0.SetParent(root.transform, false);
            var bone1 = new GameObject("Bone1").transform;
            bone1.SetParent(bone0, false);
            bone1.localPosition = new Vector3(0, 1, 0);
            var holder = new GameObject("Holder").transform;
            holder.SetParent(root.transform, false);
            holder.localScale = new Vector3(1.5f, 1f, 0.8f);
            var at = new GameObject("Mesh").transform;
            at.SetParent(holder, false);

            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("grow", 25f, new[] { new Vector3(0, 0, 0.01f), Vector3.zero, new Vector3(0.02f, 0, 0) }, null, null);
            mesh.AddBlendShapeFrame("grow", 100f, new[] { new Vector3(0, 0, 0.05f), new Vector3(0.03f, 0, 0), new Vector3(0.01f, 0.04f, 0) }, null, null);
            mesh.AddBlendShapeFrame("frozen", 100f, new[] { Vector3.zero, new Vector3(0, 0, 0.02f), Vector3.zero }, null, null);
            mesh.AddBlendShapeFrame("partway", 100f, new[] { new Vector3(0.01f, 0.01f, 0), Vector3.zero, new Vector3(0, 0, -0.03f) }, null, null);
            mesh.bindposes = new[] { bone0.worldToLocalMatrix * at.localToWorldMatrix, bone1.worldToLocalMatrix * at.localToWorldMatrix };
            var renderer = at.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone0, bone1 };
            bone1.localRotation = Quaternion.Euler(0, 0, 40);
            var weights = new[] { held, 35f, 60f };
            for (var k = 0; k < weights.Length; k++) renderer.SetBlendShapeWeight(k, weights[k]);
            var liveness = new[] { ShapeKeyLiveness.Live(0f, 1f, held), ShapeKeyLiveness.Frozen(35f), ShapeKeyLiveness.Live(0f, 1f, 60f) };

            using var eval = RendererExtraction.Extract(renderer, new AvatarBones(root.transform), liveness, Allocator.Temp, out var frames);

            var toRoot = RendererExtraction.WorldToRoot(root.transform) * at.localToWorldMatrix;
            var baked = Made(new Mesh());
            Vector3[] Bake(float[] w)
            {
                for (var k = 0; k < w.Length; k++) renderer.SetBlendShapeWeight(k, w[k]);
                renderer.BakeMesh(baked, true);
                return baked.vertices.Select(v => toRoot.MultiplyPoint3x4(v)).ToArray();
            }
            void AssertBaked(Vector3[] expected, System.Func<int, Vector3> actual, string what)
            {
                for (var v = 0; v < expected.Length; v++)
                    Assert.That(Vector3.Distance(actual(v), expected[v]), Is.LessThan(1e-6f), $"{what}, vertex {v}: {actual(v)} is not {expected[v]}");
            }

            AssertBaked(Bake(weights), v => eval.Positions[v], "as it stands");
            Assert.That(frames.Length, Is.EqualTo(eval.Frames.Length));
            for (var f = 0; f < frames.Length; f++)
            {
                var w = (float[])weights.Clone();
                w[frames[f].ShapeKey] = frames[f].Weight;
                AssertBaked(Bake(w), v => Displaced(eval, f, v), $"key {frames[f].ShapeKey} at {frames[f].Weight}");
            }
        }

        [Test]
        public void DeadKey_IsNeitherAppliedNorCarried()
        {
            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("push", 100f, new[] { Vector3.zero, Vector3.right, Vector3.zero }, null, null);

            using var eval = MeshExtractor.Extract(mesh, TwoBonePose(), new[] { ShapeKeyLiveness.Dead }, Allocator.Temp, out _);

            AssertNear(eval.Positions[1], new Vector3(-1, 1, 0), "vertex under a dead key");
            Assert.That(eval.Frames.Length, Is.Zero);
        }

        [TestCase(IndexFormat.UInt16)]
        [TestCase(IndexFormat.UInt32)]
        public void TrianglesKeepTheirSubmesh_AndOtherTopologiesStayOut(IndexFormat format)
        {
            var mesh = Made(new Mesh { indexFormat = format });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one, Vector3.forward };
            mesh.subMeshCount = 3;
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 2, 3, 3, 4 }, MeshTopology.Lines, 1);
            mesh.SetIndices(new[] { 1, 3, 2, 2, 3, 4 }, MeshTopology.Triangles, 2);

            using var eval = MeshExtractor.Extract(mesh, SkinningPose.Unskinned(Matrix4x4.identity, 0), null, Allocator.Temp, out _);

            Assert.That(eval.Triangles.ToArray(), Is.EqualTo(new[] { new int3(0, 1, 2), new int3(1, 3, 2), new int3(2, 3, 4) }));
            Assert.That(eval.TriangleSubmesh.ToArray(), Is.EqualTo(new[] { 0, 2, 2 }));
        }

        [Test]
        public void MeshThatIsNotReadable_IsStillExtracted()
        {
            var mesh = TwoBoneMesh();
            mesh.AddBlendShapeFrame("push", 100f, new[] { Vector3.zero, Vector3.right, Vector3.zero }, null, null);
            mesh.UploadMeshData(true);
            Assert.That(mesh.isReadable, Is.False, "the fixture must be a mesh that is not readable");

            using var eval = MeshExtractor.Extract(mesh, TwoBonePose(), new[] { ShapeKeyLiveness.Live(1f) }, Allocator.Temp, out _);

            AssertNear(eval.Positions[1], new Vector3(-1, 1, 0), "skinned vertex");
            Assert.That(eval.Triangles.Length, Is.EqualTo(1));
            Assert.That(eval.Deltas.Length, Is.EqualTo(1));
        }
    }
}
