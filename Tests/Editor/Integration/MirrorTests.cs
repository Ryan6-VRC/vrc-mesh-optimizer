using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Unity.Collections;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // Occlusion reads a face's front from its winding in avatar-root space. Unity draws a renderer whose transform
    // mirrors with its winding reversed, so the pass reverses what occlusion reads for such a renderer, and only
    // for it. Which transform decides is measured here against Unity's own rasterizer rather than assumed.
    public class MirrorTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

        // One back-culled triangle facing a camera at -Z. With each of the renderer's transform, its bone and its root
        // bone placed under a parent that mirrors X or not, it is visible exactly when the mirroring of its geometry
        // (the bone's, for a skinned renderer; the transform's, for a MeshRenderer) matches DrawsMirrored.
        static System.Collections.Generic.IEnumerable<TestCaseData> Placements()
        {
            foreach (var rendererMirrored in new[] { false, true })
            {
                yield return new TestCaseData(rendererMirrored, false, false, false, false).SetName($"MeshRenderer mirrored={rendererMirrored}");
                for (var bits = 0; bits < 4; bits++)
                {
                    bool bone = (bits & 1) != 0, rootBone = (bits & 2) != 0;
                    yield return new TestCaseData(rendererMirrored, bone, rootBone, true, false)
                        .SetName($"Skinned renderer={rendererMirrored} bone={bone} rootBone={rootBone}");
                }
                foreach (var bone in new[] { false, true })
                    yield return new TestCaseData(rendererMirrored, bone, false, true, true)
                        .SetName($"Skinned renderer={rendererMirrored} bone={bone} no root bone");
            }
        }

        [TestCaseSource(nameof(Placements))]
        public void DrawsMirrored_AgreesWithWhatUnityDraws(bool rendererMirrored, bool boneMirrored, bool rootBoneMirrored,
            bool skinned, bool noRootBone)
        {
            a.Bone("Mirror").localScale = new Vector3(-1, 1, 1);
            Transform Under(bool mirrored, string name) => a.Bone((mirrored ? "Mirror/" : "Plain/") + name);

            var mesh = a.Made(new Mesh());
            mesh.vertices = new[] { new Vector3(0.1f, 0.1f, 0), new Vector3(0.1f, 0.9f, 0), new Vector3(0.9f, 0.1f, 0) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10);
            var material = a.Made(new Material(Shader.Find("Unlit/Color")) { color = Color.white });
            Renderer renderer;
            bool geometryMirrored;
            var go = Under(rendererMirrored, "Renderer").gameObject;
            if (skinned)
            {
                using (var perVertex = new NativeArray<byte>(new byte[] { 1, 1, 1 }, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(Enumerable.Repeat(new BoneWeight1 { boneIndex = 0, weight = 1 }, 3).ToArray(), Allocator.Temp))
                    mesh.SetBoneWeights(perVertex, weights);
                mesh.bindposes = new[] { Matrix4x4.identity };
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = new[] { Under(boneMirrored, "Bone") };
                smr.rootBone = noRootBone ? null : Under(rootBoneMirrored, "RootBone");
                smr.updateWhenOffscreen = true;
                renderer = smr;
                geometryMirrored = boneMirrored;
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = go.AddComponent<MeshRenderer>();
                geometryMirrored = rendererMirrored;
            }
            renderer.sharedMaterial = material;

            var visible = LitPixels() > 0;

            Assert.That(visible, Is.EqualTo(geometryMirrored == RendererExtraction.DrawsMirrored(renderer)),
                $"drawn {(visible ? "front" : "back")}, DrawsMirrored says {RendererExtraction.DrawsMirrored(renderer)}");
        }

        int LitPixels()
        {
            var camera = a.Made(new GameObject("Camera")).AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -5);
            camera.orthographic = true;
            camera.orthographicSize = 2;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var target = a.Made(new RenderTexture(64, 64, 24));
            camera.targetTexture = target;
            camera.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            var read = a.Made(new Texture2D(64, 64, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            RenderTexture.active = previous;
            camera.targetTexture = null;
            return read.GetPixels32().Count(p => p.r > 128);
        }

        // The same bowl over the same body, once as authored and once as its mirror image under a parent that
        // mirrors it back: the world sees one garment, so occlusion must hide the same triangles.
        [TestCase(false)]
        [TestCase(true)]
        public void MirroredRenderer_GivesTheSameVerdictsAsItsUnmirroredTwin(bool skinned)
        {
            var straight = Verdicts(skinned, mirrored: false, out var straightMirrored);
            var mirrored = Verdicts(skinned, mirrored: true, out var mirroredMirrored);

            Assert.That((straightMirrored, mirroredMirrored), Is.EqualTo((false, true)));
            Assert.That(straight.Count(h => h), Is.GreaterThan(0), "the bowl hides something");
            Assert.That(mirrored, Is.EqualTo(straight));
        }

        static bool[] Verdicts(bool skinned, bool mirrored, out bool garmentMirrored)
        {
            using var f = new AvatarFixture();
            var chest = f.Bone("Armature/Chest");
            var body = f.Skinned("Body", f.Sphere(0.1f, 16, 32, name: "body"), new[] { chest });
            var bowl = f.Sphere(0.105f, 24, 48, 60f, 180f, name: "garment");
            var parent = chest;
            if (mirrored)
            {
                parent = f.Bone("Armature/Chest/Mirror");
                parent.localScale = new Vector3(-1, 1, 1);
                bowl = f.MirroredX(bowl);
            }
            Renderer garment = skinned
                ? f.Skinned("Garment", bowl, new[] { f.Bone(AvatarBonePath(parent, f) + "/Bone") }, parent: parent)
                : f.Rigid("Garment", bowl, parent);
            f.Optimizer().alwaysOn.Add(garment);

            var report = f.Run();

            garmentMirrored = Of(report, garment).Mirrored;
            return Of(report, body).HiddenSourceTriangles;
        }

        static string AvatarBonePath(Transform t, AvatarFixture f) => UnityEditor.AnimationUtility.CalculateTransformPath(t, f.Root.transform);
    }
}
