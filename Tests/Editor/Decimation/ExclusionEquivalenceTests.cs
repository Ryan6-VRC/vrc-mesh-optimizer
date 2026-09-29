using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Ryan6Vrc.MeshOptimizer.Tests.Decimation.LayerFixtures;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    // Temporary: excluding triangles by mask must record exactly what decimating a copy without them records. Each case
    // builds every mesh's layers, the clearance surface and each mesh's sequence both ways, and compares bit for bit.
    internal sealed class ExclusionEquivalenceTests
    {
        public enum Mask { None, Random, Band, All }

        static MeshBuilder WithDegenerates(MeshBuilder b)
        {
            var mid = b.Triangles.Count / 2;
            b.Triangles.Insert(mid, new int3(0, 0, 1));
            b.Submesh.Insert(mid, b.Submesh[mid]);
            b.Triangles.Insert(1, new int3(3, 4, 3));
            b.Submesh.Insert(1, b.Submesh[1]);
            b.Tri(5, 6, 6);
            return b;
        }

        static MeshBuilder Grid()
        {
            var b = Fixtures.Grid(24, 24, 0.3f, 0.3f, out _);
            for (var t = 0; t < b.Submesh.Count; t++) b.Submesh[t] = t / 2 % 7 < 3 ? 1 : 0;
            return b;
        }

        static MeshBuilder SeamedCylinderWithAFrame()
        {
            var b = Fixtures.Cylinder(32, 20, 0.05f, 0.2f, true, out _);
            var f = b.AddFrame();
            for (var v = 0; v < b.VertexCount; v++)
                if (b.Positions[v].y > 0.12f) f[v] = new float3(0.004f * b.Positions[v].x / 0.05f, 0f, 0f);
            return b;
        }

        static bool[] MaskOf(MeshBuilder b, Mask mask, int seed)
        {
            var random = new System.Random(seed);
            return b.Triangles.Select(t =>
            {
                switch (mask)
                {
                    case Mask.Random: return random.NextDouble() < 0.35;
                    case Mask.Band:
                        var y = (b.Positions[t.x].y + b.Positions[t.y].y + b.Positions[t.z].y) / 3f;
                        var x = (b.Positions[t.x].x + b.Positions[t.y].x + b.Positions[t.z].x) / 3f;
                        return math.frac(y * 20f + x * 7f) < 0.4f;
                    case Mask.All: return true;
                    default: return false;
                }
            }).ToArray();
        }

        static IEnumerable<TestCaseData> SingleMeshCases()
        {
            foreach (var mask in new[] { Mask.None, Mask.Random, Mask.Band, Mask.All })
            {
                yield return new TestCaseData("grid", mask);
                yield return new TestCaseData("cylinder", mask);
                yield return new TestCaseData("icosphere", mask);
                yield return new TestCaseData("strip", mask);
            }
        }

        [TestCaseSource(nameof(SingleMeshCases))]
        public void DecimationFixture_RecordsTheSameSequenceBothWays(string fixture, Mask mask)
        {
            MeshBuilder b;
            switch (fixture)
            {
                case "grid": b = Grid(); break;
                case "cylinder": b = SeamedCylinderWithAFrame(); break;
                case "icosphere": b = Fixtures.Icosphere(3, 0.1f); break;
                default: b = Fixtures.Strip(40, 0.2f, 2f, 0.02f, out _, out _); break;
            }
            WithDegenerates(b);
            Compare(new List<MeshBuilder> { b }, new List<bool[]> { MaskOf(b, mask, 7) }, false);
        }

        [TestCase(Mask.Random, false)]
        [TestCase(Mask.Band, false)]
        [TestCase(Mask.None, false)]
        [TestCase(Mask.Random, true)]
        [TestCase(Mask.Band, true)]
        public void LayerFixture_RecordsTheSameSequenceBothWays(Mask mask, bool bent)
        {
            var meshes = bent
                ? new List<MeshBuilder> { BendFixtures.SkinnedTube(64, 60, 0.05f, 0f, 0.02f), BendFixtures.SkinnedTube(56, 54, 0.05f + BendFixtures.Gap, 0.5f, 0.02f) }
                : new List<MeshBuilder> { SkinTube(), LayerTube(Over(0.002f)) };
            // Two pieces in one mesh, so a piece hidden whole shifts the numbering of the pieces after it.
            if (!bent) Tube(20, 8, Length, Over(0.006f), 0.25f, meshes[1]);
            WithDegenerates(meshes[0]);
            WithDegenerates(meshes[1]);
            var masks = new List<bool[]> { MaskOf(meshes[0], mask, 11), MaskOf(meshes[1], mask == Mask.Band ? Mask.Random : Mask.None, 13) };
            Compare(meshes, masks, bent);
        }

        static void Compare(List<MeshBuilder> builders, List<bool[]> hidden, bool rig)
        {
            var owned = new List<IDisposable>();
            try
            {
                var full = new List<EvalMesh>();
                var seen = new List<EvalMesh>();
                var reduced = new List<int[]>();
                var masks = new List<NativeArray<bool>>();
                foreach (var b in builders)
                {
                    var mesh = b.Build();
                    owned.Add(mesh);
                    full.Add(mesh);
                    var h = hidden[full.Count - 1];
                    var keep = Enumerable.Range(0, mesh.TriangleCount).Where(t => !h[t]).ToArray();
                    reduced.Add(keep);
                    var copy = mesh;
                    copy.Triangles = new NativeArray<int3>(keep.Select(t => mesh.Triangles[t]).ToArray(), Allocator.Persistent);
                    copy.TriangleSubmesh = new NativeArray<int>(keep.Select(t => mesh.TriangleSubmesh[t]).ToArray(), Allocator.Persistent);
                    owned.Add(copy.Triangles);
                    owned.Add(copy.TriangleSubmesh);
                    seen.Add(copy);
                    var m = new NativeArray<bool>(h, Allocator.Persistent);
                    owned.Add(m);
                    masks.Add(m);
                }

                // Layers and the clearance surface, as the pipeline builds them: over a kept copy, and over a mask.
                var oldSources = new List<ClearanceLayerSource>();
                var newSources = new List<ClearanceLayerSource>();
                var oldLayers = new List<NativeArray<int>>();
                var newLayers = new List<NativeArray<int>>();
                int nextOld = 0, nextNew = 0;
                for (var i = 0; i < full.Count; i++)
                {
                    var ol = Layers.Pieces(seen[i].Positions, seen[i].Triangles, Allocator.Persistent, out var oc);
                    owned.Add(ol);
                    for (var k = 0; k < ol.Length; k++) ol[k] += nextOld;
                    nextOld += oc;
                    var nl = Layers.Pieces(full[i].Positions, full[i].Triangles, masks[i], Allocator.Persistent, out var nc);
                    owned.Add(nl);
                    for (var k = 0; k < nl.Length; k++)
                        if (nl[k] >= 0) nl[k] += nextNew;
                    nextNew += nc;
                    Assert.That(nc, Is.EqualTo(oc), $"mesh {i}: pieces");
                    for (var k = 0; k < reduced[i].Length; k++) Assert.That(nl[reduced[i][k]], Is.EqualTo(ol[k]), $"mesh {i}: layer of kept triangle {k}");
                    for (var t = 0; t < nl.Length; t++) if (hidden[i][t]) Assert.That(nl[t], Is.EqualTo(-1), $"mesh {i}: hidden triangle {t} is in no piece");
                    oldLayers.Add(ol);
                    newLayers.Add(nl);
                    oldSources.Add(new ClearanceLayerSource { Positions = seen[i].Positions, Triangles = seen[i].Triangles, TriangleLayer = ol });
                    newSources.Add(new ClearanceLayerSource { Positions = full[i].Positions, Triangles = full[i].Triangles, TriangleLayer = nl, Excluded = masks[i] });
                }
                var reach = ClearanceSettings.Default.Reach;
                var oldSurface = ClearanceSurface.Build(oldSources, reach, Allocator.Persistent);
                owned.Add(oldSurface);
                var newSurface = ClearanceSurface.Build(newSources, reach, Allocator.Persistent);
                owned.Add(newSurface);
                Same(oldSurface.Nodes, newSurface.Nodes, "surface nodes");
                Same(oldSurface.Prims, newSurface.Prims, "surface prims");
                Same(oldSurface.PrimLayer, newSurface.PrimLayer, "surface prim layers");
                Same(oldSurface.NodeLayer, newSurface.NodeLayer, "surface node layers");
                Same(oldSurface.PrimClearance, newSurface.PrimClearance, "surface prim clearance");
                Same(oldSurface.NodeClearance, newSurface.NodeClearance, "surface node clearance");

                var bendRig = rig ? BendFixtures.Rig() : default;
                if (rig)
                {
                    owned.Add(bendRig.Parent);
                    owned.Add(bendRig.Position);
                    owned.Add(bendRig.Axis);
                    owned.Add(bendRig.Right);
                }
                var settings = DecimationSettings.Default;
                settings.Clearance.Enabled = true;
                for (var i = 0; i < full.Count; i++)
                {
                    using var before = Decimator.Run(seen[i], settings, default, new ClearanceInput { Surface = oldSurface, TriangleLayer = oldLayers[i] }, bendRig, Allocator.Persistent, out var beforeStats);
                    using var after = Decimator.Run(full[i], masks[i], settings, default, new ClearanceInput { Surface = newSurface, TriangleLayer = newLayers[i] }, bendRig, Allocator.Persistent, out var afterStats);
                    var what = $"mesh {i}";
                    TestContext.WriteLine($"{what}: {full[i].TriangleCount} triangles, {reduced[i].Length} kept, {before.Length} steps, near {beforeStats.NearVertices}, refused {beforeStats.Refused}");
                    Assert.That(after.InitialTriangleCount, Is.EqualTo(before.InitialTriangleCount), what + ": initial triangles");
                    Assert.That(after.SourceVertexCount, Is.EqualTo(before.SourceVertexCount), what + ": source vertices");
                    Assert.That(afterStats.NearVertices, Is.EqualTo(beforeStats.NearVertices), what + ": near vertices");
                    Assert.That(afterStats.Refused, Is.EqualTo(beforeStats.Refused), what + ": refused");
                    Same(before.Steps, after.Steps, what + ": steps");
                    Same(before.Records, after.Records, what + ": records");
                    Same(before.WorkingOrigin, after.WorkingOrigin, what + ": working origin");
                    Assert.That(after.WorkingTriangles.Length, Is.EqualTo(full[i].TriangleCount), what + ": working triangles per mesh triangle");
                    for (var k = 0; k < reduced[i].Length; k++)
                        Assert.That(after.WorkingTriangles[reduced[i][k]], Is.EqualTo(before.WorkingTriangles[k]), $"{what}: working triangle {k}");
                    for (var t = 0; t < full[i].TriangleCount; t++)
                        if (hidden[i][t]) Assert.That(after.WorkingTriangles[t], Is.EqualTo(new int3(-1)), $"{what}: excluded triangle {t}");

                    foreach (var at in new[] { 0, before.Length / 3, before.Length / 2, before.Length })
                    {
                        using var cb = before.Cut(at, Allocator.Persistent);
                        using var ca = after.Cut(at, Allocator.Persistent);
                        Same(cb.Triangles.AsArray(), ca.Triangles.AsArray(), $"{what}: cut {at} triangles");
                        Assert.That(ca.SourceTriangle.AsArray().ToArray(), Is.EqualTo(cb.SourceTriangle.AsArray().ToArray().Select(t => reduced[i][t]).ToArray()),
                            $"{what}: cut {at} names the mesh's triangles");
                        Same(cb.KeptVertices.AsArray(), ca.KeptVertices.AsArray(), $"{what}: cut {at} kept vertices");
                    }
                }
            }
            finally
            {
                for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        static void Same<T>(NativeArray<T> a, NativeArray<T> b, string what) where T : struct
        {
            Assert.That(b.Length, Is.EqualTo(a.Length), what + ": length");
            var size = UnsafeUtility.SizeOf<T>();
            var x = a.Reinterpret<byte>(size).ToArray();
            var y = b.Reinterpret<byte>(size).ToArray();
            Assert.That(x.SequenceEqual(y), Is.True, what + ": bit for bit");
        }
    }
}
