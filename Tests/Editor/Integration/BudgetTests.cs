using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Decimation;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // The target is the avatar's, not each renderer's: triangles go where they cost least error, and anything the
    // pass does not decimate still counts.
    public class BudgetTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

        AvatarMeshOptimizer Budget(int triangles)
        {
            var c = a.Optimizer(decimate: true, removeHidden: false);
            c.target = TargetKind.TriangleBudget;
            c.triangleBudget = triangles;
            return c;
        }

        SkinnedMeshRenderer Ball(string name, Vector3 at, int rings = 30, int segments = 40)
        {
            var bone = a.Bone("Armature/" + name);
            bone.localPosition = at;
            return a.Skinned(name, a.Sphere(0.1f, rings, segments, name: name), new[] { bone }, parent: bone);
        }

        static float Kept(RendererReport r) => (float)r.TrianglesAfter / r.TrianglesBefore;

        [Test]
        public void AvatarWideBudget_IsMet_AndTheFlatterRendererGivesUpMore()
        {
            var flat = a.Rigid("Flat", a.Grid(40, 0.01f));
            var round = Ball("Round", new Vector3(1, 0, 0));
            var total = Triangles(flat.GetComponent<MeshFilter>().sharedMesh) + Triangles(round.sharedMesh);
            Budget(total / 2);

            var report = a.Run();

            Assert.That(report.TrianglesAfter, Is.LessThanOrEqualTo(total / 2));
            Assert.That(report.TargetMet, Is.True);
            Assert.That(Kept(Of(report, flat)), Is.LessThan(Kept(Of(report, round))), "a flat grid loses triangles at no error; a sphere cannot");
            Assert.That(Triangles(flat.GetComponent<MeshFilter>().sharedMesh), Is.EqualTo(Of(report, flat).TrianglesAfter));
            Assert.That(Triangles(round.sharedMesh), Is.EqualTo(Of(report, round).TrianglesAfter));
        }

        [Test]
        public void ExcludedRenderer_IsLeftWhole_AndCountsAgainstTheBudgetInFull()
        {
            var one = Ball("One", new Vector3(1, 0, 0));
            var two = Ball("Two", new Vector3(2, 0, 0));
            var excluded = Ball("Excluded", new Vector3(3, 0, 0));
            var each = Triangles(one.sharedMesh);
            var excludedMesh = excluded.sharedMesh;
            var c = Budget(2 * each);
            c.renderers.Add(new RendererSettings { renderer = excluded, excludeFromDecimation = true });

            var report = a.Run();

            Assert.That(excluded.sharedMesh, Is.SameAs(excludedMesh));
            Assert.That(Of(report, excluded).ExcludedFromDecimation, Is.True);
            Assert.That(report.TrianglesAfter, Is.LessThanOrEqualTo(2 * each), "the other two absorb the excluded one's share");
            Assert.That(Of(report, one).TrianglesAfter + Of(report, two).TrianglesAfter, Is.LessThanOrEqualTo(each));
        }

        // A triangle that repeats a vertex is not in a collapse sequence, but a renderer left uncut still draws it, so
        // it costs the budget in full. Five of them over a budget two above the rest leave the renderer to be cut.
        [Test]
        public void DegenerateTriangles_CountAgainstTheBudget()
        {
            var ball = Ball("Ball", Vector3.zero);
            var mesh = ball.sharedMesh;
            var proper = Triangles(mesh);
            var triangles = mesh.triangles.ToList();
            for (var i = 0; i < 5; i++) triangles.AddRange(new[] { 0, 0, 1 });
            mesh.triangles = triangles.ToArray();
            Budget(proper + 2);

            var report = a.Run();

            Assert.That(report.TrianglesAfter, Is.LessThanOrEqualTo(proper + 2));
            Assert.That(report.TargetMet, Is.True);
        }

        [Test]
        public void Tolerance_CutsEachRendererOnlyAsFarAsTheErrorAllows()
        {
            var flat = a.Rigid("Flat", a.Grid(40, 0.01f));
            var round = Ball("Round", new Vector3(1, 0, 0));
            var c = a.Optimizer(decimate: true, removeHidden: false);
            c.target = TargetKind.Tolerance;
            c.tolerance = 0.01f;

            var report = a.Run();

            Assert.That(Of(report, flat).TrianglesAfter, Is.LessThan(Of(report, flat).TrianglesBefore), "a flat grid decimates at no error");
            Assert.That(Of(report, round).Error, Is.LessThanOrEqualTo(0.01f / 1000f), "tolerance is millimetres on the component, metres in the core");
            Assert.That(report.ErrorLevel, Is.LessThanOrEqualTo(0.01f / 1000f));
        }

        // Hidden triangles go first, so decimation spends the budget only on what shows. The bowl is excluded, so it
        // costs its triangles in full; the budget leaves the 960-triangle body a given number. At least 416 of them lie
        // under the bowl, so what shows fits in 600 but not in 200.
        [TestCase(true, 600, false)]
        [TestCase(false, 600, true)]
        [TestCase(true, 200, true)]
        public void HiddenTrianglesGoFirst_SoTheBudgetIsSpentOnlyOnWhatShows(bool removeHidden, int forBody, bool decimated)
        {
            var chest = a.Bone("Armature/Chest");
            var bodyMesh = a.Sphere(0.1f, 16, 32, name: "body");
            var body = a.Skinned("Body", bodyMesh, new[] { chest });
            var garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            var c = Budget(Triangles(garment.sharedMesh) + forBody);
            c.removeHiddenTriangles = removeHidden;
            c.alwaysOn.Add(garment);
            c.renderers.Add(new RendererSettings { renderer = garment, excludeFromDecimation = true });

            var report = a.Run();

            var r = Of(report, body);
            var visible = r.TrianglesBefore - r.HiddenRemoved;
            Assert.That(report.TargetMet, Is.True);
            Assert.That(r.TrianglesAfter, Is.LessThanOrEqualTo(forBody));
            Assert.That(r.TrianglesBefore, Is.EqualTo(960));
            Assert.That(r.AllocationCut > 0, Is.EqualTo(decimated));
            if (!decimated) Assert.That(r.TrianglesAfter, Is.EqualTo(visible), "what shows already fits, so none of it is decimated");
            if (removeHidden)
            {
                // Nothing the decimator returns reaches into what occlusion removed.
                var polar = Polar(body.sharedMesh);
                Assert.That(polar.Where(p => p > 110f), Is.Empty);
            }
        }

        [Test]
        public void DecimateOff_CutsNothing()
        {
            var round = Ball("Round", Vector3.zero);
            var mesh = round.sharedMesh;
            Budget(10).decimate = false;

            a.Run();

            Assert.That(round.sharedMesh, Is.SameAs(mesh));
        }

        [Test]
        public void TheVisemeRenderer_StaysAtOrAboveItsFloor_WhileItsTwinGoesBelow()
        {
            var face = Ball("Face", new Vector3(1, 0, 0));
            var twin = Ball("Twin", new Vector3(2, 0, 0));
            var descriptor = a.Root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = face;
            Budget(100);

            var report = a.Run();

            var f = Of(report, face);
            Assert.That(f.FloorTriangles, Is.EqualTo(Mathf.CeilToInt(f.TrianglesBefore * Protection.FaceFloor)));
            Assert.That(f.TrianglesAfter, Is.GreaterThanOrEqualTo(f.FloorTriangles));
            Assert.That(Kept(Of(report, twin)), Is.LessThan(Protection.FaceFloor), "the same sphere, unnamed, is cut deeper");
            Assert.That(report.TargetMet, Is.False, "the floor stops the budget");
        }

        // A dense patch on the right hand: the half at +X rides the index finger, the rest the hand. Fingers are
        // found through the humanoid mapping, so the same bones without a humanoid avatar protect nothing.
        [TestCase(true)]
        [TestCase(false)]
        public void FingerVertices_SurviveAnyBudget_WhenTheHumanoidMappingNamesThem(bool humanoid)
        {
            var skeleton = new Humanoid(a);
            if (!humanoid) a.Root.GetComponent<Animator>().avatar = null;
            var hand = skeleton.Bones[HumanBodyBones.RightHand];
            var finger = skeleton.Bones[HumanBodyBones.RightIndexProximal];
            var mesh = a.Grid(24, 0.002f, "hand");
            Weight(mesh, v => v.x > 0 ? 1 : 0);
            var renderer = a.Skinned("Hand", mesh, new[] { hand, finger }, parent: hand);
            Budget(20);

            var report = a.Run();

            var r = Of(report, renderer);
            var kept = new HashSet<int>(r.KeptVertices);
            var onFinger = mesh.vertices.Select((v, i) => (v, i)).Where(p => p.v.x > 0).Select(p => p.i).ToList();
            var onHand = mesh.vertices.Length - onFinger.Count;
            if (humanoid)
            {
                Assert.That(onFinger.Where(i => !kept.Contains(i)), Is.Empty, "every finger vertex is locked");
                Assert.That(r.LockedVertices, Is.EqualTo(onFinger.Count));
            }
            else
            {
                Assert.That(onFinger.Count(i => !kept.Contains(i)), Is.GreaterThan(onFinger.Count / 2), "bone names alone protect nothing");
                Assert.That(r.LockedVertices, Is.Zero);
            }
            Assert.That(kept.Count, Is.LessThan(onFinger.Count + onHand), "the hand half decimates either way");
        }

        // An accessory an animation shows by growing its bone from a shrunk rest: as the avatar stands it is a millimetre
        // across, so every collapse on it costs less than the tolerance. Its cost is charged at the size the clip shows it,
        // so it decimates as the same ball at full size does. Without the clip it is only ever that small, and decimates away.
        [TestCase(true)]
        [TestCase(false)]
        public void AnAccessoryAClipGrowsFromAShrunkRest_IsCostedAtItsGrownSize(bool grows)
        {
            var bone = a.Bone("Armature/Accessory");
            var mesh = a.Sphere(0.1f, 30, 40, name: "accessory");
            var accessory = a.Skinned("Accessory", mesh, new[] { bone }, parent: bone);
            // Shrunk after binding, so the rest pose draws the accessory at a hundredth of its size.
            bone.localScale = Vector3.one * 0.01f;
            if (grows) a.ScaleClip(bone, 0.01f, 1f);
            var twin = Ball("Ball", new Vector3(1, 0, 0));
            var c = a.Optimizer(decimate: true, removeHidden: false);
            c.target = TargetKind.Tolerance;
            c.tolerance = 1f;

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            var r = Of(report, accessory);
            var full = Of(report, twin);
            Assert.That(full.TrianglesAfter, Is.LessThan(full.TrianglesBefore), "the full-size twin decimates at this tolerance");
            if (grows)
            {
                Assert.That(r.TrianglesAfter, Is.EqualTo(full.TrianglesAfter).Within(full.TrianglesAfter / 50), "costed as the full-size twin");
                Assert.That(r.CostRaisedForGrowth, Is.EqualTo(mesh.vertexCount), "the report counts what the rule raised");
                Assert.That(r.LockedVertices, Is.Zero, "a hundredfold growth is costed, not locked");
                StringAssert.Contains($"{mesh.vertexCount} vertices on bones a clip grows were costed", report.Summary());
            }
            else
            {
                Assert.That(r.TrianglesAfter, Is.LessThan(full.TrianglesAfter / 2), "at a millimetre across, the tolerance takes most of it");
                Assert.That(r.CostRaisedForGrowth, Is.Zero);
            }
            Assert.That(full.CostRaisedForGrowth + full.LockedVertices, Is.Zero, "a renderer on another bone is judged as before");
        }

        // From a rest of zero the accessory has no size at all, and no factor makes a zero cost what is shown: it is locked.
        [Test]
        public void AnAccessoryAClipGrowsFromZeroScale_KeepsEveryTriangle()
        {
            var bone = a.Bone("Armature/Accessory");
            var mesh = a.Sphere(0.1f, 30, 40, name: "accessory");
            var accessory = a.Skinned("Accessory", mesh, new[] { bone }, parent: bone);
            bone.localScale = Vector3.zero;
            a.ScaleClip(bone, 0f, 1f);
            var c = a.Optimizer(decimate: true, removeHidden: false);
            c.target = TargetKind.Tolerance;

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            var r = Of(report, accessory);
            Assert.That(r.TrianglesAfter, Is.EqualTo(r.TrianglesBefore));
            Assert.That(r.LockedFromZeroScale, Is.EqualTo(mesh.vertexCount));
            Assert.That(r.LockedVertices, Is.EqualTo(mesh.vertexCount));
            StringAssert.Contains($"{mesh.vertexCount} vertices on bones a clip grows from zero scale", report.Summary());
        }

        // With nothing grown anywhere, the growth rule changes nothing: a clip that shrinks a bone or holds it at rest leaves
        // every renderer's sequence, cut, reported error and written triangles bit for bit what they are with no clip at all,
        // under a tolerance and under a budget.
        [TestCase(TargetKind.Tolerance)]
        [TestCase(TargetKind.TriangleBudget)]
        public void ClipsThatGrowNothing_ChangeNoRenderer(TargetKind target)
        {
            var chest = a.Bone("Armature/Hips/Chest");
            var mesh = a.Sphere(0.1f, 32, 64, name: "body");
            mesh.AddBlendShapeFrame("Swell", 100f, mesh.vertices.Select(v => v.y > 0 ? v * 0.05f : Vector3.zero).ToArray(), null, null);
            a.Skinned("Body", mesh, new[] { chest });
            a.Skinned("Garment", a.Sphere(0.101f, 40, 72, 60f, 180f, name: "garment"), new[] { chest });
            var round = Ball("Round", new Vector3(1, 0, 0));
            var c = a.Optimizer(decimate: true, removeHidden: false);
            c.target = target;
            c.triangleBudget = 4000;
            a.ScaleClip(chest, 1f, 0.5f);
            a.ScaleClip(round.transform.parent, 1f, 1f);

            var without = a.Run(apply: false);
            var with = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform), apply: false);

            Assert.That(without.Renderers.Sum(r => r.SequenceLength), Is.GreaterThan(0), "something was decimated");
            for (var i = 0; i < without.Renderers.Count; i++)
            {
                RendererReport x = without.Renderers[i], y = with.Renderers[i];
                Assert.That((y.SequenceLength, y.AllocationCut, y.TrianglesAfter, y.CostRaisedForGrowth, y.LockedVertices),
                    Is.EqualTo((x.SequenceLength, x.AllocationCut, x.TrianglesAfter, 0, x.LockedVertices)), x.Path);
                Assert.That(System.BitConverter.SingleToInt32Bits(y.Error), Is.EqualTo(System.BitConverter.SingleToInt32Bits(x.Error)), x.Path);
                Assert.That(y.OutputMesh != null ? y.OutputMesh.triangles : null, Is.EqualTo(x.OutputMesh != null ? x.OutputMesh.triangles : null), x.Path);
            }
            Assert.That(System.BitConverter.SingleToInt32Bits(with.ErrorLevel), Is.EqualTo(System.BitConverter.SingleToInt32Bits(without.ErrorLevel)));
        }

        // A size slider on Hips takes everything under it to 1.2 times: an error there is shown 1.2 times larger, so it is
        // charged so, and nothing is locked.
        [Test]
        public void ASizeSliderOnHips_LocksNothing_AndCostsWhatItGrows()
        {
            var hips = a.Bone("Armature/Hips");
            var bone = a.Bone("Armature/Hips/Chest");
            var mesh = a.Sphere(0.1f, 30, 40, name: "body");
            var body = a.Skinned("Body", mesh, new[] { bone }, parent: bone);
            a.ScaleClip(hips, 1f, 1.2f);
            a.Optimizer(decimate: true, removeHidden: false).target = TargetKind.Tolerance;

            var report = a.Run(UnstableBones.FromLegacyAnimations(a.Root.transform));

            var r = Of(report, body);
            Assert.That(r.LockedVertices, Is.Zero);
            Assert.That(r.CostRaisedForGrowth, Is.EqualTo(mesh.vertexCount));
            Assert.That(r.TrianglesAfter, Is.LessThan(r.TrianglesBefore), "still decimated");
        }

        // Shape keys are never dropped, renamed or reordered, so anything addressing one by index still finds it.
        [Test]
        public void EveryShapeKey_KeepsItsName_Index_FrameCount_AndFrameWeights()
        {
            var bone = a.Bone("Armature/Head");
            var mesh = a.Sphere(0.1f, 20, 24, name: "face");
            var n = mesh.vertexCount;
            Vector3[] Push(float y) => Enumerable.Range(0, n).Select(i => i % 3 == 0 ? new Vector3(0, y, 0) : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("smile", 100f, Push(0.01f), null, null);
            mesh.AddBlendShapeFrame("two frames", 40f, Push(0.004f), null, null);
            mesh.AddBlendShapeFrame("two frames", 100f, Push(0.02f), null, null);
            mesh.AddBlendShapeFrame("dead", 100f, Push(-0.01f), null, null);
            var renderer = a.Skinned("Face", mesh, new[] { bone }, parent: bone);
            Budget(200);

            var report = a.Run(tweak: input => input.Liveness = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>
            {
                [renderer] = new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Dead },
            });

            var output = renderer.sharedMesh;
            var r = Of(report, renderer);
            Assert.That(output, Is.SameAs(r.OutputMesh));
            Assert.That(r.TrianglesAfter, Is.LessThan(r.TrianglesBefore));
            Assert.That(output.blendShapeCount, Is.EqualTo(3));
            for (var k = 0; k < 3; k++)
            {
                Assert.That(output.GetBlendShapeName(k), Is.EqualTo(mesh.GetBlendShapeName(k)), $"key {k}");
                Assert.That(output.GetBlendShapeFrameCount(k), Is.EqualTo(mesh.GetBlendShapeFrameCount(k)), $"key {k}");
                for (var f = 0; f < mesh.GetBlendShapeFrameCount(k); f++)
                {
                    Assert.That(output.GetBlendShapeFrameWeight(k, f), Is.EqualTo(mesh.GetBlendShapeFrameWeight(k, f)), $"key {k} frame {f}");
                    var src = new Vector3[n];
                    var dst = new Vector3[output.vertexCount];
                    mesh.GetBlendShapeFrameVertices(k, f, src, null, null);
                    output.GetBlendShapeFrameVertices(k, f, dst, null, null);
                    for (var i = 0; i < dst.Length; i++)
                        Assert.That(dst[i], Is.EqualTo(src[r.KeptVertices[i]]), $"key {k} frame {f} output vertex {i}");
                }
            }
            Assert.That(r.Liveness, Has.Length.EqualTo(3), "the report carries the table the pass used");
            Assert.That(r.Liveness[2].State, Is.EqualTo(ShapeKeyState.Dead));
        }
    }
}
