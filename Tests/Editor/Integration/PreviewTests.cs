using System.Linq;
using nadena.dev.ndmf.preview;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Preview;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // The edit-mode preview's input and its re-cut. Its drawing is NDMF's and is looked at in the Editor, not here.
    public class PreviewTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

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

            int[] Counts(OptimizationReport report) => report.Renderers.Select(r => r.TrianglesAfter).ToArray();
            int[] Fresh(float tolerance)
            {
                c.tolerance = tolerance;
                using (var prepared = OptimizerPipeline.Prepare(PreviewInput.For(a.Root.transform, c, out _)))
                {
                    var report = OptimizerPipeline.Cut(prepared);
                    foreach (var r in report.Renderers) if (r.OutputMesh != null) a.Made(r.OutputMesh);
                    return Counts(report);
                }
            }

            int[] fine, coarse;
            using (var prepared = OptimizerPipeline.Prepare(PreviewInput.For(a.Root.transform, c, out _)))
            {
                Assert.That(prepared.OcclusionRan, Is.False, "the preview removes no hidden triangles");
                c.tolerance = 1f;
                var first = OptimizerPipeline.Cut(prepared);
                c.tolerance = 3f;
                var second = OptimizerPipeline.Cut(prepared);
                foreach (var r in first.Renderers.Concat(second.Renderers)) if (r.OutputMesh != null) a.Made(r.OutputMesh);
                fine = Counts(first);
                coarse = Counts(second);
            }

            Assert.That(coarse.Sum(), Is.LessThan(fine.Sum()), "a coarser tolerance cuts deeper");
            Assert.That(fine, Is.EqualTo(Fresh(1f)));
            Assert.That(coarse, Is.EqualTo(Fresh(3f)));
        }

        // The filter's node lifecycle, driven as NDMF drives it but without a preview session: a target change re-cuts the one
        // prepared avatar, a changed renderer or any other setting prepares anew, and the prepared avatar and every mesh are
        // freed only when the last node cut from it is disposed.
        [Test]
        public void ATargetChange_ReCutsThePreparedAvatar_WhichTheLastNodeFrees()
        {
            var one = Ball("One", Vector3.zero);
            var two = Ball("Two", new Vector3(1, 0, 0));
            a.Root.AddComponent<VRCAvatarDescriptor>();
            var c = a.Optimizer(decimate: true);
            c.target = TargetKind.Tolerance;
            c.tolerance = 1f;
            var pairs = new[] { one, two }.Select(r =>
            {
                var proxy = a.Made(new GameObject(r.name + " proxy")).AddComponent<SkinnedMeshRenderer>();
                return ((Renderer)r, (Renderer)proxy);
            }).ToList();
            var group = RenderGroup.For(pairs.Select(p => p.Item1));
            var filter = new DecimationPreview();
            ComputeContext Context() => new ComputeContext("preview test");
            Mesh Shown(IRenderFilterNode node)
            {
                node.OnFrame(pairs[0].Item1, pairs[0].Item2);
                return ((SkinnedMeshRenderer)pairs[0].Item2).sharedMesh;
            }

            var first = filter.Instantiate(group, pairs, Context()).Result;
            var prepared = DecimationPreview.PreparedOf(first);
            Assert.That(prepared, Is.Not.Null);
            Assert.That(prepared.Input.BeforeStage, Is.Null, "no cut may raise the preparing progress bar again");
            var fine = PreviewStatus.Of(c);
            Assert.That(fine.Failure, Is.Null);
            var fineMesh = Shown(first);
            Assert.That(fineMesh, Is.Not.Null.And.Not.SameAs(one.sharedMesh));

            Assert.That(first.Refresh(pairs, Context(), 0).Result, Is.SameAs(first), "nothing changed");
            c.tolerance = 3f;
            var second = first.Refresh(pairs, Context(), 0).Result;
            Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
            Assert.That(DecimationPreview.PreparedOf(second), Is.SameAs(prepared), "a target change re-cuts, it does not re-prepare");
            Assert.That(PreviewStatus.Of(c).TrianglesAfter, Is.LessThan(fine.TrianglesAfter));

            Assert.That(second.Refresh(pairs, Context(), RenderAspects.Shapes).Result, Is.Null, "a changed renderer prepares anew");
            c.faceFloor = 0.8f;
            Assert.That(second.Refresh(pairs, Context(), 0).Result, Is.Null, "another setting prepares anew");

            first.Dispose();
            Assert.That(fineMesh == null, Is.True, "the first cut's meshes go with its node");
            Assert.That(prepared.Disposed, Is.False, "the second node still holds the prepared avatar");
            Assert.That(PreviewStatus.Of(c), Is.Not.Null, "the status is the second node's");
            var coarseMesh = Shown(second);
            second.Dispose();
            second.Dispose();
            Assert.That(coarseMesh == null, Is.True, "the last cut's meshes go with its node");
            Assert.That(prepared.Disposed, Is.True, "the last node frees the prepared avatar");
            Assert.That(PreviewStatus.Of(c), Is.Null);
        }

        string Path(Renderer r) => nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(a.Root, r.gameObject);
    }
}
