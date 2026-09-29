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
    public class VanishingBonesTests
    {
        // Rest scale, then the curve's values. A face stops counting as cover when its bone can shrink, reach or cross
        // zero, or go non-finite; growing only moves it outward with everything riding the bone.
        [TestCase(1f, new[] { 1f, 0f }, true)]
        [TestCase(1f, new[] { 1f, 1e-4f }, true)]
        [TestCase(1f, new[] { 1f, 0.9f }, true)]
        [TestCase(1f, new[] { float.NaN }, true)]
        [TestCase(1f, new[] { float.PositiveInfinity }, true)]
        [TestCase(1f, new[] { -1f }, true)]
        [TestCase(1.2f, new[] { 1.2f, 1f }, true)]
        [TestCase(-1f, new[] { -1f, 1f }, true)]
        [TestCase(1f, new[] { 1f, 1.2f }, false)]
        [TestCase(1f, new[] { 1f, 1f }, false)]
        [TestCase(-1f, new[] { -1f, -1.5f }, false)]
        public void Collapses_WhenTheCurveCanPullTheBoneIn_Mirror_OrRemoveIt(float rest, float[] values, bool collapses)
        {
            Assert.That(VanishingBones.Collapses(rest, values), Is.EqualTo(collapses));
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

            var flags = VanishingBones.Compute(bones, new[] { new ScaleCurve(scaled, 0, new[] { 1f, 0f }), new ScaleCurve(steady, 1, new[] { 1f, 1.1f }) });

            Assert.That(flags[bones.Id(named)], Is.True, "by name");
            Assert.That(flags[bones.Id(scaled)], Is.True, "by scale");
            Assert.That(flags[bones.Id(child)], Is.True, "under a scaled bone");
            Assert.That(flags[bones.Id(steady)], Is.False, "grown only");
            Assert.That(flags[bones.Id(a.Bone("Armature/Hips"))], Is.False, "a parent is not flagged by its child");
        }

        // A legacy clip keyed on scale X also carries Y and Z, held where the bone stands; only X can collapse it.
        [Test]
        public void LegacyAnimationScaleCurves_ResolveTheirPathFromTheComponent_AndKeepANaNKey()
        {
            using var a = new AvatarFixture();
            var bone = a.Bone("Armature/Hips/Skirt");
            a.ScaleClip(bone, 1f, float.NaN);

            var curves = VanishingBones.FromLegacyAnimations(a.Root.transform).ToList();

            Assert.That(curves.Select(c => c.Target).Distinct(), Is.EqualTo(new[] { bone }));
            Assert.That(curves.Single(c => c.Axis == 0).Values, Has.Some.NaN);
            Assert.That(curves.Where(c => c.Axis != 0).Select(c => VanishingBones.Collapses(1f, c.Values)), Has.All.False);
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
