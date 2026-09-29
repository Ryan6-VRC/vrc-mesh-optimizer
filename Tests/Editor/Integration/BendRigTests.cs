using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Decimation;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Unity.Mathematics;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    public class BendRigTests
    {
        // Settings handed to builds from outside hold for the scope that hands them and no longer, however it ends.
        [Test]
        public void SettingsHandedToBuilds_HoldForTheirScope_AndAreRestoredWhenItThrows()
        {
            var before = BuildSettings.Decimation;
            var off = DecimationSettings.Default;
            off.Bend.Degrees = 0f;
            try
            {
                using (BuildSettings.Override(off))
                {
                    Assert.That(new PipelineInput().Decimation.Bend.Degrees, Is.Zero, "a build inside the scope");
                    throw new System.InvalidOperationException("a measurement that fails");
                }
            }
            catch (System.InvalidOperationException)
            {
            }
            Assert.That(BuildSettings.Decimation.Bend.Degrees, Is.EqualTo(before.Bend.Degrees));
            Assert.That(new PipelineInput().Decimation.Bend.Degrees, Is.EqualTo(DecimationSettings.Default.Bend.Degrees), "a build after it");
        }

        // A bone runs toward the child that carries the most of the avatar's skin weight under it: an upper arm toward its
        // forearm and hand, not toward a long ribbon hung on it, which has more transforms and far less surface. Taken the
        // wrong way, a swing of the arm becomes a twist about it.
        [Test]
        public void ABoneRunsTowardTheChildCarryingTheMostSkinWeight_NotTheLongestChain()
        {
            using var a = new AvatarFixture();
            var upper = a.Bone("Armature/UpperArm");
            var lower = a.Bone("Armature/UpperArm/LowerArm");
            lower.localPosition = new Vector3(-0.3f, 0f, 0f);
            var hand = a.Bone("Armature/UpperArm/LowerArm/Hand");
            hand.localPosition = new Vector3(-0.25f, 0f, 0f);
            var at = upper;
            for (var i = 0; i < 30; i++)
            {
                var link = new GameObject($"Ribbon{i}").transform;
                link.SetParent(at, false);
                link.localPosition = new Vector3(0f, -0.02f, 0f);
                at = link;
            }
            var bones = new AvatarBones(a.Root.transform);
            var weight = new float[bones.Count];
            weight[bones.Id(upper)] = 400f;
            weight[bones.Id(lower)] = 600f;
            weight[bones.Id(hand)] = 500f;
            weight[bones.Id(upper.Find("Ribbon0/Ribbon1"))] = 20f;

            var rig = BendRigs.Of(bones, weight);
            try
            {
                var axis = rig.Axis[bones.Id(upper)];
                Assert.That(math.dot(axis, new float3(-1f, 0f, 0f)), Is.GreaterThan(0.99f), $"the upper arm runs along {axis}");
            }
            finally
            {
                rig.Parent.Dispose();
                rig.Position.Dispose();
                rig.Axis.Dispose();
                rig.Right.Dispose();
            }
        }
    }
}
