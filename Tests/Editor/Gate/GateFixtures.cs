using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Writing;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    /// <summary>Synthetic avatars for the gate: hierarchies, meshes and skins built in code, destroyed on dispose.</summary>
    internal sealed class GateScene : IDisposable
    {
        readonly List<Object> _made = new List<Object>();

        public T Track<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        public Transform Node(string name, Transform parent, Vector3 localPosition)
        {
            var go = new GameObject(name);
            if (parent == null) Track(go);
            else go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        /// <summary>A skinned renderer bound to <paramref name="bones"/> where they stand now.</summary>
        public SkinnedMeshRenderer Skin(Transform at, Mesh mesh, params Transform[] bones)
        {
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * at.localToWorldMatrix).ToArray();
            var renderer = at.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = bones;
            renderer.sharedMesh = mesh;
            return renderer;
        }

        /// <summary>
        /// The optimized mesh the writer makes from <paramref name="source"/>: the vertices <paramref name="triangles"/>
        /// name, copied unmoved with every shape key.
        /// </summary>
        public Mesh Optimized(Mesh source, int[] triangles)
        {
            var kept = triangles.Distinct().OrderBy(v => v).ToArray();
            return Track(MeshWriter.Write(source, new[] { triangles }, kept));
        }

        public void Dispose()
        {
            for (var i = _made.Count - 1; i >= 0; i--)
                if (_made[i] != null) Object.DestroyImmediate(_made[i]);
            _made.Clear();
        }
    }

    /// <summary>A closed tube: rings of vertices along an axis, ring r at r / rings of the length.</summary>
    internal sealed class Tube
    {
        public readonly Vector3[] Vertices;
        public readonly int Segments;
        public readonly int Rings;
        public readonly float Length;

        public Tube(Vector3 start, Vector3 axis, float length, float radius, int segments, int rings)
        {
            Segments = segments;
            Rings = rings;
            Length = length;
            axis = axis.normalized;
            var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(axis, u);
            Vertices = new Vector3[(rings + 1) * segments];
            for (var r = 0; r <= rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var a = 2f * Mathf.PI * s / segments;
                Vertices[Index(s, r)] = start + axis * AlongAxis(r) + radius * (Mathf.Cos(a) * u + Mathf.Sin(a) * v);
            }
        }

        public float AlongAxis(int ring) => Length * ring / Rings;
        public int Index(int segment, int ring) => ring * Segments + segment % Segments;
        public int RingOf(int vertex) => vertex / Segments;

        /// <summary>Two triangles per segment between each pair of consecutive rings listed.</summary>
        public int[] TrianglesBetween(IReadOnlyList<int> rings)
        {
            var t = new List<int>();
            for (var i = 0; i + 1 < rings.Count; i++)
            for (var s = 0; s < Segments; s++)
            {
                int a = Index(s, rings[i]), b = Index(s + 1, rings[i]), c = Index(s + 1, rings[i + 1]), d = Index(s, rings[i + 1]);
                t.AddRange(new[] { a, d, c, a, c, b });
            }
            return t.ToArray();
        }

        public int[] AllRings() => Enumerable.Range(0, Rings + 1).ToArray();

        /// <summary>A mesh of the whole tube, each vertex weighted between two bone slots by <paramref name="secondWeight"/> of its ring.</summary>
        public Mesh Mesh(string name, Func<int, float> secondWeight)
        {
            var mesh = new Mesh { name = name, vertices = Vertices, triangles = TrianglesBetween(AllRings()) };
            mesh.boneWeights = Vertices.Select((_, i) =>
            {
                var w = Mathf.Clamp01(secondWeight(RingOf(i)));
                return new BoneWeight { boneIndex0 = 0, weight0 = 1f - w, boneIndex1 = 1, weight1 = w };
            }).ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }
    }

    /// <summary>A flat grid in the XY plane, cells a centimetre square, whose middle vertex a decimation removes.</summary>
    internal static class Sheet
    {
        public const int Cells = 8;
        public const float Cell = 0.01f;
        public const int Middle = Cells / 2 * (Cells + 1) + Cells / 2;

        public static Mesh Mesh(string name)
        {
            var vertices = new List<Vector3>();
            for (var j = 0; j <= Cells; j++)
            for (var i = 0; i <= Cells; i++)
                vertices.Add(new Vector3((i - Cells / 2) * Cell, (j - Cells / 2) * Cell, 0f));
            var mesh = new Mesh { name = name, vertices = vertices.ToArray(), triangles = Triangles(false) };
            mesh.boneWeights = vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1f }).ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        static int At(int i, int j) => j * (Cells + 1) + i;

        /// <summary>The grid's triangles; without the middle vertex, its six triangles are replaced by a fan over the hexagon around it.</summary>
        public static int[] Triangles(bool withoutMiddle)
        {
            var t = new List<int>();
            for (var j = 0; j < Cells; j++)
            for (var i = 0; i < Cells; i++)
            {
                var cell = new[] { At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j), At(i + 1, j + 1), At(i, j + 1) };
                for (var k = 0; k < 6; k += 3)
                    if (!withoutMiddle || (cell[k] != Middle && cell[k + 1] != Middle && cell[k + 2] != Middle))
                        t.AddRange(new[] { cell[k], cell[k + 1], cell[k + 2] });
            }
            if (withoutMiddle)
            {
                const int m = Cells / 2;
                var ring = new[] { At(m - 1, m - 1), At(m, m - 1), At(m + 1, m), At(m + 1, m + 1), At(m, m + 1), At(m - 1, m) };
                for (var k = 1; k + 1 < ring.Length; k++) t.AddRange(new[] { ring[0], ring[k], ring[k + 1] });
            }
            return t.ToArray();
        }

        /// <summary>A shape key moving only the middle vertex by <paramref name="delta"/>.</summary>
        public static void AddKey(Mesh mesh, string name, Vector3 delta)
        {
            var d = new Vector3[mesh.vertexCount];
            d[Middle] = delta;
            mesh.AddBlendShapeFrame(name, 100f, d, null, null);
        }
    }

    /// <summary>
    /// A minimal humanoid in T-pose, facing +Z, with every finger, built as a hierarchy under an Animator whose Avatar
    /// is built from it in code.
    /// </summary>
    internal sealed class Humanoid
    {
        public readonly Transform Root;
        public readonly Dictionary<HumanBodyBones, Transform> Bones = new Dictionary<HumanBodyBones, Transform>();

        public Humanoid(GateScene scene)
        {
            Root = scene.Node("Avatar", null, Vector3.zero);
            var armature = scene.Node("Armature", Root, Vector3.zero);
            var hips = Bone(scene, HumanBodyBones.Hips, armature, new Vector3(0, 1f, 0));
            var spine = Bone(scene, HumanBodyBones.Spine, hips, new Vector3(0, 0.1f, 0));
            var chest = Bone(scene, HumanBodyBones.Chest, spine, new Vector3(0, 0.15f, 0));
            var neck = Bone(scene, HumanBodyBones.Neck, chest, new Vector3(0, 0.2f, 0));
            Bone(scene, HumanBodyBones.Head, neck, new Vector3(0, 0.1f, 0));
            foreach (var left in new[] { true, false })
            {
                var x = left ? -1f : 1f;
                HumanBodyBones Side(HumanBodyBones l, HumanBodyBones r) => left ? l : r;
                var shoulder = Bone(scene, Side(HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder), chest, new Vector3(0.04f * x, 0.17f, 0));
                var upper = Bone(scene, Side(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm), shoulder, new Vector3(0.1f * x, 0, 0));
                var lower = Bone(scene, Side(HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm), upper, new Vector3(0.28f * x, 0, 0));
                var hand = Bone(scene, Side(HumanBodyBones.LeftHand, HumanBodyBones.RightHand), lower, new Vector3(0.25f * x, 0, 0));
                var fingers = left
                    ? new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal }
                    : new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal };
                var roots = new[] { new Vector3(0.02f * x, -0.01f, 0.03f), new Vector3(0.08f * x, 0, 0.025f), new Vector3(0.085f * x, 0, 0.008f), new Vector3(0.08f * x, 0, -0.01f), new Vector3(0.07f * x, 0, -0.027f) };
                for (var f = 0; f < fingers.Length; f++)
                {
                    // Proximal, intermediate and distal are consecutive in HumanBodyBones.
                    var p = Bone(scene, fingers[f], hand, roots[f]);
                    var m = Bone(scene, fingers[f] + 1, p, new Vector3(0.035f * x, 0, 0));
                    Bone(scene, fingers[f] + 2, m, new Vector3(0.025f * x, 0, 0));
                }
                var thigh = Bone(scene, Side(HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg), hips, new Vector3(0.09f * x, -0.05f, 0));
                var shin = Bone(scene, Side(HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg), thigh, new Vector3(0, -0.42f, 0));
                var foot = Bone(scene, Side(HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot), shin, new Vector3(0, -0.42f, 0));
                Bone(scene, Side(HumanBodyBones.LeftToes, HumanBodyBones.RightToes), foot, new Vector3(0, -0.05f, 0.12f));
            }

            var description = new HumanDescription
            {
                human = Bones.Select(kv => new HumanBone
                {
                    humanName = HumanTrait.BoneName[(int)kv.Key],
                    boneName = kv.Value.name,
                    limit = new HumanLimit { useDefaultValues = true },
                }).ToArray(),
                skeleton = Root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                }).ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };
            var avatar = scene.Track(AvatarBuilder.BuildHumanAvatar(Root.gameObject, description));
            avatar.name = "synthetic-humanoid";
            Root.gameObject.AddComponent<Animator>().avatar = avatar;
        }

        Transform Bone(GateScene scene, HumanBodyBones bone, Transform parent, Vector3 localPosition)
        {
            var t = scene.Node(bone.ToString(), parent, localPosition);
            Bones[bone] = t;
            return t;
        }

        public string PathOf(HumanBodyBones bone)
        {
            var t = Bones[bone];
            var path = t.name;
            for (var p = t.parent; p != Root; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
