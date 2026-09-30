using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        /// <summary>
        /// Every key at the weight the renderer holds, which is the weight liveness recorded, bones where the avatar stands: the
        /// state the avatar is seen in by default, and the one the optimizer evaluates as rest.
        /// </summary>
        AsItStands,
        /// <summary>
        /// Frozen keys at their held weights, every live key at zero, bones where the avatar stands. Present only when some
        /// live key is held off zero; otherwise it is the avatar as it stands.
        /// </summary>
        LiveKeysAtZero,
        /// <summary>
        /// As it stands, with one live key moved to one other weight it reaches, the weights the optimizer evaluates it at
        /// (<see cref="MeshExtractor.Configurations"/>); or with a link's keys at one of the states the pass evaluated the link
        /// in, named for it, its <see cref="GateConfiguration.ShapeKey"/> -1.
        /// </summary>
        ShapeKey,
        /// <summary>As it stands, or with live keys at zero when <see cref="GateConfiguration.LiveKeysAtZero"/>, with the bones posed.</summary>
        Pose,
    }

    internal readonly struct GateConfiguration
    {
        public readonly ConfigurationKind Kind;
        /// <summary>"as it stands", "live keys at zero", the shape key's name, or the pose's name.</summary>
        public readonly string Name;
        /// <summary>For a shape key configuration, the key's index; otherwise -1.</summary>
        public readonly int ShapeKey;
        /// <summary>For a shape key configuration, the weight it is driven to, in the renderer's units; otherwise 0.</summary>
        public readonly float Weight;
        /// <summary>For a pose, whether its live keys are at zero rather than at the weights the renderer held.</summary>
        public readonly bool LiveKeysAtZero;

        public GateConfiguration(ConfigurationKind kind, string name, int shapeKey = -1, float weight = 0f, bool liveKeysAtZero = false)
        {
            Kind = kind;
            Name = name;
            ShapeKey = shapeKey;
            Weight = weight;
            LiveKeysAtZero = liveKeysAtZero;
        }

        public override string ToString() => Kind switch
        {
            ConfigurationKind.ShapeKey when ShapeKey < 0 => Name,
            ConfigurationKind.ShapeKey => $"shape key '{Name}' at {Weight.ToString("0.###", CultureInfo.InvariantCulture)}",
            ConfigurationKind.Pose => LiveKeysAtZero ? $"pose '{Name}' with live keys at zero" : $"pose '{Name}'",
            ConfigurationKind.AsItStands => "as it stands, every key at the weight the renderer holds",
            ConfigurationKind.LiveKeysAtZero => "live keys at zero, frozen keys held",
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
        /// <summary>The renderer's path from the avatar root, as <see cref="RendererExtraction.PathOf"/> gives it; each line starts with it.</summary>
        public readonly string Path;
        public readonly IReadOnlyList<ConfigurationDistance> Configurations;
        /// <summary>Configurations asked for and not measured, each with why.</summary>
        public readonly IReadOnlyList<SkippedConfiguration> Skipped;

        public GeometryReport(Renderer renderer, string path, IReadOnlyList<ConfigurationDistance> configurations, IReadOnlyList<SkippedConfiguration> skipped)
        {
            Renderer = renderer;
            Path = path;
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

        /// <summary>
        /// The configuration reading the largest value of a statistic, the first on a tie; -1 when nothing was measured. A
        /// configuration that sampled nothing in that direction reads worst of all: its surface could not be checked, so it
        /// cannot be passed.
        /// </summary>
        public int Worst(GateDirection direction, Statistic statistic)
        {
            var worst = -1;
            for (var c = 0; c < Configurations.Count; c++)
                if (worst < 0 || Worse(Stats(c, direction), Stats(worst, direction), statistic)) worst = c;
            return worst;
        }

        static bool Worse(DistanceStats a, DistanceStats b, Statistic statistic) =>
            !a.Sampled ? b.Sampled : b.Sampled && a.Get(statistic) > b.Get(statistic);

        /// <summary>
        /// One line per configuration, then one per skip: each direction's mean, p99 and max in millimetres and in pixels at
        /// <paramref name="camera"/>, and where each max is, or that the direction sampled nothing.
        /// </summary>
        public string[] Lines(GateCamera camera)
        {
            var lines = new List<string>();
            foreach (var c in Configurations)
            {
                var d = c.Distance;
                lines.Add($"{Path}, {c.Configuration}: source-to-optimized {Direction(d.AToB, "source", camera)}; " +
                          $"optimized-to-source {Direction(d.BToA, "optimized", camera)}");
            }
            foreach (var s in Skipped) lines.Add($"{Path}, {s.Name}: not measured, {s.Reason}");
            return lines.ToArray();
        }

        static string Direction(DirectedDistance d, string mesh, GateCamera camera) => d.Stats.Sampled
            ? $"{Figures(d.Stats, camera)}, max {Where(d, mesh)}"
            : $"nothing sampled, since no {mesh} triangle measured has area in this configuration";

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
        /// Configurations, in order: as it stands; live keys at zero, when a live key is held off zero; each live key moved
        /// alone, every other key as it stands, to each weight the optimizer evaluates it at; each pose as it stands, and
        /// again with live keys at zero. A MeshRenderer is measured as it stands only: it never applies shape keys, and a
        /// pose moves it rigidly, which moves no distance. A muscle pose on an avatar with no humanoid Avatar is skipped and named in the report. The avatar
        /// is left as it was found, every blendshape weight, every transform and the renderer's mesh, when this throws too.
        /// </remarks>
        public static GeometryReport Measure(Renderer renderer, Transform avatarRoot, Mesh source, Mesh optimized,
            bool[] removedTriangles, IReadOnlyList<ShapeKeyLiveness> liveness, IReadOnlyList<GatePose> poses, in SurfaceDistanceSettings settings) =>
            MeasureInput(new GateInput(renderer, source, optimized, removedTriangles, liveness), avatarRoot, poses, null, settings);

        /// <summary>
        /// <see cref="Measure"/> on a renderer as <see cref="GateReportInputs"/> gives it, with the report's linked states: a key
        /// in <see cref="GateInput.LinkedKeys"/> is never moved alone, and each of <paramref name="linked"/> that sets this
        /// renderer's weights is measured, after the keys moved alone, as the pass evaluated it.
        /// </summary>
        public static GeometryReport MeasureInput(GateInput input, Transform avatarRoot, IReadOnlyList<GatePose> poses,
            IReadOnlyList<AvatarConfiguration> linked, in SurfaceDistanceSettings settings)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            input.Check(avatarRoot);
            var source = input.Source;
            var optimized = input.Optimized;
            var removedTriangles = input.RemovedTriangles;

            var sides = new Sides();
            try
            {
                sides.A.Triangles = new NativeArray<int3>(MeshTriangles.Read(source).triangles, Allocator.Persistent);
                sides.B.Triangles = new NativeArray<int3>(MeshTriangles.Read(optimized).triangles, Allocator.Persistent);
                if (removedTriangles != null) sides.A.Excluded = new NativeArray<bool>(removedTriangles, Allocator.Persistent);
                return input.Skinned != null
                    ? MeasureSkinned(input, avatarRoot, poses, linked, settings, ref sides)
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
            var measured = new[] { new ConfigurationDistance(new GateConfiguration(ConfigurationKind.AsItStands, "as it stands"), SurfaceDistance.Measure(sides.A, sides.B, settings)) };
            var skipped = new List<SkippedConfiguration>();
            foreach (var pose in poses)
                skipped.Add(new SkippedConfiguration($"pose '{pose.Name}'", "a MeshRenderer moves rigidly with its bone, so every pose reads as it stands"));
            return new GeometryReport(input.Renderer, RendererExtraction.PathOf(root, input.Renderer), measured, skipped);
        }

        static GeometryReport MeasureSkinned(GateInput input, Transform root, IReadOnlyList<GatePose> poses, IReadOnlyList<AvatarConfiguration> linked,
            in SurfaceDistanceSettings settings, ref Sides sides)
        {
            var source = input.Source;
            var liveness = input.Liveness;
            var standingWeights = input.StandingWeights(out var liveOffZero);
            var zeroWeights = input.LiveKeysAtZero();
            var configurations = new List<(GateConfiguration configuration, GatePose pose, float[] weights)>
            {
                (new GateConfiguration(ConfigurationKind.AsItStands, "as it stands"), null, standingWeights),
            };
            if (liveOffZero) configurations.Add((new GateConfiguration(ConfigurationKind.LiveKeysAtZero, "live keys at zero"), null, zeroWeights));
            for (var k = 0; k < source.blendShapeCount; k++)
            {
                if (liveness[k].State != ShapeKeyState.Live || input.LinkedKeys.Contains(k)) continue;
                foreach (var weight in KeyWeights(source, k, liveness[k], standingWeights[k]))
                {
                    var weights = (float[])standingWeights.Clone();
                    weights[k] = weight;
                    configurations.Add((new GateConfiguration(ConfigurationKind.ShapeKey, source.GetBlendShapeName(k), k, weight), null, weights));
                }
            }
            foreach (var link in linked ?? Array.Empty<AvatarConfiguration>())
            {
                if (!link.Weights.TryGetValue(input.Renderer, out var weights)) continue;
                if (weights == null || weights.Length != source.blendShapeCount)
                    throw new ArgumentException($"configuration '{link.Name}' gives '{input.Renderer.name}' {weights?.Length ?? 0} weights for a source mesh with {source.blendShapeCount} shape keys", nameof(linked));
                if (link.Pose != null)
                    throw new ArgumentException($"configuration '{link.Name}' is posed; a linked state is measured with the bones where the avatar stands, and poses go in poses", nameof(linked));
                configurations.Add((new GateConfiguration(ConfigurationKind.ShapeKey, link.Name), null, weights));
            }

            var skipped = new List<SkippedConfiguration>();
            var usable = GatePoses.Usable(root, poses, skipped, out var humanoid);
            foreach (var pose in usable)
            {
                configurations.Add((new GateConfiguration(ConfigurationKind.Pose, pose.Name), pose, standingWeights));
                if (liveOffZero) configurations.Add((new GateConfiguration(ConfigurationKind.Pose, pose.Name, liveKeysAtZero: true), pose, zeroWeights));
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
            return new GeometryReport(input.Renderer, RendererExtraction.PathOf(root, input.Renderer), measured, skipped);
        }

        /// <summary>
        /// The weights a live key is measured at, departing from <paramref name="held"/>: the ones the optimizer evaluates it
        /// at, by the same rule, <see cref="MeshExtractor.Configurations"/>.
        /// </summary>
        internal static List<float> KeyWeights(Mesh mesh, int key, in ShapeKeyLiveness liveness, float held) =>
            MeshExtractor.Configurations(ShapeKeyFrames.WeightsOf(mesh, key), liveness, held);

        static void Replace(ref NativeArray<float3> array, NativeArray<float3> with)
        {
            if (array.IsCreated) array.Dispose();
            array = with;
        }
    }
}
