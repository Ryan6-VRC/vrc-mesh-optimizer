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
    /// <see cref="ShapeKeyClassifier.HoldTolerance"/>. Same curve means the same key times and the same normalised values,
    /// keys and samples between them both, as <see cref="ShapeKeyClassifier.CurveValues"/> reads them, since tangents
    /// carry a curve between its keys. A clip is the object, never its name.
    ///
    /// A curve that only holds a key where it already is can still stand keys apart, through the layer that plays it. A
    /// later layer overrides an earlier one, so a layer that only holds one member pins it while an earlier layer drives
    /// the rest; and with write defaults off, a state of a driving layer that holds one member resets it alone. So:
    /// <list type="bullet">
    /// <item>In a layer that drives either key, every clip that binds either binds both with the same normalised curve,
    /// hold or not.</item>
    /// <item>A layer that binds a key with holds alone takes no part only when it plays before every layer that drives
    /// that key; one that plays after a driver, or whose place is not known, refuses the link.</item>
    /// </list>
    /// The defaults Modular Avatar and VRCFury write for every animated key sit in layers before the layers that drive it,
    /// or hold every member alike inside them.
    ///
    /// A key driven by anything outside the clips (the descriptor's visemes, jaw flap and eyelids, the MMD names) never
    /// links, nor does one bound by a curve of unknown clip, one a clip binds with two different curves, or one on a
    /// renderer whose path another renderer shares. A wrong link evaluates a state the avatar can leave, so every doubt
    /// keeps keys apart.
    /// </summary>
    internal static class ShapeKeyLinks
    {
        /// <summary>Two normalised values this close are the same: a hold's tolerance on a key whose full weight is 100.</summary>
        public const float Tolerance = ShapeKeyClassifier.HoldTolerance / 100f;

        /// <summary>One curve on a key, normalised.</summary>
        sealed class Curve
        {
            public int Clip;
            public float[] Times;
            public float[] Values;
        }

        /// <summary>How one layer binds one key: every clip's curve on it, and whether any of them drives it.</summary>
        sealed class LayerUse
        {
            public long? Order;
            public bool Drives;
            public readonly Dictionary<int, Curve> Curves = new Dictionary<int, Curve>();
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

            var ids = new Dictionary<object, int>(ReferenceComparer.Instance);
            int Id(object o)
            {
                if (!ids.TryGetValue(o, out var id)) ids[o] = id = ids.Count;
                return id;
            }
            var curves = new Dictionary<KeyRef, List<Curve>>();
            var layers = new Dictionary<KeyRef, Dictionary<int, LayerUse>>();
            foreach (var b in bindings)
            {
                if (b.Values == null || b.Values.Length == 0) continue;
                if (!Resolve(renderers, byPath, b.Path, b.Key, out var key)) continue;
                var input = renderers[key.Renderer].Keys[key.Key];
                var drives = Drives(b.Values, input.Weight);
                if (b.Clip == null || b.Times == null || sharedPath.Contains(b.Path))
                {
                    unlinkable.Add(key);
                    continue;
                }
                var clip = Id(b.Clip);
                var full = ShapeKeyClassifier.FullScale(input.FullWeight);
                var curve = new Curve { Clip = clip, Times = b.Times, Values = b.Values.Select(v => v / full).ToArray() };

                // A layer of unknown identity is a layer of its own, of unknown order.
                var layer = Id(b.Layer ?? new object());
                if (!layers.TryGetValue(key, out var uses)) layers[key] = uses = new Dictionary<int, LayerUse>();
                if (!uses.TryGetValue(layer, out var use)) uses[layer] = use = new LayerUse { Order = b.Order };
                use.Drives |= drives;
                // One clip reached twice gives the same curve again; a different curve from the same clip on the same key is
                // two bindings resolving to one renderer, and no one can say which plays.
                if (!use.Curves.TryGetValue(clip, out var same)) use.Curves[clip] = curve;
                else if (!Same(same, curve, 0f)) unlinkable.Add(key);

                if (!drives) continue;
                if (!curves.TryGetValue(key, out var list)) curves[key] = list = new List<Curve>();
                same = list.FirstOrDefault(c => c.Clip == clip);
                if (same == null) list.Add(curve);
                else if (!Same(same, curve, 0f)) unlinkable.Add(key);
            }

            // Keys driven by the same set of clips, then within each set, groups whose every pair matches.
            var buckets = new Dictionary<string, List<KeyRef>>();
            foreach (var kv in curves.OrderBy(kv => kv.Key.Renderer).ThenBy(kv => kv.Key.Key))
            {
                if (unlinkable.Contains(kv.Key) || !HoldsPlayFirst(layers[kv.Key])) continue;
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
                    var home = open.FirstOrDefault(g => g.All(m => Match(renderers, curves, layers, key, m)));
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

        /// <summary>Every layer that only holds the key plays, at a known place, before every layer that drives it.</summary>
        static bool HoldsPlayFirst(Dictionary<int, LayerUse> uses)
        {
            foreach (var hold in uses.Values)
            {
                if (hold.Drives) continue;
                foreach (var driver in uses.Values)
                    if (driver.Drives && !(hold.Order.HasValue && driver.Order.HasValue && hold.Order.Value < driver.Order.Value)) return false;
            }
            return true;
        }

        static bool Same(Curve a, Curve b, float tolerance)
        {
            if (!a.Times.SequenceEqual(b.Times) || a.Values.Length != b.Values.Length) return false;
            for (var j = 0; j < a.Values.Length; j++)
                if (Mathf.Abs(a.Values[j] - b.Values[j]) > tolerance) return false;
            return true;
        }

        /// <summary>
        /// Both keys held at the same normalised weight; in each clip that drives either, curves at the same times through the
        /// same normalised values; and in each layer that drives either, every clip that binds either binding both alike.
        /// </summary>
        static bool Match(IReadOnlyList<RendererShapeKeys> renderers, Dictionary<KeyRef, List<Curve>> curves,
            Dictionary<KeyRef, Dictionary<int, LayerUse>> layers, KeyRef a, KeyRef b)
        {
            var ka = renderers[a.Renderer].Keys[a.Key];
            var kb = renderers[b.Renderer].Keys[b.Key];
            if (Mathf.Abs(ka.Weight / ShapeKeyClassifier.FullScale(ka.FullWeight) - kb.Weight / ShapeKeyClassifier.FullScale(kb.FullWeight)) > Tolerance) return false;
            var ca = curves[a];
            var cb = curves[b];
            if (ca.Count != cb.Count) return false;
            for (var i = 0; i < ca.Count; i++)
                if (ca[i].Clip != cb[i].Clip || !Same(ca[i], cb[i], Tolerance)) return false;

            var la = layers[a];
            var lb = layers[b];
            var driving = la.Where(kv => kv.Value.Drives).Select(kv => kv.Key).Union(lb.Where(kv => kv.Value.Drives).Select(kv => kv.Key));
            foreach (var layer in driving)
            {
                if (!la.TryGetValue(layer, out var ua) || !lb.TryGetValue(layer, out var ub) || ua.Curves.Count != ub.Curves.Count) return false;
                foreach (var kv in ua.Curves)
                    if (!ub.Curves.TryGetValue(kv.Key, out var other) || !Same(kv.Value, other, Tolerance)) return false;
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
