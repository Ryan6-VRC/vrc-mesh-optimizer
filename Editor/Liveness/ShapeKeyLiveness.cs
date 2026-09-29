using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Liveness
{
    public enum ShapeKeyState : byte
    {
        /// <summary>Zero on the renderer and driven by nothing: it never deforms the mesh at runtime.</summary>
        Dead,
        /// <summary>Nonzero on the renderer and driven by nothing: it holds <see cref="ShapeKeyLiveness.HeldWeight"/> forever.</summary>
        Frozen,
        /// <summary>Something moves it at runtime, anywhere from <see cref="ShapeKeyLiveness.DrivenMin"/> to <see cref="ShapeKeyLiveness.DrivenMax"/>.</summary>
        Live,
    }

    /// <summary>
    /// One shape key as the optimizer evaluates it: whether anything moves it, the weight the avatar stands with it at,
    /// and, when live, how far it can be moved. The held weight is the one place the optimizer reads how a renderer
    /// holds a key.
    /// </summary>
    public readonly struct ShapeKeyLiveness : IEquatable<ShapeKeyLiveness>
    {
        public readonly ShapeKeyState State;
        /// <summary>
        /// The weight the renderer holds the key at as the avatar is built, in the renderer's units (0 to 100 for a usual
        /// key): the weight a frozen key keeps forever and a live key starts from. Zero for a dead key.
        /// </summary>
        public readonly float HeldWeight;
        /// <summary>For a live key, the lowest weight it can take, as a fraction of its full weight (the weight of its last frame).</summary>
        public readonly float DrivenMin;
        /// <summary>For a live key, the highest weight it can take, as a fraction of its full weight.</summary>
        public readonly float DrivenMax;

        ShapeKeyLiveness(ShapeKeyState state, float heldWeight, float drivenMin, float drivenMax)
        {
            State = state;
            HeldWeight = heldWeight;
            DrivenMin = drivenMin;
            DrivenMax = drivenMax;
        }

        public static ShapeKeyLiveness Dead => default;
        public static ShapeKeyLiveness Frozen(float weight) => new ShapeKeyLiveness(ShapeKeyState.Frozen, weight, 0f, 0f);

        /// <summary>A live key held at zero and driven as far as <paramref name="driven"/>, a fraction of its full weight, of either sign.</summary>
        public static ShapeKeyLiveness Live(float driven) => Live(Math.Min(0f, driven), Math.Max(0f, driven), 0f);

        /// <summary>
        /// A live key held at <paramref name="heldWeight"/>, in the renderer's units, reaching from <paramref name="drivenMin"/>
        /// to <paramref name="drivenMax"/>, fractions of its full weight.
        /// </summary>
        public static ShapeKeyLiveness Live(float drivenMin, float drivenMax, float heldWeight) =>
            new ShapeKeyLiveness(ShapeKeyState.Live, heldWeight, drivenMin, drivenMax);

        public bool Equals(ShapeKeyLiveness other) =>
            State == other.State && HeldWeight.Equals(other.HeldWeight) && DrivenMin.Equals(other.DrivenMin) && DrivenMax.Equals(other.DrivenMax);
        public override bool Equals(object obj) => obj is ShapeKeyLiveness other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(State, HeldWeight, DrivenMin, DrivenMax);

        public override string ToString() => State switch
        {
            ShapeKeyState.Frozen => $"Frozen({HeldWeight})",
            ShapeKeyState.Live => $"Live({DrivenMin}..{DrivenMax}, held {HeldWeight})",
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
            // The key's reachable weights: where the renderer holds it, and everywhere anything drives it.
            var lo = key.Weight;
            var hi = key.Weight;
            var live = false;
            if (drivenToFull)
            {
                live = true;
                lo = Mathf.Min(lo, full);
                hi = Mathf.Max(hi, full);
            }
            if (curves != null)
            {
                foreach (var values in curves)
                foreach (var v in values)
                {
                    // Any value away from where the renderer holds the key makes it live, whatever its sign or
                    // direction: a value away from zero, or zero itself when the renderer holds the key elsewhere.
                    // Only a curve that holds the key where it already is leaves it frozen or dead, and a dead key
                    // is one no curve ever moves off zero.
                    if (Mathf.Abs(v - key.Weight) <= HoldTolerance) continue;
                    live = true;
                    lo = Mathf.Min(lo, v);
                    hi = Mathf.Max(hi, v);
                }
            }

            if (live) return ShapeKeyLiveness.Live(lo / full, hi / full, key.Weight);
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
