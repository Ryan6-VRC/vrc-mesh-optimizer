#if VRC_FT_SETS_PRESENT
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Tests.Integration;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using TargetMode = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.ParameterBehaviorTargetMode;

namespace Ryan6Vrc.MeshOptimizer.Tests.Liveness
{
    // Native face tracking drives the shapes its Blendshapes Sets and blendshape-target Parameter Behaviors name, with no
    // clip, on the meshes its Face Tracking Meshes list names. Counting one of those dead lets decimation and hidden-
    // triangle removal ignore a shape the face can move.
    public class FaceTrackingTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

        SkinnedMeshRenderer Renderer(string name, params string[] keys)
        {
            var mesh = a.Sphere(0.1f, 4, 8, name: name);
            foreach (var key in keys)
                mesh.AddBlendShapeFrame(key, 100f, Enumerable.Repeat(Vector3.up * 0.01f, mesh.vertexCount).ToArray(), null, null);
            return a.Skinned(name, mesh, new[] { a.Root.transform });
        }

        static VRCAvatarDescriptor.FaceTrackingBlendshapesSet Set(float weight) => new VRCAvatarDescriptor.FaceTrackingBlendshapesSet
        {
            setName = "set" + weight,
            setWeight = weight,
            faceTrackingBlendshapeNames = new string[(int)VRCAvatarDescriptor.FaceTrackingKey.Max],
            visemeBlendshapeNames = new string[(int)VRC_AvatarDescriptor.Viseme.Count],
        };

        // "jaw" is a key slot of a set at weight 1, "tongue" a viseme slot of a set at weight 0, which a parameter can
        // raise at runtime, and "puff" a Parameter Behavior's target: each is live on the listed mesh. A target mode that
        // names a set rather than a blendshape drives nothing, and a mesh the list leaves out is not face tracked.
        [TestCase(TargetMode.ApplyToBlendshape, true)]
        [TestCase(TargetMode.LimitBlendshapeMax, true)]
        [TestCase(TargetMode.LimitBlendshapeMin, true)]
        [TestCase(TargetMode.ApplyBlendSetWeight, false)]
        public void FaceTrackedShapes_AreLive_OnTheFaceTrackingMeshesOnly(TargetMode mode, bool puffLive)
        {
            var face = Renderer("Face", "jaw", "tongue", "puff", "other");
            var outfit = Renderer("Outfit", "jaw");
            var on = Set(1f);
            on.faceTrackingBlendshapeNames[0] = "jaw";
            var off = Set(0f);
            off.visemeBlendshapeNames[(int)VRC_AvatarDescriptor.Viseme.aa] = "tongue";
            var descriptor = a.Root.AddComponent<VRCAvatarDescriptor>();
            descriptor.faceTrackingMeshes = new List<SkinnedMeshRenderer> { face };
            descriptor.faceTrackingBlendshapesSetList = new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet> { on, off };
            descriptor.faceTrackingParameterBehaviors = new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>
            {
                new VRCAvatarDescriptor.FaceTrackingParameterBehavior { targetMode = mode, targetBlendshapeName = "puff" },
            };

            var result = LivenessSources.Classify(a.Root.transform, new[] { face, outfit }, new List<ShapeKeyBinding>(), out _, out _);

            var states = result[face].Select(k => k.State).ToArray();
            Assert.That(states, Is.EqualTo(new[]
            {
                ShapeKeyState.Live, ShapeKeyState.Live, puffLive ? ShapeKeyState.Live : ShapeKeyState.Dead, ShapeKeyState.Dead,
            }), "jaw, tongue, puff, other");
            Assert.That(result[outfit][0].State, Is.EqualTo(ShapeKeyState.Dead), "the outfit is not a Face Tracking Mesh");
        }
    }
}
#endif
