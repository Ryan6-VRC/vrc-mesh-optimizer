using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // The avatar stands, by default, with every live key at the weight its renderer holds, and that state is one no
    // single key's frame reaches when two act on one region. Each test holds the same two keys at full weight and at
    // zero: the extra configuration exists only in the first, so any difference is its doing.
    public class AsItStandsTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

        static Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> BothLive(SkinnedMeshRenderer r) =>
            new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]> { [r] = new[] { ShapeKeyLiveness.Live(1f), ShapeKeyLiveness.Live(1f) } };

        // Two keys each push a band of the body 3 mm out: still inside a bowl 5 mm off, so covered under either alone.
        // Together they push it 6 mm, through the bowl.
        [TestCase(100f, false)]
        [TestCase(0f, true)]
        public void TwoHeldKeys_ThatTogetherPushThroughTheCover_KeepWhatTheyPush(float held, bool bandHidden)
        {
            var chest = a.Bone("Armature/Chest");
            var mesh = a.Sphere(0.1f, 16, 32, name: "body");
            var push = mesh.vertices.Select(v => Vector3.Angle(Vector3.up, v) is var p && p >= 105f && p <= 135f ? v.normalized * 0.003f : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("left", 100f, push, null, null);
            mesh.AddBlendShapeFrame("right", 100f, push, null, null);
            var body = a.Skinned("Body", mesh, new[] { chest });
            body.SetBlendShapeWeight(0, held);
            body.SetBlendShapeWeight(1, held);
            var garment = a.Skinned("Garment", a.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment"), new[] { chest });
            a.Optimizer().alwaysOn.Add(garment);

            var report = a.Run(tweak: input => input.Liveness = BothLive(body));

            var r = Of(report, body);
            Assert.That(r.EvaluatedAsItStands, Is.EqualTo(held != 0f));
            Assert.That(r.HeldWeights, Is.EqualTo(new[] { held, held }), "the report carries the weights the pass read");
            var polar = Polar(mesh);
            var band = Enumerable.Range(0, polar.Length).Where(t => polar[t] >= 110f && polar[t] <= 130f).ToList();
            Assert.That(band, Is.Not.Empty);
            foreach (var t in band) Assert.That(r.HiddenSourceTriangles[t], Is.EqualTo(bandHidden), $"triangle {t} at {polar[t]} degrees");
            Assert.That(r.HiddenRemoved, Is.GreaterThan(0), "the rest of the covered body goes either way");
        }

        // Two keys each bend a flat patch into the same curve; held together they bend it twice as far, so the same
        // budget costs more error when that configuration is part of the cost.
        [Test]
        public void TwoHeldKeys_ThatTogetherBendFurther_CostDecimationMore()
        {
            float Level(float held)
            {
                using var f = new AvatarFixture();
                var bone = f.Bone("Armature/Chest");
                var mesh = f.Grid(20, 0.01f, "patch");
                var bend = mesh.vertices.Select(v => new Vector3(0, v.x * v.x + v.z * v.z, 0)).ToArray();
                mesh.AddBlendShapeFrame("one", 100f, bend, null, null);
                mesh.AddBlendShapeFrame("two", 100f, bend, null, null);
                var patch = f.Skinned("Patch", mesh, new[] { bone });
                patch.SetBlendShapeWeight(0, held);
                patch.SetBlendShapeWeight(1, held);
                var c = f.Optimizer(decimate: true, removeHidden: false);
                c.triangleBudget = 100;
                var report = f.Run(tweak: input => input.Liveness = BothLive(patch));
                Assert.That(report.TargetMet, Is.True);
                return report.ErrorLevel;
            }

            var apart = Level(0f);
            var together = Level(100f);

            Assert.That(apart, Is.GreaterThan(0f));
            Assert.That(together, Is.GreaterThan(apart * 1.5f), $"held together {together}, apart {apart}");
        }
    }
}
