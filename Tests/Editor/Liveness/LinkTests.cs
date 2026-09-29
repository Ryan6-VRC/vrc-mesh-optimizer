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
        /// <summary>A layer by identity, as NDMF's virtual layers are, and where it plays; null when that is not known.</summary>
        sealed class Layer
        {
            readonly string _name;
            public readonly long? Order;
            public Layer(string name, long? order) { _name = name; Order = order; }
            public override string ToString() => _name;
        }

        /// <summary>A clip by identity, as NDMF's virtual clips are, and the layer that plays it; its name is only for reading a failure.</summary>
        sealed class Clip
        {
            readonly string _name;
            public readonly Layer Layer;
            public Clip(string name, Layer layer = null) { _name = name; Layer = layer ?? new Layer(name, 100); }
            public override string ToString() => _name;
        }

        // As on the measured avatar: a garment's toggle layer at 13 holds the tights' heel, the boots' layer at 17 drives it.
        static readonly Layer Toggles = new Layer("toggles", 13);
        static readonly Layer Boots = new Layer("boots", 17);
        static readonly Clip Heels = new Clip("heels", Boots);
        static readonly Clip Hips = new Clip("hips");
        static readonly Clip Sweater = new Clip("sweater", Toggles);

        static ShapeKeyInput Key(string name, float weight = 0f, float fullWeight = 100f) => new ShapeKeyInput(name, weight, fullWeight);

        static RendererShapeKeys Renderer(string path, params ShapeKeyInput[] keys) => new RendererShapeKeys(path, keys);

        /// <summary>A curve in <paramref name="clip"/>, played by its layer, through <paramref name="values"/> at times 0, 1, 2 and so on, read as the sources read one.</summary>
        static ShapeKeyBinding Curve(Clip clip, string path, string key, params float[] values) => Curve(clip.Layer, clip, path, key, values);

        /// <summary>As the other overload, played by <paramref name="layer"/>: one clip two layers play is read once from each.</summary>
        static ShapeKeyBinding Curve(Layer layer, Clip clip, string path, string key, params float[] values)
        {
            var curve = new AnimationCurve(values.Select((v, i) => new Keyframe(i, v)).ToArray());
            return Binding(layer, clip, path, key, curve);
        }

        static ShapeKeyBinding Binding(Clip clip, string path, string key, AnimationCurve curve) => Binding(clip.Layer, clip, path, key, curve);

        static ShapeKeyBinding Binding(Layer layer, Clip clip, string path, string key, AnimationCurve curve) =>
            new ShapeKeyBinding(path, key, ShapeKeyClassifier.CurveValues(curve), curve.keys.Select(k => k.time).ToArray(), clip, layer, layer.Order);

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

        // ------------------------------------------------------------------------------------------------
        // Holds. A layer overrides the layers before it, so a layer that only holds one member pins it wherever the layers
        // before it drive the rest; and with write defaults off, a state of the driving layer that holds one member resets
        // it alone and leaves the others where the last state drove them.

        [Test]
        public void AHoldInAnEarlierLayer_OnOneSideOnly_TakesNoPart_AndTheKeysLink()
        {
            // The sweater's toggle, at 13, holds the tights' heel where the renderer holds it; the boots' layer at 17 drives both.
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(Sweater, "Tights", "Toe_heels", 100f, 100f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.EqualTo(new[] { BothHeels }));
        }

        [TestCase(20L, TestName = "TheSameHoldInALaterLayer_Refuses")]
        [TestCase(null, TestName = "TheSameHoldInALayerOfUnknownOrder_Refuses")]
        public void AHoldAfterTheDriver(long? order)
        {
            var sweater = new Clip("sweater", new Layer("toggles", order));
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(sweater, "Tights", "Toe_heels", 100f, 100f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void AHoldAfterOneOfTwoLayersPlayingTheDriver_Refuses()
        {
            // One heels clip played by layers at 5 and 17; the toggle at 13 plays after the first.
            var early = new Layer("early boots", 5);
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(early, Heels, "Body", "Toe_heels", 100f, 0f), Curve(early, Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(Sweater, "Tights", "Toe_heels", 100f, 100f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void AOneSidedHoldInAnotherStateOfTheDrivingLayer_Refuses()
        {
            // The boots' layer's off state holds the tights' heel alone: with write defaults off it resets the tights and
            // leaves the body where the heels state drove it.
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(new Clip("boots off", Boots), "Tights", "Toe_heels", 100f, 100f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.Empty);
        }

        [Test]
        public void TheSameHoldOnEveryMemberInTheDrivingLayer_Links()
        {
            var off = new Clip("boots off", Boots);
            var bindings = new[]
            {
                Curve(Heels, "Body", "Toe_heels", 100f, 0f), Curve(Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(off, "Body", "Toe_heels", 100f, 100f), Curve(off, "Tights", "Toe_heels", 100f, 100f),
            };

            Assert.That(Find(BodyAndTights, bindings), Is.EqualTo(new[] { BothHeels }));
        }

        // The playable layers play Base, Additive, Gesture, Action, FX; a controller's layers in index order.
        [TestCase(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.Gesture, 30, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX, 0, true,
            TestName = "AHoldInGesture_BeforeADriverInFX_Links")]
        [TestCase(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX, 0, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.Gesture, 30, false,
            TestName = "AHoldInFX_AfterADriverInGesture_Refuses")]
        [TestCase(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.Sitting, 0, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX, 30, false,
            TestName = "AHoldInASpecialLayer_Refuses")]
        public void OrderAcrossControllers(object holdController, int holdIndex, object driveController, int driveIndex, bool links)
        {
            var drive = new Layer("drive", Animators.AnimatedCurves.PlayOrder(driveController, driveIndex));
            var hold = new Layer("hold", Animators.AnimatedCurves.PlayOrder(holdController, holdIndex));
            var bindings = new[]
            {
                Curve(drive, Heels, "Body", "Toe_heels", 100f, 0f), Curve(drive, Heels, "Tights", "Toe_heels", 100f, 0f),
                Curve(hold, Sweater, "Tights", "Toe_heels", 100f, 100f),
            };

            var found = Find(BodyAndTights, bindings);
            if (links) Assert.That(found, Is.EqualTo(new[] { BothHeels }));
            else Assert.That(found, Is.Empty);
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
        public void ACurveAnAvatarMaskMayKeepFromItsKey_NeverLinks()
        {
            var body = Curve(Heels, "Body", "Toe_heels", 100f, 0f);
            var tights = Curve(Heels, "Tights", "Toe_heels", 100f, 0f);
            var masked = new ShapeKeyBinding(tights.Path, tights.Key, tights.Values, tights.Times, tights.Clip, tights.Layer, tights.Order, masked: true);

            Assert.That(Find(BodyAndTights, new[] { body, masked }), Is.Empty);
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
