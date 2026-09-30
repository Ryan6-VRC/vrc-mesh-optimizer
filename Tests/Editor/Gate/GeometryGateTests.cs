using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Unity.Collections;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    // The decimator claims its surface stays close with each live key driven and the skeleton posed, not only as the avatar stands.
    // The gate tests that claim by baking both meshes on the renderer in each configuration, so these tests build
    // meshes whose damage shows only in one configuration and check the gate finds it there, in metres, and leaves the
    // avatar as it found it.
    public class GeometryGateTests
    {
        GateScene _scene;

        [SetUp]
        public void SetUp() => _scene = new GateScene();

        [TearDown]
        public void TearDown() => _scene.Dispose();

        static readonly SurfaceDistanceSettings Settings = SurfaceDistanceSettings.Default;

        // An upright tube on two bones, the joint at half height, blended over 0.35 to 0.65; the optimized tube drops
        // every ring between 0.3 and 0.7, which is exact while the tube is straight.
        (Transform root, SkinnedMeshRenderer renderer, Mesh source, Mesh optimized) TwoBoneTube()
        {
            var root = _scene.Node("Avatar", null, Vector3.zero);
            var armature = _scene.Node("Armature", root, Vector3.zero);
            var lower = _scene.Node("Lower", armature, Vector3.zero);
            var upper = _scene.Node("Upper", lower, new Vector3(0, 0.5f, 0));
            var tube = new Tube(Vector3.zero, Vector3.up, 1f, 0.05f, 16, 20);
            var source = _scene.Track(tube.Mesh("tube", r => (tube.AlongAxis(r) - 0.35f) / 0.3f));
            var flare = new Vector3[source.vertexCount];
            for (var s = 0; s < tube.Segments; s++) flare[tube.Index(s, 10)] = (tube.Vertices[tube.Index(s, 10)] - new Vector3(0, 0.5f, 0)) * 0.2f;
            source.AddBlendShapeFrame("flare", 100f, flare, null, null);
            var renderer = _scene.Skin(_scene.Node("Body", root, Vector3.zero), source, lower, upper);
            var kept = tube.AllRings().Where(r => tube.AlongAxis(r) <= 0.3f + 1e-4f || tube.AlongAxis(r) >= 0.7f - 1e-4f).ToArray();
            var optimized = _scene.Optimized(source, tube.TrianglesBetween(kept));
            return (root, renderer, source, optimized);
        }

        static readonly GatePose Bent = new BoneRotationPose("bent", ("Armature/Lower/Upper", Quaternion.Euler(0, 0, 90)));

        [Test]
        public void TubeDecimatedAcrossItsJoint_ReadsMoreErrorBentThanAtRest()
        {
            var (root, renderer, source, optimized) = TwoBoneTube();

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, new[] { ShapeKeyLiveness.Dead }, new[] { Bent }, Settings);

            Assert.That(report.Configurations.Select(c => c.Configuration.Name), Is.EqualTo(new[] { "as it stands", "bent" }));
            var rest = report.Stats(0, GateDirection.Combined).Max;
            var bent = report.Stats(1, GateDirection.SourceToOptimized).Max;
            Assert.That(rest, Is.LessThan(1e-5f), "straight, the dropped rings lay on the facets that replace them");
            Assert.That(bent, Is.GreaterThan(0.01f), "bent, the blended rings round the corner the straight facets cut");
            Assert.That(report.Worst(GateDirection.SourceToOptimized, Statistic.Max), Is.EqualTo(1));
            Assert.That(report.Worst(GateDirection.Combined, Statistic.Mean), Is.EqualTo(1));
            Assert.That(report.Skipped, Is.Empty);
        }

        (Transform root, SkinnedMeshRenderer renderer, Mesh source, Mesh optimized) SheetRig(float rootScale = 1f, float rendererScale = 1f,
            Action<Transform, Transform> place = null)
        {
            var root = _scene.Node("Avatar", null, Vector3.zero);
            root.localScale = Vector3.one * rootScale;
            var bone = _scene.Node("Bone", root, new Vector3(0.1f, 0.2f, 0.3f));
            var holder = _scene.Node("Holder", root, new Vector3(0f, 0.5f, 0f));
            var at = _scene.Node("Sheet", holder, Vector3.zero);
            at.localScale = Vector3.one * rendererScale;
            place?.Invoke(holder, at);
            var source = _scene.Track(Sheet.Mesh("sheet"));
            Sheet.AddKey(source, "bump", new Vector3(0, 0, 0.01f));
            Sheet.AddKey(source, "held", new Vector3(0, 0, 0.004f));
            Sheet.AddKey(source, "never", new Vector3(0, 0, 1f));
            var renderer = _scene.Skin(at, source, bone);
            var optimized = _scene.Optimized(source, Sheet.Triangles(withoutMiddle: true));
            return (root, renderer, source, optimized);
        }

        [Test]
        public void LiveKeyAtItsDrivenMaximum_ReadsTheErrorTheKeyIntroduces()
        {
            var (root, renderer, source, optimized) = SheetRig();
            var liveness = new[] { ShapeKeyLiveness.Live(0.6f), ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead };

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);

            Assert.That(report.Configurations.Count, Is.EqualTo(2));
            var key = report.Configurations[1].Configuration;
            Assert.That((key.Kind, key.Name, key.ShapeKey), Is.EqualTo((ConfigurationKind.ShapeKey, "bump", 0)));
            Assert.That(key.Weight, Is.EqualTo(60f).Within(1e-4f));
            Assert.That(report.Stats(0, GateDirection.Combined).Max, Is.LessThan(1e-6f), "flat at rest, the removed vertex was redundant");
            // At 60 the bump stands 6 mm over the fan that replaced its vertex.
            var lost = report.Configurations[1].Distance.AToB;
            Assert.That(lost.Stats.Max, Is.EqualTo(0.006f).Within(1e-5f));
            Assert.That(lost.MaxTriangle, Is.GreaterThanOrEqualTo(0));
            Assert.That(report.Stats(1, GateDirection.OptimizedToSource).Max, Is.LessThan(lost.Stats.Max));
            Assert.That(report.Worst(GateDirection.SourceToOptimized, Statistic.Max), Is.EqualTo(1));
        }

        [Test]
        public void TwoFrameKey_ReadsErrorOnlyItsIntermediateFrameShows_AtThatFramesWeight()
        {
            var (root, renderer, source, _) = SheetRig();
            // At 50 the key lifts the middle vertex, which the optimized mesh lacks; at 100 it only lifts a corner both keep.
            var half = new Vector3[source.vertexCount];
            half[Sheet.Middle] = new Vector3(0, 0, 0.008f);
            var full = new Vector3[source.vertexCount];
            full[0] = new Vector3(0, 0, 0.005f);
            source.AddBlendShapeFrame("rise", 50f, half, null, null);
            source.AddBlendShapeFrame("rise", 100f, full, null, null);
            var optimized = _scene.Optimized(source, Sheet.Triangles(withoutMiddle: true));
            var liveness = new[] { ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead, ShapeKeyLiveness.Live(1f) };

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);

            Assert.That(report.Configurations.Select(c => (c.Configuration.Name, c.Configuration.Weight)),
                Is.EqualTo(new[] { ("as it stands", 0f), ("rise", 50f), ("rise", 100f) }));
            Assert.That(report.Stats(1, GateDirection.SourceToOptimized).Max, Is.EqualTo(0.008f).Within(1e-5f), "the intermediate frame");
            Assert.That(report.Stats(2, GateDirection.Combined).Max, Is.LessThan(1e-6f), "the full frame moves only what both meshes keep");
            Assert.That(report.Worst(GateDirection.SourceToOptimized, Statistic.Max), Is.EqualTo(1));
        }

        // The gate measures a live key where the optimizer evaluated it, so an error the optimizer accepted in a
        // configuration is one the gate reads there. Both ask one rule; this checks each hands it the same key.
        [TestCase(0f, 0f, 1f)]
        [TestCase(0f, 0f, 0.3f)]
        [TestCase(0f, -0.5f, 0f)]
        [TestCase(100f, 0f, 1f)]
        [TestCase(60f, 0f, 1f)]
        [TestCase(40f, -0.5f, 0.8f)]
        public void ShapeKeyConfigurations_AreTheWeightsExtractionEvaluates(float held, float drivenMin, float drivenMax)
        {
            var root = _scene.Node("Avatar", null, Vector3.zero);
            var bone = _scene.Node("Bone", root, Vector3.zero);
            var mesh = _scene.Track(Sheet.Mesh("frames"));
            foreach (var w in new[] { 25f, 50f, 100f })
            {
                var d = new Vector3[mesh.vertexCount];
                d[Sheet.Middle] = new Vector3(0, 0, w * 1e-4f);
                mesh.AddBlendShapeFrame("stepped", w, d, null, null);
            }
            var renderer = _scene.Skin(_scene.Node("Sheet", root, Vector3.zero), mesh, bone);
            renderer.SetBlendShapeWeight(0, held);
            var liveness = new[] { ShapeKeyLiveness.Live(drivenMin, drivenMax, held) };

            var report = GeometryGate.Measure(renderer, root, mesh, _scene.Optimized(mesh, Sheet.Triangles(false)), null, liveness, null, Settings);
            using var eval = MeshExtractor.Extract(mesh, SkinningPose.Unskinned(Matrix4x4.identity, 0), liveness, Allocator.Temp, out var frames);

            var measured = report.Configurations.Where(c => c.Configuration.Kind == ConfigurationKind.ShapeKey).Select(c => c.Configuration.Weight).ToArray();
            Assert.That(measured, Is.EqualTo(frames.Select(f => f.Weight)));
            Assert.That(measured, Is.Not.Empty.And.Not.Contains(held));
        }

        [Test]
        public void AShapeKeyConfiguration_MovesOneKeyFromTheAvatarAsItStands()
        {
            var (root, renderer, source, optimized) = SheetRig();
            // Both keys lift the removed middle vertex: "held" by 4 mm and held on, "bump" by 10 mm when driven.
            renderer.SetBlendShapeWeight(1, 100f);
            var liveness = new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Live(0f, 1f, 100f), ShapeKeyLiveness.Dead };

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);

            float Lost(string name) => report.Stats(report.Configurations.Select((c, i) => (c, i)).Single(x => x.c.Configuration.ToString() == name).i, GateDirection.SourceToOptimized).Max;
            Assert.That(Lost("as it stands, every key at the weight the renderer holds"), Is.EqualTo(0.004f).Within(1e-5f));
            Assert.That(Lost("live keys at zero, frozen keys held"), Is.LessThan(1e-6f));
            Assert.That(Lost("shape key 'bump' at 100"), Is.EqualTo(0.014f).Within(1e-5f), "the bump on top of the held key");
            Assert.That(Lost("shape key 'held' at 0"), Is.LessThan(1e-6f), "the held key taken off");
        }

        [Test]
        public void LiveKeyHeldAtFullWeight_ReadsItsErrorAsItStands_AndNotWithLiveKeysAtZero()
        {
            var (root, renderer, source, optimized) = SheetRig();
            // The avatar stands with the bump on; only then does the removed middle vertex show.
            renderer.SetBlendShapeWeight(0, 100f);
            var liveness = new[] { ShapeKeyLiveness.Live(0f, 1f, 100f), ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead };
            var tilted = new BoneRotationPose("tilted", ("Bone", Quaternion.Euler(40, 0, 0)));

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { tilted }, Settings);

            var names = report.Configurations.Select(c => c.Configuration.ToString()).ToArray();
            Assert.That(names, Is.EqualTo(new[]
            {
                "as it stands, every key at the weight the renderer holds", "live keys at zero, frozen keys held", "shape key 'bump' at 0",
                "pose 'tilted'", "pose 'tilted' with live keys at zero",
            }));
            Assert.That(report.Configurations.Select(c => c.Configuration.Kind), Is.EqualTo(new[]
                { ConfigurationKind.AsItStands, ConfigurationKind.LiveKeysAtZero, ConfigurationKind.ShapeKey, ConfigurationKind.Pose, ConfigurationKind.Pose }));
            float Lost(int c) => report.Stats(c, GateDirection.SourceToOptimized).Max;
            Assert.That(Lost(0), Is.EqualTo(0.01f).Within(1e-5f), "as it stands");
            Assert.That(Lost(1), Is.LessThan(1e-6f), "live keys at zero");
            Assert.That(Lost(2), Is.LessThan(1e-6f), "the key taken off");
            Assert.That(Lost(3), Is.EqualTo(0.01f).Within(1e-5f), "posed as it stands");
            Assert.That(Lost(4), Is.LessThan(1e-6f), "posed with live keys at zero");
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100f), "the held weight is left as found");
        }

        [Test]
        public void NoLiveKeyHeldOffZero_AddsNoLiveKeysAtZero()
        {
            var (root, renderer, source, optimized) = SheetRig();
            // A frozen key held off zero is held in every configuration, so live keys at zero would be the avatar as it stands.
            renderer.SetBlendShapeWeight(1, 50f);
            var liveness = new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Frozen(50f), ShapeKeyLiveness.Dead };

            var tilted = new BoneRotationPose("tilted", ("Bone", Quaternion.Euler(40, 0, 0)));

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { tilted }, Settings);

            Assert.That(report.Configurations.Select(c => c.Configuration.ToString()),
                Is.EqualTo(new[] { "as it stands, every key at the weight the renderer holds", "shape key 'bump' at 100", "pose 'tilted'" }));
            Assert.That(report.Skipped, Is.Empty);
        }

        [Test]
        public void RendererOnAnInactiveObject_IsBakedAsWhenActive()
        {
            // A toggled-off outfit sits under an inactive object, and it is optimized all the same.
            var (root, renderer, source, optimized) = SheetRig();
            var liveness = new[] { ShapeKeyLiveness.Live(0.6f), ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead };
            var tilted = new BoneRotationPose("tilted", ("Bone", Quaternion.Euler(40, 0, 0)));
            var active = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { tilted }, Settings);
            renderer.gameObject.SetActive(false);

            var inactive = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { tilted }, Settings);

            Assert.That(renderer.gameObject.activeSelf, Is.False);
            for (var c = 0; c < active.Configurations.Count; c++)
                Assert.That(inactive.Stats(c, GateDirection.SourceToOptimized).Max, Is.EqualTo(active.Stats(c, GateDirection.SourceToOptimized).Max),
                    active.Configurations[c].Configuration.ToString());
            Assert.That(inactive.Stats(1, GateDirection.SourceToOptimized).Max, Is.EqualTo(0.006f).Within(1e-5f));
        }

        [Test]
        public void FrozenKeyHoldsItsWeight_AndADeadKeyIsNeverApplied()
        {
            var (root, renderer, source, optimized) = SheetRig();
            renderer.SetBlendShapeWeight(1, 50f);
            var liveness = new[] { ShapeKeyLiveness.Dead, ShapeKeyLiveness.Frozen(50f), ShapeKeyLiveness.Dead };

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);

            Assert.That(report.Configurations.Select(c => c.Configuration.Name), Is.EqualTo(new[] { "as it stands" }));
            // Half of the held key's 4 mm; the dead key would have read a metre.
            Assert.That(report.Stats(0, GateDirection.SourceToOptimized).Max, Is.EqualTo(0.002f).Within(1e-5f));
        }

        [Test]
        public void BothBakes_LandInOneSpace_InWorldMetres()
        {
            // The avatar root scaled 2, the renderer 3 under it: the sheet is drawn six times its mesh size, and so is its key.
            var (root, renderer, source, optimized) = SheetRig(rootScale: 2f, rendererScale: 3f);
            var liveness = new[] { ShapeKeyLiveness.Live(0.6f), ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead };

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);

            Assert.That(report.Stats(0, GateDirection.Combined).Max, Is.LessThan(1e-5f));
            Assert.That(report.Stats(1, GateDirection.SourceToOptimized).Max, Is.EqualTo(6f * 0.006f).Within(1e-4f));
        }

        // Mirrored, non-uniform, and skewed by a rotated renderer under a non-uniformly scaled parent.
        static readonly Action<Transform, Transform>[] Placements =
        {
            (holder, at) => at.localScale = new Vector3(-2f, 3f, 1.5f),
            (holder, at) => holder.localScale = new Vector3(1f, 1f, -1f),
            (holder, at) =>
            {
                holder.localScale = new Vector3(1f, 2f, 3f);
                at.localRotation = Quaternion.Euler(30f, 20f, 0f);
            },
        };

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void MirroredNonUniformOrSkewedRenderer_ReadsTheDistanceTheRendererDraws(int placement)
        {
            var (root, renderer, source, optimized) = SheetRig(place: Placements[placement]);
            var liveness = new[] { ShapeKeyLiveness.Live(0.6f), ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead };
            // The sheet is drawn through the renderer's matrix; the bump stands off the drawn plane by the part of its
            // drawn displacement along the drawn plane's normal.
            var m = renderer.transform.localToWorldMatrix;
            var normal = Vector3.Cross(m.MultiplyVector(Vector3.right), m.MultiplyVector(Vector3.up)).normalized;
            var expected = Mathf.Abs(Vector3.Dot(normal, m.MultiplyVector(new Vector3(0, 0, 0.006f))));

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);

            Assert.That(report.Stats(0, GateDirection.Combined).Max, Is.LessThan(1e-5f));
            Assert.That(report.Stats(1, GateDirection.SourceToOptimized).Unmatched, Is.Zero);
            Assert.That(report.Stats(1, GateDirection.SourceToOptimized).Max, Is.EqualTo(expected).Within(1e-4f * expected));
        }

        [Test]
        public void TrianglesRemovedAsHidden_AreNotReadAsHoles()
        {
            var (root, renderer, source, optimized) = SheetRig();
            var hidden = MeshTriangles.Read(source).triangles.Select(t => t.x == Sheet.Middle || t.y == Sheet.Middle || t.z == Sheet.Middle).ToArray();
            // The optimized mesh without the hexagon at all: hidden triangles deleted, not refanned.
            var all = MeshTriangles.Read(source).triangles;
            var deleted = _scene.Optimized(source, all.Where((_, i) => !hidden[i]).SelectMany(t => new[] { t.x, t.y, t.z }).ToArray());
            var liveness = new[] { ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead, ShapeKeyLiveness.Dead };

            var unmasked = GeometryGate.Measure(renderer, root, source, deleted, null, liveness, null, Settings);
            var masked = GeometryGate.Measure(renderer, root, source, deleted, hidden, liveness, null, Settings);

            Assert.That(unmasked.Stats(0, GateDirection.SourceToOptimized).Max, Is.GreaterThan(0.005f));
            Assert.That(masked.Stats(0, GateDirection.Combined).Max, Is.Zero);
        }

        // A mesh on a bone standing at zero scale has no area as the avatar stands, so neither side has anything to
        // sample. The gate says so, and ranks that configuration worst, since a surface it could not check cannot pass.
        [Test]
        public void AConfigurationThatSampledNothing_ReadsAsNothingSampled_AndAsTheWorst()
        {
            var (root, renderer, source, optimized) = TwoBoneTube();
            var liveness = new[] { ShapeKeyLiveness.Dead };
            var measured = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { Bent }, Settings);
            root.Find("Armature/Lower").localScale = Vector3.zero;

            var collapsed = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { Bent }, Settings);

            foreach (var direction in new[] { GateDirection.SourceToOptimized, GateDirection.OptimizedToSource, GateDirection.Combined })
            {
                Assert.That(collapsed.Stats(0, direction).Max, Is.NaN, direction.ToString());
                Assert.That(collapsed.Stats(0, direction).Sampled, Is.False, direction.ToString());
                Assert.That(measured.Stats(0, direction).Sampled, Is.True, direction.ToString());
            }
            foreach (var line in collapsed.Lines(GateCamera.Default))
            {
                StringAssert.Contains("nothing sampled", line);
                StringAssert.DoesNotContain(" mm", line);
            }
            var mixed = new GeometryReport(renderer, measured.Path, new[] { measured.Configurations[1], collapsed.Configurations[0], measured.Configurations[0] },
                Array.Empty<SkippedConfiguration>());
            Assert.That(mixed.Worst(GateDirection.Combined, Statistic.Max), Is.EqualTo(1));
            Assert.That(mixed.Worst(GateDirection.SourceToOptimized, Statistic.Mean), Is.EqualTo(1));
        }

        [Test]
        public void MeshRenderer_IsMeasuredAsItStands_AndItsPosesAreNamedAsNotMeasured()
        {
            var root = _scene.Node("Avatar", null, Vector3.zero);
            var at = _scene.Node("Prop", root, Vector3.zero);
            at.localScale = Vector3.one * 2f;
            var source = _scene.Track(Sheet.Mesh("prop"));
            var bumped = source.vertices;
            bumped[Sheet.Middle] += new Vector3(0, 0, 0.005f);
            source.vertices = bumped;
            at.gameObject.AddComponent<MeshFilter>().sharedMesh = source;
            var renderer = at.gameObject.AddComponent<MeshRenderer>();
            var optimized = _scene.Optimized(source, Sheet.Triangles(withoutMiddle: true));

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, null, new[] { Bent }, Settings);

            Assert.That(report.Configurations.Count, Is.EqualTo(1));
            Assert.That(report.Stats(0, GateDirection.SourceToOptimized).Max, Is.EqualTo(0.01f).Within(1e-5f), "5 mm under a scale of 2");
            Assert.That(report.Skipped.Select(s => s.Name), Is.EqualTo(new[] { "pose 'bent'" }));
        }

        [Test]
        public void MusclePoses_OnARigWithNoHumanoid_AreSkippedByName()
        {
            var (root, renderer, source, optimized) = TwoBoneTube();

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, new[] { ShapeKeyLiveness.Live(1f) }, PoseBank.Default, Settings);

            Assert.That(report.Configurations.Select(c => c.Configuration.Kind), Is.EqualTo(new[] { ConfigurationKind.AsItStands, ConfigurationKind.ShapeKey }));
            Assert.That(report.Skipped.Select(s => s.Name), Is.EqualTo(PoseBank.Default.Select(p => $"pose '{p.Name}'")));
            StringAssert.Contains("humanoid", report.Skipped[0].Reason);
            Assert.That(report.Lines(GateCamera.Default).Length, Is.EqualTo(2 + PoseBank.Default.Count));
        }

        [Test]
        public void BadInput_IsRefusedByName_BeforeAnythingMoves()
        {
            var (root, renderer, source, optimized) = TwoBoneTube();
            var before = State.Of(root, renderer);

            var e = Assert.Throws<ArgumentException>(() => GeometryGate.Measure(renderer, root, source, optimized, null, null, null, Settings));
            StringAssert.Contains("liveness", e.Message);
            e = Assert.Throws<ArgumentException>(() => GeometryGate.Measure(renderer, root, source, optimized, new bool[3], new[] { ShapeKeyLiveness.Dead }, null, Settings));
            StringAssert.Contains("removed-triangle", e.Message);
            var missing = new BoneRotationPose("missing", ("Armature/Lower/Upper", Quaternion.identity), ("Armature/Nowhere", Quaternion.identity));
            e = Assert.Throws<ArgumentException>(() => GeometryGate.Measure(renderer, root, source, optimized, null, new[] { ShapeKeyLiveness.Dead }, new[] { missing }, Settings));
            StringAssert.Contains("Armature/Nowhere", e.Message);
            Assert.That(State.Of(root, renderer), Is.EqualTo(before));

            // The renderer holding a key where liveness does not record it: the avatar as it stands is not the optimizer's rest.
            renderer.SetBlendShapeWeight(0, 100f);
            before = State.Of(root, renderer);
            e = Assert.Throws<ArgumentException>(() => GeometryGate.Measure(renderer, root, source, optimized, null, new[] { ShapeKeyLiveness.Live(1f) }, null, Settings));
            StringAssert.Contains("holds shape key 'flare' at 100 and its liveness records 0", e.Message);

            Assert.That(State.Of(root, renderer), Is.EqualTo(before));
        }

        [Test]
        public void RendererHoldingAMeshWithOtherKeys_IsRefused_BecauseItsWeightsCannotSayHowTheAvatarStands()
        {
            var (root, renderer, source, optimized) = SheetRig();
            var other = _scene.Track(Sheet.Mesh("other"));
            Sheet.AddKey(other, "held", new Vector3(0, 0, 0.004f));
            renderer.sharedMesh = other;
            renderer.SetBlendShapeWeight(0, 100f);
            var liveness = new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Dead };

            var e = Assert.Throws<ArgumentException>(() => GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings));
            StringAssert.Contains("'other'", e.Message);

            // Holding the optimized mesh, whose keys are the source's by index, is how the gate may find a built avatar.
            renderer.sharedMesh = optimized;
            renderer.SetBlendShapeWeight(0, 0f);
            renderer.SetBlendShapeWeight(1, 100f);
            liveness = new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Live(0f, 1f, 100f), ShapeKeyLiveness.Dead };
            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, null, Settings);
            Assert.That(report.Configurations[0].Configuration.Kind, Is.EqualTo(ConfigurationKind.AsItStands));
            Assert.That(report.Stats(0, GateDirection.SourceToOptimized).Max, Is.EqualTo(0.004f).Within(1e-5f));
            Assert.That(report.Configurations[1].Configuration.Kind, Is.EqualTo(ConfigurationKind.LiveKeysAtZero));
            Assert.That(renderer.sharedMesh, Is.SameAs(optimized));
        }

        sealed class DestroyingPose : GatePose
        {
            public DestroyingPose() : base("destroys") { }
            internal override bool NeedsHumanoid => false;

            internal override void Apply(Transform root, HumanPoseHandler humanoid)
            {
                root.Find("Armature/Lower").localRotation *= Quaternion.Euler(30, 0, 0);
                UnityEngine.Object.DestroyImmediate(root.GetComponentInChildren<SkinnedMeshRenderer>());
                throw new InvalidOperationException("forced failure after the renderer is gone");
            }
        }

        [Test]
        public void RendererDestroyedMidRun_StillRestoresTransforms_FreesTheBakes_AndKeepsTheFailure()
        {
            var (root, renderer, source, optimized) = TwoBoneTube();
            string[] Transforms() => State.Of(root, renderer).Where(l => !l.StartsWith("mesh ") && !l.StartsWith("weight ")).ToArray();
            var before = Transforms();

            var e = Assert.Throws<InvalidOperationException>(() =>
                GeometryGate.Measure(renderer, root, source, optimized, null, new[] { ShapeKeyLiveness.Dead }, new GatePose[] { new DestroyingPose() }, Settings));

            StringAssert.Contains("renderer is gone", e.Message);
            Assert.That(renderer == null, "the pose destroyed it");
            Assert.That(Transforms(), Is.EqualTo(before));
        }

        [Test]
        public void AvatarIsLeftAsFound_AfterARun_AndAfterAFailureMidRun()
        {
            var (root, renderer, source, optimized) = TwoBoneTube();
            // The avatar as it stands: a key held off zero and the joint already turned.
            renderer.SetBlendShapeWeight(0, 37f);
            root.Find("Armature/Lower/Upper").localRotation = Quaternion.Euler(0, 0, 10);
            var liveness = new[] { ShapeKeyLiveness.Live(0f, 1f, 37f) };
            var before = State.Of(root, renderer);

            var report = GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new[] { Bent }, Settings);
            Assert.That(report.Configurations.Select(c => c.Configuration.ToString()), Is.EqualTo(new[]
            {
                "as it stands, every key at the weight the renderer holds", "live keys at zero, frozen keys held", "shape key 'flare' at 0", "shape key 'flare' at 100",
                "pose 'bent'", "pose 'bent' with live keys at zero",
            }));
            Assert.That(State.Of(root, renderer), Is.EqualTo(before), "after a run");

            // The failure comes after the key's bake left the optimized mesh on the renderer, as the pose turns a bone.
            var e = Assert.Throws<InvalidOperationException>(() =>
                GeometryGate.Measure(renderer, root, source, optimized, null, liveness, new GatePose[] { Bent, new ThrowingPose() }, Settings));
            StringAssert.Contains("forced failure", e.Message);
            Assert.That(State.Of(root, renderer), Is.EqualTo(before), "after a failure");
        }

        /// <summary>Everything the gate may touch, as exact text: every transform, the renderer's mesh and weights, and how many meshes and objects exist.</summary>
        internal static class State
        {
            public static string[] Of(Transform root, SkinnedMeshRenderer renderer)
            {
                string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
                var lines = new List<string>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    Vector3 p = t.localPosition, s = t.localScale;
                    var r = t.localRotation;
                    lines.Add($"{t.name} p {F(p.x)} {F(p.y)} {F(p.z)} r {F(r.x)} {F(r.y)} {F(r.z)} {F(r.w)} s {F(s.x)} {F(s.y)} {F(s.z)}");
                }
                if (renderer != null)
                {
                    lines.Add($"mesh {renderer.sharedMesh.GetInstanceID()}");
                    for (var k = 0; k < renderer.sharedMesh.blendShapeCount; k++) lines.Add($"weight {k} {F(renderer.GetBlendShapeWeight(k))}");
                }
                lines.Add($"meshes {Resources.FindObjectsOfTypeAll<Mesh>().Length}");
                lines.Add($"objects {Resources.FindObjectsOfTypeAll<GameObject>().Length}");
                return lines.ToArray();
            }
        }

        [Test]
        public void PoseBank_BendsEachJointItNames_InEditMode()
        {
            Assert.That(Application.isPlaying, Is.False, "this proves muscle poses in edit mode");
            var human = new Humanoid(_scene);
            var animator = human.Root.GetComponent<Animator>();
            Assert.That(animator.avatar.isValid && animator.avatar.isHuman, "the fixture must be a valid humanoid");
            var expected = new Dictionary<string, HumanBodyBones[]>
            {
                ["elbows-bent"] = new[] { HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm },
                ["knees-bent"] = new[] { HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg },
                ["arms-up"] = new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm },
                ["arms-down"] = new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm },
                ["hips-flexed"] = new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg },
                ["wrists-bent"] = new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand },
                ["fists"] = new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleDistal },
                ["neck-turned"] = new[] { HumanBodyBones.Neck },
            };
            Assert.That(PoseBank.Default.Select(p => p.Name), Is.EquivalentTo(expected.Keys));

            var transforms = human.Root.GetComponentsInChildren<Transform>(true);
            var rest = transforms.Select(t => t.localRotation).ToArray();
            var restPositions = transforms.Select(t => t.localPosition).ToArray();
            using var handler = new HumanPoseHandler(animator.avatar, human.Root);
            var failures = new List<string>();
            foreach (var pose in PoseBank.Default)
            {
                pose.Apply(human.Root, handler);
                foreach (var bone in expected[pose.Name])
                {
                    var i = Array.IndexOf(transforms, human.Bones[bone]);
                    var angle = Quaternion.Angle(rest[i], transforms[i].localRotation);
                    TestContext.WriteLine($"{pose.Name}: {bone} turned {angle:0.#} degrees");
                    if (angle < 20f) failures.Add($"{pose.Name}: {bone} turned {angle:0.#} degrees");
                }
                for (var i = 0; i < transforms.Length; i++)
                {
                    transforms[i].localRotation = rest[i];
                    transforms[i].localPosition = restPositions[i];
                }
            }
            Assert.That(failures, Is.Empty);
        }

        [Test]
        public void HumanoidArmDecimatedAcrossTheElbow_ReadsItsErrorInTheBanksElbowPose_AndIsLeftAsFound()
        {
            var human = new Humanoid(_scene);
            var upper = human.Bones[HumanBodyBones.LeftUpperArm];
            var lower = human.Bones[HumanBodyBones.LeftLowerArm];
            var elbow = Vector3.Distance(upper.position, lower.position);
            var tube = new Tube(upper.position, lower.position - upper.position, elbow + 0.25f, 0.035f, 16, 21);
            var source = _scene.Track(tube.Mesh("arm", r => (tube.AlongAxis(r) - (elbow - 0.06f)) / 0.12f));
            var renderer = _scene.Skin(_scene.Node("Arm", human.Root, Vector3.zero), source, upper, lower);
            var kept = tube.AllRings().Where(r => Mathf.Abs(tube.AlongAxis(r) - elbow) > 0.08f).ToArray();
            var optimized = _scene.Optimized(source, tube.TrianglesBetween(kept));
            var before = State.Of(human.Root, renderer);

            var report = GeometryGate.Measure(renderer, human.Root, source, optimized, null, Array.Empty<ShapeKeyLiveness>(), PoseBank.Default, Settings);

            Assert.That(report.Skipped, Is.Empty);
            Assert.That(report.Configurations.Count, Is.EqualTo(1 + PoseBank.Default.Count));
            var worst = report.Worst(GateDirection.SourceToOptimized, Statistic.Max);
            Assert.That(report.Configurations[worst].Configuration.Name, Is.EqualTo("elbows-bent"));
            Assert.That(report.Stats(0, GateDirection.Combined).Max, Is.LessThan(1e-4f), "in T-pose the arm is straight");
            Assert.That(report.Stats(worst, GateDirection.SourceToOptimized).Max, Is.GreaterThan(0.005f));
            Assert.That(State.Of(human.Root, renderer), Is.EqualTo(before));
        }
    }
}
