using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>Where a renderer's bones stand, in avatar-root space, and which avatar-wide id each one has.</summary>
    internal readonly struct SkinningPose
    {
        /// <summary>Per bone slot of the renderer, the bone's local-to-avatar-root matrix. Empty for an unskinned renderer.</summary>
        public readonly Matrix4x4[] BoneToRoot;
        /// <summary>Per bone slot, the bone's avatar-wide id.</summary>
        public readonly int[] BoneIds;
        /// <summary>The renderer's own local-to-avatar-root matrix, which places every vertex when there are no bones.</summary>
        public readonly Matrix4x4 RendererToRoot;
        public readonly int RendererBoneId;

        public SkinningPose(Matrix4x4[] boneToRoot, int[] boneIds, Matrix4x4 rendererToRoot, int rendererBoneId)
        {
            BoneToRoot = boneToRoot ?? Array.Empty<Matrix4x4>();
            BoneIds = boneIds ?? Array.Empty<int>();
            RendererToRoot = rendererToRoot;
            RendererBoneId = rendererBoneId;
        }

        public static SkinningPose Unskinned(Matrix4x4 rendererToRoot, int rendererBoneId) =>
            new SkinningPose(null, null, rendererToRoot, rendererBoneId);
    }

    /// <summary>
    /// Which shape key, at which weight, an entry of <see cref="EvalMesh.Frames"/> is; or, with
    /// <see cref="ShapeKey"/> equal to <see cref="AsItStands"/>, every live key at once at the weight the renderer holds.
    /// </summary>
    internal readonly struct LiveFrame
    {
        /// <summary>The <see cref="ShapeKey"/> of the configuration the renderer stands in as built.</summary>
        public const int AsItStands = -1;

        public readonly int ShapeKey;
        public readonly float Weight;

        public LiveFrame(int shapeKey, float weight)
        {
            ShapeKey = shapeKey;
            Weight = weight;
        }
    }

    internal static class MeshExtractor
    {
        /// <summary>A delta shorter than a micrometre moves nothing anyone can see, so it is left out of the sparse set.</summary>
        public const float DeltaEpsilonSq = 1e-12f;

        /// <summary>
        /// Builds the evaluation mesh: positions and normals skinned into avatar-root space at the given pose with
        /// frozen keys applied, influences by avatar-wide bone id, triangles of every triangle submesh, and each
        /// live key as one frame per configuration it reaches: each authored frame at or under its driven maximum,
        /// then the key at that maximum. <paramref name="liveness"/> is indexed by shape key; null treats every key
        /// as dead, as a MeshRenderer, which never applies shape keys, needs.
        /// <paramref name="heldWeights"/>, per shape key, is the weight the renderer holds each key at. When a live key
        /// holds a nonzero weight, one more frame follows the rest: every live key at once at its held weight, the
        /// state the avatar stands in by default, which no single key's frame reaches when two act on one region.
        /// </summary>
        public static EvalMesh Extract(Mesh mesh, in SkinningPose pose, IReadOnlyList<ShapeKeyLiveness> liveness,
            Allocator allocator, out LiveFrame[] liveFrames, IReadOnlyList<float> heldWeights = null)
        {
            var vertexCount = mesh.vertexCount;
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var hasNormals = normals.Length == vertexCount;
            if (!hasNormals) normals = new Vector3[vertexCount];

            var (triangles, triangleSubmesh) = ReadTriangles(mesh);

            var shapes = new ShapeKeyFrames(vertexCount);
            var keyCount = liveness == null ? 0 : Math.Min(liveness.Count, mesh.blendShapeCount);
            for (var k = 0; k < keyCount; k++)
            {
                if (liveness[k].State != ShapeKeyState.Frozen) continue;
                shapes.Load(mesh, k);
                shapes.AddAt(liveness[k].FrozenWeight, positions, hasNormals ? normals : null);
            }
            if (!hasNormals) FaceNormals(positions, triangles, normals);

            var result = new EvalMesh();
            try
            {
                liveFrames = Fill(mesh, pose, liveness, keyCount, heldWeights, shapes, positions, normals, triangles, triangleSubmesh, allocator, ref result);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        static LiveFrame[] Fill(Mesh mesh, in SkinningPose pose, IReadOnlyList<ShapeKeyLiveness> liveness, int keyCount,
            IReadOnlyList<float> heldWeights, ShapeKeyFrames shapes, Vector3[] positions, Vector3[] normals, List<int3> triangles, List<int> triangleSubmesh,
            Allocator allocator, ref EvalMesh result)
        {
            var vertexCount = positions.Length;
            var linear = Skin(mesh, pose, positions, normals, allocator, ref result);

            result.Uv0 = new NativeArray<float2>(vertexCount, allocator);
            var uv = new List<Vector2>();
            mesh.GetUVs(0, uv);
            for (var v = 0; v < uv.Count && v < vertexCount; v++) result.Uv0[v] = uv[v];

            result.Triangles = new NativeArray<int3>(triangles.ToArray(), allocator);
            result.TriangleSubmesh = new NativeArray<int>(triangleSubmesh.ToArray(), allocator);

            var frames = new List<ShapeFrame>();
            var deltas = new List<ShapeDelta>();
            var sources = new List<LiveFrame>();
            var scratch = new Vector3[vertexCount];
            for (var k = 0; k < keyCount; k++)
            {
                if (liveness[k].State != ShapeKeyState.Live) continue;
                shapes.Load(mesh, k);
                foreach (var weight in Configurations(shapes, liveness[k].DrivenMax * shapes.FullWeight))
                {
                    shapes.PositionsAt(weight, scratch);
                    var start = deltas.Count;
                    for (var v = 0; v < vertexCount; v++)
                    {
                        var s = scratch[v];
                        if (s.x == 0f && s.y == 0f && s.z == 0f) continue;
                        var d = math.mul(linear[v], (float3)s);
                        if (math.lengthsq(d) > DeltaEpsilonSq) deltas.Add(new ShapeDelta { Vertex = v, Delta = d });
                    }
                    frames.Add(new ShapeFrame { DeltaStart = start, DeltaCount = deltas.Count - start });
                    sources.Add(new LiveFrame(k, weight));
                }
            }

            // As it stands: every live key at its held weight together. A held weight lies within the key's driven
            // range by construction, since the classifier takes the held weight as a starting extreme, so none is clamped.
            var stands = new Vector3[vertexCount];
            var anyHeld = false;
            for (var k = 0; heldWeights != null && k < keyCount && k < heldWeights.Count; k++)
            {
                if (liveness[k].State != ShapeKeyState.Live || heldWeights[k] == 0f) continue;
                shapes.Load(mesh, k);
                shapes.AddAt(heldWeights[k], stands, null);
                anyHeld = true;
            }
            if (anyHeld)
            {
                var start = deltas.Count;
                for (var v = 0; v < vertexCount; v++)
                {
                    var s = stands[v];
                    if (s.x == 0f && s.y == 0f && s.z == 0f) continue;
                    var d = math.mul(linear[v], (float3)s);
                    if (math.lengthsq(d) > DeltaEpsilonSq) deltas.Add(new ShapeDelta { Vertex = v, Delta = d });
                }
                frames.Add(new ShapeFrame { DeltaStart = start, DeltaCount = deltas.Count - start });
                sources.Add(new LiveFrame(LiveFrame.AsItStands, 0f));
            }
            result.Frames = new NativeArray<ShapeFrame>(frames.ToArray(), allocator);
            result.Deltas = new NativeArray<ShapeDelta>(deltas.ToArray(), allocator);
            return sources.ToArray();
        }

        /// <summary>
        /// The weights a live key is evaluated at: every authored frame it passes on the way to its driven
        /// maximum, then the maximum itself. A key driven below zero is evaluated at its minimum only.
        /// </summary>
        internal static List<float> Configurations(ShapeKeyFrames shapes, float drivenWeight)
        {
            var weights = new List<float>();
            if (drivenWeight > 0f)
                for (var f = 0; f < shapes.FrameCount; f++)
                    if (shapes.FrameWeight(f) < drivenWeight) weights.Add(shapes.FrameWeight(f));
            weights.Add(drivenWeight);
            return weights;
        }

        static (List<int3>, List<int>) ReadTriangles(Mesh mesh)
        {
            var triangles = new List<int3>();
            var submesh = new List<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(s, true);
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    triangles.Add(new int3(indices[i], indices[i + 1], indices[i + 2]));
                    submesh.Add(s);
                }
            }
            return (triangles, submesh);
        }

        static void FaceNormals(Vector3[] positions, List<int3> triangles, Vector3[] normals)
        {
            foreach (var t in triangles)
            {
                var n = Vector3.Cross(positions[t.y] - positions[t.x], positions[t.z] - positions[t.x]);
                normals[t.x] += n;
                normals[t.y] += n;
                normals[t.z] += n;
            }
        }

        /// <summary>
        /// Linear blend skinning into avatar-root space. Fills positions, normals and influences, and returns each
        /// vertex's blended linear part, which carries a shape delta from mesh space into avatar-root space.
        /// </summary>
        static float3x3[] Skin(Mesh mesh, in SkinningPose pose, Vector3[] positions, Vector3[] normals,
            Allocator allocator, ref EvalMesh result)
        {
            var vertexCount = positions.Length;
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var boneWeights = mesh.GetAllBoneWeights();
            var bindposes = mesh.bindposes;
            var skinned = bonesPerVertex.Length == vertexCount && pose.BoneToRoot.Length > 0 && bindposes.Length > 0;

            var slots = skinned ? Math.Min(pose.BoneToRoot.Length, bindposes.Length) : 0;
            var skin = new float4x4[slots];
            for (var b = 0; b < slots; b++) skin[b] = pose.BoneToRoot[b] * bindposes[b];
            float4x4 rigid = pose.RendererToRoot;

            var outPositions = new NativeArray<float3>(vertexCount, allocator);
            var outNormals = new NativeArray<float3>(vertexCount, allocator);
            var weightStart = new NativeArray<int>(vertexCount + 1, allocator);
            var influences = new List<BoneInfluence>(vertexCount * 2);
            var linear = new float3x3[vertexCount];
            var merged = new List<BoneInfluence>(8);

            var cursor = 0;
            for (var v = 0; v < vertexCount; v++)
            {
                weightStart[v] = influences.Count;
                merged.Clear();
                var m = float4x4.zero;
                if (skinned)
                {
                    int count = bonesPerVertex[v];
                    for (var i = 0; i < count; i++)
                    {
                        var bw = boneWeights[cursor + i];
                        if (bw.weight <= 0f || bw.boneIndex < 0 || bw.boneIndex >= slots) continue;
                        m += bw.weight * skin[bw.boneIndex];
                        Merge(merged, pose.BoneIds[bw.boneIndex], bw.weight);
                    }
                    cursor += count;
                }
                // A vertex with no usable weight rides the renderer's own transform, as an unskinned one does.
                if (merged.Count == 0)
                {
                    m = rigid;
                    merged.Add(new BoneInfluence { Bone = pose.RendererBoneId, Weight = 1f });
                }

                var l = new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz);
                linear[v] = l;
                outPositions[v] = math.transform(m, positions[v]);
                // Unity skins a normal by the same blended matrix as the position, then renormalizes.
                outNormals[v] = math.normalizesafe(math.mul(l, (float3)normals[v]));

                merged.Sort((a, b) => b.Weight != a.Weight ? b.Weight.CompareTo(a.Weight) : a.Bone.CompareTo(b.Bone));
                var sum = 0f;
                foreach (var inf in merged) sum += inf.Weight;
                foreach (var inf in merged) influences.Add(new BoneInfluence { Bone = inf.Bone, Weight = inf.Weight / sum });
            }
            weightStart[vertexCount] = influences.Count;

            result.Positions = outPositions;
            result.Normals = outNormals;
            result.WeightStart = weightStart;
            result.Weights = new NativeArray<BoneInfluence>(influences.ToArray(), allocator);
            return linear;
        }

        /// <summary>Two bone slots can name one Transform; their weights are one influence.</summary>
        static void Merge(List<BoneInfluence> merged, int bone, float weight)
        {
            for (var i = 0; i < merged.Count; i++)
            {
                if (merged[i].Bone != bone) continue;
                merged[i] = new BoneInfluence { Bone = bone, Weight = merged[i].Weight + weight };
                return;
            }
            merged.Add(new BoneInfluence { Bone = bone, Weight = weight });
        }
    }
}
