using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Liveness
{
    /// <summary>One shape key on one renderer, a member of a link.</summary>
    internal readonly struct LinkedKey
    {
        public readonly SkinnedMeshRenderer Renderer;
        public readonly int Key;

        public LinkedKey(SkinnedMeshRenderer renderer, int key)
        {
            Renderer = renderer;
            Key = key;
        }
    }

    /// <summary>A shape key by its renderer's index in the input and its index among that renderer's keys.</summary>
    internal readonly struct KeyRef : IEquatable<KeyRef>
    {
        public readonly int Renderer;
        public readonly int Key;

        public KeyRef(int renderer, int key)
        {
            Renderer = renderer;
            Key = key;
        }

        public bool Equals(KeyRef other) => Renderer == other.Renderer && Key == other.Key;
        public override bool Equals(object obj) => obj is KeyRef other && Equals(other);
        public override int GetHashCode() => Renderer * 397 ^ Key;
        public override string ToString() => $"({Renderer}, {Key})";
    }

    /// <summary>
    /// Shape keys that cannot be driven apart. Two keys link only when every clip that drives either drives both with the
    /// same curve, normalised by each key's <see cref="ShapeKeyClassifier.FullScale"/>, and their renderers hold them at
    /// the same normalised weight; then wherever one stands, the other stands at the same fraction of its own scale.
    ///
    /// Driving is the classifier's: a curve drives a key when it leaves the weight the renderer holds by more than
    /// <see cref="ShapeKeyClassifier.HoldTolerance"/>. A curve that only holds a key where it already is takes no part, so
    /// the defaults Modular Avatar and VRCFury write for every animated key neither link nor split anything. Same curve
    /// means the same key times and the same normalised values, keys and samples between them both, as
    /// <see cref="ShapeKeyClassifier.CurveValues"/> reads them, since tangents carry a curve between its keys. A clip is
    /// the object, never its name.
    ///
    /// A key driven by anything outside the clips (the descriptor's visemes, jaw flap and eyelids, the MMD names) never
    /// links, nor does one a curve of unknown clip drives, one a clip drives with two curves, or one on a renderer whose
    /// path another renderer shares. A wrong link evaluates a state the avatar can leave, so every doubt keeps keys apart.
    /// </summary>
    internal static class ShapeKeyLinks
    {
        /// <summary>Two normalised values this close are the same: a hold's tolerance on a key whose full weight is 100.</summary>
        public const float Tolerance = ShapeKeyClassifier.HoldTolerance / 100f;

        /// <summary>One driving curve, normalised.</summary>
        sealed class Curve
        {
            public int Clip;
            public float[] Times;
            public float[] Values;
        }

        /// <summary>
        /// Every group of two or more keys that link, each group's members ascending, groups by their first member. Every
        /// member of a group links with every other.
        /// </summary>
        public static List<KeyRef[]> Find(IReadOnlyList<RendererShapeKeys> renderers, IEnumerable<ShapeKeyBinding> bindings,
            IEnumerable<DrivenKey> driven)
        {
            var byPath = new Dictionary<string, int>();
            var sharedPath = new HashSet<string>();
            for (var r = 0; r < renderers.Count; r++)
                if (!byPath.ContainsKey(renderers[r].Path)) byPath[renderers[r].Path] = r;
                else sharedPath.Add(renderers[r].Path);

            var unlinkable = new HashSet<KeyRef>();
            foreach (var d in driven)
                if (Resolve(renderers, byPath, d.Path, d.Key, out var key)) unlinkable.Add(key);

            var clipIds = new Dictionary<object, int>(ReferenceComparer.Instance);
            var curves = new Dictionary<KeyRef, List<Curve>>();
            foreach (var b in bindings)
            {
                if (b.Values == null || b.Values.Length == 0) continue;
                if (!Resolve(renderers, byPath, b.Path, b.Key, out var key)) continue;
                var input = renderers[key.Renderer].Keys[key.Key];
                if (!Drives(b.Values, input.Weight)) continue;
                if (b.Clip == null || b.Times == null || sharedPath.Contains(b.Path))
                {
                    unlinkable.Add(key);
                    continue;
                }
                if (!clipIds.TryGetValue(b.Clip, out var clip)) clipIds[b.Clip] = clip = clipIds.Count;
                if (!curves.TryGetValue(key, out var list)) curves[key] = list = new List<Curve>();
                if (list.Any(c => c.Clip == clip)) unlinkable.Add(key);
                var full = ShapeKeyClassifier.FullScale(input.FullWeight);
                list.Add(new Curve { Clip = clip, Times = b.Times, Values = b.Values.Select(v => v / full).ToArray() });
            }

            // Keys driven by the same set of clips, then within each set, groups whose every pair matches.
            var buckets = new Dictionary<string, List<KeyRef>>();
            foreach (var kv in curves.OrderBy(kv => kv.Key.Renderer).ThenBy(kv => kv.Key.Key))
            {
                if (unlinkable.Contains(kv.Key)) continue;
                kv.Value.Sort((a, b) => a.Clip.CompareTo(b.Clip));
                var set = string.Join(",", kv.Value.Select(c => c.Clip));
                if (!buckets.TryGetValue(set, out var bucket)) buckets[set] = bucket = new List<KeyRef>();
                bucket.Add(kv.Key);
            }
            var groups = new List<KeyRef[]>();
            foreach (var bucket in buckets.Values)
            {
                var open = new List<List<KeyRef>>();
                foreach (var key in bucket)
                {
                    var home = open.FirstOrDefault(g => g.All(m => Match(renderers, curves, key, m)));
                    if (home != null) home.Add(key);
                    else open.Add(new List<KeyRef> { key });
                }
                groups.AddRange(open.Where(g => g.Count > 1).Select(g => g.ToArray()));
            }
            groups.Sort((a, b) => a[0].Renderer != b[0].Renderer ? a[0].Renderer.CompareTo(b[0].Renderer) : a[0].Key.CompareTo(b[0].Key));
            return groups;
        }

        /// <summary>The key a path and name name, unless the renderer's path is not its own or the name is not unique on it.</summary>
        static bool Resolve(IReadOnlyList<RendererShapeKeys> renderers, Dictionary<string, int> byPath, string path, string name, out KeyRef key)
        {
            key = default;
            if (!byPath.TryGetValue(path, out var r)) return false;
            var keys = renderers[r].Keys;
            var found = -1;
            for (var k = 0; k < keys.Length; k++)
            {
                if (keys[k].Name != name) continue;
                if (found >= 0) return false;
                found = k;
            }
            if (found < 0) return false;
            key = new KeyRef(r, found);
            return true;
        }

        static bool Drives(float[] values, float held)
        {
            foreach (var v in values)
                if (Mathf.Abs(v - held) > ShapeKeyClassifier.HoldTolerance) return true;
            return false;
        }

        /// <summary>Both keys held at the same normalised weight, and in each clip, curves at the same times through the same normalised values.</summary>
        static bool Match(IReadOnlyList<RendererShapeKeys> renderers, Dictionary<KeyRef, List<Curve>> curves, KeyRef a, KeyRef b)
        {
            var ka = renderers[a.Renderer].Keys[a.Key];
            var kb = renderers[b.Renderer].Keys[b.Key];
            if (Mathf.Abs(ka.Weight / ShapeKeyClassifier.FullScale(ka.FullWeight) - kb.Weight / ShapeKeyClassifier.FullScale(kb.FullWeight)) > Tolerance) return false;
            var ca = curves[a];
            var cb = curves[b];
            if (ca.Count != cb.Count) return false;
            for (var i = 0; i < ca.Count; i++)
            {
                if (ca[i].Clip != cb[i].Clip || !ca[i].Times.SequenceEqual(cb[i].Times) || ca[i].Values.Length != cb[i].Values.Length) return false;
                for (var j = 0; j < ca[i].Values.Length; j++)
                    if (Mathf.Abs(ca[i].Values[j] - cb[i].Values[j]) > Tolerance) return false;
            }
            return true;
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
