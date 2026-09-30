using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.OrderProbes;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Plugin;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // One synthetic avatar through NDMF's real processing: the plugin is found, its pass runs in Optimizing in the
    // order it declares against Modular Avatar (installed here) and the probes, the mesh it writes is on the renderer,
    // and the component is gone.
    public class PluginTests
    {
        AvatarFixture a;
        readonly List<string> log = new List<string>();
        readonly List<string> errors = new List<string>();

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture("Plugin Test Avatar");
            log.Clear();
            errors.Clear();
            Application.logMessageReceived += Record;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Record;
            a.Dispose();
        }

        void Record(string message, string stack, LogType type)
        {
            log.Add(message);
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
        }

        SkinnedMeshRenderer inner, garment;

        (SkinnedMeshRenderer body, Mesh source) Avatar(bool withComponent)
        {
            a.Root.AddComponent<VRCAvatarDescriptor>();
            var chest = a.Bone("Armature/Hips/Chest");
            var source = a.Sphere(0.1f, 16, 32, name: "body");
            var body = a.Skinned("Body", source, new[] { chest });
            garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            // A small sphere inside a closed shell loses every triangle, so NDMF finishes the build over an empty mesh. Both hang
            // under the belly, clear of the body and the garment.
            var belly = a.Bone("Armature/Hips/Belly");
            belly.localPosition = new Vector3(0, 0, 1);
            inner = a.Skinned("Inner", a.Sphere(0.05f, 8, 16, name: "inner"), new[] { belly }, parent: belly);
            var shell = a.Skinned("Shell", a.Sphere(0.06f, 12, 24, name: "shell"), new[] { belly }, parent: belly);
            // Decimating, as the component ships, so the build takes the path a user's does.
            if (withComponent) a.Optimizer(decimate: true).alwaysOn.AddRange(new Renderer[] { garment, shell });
            return (body, source);
        }

        void Process()
        {
            LogAssert.ignoreFailingMessages = true;
            using (new OverrideTemporaryDirectoryScope(null))
                AvatarProcessor.ProcessAvatar(a.Root);
        }

        /// <summary>The passes NDMF ran in the Optimizing phase, by display name, in the order it ran them.</summary>
        List<string> OptimizingPasses()
        {
            var start = log.FindIndex(l => l.StartsWith("=== Processing phase Optimizing"));
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "NDMF ran no Optimizing phase");
            return log.Skip(start + 1)
                .TakeWhile(l => !l.StartsWith("=== Processing phase"))
                .Where(l => l.StartsWith("Processed pass "))
                .Select(l => l.Substring("Processed pass ".Length, l.LastIndexOf(" in ") - "Processed pass ".Length))
                .ToList();
        }

        [Test]
        public void TheAvatarIsOptimized_InOrder_AndTheComponentIsGone()
        {
            var (body, source) = Avatar(withComponent: true);

            Process();

            Assert.That(errors, Is.Empty, "the build logs no error");
            Assert.That(Triangles(inner.sharedMesh), Is.Zero);
            Assert.That(a.Root.GetComponentInChildren<AvatarMeshOptimizer>(true), Is.Null, "the pass removes its component");
            Assert.That(body.sharedMesh, Is.Not.SameAs(source));
            Assert.That(Triangles(body.sharedMesh), Is.LessThan(Triangles(source)));
            Assert.That(OptimizationReports.Latest?.AvatarName, Is.EqualTo(a.Root.name));
            Assert.That(OptimizationReports.Latest.Renderers.Single(r => r.Renderer == body).OutputMesh, Is.SameAs(body.sharedMesh));
            var g = OptimizationReports.Latest.Renderers.Single(r => r.Renderer == garment);
            TestContext.WriteLine($"garment: {g.SequenceLength} steps recorded, {g.TrianglesBefore} to {g.TrianglesAfter} triangles; {OptimizationReports.Latest.HiddenRestored} restored");
            Assert.That(g.SequenceLength, Is.GreaterThan(0), "the build decimated");
            Assert.That(OptimizationReports.Latest.Diagnostics.Select(d => d.Key), Has.No.Member(DiagnosticKeys.RendererFailed).And.No.Member(DiagnosticKeys.RejudgeFailed));
            Assert.That(log.Any(l => l.StartsWith("[Mesh Optimizer] " + a.Root.name)), "a summary reaches the console");

            var passes = OptimizingPasses();
            var ours = passes.IndexOf(MeshOptimizerPass.Instance.DisplayName);
            Assert.That(ours, Is.GreaterThanOrEqualTo(0), "the pass ran in Optimizing: " + string.Join(", ", passes));
            Assert.That(passes.IndexOf("GCGameObjectsPluginPass"), Is.InRange(0, ours - 1), "after Modular Avatar: " + string.Join(", ", passes));
            foreach (var probe in new[] { "com.anatawa12.avatar-optimizer", "dev.limitex.avatar-compressor.texture", "net.rs64.tex-trans-tool" })
                Assert.That(passes.IndexOf("Order probe: " + probe), Is.GreaterThan(ours), $"before {probe}: " + string.Join(", ", passes));
        }

        // Without the constraints each probe would run first, which is what makes the order above a test of them.
        [Test]
        public void TheProbes_SortBeforeThePlugin_AndModularAvatarAfterIt()
        {
            var ours = typeof(MeshOptimizerPlugin).FullName;
            foreach (var probe in new[] { typeof(AvatarOptimizerProbe), typeof(TextureCompressorProbe), typeof(TexTransToolProbe) })
                Assert.That(string.CompareOrdinal(probe.FullName, ours), Is.LessThan(0), probe.FullName);
            Assert.That(string.CompareOrdinal("nadena.dev.modular_avatar.core.editor.plugin.PluginDefinition", ours), Is.GreaterThan(0));
        }

        // The scale source reads the descriptor's merged layers through NDMF's animator services, resolving the clip's
        // path from the avatar root: an FX clip that can scale the garment's bone stops it covering, one that holds its
        // rest scale does not.
        [TestCase(0f, false)]
        [TestCase(1.2f, false)]
        [TestCase(1f, true)]
        public void AnFxClipScalingTheGarmentsBone_DecidesWhetherItCovers(float scale, bool covers)
        {
            var descriptor = a.Root.AddComponent<VRCAvatarDescriptor>();
            var skirt = a.Bone("Armature/Hips/Chest/Skirt");
            var source = a.Sphere(0.1f, 16, 32, name: "body");
            var body = a.Skinned("Body", source, new[] { a.Bone("Armature/Hips/Chest") });
            var garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { skirt });
            a.Optimizer().alwaysOn.Add(garment);

            var clip = a.Made(new AnimationClip { name = "skirt scale" });
            var curve = new AnimationCurve();
            curve.AddKey(new Keyframe(0, 1));
            curve.AddKey(new Keyframe(1, scale));
            clip.SetCurve("Armature/Hips/Chest/Skirt", typeof(Transform), "m_LocalScale.x", curve);
            var controller = a.Made(new UnityEditor.Animations.AnimatorController { name = "fx" });
            controller.AddLayer("skirt");
            controller.layers[0].stateMachine.AddState("scale").motion = clip;
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false },
            };
            descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

            Process();

            Assert.That(errors, Is.Empty, "the build logs no error");
            Assert.That(body.sharedMesh != source, Is.EqualTo(covers));
            Assert.That(OptimizationReports.Latest.UnstableBones.Any(p => p.EndsWith("Skirt")), Is.EqualTo(!covers));
        }

        public enum Second { None, DrivesAfter, HoldsBefore, HoldsAfter, HoldsInGesture, DriverInGestureHoldInFx, SyncedHoldAfter, MaskOmitsOne, MaskHasBoth, FirstLayerMaskOmitsOne, HumanoidMask }

        // Links read the merged layers through NDMF's animator services, by clip object and by the order layers play in:
        // one FX clip driving a key on two renderers links them; a second clip driving it on one keeps them apart; a layer
        // that only holds one where the renderer holds it takes no part if it plays before the driver, and refuses the
        // link if it plays after it. Gesture plays before FX.
        [TestCase(Second.None, true, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_LinksThem")]
        [TestCase(Second.DrivesAfter, false, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_Control_ASecondClipDrivingOneKeepsThemApart")]
        [TestCase(Second.HoldsBefore, true, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_AHoldOnOneInAnEarlierLayerTakesNoPart")]
        [TestCase(Second.HoldsAfter, false, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_AHoldOnOneInALaterLayerRefuses")]
        [TestCase(Second.HoldsInGesture, true, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_AHoldOnOneInGestureTakesNoPart")]
        [TestCase(Second.DriverInGestureHoldInFx, false, TestName = "AGestureClipDrivingAKeyOnTwoRenderers_AHoldOnOneInFxRefuses")]
        [TestCase(Second.SyncedHoldAfter, false, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_ALaterLayerSyncedToAHoldOnOneRefuses")]
        [TestCase(Second.MaskOmitsOne, false, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_InALayerWhoseMaskOmitsOneRefuses")]
        [TestCase(Second.FirstLayerMaskOmitsOne, false, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_UnderAFirstLayerMaskOmittingOneRefuses")]
        [TestCase(Second.MaskHasBoth, true, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_InALayerWhoseMaskHasBothLinks")]
        [TestCase(Second.HumanoidMask, true, TestName = "AnFxClipDrivingAKeyOnTwoRenderers_InALayerWithAHumanoidOnlyMaskLinks")]
        public void ClipsLayersAndLinks(Second second, bool links)
        {
            var descriptor = a.Root.AddComponent<VRCAvatarDescriptor>();
            var chest = a.Bone("Armature/Hips/Chest");
            SkinnedMeshRenderer Layer(string name, float radius)
            {
                var mesh = a.Sphere(radius, 16, 32, name: name);
                mesh.AddBlendShapeFrame("heels", 100f, mesh.vertices.Select(v => v.normalized * 0.01f).ToArray(), null, null);
                return a.Skinned(name, mesh, new[] { chest });
            }
            Layer("Body", 0.1f);
            a.Optimizer().alwaysOn.Add(Layer("Tights", 0.105f));

            AnimationClip Clip(string name, params (string path, float[] values)[] curves)
            {
                var clip = a.Made(new AnimationClip { name = name });
                foreach (var (path, values) in curves)
                    clip.SetCurve(path, typeof(SkinnedMeshRenderer), "blendShape.heels", new AnimationCurve(values.Select((v, i) => new Keyframe(i, v)).ToArray()));
                return clip;
            }
            UnityEditor.Animations.AnimatorController Controller(string name) => a.Made(new UnityEditor.Animations.AnimatorController { name = name });
            void AddLayer(UnityEditor.Animations.AnimatorController c, string name, AnimationClip clip)
            {
                c.AddLayer(name);
                c.layers[c.layers.Length - 1].stateMachine.AddState(name).motion = clip;
            }
            void Masked(UnityEditor.Animations.AnimatorController c, int index, params (string path, bool active)[] transforms)
            {
                var mask = a.Made(new AvatarMask { name = "mask" });
                mask.transformCount = transforms.Length;
                for (var i = 0; i < transforms.Length; i++)
                {
                    mask.SetTransformPath(i, transforms[i].path);
                    mask.SetTransformActive(i, transforms[i].active);
                }
                var layers = c.layers;
                layers[index].avatarMask = mask;
                c.layers = layers;
            }
            var heels = Clip("heels", ("Body", new[] { 0f, 100f }), ("Tights", new[] { 0f, 100f }));
            var hold = Clip("sweater", ("Tights", new[] { 0f, 0f }));
            var fx = Controller("fx");
            var gesture = Controller("gesture");
            switch (second)
            {
                case Second.None: AddLayer(fx, "heels", heels); break;
                case Second.DrivesAfter: AddLayer(fx, "heels", heels); AddLayer(fx, "sweater", Clip("sweater", ("Tights", new[] { 0f, 50f }))); break;
                case Second.HoldsBefore: AddLayer(fx, "sweater", hold); AddLayer(fx, "heels", heels); break;
                case Second.HoldsAfter: AddLayer(fx, "heels", heels); AddLayer(fx, "sweater", hold); break;
                case Second.HoldsInGesture: AddLayer(gesture, "sweater", hold); AddLayer(fx, "heels", heels); break;
                case Second.DriverInGestureHoldInFx: AddLayer(gesture, "heels", heels); AddLayer(fx, "sweater", hold); break;
                case Second.SyncedHoldAfter:
                    AddLayer(fx, "sweater", hold);
                    AddLayer(fx, "heels", heels);
                    fx.AddLayer(new UnityEditor.Animations.AnimatorControllerLayer { name = "synced", syncedLayerIndex = 0, defaultWeight = 1f, stateMachine = a.Made(new UnityEditor.Animations.AnimatorStateMachine()) });
                    break;
                case Second.MaskOmitsOne: AddLayer(fx, "heels", heels); Masked(fx, 0, ("Body", true), ("Tights", false)); break;
                case Second.MaskHasBoth: AddLayer(fx, "heels", heels); Masked(fx, 0, ("Body", true), ("Tights", true)); break;
                case Second.FirstLayerMaskOmitsOne: AddLayer(fx, "base", Clip("empty")); AddLayer(fx, "heels", heels); Masked(fx, 0, ("Body", true)); break;
                case Second.HumanoidMask: AddLayer(fx, "heels", heels); Masked(fx, 0); break;
            }
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Gesture, animatorController = gesture, isDefault = false },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx, isDefault = false },
            };
            descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

            Process();

            Assert.That(errors, Is.Empty, "the build logs no error");
            var report = OptimizationReports.Latest;
            Assert.That(report.AvatarName, Is.EqualTo(a.Root.name));
            if (links)
            {
                Assert.That(report.Links.Single().ToString(), Is.EqualTo("Body:heels, Tights:heels"));
                Assert.That(report.Configurations.Select(c => c.Weight), Is.EqualTo(new[] { 1f }));
            }
            else Assert.That(report.Links, Is.Empty);
        }

        [Test]
        public void WithoutTheComponent_ThePassChangesNothing()
        {
            var (body, source) = Avatar(withComponent: false);
            var before = OptimizationReports.Latest;

            Process();

            Assert.That(body.sharedMesh, Is.SameAs(source));
            Assert.That(OptimizationReports.Latest, Is.SameAs(before), "no report, so the pipeline never ran");
        }
    }
}
