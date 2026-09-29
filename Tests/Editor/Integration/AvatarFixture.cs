using System;
using System.Collections.Generic;
using System.Linq;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using ScaleCurve = Ryan6Vrc.MeshOptimizer.Occlusion.ScaleCurve;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    /// <summary>
    /// A synthetic avatar built in code: a root, bones by path, and renderers over meshes made here. Everything it
    /// makes is destroyed with it.
    /// </summary>
    internal sealed class AvatarFixture : IDisposable
    {
        public readonly GameObject Root;
        readonly List<Object> _made = new List<Object>();

        public AvatarFixture(string name = "Avatar")
        {
            Root = new GameObject(name);
            _made.Add(Root);
        }

        public T Made<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        public void Dispose()
        {
            foreach (var o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        /// <summary>The Transform at a path under the root, made with its parents if missing.</summary>
        public Transform Bone(string path)
        {
            var at = Root.transform;
            foreach (var part in path.Split('/'))
            {
                var next = at.Find(part);
                if (next == null)
                {
                    next = new GameObject(part).transform;
                    next.SetParent(at, false);
                }
                at = next;
            }
            return at;
        }

        public AvatarMeshOptimizer Optimizer(bool decimate = false, bool removeHidden = true)
        {
            if (!Root.TryGetComponent<AvatarMeshOptimizer>(out var c)) c = Root.AddComponent<AvatarMeshOptimizer>();
            c.decimate = decimate;
            c.removeHiddenTriangles = removeHidden;
            return c;
        }

        public Material Opaque()
        {
            var m = Made(new Material(Shader.Find("Standard")));
            m.renderQueue = 2000;
            return m;
        }

        /// <summary>
        /// A sphere, or the band of it between two polar angles in degrees from +Y, centred at the origin of its
        /// renderer. Faces point out, wound clockwise seen from outside, Unity's front; or in, when inward. UV is
        /// (azimuth fraction, 1 - polar degrees / 360).
        /// </summary>
        public Mesh Sphere(float radius, int rings, int segments, float theta0 = 0f, float theta1 = 180f, bool inward = false, string name = "sphere")
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (var i = 0; i <= rings; i++)
            {
                var theta = Mathf.Deg2Rad * Mathf.Lerp(theta0, theta1, (float)i / rings);
                for (var j = 0; j <= segments; j++)
                {
                    var phi = 2f * Mathf.PI * j / segments;
                    var dir = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                    vertices.Add(radius * dir);
                    normals.Add(inward ? -dir : dir);
                    uvs.Add(new Vector2((float)j / segments, 1f - theta * Mathf.Rad2Deg / 360f));
                }
            }
            for (var i = 0; i < rings; i++)
            {
                var topPole = i == 0 && theta0 <= 0f;
                var bottomPole = i == rings - 1 && theta1 >= 180f;
                for (var j = 0; j < segments; j++)
                {
                    var a = i * (segments + 1) + j;
                    int b = a + 1, c = a + segments + 1, d = c + 1;
                    if (!topPole) triangles.AddRange(inward ? new[] { a, c, b } : new[] { a, b, c });
                    if (!bottomPole) triangles.AddRange(inward ? new[] { b, c, d } : new[] { b, d, c });
                }
            }
            var mesh = Made(new Mesh { name = name });
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        /// <summary>A flat grid in the XZ plane facing +Y, cells <paramref name="cell"/> wide, centred on the origin.</summary>
        public Mesh Grid(int cells, float cell, string name = "grid")
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var half = cells * cell / 2f;
            for (var i = 0; i <= cells; i++)
                for (var k = 0; k <= cells; k++)
                {
                    vertices.Add(new Vector3(i * cell - half, 0, k * cell - half));
                    uvs.Add(new Vector2((float)i / cells, (float)k / cells));
                }
            for (var i = 0; i < cells; i++)
                for (var k = 0; k < cells; k++)
                {
                    var p00 = i * (cells + 1) + k;
                    int p01 = p00 + 1, p10 = p00 + cells + 1, p11 = p10 + 1;
                    triangles.AddRange(new[] { p00, p01, p10, p10, p01, p11 });
                }
            var mesh = Made(new Mesh { name = name });
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertices.Select(_ => Vector3.up).ToList());
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        /// <summary>Mirrors a mesh in X and reverses its winding: the asset a mirrored copy of it would be.</summary>
        public Mesh MirroredX(Mesh source)
        {
            var mesh = Made(Object.Instantiate(source));
            mesh.name = source.name + " mirrored";
            mesh.SetVertices(source.vertices.Select(v => new Vector3(-v.x, v.y, v.z)).ToList());
            mesh.SetNormals(source.normals.Select(n => new Vector3(-n.x, n.y, n.z)).ToList());
            var t = source.triangles;
            for (var i = 0; i < t.Length; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
            mesh.SetTriangles(t, 0);
            return mesh;
        }

        /// <summary>Skin weights: each vertex rides the bone slot <paramref name="slotOf"/> gives it, fully.</summary>
        public static void Weight(Mesh mesh, Func<Vector3, int> slotOf)
        {
            mesh.boneWeights = mesh.vertices.Select(v => new BoneWeight { boneIndex0 = slotOf(v), weight0 = 1f }).ToArray();
        }

        /// <summary>
        /// A skinned renderer at <paramref name="parent"/> (the root when null) over <paramref name="mesh"/>, bound to
        /// <paramref name="bones"/> as they stand, so the mesh sits at rest where the renderer puts it. Every vertex
        /// rides the first bone unless the mesh already carries weights.
        /// </summary>
        public SkinnedMeshRenderer Skinned(string name, Mesh mesh, Transform[] bones, Transform parent = null, Material material = null, Transform rootBone = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : Root.transform, false);
            if (mesh.boneWeights.Length == 0) Weight(mesh, _ => 0);
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * go.transform.localToWorldMatrix).ToArray();
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = rootBone != null ? rootBone : bones[0];
            smr.sharedMaterials = Enumerable.Repeat(material != null ? material : Opaque(), mesh.subMeshCount).ToArray();
            return smr;
        }

        public MeshRenderer Rigid(string name, Mesh mesh, Transform parent = null, Material material = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = Enumerable.Repeat(material != null ? material : Opaque(), mesh.subMeshCount).ToArray();
            return mr;
        }

        /// <summary>Runs the pipeline as the pass would, with liveness and scale curves injected, and applies it.</summary>
        public OptimizationReport Run(IEnumerable<ScaleCurve> scaleCurves = null, Action<PipelineInput> tweak = null, bool apply = true)
        {
            var input = new PipelineInput
            {
                Root = Root.transform,
                Settings = Root.GetComponent<AvatarMeshOptimizer>(),
                Liveness = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>(),
                ScaleCurves = scaleCurves,
            };
            tweak?.Invoke(input);
            OptimizationReport report;
            using (var prepared = OptimizerPipeline.Prepare(input)) report = OptimizerPipeline.Cut(prepared);
            foreach (var r in report.Renderers) if (r.OutputMesh != null) Made(r.OutputMesh);
            if (apply) OptimizerPipeline.Apply(report);
            return report;
        }

        public static RendererReport Of(OptimizationReport report, Renderer renderer) =>
            report.Renderers.Single(r => r.Renderer == renderer);

        public static int Triangles(Mesh mesh)
        {
            var n = 0;
            for (var s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) n += (int)mesh.GetIndexCount(s) / 3;
            return n;
        }

        /// <summary>Per source triangle, the polar angle in degrees of its centroid about the renderer's origin.</summary>
        public static float[] Polar(Mesh mesh)
        {
            var v = mesh.vertices;
            var t = mesh.triangles;
            var result = new float[t.Length / 3];
            for (var i = 0; i < result.Length; i++)
            {
                var c = (v[t[3 * i]] + v[t[3 * i + 1]] + v[t[3 * i + 2]]) / 3f;
                result[i] = Vector3.Angle(Vector3.up, c);
            }
            return result;
        }

        /// <summary>A legacy Animation on the root playing one clip that drives a bone's local scale on X.</summary>
        public AnimationClip ScaleClip(Transform bone, params float[] values)
        {
            var clip = Made(new AnimationClip { legacy = true, name = "scale " + bone.name });
            // AddKey, because AnimationCurve's constructor and its keys setter drop a NaN key; Modular Avatar's
            // NaNimation clips are built with AddKey too.
            var curve = new AnimationCurve();
            for (var i = 0; i < values.Length; i++) curve.AddKey(new Keyframe(i, 0) { value = values[i] });
            var path = AnimationUtility.CalculateTransformPath(bone, Root.transform);
            clip.SetCurve(path, typeof(Transform), "m_LocalScale.x", curve);
            if (!Root.TryGetComponent<UnityEngine.Animation>(out var animation)) animation = Root.AddComponent<UnityEngine.Animation>();
            animation.AddClip(clip, clip.name);
            if (animation.clip == null) animation.clip = clip;
            return clip;
        }
    }
}
