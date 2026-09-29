using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Tests.Integration;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Liveness
{
    // Two siblings can share a name, and a curve names its target by path. Which of them Unity animates decides which
    // renderer's key is live; crediting the other evaluates a state no player sees. The first test pins Unity's own
    // binding; the rest hold the classifier and the linker to it.
    public class SiblingBindingTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

        /// <summary>A child of the root named <paramref name="name"/>: a skinned renderer with one key, "Key", or a plain object.</summary>
        GameObject Child(string name, bool skinned)
        {
            if (!skinned)
            {
                var plain = new GameObject(name);
                plain.transform.SetParent(a.Root.transform, false);
                return plain;
            }
            var mesh = a.Sphere(0.1f, 4, 8, name: name);
            mesh.AddBlendShapeFrame("Key", 100f, Enumerable.Repeat(Vector3.up * 0.01f, mesh.vertexCount).ToArray(), null, null);
            return a.Skinned(name, mesh, new[] { a.Root.transform }).gameObject;
        }

        static EditorCurveBinding KeyBinding(string path) => EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape.Key");

        AnimationClip Clip(bool legacy, params (string path, float value)[] curves)
        {
            var clip = a.Made(new AnimationClip { legacy = legacy, name = "drive" });
            foreach (var (path, value) in curves)
                AnimationUtility.SetEditorCurve(clip, KeyBinding(path), AnimationCurve.Constant(0f, 1f, value));
            return clip;
        }

        void Play(AnimationClip legacyClip)
        {
            var animation = a.Root.AddComponent<UnityEngine.Animation>();
            animation.AddClip(legacyClip, legacyClip.name);
            animation.clip = legacyClip;
        }

        // Unity binds a path to the first child of each name along it, and to nothing else: when that first child has no
        // skinned renderer, the curve animates nothing, though a later sibling of the name has one.
        [TestCase(false, TestName = "{m}(plain object first)")]
        [TestCase(true, TestName = "{m}(skinned renderer first)")]
        public void UnityBindsAPath_ToTheFirstSiblingOfItsName_Only(bool firstSkinned)
        {
            a.Root.AddComponent<Animator>();
            var first = Child("Body", firstSkinned);
            var second = Child("Body", true).GetComponent<SkinnedMeshRenderer>();
            var clip = Clip(false, ("Body", 80f));

            AnimationMode.StartAnimationMode();
            try
            {
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(a.Root, clip, 0f);
                AnimationMode.EndSampling();

                Assert.That(second.GetBlendShapeWeight(0), Is.EqualTo(0f), "the second sibling is never animated");
                if (firstSkinned) Assert.That(first.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(80f));
                var bound = AnimationUtility.GetAnimatedObject(a.Root, KeyBinding("Body"));
                Assert.That(bound, firstSkinned ? Is.SameAs(first.GetComponent<SkinnedMeshRenderer>()) : Is.Null);
            }
            finally
            {
                AnimationMode.StopAnimationMode();
            }
        }

        [TestCase(false, TestName = "{m}(plain object first)")]
        [TestCase(true, TestName = "{m}(skinned renderer first)")]
        public void AKey_IsLiveOnlyOnTheRendererItsPathBinds(bool firstSkinned)
        {
            var first = Child("Body", firstSkinned);
            var second = Child("Body", true).GetComponent<SkinnedMeshRenderer>();
            Play(Clip(true, ("Body", 80f)));
            var renderers = a.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            var table = ShapeKeyClassifier.Classify(renderers.Select(r => LivenessSources.Describe(r, a.Root.transform)).ToList(),
                LivenessSources.FromLegacyAnimations(a.Root.transform).ToList(), new DrivenKey[0]);

            Assert.That(table[System.Array.IndexOf(renderers, second)][0].State, Is.EqualTo(ShapeKeyState.Dead), "Unity never animates the second");
            if (firstSkinned)
                Assert.That(table[System.Array.IndexOf(renderers, first.GetComponent<SkinnedMeshRenderer>())][0].State, Is.EqualTo(ShapeKeyState.Live));
        }

        // One clip drives Key on Body and on Tights alike. Linked, they move together, which holds only for the Body the
        // path binds; a later Body of the same name never moves, and one behind a plain object of the name links with nothing.
        [TestCase(false, TestName = "{m}(plain object first)")]
        [TestCase(true, TestName = "{m}(skinned renderer first)")]
        public void AKeyLinks_OnlyOnTheRendererItsPathBinds(bool firstSkinned)
        {
            var first = Child("Body", firstSkinned);
            var second = Child("Body", true).GetComponent<SkinnedMeshRenderer>();
            var tights = Child("Tights", true).GetComponent<SkinnedMeshRenderer>();
            Play(Clip(true, ("Body", 80f), ("Tights", 80f)));
            var renderers = a.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList();

            var links = ShapeKeyLinks.Find(renderers.Select(r => LivenessSources.Describe(r, a.Root.transform)).ToList(),
                LivenessSources.FromLegacyAnimations(a.Root.transform).ToList(), new DrivenKey[0]);

            var members = links.SelectMany(g => g).Select(k => renderers[k.Renderer]).ToList();
            Assert.That(members, Has.No.Member(second), "the second Body never moves");
            if (firstSkinned)
                Assert.That(links.Select(g => g.Select(k => renderers[k.Renderer])), Is.EqualTo(new[] { new[] { first.GetComponent<SkinnedMeshRenderer>(), tights } }));
            else
                Assert.That(links, Is.Empty, "Tights moves alone");
        }
    }
}
