using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Which shape keys an entry of <see cref="EvalMesh.Frames"/> moves together, each from the weight it is held at to
    /// which weight, and the avatar-wide configuration it is part of: zero for one key moved alone.
    /// </summary>
    internal readonly struct LiveFrame : IEquatable<LiveFrame>
    {
        public readonly int Configuration;
        /// <summary>The keys the frame moves; one for a key moved alone.</summary>
        public readonly int[] Keys;
        /// <summary>Per key, the weight it is moved to, in the renderer's units.</summary>
        public readonly float[] Weights;

        /// <summary>A key moved alone.</summary>
        public LiveFrame(int shapeKey, float weight) : this(0, new[] { shapeKey }, new[] { weight }) { }

        public LiveFrame(int configuration, int[] keys, float[] weights)
        {
            if (keys == null || weights == null || keys.Length == 0 || keys.Length != weights.Length)
                throw new ArgumentException("a frame moves one or more keys, each to one weight");
            Configuration = configuration;
            Keys = keys;
            Weights = weights;
        }

        /// <summary>The first key the frame moves: the only one for a key moved alone.</summary>
        public int ShapeKey => Keys[0];
        /// <summary>The weight the first key is moved to.</summary>
        public float Weight => Weights[0];

        public bool Equals(LiveFrame other) =>
            Configuration == other.Configuration && Keys.SequenceEqual(other.Keys) && Weights.SequenceEqual(other.Weights);
        public override bool Equals(object obj) => obj is LiveFrame other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Configuration, Keys[0], Weights[0]);
        public override string ToString()
        {
            var weights = Weights;
            return $"configuration {Configuration}: {string.Join(", ", Keys.Select((k, i) => $"key {k} at {weights[i]}"))}";
        }
    }

    /// <summary>
    /// What extraction refuses: a vertex whose position or texture coordinate, or a shape key whose delta, is not a
    /// finite number.
    /// </summary>
    internal sealed class NonFiniteMeshException : Exception
    {
        /// <summary>The shape key whose delta is not finite, by the name Unity gives it; null when the vertex itself is not.</summary>
        public readonly string ShapeKey;
        public readonly int Vertex;

        public NonFiniteMeshException(string shapeKey, int vertex)
            : base(shapeKey != null
                ? $"shape key '{shapeKey}' moves vertex {vertex} by a delta that is not a finite number"
                : $"vertex {vertex} has a position or texture coordinate that is not a finite number")
        {
            ShapeKey = shapeKey;
            Vertex = vertex;
        }
    }

    internal static class MeshExtractor
    {
        /// <summary>A delta shorter than a micrometre moves nothing anyone can see, so it is left out of the sparse set.</summary>
        public const float DeltaEpsilonSq = 1e-12f;

        /// <summary>
        /// Builds the evaluation mesh: positions and normals skinned into avatar-root space at the given pose with every
        /// key at the weight the renderer holds it at, the state the avatar stands in; influences by avatar-wide bone id;
        /// triangles of every triangle submesh; and each live key as one frame per other weight it reaches (see
        /// <see cref="Configurations"/>), every other key held. <paramref name="liveness"/> is indexed by shape key; null
        /// treats every key as dead, as a MeshRenderer, which never applies shape keys, needs.
        /// </summary>
        public static EvalMesh Extract(Mesh mesh, in SkinningPose pose, IReadOnlyList<ShapeKeyLiveness> liveness,
            Allocator allocator, out LiveFrame[] liveFrames) =>
            Extract(mesh, pose, liveness, null, allocator, out liveFrames);

        /// <summary>
        /// As the overload without <paramref name="linked"/>, but a key some entry of <paramref name="linked"/> names is not
        /// moved alone: each entry is one frame instead, moving its keys together from their held weights to its weights and
        /// carrying its configuration, after the frames of keys moved alone and in the order given.
        /// </summary>
        public static EvalMesh Extract(Mesh mesh, in SkinningPose pose, IReadOnlyList<ShapeKeyLiveness> liveness,
            IReadOnlyList<LiveFrame> linked, Allocator allocator, out LiveFrame[] liveFrames)
        {
            linked ??= Array.Empty<LiveFrame>();
            foreach (var f in linked)
                foreach (var k in f.Keys)
                    if (liveness == null || k < 0 || k >= Math.Min(liveness.Count, mesh.blendShapeCount))
                        throw new ArgumentException($"a linked frame moves shape key {k}, which '{mesh.name}' does not evaluate", nameof(linked));
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
                if (liveness[k].State == ShapeKeyState.Dead || liveness[k].HeldWeight == 0f) continue;
                shapes.Load(mesh, k);
                shapes.AddAt(liveness[k].HeldWeight, positions, hasNormals ? normals : null);
            }
            if (!hasNormals) FaceNormals(positions, triangles, normals);

            var result = new EvalMesh();
            try
            {
                liveFrames = Fill(mesh, pose, liveness, linked, keyCount, shapes, positions, normals, triangles, triangleSubmesh, allocator, ref result);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        static LiveFrame[] Fill(Mesh mesh, in SkinningPose pose, IReadOnlyList<ShapeKeyLiveness> liveness, IReadOnlyList<LiveFrame> linked, int keyCount,
            ShapeKeyFrames shapes, Vector3[] positions, Vector3[] normals, List<int3> triangles, List<int> triangleSubmesh,
            Allocator allocator, ref EvalMesh result)
        {
            var vertexCount = positions.Length;
            var linear = Skin(mesh, pose, positions, normals, allocator, ref result);

            result.Uv0 = new NativeArray<float2>(vertexCount, allocator);
            var uv = new List<Vector2>();
            mesh.GetUVs(0, uv);
            for (var v = 0; v < uv.Count && v < vertexCount; v++) result.Uv0[v] = uv[v];
            // A position (from the mesh, its bone weights or its bind poses) or texture coordinate that is not a finite
            // number reads as no cost to every rule that measures one, so the mesh is refused rather than evaluated.
            for (var v = 0; v < vertexCount; v++)
                if (!math.all(math.isfinite(result.Positions[v])) || !math.all(math.isfinite(result.Uv0[v])))
                    throw new NonFiniteMeshException(null, v);

            result.Triangles = new NativeArray<int3>(triangles.ToArray(), allocator);
            result.TriangleSubmesh = new NativeArray<int>(triangleSubmesh.ToArray(), allocator);

            var frames = new List<ShapeFrame>();
            var deltas = new List<ShapeDelta>();
            var sources = new List<LiveFrame>();
            var scratch = new Vector3[vertexCount];
            void Emit(in LiveFrame source)
            {
                var start = deltas.Count;
                for (var v = 0; v < vertexCount; v++)
                {
                    var s = scratch[v];
                    if (s.x == 0f && s.y == 0f && s.z == 0f) continue;
                    var d = math.mul(linear[v], (float3)s);
                    if (math.lengthsq(d) > DeltaEpsilonSq) deltas.Add(new ShapeDelta { Vertex = v, Delta = d });
                }
                frames.Add(new ShapeFrame { DeltaStart = start, DeltaCount = deltas.Count - start, Configuration = source.Configuration });
                sources.Add(source);
            }

            var moveTogether = new HashSet<int>(linked.SelectMany(f => f.Keys));
            for (var k = 0; k < keyCount; k++)
            {
                if (liveness[k].State != ShapeKeyState.Live || moveTogether.Contains(k)) continue;
                shapes.Load(mesh, k);
                var held = liveness[k].HeldWeight;
                foreach (var weight in Configurations(shapes.Weights, liveness[k], held))
                {
                    shapes.PositionsBetween(held, weight, scratch);
                    Emit(new LiveFrame(k, weight));
                }
            }
            // Keys moved together add, each from where it is held.
            foreach (var f in linked)
            {
                Array.Clear(scratch, 0, scratch.Length);
                for (var i = 0; i < f.Keys.Length; i++)
                {
                    shapes.Load(mesh, f.Keys[i]);
                    shapes.AddBetween(liveness[f.Keys[i]].HeldWeight, f.Weights[i], scratch);
                }
                Emit(f);
            }
            result.Frames = new NativeArray<ShapeFrame>(frames.ToArray(), allocator);
            result.Deltas = new NativeArray<ShapeDelta>(deltas.ToArray(), allocator);
            return sources.ToArray();
        }

        /// <summary>
        /// The weights a live key is evaluated at, besides the <paramref name="held"/> weight rest already has it at: zero,
        /// the lowest and highest weights it is driven to, and every authored frame weight between them, ascending. Its
        /// range is widened to take in zero and the held weight, since evaluating a weight the key never reaches only keeps
        /// triangles, and missing one it does reach can open a hole. A renderer blends a key linearly between authored
        /// frames, so these weights and the held one are every corner of the path the key moves a vertex along.
        /// </summary>
        /// <param name="frameWeights">The key's authored frame weights, ascending.</param>
        /// <param name="key">A live key's liveness, whose driven range is a fraction of <see cref="ShapeKeyClassifier.FullScale"/>.</param>
        /// <param name="held">The weight the other configurations depart from, in the renderer's units.</param>
        internal static List<float> Configurations(IReadOnlyList<float> frameWeights, in ShapeKeyLiveness key, float held)
        {
            var full = ShapeKeyClassifier.FullScale(frameWeights[frameWeights.Count - 1]);
            var lo = Math.Min(Math.Min(Snap(key.DrivenMin * full, held), 0f), held);
            var hi = Math.Max(Math.Max(Snap(key.DrivenMax * full, held), 0f), held);
            var candidates = new List<float> { lo, 0f, hi };
            foreach (var w in frameWeights)
                if (w > lo && w < hi) candidates.Add(w);
            candidates.Sort();

            var weights = new List<float>(candidates.Count);
            foreach (var w in candidates)
            {
                // Only the held weight itself is the rest state again: a weight a hair from it can be an authored frame far from it in space.
                if (w == held) continue;
                if (weights.Count > 0 && weights[weights.Count - 1] == w) continue;
                weights.Add(w);
            }
            return weights;
        }

        /// <summary>
        /// A range end read back from its fraction, put on the held weight when the classifier would have read it as a hold:
        /// the round trip through a fraction can leave the held end a float's width off it.
        /// </summary>
        static float Snap(float end, float held) => Math.Abs(end - held) <= ShapeKeyClassifier.HoldTolerance ? held : end;

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

            // Each array goes into the result as it is made, so the caller's cleanup reaches it if anything below throws.
            var outPositions = result.Positions = new NativeArray<float3>(vertexCount, allocator);
            var outNormals = result.Normals = new NativeArray<float3>(vertexCount, allocator);
            var weightStart = result.WeightStart = new NativeArray<int>(vertexCount + 1, allocator);
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
