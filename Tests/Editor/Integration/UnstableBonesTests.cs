using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    public class UnstableBonesTests
    {
        // Rest scale, then the curve's values. A bone is unstable when a clip can take its scale away from rest in either
        // direction, since its kin do not scale with it, or to a non-finite value; a curve that holds rest is not.
        [TestCase(1f, new[] { 1f, 0f }, true)]
        [TestCase(1f, new[] { 1f, 0.9f }, true)]
        [TestCase(1f, new[] { float.NaN }, true)]
        [TestCase(1f, new[] { float.PositiveInfinity }, true)]
        [TestCase(1f, new[] { -1f }, true)]
        [TestCase(1f, new[] { 1f, 1.2f }, true)]
        [TestCase(-1f, new[] { -1f, -1.5f }, true)]
        [TestCase(0f, new[] { 0f, 0.5f }, true)]
        [TestCase(1f, new[] { 1f, 1f }, false)]
        [TestCase(1.2f, new[] { 1.2f, 1.2002f }, false)]
        [TestCase(0f, new[] { 0f }, false)]
        public void Moves_WhenACurveCanTakeTheScaleAwayFromRest(float rest, float[] values, bool moves)
        {
            Assert.That(UnstableBones.Moves(rest, values), Is.EqualTo(moves));
        }

        // Rest scale, then the curve's values, then how many times the curve grows the bone: its largest magnitude over the
        // rest magnitude, without bound from zero; shrinking, flipping the sign, holding rest and NaN grow it by nothing.
        [TestCase(0f, new[] { 0f, 1f }, float.PositiveInfinity)]
        [TestCase(0.01f, new[] { 0.01f, 1f }, 100f)]
        [TestCase(1f, new[] { 1f, 1.2f, 1.1f }, 1.2f)]
        [TestCase(-1f, new[] { -1f, -1.5f }, 1.5f)]
        [TestCase(1f, new[] { float.PositiveInfinity }, float.PositiveInfinity)]
        [TestCase(1f, new[] { 1f, 0f }, 1f)]
        [TestCase(1f, new[] { -1f }, 1f)]
        [TestCase(1f, new[] { float.NaN }, 1f)]
        [TestCase(1.2f, new[] { 1.2f, 1.2002f }, 1f)]
        [TestCase(0f, new[] { 0f }, 1f)]
        public void GrowthOf_IsTheLargestScaleOverRest(float rest, float[] values, float growth)
        {
            Assert.That(UnstableBones.GrowthOf(rest, values), Is.EqualTo(growth).Within(1e-4f));
        }

        [Test]
        public void Growth_MultipliesDownTheTree_AndIgnoresShrinkingAndDeletionBones()
        {
            using var a = new AvatarFixture();
            var hips = a.Bone("Armature/Hips");
            var grown = a.Bone("Armature/Hips/Accessory");
            var child = a.Bone("Armature/Hips/Accessory/Tip");
            var shrunk = a.Bone("Armature/Hips/Skirt");
            var zero = a.Bone("Armature/Hips/Hidden");
            var named = a.Bone("Armature/Chest/NaNimatedBone for x");
            grown.localScale = Vector3.one * 0.5f;
            zero.localScale = Vector3.zero;
            var bones = new AvatarBones(a.Root.transform);

            var growth = UnstableBones.Growth(bones, new[]
            {
                new ScaleCurve(hips, 1, new[] { 1f, 1.2f }), new ScaleCurve(grown, 0, new[] { 0.5f, 1f }), new ScaleCurve(grown, 2, new[] { 0.5f, 1.5f }),
                new ScaleCurve(shrunk, 1, new[] { 1f, 0f }), new ScaleCurve(zero, 0, new[] { 0f, 1f }),
            });

            Assert.That(growth[bones.Id(hips)], Is.EqualTo(1.2f).Within(1e-5f));
            Assert.That(growth[bones.Id(grown)], Is.EqualTo(1.2f * 3f).Within(1e-5f), "its largest axis, times its parent's");
            Assert.That(growth[bones.Id(child)], Is.EqualTo(1.2f * 3f).Within(1e-5f), "under a growing bone");
            Assert.That(growth[bones.Id(shrunk)], Is.EqualTo(1.2f).Within(1e-5f), "only its parent grows it");
            Assert.That(growth[bones.Id(zero)], Is.EqualTo(float.PositiveInfinity));
            Assert.That(growth[bones.Id(named)], Is.EqualTo(1f), "a deletion bone is shown at its rest scale or not at all");
            Assert.That(growth[bones.Id(a.Bone("Armature"))], Is.EqualTo(1f), "a parent is not grown by its child");
        }
        [Test]
        public void Flags_UnionBothSources_AndRunDownTheTree()
        {
            using var a = new AvatarFixture();
            var named = a.Bone("Armature/Chest/NaNimatedBone for x");
            var scaled = a.Bone("Armature/Hips/Skirt");
            var child = a.Bone("Armature/Hips/Skirt/Tip");
            var steady = a.Bone("Armature/Hips/Steady");
            var bones = new AvatarBones(a.Root.transform);

            var flags = UnstableBones.Compute(bones, new[] { new ScaleCurve(scaled, 0, new[] { 1f, 1.2f }), new ScaleCurve(steady, 1, new[] { 1f, 1f }) });

            Assert.That(flags[bones.Id(named)], Is.True, "by name");
            Assert.That(flags[bones.Id(scaled)], Is.True, "by scale");
            Assert.That(flags[bones.Id(child)], Is.True, "under a scaled bone");
            Assert.That(flags[bones.Id(steady)], Is.False, "held at rest");
            Assert.That(flags[bones.Id(a.Bone("Armature/Hips"))], Is.False, "a parent is not flagged by its child");
        }

        // A legacy clip keyed on scale X also carries Y and Z, held where the bone stands; only X can collapse it.
        [Test]
        public void LegacyAnimationScaleCurves_ResolveTheirPathFromTheComponent_AndKeepANaNKey()
        {
            using var a = new AvatarFixture();
            var bone = a.Bone("Armature/Hips/Skirt");
            a.ScaleClip(bone, 1f, float.NaN);

            var curves = UnstableBones.FromLegacyAnimations(a.Root.transform).ToList();

            Assert.That(curves.Select(c => c.Target).Distinct(), Is.EqualTo(new[] { bone }));
            Assert.That(curves.Single(c => c.Axis == 0).Values, Has.Some.NaN);
            Assert.That(curves.Where(c => c.Axis != 0).Select(c => UnstableBones.Moves(1f, c.Values)), Has.All.False);
        }

        static string Po(string file) => File.ReadAllText(Path.GetFullPath("Packages/com.ryan6vrc.mesh-optimizer/Editor/Localization/" + file));

        // Every problem the pass can raise reads in both languages, with its detail and its fix.
        [Test]
        public void EveryDiagnosticKey_HasATitle_Description_AndHint_InBothLanguages()
        {
            foreach (var file in new[] { "en-us.po", "ja-jp.po" })
            {
                var text = Po(file);
                foreach (var key in DiagnosticKeys.All)
                foreach (var suffix in new[] { "", ":description", ":hint" })
                    Assert.That(Regex.IsMatch(text, "msgid \"" + Regex.Escape(key + suffix) + "\"\\s*\\nmsgstr \"[^\"]+\""), $"{file} lacks {key}{suffix}");
            }
        }
    }
}
