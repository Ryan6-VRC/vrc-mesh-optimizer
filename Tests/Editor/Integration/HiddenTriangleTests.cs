using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
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
    internal class HiddenTriangleTests
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

        // Copying a component's values from another avatar leaves its list naming that avatar's renderers.
        [Test]
        public void AlwaysOnGarmentOfAnotherAvatar_HidesNothing_AndIsNamed()
        {
            using var other = new AvatarFixture("Other");
            var garment = other.Skinned("Garment", Bowl(), new[] { other.Bone("Armature/Hips/Chest") });
            a.Optimizer().alwaysOn.Add(garment);

            var report = a.Run();

            Assert.That(HiddenOnBody(report), Is.Zero);
            Assert.That(report.Diagnostics.Any(d => d.Key == DiagnosticKeys.AlwaysOnOutsideAvatar && d.Args.Contains(garment)));
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

        // An override names the material the user sees; the build may put a clone in its place. An NDMF plugin that
        // records its clone leads back to the original. VRCFury records nothing but keeps the original's name, so a
        // never-occludes override also takes a same-named build-time clone: a wrong match there only keeps triangles.
        // A clone under another name matches nothing, and the idle safety setting is raised as a warning.
        [TestCase(true, true, false, TestName = "{m}(recorded)")]
        [TestCase(false, true, false, TestName = "{m}(unrecorded, same name)")]
        [TestCase(false, false, true, TestName = "{m}(unrecorded, renamed)")]
        public void NeverOccludesOverride_FollowsAClone(bool recorded, bool sameName, bool hides)
        {
            var original = a.Opaque();
            original.name = "Garment";
            var clone = a.Made(new Material(original) { name = sameName ? original.name : "Garment Variant" });
            Garment(chest, material: clone);
            a.Optimizer().materials.Add(new Runtime.MaterialOcclusion { material = original, mode = Runtime.OcclusionMode.NeverOccludes });

            OptimizationReport report;
            using (new nadena.dev.ndmf.ObjectRegistryScope(new nadena.dev.ndmf.ObjectRegistry(a.Root.transform)))
            {
                if (recorded) nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(original, clone);
                report = a.Run();
            }

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(hides), "never-occludes applies to the clone");
            Assert.That(report.UnmatchedMaterialOverrides.Contains(original), Is.EqualTo(hides));
            Assert.That(report.Diagnostics.Count(d => d.Key == DiagnosticKeys.NeverOccludesUnmatched && d.Warning && ReferenceEquals(d.Args[0], original)),
                Is.EqualTo(hides ? 1 : 0), "an idle never-occludes override is a warning");
        }

        // An always-opaque override makes a surface hide what is behind it, so a wrong match deletes triangles: it keeps
        // matching strictly. A same-named clone of a see-through material stays see-through, against the same override
        // set on the clone itself, which hides.
        [TestCase(false, false)]
        [TestCase(true, true)]
        public void ForceOpaqueOverride_DoesNotFollowAnUnrecordedClone(bool onTheClone, bool hides)
        {
            var original = a.Made(new Material(CullShader()) { name = "Veil", renderQueue = 3000 });
            var clone = a.Made(new Material(original) { name = original.name });
            Garment(chest, material: clone);
            a.Optimizer().materials.Add(new Runtime.MaterialOcclusion { material = onTheClone ? clone : original, mode = Runtime.OcclusionMode.ForceOpaque });

            var report = a.Run();

            Assert.That(HiddenOnBody(report) > 0, Is.EqualTo(hides));
            Assert.That(report.UnmatchedMaterialOverrides.Count, Is.EqualTo(hides ? 0 : 1));
            Assert.That(report.Diagnostics.Any(d => d.Key == DiagnosticKeys.NeverOccludesUnmatched), Is.False, "only a never-occludes override is warned about");
        }

        // A name two overrides share cannot say whose clone a material is. The never-occludes override on the other
        // material must not count as matched through it, or its warning goes quiet while nothing honours it.
        [Test]
        public void NeverOccludesOverride_IsNotCreditedThroughANameAnotherOverrideShares()
        {
            var never = a.Made(new Material(CullShader()) { name = "Veil" });
            var opaque = a.Made(new Material(CullShader()) { name = "Veil", renderQueue = 3000 });
            var clone = a.Made(new Material(opaque) { name = opaque.name });
            Garment(chest, material: clone);
            a.Optimizer().materials.Add(new Runtime.MaterialOcclusion { material = never, mode = Runtime.OcclusionMode.NeverOccludes });
            a.Optimizer().materials.Add(new Runtime.MaterialOcclusion { material = opaque, mode = Runtime.OcclusionMode.ForceOpaque });

            var report = a.Run();

            Assert.That(HiddenOnBody(report), Is.Zero, "the clone is judged by its own see-through material");
            Assert.That(report.Diagnostics.Count(d => d.Key == DiagnosticKeys.NeverOccludesUnmatched && ReferenceEquals(d.Args[0], never)), Is.EqualTo(1));
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

        // A cup inside the bowl, open to the top like a skirt, not always on. Its outside is covered; its inside faces the
        // open top. Drawn back-culled only the covered outside shows and it goes; drawn with its back too, the inside
        // shows through the open top and it stays.
        [TestCase(2, true)]
        [TestCase(1, false)]
        [TestCase(0, false)]
        public void CandidateThatDrawsItsBack_IsKept_WhenOnlyItsFrontIsCovered(int cull, bool hides)
        {
            var material = a.Made(new Material(CullShader()));
            material.SetFloat(OccluderMaterials.CullProperty, cull);
            var cup = a.Skinned("Cup", a.Sphere(BodyRadius, 16, 32, 100f, 180f, name: "cup"), new[] { chest }, material: material);
            Garment(chest);

            var report = a.Run();

            var r = Of(report, cup);
            Assert.That(r.HiddenRemoved > 0, Is.EqualTo(hides));
            if (!hides) Assert.That(cup.sharedMesh.triangles.Length, Is.EqualTo(r.HiddenSourceTriangles.Length * 3), "every triangle of the cup is drawn");
        }

        /// <summary>
        /// A back-culled main pass, and, when <paramref name="furPass"/> is given, a second pass by that name drawn cull-off,
        /// as a fur pass is.
        /// </summary>
        Material Furred(string shaderName, string furPass)
        {
            var source = $"Shader \"{shaderName}\" {{ Properties {{ _Cull (\"Cull\", Float) = 2 }} SubShader {{ Tags {{ \"RenderType\"=\"Opaque\" }} " +
                         "Pass { Name \"FORWARD\" Cull [_Cull] } " + (furPass != null ? $"Pass {{ Name \"{furPass}\" Cull Off }} " : "") + "} }";
            return a.Made(new Material(a.Made(ShaderUtil.CreateShaderAsset(source, true))));
        }

        // Fur grows past its cover, so a body drawn with fur keeps every triangle the garment covers, counted as kept for
        // fur; the same body without a fur pass loses them. lilToon's fur passes are told by name, Poiyomi's by its shader's.
        [TestCase("Hidden/MeshOptimizerTests/lilToonFur", "FORWARD_FUR", true)]
        [TestCase("Hidden/MeshOptimizerTests/lilToonFurOnly", "FORWARD_ADD_FUR", true)]
        [TestCase(".poiyomi/Poiyomi Toon + Lil Fur", "Base", true)]
        [TestCase("Hidden/MeshOptimizerTests/TwoPass", "FORWARD_BACK_ONLY", false)]
        [TestCase("Hidden/MeshOptimizerTests/NoFur", null, false)]
        public void ABodyDrawnWithFur_KeepsItsCoveredTriangles(string shaderName, string furPass, bool fur)
        {
            body.sharedMaterials = new[] { Furred(shaderName, furPass) };
            Garment(chest);

            var report = a.Run();

            var r = Of(report, body);
            Assert.That(r.HiddenRemoved > 0, Is.EqualTo(!fur));
            Assert.That(r.KeptForFur, fur ? Is.GreaterThanOrEqualTo(Polar(bodyMesh).Count(p => p >= 100f)) : Is.Zero, "the report counts what fur kept");
            if (fur) StringAssert.Contains($"fur kept {r.KeptForFur} covered triangles", report.Summary());
        }

        // Fur is grown per triangle, so decimating a furred mesh thins its fur: every triangle is kept, while the same flat
        // grid without fur loses its interior at no error.
        [TestCase(true)]
        [TestCase(false)]
        public void AMeshDrawnWithFur_IsNotDecimated(bool fur)
        {
            var grid = a.Grid(12, 0.01f, "pelt");
            var pelt = a.Rigid("Pelt", grid, chest, Furred("Hidden/MeshOptimizerTests/Pelt", fur ? "FORWARD_FUR" : null));
            a.Optimizer(decimate: true, removeHidden: false);

            var report = a.Run();

            var r = Of(report, pelt);
            Assert.That(r.TrianglesAfter == r.TrianglesBefore, Is.EqualTo(fur));
            Assert.That(r.LockedVertices, Is.EqualTo(fur ? grid.vertexCount : 0));
        }

        // A shader that states no cull property but draws both sides, and the MToon and UTS convention _CullMode.
        [TestCase("Shader \"Hidden/MeshOptimizerTests/HardCullOff\" { SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { Cull Off } } }", -1, false)]
        [TestCase("Shader \"Hidden/MeshOptimizerTests/CullMode\" { Properties { _CullMode (\"Cull\", Float) = 2 } SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { Cull [_CullMode] } } }", 0, false)]
        [TestCase("Shader \"Hidden/MeshOptimizerTests/CullMode\" { Properties { _CullMode (\"Cull\", Float) = 2 } SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { Cull [_CullMode] } } }", 2, true)]
        public void CandidateWhoseShaderCullingIsNotRead_IsTakenAsDrawingItsBack(string source, int cullMode, bool hides)
        {
            var material = a.Made(new Material(a.Made(ShaderUtil.CreateShaderAsset(source, true))));
            if (cullMode >= 0) material.SetFloat("_CullMode", cullMode);
            var cup = a.Skinned("Cup", a.Sphere(BodyRadius, 16, 32, 100f, 180f, name: "cup"), new[] { chest }, material: material);
            Garment(chest);

            var report = a.Run();

            Assert.That(Of(report, cup).HiddenRemoved > 0, Is.EqualTo(hides));
        }

        // The same unread culling on an always-on cup: as a cover it blocks as back-culled, but as something that might be
        // hidden it is taken to draw both sides.
        [Test]
        public void AlwaysOnCandidateWhoseShaderCullingIsNotRead_IsTakenAsDrawingItsBack()
        {
            var shader = a.Made(ShaderUtil.CreateShaderAsset(
                "Shader \"Hidden/MeshOptimizerTests/HardCullOff\" { SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { Cull Off } } }", true));
            var cup = a.Skinned("Cup", a.Sphere(BodyRadius, 16, 32, 100f, 180f, name: "cup"), new[] { chest }, material: a.Made(new Material(shader)));
            Garment(chest);
            a.Optimizer().alwaysOn.Add(cup);

            var report = a.Run();

            Assert.That(Of(report, cup).HiddenRemoved, Is.Zero);
        }

        // Unity draws a material slot past the last submesh over the last submesh again: here a second, double-sided
        // material over the cup's one submesh.
        [Test]
        public void CandidateWithASurplusMaterialSlotDrawingItsBack_IsKept()
        {
            Material Cull(int cull)
            {
                var m = a.Made(new Material(CullShader()));
                m.SetFloat(OccluderMaterials.CullProperty, cull);
                return m;
            }
            var cup = a.Skinned("Cup", a.Sphere(BodyRadius, 16, 32, 100f, 180f, name: "cup"), new[] { chest }, material: Cull(2));
            cup.sharedMaterials = new[] { Cull(2), Cull(0) };
            Garment(chest);

            var report = a.Run();

            Assert.That(Of(report, cup).HiddenRemoved, Is.Zero, "the surplus slot draws the cup's inside, open to the top");
        }

        Shader CullShader() => a.Made(ShaderUtil.CreateShaderAsset(
            "Shader \"Hidden/MeshOptimizerTests/Cull\" { Properties { [Enum(UnityEngine.Rendering.CullMode)] _Cull (\"Cull\", Float) = 2 } " +
            "SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { Cull [_Cull] } } }", true));

        // The sides a material draws as something that might be hidden, read toward drawing more: the cull convention when
        // the shader declares it, back only for Unity's built-in shaders, both for any shader that states nothing, and back
        // for an empty slot, which draws nothing.
        [TestCase("cull", 2f, FaceCulling.Back)]
        [TestCase("cull", 1f, FaceCulling.Front)]
        [TestCase("cull", 0f, FaceCulling.Off)]
        [TestCase("Standard", 0f, FaceCulling.Back)]
        [TestCase("unknown", 0f, FaceCulling.Off)]
        [TestCase(null, 0f, FaceCulling.Back)]
        public void SidesOf_ReadsWhatTheMaterialStates_TowardDrawingMore(string shader, float cull, FaceCulling sides)
        {
            Material material = null;
            if (shader == "cull") material = a.Made(new Material(CullShader()));
            else if (shader == "Standard") material = a.Made(new Material(Shader.Find("Standard")));
            else if (shader == "unknown")
                material = a.Made(new Material(a.Made(ShaderUtil.CreateShaderAsset(
                    "Shader \"Hidden/MeshOptimizerTests/NoCull\" { SubShader { Tags { \"RenderType\"=\"Opaque\" } Pass { } } }", true))));
            if (shader == "cull") material.SetFloat(OccluderMaterials.CullProperty, cull);

            Assert.That(OccluderMaterials.SidesOf(material, outlines: false), Is.EqualTo(sides));
        }

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
