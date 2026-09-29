using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Ryan6Vrc.MeshOptimizer.Tests.Occlusion;
using Unity.Collections;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // The state the pass calls rest is the one it evaluates across every renderer at once, so it has to be the state the
    // avatar stands in. A body and a second layer each hold a live key at full weight that pulls their surface inside a
    // boot; with the keys at zero both surfaces enclose the boot. Evaluated with live keys at zero and each key alone,
    // the boot reads as covered in every configuration, though as the avatar stands nothing covers it at all. The same
    // holds for decimation: its rest error is the one the avatar is seen with most.
    public class HeldKeyTests
    {
        const float BootRadius = 0.1f;

        AvatarFixture a;

        [SetUp]
        public void SetUp() => a = new AvatarFixture();

        [TearDown]
        public void TearDown() => a.Dispose();

        /// <summary>
        /// A sphere of <paramref name="radius"/> whose key "heels", at 100, moves every vertex out to <paramref name="keyed"/>,
        /// riding <paramref name="bone"/> and held by its renderer at <paramref name="held"/>.
        /// </summary>
        SkinnedMeshRenderer Layer(string name, Transform bone, float radius, float keyed, float held)
        {
            var mesh = a.Sphere(radius, 16, 32, name: name);
            mesh.AddBlendShapeFrame("heels", 100f, mesh.vertices.Select(v => v.normalized * (keyed - radius)).ToArray(), null, null);
            var renderer = a.Skinned(name, mesh, new[] { bone });
            renderer.SetBlendShapeWeight(0, held);
            return renderer;
        }

        /// <summary>
        /// Body and tights ride bones too far apart in the tree to cover each other, so each is judged against the boot
        /// alone; the boot rides both, so each of them can cover it.
        /// </summary>
        (SkinnedMeshRenderer body, SkinnedMeshRenderer tights, SkinnedMeshRenderer boot) Rig(float bodyKeyed, float tightsKeyed, float held)
        {
            var left = a.Bone("Armature/Hips/A1/A2/A3/BodyBone");
            var right = a.Bone("Armature/Hips/B1/B2/B3/TightsBone");
            var body = Layer("Body", left, 0.12f, bodyKeyed, held);
            var tights = Layer("Tights", right, 0.125f, tightsKeyed, held);
            var bootMesh = a.Sphere(BootRadius, 16, 32, name: "boot");
            bootMesh.boneWeights = bootMesh.vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f }).ToArray();
            var boot = a.Skinned("Boot", bootMesh, new[] { left, right });
            a.Optimizer().alwaysOn.AddRange(new Renderer[] { body, tights, boot });
            return (body, tights, boot);
        }

        /// <summary>Liveness as the pass classifies it, with an FX curve driving the key on each renderer between 0 and 100.</summary>
        Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> Classified(string key, params SkinnedMeshRenderer[] renderers)
        {
            var root = a.Root.transform;
            var inputs = renderers.Select(r => LivenessSources.Describe(r, root)).ToList();
            var bindings = inputs.Select(i => new ShapeKeyBinding(i.Path, key, new[] { 0f, 100f }));
            var table = ShapeKeyClassifier.Classify(inputs, bindings, new DrivenKey[0]);
            var result = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>();
            for (var i = 0; i < renderers.Length; i++) result[renderers[i]] = table[i];
            return result;
        }

        // Classified as the pass does, or left out of the liveness table, where the pipeline reads the renderer itself.
        [TestCase(true)]
        [TestCase(false)]
        public void BootUncoveredAsTheAvatarStands_IsKept_ThoughEachKeyAloneAtZeroCoversIt(bool classified)
        {
            // Held at 100, body and tights sit inside the boot; at zero, outside it.
            var (body, tights, boot) = Rig(bodyKeyed: 0.08f, tightsKeyed: 0.085f, held: 100f);

            var report = a.Run(tweak: input => { if (classified) input.Liveness = Classified("heels", body, tights); });

            Assert.That(report.OcclusionRan);
            Assert.That(Of(report, boot).HiddenRemoved, Is.Zero, "as the avatar stands, the boot is the outermost surface");
        }

        [Test]
        public void BootCoveredAsTheAvatarStands_AndInEveryStateAKeyReaches_IsRemoved()
        {
            // The same rig, but the keys move body and tights further out: covered held, and with either key at zero.
            var (body, tights, boot) = Rig(bodyKeyed: 0.13f, tightsKeyed: 0.135f, held: 100f);
            var bootTriangles = Triangles(boot.sharedMesh);

            var report = a.Run(tweak: input => input.Liveness = Classified("heels", body, tights));

            Assert.That(Of(report, boot).HiddenRemoved, Is.EqualTo(bootTriangles), "nothing the boot draws can be seen");
        }

        [Test]
        public void EveryTriangleHiddenFromHeldKeys_IsCoveredAtRestAndInEveryFrame_ByTheIndependentChecker()
        {
            // The solver and the checker read one evaluation mesh; this runs both on what extraction makes of held keys.
            var (body, tights, boot) = Rig(bodyKeyed: 0.13f, tightsKeyed: 0.135f, held: 100f);
            var liveness = Classified("heels", body, tights);
            var bones = new AvatarBones(a.Root.transform);
            var meshes = new[] { body, tights, boot }.Select(r => new OcclusionMesh
            {
                Mesh = RendererExtraction.Extract(r, bones, liveness.TryGetValue(r, out var l) ? l : new ShapeKeyLiveness[0], Allocator.Persistent, out _),
                Occludes = true,
            }).ToList();
            var parents = bones.Parents.ToArray();
            using var parentArray = new NativeArray<int>(parents, Allocator.Persistent);
            OcclusionResult[] results = null;
            try
            {
                Assert.That(meshes[0].Mesh.Frames.Length, Is.EqualTo(1), "the body's key is evaluated off, from where it is held");
                results = OcclusionSolver.Solve(meshes, new OcclusionBones { Parents = parentArray }, OcclusionSettings.Default, Allocator.Persistent, out _);
                Assert.That(results[2].HiddenCount, Is.GreaterThan(0));

                var report = CoverChecker.Check(meshes, parents, null, OcclusionSettings.Default, results);

                Assert.That(report.Ok, Is.True, report.ToString());
                Assert.That(report.HiddenChecked, Is.GreaterThan(0));
            }
            finally
            {
                if (results != null) foreach (var r in results) r.Dispose();
                foreach (var m in meshes) m.Mesh.Dispose();
            }
        }

        /// <summary>
        /// A flat 20 cm patch with a key that slides it along itself and tilts it 45 degrees: flat with the key off, flat
        /// with it full on, and bowed about 4 mm in between, where the slide and the tilt are out of step.
        /// </summary>
        SkinnedMeshRenderer Patch(string name, Vector3 at, float held)
        {
            var bone = a.Bone("Armature/" + name);
            bone.localPosition = at;
            var mesh = a.Grid(20, 0.01f, name);
            // One UV everywhere, so the UV term costs nothing and only the surface is judged.
            mesh.uv = new Vector2[mesh.vertexCount];
            mesh.AddBlendShapeFrame("fold", 100f, mesh.vertices.Select(v =>
            {
                var slide = 0.02f * Mathf.Cos(Mathf.PI * v.x / 0.2f);
                return new Vector3(slide, v.x + slide, 0f);
            }).ToArray(), null, null);
            var renderer = a.Skinned(name, mesh, new[] { bone }, parent: bone);
            renderer.SetBlendShapeWeight(0, held);
            return renderer;
        }

        [Test]
        public void DecimationError_IsTheErrorAsTheAvatarStands()
        {
            var off = Patch("Off", new Vector3(1, 0, 0), 0f);
            var partway = Patch("Partway", new Vector3(2, 0, 0), 30f);
            var optimizer = a.Optimizer(decimate: true, removeHidden: false);
            optimizer.target = TargetKind.Tolerance;
            optimizer.tolerance = 0.5f;

            var report = a.Run(tweak: input => input.Liveness = Classified("fold", off, partway));

            // Measured on the built meshes as the avatar stands, the way a viewer sees them.
            float AsItStands(SkinnedMeshRenderer r)
            {
                var rr = Of(report, r);
                var gate = GeometryGate.Measure(r, a.Root.transform, rr.SourceMesh, rr.OutputMesh, null, rr.Liveness, null, SurfaceDistanceSettings.Default);
                return gate.Stats(0, GateDirection.SourceToOptimized).Max;
            }
            var before = Of(report, partway).TrianglesBefore;
            Assert.That(Of(report, off).TrianglesAfter, Is.LessThan(before / 10), "flat in every state it reaches, it collapses");
            Assert.That(AsItStands(off), Is.LessThan(0.001f));
            Assert.That(Of(report, partway).TrianglesAfter, Is.GreaterThan(2 * Of(report, off).TrianglesAfter), "the bow it stands in is kept");
            Assert.That(AsItStands(partway), Is.LessThan(0.001f), "a 4 mm bow collapsed flat would read here");
        }
    }
}
