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

        SkinnedMeshRenderer inner;

        (SkinnedMeshRenderer body, Mesh source) Avatar(bool withComponent)
        {
            a.Root.AddComponent<VRCAvatarDescriptor>();
            var chest = a.Bone("Armature/Hips/Chest");
            var source = a.Sphere(0.1f, 16, 32, name: "body");
            var body = a.Skinned("Body", source, new[] { chest });
            var garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            // A small sphere inside a closed shell loses every triangle, so NDMF finishes the build over an empty mesh.
            var belly = a.Bone("Armature/Hips/Belly");
            belly.localPosition = new Vector3(0, 0, 1);
            inner = a.Skinned("Inner", a.Sphere(0.05f, 8, 16, name: "inner"), new[] { belly });
            var shell = a.Skinned("Shell", a.Sphere(0.06f, 12, 24, name: "shell"), new[] { belly });
            if (withComponent) a.Optimizer().alwaysOn.AddRange(new Renderer[] { garment, shell });
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
        // path from the avatar root: an FX clip that can collapse the garment's bone stops it covering, one that only
        // grows it does not.
        [TestCase(0f, false)]
        [TestCase(1.2f, true)]
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
            Assert.That(OptimizationReports.Latest.VanishingBones.Any(p => p.EndsWith("Skirt")), Is.EqualTo(!covers));
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
