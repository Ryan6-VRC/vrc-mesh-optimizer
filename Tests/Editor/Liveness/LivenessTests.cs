using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Liveness
{
    // A live key is one the evaluation must deform, a frozen key is baked into the rest shape, and a dead
    // key is ignored. Misclassifying a driven key as frozen or dead lets the optimizer delete or collapse
    // geometry the key would have revealed, so each rule is pinned by the situation it exists for.
    public class LivenessTests
    {
        static ShapeKeyInput Key(string name, float weight = 0f, float fullWeight = 100f) => new ShapeKeyInput(name, weight, fullWeight);

        static ShapeKeyLiveness[][] Classify(RendererShapeKeys[] renderers, ShapeKeyBinding[] bindings, DrivenKey[] driven = null) =>
            ShapeKeyClassifier.Classify(renderers, bindings, driven ?? new DrivenKey[0]);

        [Test]
        public void KeyBoundByAClip_IsLive_AtTheLargestValueItReaches()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("smile") });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "smile", new[] { 0f, 40f, 80f }) });

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Live(0.8f)));
        }

        [Test]
        public void KeyOnlyEverHeldAtZero_IsDead()
        {
            // The defaults clips MA and VRCFury write bind every animated key at its resting value.
            var body = new RendererShapeKeys("Body", new[] { Key("unused") });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "unused", new[] { 0f, 0f }) });

            Assert.That(result[0][0].State, Is.EqualTo(ShapeKeyState.Dead));
        }

        [Test]
        public void NonzeroKeyDrivenByNothing_IsFrozenAtItsWeight()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("chest", 35f) });
            var result = Classify(new[] { body }, new ShapeKeyBinding[0]);

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Frozen(35f)));
        }

        [Test]
        public void NonzeroKeyOnlyHeldAtItsOwnWeight_IsFrozen()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("chest", 35f) });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "chest", new[] { 35f }) });

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Frozen(35f)));
        }

        [Test]
        public void KeyDrivenToZeroFromANonzeroWeight_IsLive_HeldAtItsOwnWeight()
        {
            // A toggle that flattens a key the renderer holds at 60 moves it between 0 and 60, and the avatar stands at 60.
            var body = new RendererShapeKeys("Body", new[] { Key("shrink", 60f) });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "shrink", new[] { 0f }) });

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Live(0f, 0.6f, 60f)));
        }

        [Test]
        public void KeyDrivenOnlyNegative_IsLive_DownToItsLowestValue()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("dent") });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "dent", new[] { 0f, -20f, -50f }) });

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Live(-0.5f)));
        }

        [Test]
        public void KeyDrivenBelowItsOwnWeight_IsLive_BetweenTheTwo_HeldAtItsOwnWeight()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("chest", 80f) });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "chest", new[] { 30f }) });

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Live(0.3f, 0.8f, 80f)));
        }

        [Test]
        public void KeyDrivenBothAboveAndBelowZero_KeepsBothEnds()
        {
            // Held at 50, driven to -100 by one clip and 80 by another: a single signed extreme would keep -100 and lose
            // the 80, the one configuration here that pushes the surface further out than the avatar stands.
            var body = new RendererShapeKeys("Body", new[] { Key("swell", 50f) });
            var bindings = new[]
            {
                new ShapeKeyBinding("Body", "swell", new[] { -100f }),
                new ShapeKeyBinding("Body", "swell", new[] { 80f }),
            };

            Assert.That(Classify(new[] { body }, bindings)[0][0], Is.EqualTo(ShapeKeyLiveness.Live(-1f, 0.8f, 50f)));
        }

        [Test]
        public void DrivenMaximum_IsAFractionOfTheKeysFullWeight()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("wide", 0f, fullWeight: 50f) });
            var result = Classify(new[] { body }, new[] { new ShapeKeyBinding("Body", "wide", new[] { 25f }) });

            Assert.That(result[0][0], Is.EqualTo(ShapeKeyLiveness.Live(0.5f)));
        }

        [Test]
        public void VisemeAndEyelidKeys_AreLive_WithNoClipAtAll()
        {
            var face = new RendererShapeKeys("Body", new[] { Key("vrc.v_aa"), Key("blink"), Key("other") });
            var driven = new[] { new DrivenKey("Body", "vrc.v_aa"), new DrivenKey("Body", "blink") };
            var result = Classify(new[] { face }, new ShapeKeyBinding[0], driven);

            Assert.That(result[0], Is.EqualTo(new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Dead }));
        }

        [Test]
        public void Bindings_MatchTheirRendererByPath_NotByName()
        {
            // Two renderers named Body, with the same key; the clip animates only the outfit's.
            var face = new RendererShapeKeys("Body", new[] { Key("smile") });
            var outfit = new RendererShapeKeys("Outfit/Body", new[] { Key("smile") });
            var result = Classify(new[] { face, outfit }, new[] { new ShapeKeyBinding("Outfit/Body", "smile", new[] { 100f }) });

            Assert.That(result[0][0].State, Is.EqualTo(ShapeKeyState.Dead));
            Assert.That(result[1][0], Is.EqualTo(ShapeKeyLiveness.Live(1f)));
        }

        [Test]
        public void EveryClipOnAKey_CountsTowardItsMaximum()
        {
            var body = new RendererShapeKeys("Body", new[] { Key("smile") });
            var bindings = new[]
            {
                new ShapeKeyBinding("Body", "smile", new[] { 0f, 30f }),
                new ShapeKeyBinding("Body", "smile", new[] { 0f, 90f }),
            };

            Assert.That(Classify(new[] { body }, bindings)[0][0], Is.EqualTo(ShapeKeyLiveness.Live(0.9f)));
        }

        [Test]
        public void CurveValues_CatchATangentThatOvershootsItsKeys()
        {
            // Both keys are at most 100, but the tangents carry the curve past 100 between them.
            var curve = new AnimationCurve(new Keyframe(0f, 0f, 0f, 400f), new Keyframe(1f, 100f, 0f, 0f));
            var values = ShapeKeyClassifier.CurveValues(curve);

            Assert.That(values.Max(), Is.GreaterThan(100f));
            Assert.That(values, Has.Member(0f).And.Member(100f));
        }
    }
}
