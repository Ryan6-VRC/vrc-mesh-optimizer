using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // Temporary: the split pipeline against main's, on avatars that reach every stage, every report field compared and
    // every written mesh compared index for index.
    public class EquivalenceTests
    {
        AvatarFixture a;

        [SetUp] public void SetUp() => a = new AvatarFixture();
        [TearDown] public void TearDown() => a.Dispose();

        Material Furred()
        {
            const string source = "Shader \"Hidden/MeshOptimizerTests/EqFur\" { Properties { _Cull (\"Cull\", Float) = 2 } SubShader { Tags { \"RenderType\"=\"Opaque\" } " +
                                  "Pass { Name \"FORWARD\" Cull [_Cull] } Pass { Name \"FORWARD_FUR\" Cull Off } } }";
            return a.Made(new Material(a.Made(ShaderUtil.CreateShaderAsset(source, true))));
        }

        SkinnedMeshRenderer Keyed(string name, Transform bone, float radius, float keyed, float theta0 = 0f, float theta1 = 180f, int rings = 16)
        {
            var mesh = a.Sphere(radius, rings, 2 * rings, theta0, theta1, name: name);
            mesh.AddBlendShapeFrame("heels", 100f, mesh.vertices.Select(v => v.normalized * (keyed - radius)).ToArray(), null, null);
            return a.Skinned(name, mesh, new[] { bone });
        }

        // 0 budget, 1 tolerance, 2 no decimation, 3 budget without occlusion, 4 budget with outlines and a failing renderer,
        // 5 tolerance with a tight budget of protection weights.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void TheSplitPipeline_ReportsAndWritesExactlyAsMainDid(int scenario)
        {
            var chest = a.Bone("Armature/Hips/Chest");
            var skirtBone = a.Bone("Armature/Hips/Skirt");
            var body = Keyed("Body", chest, 0.1f, 0.103f, rings: 20);
            var garment = Keyed("Garment", chest, 0.104f, 0.107f, 60f, 180f, rings: 24);
            var skirt = a.Skinned("Skirt", a.Sphere(0.106f, 16, 32, 120f, 180f, name: "skirt"), new[] { skirtBone });
            a.Skinned("Cape", a.Sphere(0.12f, 8, 16, 0f, 90f, name: "cape"), new[] { chest }).gameObject.AddComponent<Cloth>();
            var mirrorRoot = new GameObject("Mirror");
            mirrorRoot.transform.SetParent(a.Root.transform, false);
            mirrorRoot.transform.localScale = new Vector3(-1f, 1f, 1f);
            var mirrored = a.Rigid("Glove", a.Sphere(0.03f, 8, 16, name: "glove"), mirrorRoot.transform);
            var pelt = a.Rigid("Pelt", a.Grid(8, 0.01f, "pelt"), chest, Furred());
            var lines = a.Sphere(0.05f, 8, 16, name: "withLines");
            var t = lines.triangles;
            lines.subMeshCount = 2;
            lines.SetTriangles(t, 0);
            lines.SetIndices(new[] { 0, 1, 1, 2 }, MeshTopology.Lines, 1);
            var lined = a.Rigid("Lined", lines, chest);
            a.ScaleClip(skirtBone, 1f, 1.5f);

            var optimizer = a.Optimizer(decimate: scenario != 2, removeHidden: scenario != 3);
            optimizer.alwaysOn.AddRange(new Renderer[] { garment, mirrored, skirt });
            optimizer.target = scenario == 1 || scenario == 5 ? TargetKind.Tolerance : TargetKind.TriangleBudget;
            optimizer.triangleBudget = 1500;
            optimizer.tolerance = 3f;
            optimizer.outlinesShowInside = scenario == 4;
            optimizer.renderers.Add(new RendererSettings { renderer = lined, excludeFromDecimation = scenario == 5 });
            optimizer.renderers.Add(new RendererSettings { renderer = body, protection = scenario == 5 ? 3f : 1f });

            var root = a.Root.transform;
            var skinned = new[] { body, garment };
            var inputs = skinned.Select(r => LivenessSources.Describe(r, root)).ToList();
            var table = ShapeKeyClassifier.Classify(inputs, inputs.Select(i => new ShapeKeyBinding(i.Path, "heels", new[] { 0f, 100f })), new DrivenKey[0]);
            var liveness = new Dictionary<SkinnedMeshRenderer, ShapeKeyLiveness[]>();
            for (var i = 0; i < skinned.Length; i++) liveness[skinned[i]] = table[i];

            PipelineInput Input() => new PipelineInput
            {
                Root = root,
                Settings = optimizer,
                Liveness = liveness,
                Links = new[] { skinned.Select(r => new LinkedKey(r, 0)).ToArray() },
                ScaleCurves = UnstableBones.FromLegacyAnimations(root).ToList(),
                BeforeStage = scenario == 4 ? (r, stage) => { if (r == lined && stage == PipelineStage.Writing) throw new System.InvalidOperationException("injected"); } : null,
            };
            if (scenario == 4) LogAssert.Expect(LogType.Exception, new Regex("injected"));
            if (scenario == 4) LogAssert.Expect(LogType.Exception, new Regex("injected"));

            var before = MainPipeline.Run(Input());
            OptimizationReport after;
            using (var prepared = OptimizerPipeline.Prepare(Input())) after = OptimizerPipeline.Cut(prepared);
            foreach (var r in before.Renderers.Concat(after.Renderers)) if (r.OutputMesh != null) a.Made(r.OutputMesh);

            Assert.That(after.Renderers.Count(r => r.OutputMesh != null), Is.GreaterThanOrEqualTo(2), "the scenario writes meshes");
            Same(before, after);
        }

        static void Same(OptimizationReport x, OptimizationReport y)
        {
            foreach (var p in typeof(OptimizationReport).GetProperties())
            {
                if (p.Name == nameof(OptimizationReport.Renderers) || p.GetIndexParameters().Length > 0) continue;
                var (vx, vy) = (p.GetValue(x), p.GetValue(y));
                if (vx is Core.Occlusion.OcclusionStats sx)
                    Assert.That(Untimed((Core.Occlusion.OcclusionStats)vy), Is.EqualTo(Untimed(sx)), p.Name);
                else if (vx is System.Collections.IEnumerable ex && !(vx is string))
                    Assert.That(Strings(vy), Is.EqualTo(Strings(ex)), p.Name);
                else
                    Assert.That(vy, Is.EqualTo(vx), p.Name);
            }
            Assert.That(y.Diagnostics.Select(d => $"{d.Key} {d.Warning} {string.Join("|", d.Args.Select(o => o?.ToString()))}"),
                Is.EqualTo(x.Diagnostics.Select(d => $"{d.Key} {d.Warning} {string.Join("|", d.Args.Select(o => o?.ToString()))}")), "diagnostics");
            Assert.That(Regex.Replace(y.Summary(), @"\d+ ms", ""), Is.EqualTo(Regex.Replace(x.Summary(), @"\d+ ms", "")), "summary");
            Assert.That(y.Renderers.Count, Is.EqualTo(x.Renderers.Count));
            for (var i = 0; i < x.Renderers.Count; i++)
            {
                var (rx, ry) = (x.Renderers[i], y.Renderers[i]);
                foreach (var p in typeof(RendererReport).GetProperties())
                {
                    var (vx, vy) = (p.GetValue(rx), p.GetValue(ry));
                    var at = $"{rx.Path}.{p.Name}";
                    if (p.Name == nameof(RendererReport.OutputMesh)) SameMesh((Mesh)vx, (Mesh)vy, at);
                    else Assert.That(vy, Is.EqualTo(vx), at);
                }
            }
        }

        static Core.Occlusion.OcclusionStats Untimed(Core.Occlusion.OcclusionStats s)
        {
            s.BuildMs = s.RestMs = s.FramesMs = s.FramePrepMs = s.DeletionPassMs = s.TotalMs = 0;
            return s;
        }

        static IEnumerable<string> Strings(object o) => ((System.Collections.IEnumerable)o).Cast<object>().Select(e => e?.ToString());

        static void SameMesh(Mesh x, Mesh y, string at)
        {
            Assert.That(y == null, Is.EqualTo(x == null), at);
            if (x == null) return;
            Assert.That(y, Is.Not.SameAs(x), at);
            Assert.That(y.vertexCount, Is.EqualTo(x.vertexCount), at);
            Assert.That(y.subMeshCount, Is.EqualTo(x.subMeshCount), at);
            for (var s = 0; s < x.subMeshCount; s++)
            {
                Assert.That(y.GetTopology(s), Is.EqualTo(x.GetTopology(s)), at);
                Assert.That(y.GetIndices(s), Is.EqualTo(x.GetIndices(s)), at);
            }
            Assert.That(y.vertices, Is.EqualTo(x.vertices), at);
            Assert.That(y.blendShapeCount, Is.EqualTo(x.blendShapeCount), at);
        }
    }
}
