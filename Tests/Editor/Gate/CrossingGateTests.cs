using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Gate;
using Ryan6Vrc.MeshOptimizer.Liveness;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    // A garment decimated on its own can stay within its error of its source and still be driven through the skin under
    // it. These tests build a skin and a garment 2 mm over it on one two-bone rig, decimate the garment across the
    // joint, and check the gate finds the crossing where the avatar's pose makes it, tells it from crossing the avatar
    // already had, and leaves the avatar as it found it.
    public class CrossingGateTests
    {
        GateScene _scene;

        [SetUp]
        public void SetUp() => _scene = new GateScene();

        [TearDown]
        public void TearDown() => _scene.Dispose();

        static readonly CrossingSettings Settings = CrossingSettings.Default;
        static readonly GatePose Bent = new BoneRotationPose("bent", ("Armature/Lower/Upper", Quaternion.Euler(0, 0, 60)));

        sealed class Rig
        {
            public Transform Root;
            public GateInput Skin;
            public GateInput Tights;
            public Tube Tube;
        }

        // Skin and tights: two tubes on the same two bones, blended over the joint alike, the tights 2 mm outside. The
        // optimized tights drop every ring between 0.3 and 0.7, exact while straight; the skin keeps every triangle. The
        // skin carries a key that swells one ring low on the leg 3 mm outward, through the tights.
        Rig TwoLayers(float tightsGap = 0.002f)
        {
            var root = _scene.Node("Avatar", null, Vector3.zero);
            var armature = _scene.Node("Armature", root, Vector3.zero);
            var lower = _scene.Node("Lower", armature, Vector3.zero);
            var upper = _scene.Node("Upper", lower, new Vector3(0, 0.5f, 0));
            GateInput Layer(string name, float radius, bool decimate)
            {
                var tube = new Tube(Vector3.zero, Vector3.up, 1f, radius, 16, 20);
                var source = _scene.Track(tube.Mesh(name, r => (tube.AlongAxis(r) - 0.35f) / 0.3f));
                var swell = new Vector3[source.vertexCount];
                for (var s = 0; s < tube.Segments; s++)
                {
                    var v = tube.Vertices[tube.Index(s, 3)];
                    swell[tube.Index(s, 3)] = new Vector3(v.x, 0, v.z).normalized * (radius < 0.051f ? 0.003f : 0f);
                }
                source.AddBlendShapeFrame("swell", 100f, swell, null, null);
                var renderer = _scene.Skin(_scene.Node(name, root, Vector3.zero), source, lower, upper);
                var kept = decimate ? tube.AllRings().Where(r => tube.AlongAxis(r) <= 0.3f + 1e-4f || tube.AlongAxis(r) >= 0.7f - 1e-4f).ToArray() : tube.AllRings();
                var optimized = _scene.Optimized(source, tube.TrianglesBetween(kept));
                return new GateInput(renderer, source, optimized, null, new[] { ShapeKeyLiveness.Live(1f) });
            }
            var rig = new Rig { Root = root, Skin = Layer("Skin", 0.05f, false), Tights = Layer("Tights", 0.05f + tightsGap, true) };
            return rig;
        }

        static PairCrossing Pair(CrossingReport report, string a, string b, string configuration) =>
            report.Pairs.Single(p => p.RendererA == a && p.RendererB == b && p.Configuration == configuration);

        [Test]
        public void GarmentDecimatedAcrossAJoint_CrossesTheSkinOnlyWhenBent_AndThatIsNew()
        {
            var rig = TwoLayers();

            var report = MeasureRig(rig, new[] { Bent });

            Assert.That(report.Configurations, Is.EqualTo(new[] { "as it stands", "pose 'bent'" }));
            var rest = Pair(report, "Skin", "Tights", "as it stands").Change;
            var bent = Pair(report, "Skin", "Tights", "pose 'bent'").Change;
            Assert.That(rest.After.Length, Is.Zero, "straight, the tights' long facets lie where the dropped rings were");
            Assert.That(bent.Before.Length, Is.Zero, "bent, the source tights still clear the skin");
            Assert.That(bent.New, Is.GreaterThan(0.01), "the tights' straight facets cut through the skin round the outside of the bend");
            Assert.That(bent.New, Is.EqualTo(bent.After.Length).Within(1e-6));
            Assert.That(bent.Runs, Is.GreaterThanOrEqualTo(1));
            Assert.That(bent.LongestRunTriangleA, Is.GreaterThanOrEqualTo(0));
            Assert.That(bent.LongestRunAt.y, Is.InRange(0.3f, 0.75f), "near the joint");
            var worst = report.Pairs[report.Worst()];
            Assert.That((worst.RendererA, worst.RendererB, worst.Configuration), Is.EqualTo(("Skin", "Tights", "pose 'bent'")));
            Assert.That(report.Lines()[0], Does.StartWith("Skin x Tights, pose 'bent'"));
        }

        CrossingReport MeasureRig(Rig rig, IReadOnlyList<GatePose> poses, IReadOnlyList<AvatarConfiguration> extra = null) =>
            CrossingGate.Measure(rig.Root, new[] { rig.Skin, rig.Tights }, poses, extra, Settings);

        [Test]
        public void CrossingTheAvatarShippedWith_IsNotNew()
        {
            var rig = TwoLayers();
            // A caller's avatar-wide configuration: the skin's key swells a ring low on the leg through the tights, source and optimized alike.
            var swollen = new AvatarConfiguration("skin swollen", null, new Dictionary<Renderer, float[]> { [rig.Skin.Renderer] = new[] { 100f } });

            var report = MeasureRig(rig, null, new[] { swollen });

            var change = Pair(report, "Skin", "Tights", "skin swollen").Change;
            Assert.That(change.Before.Length, Is.GreaterThan(0.1), "the swollen ring passes through the tights all round");
            Assert.That(change.After.Length, Is.EqualTo(change.Before.Length).Within(1e-6));
            Assert.That(change.New, Is.Zero);
        }

        [Test]
        public void ARendererThePassEmptied_IsMeasuredAsNoSurface()
        {
            // Every triangle of the tights removed: after the pass nothing is left to cross the skin or itself.
            var rig = TwoLayers();
            var emptied = _scene.Optimized(rig.Tights.Source, new int[0]);
            rig.Tights = new GateInput(rig.Tights.Renderer, rig.Tights.Source, emptied, null, rig.Tights.Liveness);

            var report = MeasureRig(rig, new[] { Bent });

            foreach (var configuration in new[] { "as it stands", "pose 'bent'" })
            {
                Assert.That(Pair(report, "Skin", "Tights", configuration).Change.After.Length, Is.Zero, configuration);
                Assert.That(Pair(report, "Tights", "Tights", configuration).Change.After.Length, Is.Zero, configuration);
                Assert.That(Pair(report, "Skin", "Tights", configuration).Change.New, Is.Zero, configuration);
            }
        }

        [Test]
        public void RenderersApart_AreNotPaired_AndEveryRendererIsMeasuredAgainstItself()
        {
            var rig = TwoLayers();
            var hat = _scene.Node("Hat", rig.Root, new Vector3(0, 3f, 0));
            var hatMesh = _scene.Track(Sheet.Mesh("hat"));
            hat.gameObject.AddComponent<MeshFilter>().sharedMesh = hatMesh;
            var hatInput = new GateInput(hat.gameObject.AddComponent<MeshRenderer>(), hatMesh, _scene.Optimized(hatMesh, Sheet.Triangles(false)), null, null);

            var report = CrossingGate.Measure(rig.Root, new[] { rig.Skin, rig.Tights, hatInput }, null, null, Settings);

            var pairs = report.Pairs.Select(p => $"{p.RendererA} x {p.RendererB}").ToArray();
            Assert.That(pairs, Is.EquivalentTo(new[] { "Skin x Skin", "Skin x Tights", "Tights x Tights", "Hat x Hat" }));
        }

        [Test]
        public void MusclePoses_OnARigWithNoHumanoid_AreSkippedByName()
        {
            var rig = TwoLayers();

            var report = MeasureRig(rig, PoseBank.Default);

            Assert.That(report.Configurations, Is.EqualTo(new[] { "as it stands" }));
            Assert.That(report.Skipped.Select(s => s.Name), Is.EqualTo(PoseBank.Default.Select(p => $"pose '{p.Name}'")));
        }

        [Test]
        public void AvatarIsLeftAsFound_AfterARun_AndAfterAFailureMidRun()
        {
            var rig = TwoLayers();
            var skin = (SkinnedMeshRenderer)rig.Skin.Renderer;
            var tights = (SkinnedMeshRenderer)rig.Tights.Renderer;
            // The avatar as it stands: the skin's key held part-way, the joint already turned.
            skin.SetBlendShapeWeight(0, 40f);
            rig.Skin = new GateInput(skin, rig.Skin.Source, rig.Skin.Optimized, null, new[] { ShapeKeyLiveness.Live(0f, 1f, 40f) });
            rig.Root.Find("Armature/Lower/Upper").localRotation = Quaternion.Euler(0, 0, 10);
            string[] State() => GeometryGateTests.State.Of(rig.Root, skin).Concat(GeometryGateTests.State.Of(rig.Root, tights)).ToArray();
            var before = State();

            var report = MeasureRig(rig, new[] { Bent });
            Assert.That(report.Configurations, Is.EqualTo(new[] { "as it stands", "live keys at zero", "pose 'bent'", "pose 'bent' with live keys at zero" }));
            Assert.That(State(), Is.EqualTo(before), "after a run");

            var e = Assert.Throws<InvalidOperationException>(() => MeasureRig(rig, new GatePose[] { Bent, new ThrowingPose() }));
            StringAssert.Contains("forced failure", e.Message);
            Assert.That(State(), Is.EqualTo(before), "after a failure");
        }

        [Test]
        public void BadInput_IsRefusedByName_BeforeAnythingMoves()
        {
            var rig = TwoLayers();
            var before = GeometryGateTests.State.Of(rig.Root, (SkinnedMeshRenderer)rig.Skin.Renderer);

            var e = Assert.Throws<ArgumentException>(() => CrossingGate.Measure(rig.Root, new[] { rig.Skin, rig.Skin }, null, null, Settings));
            StringAssert.Contains("'Skin' is passed 2 times", e.Message);
            var wrong = new AvatarConfiguration("wrong", null, new Dictionary<Renderer, float[]> { [rig.Skin.Renderer] = new[] { 1f, 2f } });
            e = Assert.Throws<ArgumentException>(() => MeasureRig(rig, null, new[] { wrong }));
            StringAssert.Contains("2 weights", e.Message);

            Assert.That(GeometryGateTests.State.Of(rig.Root, (SkinnedMeshRenderer)rig.Skin.Renderer), Is.EqualTo(before));
        }
    }
}
