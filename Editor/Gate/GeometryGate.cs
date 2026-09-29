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
        /// <summary>
        /// Rest, with every live key at the weight the renderer held when the gate was called: the shapes the avatar is
        /// seen in by default. Present only when some live key is held off zero.
        /// </summary>
        AsItStands,
        /// <summary>Rest, with one live key at one authored frame weight under its driven maximum, or at that maximum.</summary>
        ShapeKey,
        /// <summary>Rest, or as it stands when <see cref="GateConfiguration.AtHeldWeights"/>, with the bones posed.</summary>
        Pose,
    }

    internal readonly struct GateConfiguration
    {
        public readonly ConfigurationKind Kind;
        /// <summary>"rest", "as it stands", the shape key's name, or the pose's name.</summary>
        public readonly string Name;
        /// <summary>For a shape key configuration, the key's index; otherwise -1.</summary>
        public readonly int ShapeKey;
        /// <summary>For a shape key configuration, the weight it is driven to, in the renderer's units; otherwise 0.</summary>
        public readonly float Weight;
        /// <summary>For a pose, whether its live keys are at the weights the renderer held rather than at rest.</summary>
        public readonly bool AtHeldWeights;

        public GateConfiguration(ConfigurationKind kind, string name, int shapeKey = -1, float weight = 0f, bool atHeldWeights = false)
        {
            Kind = kind;
            Name = name;
            ShapeKey = shapeKey;
            Weight = weight;
            AtHeldWeights = atHeldWeights;
        }

        public override string ToString() => Kind switch
        {
            ConfigurationKind.ShapeKey => $"shape key '{Name}' at {Weight.ToString("0.###", CultureInfo.InvariantCulture)}",
            ConfigurationKind.Pose => AtHeldWeights ? $"pose '{Name}' as it stands" : $"pose '{Name}'",
            ConfigurationKind.AsItStands => "as it stands, live keys at the weights the renderer holds",
            _ => Name,
        };
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
        /// Configurations, in order: rest; as it stands, when a live key is held off zero; each live key alone at each
        /// authored frame weight under its driven maximum and at that maximum; each pose at rest weights, and again as it
        /// stands. A
        /// MeshRenderer is measured at rest only: it never applies shape keys, and a pose moves it rigidly, which moves
        /// no distance. A muscle pose on an avatar with no humanoid Avatar is skipped and named in the report. The avatar
        /// is left as it was found, every blendshape weight, every transform and the renderer's mesh, when this throws too.
        /// </remarks>
        public static GeometryReport Measure(Renderer renderer, Transform avatarRoot, Mesh source, Mesh optimized,
            bool[] removedTriangles, IReadOnlyList<ShapeKeyLiveness> liveness, IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            var input = new GateInput(renderer, source, optimized, removedTriangles, liveness);
            input.Check(avatarRoot);

            var sides = new Sides();
            try
            {
                sides.A.Triangles = new NativeArray<int3>(ReadTriangles(source), Allocator.Persistent);
                sides.B.Triangles = new NativeArray<int3>(ReadTriangles(optimized), Allocator.Persistent);
                if (removedTriangles != null) sides.A.Excluded = new NativeArray<bool>(removedTriangles, Allocator.Persistent);
                return input.Skinned != null
                    ? MeasureSkinned(input, avatarRoot, poses, settings, ref sides)
                    : MeasureRigid(input, avatarRoot, poses ?? Array.Empty<GatePose>(), settings, ref sides);
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

        static GeometryReport MeasureRigid(GateInput input, Transform root, IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings, ref Sides sides)
        {
            sides.A.Positions = input.Bake(input.Source, null, null, root);
            sides.B.Positions = input.Bake(input.Optimized, null, null, root);
            var measured = new[] { new ConfigurationDistance(new GateConfiguration(ConfigurationKind.Rest, "rest"), SurfaceDistance.Measure(sides.A, sides.B, settings)) };
            var skipped = new List<SkippedConfiguration>();
            foreach (var pose in poses)
                skipped.Add(new SkippedConfiguration($"pose '{pose.Name}'", "a MeshRenderer moves rigidly with its bone, so every pose reads as rest does"));
            return new GeometryReport(input.Renderer, measured, skipped);
        }

        static GeometryReport MeasureSkinned(GateInput input, Transform root, IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings, ref Sides sides)
        {
            var source = input.Source;
            var liveness = input.Liveness;
            var restWeights = input.RestWeights();
            var standingWeights = input.HeldWeights(out var standing);
            var configurations = new List<(GateConfiguration configuration, GatePose pose, float[] weights)>
            {
                (new GateConfiguration(ConfigurationKind.Rest, "rest"), null, restWeights),
            };
            if (standing) configurations.Add((new GateConfiguration(ConfigurationKind.AsItStands, "as it stands"), null, standingWeights));
            for (var k = 0; k < source.blendShapeCount; k++)
            {
                if (liveness[k].State != ShapeKeyState.Live) continue;
                foreach (var weight in KeyWeights(source, k, liveness[k].DrivenMax))
                {
                    var weights = (float[])restWeights.Clone();
                    weights[k] = weight;
                    configurations.Add((new GateConfiguration(ConfigurationKind.ShapeKey, source.GetBlendShapeName(k), k, weight), null, weights));
                }
            }

            var skipped = new List<SkippedConfiguration>();
            var usable = GatePoses.Usable(root, poses, skipped, out var humanoid);
            foreach (var pose in usable)
            {
                configurations.Add((new GateConfiguration(ConfigurationKind.Pose, pose.Name), pose, restWeights));
                if (standing) configurations.Add((new GateConfiguration(ConfigurationKind.Pose, pose.Name, atHeldWeights: true), pose, standingWeights));
            }

            var measured = new List<ConfigurationDistance>(configurations.Count);
            var state = new AvatarState(root, new[] { input.Skinned });
            HumanPoseHandler handler = null;
            var scratch = new Mesh { name = "gate-bake" };
            try
            {
                if (humanoid != null) handler = new HumanPoseHandler(humanoid.avatar, humanoid.transform);
                foreach (var (configuration, pose, weights) in configurations)
                {
                    state.RestoreTransforms();
                    pose?.Apply(root, handler);
                    Replace(ref sides.A.Positions, input.Bake(source, weights, scratch, root));
                    Replace(ref sides.B.Positions, input.Bake(input.Optimized, weights, scratch, root));
                    measured.Add(new ConfigurationDistance(configuration, SurfaceDistance.Measure(sides.A, sides.B, settings)));
                }
            }
            finally
            {
                try
                {
                    state.Restore();
                }
                finally
                {
                    handler?.Dispose();
                    UnityEngine.Object.DestroyImmediate(scratch);
                }
            }
            return new GeometryReport(input.Renderer, measured, skipped);
        }

        /// <summary>
        /// The weights a live key is measured at: each authored frame weight it passes on the way to its driven maximum,
        /// then the maximum itself, as <see cref="MeshExtractor.Configurations"/> evaluates it for the decimator. A key
        /// driven below zero is measured at that weight only.
        /// </summary>
        internal static List<float> KeyWeights(Mesh mesh, int key, float drivenMax)
        {
            var frames = mesh.GetBlendShapeFrameCount(key);
            var driven = drivenMax * mesh.GetBlendShapeFrameWeight(key, frames - 1);
            var weights = new List<float>();
            if (driven > 0f)
                for (var f = 0; f < frames; f++)
                    if (mesh.GetBlendShapeFrameWeight(key, f) < driven) weights.Add(mesh.GetBlendShapeFrameWeight(key, f));
            weights.Add(driven);
            return weights;
        }

        static void Replace(ref NativeArray<float3> array, NativeArray<float3> with)
        {
            if (array.IsCreated) array.Dispose();
            array = with;
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
    }
}
