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
    // A body under an always-on garment on the same chest bone, 5 mm apart. Coarsened far enough, the garment gives way in
    // two ways. Where its rim draws back, a triangle occlusion removed there is no longer covered and shows; judged again
    // against the garment as written, it must be written back. Where its faces sink past the body, the cover moved but did
    // not go: a viewer still sees the garment, so the triangle stays removed. The body carries a key that swells its lower
    // half, so each verdict holds in a configuration as well as at rest.
    public class RejudgeTests
    {
        AvatarFixture a;
        Transform chest;
        SkinnedMeshRenderer body, garment;

        [TearDown] public void TearDown() => a?.Dispose();

        /// <summary>
        /// The garment a band of sphere from <paramref name="garmentFrom"/> degrees down, over a body of
        /// <paramref name="rings"/> rings, left undecimated unless <paramref name="bodyDecimated"/>.
        /// </summary>
        void Build(float garmentFrom, int rings = 16, bool bodyDecimated = false)
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            var bodyMesh = Welded(a.Sphere(0.1f, rings, 2 * rings, name: "body"));
            bodyMesh.AddBlendShapeFrame("Swell", 100f, bodyMesh.vertices.Select(v => v.y < -0.03f ? v.normalized * 0.001f : Vector3.zero).ToArray(), null, null);
            body = a.Skinned("Body", bodyMesh, new[] { chest });
            garment = a.Skinned("Garment", Welded(a.Sphere(0.105f, rings + rings / 2, 3 * rings, garmentFrom, 180f, name: "garment")), new[] { chest });
            var c = a.Optimizer(decimate: true);
            c.alwaysOn.Add(garment);
            if (!bodyDecimated) c.renderers.Add(new RendererSettings { renderer = body, excludeFromDecimation = true });
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
        /// under which the garment covers the body from as far behind as its cut's error; without, by the rule itself.
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

        // At a 6 mm tolerance the bowl's rim draws back. Switched off, the checker finds removed triangles the written
        // garment no longer covers; on, they are written back as authored and every triangle still removed is covered, at
        // rest and with the body's key applied, by the garment where it stands or where its cut moved it past them.
        [Test]
        public void ACoarsenedGarment_GivesBackWhatItNoLongerCovers_AndWhatStaysRemovedIsCovered()
        {
            Build(garmentFrom: 60f);

            var off = Run(TargetKind.Tolerance, 6f, rejudge: false);
            Assert.That(CheckWritten(off, setback: true).Ok, Is.False, "the coarsened garment uncovers some of what occlusion removed");
            Assert.That(off.RejudgePasses, Is.Zero);

            var on = Run(TargetKind.Tolerance, 6f, rejudge: true);
            var check = CheckWritten(on, setback: true);
            Assert.That(check.Ok, Is.True, check.ToString());
            Assert.That(check.HiddenChecked, Is.GreaterThan(0), "some triangles stay removed");

            var r = Of(on, body);
            Assert.That(r.HiddenRestored, Is.GreaterThan(0));
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
        // through and gives back what the coarsened garment uncovered.
        [Test]
        public void ARendererNeitherJudgedNorBlocking_LastInTheAvatar_DoesNotStopTheJudge()
        {
            Build(garmentFrom: 60f);
            var charmMesh = a.Sphere(0.02f, 8, 16, name: "charm");
            charmMesh.vertices = charmMesh.vertices.Select(v => v + new Vector3(0.3f, 0f, 0f)).ToArray();
            charmMesh.RecalculateBounds();
            var charm = a.Skinned("Charm", charmMesh, new[] { chest });
            a.Optimizer(decimate: true).renderers.Add(new RendererSettings { renderer = charm, excludeFromDecimation = true });

            var on = Run(TargetKind.Tolerance, 6f, rejudge: true);

            Assert.That(on.Diagnostics.Select(d => d.Key), Has.No.Member(DiagnosticKeys.RejudgeFailed));
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

        // Tights under the rim of an always-on boot, always-on skin half a millimetre under the tights. The tights are not
        // always on, so they hide none of the skin; the boot rides a bone four steps from the skin's, so it hides none of
        // the skin either; the tights ride both, so the boot hides them. Cut at 6 mm, the boot's rim draws back, and the
        // skin, cut too, may cover from as far behind as the boot may: the tights the rim uncovers must come back, though
        // the skin lies within that setback under them, where a setback alone reads it as their cover and a viewer would
        // see it through the hole. The clearance rule is off, since it holds this rim where it stands; the rule under
        // test does not read it.
        [Test]
        public void ALayerUnderATriangle_ThatNeverCoveredIt_DoesNotStandInForTheCoverItLost()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
            var foot = a.Bone("Armature/Hips/Leg/Knee/Foot");
            var skin = a.Skinned("Skin", Welded(a.Sphere(0.1f, 16, 32, name: "skin")), new[] { foot });
            var tightsMesh = Welded(a.Sphere(0.1005f, 16, 32, name: "tights"));
            tightsMesh.boneWeights = tightsMesh.vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f }).ToArray();
            var tights = a.Skinned("Tights", tightsMesh, new[] { chest, foot });
            var boot = a.Skinned("Boot", Welded(a.Sphere(0.105f, 24, 48, 60f, 180f, name: "boot")), new[] { chest });
            var c = a.Optimizer(decimate: true);
            c.alwaysOn.Add(boot);
            c.alwaysOn.Add(skin);
            c.renderers.Add(new RendererSettings { renderer = tights, excludeFromDecimation = true });
            var renderers = new Renderer[] { skin, tights, boot };
            var bootOnly = new[] { 0UL, 1UL << 2, 0UL };
            var anyMesh = new[] { ulong.MaxValue, ulong.MaxValue, ulong.MaxValue };
            var decimation = DecimationSettings.Default;
            decimation.Clearance.Enabled = false;

            OptimizationReport off, on;
            using (BuildSettings.Override(decimation))
            {
                off = Run(TargetKind.Tolerance, 6f, rejudge: false);
                on = Run(TargetKind.Tolerance, 6f, rejudge: true);
            }

            Assert.That(Of(off, skin).HiddenRemoved, Is.Zero, "the boot hides none of the skin");
            Assert.That(Of(off, tights).HiddenRemoved, Is.GreaterThan(0), "and hides the tights");
            Assert.That(Of(on, skin).Error, Is.GreaterThan(0.0005f), "the skin's setback reaches past the half millimetre");
            var lost = CheckWritten(off, renderers, bootOnly, out _).Violations.Count;
            Assert.That(lost, Is.GreaterThan(CheckWritten(off, renderers, anyMesh, out _).Violations.Count),
                "the cut boot uncovers tights that only the skin under them covers from behind");
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

        // Cut too deep, the garment covers less than any cut saves: no pass meets the budget, and the avatar is still
        // written covered, the budget reported not met.
        [Test]
        public void ABudgetNoPassMeets_IsReportedNotMet_AndTheAvatarIsStillCovered()
        {
            Build(garmentFrom: 60f);

            var on = Run(TargetKind.TriangleBudget, 600, rejudge: true);

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
