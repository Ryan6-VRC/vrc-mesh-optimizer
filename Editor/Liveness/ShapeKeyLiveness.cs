using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Liveness
{
    internal enum ShapeKeyState : byte
    {
        /// <summary>Zero on the renderer, within <see cref="ShapeKeyClassifier.HoldTolerance"/>, and driven by nothing: it never deforms the mesh at runtime.</summary>
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
    internal readonly struct ShapeKeyLiveness : IEquatable<ShapeKeyLiveness>
    {
        public readonly ShapeKeyState State;
        /// <summary>
        /// The weight the renderer holds the key at as the avatar is built, in the renderer's units (0 to 100 for a usual
        /// key): the weight a frozen key keeps forever and a live key starts from. Zero for a dead key.
        /// </summary>
        public readonly float HeldWeight;
        /// <summary>For a live key, the lowest weight it can take, as a fraction of <see cref="ShapeKeyClassifier.FullScale"/>.</summary>
        public readonly float DrivenMin;
        /// <summary>For a live key, the highest weight it can take, as a fraction of <see cref="ShapeKeyClassifier.FullScale"/>.</summary>
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

        /// <summary>A live key held at zero and driven as far as <paramref name="driven"/>, a fraction of its full scale, of either sign.</summary>
        public static ShapeKeyLiveness Live(float driven) => Live(Math.Min(0f, driven), Math.Max(0f, driven), 0f);

        /// <summary>
        /// A live key held at <paramref name="heldWeight"/>, in the renderer's units, reaching from <paramref name="drivenMin"/>
        /// to <paramref name="drivenMax"/>, fractions of its full scale.
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
        /// <summary>The renderer's own transform; null when not known, and then only <see cref="Path"/> says what binds it.</summary>
        public readonly Transform Transform;

        public RendererShapeKeys(string path, ShapeKeyInput[] keys, Transform transform = null)
        {
            Path = path;
            Keys = keys;
            Transform = transform;
        }

        /// <summary>
        /// Whether a curve, or a driver, bound at <paramref name="path"/> to <paramref name="target"/> reaches this renderer.
        /// Unity binds a path to the first child of each name along it and to nothing else, so where two siblings share a
        /// name, the path names only the first: when both transforms are known, the target must be this renderer's own.
        /// </summary>
        public bool IsBoundBy(string path, Transform target) =>
            target != null && Transform != null ? target == Transform : path == Path;
    }

    /// <summary>
    /// One animation curve on a shape key: the renderer's path from the avatar root, the key's name, the values the
    /// curve takes as <see cref="Animators.AnimatedCurves.CurveValues"/> reads them, and, for linking, the clip it is in and
    /// the times of its keys.
    /// </summary>
    internal readonly struct ShapeKeyBinding
    {
        public readonly string Path;
        public readonly string Key;
        public readonly float[] Values;
        /// <summary>The times of the curve's keys, which with <see cref="Values"/> say what the curve is. Null when unknown.</summary>
        public readonly float[] Times;
        /// <summary>
        /// The clip object the curve is in, by identity: two curves in one clip always play together. Null when unknown,
        /// and a key a curve of unknown clip drives never links.
        /// </summary>
        public readonly object Clip;
        /// <summary>The layer that plays the curve, by identity. Null when unknown: the curve is then a layer of its own, of unknown order.</summary>
        public readonly object Layer;
        /// <summary>Where <see cref="Layer"/> plays among the avatar's layers, later overriding earlier; null when not known.</summary>
        public readonly long? Order;

        /// <summary>An avatar mask on <see cref="Layer"/> may keep the curve from the key; such a key never links.</summary>
        public readonly bool Masked;

        /// <summary>The transform the curve's path binds, as Unity resolves it; null when not known.</summary>
        public readonly Transform Target;

        public ShapeKeyBinding(string path, string key, float[] values, float[] times = null, object clip = null, object layer = null, long? order = null, bool masked = false,
            Transform target = null)
        {
            Target = target;
            Masked = masked;
            Layer = layer;
            Order = order;
            Path = path;
            Key = key;
            Values = values;
            Times = times;
            Clip = clip;
        }
    }

    /// <summary>
    /// A shape key something outside the animators drives to its full weight: the descriptor's visemes,
    /// jaw flap, eyelids and face tracking, or a world's MMD dance.
    /// </summary>
    internal readonly struct DrivenKey
    {
        public readonly string Path;
        public readonly string Key;
        /// <summary>The renderer's transform the driver names; null when not known.</summary>
        public readonly Transform Target;

        public DrivenKey(string path, string key, Transform target = null)
        {
            Path = path;
            Key = key;
            Target = target;
        }
    }

    /// <summary>Decides each shape key's <see cref="ShapeKeyState"/> from the curves and drivers that reach it.</summary>
    internal static class ShapeKeyClassifier
    {
        /// <summary>
        /// A curve value within this many weight units of the renderer's own weight only holds the key where
        /// it already is, which is not driving it. The defaults layers MA and VRCFury write are such curves.
        /// </summary>
        public const float HoldTolerance = 1e-3f;

        /// <summary>
        /// The weight a live key's driven range is a fraction of: the weight of its last frame, or 100 when that frame weighs
        /// nothing or less. Whoever reads the range back into weights reads it against this same scale.
        /// </summary>
        public static float FullScale(float lastFrameWeight) => lastFrameWeight > 0f ? lastFrameWeight : 100f;

        /// <summary>Classifies every key of every renderer. The result is indexed like the input.</summary>
        public static ShapeKeyLiveness[][] Classify(
            IReadOnlyList<RendererShapeKeys> renderers,
            IEnumerable<ShapeKeyBinding> bindings,
            IEnumerable<DrivenKey> driven)
        {
            var byKey = new Dictionary<(string, string), List<ShapeKeyBinding>>();
            foreach (var b in bindings)
            {
                if (b.Values == null || b.Values.Length == 0) continue;
                if (!byKey.TryGetValue((b.Path, b.Key), out var list)) byKey[(b.Path, b.Key)] = list = new List<ShapeKeyBinding>();
                list.Add(b);
            }
            var drivenByKey = new Dictionary<(string, string), List<DrivenKey>>();
            foreach (var d in driven)
            {
                if (!drivenByKey.TryGetValue((d.Path, d.Key), out var list)) drivenByKey[(d.Path, d.Key)] = list = new List<DrivenKey>();
                list.Add(d);
            }

            var result = new ShapeKeyLiveness[renderers.Count][];
            for (var r = 0; r < renderers.Count; r++)
            {
                var renderer = renderers[r];
                var keys = renderer.Keys;
                var row = result[r] = new ShapeKeyLiveness[keys.Length];
                for (var k = 0; k < keys.Length; k++)
                {
                    // A curve counts only on the renderer its path binds (RendererShapeKeys.IsBoundBy).
                    byKey.TryGetValue((renderer.Path, keys[k].Name), out var found);
                    var curves = found?.Where(b => renderer.IsBoundBy(b.Path, b.Target)).Select(b => b.Values).ToList();
                    var drives = drivenByKey.TryGetValue((renderer.Path, keys[k].Name), out var drivers) && drivers.Any(d => renderer.IsBoundBy(d.Path, d.Target));
                    row[k] = ClassifyKey(keys[k], curves, drives);
                }
            }
            return result;
        }

        public static ShapeKeyLiveness ClassifyKey(ShapeKeyInput key, IEnumerable<float[]> curves, bool drivenToFull)
        {
            var full = FullScale(key.FullWeight);
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
    }
}
