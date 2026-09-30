using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    /// <summary>
    /// One state of the whole avatar to measure crossing in: an optional pose, and the weights each renderer holds. A
    /// renderer <see cref="Weights"/> does not name holds the weights it stands at. The crossing gate builds as it stands,
    /// live keys at zero and the poses itself; a caller passes any others, such as the linked states
    /// <see cref="GateReportInputs.Configurations"/> builds from a report.
    /// </summary>
    internal sealed class AvatarConfiguration
    {
        public readonly string Name;
        public readonly GatePose Pose;
        /// <summary>Per renderer, a weight for each shape key of its source mesh, in the renderer's units.</summary>
        public readonly IReadOnlyDictionary<Renderer, float[]> Weights;

        public AvatarConfiguration(string name, GatePose pose = null, IReadOnlyDictionary<Renderer, float[]> weights = null)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("a configuration needs a name", nameof(name));
            Name = name;
            Pose = pose;
            Weights = weights ?? new Dictionary<Renderer, float[]>();
        }
    }

    /// <summary>One pair of renderers in one configuration. A renderer paired with itself is its own triangles crossing each other.</summary>
    internal readonly struct PairCrossing
    {
        /// <summary>The two renderers' paths from the avatar root; the same path twice for a renderer against itself.</summary>
        public readonly string RendererA;
        public readonly string RendererB;
        public readonly string Configuration;
        /// <summary>Before is the source meshes, after the optimized ones; triangle indices name the optimized meshes' triangles in submesh order.</summary>
        public readonly CrossingChange Change;

        public PairCrossing(string rendererA, string rendererB, string configuration, CrossingChange change)
        {
            RendererA = rendererA;
            RendererB = rendererB;
            Configuration = configuration;
            Change = change;
        }

        public override string ToString()
        {
            var c = Change;
            string Cm(double m) => (m * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " cm";
            string Mm(float m) => (m * 1000f).ToString("0.###", CultureInfo.InvariantCulture) + " mm";
            var line = $"{RendererA} x {RendererB}, {Configuration}: crossing before {Cm(c.Before.Length)}, after {Cm(c.After.Length)}, new {Cm(c.New)} " +
                       $"({Cm(c.NewSameFacing)} same-facing) in {c.Runs} runs; reach after {Mm(c.After.ReachAThroughB)} and {Mm(c.After.ReachBThroughA)}";
            if (c.Runs == 0) return line;
            var p = c.LongestRunAt;
            return line + string.Format(CultureInfo.InvariantCulture, "; longest new run {0} at ({1:0.####}, {2:0.####}, {3:0.####}) m in avatar-root space, triangles {4} and {5}",
                Cm(c.LongestRun), p.x, p.y, p.z, c.LongestRunTriangleA, c.LongestRunTriangleB);
        }
    }

    internal sealed class CrossingReport
    {
        public readonly IReadOnlyList<string> Configurations;
        /// <summary>Every pair the broad phase let through, in every configuration; a pair left out cannot cross in it.</summary>
        public readonly IReadOnlyList<PairCrossing> Pairs;
        public readonly IReadOnlyList<SkippedConfiguration> Skipped;

        public CrossingReport(IReadOnlyList<string> configurations, IReadOnlyList<PairCrossing> pairs, IReadOnlyList<SkippedConfiguration> skipped)
        {
            Configurations = configurations;
            Pairs = pairs;
            Skipped = skipped;
        }

        /// <summary>The pair and configuration with the most new crossing, the first on a tie; -1 when nothing was measured.</summary>
        public int Worst()
        {
            var worst = -1;
            for (var i = 0; i < Pairs.Count; i++)
                if (worst < 0 || Pairs[i].Change.New > Pairs[worst].Change.New) worst = i;
            return worst;
        }

        /// <summary>One line per pair and configuration where anything crosses, most new crossing first, then one per skip.</summary>
        public string[] Lines() => Pairs.Where(p => p.Change.Before.Length > 0 || p.Change.After.Length > 0)
            .OrderByDescending(p => p.Change.New).Select(p => p.ToString())
            .Concat(Skipped.Select(s => $"{s.Name}: not measured, {s.Reason}")).ToArray();
    }

    /// <summary>
    /// The optimizer's crossing gate: where one surface of the avatar passes through another. Each renderer decimated on
    /// its own can stay within its error of its source and still be driven through the surface a millimetre from it, as
    /// skin through tights, which no per-renderer distance can see. So the whole avatar is baked with its source meshes,
    /// then with its optimized meshes, in the same configuration, and every pair of renderers that can touch is measured
    /// both times; what the gate reports is the crossing the optimizer added, apart from the crossing the avatar shipped with.
    /// </summary>
    internal static class CrossingGate
    {
        /// <remarks>
        /// Configurations, in order: as it stands; live keys at zero, when some renderer holds a live key off zero; each pose
        /// as it stands, and again with live keys at zero; then <paramref name="configurations"/>. A pair is measured when the two
        /// renderers' bounds overlap before or after, and every renderer is measured against itself. The avatar is left
        /// as it was found, every blendshape weight, every transform and every renderer's mesh, when this throws too.
        /// </remarks>
        public static CrossingReport Measure(Transform avatarRoot, IReadOnlyList<GateInput> inputs, IReadOnlyList<GatePose> poses,
            IReadOnlyList<AvatarConfiguration> configurations, in CrossingSettings settings)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            if (inputs == null || inputs.Count == 0) throw new ArgumentException("pass at least one renderer", nameof(inputs));
            foreach (var input in inputs) input.Check(avatarRoot);
            var repeated = inputs.GroupBy(i => i.Renderer).FirstOrDefault(g => g.Count() > 1);
            if (repeated != null) throw new ArgumentException($"'{repeated.Key.name}' is passed {repeated.Count()} times; pass each renderer once", nameof(inputs));

            var liveOffZero = false;
            var standing = inputs.Select(i =>
            {
                var w = i.StandingWeights(out var s);
                liveOffZero |= s;
                return w;
            }).ToArray();
            var zeroMap = new Dictionary<Renderer, float[]>();
            foreach (var input in inputs) zeroMap[input.Renderer] = input.LiveKeysAtZero();

            var planned = new List<AvatarConfiguration> { new AvatarConfiguration("as it stands") };
            if (liveOffZero) planned.Add(new AvatarConfiguration("live keys at zero", null, zeroMap));
            var skipped = new List<SkippedConfiguration>();
            var usable = GatePoses.Usable(avatarRoot, poses, skipped, out var humanoid);
            foreach (var pose in usable)
            {
                planned.Add(new AvatarConfiguration($"pose '{pose.Name}'", pose));
                if (liveOffZero) planned.Add(new AvatarConfiguration($"pose '{pose.Name}' with live keys at zero", pose, zeroMap));
            }
            foreach (var extra in configurations ?? Array.Empty<AvatarConfiguration>())
            {
                if (extra.Pose != null)
                {
                    var reasons = new List<SkippedConfiguration>();
                    if (GatePoses.Usable(avatarRoot, new[] { extra.Pose }, reasons, out var needs).Count == 0)
                    {
                        skipped.Add(new SkippedConfiguration(extra.Name, reasons[0].Reason));
                        continue;
                    }
                    humanoid = humanoid != null ? humanoid : needs;
                }
                foreach (var kv in extra.Weights)
                {
                    var input = inputs.FirstOrDefault(i => i.Renderer == kv.Key);
                    if (input == null) throw new ArgumentException($"configuration '{extra.Name}' sets weights on '{kv.Key.name}', which is not among the renderers passed", nameof(configurations));
                    var keys = input.Skinned != null ? input.Source.blendShapeCount : 0;
                    if (kv.Value == null || kv.Value.Length != keys)
                        throw new ArgumentException($"configuration '{extra.Name}' gives '{kv.Key.name}' {kv.Value?.Length ?? 0} weights for a source mesh with {keys} shape keys", nameof(configurations));
                }
                planned.Add(extra);
            }

            var surfaces = new Surfaces(inputs);
            var pairs = new List<PairCrossing>();
            var paths = inputs.Select(i => RendererExtraction.PathOf(avatarRoot, i.Renderer)).ToArray();
            var state = new AvatarState(avatarRoot, inputs.Select(i => i.Skinned).Where(s => s != null));
            HumanPoseHandler handler = null;
            var scratch = new Mesh { name = "gate-bake" };
            var before = new NativeList<CrossingSegment>(Allocator.Persistent);
            var after = new NativeList<CrossingSegment>(Allocator.Persistent);
            try
            {
                if (humanoid != null) handler = new HumanPoseHandler(humanoid.avatar, humanoid.transform);
                foreach (var configuration in planned)
                {
                    state.RestoreTransforms();
                    configuration.Pose?.Apply(avatarRoot, handler);
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        var weights = configuration.Weights.TryGetValue(inputs[i].Renderer, out var w) ? w : standing[i];
                        surfaces.Bake(i, inputs[i], weights, scratch, avatarRoot);
                    }
                    for (var i = 0; i < inputs.Count; i++)
                    for (var j = i; j < inputs.Count; j++)
                    {
                        if (i != j && !surfaces.MayTouch(i, j, settings.Touch)) continue;
                        if (i == j)
                        {
                            Crossing.MeasureSelf(surfaces.Source[i], settings, before);
                            Crossing.MeasureSelf(surfaces.Optimized[i], settings, after);
                        }
                        else
                        {
                            Crossing.Measure(surfaces.Source[i], surfaces.Source[j], settings, before);
                            Crossing.Measure(surfaces.Optimized[i], surfaces.Optimized[j], settings, after);
                        }
                        pairs.Add(new PairCrossing(paths[i], paths[j], configuration.Name, Crossing.Compare(before.AsArray(), after.AsArray(), settings)));
                    }
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
                    before.Dispose();
                    after.Dispose();
                    surfaces.Dispose();
                }
            }
            return new CrossingReport(planned.Select(c => c.Name).ToArray(), pairs, skipped);
        }

        /// <summary>Every renderer's source and optimized surface, re-baked per configuration, with their bounds.</summary>
        sealed class Surfaces : IDisposable
        {
            public readonly Surface[] Source;
            public readonly Surface[] Optimized;
            readonly float3[] _sourceMin, _sourceMax, _optimizedMin, _optimizedMax;

            public Surfaces(IReadOnlyList<GateInput> inputs)
            {
                var n = inputs.Count;
                Source = new Surface[n];
                Optimized = new Surface[n];
                _sourceMin = new float3[n];
                _sourceMax = new float3[n];
                _optimizedMin = new float3[n];
                _optimizedMax = new float3[n];
                try
                {
                    for (var i = 0; i < n; i++)
                    {
                        Source[i].Triangles = new NativeArray<int3>(MeshTriangles.Read(inputs[i].Source).triangles, Allocator.Persistent);
                        if (inputs[i].RemovedTriangles != null) Source[i].Excluded = new NativeArray<bool>(inputs[i].RemovedTriangles, Allocator.Persistent);
                        Optimized[i].Triangles = new NativeArray<int3>(MeshTriangles.Read(inputs[i].Optimized).triangles, Allocator.Persistent);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Bake(int i, GateInput input, float[] weights, Mesh scratch, Transform root)
            {
                Replace(ref Source[i].Positions, input.Bake(input.Source, weights, scratch, root));
                Replace(ref Optimized[i].Positions, input.Bake(input.Optimized, weights, scratch, root));
                Bounds(Source[i], out _sourceMin[i], out _sourceMax[i]);
                Bounds(Optimized[i], out _optimizedMin[i], out _optimizedMax[i]);
            }

            /// <summary>Whether two renderers' bounds overlap before or after: renderers whose bounds are apart cannot cross.</summary>
            public bool MayTouch(int i, int j, float margin) =>
                Overlap(_sourceMin[i], _sourceMax[i], _sourceMin[j], _sourceMax[j], margin) ||
                Overlap(_optimizedMin[i], _optimizedMax[i], _optimizedMin[j], _optimizedMax[j], margin);

            static bool Overlap(float3 aMin, float3 aMax, float3 bMin, float3 bMax, float margin) =>
                math.all(aMin <= bMax + margin) && math.all(bMin <= aMax + margin);

            static void Bounds(in Surface s, out float3 min, out float3 max)
            {
                min = new float3(float.MaxValue);
                max = new float3(float.MinValue);
                for (var t = 0; t < s.Triangles.Length; t++)
                {
                    var tri = s.Triangles[t];
                    for (var c = 0; c < 3; c++)
                    {
                        var p = s.Positions[tri[c]];
                        min = math.min(min, p);
                        max = math.max(max, p);
                    }
                }
            }

            static void Replace(ref NativeArray<float3> array, NativeArray<float3> with)
            {
                if (array.IsCreated) array.Dispose();
                array = with;
            }

            public void Dispose()
            {
                foreach (var s in Source.Concat(Optimized))
                {
                    if (s.Positions.IsCreated) s.Positions.Dispose();
                    if (s.Triangles.IsCreated) s.Triangles.Dispose();
                    if (s.Excluded.IsCreated) s.Excluded.Dispose();
                }
            }
        }
    }
}
