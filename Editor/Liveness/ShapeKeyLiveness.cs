using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Liveness
{
    internal enum ShapeKeyState : byte
    {
        /// <summary>Zero on the renderer and driven by nothing: it never deforms the mesh at runtime.</summary>
        Dead,
        /// <summary>Nonzero on the renderer and driven by nothing: it holds <see cref="ShapeKeyLiveness.FrozenWeight"/> forever.</summary>
        Frozen,
        /// <summary>Something moves it at runtime, as far as <see cref="ShapeKeyLiveness.DrivenMax"/>.</summary>
        Live,
    }

    internal readonly struct ShapeKeyLiveness : IEquatable<ShapeKeyLiveness>
    {
        public readonly ShapeKeyState State;
        /// <summary>For a frozen key, the weight it holds, in the renderer's units (0 to 100 for a usual key).</summary>
        public readonly float FrozenWeight;
        /// <summary>
        /// For a live key, the weight of largest magnitude it can take, as a fraction of its full weight
        /// (the weight of its last frame), sign kept.
        /// </summary>
        public readonly float DrivenMax;

        ShapeKeyLiveness(ShapeKeyState state, float frozenWeight, float drivenMax)
        {
            State = state;
            FrozenWeight = frozenWeight;
            DrivenMax = drivenMax;
        }

        public static ShapeKeyLiveness Dead => default;
        public static ShapeKeyLiveness Frozen(float weight) => new ShapeKeyLiveness(ShapeKeyState.Frozen, weight, 0f);
        public static ShapeKeyLiveness Live(float drivenMax) => new ShapeKeyLiveness(ShapeKeyState.Live, 0f, drivenMax);

        public bool Equals(ShapeKeyLiveness other) =>
            State == other.State && FrozenWeight.Equals(other.FrozenWeight) && DrivenMax.Equals(other.DrivenMax);
        public override bool Equals(object obj) => obj is ShapeKeyLiveness other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(State, FrozenWeight, DrivenMax);

        public override string ToString() => State switch
        {
            ShapeKeyState.Frozen => $"Frozen({FrozenWeight})",
            ShapeKeyState.Live => $"Live({DrivenMax})",
            _ => "Dead",
        };
    }

    /// <summary>One shape key as the renderer holds it.</summary>
    internal readonly struct ShapeKeyInput
    {
        public readonly string Name;
        /// <summary>The weight the renderer holds the key at.</summary>
        public readonly float Weight;
        /// <summary>The weight of the key's last frame, the weight at which it is fully applied.</summary>
        public readonly float FullWeight;

        public ShapeKeyInput(string name, float weight, float fullWeight)
        {
            Name = name;
            Weight = weight;
            FullWeight = fullWeight;
        }
    }

    /// <summary>A renderer's shape keys, in the mesh's order, under its path from the avatar root.</summary>
    internal sealed class RendererShapeKeys
    {
        public readonly string Path;
        public readonly ShapeKeyInput[] Keys;

        public RendererShapeKeys(string path, ShapeKeyInput[] keys)
        {
            Path = path;
            Keys = keys;
        }
    }

    /// <summary>
    /// One animation curve on a shape key: the renderer's path from the avatar root, the key's name, and the
    /// values the curve takes.
    /// </summary>
    internal readonly struct ShapeKeyBinding
    {
        public readonly string Path;
        public readonly string Key;
        public readonly float[] Values;

        public ShapeKeyBinding(string path, string key, float[] values)
        {
            Path = path;
            Key = key;
            Values = values;
        }
    }

    /// <summary>
    /// A shape key something outside the animators drives to its full weight: the descriptor's visemes,
    /// jaw flap and eyelids, or a world's MMD dance.
    /// </summary>
    internal readonly struct DrivenKey
    {
        public readonly string Path;
        public readonly string Key;

        public DrivenKey(string path, string key)
        {
            Path = path;
            Key = key;
        }
    }

    internal static class ShapeKeyClassifier
    {
        /// <summary>
        /// A curve value within this many weight units of the renderer's own weight only holds the key where
        /// it already is, which is not driving it. The defaults layers MA and VRCFury write are such curves.
        /// </summary>
        public const float HoldTolerance = 1e-3f;

        /// <summary>Classifies every key of every renderer. The result is indexed like the input.</summary>
        public static ShapeKeyLiveness[][] Classify(
            IReadOnlyList<RendererShapeKeys> renderers,
            IEnumerable<ShapeKeyBinding> bindings,
            IEnumerable<DrivenKey> driven)
        {
            var byKey = new Dictionary<(string, string), List<float[]>>();
            foreach (var b in bindings)
            {
                if (b.Values == null || b.Values.Length == 0) continue;
                if (!byKey.TryGetValue((b.Path, b.Key), out var list)) byKey[(b.Path, b.Key)] = list = new List<float[]>();
                list.Add(b.Values);
            }
            var drivenSet = new HashSet<(string, string)>();
            foreach (var d in driven) drivenSet.Add((d.Path, d.Key));

            var result = new ShapeKeyLiveness[renderers.Count][];
            for (var r = 0; r < renderers.Count; r++)
            {
                var renderer = renderers[r];
                var keys = renderer.Keys;
                var row = result[r] = new ShapeKeyLiveness[keys.Length];
                for (var k = 0; k < keys.Length; k++)
                {
                    byKey.TryGetValue((renderer.Path, keys[k].Name), out var curves);
                    row[k] = ClassifyKey(keys[k], curves, drivenSet.Contains((renderer.Path, keys[k].Name)));
                }
            }
            return result;
        }

        public static ShapeKeyLiveness ClassifyKey(ShapeKeyInput key, IEnumerable<float[]> curves, bool drivenToFull)
        {
            var full = key.FullWeight > 0f ? key.FullWeight : 100f;
            // The key's reachable weights: where the renderer holds it, where anything drives it.
            var extreme = key.Weight;
            var live = false;
            if (drivenToFull)
            {
                live = true;
                if (Mathf.Abs(full) > Mathf.Abs(extreme)) extreme = full;
            }
            if (curves != null)
            {
                foreach (var values in curves)
                foreach (var v in values)
                {
                    if (Mathf.Abs(v - key.Weight) <= HoldTolerance) continue;
                    live = true;
                    if (Mathf.Abs(v) > Mathf.Abs(extreme)) extreme = v;
                }
            }

            if (live) return ShapeKeyLiveness.Live(extreme / full);
            if (Mathf.Abs(key.Weight) > HoldTolerance) return ShapeKeyLiveness.Frozen(key.Weight);
            return ShapeKeyLiveness.Dead;
        }

        /// <summary>
        /// The values a curve takes: its keys, and samples between them, because a curve's tangents can carry it
        /// past both of the keys it runs between.
        /// </summary>
        public static float[] CurveValues(AnimationCurve curve, int samplesPerSegment = 8)
        {
            var keys = curve.keys;
            if (keys.Length == 0) return Array.Empty<float>();
            var values = new List<float>(keys.Length * (samplesPerSegment + 1));
            for (var i = 0; i < keys.Length; i++)
            {
                values.Add(keys[i].value);
                if (i + 1 == keys.Length) break;
                var t0 = keys[i].time;
                var t1 = keys[i + 1].time;
                for (var s = 1; s < samplesPerSegment; s++)
                    values.Add(curve.Evaluate(Mathf.Lerp(t0, t1, s / (float)samplesPerSegment)));
            }
            return values.ToArray();
        }
    }
}
