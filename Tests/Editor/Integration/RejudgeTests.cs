using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Pipeline;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Ryan6Vrc.MeshOptimizer.Tests.Occlusion;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // A body under an always-on garment on the same chest bone. Coarsened far enough, a garment gives way in two ways.
    // Where its edge draws back, a triangle occlusion removed there is no longer covered and shows; judged again against
    // the garment as written, it must be written back. Where its faces sink past the body, the cover moved but did not go:
    // a viewer still sees the garment, so the triangle stays removed. Two fixtures part the two. A round cape 1 mm over a
    // flat body only draws back: its rim becomes a polygon whose sides cut inside the circle, and nothing flat sinks. A
    // sphere 5 mm over a sphere mostly sinks: its faces cut inside the curve, and the clearance rule holds its rim. Each body
    // carries a key, so each verdict holds in a configuration as well as at rest.
    public class RejudgeTests
    {
        AvatarFixture a;
        Transform chest;
        SkinnedMeshRenderer body, garment;

        [TearDown] public void TearDown() => a?.Dispose();

        /// <summary>
        /// The garment a band of sphere from <paramref name="garmentFrom"/> degrees down, over a body of
        /// <paramref name="rings"/> rings, left undecimated unless <paramref name="bodyDecimated"/>. With
        /// <paramref name="mirrored"/>, the garment is its own mirror image under a parent that mirrors it back, as
        /// <see cref="MirrorTests"/> builds it: the world sees the same garment, drawn mirrored.
        /// </summary>
        void Build(float garmentFrom, int rings = 16, bool bodyDecimated = false, bool mirrored = false)
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            var bodyMesh = Welded(a.Sphere(0.1f, rings, 2 * rings, name: "body"));
            bodyMesh.AddBlendShapeFrame("Swell", 100f, bodyMesh.vertices.Select(v => v.y < -0.03f ? v.normalized * 0.001f : Vector3.zero).ToArray(), null, null);
            body = a.Skinned("Body", bodyMesh, new[] { chest });
            var garmentMesh = Welded(a.Sphere(0.105f, rings + rings / 2, 3 * rings, garmentFrom, 180f, name: "garment"));
            if (mirrored)
            {
                var mirror = a.Bone("Armature/Hips/Chest/Mirror");
                mirror.localScale = new Vector3(-1, 1, 1);
                garment = a.Skinned("Garment", a.MirroredX(garmentMesh), new[] { a.Bone("Armature/Hips/Chest/Mirror/Bone") }, parent: mirror);
            }
            else garment = a.Skinned("Garment", garmentMesh, new[] { chest });
            var c = a.Optimizer(decimate: true);
            c.alwaysOn.Add(garment);
            if (!bodyDecimated) c.renderers.Add(new RendererSettings { renderer = body, excludeFromDecimation = true });
        }

        /// <summary>
        /// A flat body 20 cm across, left undecimated, under the always-on round cape of <see cref="Disc"/>, 5 cm in radius,
        /// 1 mm over it. The body carries a key that lifts its -X half by 0.3 mm.
        /// </summary>
        void BuildCape()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            var bodyMesh = a.Grid(40, 0.005f, "body");
            bodyMesh.AddBlendShapeFrame("Lift", 100f, bodyMesh.vertices.Select(v => v.x < 0f ? new Vector3(0f, 0.0003f, 0f) : Vector3.zero).ToArray(), null, null);
            body = a.Skinned("Body", bodyMesh, new[] { chest });
            garment = a.Skinned("Garment", Disc(0.05f, 0.001f, 10, 64), new[] { chest });
            var c = a.Optimizer(decimate: true);
            c.alwaysOn.Add(garment);
            c.renderers.Add(new RendererSettings { renderer = body, excludeFromDecimation = true });
        }

        /// <summary>
        /// A flat disc facing +Y at height <paramref name="y"/>, <paramref name="rings"/> rings of <paramref name="segments"/>
        /// about its centre. Coarsened, its rim becomes a polygon whose sides cut inside the circle.
        /// </summary>
        Mesh Disc(float radius, float y, int rings, int segments)
        {
            var v = new List<Vector3> { new Vector3(0f, y, 0f) };
            var uv = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            var t = new List<int>();
            for (var r = 1; r <= rings; r++)
                for (var s = 0; s < segments; s++)
                {
                    var phi = 2f * Mathf.PI * s / segments;
                    var d = new Vector3(Mathf.Cos(phi), 0f, Mathf.Sin(phi)) * (radius * r / rings);
                    v.Add(d + new Vector3(0f, y, 0f));
                    uv.Add(new Vector2(0.5f + d.x / (2f * radius), 0.5f + d.z / (2f * radius)));
                }
            int At(int r, int s) => r == 0 ? 0 : 1 + (r - 1) * segments + s % segments;
            for (var s = 0; s < segments; s++) t.AddRange(new[] { 0, At(1, s + 1), At(1, s) });
            for (var r = 1; r < rings; r++)
                for (var s = 0; s < segments; s++)
                    t.AddRange(new[] { At(r, s), At(r, s + 1), At(r + 1, s), At(r + 1, s), At(r, s + 1), At(r + 1, s + 1) });
            var mesh = a.Made(new Mesh { name = "disc" });
            mesh.SetVertices(v);
            mesh.SetNormals(v.Select(_ => Vector3.up).ToList());
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            return mesh;
        }

        /// <summary>
        /// The sphere with every seam and pole vertex at exactly the place of the first vertex it stands on. The fixture's
        /// sphere puts them there only to float noise, and decimation joins split vertices only where they coincide
        /// exactly, so it would open the seams, and what shows through them is not the coarsening under test.
        /// </summary>
        static Mesh Welded(Mesh mesh)
        {
            var v = mesh.vertices;
            for (var i = 0; i < v.Length; i++)
                for (var j = 0; j < i; j++)
                    if ((v[i] - v[j]).sqrMagnitude < 1e-12f)
                    {
                        v[i] = v[j];
                        break;
                    }
            mesh.vertices = v;
            return mesh;
        }

        OptimizationReport Run(TargetKind target, float value, bool rejudge)
        {
            var c = a.Optimizer(decimate: true);
            c.target = target;
            if (target == TargetKind.Tolerance) c.tolerance = value;
            else c.triangleBudget = (int)value;
            return a.Run(apply: false, tweak: input => input.Rejudge = rejudge);
        }

        /// <summary>
        /// The body and garment as written, to the checker: with <paramref name="setback"/>, by the rule the re-judge asks,
        /// under which the garment also covers a body sample it lies straight behind, within its cut's error; without, by
        /// the rule itself.
        /// </summary>
        CoverChecker.Report CheckWritten(OptimizationReport report, bool setback = false)
        {
            var check = CheckWritten(report, new Renderer[] { body, garment }, setback ? new[] { 1UL << 1, 0UL } : null, out var meshes);
            Assert.That(meshes[0].Mesh.Frames.Length, Is.GreaterThan(0), "the body's key is a configuration the checker judges");
            return check;
        }

        /// <summary>
        /// The avatar as written, handed to the independent checker: each renderer's evaluation mesh at its source, with the
        /// triangles the pass wrote, named by source vertex, beside every triangle the report says it removed, which are the
        /// ones the checker owes cover. With <paramref name="behind"/>, per renderer the ones whose faces cover it from
        /// behind, each always-on renderer does so to its cut's error.
        /// </summary>
        CoverChecker.Report CheckWritten(OptimizationReport report, Renderer[] renderers, ulong[] behind, out List<OcclusionMesh> meshes)
        {
            var bones = new AvatarBones(a.Root.transform);
            var owned = new List<IDisposable>();
            meshes = new List<OcclusionMesh>();
            var results = new List<OcclusionResult>();
            try
            {
                foreach (var r in renderers)
                {
                    var rr = Of(report, r);
                    var skinned = (SkinnedMeshRenderer)r;
                    var eval = RendererExtraction.Extract(r, bones, LivenessSources.EveryKeyLive(skinned, a.Root.transform), Allocator.Persistent, out _);
                    owned.Add(eval);
                    var triangles = new List<int3>();
                    var submesh = new List<int>();
                    var removed = new List<bool>();
                    if (rr.OutputMesh != null)
                    {
                        for (var s = 0; s < rr.OutputMesh.subMeshCount; s++)
                        {
                            var idx = rr.OutputMesh.GetTriangles(s);
                            for (var i = 0; i < idx.Length; i += 3)
                            {
                                triangles.Add(new int3(rr.KeptVertices[idx[i]], rr.KeptVertices[idx[i + 1]], rr.KeptVertices[idx[i + 2]]));
                                submesh.Add(s);
                                removed.Add(false);
                            }
                        }
                        if (rr.HiddenSourceTriangles != null)
                            for (var t = 0; t < eval.TriangleCount; t++)
                                if (rr.HiddenSourceTriangles[t])
                                {
                                    triangles.Add(eval.Triangles[t]);
                                    submesh.Add(eval.TriangleSubmesh[t]);
                                    removed.Add(true);
                                }
                    }
                    else
                    {
                        triangles.AddRange(eval.Triangles);
                        submesh.AddRange(eval.TriangleSubmesh);
                        removed.AddRange(new bool[eval.TriangleCount]);
                    }
                    // As occlusion reads it: Unity draws a mirrored renderer with its winding reversed.
                    if (rr.Mirrored)
                        for (var i = 0; i < triangles.Count; i++) triangles[i] = triangles[i].xzy;
                    var written = eval;
                    written.Triangles = new NativeArray<int3>(triangles.ToArray(), Allocator.Persistent);
                    owned.Add(written.Triangles);
                    written.TriangleSubmesh = new NativeArray<int>(submesh.ToArray(), Allocator.Persistent);
                    owned.Add(written.TriangleSubmesh);
                    meshes.Add(new OcclusionMesh { Mesh = written, Occludes = rr.Occludes });
                    var result = new OcclusionResult { HiddenTriangles = new NativeArray<bool>(removed.ToArray(), Allocator.Persistent) };
                    owned.Add(result);
                    results.Add(result);
                }
                var setback = behind != null ? renderers.Select(r => Of(report, r).Occludes ? Of(report, r).Error : 0f).ToArray() : null;
                return CoverChecker.Check(meshes, bones.Parents.ToArray(), null, OcclusionSettings.Default, results.ToArray(), maxViolations: 1000, setback, behind);
            }
            finally
            {
                foreach (var o in owned) o.Dispose();
            }
        }

        static HashSet<(int, int, int)> Drawn(RendererReport r)
        {
            var drawn = new HashSet<(int, int, int)>();
            var output = r.OutputMesh.triangles;
            for (var i = 0; i < output.Length; i += 3)
                drawn.Add((r.KeptVertices[output[i]], r.KeptVertices[output[i + 1]], r.KeptVertices[output[i + 2]]));
            return drawn;
        }

        // At a 4 mm tolerance the cape's rim draws back. Switched off, the checker finds removed triangles the written
        // cape no longer covers; on, they are written back as authored and every triangle still removed is covered, at rest
        // and with the body's key applied. Nothing flat sank, so what comes back is exactly what the rule itself finds
        // uncovered.
        [Test]
        public void ACoarsenedGarment_GivesBackWhatItNoLongerCovers_AndWhatStaysRemovedIsCovered()
        {
            BuildCape();

            var off = Run(TargetKind.Tolerance, 4f, rejudge: false);
            Assert.That(CheckWritten(off, setback: true).Ok, Is.False, "the coarsened garment uncovers some of what occlusion removed");
            Assert.That(off.RejudgePasses, Is.Zero);
            var lost = CheckWritten(off).Violations.Select(v => v.Split(',')[0]).Distinct().Count();

            var on = Run(TargetKind.Tolerance, 4f, rejudge: true);
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
            Assert.That(check.HiddenChecked, Is.GreaterThan(0), "some triangles stay removed");

            var r = Of(on, body);
            Assert.That(r.HiddenRestored, Is.GreaterThan(0));
            Assert.That(r.HiddenRestored, Is.EqualTo(lost), "every triangle the rule finds uncovered comes back, and no other");
            Assert.That(on.HiddenRestored, Is.EqualTo(r.HiddenRestored));
            Assert.That(on.RejudgePasses, Is.EqualTo(1), "a tolerance judges once");
            Assert.That(Of(on, garment).TrianglesAfter, Is.EqualTo(Of(off, garment).TrianglesAfter), "a tolerance cuts no deeper for what it restores");
            Assert.That(r.HiddenRemoved + r.HiddenRestored, Is.EqualTo(Of(off, body).HiddenRemoved));
            Assert.That(r.HiddenRemoved, Is.EqualTo(r.HiddenSourceTriangles.Count(h => h)), "the report's removed set is the written mesh's");
            Assert.That(Triangles(r.OutputMesh), Is.EqualTo(r.TrianglesAfter));
            Assert.That(r.TrianglesAfter, Is.EqualTo(Of(off, body).TrianglesAfter + r.HiddenRestored));
            var drawn = Drawn(r);
            var source = body.sharedMesh.triangles;
            var offRemoved = Of(off, body).HiddenSourceTriangles;
            for (var t = 0; t < offRemoved.Length; t++)
            {
                var authored = (source[3 * t], source[3 * t + 1], source[3 * t + 2]);
                Assert.That(drawn.Contains(authored), Is.EqualTo(!r.HiddenSourceTriangles[t]), $"triangle {t} is drawn, at its authored corners, exactly when it is not reported removed");
            }
            StringAssert.Contains($"{r.HiddenRestored} hidden triangles kept because decimation took their cover", on.Summary());
        }

        // A renderer the judge neither judges nor blocks by, which it enters with no triangles to hold its index, is the last
        // one the avatar lists: here a charm clear of the body, not always on, that nothing covers. The judge still runs
        // through, which the fixture's run asserts, and gives back what the coarsened cape uncovered.
        [Test]
        public void ARendererNeitherJudgedNorBlocking_LastInTheAvatar_DoesNotStopTheJudge()
        {
            BuildCape();
            var charmMesh = a.Sphere(0.02f, 8, 16, name: "charm");
            charmMesh.vertices = charmMesh.vertices.Select(v => v + new Vector3(0.3f, 0f, 0f)).ToArray();
            charmMesh.RecalculateBounds();
            var charm = a.Skinned("Charm", charmMesh, new[] { chest });
            a.Optimizer(decimate: true).renderers.Add(new RendererSettings { renderer = charm, excludeFromDecimation = true });

            var on = Run(TargetKind.Tolerance, 4f, rejudge: true);

            Assert.That(Of(on, charm).HiddenRemoved, Is.Zero, "nothing covers the charm");
            Assert.That(on.RejudgePasses, Is.EqualTo(1));
            Assert.That(on.HiddenRestored, Is.GreaterThan(0));
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
        }

        // A closed garment cut at 6 mm sinks past body it hid. Judged by the rule from where each triangle stood, the
        // triangles it sank past are uncovered; the cover moved, it did not go, so they stay removed, and the triangles
        // written back are fewer than those the rule alone would write back.
        [Test]
        public void ACoverTheCutMovedPastATriangle_StillCoversIt()
        {
            Build(garmentFrom: 0f);

            var off = Run(TargetKind.Tolerance, 6f, rejudge: false);
            var on = Run(TargetKind.Tolerance, 6f, rejudge: true);

            var moved = CheckWritten(off);
            Assert.That(moved.Ok, Is.False, "the cut garment sank past body it hid");
            Assert.That(CheckWritten(on).Ok, Is.False, "and what it sank past stays removed");
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
            Assert.That(on.HiddenRestored, Is.LessThan(moved.Violations.Select(v => v.Split(',')[0]).Distinct().Count()),
                "fewer triangles come back than the rule, judging from where each stood, finds uncovered");
        }

        // A closed garment cut at 2 mm still covers everything it covered: the judge runs, restores nothing, and every
        // mesh is written index for index as with re-judging off.
        [Test]
        public void NothingUncovered_WritesEveryMeshAsTheSwitchOffDoes()
        {
            Build(garmentFrom: 0f);

            var off = Run(TargetKind.Tolerance, 2f, rejudge: false);
            var on = Run(TargetKind.Tolerance, 2f, rejudge: true);

            Assert.That(on.RejudgePasses, Is.EqualTo(1), "the garment was cut, so the removals were judged again");
            Assert.That(on.HiddenRestored, Is.Zero);
            Assert.That(Of(on, body).HiddenRemoved, Is.GreaterThan(0));
            foreach (var renderer in new Renderer[] { body, garment })
            {
                var (x, y) = (Of(off, renderer), Of(on, renderer));
                Assert.That(y.OutputMesh, Is.Not.Null);
                Assert.That(y.KeptVertices, Is.EqualTo(x.KeptVertices), renderer.name);
                Assert.That(y.HiddenSourceTriangles, Is.EqualTo(x.HiddenSourceTriangles), renderer.name);
                for (var s = 0; s < x.OutputMesh.subMeshCount; s++)
                    Assert.That(y.OutputMesh.GetIndices(s), Is.EqualTo(x.OutputMesh.GetIndices(s)), $"{renderer.name} submesh {s}");
            }
        }

        // Tights under the rim of an always-on boot, always-on skin half a millimetre under the tights, all three flat. The
        // tights are not always on, so they hide none of the skin; the boot rides a bone four steps from the skin's, so it
        // hides none of the skin either; the tights ride both, so the boot hides them. Cut at 4 mm, the boot's round rim
        // draws back, and the skin, cut too, may cover from as far behind as the boot may: the tights the rim uncovers must
        // come back, though the skin lies straight behind them within that setback, where it would read as their cover and
        // a viewer would see it through the hole.
        [Test]
        public void ALayerUnderATriangle_ThatNeverCoveredIt_DoesNotStandInForTheCoverItLost()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            var foot = a.Bone("Armature/Hips/Leg/Knee/Foot");
            var skin = a.Skinned("Skin", Disc(0.1f, 0f, 20, 64), new[] { foot });
            var tightsMesh = Disc(0.1f, 0.0005f, 20, 64);
            tightsMesh.boneWeights = tightsMesh.vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f }).ToArray();
            var tights = a.Skinned("Tights", tightsMesh, new[] { chest, foot });
            var boot = a.Skinned("Boot", Disc(0.05f, 0.0015f, 10, 64), new[] { chest });
            var c = a.Optimizer(decimate: true);
            c.alwaysOn.Add(boot);
            c.alwaysOn.Add(skin);
            c.renderers.Add(new RendererSettings { renderer = tights, excludeFromDecimation = true });
            var renderers = new Renderer[] { skin, tights, boot };
            var bootOnly = new[] { 0UL, 1UL << 2, 0UL };
            var anyMesh = new[] { ulong.MaxValue, ulong.MaxValue, ulong.MaxValue };

            var off = Run(TargetKind.Tolerance, 4f, rejudge: false);
            var on = Run(TargetKind.Tolerance, 4f, rejudge: true);

            Assert.That(Of(off, skin).HiddenRemoved, Is.Zero, "the boot hides none of the skin");
            Assert.That(Of(off, tights).HiddenRemoved, Is.GreaterThan(0), "and hides the tights");
            Assert.That(Of(on, skin).Error, Is.GreaterThan(0.0005f), "the skin's setback reaches past the half millimetre");
            int Lost(ulong[] behind) => CheckWritten(off, renderers, behind, out _).Violations.Select(v => v.Split(',')[0]).Distinct().Count();
            var lost = Lost(bootOnly);
            Assert.That(lost, Is.GreaterThan(Lost(anyMesh)), "the cut boot uncovers tights that only the skin under them covers from behind");
            Assert.That(Of(on, tights).HiddenRestored, Is.EqualTo(lost), "every one of them comes back");
            var check = CheckWritten(on, renderers, bootOnly, out _);
            Assert.That(check.Ok, Is.True, check.ToString());
            Assert.That(check.HiddenChecked, Is.GreaterThan(0), "some of the tights stay removed");
        }

        // A finer body, decimated too, at a budget of 800: the first cut uncovers triangles, the budget pays for them by
        // cutting deeper, and what the deeper cut uncovers is judged again, until the written avatar is inside the budget
        // and covered.
        [Test]
        public void ABudgetPaysForWhatItRestores_AndIsMet()
        {
            Build(garmentFrom: 60f, rings: 32, bodyDecimated: true);

            var off = Run(TargetKind.TriangleBudget, 800, rejudge: false);
            Assert.That(CheckWritten(off, setback: true).Ok, Is.False, "the cut the budget alone chooses uncovers some of what occlusion removed");

            var on = Run(TargetKind.TriangleBudget, 800, rejudge: true);

            Assert.That(on.HiddenRestored, Is.GreaterThan(0));
            Assert.That(on.RejudgePasses, Is.GreaterThan(1), "the restored triangles were paid for by a deeper cut");
            Assert.That(on.TargetMet, Is.True);
            Assert.That(on.TrianglesAfter, Is.LessThanOrEqualTo(800));
            Assert.That(on.TrianglesAfter - on.HiddenRestored, Is.LessThan(off.TrianglesAfter), "the cut keeps fewer to pay for them");
            Assert.That(on.Diagnostics.Any(d => d.Key == DiagnosticKeys.BudgetNotMet), Is.False);
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
        }

        // Cut too deep, the cape covers less than any cut saves: at a budget that leaves it ten triangles over the body,
        // no pass meets it, and the avatar is still written covered, the budget reported not met.
        [Test]
        public void ABudgetNoPassMeets_IsReportedNotMet_AndTheAvatarIsStillCovered()
        {
            BuildCape();
            var bodyAfter = Of(Run(TargetKind.Tolerance, 0.01f, rejudge: false), body).TrianglesAfter;

            var on = Run(TargetKind.TriangleBudget, bodyAfter + 10, rejudge: true);

            Assert.That(on.TargetMet, Is.False);
            Assert.That(on.RejudgePasses, Is.GreaterThan(1));
            Assert.That(on.Diagnostics.Count(d => d.Key == DiagnosticKeys.BudgetNotMet), Is.EqualTo(1));
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
        }

        // At 635 the first cut lands a triangle under what it was asked for and restores two, one over. Asking the next
        // pass for the overage less than before hands that slack back and cuts the same again; asking it for this cut less
        // the overage moves it, and the budget is met.
        [Test]
        public void ABudgetTheCutLandsUnder_IsNotHandedBackItsSlack_AndIsMet()
        {
            Build(garmentFrom: 60f, rings: 32, bodyDecimated: true);

            var on = Run(TargetKind.TriangleBudget, 635, rejudge: true);

            Assert.That(on.HiddenRestored, Is.GreaterThan(0));
            Assert.That(on.TargetMet, Is.True, $"{on.TrianglesAfter} after {on.RejudgePasses} passes");
            Assert.That(on.TrianglesAfter, Is.LessThanOrEqualTo(635));
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
        }

        // The garment of MirrorTests, drawn mirrored, coarsened at 16 mm until its rim draws back. The re-judge reads its
        // faces as Unity draws them, as the first solve did: it gives back what the cut uncovered, and every triangle still
        // removed is covered, judged by the checker from the same winding.
        [Test]
        public void AMirroredGarment_GivesBackWhatItNoLongerCovers_AndWhatStaysRemovedIsCovered()
        {
            Build(garmentFrom: 60f, mirrored: true);

            var on = Run(TargetKind.Tolerance, 16f, rejudge: true);

            var check = CheckWritten(on, setback: true);
            TestContext.WriteLine($"{on.HiddenRestored} restored, {Of(on, body).HiddenRemoved} still removed; the garment cut from {Of(on, garment).TrianglesBefore} to {Of(on, garment).TrianglesAfter} triangles");
            Assert.That(Of(on, garment).Mirrored, Is.True, "the garment is drawn mirrored");
            Assert.That(on.HiddenRestored, Is.GreaterThan(0), "the cut uncovered some of the body");
            Assert.That(check.Ok, Is.True, check.ToString());
            Assert.That(check.HiddenChecked, Is.GreaterThan(0), "some of the body stays removed");
        }

        [Test]
        public void TheBuildSwitch_TurnsRejudgingOffUntilDisposed()
        {
            Assert.That(new PipelineInput().Rejudge, Is.True, "on unless overridden");
            using (BuildSettings.OverrideRejudging(false))
                Assert.That(new PipelineInput().Rejudge, Is.False);
            Assert.That(new PipelineInput().Rejudge, Is.True);
        }
    }
}
