using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Tests.Occlusion;
using Unity.Collections;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // A body and the tights over it carry the same key, and one clip drives both. The pipeline turns each link into
    // states of the whole avatar, one per weight, in which every renderer with a member takes its own frame; a key it
    // does not link still moves alone. Evaluating a linked key alone is a state the avatar never takes, and it can hide
    // what the state the avatar does take shows.
    public class LinkedKeyTests
    {
        const float BootRadius = 0.1f;

        AvatarFixture a;

        [SetUp]
        public void SetUp() => a = new AvatarFixture();

        [TearDown]
        public void TearDown() => a.Dispose();

        /// <summary>
        /// A sphere of <paramref name="radius"/> whose key "heels", at 100, moves every vertex out to <paramref name="keyed"/>.
        /// A sphere a key swells is made fine enough that no triangle outgrows the lattice it was sampled on at rest.
        /// </summary>
        SkinnedMeshRenderer Layer(string name, Transform bone, float radius, float keyed, int rings = 16)
        {
            var mesh = a.Sphere(radius, rings, 2 * rings, name: name);
            mesh.AddBlendShapeFrame("heels", 100f, mesh.vertices.Select(v => v.normalized * (keyed - radius)).ToArray(), null, null);
            return a.Skinned(name, mesh, new[] { bone });
        }

        /// <summary>
        /// Body and tights over a boot, on bones too far apart in the tree to cover each other, so each is judged against the
        /// boot alone; the boot rides both, so each can cover it. The key takes each layer to its keyed radius.
        /// </summary>
        (SkinnedMeshRenderer body, SkinnedMeshRenderer tights, SkinnedMeshRenderer boot) BootRig(float bodyKeyed, float tightsKeyed)
        {
            var left = a.Bone("Armature/Hips/A1/A2/A3/BodyBone");
            var right = a.Bone("Armature/Hips/B1/B2/B3/TightsBone");
            var body = Layer("Body", left, 0.12f, bodyKeyed);
            var tights = Layer("Tights", right, 0.125f, tightsKeyed);
            var bootMesh = a.Sphere(BootRadius, 16, 32, name: "boot");
            bootMesh.boneWeights = bootMesh.vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f }).ToArray();
            var boot = a.Skinned("Boot", bootMesh, new[] { left, right });
            a.Optimizer().alwaysOn.AddRange(new Renderer[] { body, tights, boot });
            return (body, tights, boot);
        }

        /// <summary>Liveness as the pass classifies it, with the key driven between 0 and 100 on each renderer.</summary>
        Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Classified(params SkinnedMeshRenderer[] renderers)
        {
            var root = a.Root.transform;
            var inputs = renderers.Select(r => LivenessSources.Describe(r, root)).ToList();
            var table = ShapeKeyClassifier.Classify(inputs, inputs.Select(i => new ShapeKeyBinding(i.Path, "heels", new[] { 0f, 100f })), new DrivenKey[0]);
            var result = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>();
            for (var i = 0; i < renderers.Length; i++) result[renderers[i]] = table[i];
            return result;
        }

        static LinkedKey[][] Linked(params SkinnedMeshRenderer[] renderers) => new[] { renderers.Select(r => new LinkedKey(r, 0)).ToArray() };

        // ------------------------------------------------------------------------------------------------
        // The defect: with both keys on, body and tights sink inside the boot and nothing covers it, though with either
        // key on alone the other layer still does.

        [TestCase(true, TestName = "BootExposedOnlyWithBothLinkedKeysOn_IsKept")]
        [TestCase(false, TestName = "BootExposedOnlyWithBothLinkedKeysOn_Control_UnlinkedEachKeyAloneLeavesItCovered")]
        public void BootUnderLinkedLayers(bool linked)
        {
            var (body, tights, boot) = BootRig(bodyKeyed: 0.08f, tightsKeyed: 0.085f);
            var bootTriangles = Triangles(boot.sharedMesh);

            var report = a.Run(tweak: input =>
            {
                input.Liveness = Classified(body, tights);
                if (linked) input.Links = Linked(body, tights);
            });

            Assert.That(report.OcclusionRan);
            Assert.That(Of(report, boot).HiddenRemoved, Is.EqualTo(linked ? 0 : bootTriangles));
            Assert.That(report.Links, Has.Count.EqualTo(linked ? 1 : 0));
        }

        // ------------------------------------------------------------------------------------------------
        // The mirror: the body's key pushes it out through the tights at rest, and the tights' key, linked to it, pushes
        // them out further. The body alone pokes through; as the avatar takes the state, it stays under.

        [TestCase(true, TestName = "BodyPushedOutWithLinkedTights_StaysCovered_AndIsRemoved")]
        [TestCase(false, TestName = "BodyPushedOutWithLinkedTights_Control_BodyKeyAlonePokesThrough")]
        public void BodyUnderLinkedTights(bool linked)
        {
            var bone = a.Bone("Armature/Hips/Leg");
            var body = Layer("Body", bone, 0.1f, 0.13f, rings: 32);
            var tights = Layer("Tights", bone, 0.105f, 0.135f, rings: 32);
            a.Optimizer().alwaysOn.Add(tights);
            var bodyTriangles = Triangles(body.sharedMesh);

            var report = a.Run(tweak: input =>
            {
                input.Liveness = Classified(body, tights);
                if (linked) input.Links = Linked(body, tights);
            });

            if (linked) Assert.That(Of(report, body).HiddenRemoved, Is.EqualTo(bodyTriangles));
            else Assert.That(Of(report, body).HiddenRemoved, Is.Zero);
        }

        [Test]
        public void EveryTriangleHiddenUnderLinkedKeys_IsCoveredInEveryConfiguration_ByTheIndependentChecker()
        {
            // Extraction and the solver on what the pipeline makes of a link, judged by the checker, which groups frames by id.
            var bone = a.Bone("Armature/Hips/Leg");
            var body = Layer("Body", bone, 0.1f, 0.13f, rings: 32);
            var tights = Layer("Tights", bone, 0.105f, 0.135f, rings: 32);
            var liveness = Classified(body, tights);
            var linked = new[] { new LiveFrame(1, new[] { 0 }, new[] { 100f }) };
            var bones = new AvatarBones(a.Root.transform);
            var meshes = new[] { body, tights }.Select(r => new OcclusionMesh
            {
                Mesh = RendererExtraction.Extract(r, bones, liveness[r], linked, Allocator.Persistent, out _),
                Occludes = r == tights,
            }).ToList();
            var parents = bones.Parents.ToArray();
            using var parentArray = new NativeArray<int>(parents, Allocator.Persistent);
            OcclusionResult[] results = null;
            try
            {
                Assert.That(meshes.Select(m => m.Mesh.Frames.Length), Is.EqualTo(new[] { 1, 1 }), "each renderer's one frame is its part in the link");
                results = OcclusionSolver.Solve(meshes, new OcclusionBones { Parents = parentArray }, OcclusionSettings.Default, Allocator.Persistent, out var stats);
                Assert.That(stats.Configurations, Is.EqualTo(1));
                Assert.That(results[0].HiddenCount, Is.EqualTo(results[0].HiddenTriangles.Length));

                var check = CoverChecker.Check(meshes, parents, null, OcclusionSettings.Default, results);

                Assert.That(check.Ok, Is.True, check.ToString());
                Assert.That(check.HiddenChecked, Is.GreaterThan(0));
            }
            finally
            {
                if (results != null) foreach (var r in results) r.Dispose();
                foreach (var m in meshes) m.Mesh.Dispose();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // What the report says, and what the gate builds from it.

        [Test]
        public void TheReport_NamesEachLinkAndConfiguration_ByPathAndKey()
        {
            var (body, tights, boot) = BootRig(bodyKeyed: 0.13f, tightsKeyed: 0.135f);

            var report = a.Run(tweak: input =>
            {
                input.Liveness = Classified(body, tights);
                input.Links = Linked(body, tights);
            });

            Assert.That(report.Links.Single().Keys.Select(k => k.ToString()), Is.EqualTo(new[] { "Body:heels", "Tights:heels" }));
            var configuration = report.Configurations.Single();
            Assert.That(configuration.Link, Is.SameAs(report.Links[0]));
            Assert.That(configuration.Weight, Is.EqualTo(1f));
            Assert.That(configuration.Weights, Is.EqualTo(new[] { 100f, 100f }));
            Assert.That(report.OcclusionStats.Configurations, Is.EqualTo(1), "the link at full weight, and nothing moved alone");
            Assert.That(report.Summary(), Does.Contain("linked Body:heels, Tights:heels: 1 configurations"));
        }

        // A key that moves alone though its clips drive it with another is counted by why: the refusals the linker found, for
        // keys the pass evaluates, and a link the pass cannot evaluate as one, here because its other member wears Cloth.
        [Test]
        public void KeysThatMoveAloneThoughDrivenWithAnother_AreCountedInTheReport_ByReason()
        {
            var (body, tights, _) = BootRig(bodyKeyed: 0.13f, tightsKeyed: 0.135f);
            tights.gameObject.AddComponent<Cloth>();

            var report = a.Run(tweak: input =>
            {
                input.Liveness = Classified(body, tights);
                input.Links = Linked(body, tights);
                input.Unlinked = new[] { (new LinkedKey(body, 0), LinkRefusal.Masked), (new LinkedKey(tights, 0), LinkRefusal.Masked) };
            });

            Assert.That(report.Links, Is.Empty);
            Assert.That(report.LinkRefusals, Is.EquivalentTo(new Dictionary<LinkRefusal, int> { [LinkRefusal.Masked] = 1, [LinkRefusal.NotEvaluated] = 1 }),
                "the tights are not evaluated, so neither refusal counts them");
            Assert.That(report.Summary(), Does.Contain("1 shape keys driven with another moved alone: NotEvaluated."));
        }

        [Test]
        public void ALinkWhoseKeysReachDifferentWeights_IsEvaluatedAtTheUnionOfThem()
        {
            // The tights' key is authored with a frame at 50: its path bends there, so the link is evaluated at half too.
            var (body, tights, _) = BootRig(bodyKeyed: 0.13f, tightsKeyed: 0.135f);
            var mesh = Object.Instantiate(tights.sharedMesh);
            a.Made(mesh);
            mesh.ClearBlendShapes();
            var v = mesh.vertices;
            mesh.AddBlendShapeFrame("heels", 50f, v.Select(p => p.normalized * 0.004f).ToArray(), null, null);
            mesh.AddBlendShapeFrame("heels", 100f, v.Select(p => p.normalized * 0.01f).ToArray(), null, null);
            tights.sharedMesh = mesh;

            var report = a.Run(tweak: input =>
            {
                input.Liveness = Classified(body, tights);
                input.Links = Linked(body, tights);
            });

            Assert.That(report.Configurations.Select(c => c.Weight), Is.EqualTo(new[] { 0.5f, 1f }));
            Assert.That(report.Configurations[0].Weights, Is.EqualTo(new[] { 50f, 50f }));
        }

        [Test]
        public void ALinkMemberThePassDoesNotEvaluateAsLive_LeavesTheOthersToMoveAlone()
        {
            var (body, tights, _) = BootRig(bodyKeyed: 0.13f, tightsKeyed: 0.135f);
            var liveness = Classified(body);
            liveness[tights] = new[] { ShapeKeyLiveness.Dead };

            var report = a.Run(tweak: input =>
            {
                input.Liveness = liveness;
                input.Links = Linked(body, tights);
            });

            Assert.That(report.Links, Is.Empty);
            Assert.That(report.OcclusionStats.Configurations, Is.EqualTo(1), "the body's key alone");
        }

        [Test]
        public void TheGate_MeasuresCrossingInEachLinkedConfiguration_AsTheAvatarTakesIt()
        {
            // The boot is covered in every state, so the pass empties it: the gate measures it as having no surface.
            var (body, tights, boot) = BootRig(bodyKeyed: 0.13f, tightsKeyed: 0.135f);
            var report = a.Run(tweak: input =>
            {
                input.Liveness = Classified(body, tights);
                input.Links = Linked(body, tights);
            });

            var inputs = GateReportInputs.From(report);
            var configuration = inputs.Configurations.Single();
            Assert.That(configuration.Name, Is.EqualTo("linked Body:heels, Tights:heels at 1"));
            Assert.That(configuration.Weights[body], Is.EqualTo(new[] { 100f }));
            Assert.That(configuration.Weights[tights], Is.EqualTo(new[] { 100f }));
            Assert.That(configuration.Weights.ContainsKey(boot), Is.False, "a renderer with no member stands as it is");

            var crossing = CrossingGate.Measure(a.Root.transform, inputs.Crossing, null, inputs.Configurations, CrossingSettings.Default);
            Assert.That(crossing.Configurations, Has.Member("linked Body:heels, Tights:heels at 1"));
            Assert.That(crossing.Pairs.Any(p => p.Configuration == configuration.Name), "the pairs are measured in it");
        }
    }
}
