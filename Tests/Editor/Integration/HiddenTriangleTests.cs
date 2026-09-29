using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using UnityEditor;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // A body sphere under a bowl-shaped garment on the same chest bone: everything the bowl covers from every
    // direction goes, the open top stays. Each test changes one input the pass owes occlusion and checks the
    // verdict turns with it, against the same scene where it does not.
    public class HiddenTriangleTests
    {
        const float BodyRadius = 0.1f;
        const float GarmentRadius = 0.105f;

        AvatarFixture a;
        Transform chest;
        Mesh bodyMesh;
        SkinnedMeshRenderer body;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            bodyMesh = a.Sphere(BodyRadius, 16, 32, name: "body");
            body = a.Skinned("Body", bodyMesh, new[] { chest });
        }

        [TearDown]
        public void TearDown() => a.Dispose();

        Mesh Bowl(bool inward = false) => a.Sphere(GarmentRadius, 24, 48, 60f, 180f, inward, "garment");

        SkinnedMeshRenderer Garment(Transform bone, bool alwaysOn = true, Material material = null, bool inward = false)
        {
            var garment = a.Skinned("Garment", Bowl(inward), new[] { bone }, material: material);
            var optimizer = a.Optimizer();
            if (alwaysOn) optimizer.alwaysOn.Add(garment);
            return garment;
        }

        int HiddenOnBody(OptimizationReport report) => Of(report, body).HiddenRemoved;

        void AssertBowlVerdict(RendererReport r)
        {
            var polar = Polar(bodyMesh);
            Assert.That(r.HiddenSourceTriangles, Is.Not.Null);
            for (var t = 0; t < polar.Length; t++)
            {
                if (polar[t] >= 100f) Assert.That(r.HiddenSourceTriangles[t], Is.True, $"triangle {t} at {polar[t]} degrees is under the bowl");
                if (polar[t] <= 40f) Assert.That(r.HiddenSourceTriangles[t], Is.False, $"triangle {t} at {polar[t]} degrees is under the open top");
            }
        }

        [Test]
        public void BodyUnderAlwaysOnGarment_LosesCoveredTriangles_AndTheOutputDrawsExactlyTheRest()
        {
            var garment = Garment(chest);
            var sourceTriangles = bodyMesh.triangles;
            var sourceVertices = bodyMesh.vertexCount;

            var report = a.Run();

            var r = Of(report, body);
            AssertBowlVerdict(r);
            Assert.That(r.HiddenRemoved, Is.GreaterThan(0));
            Assert.That(body.sharedMesh, Is.SameAs(r.OutputMesh), "the renderer draws the written mesh");
            Assert.That(Triangles(body.sharedMesh), Is.EqualTo(sourceTriangles.Length / 3 - r.HiddenRemoved));

            // The output's triangles, named by source vertex, are exactly the source triangles occlusion kept.
            var drawn = new HashSet<(int, int, int)>();
            var output = body.sharedMesh.triangles;
            for (var i = 0; i < output.Length; i += 3)
                drawn.Add((r.KeptVertices[output[i]], r.KeptVertices[output[i + 1]], r.KeptVertices[output[i + 2]]));
            var kept = new HashSet<(int, int, int)>();
            for (var t = 0; t < sourceTriangles.Length / 3; t++)
                if (!r.HiddenSourceTriangles[t]) kept.Add((sourceTriangles[3 * t], sourceTriangles[3 * t + 1], sourceTriangles[3 * t + 2]));
            Assert.That(drawn, Is.EquivalentTo(kept));

            Assert.That(bodyMesh.triangles, Is.EqualTo(sourceTriangles), "the source mesh is never written");
            Assert.That(bodyMesh.vertexCount, Is.EqualTo(sourceVertices));
            Assert.That(Of(report, garment).Skip, Is.EqualTo(SkipReason.Unchanged), "nothing covers the garment");
            Assert.That(garment.sharedMesh.name, Is.EqualTo("garment"));
        }

        [Test]
        public void SameGarmentNotListedAlwaysOn_HidesNothing()
        {
            Garment(chest, alwaysOn: false);

            var report = a.Run();

            Assert.That(Of(report, body).Skip, Is.EqualTo(SkipReason.Unchanged));
            Assert.That(body.sharedMesh, Is.SameAs(bodyMesh));
            Assert.That(report.OcclusionRan, Is.False, "with no always-on renderer there is nothing to occlude with");
        }

        [Test]
        public void AlwaysOnGarmentThatIsOffAtBuild_HidesNothing_AndIsNamed()
        {
            var garment = Garment(chest);
            garment.gameObject.SetActive(false);

            var report = a.Run();

            Assert.That(HiddenOnBody(report), Is.Zero);
            Assert.That(report.Diagnostics.Any(d => d.Key == DiagnosticKeys.AlwaysOnIsOff && d.Args.Contains(garment)));
        }

        // Modular Avatar moves toggle-deletable vertices onto a "NaNimatedBone for ..." bone one step under the
        // original; at two kin steps those faces would cover the body though a toggle can delete them.
        [TestCase("NaNimatedBone for Hide sleeves", false)]
        [TestCase("Accessory", true)]
        public void OccluderOnAVanishNamedBone_HidesNothing(string boneName, bool hides)
        {
            Garment(a.Bone("Armature/Hips/Chest/" + boneName));

            var report = a.Run();

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(hides));
            Assert.That(report.UnstableBones.Any(p => p.EndsWith(boneName)), Is.EqualTo(!hides));
        }

        // A clip that moves a bone's scale either way takes its faces off the kin they covered, which do not scale with
        // it; a clip that holds the rest scale changes nothing.
        [TestCase(new[] { 1f, 0f }, false)]
        [TestCase(new[] { 1f, 0.5f }, false)]
        [TestCase(new[] { 1f, float.NaN }, false)]
        [TestCase(new[] { 1f, -1f }, false)]
        [TestCase(new[] { 1f, 1.2f }, false)]
        [TestCase(new[] { 1f, 1f }, true)]
        public void OccluderOnABoneAClipScales_HidesOnlyWhenTheClipHoldsItsScale(float[] values, bool hides)
        {
            var bone = a.Bone("Armature/Hips/Chest/Skirt");
            Garment(bone);
            a.ScaleClip(bone, values);

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(hides));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OccluderOnTheChildOfACollapsingBone_HidesNothing(bool collapse)
        {
            var parent = a.Bone("Armature/Hips/Chest/Collapse");
            Garment(a.Bone("Armature/Hips/Chest/Collapse/Child"));
            if (collapse) a.ScaleClip(parent, 1f, 0f);

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(!collapse), "the child two kin steps away covers unless its parent can collapse");
            Assert.That(report.UnstableBones.Any(p => p.EndsWith("Collapse/Child")), Is.EqualTo(collapse));
        }

        // The candidate side. A body on a bone a slider grows, under cover riding that bone's parent, swells out through
        // cover that stays put, so none of it is deleted; without the slider the same body is.
        [TestCase(true)]
        [TestCase(false)]
        public void ACandidateOnAScaledBone_UnderCoverOnItsParent_IsKept(bool scaled)
        {
            var core = a.Bone("Armature/Hips/Chest/Core");
            var covered = a.Skinned("Covered", a.Sphere(BodyRadius, 16, 32, name: "covered"), new[] { core });
            Garment(chest);
            if (scaled) a.ScaleClip(core, 1f, 1.2f);

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            var r = Of(report, covered);
            Assert.That(r.HiddenRemoved > 0, Is.EqualTo(!scaled));
            Assert.That(r.KeptOnUnstableBones > 0, Is.EqualTo(scaled), "the report counts what the rule kept");
            Assert.That(report.KeptOnUnstableBones, Is.EqualTo(r.KeptOnUnstableBones));
            Assert.That(HiddenOnBody(report), Is.GreaterThan(0), "the body on the steady bone beside it is still judged");
        }

        // Any weight on an unstable bone moves a corner with it, however small: a body riding the steady chest with a
        // trace of weight on a bone a clip collapses is kept, as the occluder side treats a trace of weight too.
        [TestCase(true)]
        [TestCase(false)]
        public void ACandidateWithATraceOfWeightOnAnUnstableBone_IsKept(bool scaled)
        {
            var core = a.Bone("Armature/Hips/Chest/Core");
            var mesh = a.Sphere(BodyRadius, 16, 32, name: "traced");
            mesh.boneWeights = mesh.vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 0.995f, boneIndex1 = 1, weight1 = 0.005f }).ToArray();
            var traced = a.Skinned("Traced", mesh, new[] { chest, core });
            Garment(chest);
            if (scaled) a.ScaleClip(core, 1f, 0f);

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            Assert.That(Of(report, traced).HiddenRemoved > 0, Is.EqualTo(!scaled));
        }

        // An override names the material the user sees; an NDMF plugin that cloned it and recorded the clone as its
        // replacement still gets the override. An unrecorded clone does not, and the report names the idle override.
        [TestCase(true)]
        [TestCase(false)]
        public void MaterialOverride_FollowsARecordedClone(bool recorded)
        {
            var original = a.Opaque();
            var clone = a.Made(new Material(original));
            Garment(chest, material: clone);
            a.Optimizer().materials.Add(new Runtime.MaterialOcclusion { material = original, mode = Runtime.OcclusionMode.NeverOccludes });

            OptimizationReport report;
            using (new nadena.dev.ndmf.ObjectRegistryScope(new nadena.dev.ndmf.ObjectRegistry(a.Root.transform)))
            {
                if (recorded) nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(original, clone);
                report = a.Run();
            }

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(!recorded), "NeverOccludes applies only when the clone traces back");
            Assert.That(report.UnmatchedMaterialOverrides.Contains(original), Is.EqualTo(!recorded));
        }

        // Occlusion reads the front of a face by Unity's winding, and a back-culled material draws only its front.
        // An inward-facing bowl shows its back to anyone outside, so it covers only when its material draws the back:
        // culling off, or culling the front.
        [TestCase(2, false)]
        [TestCase(1, true)]
        [TestCase(0, true)]
        public void InwardFacingOccluder_HidesOnlyWhenItsMaterialDrawsItsBack(int cull, bool hides)
        {
            var material = a.Made(new Material(CullShader()));
            material.SetFloat(OccluderMaterials.CullProperty, cull);
            Garment(chest, material: material, inward: true);

            var report = a.Run();

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(hides));
        }

        Shader CullShader() => a.Made(ShaderUtil.CreateShaderAsset(
            "Shader \"Hidden/MeshOptimizerTests/Cull\" { Properties { [Enum(UnityEngine.Rendering.CullMode)] _Cull (\"Cull\", Float) = 2 } " +
            "SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { Cull [_Cull] } } }", true));

        [Test]
        public void CullingOf_ReadsTheCullModeConvention_AndBackWithoutIt()
        {
            var material = a.Made(new Material(CullShader()));
            material.SetFloat("_Cull", 0);
            Assert.That(OccluderMaterials.CullingOf(material), Is.EqualTo(Core.Occlusion.FaceCulling.Off));
            material.SetFloat("_Cull", 1);
            Assert.That(OccluderMaterials.CullingOf(material), Is.EqualTo(Core.Occlusion.FaceCulling.Front));
            material.SetFloat("_Cull", 2);
            Assert.That(OccluderMaterials.CullingOf(material), Is.EqualTo(Core.Occlusion.FaceCulling.Back));
            Assert.That(OccluderMaterials.CullingOf(a.Opaque()), Is.EqualTo(Core.Occlusion.FaceCulling.Back), "Standard states no cull");
        }

        [Test]
        public void MaterialOverride_NeverOccludes_HidesNothing()
        {
            var material = a.Opaque();
            Garment(chest, material: material);
            a.Optimizer().materials.Add(new Runtime.MaterialOcclusion { material = material, mode = Runtime.OcclusionMode.NeverOccludes });

            Assert.That(HiddenOnBody(a.Run()), Is.Zero);
        }

        [Test]
        public void RemoveHiddenTrianglesOff_HidesNothing()
        {
            Garment(chest);
            a.Optimizer(removeHidden: false);

            var report = a.Run();

            Assert.That(HiddenOnBody(report), Is.Zero);
            Assert.That(Of(report, body).HiddenSourceTriangles, Is.Null);
        }
    }
}
