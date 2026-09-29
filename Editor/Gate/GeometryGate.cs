using System;
using System.Collections.Generic;
using System.Globalization;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    internal enum ConfigurationKind : byte
    {
        /// <summary>Frozen keys at their frozen weights, every other key at zero, bones where the avatar stands.</summary>
        Rest,
        /// <summary>Rest, with one live key at its driven maximum.</summary>
        ShapeKey,
        /// <summary>Rest, with the bones posed.</summary>
        Pose,
    }

    internal readonly struct GateConfiguration
    {
        public readonly ConfigurationKind Kind;
        /// <summary>"rest", the shape key's name, or the pose's name.</summary>
        public readonly string Name;
        /// <summary>For a shape key configuration, the key's index; otherwise -1.</summary>
        public readonly int ShapeKey;
        /// <summary>For a shape key configuration, the weight it is driven to, in the renderer's units; otherwise 0.</summary>
        public readonly float Weight;

        public GateConfiguration(ConfigurationKind kind, string name, int shapeKey = -1, float weight = 0f)
        {
            Kind = kind;
            Name = name;
            ShapeKey = shapeKey;
            Weight = weight;
        }

        public override string ToString() => Kind == ConfigurationKind.ShapeKey
            ? $"shape key '{Name}' at {Weight.ToString("0.###", CultureInfo.InvariantCulture)}"
            : Kind == ConfigurationKind.Pose ? $"pose '{Name}'" : Name;
    }

    /// <summary>Source is surface A, the optimized mesh surface B, in the renderer's avatar-root space.</summary>
    internal readonly struct ConfigurationDistance
    {
        public readonly GateConfiguration Configuration;
        public readonly SurfaceDistanceResult Distance;

        public ConfigurationDistance(GateConfiguration configuration, SurfaceDistanceResult distance)
        {
            Configuration = configuration;
            Distance = distance;
        }
    }

    internal readonly struct SkippedConfiguration
    {
        public readonly string Name;
        public readonly string Reason;

        public SkippedConfiguration(string name, string reason)
        {
            Name = name;
            Reason = reason;
        }
    }

    internal enum GateDirection : byte
    {
        /// <summary>Sampled on the source: a feature the optimized surface lost.</summary>
        SourceToOptimized,
        /// <summary>Sampled on the optimized mesh: a spike or fold it added.</summary>
        OptimizedToSource,
        Combined,
    }

    internal sealed class GeometryReport
    {
        public readonly Renderer Renderer;
        public readonly IReadOnlyList<ConfigurationDistance> Configurations;
        /// <summary>Configurations asked for and not measured, each with why.</summary>
        public readonly IReadOnlyList<SkippedConfiguration> Skipped;

        public GeometryReport(Renderer renderer, IReadOnlyList<ConfigurationDistance> configurations, IReadOnlyList<SkippedConfiguration> skipped)
        {
            Renderer = renderer;
            Configurations = configurations;
            Skipped = skipped;
        }

        public DistanceStats Stats(int configuration, GateDirection direction)
        {
            var d = Configurations[configuration].Distance;
            return direction switch
            {
                GateDirection.SourceToOptimized => d.AToB.Stats,
                GateDirection.OptimizedToSource => d.BToA.Stats,
                _ => d.Combined,
            };
        }

        /// <summary>The configuration reading the largest value of a statistic, the first on a tie; -1 when nothing was measured.</summary>
        public int Worst(GateDirection direction, Statistic statistic)
        {
            var worst = -1;
            for (var c = 0; c < Configurations.Count; c++)
                if (worst < 0 || Stats(c, direction).Get(statistic) > Stats(worst, direction).Get(statistic)) worst = c;
            return worst;
        }

        /// <summary>One line per configuration, then one per skip: each direction's mean, p99 and max in millimetres and in pixels at <paramref name="camera"/>, and where each max is.</summary>
        public string[] Lines(GateCamera camera)
        {
            var lines = new List<string>();
            foreach (var c in Configurations)
            {
                var d = c.Distance;
                lines.Add($"{Renderer.name}, {c.Configuration}: source-to-optimized {Figures(d.AToB.Stats, camera)}, max {Where(d.AToB, "source")}; " +
                          $"optimized-to-source {Figures(d.BToA.Stats, camera)}, max {Where(d.BToA, "optimized")}");
            }
            foreach (var s in Skipped) lines.Add($"{Renderer.name}, {s.Name}: not measured, {s.Reason}");
            return lines.ToArray();
        }

        static string Where(DirectedDistance d, string mesh) => string.Format(CultureInfo.InvariantCulture,
            "at ({0:0.####}, {1:0.####}, {2:0.####}) m in avatar-root space, on {3} triangle {4}", d.MaxPosition.x, d.MaxPosition.y, d.MaxPosition.z, mesh, d.MaxTriangle);

        static string Figures(DistanceStats s, GateCamera camera)
        {
            string Both(float m) => string.Format(CultureInfo.InvariantCulture, "{0:0.###} mm ({1:0.##} px)", m * 1000f, camera.Pixels(m));
            var unmatched = s.Unmatched > 0 ? $", {s.Unmatched} samples matched nothing" : "";
            return $"mean {Both(s.Mean)}, p99 {Both(s.P99)}, max {Both(s.Max)}{unmatched}";
        }
    }

    /// <summary>
    /// The optimizer's geometric gate: bakes the source and the optimized mesh on one renderer in the same
    /// configuration and measures how far apart the two surfaces are. <see cref="SkinnedMeshRenderer.BakeMesh(Mesh, bool)"/>
    /// evaluates Unity's own skinning and blend shapes on the CPU when asked, so what is measured is what the renderer
    /// draws, never a stale deform.
    /// </summary>
    internal static class GeometryGate
    {
        /// <param name="renderer">A SkinnedMeshRenderer, or a MeshRenderer, under <paramref name="avatarRoot"/>.</param>
        /// <param name="source">The mesh before optimization.</param>
        /// <param name="optimized">The optimized mesh: a subset of the source's vertices, every shape key kept by index.</param>
        /// <param name="removedTriangles">
        /// Optional, one per source triangle in submesh order (the evaluation mesh's triangle order): true for a triangle
        /// removed as hidden. Those are judged elsewhere, so they are left out of the source surface rather than read as
        /// holes.
        /// </param>
        /// <param name="liveness">One entry per shape key of <paramref name="source"/>; may be null only for a mesh without shape keys.</param>
        /// <param name="poses">Poses to bake in, usually <see cref="PoseBank.Default"/>.</param>
        /// <remarks>
        /// Configurations, in order: rest; each live key alone at its driven maximum; each pose, at rest weights. A
        /// MeshRenderer is measured at rest only: it never applies shape keys, and a pose moves it rigidly, which moves
        /// no distance. A muscle pose on an avatar with no humanoid Avatar is skipped and named in the report. The avatar
        /// is left as it was found, every blendshape weight, every transform and the renderer's mesh, when this throws too.
        /// </remarks>
        public static GeometryReport Measure(Renderer renderer, Transform avatarRoot, Mesh source, Mesh optimized,
            bool[] removedTriangles, IReadOnlyList<ShapeKeyLiveness> liveness, IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (optimized == null) throw new ArgumentNullException(nameof(optimized));
            if (!renderer.transform.IsChildOf(avatarRoot))
                throw new ArgumentException($"'{renderer.name}' is not under the avatar root '{avatarRoot.name}'", nameof(renderer));
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null && !(renderer is MeshRenderer))
                throw new ArgumentException($"'{renderer.name}' is a {renderer.GetType().Name}; the gate measures a SkinnedMeshRenderer or a MeshRenderer", nameof(renderer));
            if (skinned != null)
            {
                if (optimized.blendShapeCount != source.blendShapeCount)
                    throw new ArgumentException($"the optimized mesh has {optimized.blendShapeCount} shape keys and the source {source.blendShapeCount}; an optimized mesh keeps every one", nameof(optimized));
                if (optimized.bindposes.Length != source.bindposes.Length)
                    throw new ArgumentException($"the optimized mesh has {optimized.bindposes.Length} bind poses and the source {source.bindposes.Length}; both must skin to the renderer's bones", nameof(optimized));
                var keys = liveness?.Count ?? 0;
                if (keys != source.blendShapeCount)
                    throw new ArgumentException($"liveness has {keys} entries for a source mesh with {source.blendShapeCount} shape keys; pass the renderer's liveness table", nameof(liveness));
            }

            var sourceTriangles = ReadTriangles(source);
            var optimizedTriangles = ReadTriangles(optimized);
            if (removedTriangles != null && removedTriangles.Length != sourceTriangles.Length)
                throw new ArgumentException($"{removedTriangles.Length} removed-triangle flags for a source mesh with {sourceTriangles.Length} triangles", nameof(removedTriangles));

            var sides = new Sides();
            try
            {
                sides.A.Triangles = new NativeArray<int3>(sourceTriangles, Allocator.Persistent);
                sides.B.Triangles = new NativeArray<int3>(optimizedTriangles, Allocator.Persistent);
                if (removedTriangles != null) sides.A.Excluded = new NativeArray<bool>(removedTriangles, Allocator.Persistent);
                var poseList = poses ?? Array.Empty<GatePose>();
                return skinned != null
                    ? MeasureSkinned(skinned, avatarRoot, source, optimized, liveness, poseList, settings, ref sides)
                    : MeasureRigid(renderer, avatarRoot, source, optimized, poseList, settings, ref sides);
            }
            finally
            {
                sides.Dispose();
            }
        }

        struct Sides : IDisposable
        {
            public Surface A;
            public Surface B;

            public void Dispose()
            {
                if (A.Positions.IsCreated) A.Positions.Dispose();
                if (A.Triangles.IsCreated) A.Triangles.Dispose();
                if (A.Excluded.IsCreated) A.Excluded.Dispose();
                if (B.Positions.IsCreated) B.Positions.Dispose();
                if (B.Triangles.IsCreated) B.Triangles.Dispose();
            }
        }

        static GeometryReport MeasureRigid(Renderer renderer, Transform root, Mesh source, Mesh optimized,
            IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings, ref Sides sides)
        {
            var toRoot = RendererExtraction.WorldToRoot(root) * renderer.transform.localToWorldMatrix;
            sides.A.Positions = ToRoot(source.vertices, toRoot);
            sides.B.Positions = ToRoot(optimized.vertices, toRoot);
            var measured = new[] { new ConfigurationDistance(new GateConfiguration(ConfigurationKind.Rest, "rest"), SurfaceDistance.Measure(sides.A, sides.B, settings)) };
            var skipped = new List<SkippedConfiguration>();
            foreach (var pose in poses)
                skipped.Add(new SkippedConfiguration($"pose '{pose.Name}'", "a MeshRenderer moves rigidly with its bone, so every pose reads as rest does"));
            return new GeometryReport(renderer, measured, skipped);
        }

        static GeometryReport MeasureSkinned(SkinnedMeshRenderer renderer, Transform root, Mesh source, Mesh optimized,
            IReadOnlyList<ShapeKeyLiveness> liveness, IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings, ref Sides sides)
        {
            var keyCount = source.blendShapeCount;
            var restWeights = new float[keyCount];
            var configurations = new List<(GateConfiguration configuration, GatePose pose, float[] weights)>
            {
                (new GateConfiguration(ConfigurationKind.Rest, "rest"), null, restWeights),
            };
            for (var k = 0; k < keyCount; k++)
                if (liveness[k].State == ShapeKeyState.Frozen) restWeights[k] = liveness[k].FrozenWeight;
            for (var k = 0; k < keyCount; k++)
            {
                if (liveness[k].State != ShapeKeyState.Live) continue;
                var weights = (float[])restWeights.Clone();
                weights[k] = liveness[k].DrivenMax * source.GetBlendShapeFrameWeight(k, source.GetBlendShapeFrameCount(k) - 1);
                configurations.Add((new GateConfiguration(ConfigurationKind.ShapeKey, source.GetBlendShapeName(k), k, weights[k]), null, weights));
            }

            var skipped = new List<SkippedConfiguration>();
            var animator = root.GetComponent<Animator>();
            var humanoid = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;
            var needsHumanoid = false;
            foreach (var pose in poses)
            {
                if (pose.NeedsHumanoid && !humanoid)
                {
                    skipped.Add(new SkippedConfiguration($"pose '{pose.Name}'",
                        $"it drives humanoid muscles and '{root.name}' has no Animator with a valid humanoid Avatar; pass a BoneRotationPose for this rig"));
                    continue;
                }
                pose.Check(root);
                needsHumanoid |= pose.NeedsHumanoid;
                configurations.Add((new GateConfiguration(ConfigurationKind.Pose, pose.Name), pose, restWeights));
            }

            var measured = new List<ConfigurationDistance>(configurations.Count);
            var state = new AvatarState(root, renderer);
            HumanPoseHandler handler = null;
            var bakedA = new Mesh { name = "gate-source-bake" };
            var bakedB = new Mesh { name = "gate-optimized-bake" };
            try
            {
                if (needsHumanoid) handler = new HumanPoseHandler(animator.avatar, animator.transform);
                foreach (var (configuration, pose, weights) in configurations)
                {
                    state.RestoreTransforms();
                    pose?.Apply(root, handler);
                    Bake(renderer, source, weights, bakedA);
                    Bake(renderer, optimized, weights, bakedB);
                    // With its scale, a bake is in the renderer's local space, so its local-to-world matrix places it once.
                    var toRoot = RendererExtraction.WorldToRoot(root) * renderer.transform.localToWorldMatrix;
                    Replace(ref sides.A.Positions, ToRoot(bakedA.vertices, toRoot));
                    Replace(ref sides.B.Positions, ToRoot(bakedB.vertices, toRoot));
                    measured.Add(new ConfigurationDistance(configuration, SurfaceDistance.Measure(sides.A, sides.B, settings)));
                }
            }
            finally
            {
                state.Restore();
                handler?.Dispose();
                UnityEngine.Object.DestroyImmediate(bakedA);
                UnityEngine.Object.DestroyImmediate(bakedB);
            }
            return new GeometryReport(renderer, measured, skipped);
        }

        static void Bake(SkinnedMeshRenderer renderer, Mesh mesh, float[] weights, Mesh into)
        {
            renderer.sharedMesh = mesh;
            for (var k = 0; k < weights.Length; k++) renderer.SetBlendShapeWeight(k, weights[k]);
            renderer.BakeMesh(into, true);
            if (into.vertexCount != mesh.vertexCount)
                throw new InvalidOperationException($"baking '{mesh.name}' on '{renderer.name}' gave {into.vertexCount} vertices for a mesh with {mesh.vertexCount}");
        }

        static void Replace(ref NativeArray<float3> array, NativeArray<float3> with)
        {
            if (array.IsCreated) array.Dispose();
            array = with;
        }

        static NativeArray<float3> ToRoot(Vector3[] vertices, Matrix4x4 toRoot)
        {
            var result = new NativeArray<float3>(vertices.Length, Allocator.Persistent);
            for (var v = 0; v < vertices.Length; v++) result[v] = toRoot.MultiplyPoint3x4(vertices[v]);
            return result;
        }

        /// <summary>Every triangle submesh's triangles, in submesh order: the order the evaluation mesh numbers them in.</summary>
        internal static int3[] ReadTriangles(Mesh mesh)
        {
            var triangles = new List<int3>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(s, true);
                for (var i = 0; i + 2 < indices.Length; i += 3) triangles.Add(new int3(indices[i], indices[i + 1], indices[i + 2]));
            }
            return triangles.ToArray();
        }

        /// <summary>What the gate changes on the avatar, as it was found.</summary>
        sealed class AvatarState
        {
            readonly Transform[] _transforms;
            readonly Vector3[] _positions;
            readonly Quaternion[] _rotations;
            readonly Vector3[] _scales;
            readonly SkinnedMeshRenderer _renderer;
            readonly Mesh _mesh;
            readonly float[] _weights;

            public AvatarState(Transform root, SkinnedMeshRenderer renderer)
            {
                _transforms = root.GetComponentsInChildren<Transform>(true);
                _positions = new Vector3[_transforms.Length];
                _rotations = new Quaternion[_transforms.Length];
                _scales = new Vector3[_transforms.Length];
                for (var i = 0; i < _transforms.Length; i++)
                {
                    _positions[i] = _transforms[i].localPosition;
                    _rotations[i] = _transforms[i].localRotation;
                    _scales[i] = _transforms[i].localScale;
                }
                _renderer = renderer;
                _mesh = renderer.sharedMesh;
                _weights = new float[_mesh != null ? _mesh.blendShapeCount : 0];
                for (var k = 0; k < _weights.Length; k++) _weights[k] = renderer.GetBlendShapeWeight(k);
            }

            /// <summary>Writes back only what differs, bit for bit, so an untouched transform is not written at all.</summary>
            public void RestoreTransforms()
            {
                for (var i = 0; i < _transforms.Length; i++)
                {
                    var t = _transforms[i];
                    if (t == null) continue;
                    if (!t.localPosition.Equals(_positions[i])) t.localPosition = _positions[i];
                    if (!t.localRotation.Equals(_rotations[i])) t.localRotation = _rotations[i];
                    if (!t.localScale.Equals(_scales[i])) t.localScale = _scales[i];
                }
            }

            public void Restore()
            {
                if (_renderer.sharedMesh != _mesh) _renderer.sharedMesh = _mesh;
                for (var k = 0; k < _weights.Length; k++)
                    if (!_renderer.GetBlendShapeWeight(k).Equals(_weights[k])) _renderer.SetBlendShapeWeight(k, _weights[k]);
                RestoreTransforms();
            }
        }
    }
}
