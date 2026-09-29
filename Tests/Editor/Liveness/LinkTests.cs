using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Liveness
{
    // A link is a promise that two keys never stand apart, and occlusion then never evaluates them apart. A wrong link
    // can hide a triangle that shows when one moves without the other; a missed one only keeps triangles. So each way a
    // pair can be driven apart is a refusal pinned here, next to the situations that must still link.
    public class LinkTests
    {
        /// <summary>A clip by identity, as NDMF's virtual clips are; its name is only for reading a failure.</summary>
        sealed class Clip
        {
            readonly string _name;
            public Clip(string name) => _name = name;
            public override string ToString() => _name;
        }

        static readonly Clip Heels = new Clip("heels");
        static readonly Clip Hips = new Clip("hips");
        static readonly Clip Sweater = new Clip("sweater");

        static ShapeKeyInput Key(string name, float weight = 0f, float fullWeight = 100f) => new ShapeKeyInput(name, weight, fullWeight);

        static RendererShapeKeys Renderer(string path, params ShapeKeyInput[] keys) => new RendererShapeKeys(path, keys);

        /// <summary>A curve in <paramref name="clip"/> through <paramref name="values"/> at times 0, 1, 2 and so on, read as the sources read one.</summary>
        static ShapeKeyBinding Curve(Clip clip, string path, string key, params float[] values)
        {
            var curve = new AnimationCurve(values.Select((v, i) => new Keyframe(i, v)).ToArray());
            return Binding(clip, path, key, curve);
        }

        static ShapeKeyBinding Binding(Clip clip, string path, string key, AnimationCurve curve) =>
            new ShapeKeyBinding(path, key, ShapeKeyClassifier.CurveValues(curve), curve.keys.Select(k => k.time).ToArray(), clip);

        static List<KeyRef[]> Find(RendererShapeKeys[] renderers, IEnumerable<ShapeKeyBinding> bindings, params DrivenKey[] driven) =>
            ShapeKeyLinks.Find(renderers, bindings.ToList(), driven);

        static readonly RendererShapeKeys[] BodyAndTights =
        {
            Renderer("Body", Key("Toe_heels", 100f), Key("Hip_big")),
            Renderer("Tights", Key("Hip_big"), Key("Toe_heels", 100f)),
        };

        static KeyRef[] BothHeels => new[] { new KeyRef(0, 0), new KeyRef(1, 1) };

        // ------------------------------------------------------------------------------------------------
        // What links.

        [Test]
        public void OneClipDrivingBothKeysWithTheSameCurve_LinksThem()
        {
            var links = Find(BodyAndTights, new[] { Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f) });

            Assert.That(links, Has.Count.EqualTo(1));
            Assert.That(links[0], Is.EqualTo(BothHeels));
        }

        [Test]
        public void CurvesEqualOnlyAfterNormalisingByEachKeysFullWeight_Link()
        {
            // The body's key is authored to full at 100, the tights' at 1: 60 and 0.6 are the same fraction of each.
            var renderers = new[] { Renderer("Body", Key("Hip_big")), Renderer("Tights", Key("Hip_big", 0f, fullWeight: 1f)) };
            var links = Find(renderers, new[] { Curve(Hips, "Body", "Hip_big", 0f, 60f), Curve(Hips, "Tights", "Hip_big", 0f, 0.6f) });

            Assert.That(links, Has.Count.EqualTo(1));
        }

        [Test]
        public void AHoldCurveOnOneSideOnly_TakesNoPart_AndTheKeysLink()
        {
            // The sweater's toggle holds the tights' heel where the renderer holds it, and has no curve for the body's.
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(Sweater, "Tights", "Toe_heels", 100f, 100f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.EqualTo(new[] { BothHeels }));
        }

        [Test]
        public void ThreeKeys_OneClip_AreOneGroup()
        {
            var renderers = new[] { Renderer("Body", Key("Hip_big")), Renderer("Tights", Key("Hip_big")), Renderer("Shorts", Key("x"), Key("Hip_big")) };
            var links = Find(renderers, renderers.Select(r => Curve(Hips, r.Path, "Hip_big", 0f, 100f)));

            Assert.That(links, Is.EqualTo(new[] { new[] { new KeyRef(0, 0), new KeyRef(1, 0), new KeyRef(2, 1) } }));
        }

        [Test]
        public void TwoClipsEachDrivingTheirOwnPair_AreTwoGroups()
        {
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(Hips, "Body", "Hip_big", 0f, 100f), Curve(Hips, "Tights", "Hip_big", 0f, 100f),
            };
            var links = Find(BodyAndTights, bindings);

            Assert.That(links, Is.EqualTo(new[] { BothHeels, new[] { new KeyRef(0, 1), new KeyRef(1, 0) } }));
        }

        [Test]
        public void TwoKeysOnOneRenderer_DrivenTogether_Link()
        {
            var renderers = new[] { Renderer("Body", Key("Breasts_big"), Key("Breasts_big_side")) };
            var links = Find(renderers, new[] { Curve(Hips, "Body", "Breasts_big", 0f, 100f), Curve(Hips, "Body", "Breasts_big_side", 0f, 100f) });

            Assert.That(links, Is.EqualTo(new[] { new[] { new KeyRef(0, 0), new KeyRef(0, 1) } }));
        }

        // ------------------------------------------------------------------------------------------------
        // What refuses, one way to drive a pair apart each.

        [Test]
        public void AClipThatDrivesOneKeyAndNotTheOther_Refuses()
        {
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(Sweater, "Tights", "Toe_heels", 100f, 50f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void ASharedClipWithDifferentCurves_Refuses()
        {
            var bindings = new[] { Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 10f) };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void TheSameValuesAtDifferentTimes_Refuse()
        {
            var body = new AnimationCurve(new Keyframe(0f, 100f), new Keyframe(1f, 0f));
            var tights = new AnimationCurve(new Keyframe(0f, 100f), new Keyframe(2f, 0f));
            var bindings = new[] { Binding(Heels, "Body", "Toe_heels", body), Binding(Heels, "Tights", "Toe_heels", tights) };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void TheSameKeysHeldAtDifferentWeights_Refuse()
        {
            var renderers = new[] { Renderer("Body", Key("Hip_big", 0f)), Renderer("Tights", Key("Hip_big", 30f)) };
            var links = Find(renderers, new[] { Curve(Hips, "Body", "Hip_big", 0f, 100f), Curve(Hips, "Tights", "Hip_big", 0f, 100f) });

            Assert.That(links, Is.Empty);
        }

        [Test]
        public void TheSameKeysWithTangentsThatSampleDifferently_Refuse()
        {
            // Both run from 100 at 0 to 0 at 1; the tights' tangents carry it past 100 on the way.
            var body = new AnimationCurve(new Keyframe(0f, 100f, 0f, 0f), new Keyframe(1f, 0f, 0f, 0f));
            var tights = new AnimationCurve(new Keyframe(0f, 100f, 0f, 400f), new Keyframe(1f, 0f, 0f, 0f));
            var bindings = new[] { Binding(Heels, "Body", "Toe_heels", body), Binding(Heels, "Tights", "Toe_heels", tights) };

            Assert.That(ShapeKeyClassifier.CurveValues(body), Is.Not.EqualTo(ShapeKeyClassifier.CurveValues(tights)), "control: the samples differ");
            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void TheSameKeysWithTheSameTangents_Link()
        {
            AnimationCurve Eased() => new AnimationCurve(new Keyframe(0f, 100f, 0f, 400f), new Keyframe(1f, 0f, 0f, 0f));
            var bindings = new[] { Binding(Heels, "Body", "Toe_heels", Eased()), Binding(Heels, "Tights", "Toe_heels", Eased()) };

            Assert.That(Find(BodyAndTights, bindings), Is.EqualTo(new[] { BothHeels }));
        }

        [Test]
        public void AKeyTheDescriptorAlsoDrives_Refuses()
        {
            var bindings = new[] { Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f) };

            Assert.That(Find(BodyAndTights, bindings, new DrivenKey("Body", "Toe_heels")), Is.Empty);
        }

        [Test]
        public void ClipsAreTheirObjects_NotTheirNames()
        {
            // Two clips both named "heels", one for each key: they can play apart.
            var bindings = new[] { Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(new Clip("heels"), "Tights", "Toe_heels", 100f, 0f) };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void ACurveOfUnknownClip_NeverLinks()
        {
            var bindings = new[] { new ShapeKeyBinding("Body", "Toe_heels", new[] { 0f }), new ShapeKeyBinding("Tights", "Toe_heels", new[] { 0f }) };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void AClipDrivingOneKeyWithTwoCurves_Refuses()
        {
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Body", "Toe_heels", 100f, 50f),
                Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void OneClipReachedTwice_IsOneClip()
        {
            // The same clip in two controllers is read once from each, with the same curves.
            var bindings = new[] { Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f) };

            Assert.That(Find(BodyAndTights, bindings.Concat(bindings)), Is.EqualTo(new[] { BothHeels }));
        }

        [Test]
        public void RenderersSharingAPath_NeverLink()
        {
            // Two renderers named alike at one path: a binding cannot say which it drives.
            var renderers = new[] { Renderer("Body", Key("Hip_big")), Renderer("Tights", Key("Hip_big")), Renderer("Tights", Key("Hip_big")) };
            var links = Find(renderers, new[] { Curve(Hips, "Body", "Hip_big", 0f, 100f), Curve(Hips, "Tights", "Hip_big", 0f, 100f) });

            Assert.That(links, Is.Empty);
        }

        [Test]
        public void EveryMemberOfAGroup_MatchesEveryOther()
        {
            // B and C each match A within the tolerance and not each other: A takes one of them, never both.
            var renderers = new[] { Renderer("A", Key("k")), Renderer("B", Key("k")), Renderer("C", Key("k")) };
            var step = 0.8f * ShapeKeyLinks.Tolerance * 100f;
            var bindings = new[] { Curve(Hips, "A", "k", 0f, 50f), Curve(Hips, "B", "k", 0f, 50f + step), Curve(Hips, "C", "k", 0f, 50f - step) };
            var links = Find(renderers, bindings);

            Assert.That(links, Is.EqualTo(new[] { new[] { new KeyRef(0, 0), new KeyRef(1, 0) } }));
        }

        [Test]
        public void KeysNoClipDrives_NeverLink()
        {
            // Both only held, by the same defaults clip: neither is driven, so neither is live.
            var bindings = new[] { Curve(Heels, "Body", "Toe_heels", 100f), Curve(Heels, "Tights", "Toe_heels", 100f) };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }
    }
}
