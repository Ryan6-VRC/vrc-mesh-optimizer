using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.preview;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Preview;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // The edit-mode preview's input, its re-cut and what it keeps. Its drawing is NDMF's and is looked at in the Editor, not here.
    public class PreviewTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();

        [TearDown]
        public void TearDown()
        {
            // The filter keeps each avatar's prepared avatar past its nodes, and which avatars it shows.
            DecimationPreview.Reset();
            a.Dispose();
        }

        SkinnedMeshRenderer Ball(string name, Vector3 at)
        {
            var bone = a.Bone("Armature/" + name);
            bone.localPosition = at;
            return a.Skinned(name, a.Sphere(0.1f, 30, 40, name: name), new[] { bone }, parent: bone);
        }

        /// <summary>A face with two keys, "smile" and "blink", both held at 0.</summary>
        SkinnedMeshRenderer Face()
        {
            var face = Ball("Face", Vector3.zero);
            var mesh = face.sharedMesh;
            var deltas = mesh.vertices.Select(v => v * 0.1f).ToArray();
            mesh.AddBlendShapeFrame("smile", 100f, deltas, null, null);
            mesh.AddBlendShapeFrame("blink", 100f, deltas, null, null);
            return face;
        }

        // With no descriptor on the root the preview has nothing to judge keys by, so it evaluates every key live, and it
        // never asks for occlusion.
        [Test]
        public void ARootWithNoDescriptor_IsPreviewedWithEveryKeyLive_AndWithoutOcclusion()
        {
            Face();
            var input = PreviewInput.For(a.Root.transform, a.Optimizer(decimate: true), out var source);

            Assert.That(source, Is.EqualTo(PreviewLiveness.EveryKeyLive));
            Assert.That(input.Liveness, Is.Null, "no liveness is every key live");
            Assert.That(input.Occlude, Is.False);
        }

        // The descriptor's FX controller drives "smile" and its eyelids name "blink": the preview judges both live, as a
        // build would from the same descriptor. Without the eyelid, "blink" is driven by nothing and held at 0: dead.
        [TestCase(true)]
        [TestCase(false)]
        [Test]
        public void TheDescriptorsControllersAndEyelids_DecideWhichKeysThePreviewEvaluatesLive(bool eyelid)
        {
            var face = Face();
            var clip = a.Made(new AnimationClip { name = "smile" });
            clip.SetCurve("Armature/Face/Face", typeof(SkinnedMeshRenderer), "blendShape.smile", AnimationCurve.Linear(0, 0, 1, 100));
            var controller = a.Made(new UnityEditor.Animations.AnimatorController { name = "fx" });
            controller.AddLayer("smile");
            controller.layers[0].stateMachine.AddState("smile").motion = clip;
            var descriptor = a.Root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false },
            };
            descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
            if (eyelid)
            {
                descriptor.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
                descriptor.customEyeLookSettings.eyelidsSkinnedMesh = face;
                descriptor.customEyeLookSettings.eyelidsBlendshapes = new[] { 1 };
            }
            Assert.That(Path(face), Is.EqualTo("Armature/Face/Face"), "the clip's path names the face");

            var input = PreviewInput.For(a.Root.transform, a.Optimizer(decimate: true), out var source);

            Assert.That(source, Is.EqualTo(PreviewLiveness.Descriptor));
            var liveness = input.Liveness[face];
            Assert.That(liveness[0].State, Is.EqualTo(ShapeKeyState.Live), "smile");
            Assert.That(liveness[1].State, Is.EqualTo(eyelid ? ShapeKeyState.Live : ShapeKeyState.Dead), "blink");
        }

        // The preview prepares once and cuts again when the target changes: occlusion does not run though the component
        // asks for it, and each cut of the one prepared avatar writes what a fresh prepare and cut at that tolerance writes.
        [Test]
        public void OnePreparedAvatar_CutAtTwoTolerances_MatchesAFreshPrepareAtEach()
        {
            var one = Ball("One", Vector3.zero);
            Ball("Two", new Vector3(1, 0, 0));
            var c = a.Optimizer(decimate: true, removeHidden: true);
            c.alwaysOn.Add(one);
            c.target = TargetKind.Tolerance;

            int[] fine, coarse;
            using (var prepared = OptimizerPipeline.Prepare(PreviewInput.For(a.Root.transform, c, out _)))
            {
                Assert.That(prepared.OcclusionRan, Is.False, "the preview removes no hidden triangles");
                c.tolerance = 1f;
                fine = Counts(prepared);
                c.tolerance = 3f;
                coarse = Counts(prepared);
            }

            Assert.That(coarse.Sum(), Is.LessThan(fine.Sum()), "a coarser tolerance cuts deeper");
            c.tolerance = 1f;
            Assert.That(fine, Is.EqualTo(Fresh(c)));
            c.tolerance = 3f;
            Assert.That(coarse, Is.EqualTo(Fresh(c)));
        }

        // A renderer's protection is read by the cut, as the target is: one prepared avatar cut again after a protection
        // changes keeps more of that renderer, and writes what a fresh prepare and cut at that protection writes.
        [Test]
        public void OnePreparedAvatar_CutAgainAfterAProtectionChanges_MatchesAFreshPrepare()
        {
            var one = Ball("One", Vector3.zero);
            Ball("Two", new Vector3(1, 0, 0));
            var c = a.Optimizer(decimate: true);
            c.target = TargetKind.Tolerance;
            c.tolerance = 3f;
            var own = new RendererSettings { renderer = one };
            c.renderers.Add(own);

            int[] neutral, guarded;
            using (var prepared = OptimizerPipeline.Prepare(PreviewInput.For(a.Root.transform, c, out _)))
            {
                neutral = Counts(prepared);
                own.protection = 4f;
                guarded = Counts(prepared);
            }

            Assert.That(guarded[0], Is.GreaterThan(neutral[0]), "the protected renderer keeps more");
            Assert.That(guarded[1], Is.EqualTo(neutral[1]), "a tolerance cuts the other renderer as before");
            Assert.That(guarded, Is.EqualTo(Fresh(c)));
        }

        // The filter's node lifecycle, driven as NDMF drives it but without a preview session: a target change re-cuts the one
        // prepared avatar, another setting prepares anew, and each node's meshes go with it. The prepared avatar outlives its
        // nodes, so a preview switched off and on is cut from it again, until the filter is reset.
        [Test]
        public void ATargetChange_ReCutsThePreparedAvatar_WhichIsKeptPastItsNodes()
        {
            var pairs = TwoBalls(out var one, out _);
            var c = a.Optimizer(decimate: true);
            c.target = TargetKind.Tolerance;
            c.tolerance = 1f;
            var group = RenderGroup.For(pairs.Select(p => p.Item1));
            var filter = new DecimationPreview();

            var first = filter.Instantiate(group, pairs, Context()).Result;
            var prepared = DecimationPreview.PreparedOf(first);
            Assert.That(prepared, Is.Not.Null);
            Assert.That(prepared.Input.BeforeStage, Is.Null, "no cut may raise the preparing progress bar again");
            var fine = PreviewStatus.Of(c);
            Assert.That(fine.Failure, Is.Null);
            var fineMesh = Shown(first, pairs[0]);
            Assert.That(fineMesh, Is.Not.Null.And.Not.SameAs(one.sharedMesh));

            Assert.That(first.Refresh(pairs, Context(), 0).Result, Is.SameAs(first), "nothing changed");
            c.tolerance = 3f;
            var second = first.Refresh(pairs, Context(), 0).Result;
            Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
            Assert.That(DecimationPreview.PreparedOf(second), Is.SameAs(prepared), "a target change re-cuts, it does not re-prepare");
            Assert.That(PreviewStatus.Of(c).TrianglesAfter, Is.LessThan(fine.TrianglesAfter));

            c.faceFloor = 0.8f;
            Assert.That(second.Refresh(pairs, Context(), 0).Result, Is.Null, "another setting prepares anew");
            c.faceFloor = 0.5f;

            first.Dispose();
            Assert.That(fineMesh == null, Is.True, "the first cut's meshes go with its node");
            Assert.That(PreviewStatus.Of(c), Is.Not.Null, "the status is the second node's");
            var coarseMesh = Shown(second, pairs[0]);
            second.Dispose();
            second.Dispose();
            Assert.That(coarseMesh == null, Is.True, "the last cut's meshes go with its node");
            Assert.That(PreviewStatus.Of(c), Is.Null);
            Assert.That(prepared.Disposed, Is.False, "the prepared avatar is kept for the next switch-on");

            var third = filter.Instantiate(group, pairs, Context()).Result;
            Assert.That(DecimationPreview.PreparedOf(third), Is.SameAs(prepared), "switched on again, the preview is cut from what was kept");
            third.Dispose();
            DecimationPreview.Reset();
            Assert.That(prepared.Disposed, Is.True, "a reset frees what was kept");
        }

        // A kept prepared avatar is used again only while nothing it read has changed. A changed setting, a renderer NDMF
        // reports changed and a swapped mesh each prepare the avatar anew, and the one they replace is freed once no node
        // still shows it.
        [Test]
        public void AChangeToWhatPreparingRead_PreparesAnew_AndFreesTheOneReplaced()
        {
            var pairs = TwoBalls(out var one, out _);
            var c = a.Optimizer(decimate: true);
            var group = RenderGroup.For(pairs.Select(p => p.Item1));
            var filter = new DecimationPreview();

            var first = filter.Instantiate(group, pairs, Context()).Result;
            var prepared = DecimationPreview.PreparedOf(first);
            Assert.That(first.Refresh(pairs, Context(), RenderAspects.Shapes).Result, Is.Null, "a changed renderer prepares anew");
            var second = filter.Instantiate(group, pairs, Context()).Result;
            var anew = DecimationPreview.PreparedOf(second);
            Assert.That(anew, Is.Not.SameAs(prepared));
            Assert.That(prepared.Disposed, Is.False, "the first node still shows the one replaced");
            first.Dispose();
            Assert.That(prepared.Disposed, Is.True);

            second.Dispose();
            c.lockFingers = false;
            var third = filter.Instantiate(group, pairs, Context()).Result;
            Assert.That(DecimationPreview.PreparedOf(third), Is.Not.SameAs(anew), "a changed setting prepares anew");
            Assert.That(anew.Disposed, Is.True, "no node shows the one replaced");

            anew = DecimationPreview.PreparedOf(third);
            third.Dispose();
            one.sharedMesh = a.Sphere(0.1f, 20, 30, name: "swapped");
            var fourth = filter.Instantiate(group, pairs, Context()).Result;
            Assert.That(DecimationPreview.PreparedOf(fourth), Is.Not.SameAs(anew), "a swapped mesh prepares anew");
            Assert.That(anew.Disposed, Is.True);
            fourth.Dispose();
        }

        // While no node shows an avatar, NDMF's change tracking is what tells its kept prepared avatar from a stale one: a
        // shape-key weight changed through Undo, as the Inspector changes it, prepares the avatar anew at the next switch-on.
        [UnityTest]
        public IEnumerator AKeptPreparedAvatar_IsNotUsedAgain_OnceARendererItReadHasChanged()
        {
            var face = Face();
            a.Root.AddComponent<VRCAvatarDescriptor>();
            a.Optimizer(decimate: true);
            var pairs = Pairs(face);
            var group = RenderGroup.For(pairs.Select(p => p.Item1));
            var filter = new DecimationPreview();

            var first = filter.Instantiate(group, pairs, Context()).Result;
            var prepared = DecimationPreview.PreparedOf(first);
            first.Dispose();
            yield return null;
            var second = filter.Instantiate(group, pairs, Context()).Result;
            Assert.That(DecimationPreview.PreparedOf(second), Is.SameAs(prepared), "nothing changed while it was off");
            second.Dispose();

            Undo.RecordObject(face, "weight");
            face.SetBlendShapeWeight(0, 50f);
            Undo.FlushUndoRecordObjects();
            yield return null;
            yield return null;

            var third = filter.Instantiate(group, pairs, Context()).Result;
            Assert.That(DecimationPreview.PreparedOf(third), Is.Not.SameAs(prepared), "the weight it was prepared at has changed");
            Assert.That(prepared.Disposed, Is.True);
            third.Dispose();
        }

        // A protection change is a re-cut, as a target change is, and an entry that changes nothing is no change at all; an
        // exclusion changes what is recorded, so it prepares anew.
        [Test]
        public void AProtectionChange_ReCutsThePreparedAvatar_AndAnExclusionPreparesAnew()
        {
            var pairs = TwoBalls(out var one, out _);
            var c = a.Optimizer(decimate: true);
            c.target = TargetKind.Tolerance;
            c.tolerance = 3f;
            var group = RenderGroup.For(pairs.Select(p => p.Item1));
            var filter = new DecimationPreview();

            var first = filter.Instantiate(group, pairs, Context()).Result;
            var prepared = DecimationPreview.PreparedOf(first);
            var neutral = Triangles(Shown(first, pairs[0]));

            var own = new RendererSettings { renderer = one };
            c.renderers.Add(own);
            Assert.That(first.Refresh(pairs, Context(), 0).Result, Is.SameAs(first), "an entry at its defaults changes nothing");

            own.protection = 4f;
            var second = first.Refresh(pairs, Context(), 0).Result;
            Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
            Assert.That(DecimationPreview.PreparedOf(second), Is.SameAs(prepared), "a protection change re-cuts, it does not re-prepare");
            Assert.That(Triangles(Shown(second, pairs[0])), Is.GreaterThan(neutral), "the protected renderer keeps more");

            own.excludeFromDecimation = true;
            Assert.That(second.Refresh(pairs, Context(), 0).Result, Is.Null, "an exclusion prepares anew");
            first.Dispose();
            second.Dispose();
        }

        // The preview shows the avatars chosen by their component and no others, and only while the filter's own switch is
        // on: choosing one switches it on, and a reset forgets the choice.
        [Test]
        public void OnlyAnAvatarChosenByItsComponent_IsPreviewed()
        {
            var one = Ball("One", Vector3.zero);
            a.Root.AddComponent<VRCAvatarDescriptor>();
            var c = a.Optimizer(decimate: true);
            var filter = new DecimationPreview();
            bool Grouped() => filter.GetTargetGroups(Context()).Any(g => g.Renderers.Contains(one));

            Assert.That(DecimationPreview.Shows(c), Is.False);
            Assert.That(Grouped(), Is.False, "an avatar nobody chose is not prepared");

            DecimationPreview.Show(c, true);
            Assert.That(DecimationPreview.Toggle.IsEnabled.Value, Is.True, "choosing an avatar switches the filter on");
            Assert.That(DecimationPreview.Shows(c), Is.True);
            Assert.That(Grouped(), Is.True);

            DecimationPreview.Toggle.IsEnabled.Value = false;
            Assert.That(DecimationPreview.Shows(c), Is.False, "the filter's switch hides every chosen avatar");
            Assert.That(Grouped(), Is.True, "and switched on again it shows them without their being chosen again");

            DecimationPreview.Show(c, false);
            Assert.That(Grouped(), Is.False);
            DecimationPreview.Show(c, true);
            DecimationPreview.Reset();
            Assert.That(DecimationPreview.Toggle.IsEnabled.Value, Is.False);
            Assert.That(Grouped(), Is.False, "a reset forgets which avatars were chosen");
        }

        static ComputeContext Context() => new ComputeContext("preview test");

        /// <summary>Each renderer with a stand-in for the proxy NDMF would hand the filter.</summary>
        List<(Renderer, Renderer)> Pairs(params SkinnedMeshRenderer[] renderers) => renderers.Select(r =>
        {
            var proxy = a.Made(new GameObject(r.name + " proxy")).AddComponent<SkinnedMeshRenderer>();
            return ((Renderer)r, (Renderer)proxy);
        }).ToList();

        List<(Renderer, Renderer)> TwoBalls(out SkinnedMeshRenderer one, out SkinnedMeshRenderer two)
        {
            one = Ball("One", Vector3.zero);
            two = Ball("Two", new Vector3(1, 0, 0));
            a.Root.AddComponent<VRCAvatarDescriptor>();
            return Pairs(one, two);
        }

        /// <summary>The mesh a node puts on a renderer's proxy.</summary>
        static Mesh Shown(IRenderFilterNode node, (Renderer, Renderer) pair)
        {
            node.OnFrame(pair.Item1, pair.Item2);
            return ((SkinnedMeshRenderer)pair.Item2).sharedMesh;
        }

        /// <summary>Each renderer's triangles after one cut of a prepared avatar at the component's settings as they stand.</summary>
        int[] Counts(PreparedAvatar prepared)
        {
            var report = OptimizerPipeline.Cut(prepared);
            foreach (var r in report.Renderers) if (r.OutputMesh != null) a.Made(r.OutputMesh);
            return report.Renderers.Select(r => r.TrianglesAfter).ToArray();
        }

        int[] Fresh(AvatarMeshOptimizer c)
        {
            using (var prepared = OptimizerPipeline.Prepare(PreviewInput.For(a.Root.transform, c, out _))) return Counts(prepared);
        }

        string Path(Renderer r) => nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(a.Root, r.gameObject);
    }
}
