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

    /// <summary>
    /// Why a shape key driven by the same clips as another key moves alone: the first reason found. The last two are the
    /// pipeline's, for a link it found and could not evaluate as one.
    /// </summary>
    public enum LinkRefusal
    {
        /// <summary>The descriptor (visemes, jaw flap, eyelids) or an MMD world drives it without a clip.</summary>
        DrivenOutsideClips,
        /// <summary>A curve on it is in a clip that cannot be told apart from others.</summary>
        UnknownClip,
        /// <summary>An avatar mask on a layer that plays it may keep a curve from it.</summary>
        Masked,
        /// <summary>Another renderer shares its renderer's path, and a curve does not say which of them it binds.</summary>
        SharedPath,
        /// <summary>One clip binds it with two different curves.</summary>
        TwoCurvesInOneClip,
        /// <summary>A layer that only holds it plays after a layer that drives it, or at a place not known.</summary>
        HoldAfterDriver,
        /// <summary>The keys its clips also drive are driven by other curves, bound differently in a driving layer, or held at another weight.</summary>
        Mismatched,
        /// <summary>The rest of its link is on renderers the pass does not evaluate, or is not evaluated as live.</summary>
        NotEvaluated,
        /// <summary>Its link's members no longer stand at one normalised weight.</summary>
        HeldApart,
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
    /// keys and samples between them both, as <see cref="Animators.AnimatedCurves.CurveValues"/> reads them, since tangents
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
    /// links, nor does one bound by a curve of unknown clip or through a layer whose avatar mask may keep the curve from
    /// it, one a clip binds with two different curves, or one on a renderer whose path another renderer shares when the curve does not say which transform it binds; a curve counts only on the renderer its path binds (<see cref="RendererShapeKeys.IsBoundBy"/>). A wrong
    /// link evaluates a state the avatar can leave, so every doubt keeps keys apart.
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
            IEnumerable<DrivenKey> driven) => Find(renderers, bindings, driven, out _);

        /// <summary>
        /// As the other overload, and in <paramref name="refused"/> every key that links with no other though the same set
        /// of clips drives it and another key, with the first reason found against it. A key that moves alone only because
        /// its partner was refused is not counted again.
        /// </summary>
        public static List<KeyRef[]> Find(IReadOnlyList<RendererShapeKeys> renderers, IEnumerable<ShapeKeyBinding> bindings,
            IEnumerable<DrivenKey> driven, out Dictionary<KeyRef, LinkRefusal> refused)
        {
            var byPath = new Dictionary<string, List<int>>();
            for (var r = 0; r < renderers.Count; r++)
            {
                if (!byPath.TryGetValue(renderers[r].Path, out var atPath)) byPath[renderers[r].Path] = atPath = new List<int>();
                atPath.Add(r);
            }

            var unlinkable = new HashSet<KeyRef>();
            var reasons = new Dictionary<KeyRef, LinkRefusal>();
            void Refuse(KeyRef k, LinkRefusal why)
            {
                unlinkable.Add(k);
                if (!reasons.ContainsKey(k)) reasons[k] = why;
            }
            // Every clip that drives a key, whatever refuses it, to tell which keys the same clips drive.
            var drivers = new Dictionary<KeyRef, HashSet<object>>();

            foreach (var d in driven)
                if (Resolve(renderers, byPath, d.Path, d.Target, d.Key, out var key, out _)) Refuse(key, LinkRefusal.DrivenOutsideClips);

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
                if (!Resolve(renderers, byPath, b.Path, b.Target, b.Key, out var key, out var ambiguous)) continue;
                var input = renderers[key.Renderer].Keys[key.Key];
                var drives = Drives(b.Values, input.Weight);
                if (drives && b.Clip != null)
                {
                    if (!drivers.TryGetValue(key, out var clips)) drivers[key] = clips = new HashSet<object>(ReferenceComparer.Instance);
                    clips.Add(b.Clip);
                }
                if (b.Clip == null || b.Times == null || b.Masked || ambiguous)
                {
                    Refuse(key, b.Clip == null || b.Times == null ? LinkRefusal.UnknownClip : b.Masked ? LinkRefusal.Masked : LinkRefusal.SharedPath);
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
                else if (!Same(same, curve, 0f)) Refuse(key, LinkRefusal.TwoCurvesInOneClip);

                if (!drives) continue;
                if (!curves.TryGetValue(key, out var list)) curves[key] = list = new List<Curve>();
                same = list.FirstOrDefault(c => c.Clip == clip);
                if (same == null) list.Add(curve);
                else if (!Same(same, curve, 0f)) Refuse(key, LinkRefusal.TwoCurvesInOneClip);
            }

            // Keys driven by the same set of clips, then within each set, groups whose every pair matches.
            var buckets = new Dictionary<string, List<KeyRef>>();
            foreach (var kv in curves.OrderBy(kv => kv.Key.Renderer).ThenBy(kv => kv.Key.Key))
            {
                if (unlinkable.Contains(kv.Key)) continue;
                if (!HoldsPlayFirst(layers[kv.Key]))
                {
                    reasons[kv.Key] = LinkRefusal.HoldAfterDriver;
                    continue;
                }
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
                if (bucket.Count > 1)
                    foreach (var g in open.Where(g => g.Count == 1)) reasons[g[0]] = LinkRefusal.Mismatched;
            }
            groups.Sort((a, b) => a[0].Renderer != b[0].Renderer ? a[0].Renderer.CompareTo(b[0].Renderer) : a[0].Key.CompareTo(b[0].Key));

            refused = new Dictionary<KeyRef, LinkRefusal>();
            var grouped = new HashSet<KeyRef>(groups.SelectMany(g => g));
            var clipIds = new Dictionary<object, int>(ReferenceComparer.Instance);
            string SetOf(HashSet<object> clips) => string.Join(",", clips.Select(c =>
            {
                if (!clipIds.TryGetValue(c, out var id)) clipIds[c] = id = clipIds.Count;
                return id;
            }).OrderBy(id => id));
            foreach (var set in drivers.GroupBy(kv => SetOf(kv.Value)))
            {
                if (set.Count() < 2) continue;
                foreach (var kv in set)
                    if (!grouped.Contains(kv.Key) && reasons.TryGetValue(kv.Key, out var why)) refused[kv.Key] = why;
            }
            return groups;
        }

        /// <summary>
        /// The key a binding names: on the renderer its path binds (<see cref="RendererShapeKeys.IsBoundBy"/>), by a name
        /// unique on it. <paramref name="ambiguous"/> when the binding's target is not known and another renderer shares the
        /// path, so which of them it drives cannot be said.
        /// </summary>
        static bool Resolve(IReadOnlyList<RendererShapeKeys> renderers, Dictionary<string, List<int>> byPath, string path, Transform target, string name,
            out KeyRef key, out bool ambiguous)
        {
            key = default;
            ambiguous = false;
            if (!byPath.TryGetValue(path, out var atPath)) return false;
            var bound = atPath.Where(i => renderers[i].IsBoundBy(path, target)).ToList();
            if (bound.Count == 0) return false;
            ambiguous = bound.Count > 1;
            var r = bound[0];
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
