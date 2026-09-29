using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    public class TopologyTests
    {
        const int Nx = 6, Ny = 4, SplitI = 3, SplitJ = 2;

        /// <summary>
        /// A grid whose vertices are split along column SplitI (and optionally row SplitJ): each side of a split
        /// line has its own vertex there, at the same position, as a UV seam or hard edge arrives from an import.
        /// </summary>
        static MeshBuilder SplitGrid(bool splitRow, out Dictionary<(int i, int j, int qx, int qy), int> index)
        {
            var b = new MeshBuilder();
            var map = new Dictionary<(int, int, int, int), int>();
            int Vertex(int i, int j, int qx, int qy)
            {
                qx = i == SplitI ? qx : 0;
                qy = splitRow && j == SplitJ ? qy : 0;
                var key = (i, j, qx, qy);
                if (!map.TryGetValue(key, out var v))
                {
                    v = b.Add(new float3(i * 0.01f, j * 0.01f, 0f), new float2(i / (float)Nx + qx * 0.5f, j / (float)Ny + qy * 0.5f));
                    map[key] = v;
                }
                return v;
            }
            for (var j = 0; j < Ny; j++)
            for (var i = 0; i < Nx; i++)
            {
                var qx = i < SplitI ? 0 : 1;
                var qy = j < SplitJ ? 0 : 1;
                var a = Vertex(i, j, qx, qy);
                var c = Vertex(i + 1, j, qx, qy);
                var d = Vertex(i + 1, j + 1, qx, qy);
                var e = Vertex(i, j + 1, qx, qy);
                b.Tri(a, c, d);
                b.Tri(a, d, e);
            }
            index = map;
            return b;
        }

        static MeshTopology Classify(MeshBuilder b)
        {
            var mesh = b.Build();
            try
            {
                return MeshTopology.Build(mesh, TopologySettings.Default, Allocator.Persistent);
            }
            finally
            {
                mesh.Dispose();
            }
        }

        [Test]
        public void CoincidentWedgesThatAgree_AreASeam()
        {
            var b = SplitGrid(false, out var idx);
            // A live frame that moves both sides of the split alike keeps them one surface.
            var f = b.AddFrame();
            for (var j = 0; j <= Ny; j++)
            {
                f[idx[(SplitI, j, 0, 0)]] = new float3(0, 0, 0.004f);
                f[idx[(SplitI, j, 1, 0)]] = new float3(0, 0, 0.004f);
            }
            using var topo = Classify(b);
            for (var j = 1; j < Ny; j++)
            {
                var l = idx[(SplitI, j, 0, 0)];
                var r = idx[(SplitI, j, 1, 0)];
                Assert.That(topo.Kind[l], Is.EqualTo(VertexKind.Seam), $"row {j} left");
                Assert.That(topo.Kind[r], Is.EqualTo(VertexKind.Seam), $"row {j} right");
                Assert.That(topo.Partner[l], Is.EqualTo(r));
                Assert.That(topo.Partner[r], Is.EqualTo(l));
            }
            // Where the seam meets the open border, a wedge has two open edges on different lines: locked.
            Assert.That(topo.Kind[idx[(SplitI, 0, 0, 0)]], Is.EqualTo(VertexKind.Locked));
            Assert.That(topo.Kind[idx[(SplitI, Ny, 1, 0)]], Is.EqualTo(VertexKind.Locked));
        }

        [Test]
        public void CoincidentWedgesThatDisagreeInWeights_AreBorders()
        {
            var b = SplitGrid(false, out var idx);
            for (var j = 0; j <= Ny; j++) b.SetWeights(idx[(SplitI, j, 1, 0)], (0, 0.5f), (1, 0.5f));
            using var topo = Classify(b);
            for (var j = 1; j < Ny; j++)
            {
                Assert.That(topo.Kind[idx[(SplitI, j, 0, 0)]], Is.EqualTo(VertexKind.Border), $"row {j} left");
                Assert.That(topo.Kind[idx[(SplitI, j, 1, 0)]], Is.EqualTo(VertexKind.Border), $"row {j} right");
            }
        }

        [Test]
        public void CoincidentWedgesThatDisagreeInALiveDelta_AreBorders()
        {
            var b = SplitGrid(false, out var idx);
            // Only the right side moves: at rest the two sides meet, driven they part.
            var f = b.AddFrame();
            for (var j = 0; j <= Ny; j++)
            for (var i = SplitI; i <= Nx; i++)
                f[idx[(i, j, i == SplitI ? 1 : 0, 0)]] = new float3(0, 0, 0.004f);
            using var topo = Classify(b);
            for (var j = 1; j < Ny; j++)
            {
                Assert.That(topo.Kind[idx[(SplitI, j, 0, 0)]], Is.EqualTo(VertexKind.Border), $"row {j} left");
                Assert.That(topo.Kind[idx[(SplitI, j, 1, 0)]], Is.EqualTo(VertexKind.Border), $"row {j} right");
            }
        }

        [Test]
        public void ADeltaBelowTolerance_StillAgrees()
        {
            var b = SplitGrid(false, out var idx);
            var f = b.AddFrame();
            for (var j = 0; j <= Ny; j++) f[idx[(SplitI, j, 1, 0)]] = new float3(0, 0, 2e-6f);
            using var topo = Classify(b);
            Assert.That(topo.Kind[idx[(SplitI, 2, 0, 0)]], Is.EqualTo(VertexKind.Seam));
        }

        [Test]
        public void FourWedgesAtACrossing_AreLocked()
        {
            var b = SplitGrid(true, out var idx);
            using var topo = Classify(b);
            for (var qx = 0; qx < 2; qx++)
            for (var qy = 0; qy < 2; qy++)
                Assert.That(topo.Kind[idx[(SplitI, SplitJ, qx, qy)]], Is.EqualTo(VertexKind.Locked), $"quadrant {qx},{qy}");
            // Away from the crossing both split lines are ordinary seams.
            Assert.That(topo.Kind[idx[(SplitI, 1, 0, 0)]], Is.EqualTo(VertexKind.Seam));
            Assert.That(topo.Kind[idx[(1, SplitJ, 0, 0)]], Is.EqualTo(VertexKind.Seam));
        }

        [Test]
        public void AVertexSharedAcrossSubmeshes_IsASeamBetweenTheirUses()
        {
            var b = Fixtures.Grid(6, 4, 0.06f, 0.04f, out var idx);
            for (var t = 0; t < b.Triangles.Count; t++)
            {
                var c = (b.Positions[b.Triangles[t].x] + b.Positions[b.Triangles[t].y] + b.Positions[b.Triangles[t].z]) / 3f;
                b.Submesh[t] = c.x < 0f ? 0 : 1;
            }
            using var topo = Classify(b);
            Assert.That(topo.WorkingVertexCount, Is.EqualTo(b.VertexCount + 5), "one extra use per shared vertex on the boundary column");
            for (var j = 1; j < 4; j++)
            {
                var v = idx[3, j];
                Assert.That(topo.Kind[v], Is.EqualTo(VertexKind.Seam), $"row {j}");
                Assert.That(topo.Origin[topo.Partner[v]], Is.EqualTo(v), "the partner is the same source vertex's other use");
            }
        }

        [Test]
        public void AnEdgeSharedByThreeTriangles_LocksItsEnds()
        {
            var b = Fixtures.Grid(4, 4, 0.04f, 0.04f, out var idx);
            var fin = b.Add(new float3(0, 0, 0.01f));
            var a = idx[2, 2];
            var c = idx[3, 2];
            b.Tri(a, c, fin);
            using var topo = Classify(b);
            Assert.That(topo.Kind[a], Is.EqualTo(VertexKind.Locked));
            Assert.That(topo.Kind[c], Is.EqualTo(VertexKind.Locked));
            Assert.That(topo.Kind[idx[1, 1]], Is.EqualTo(VertexKind.Interior));
        }

        [Test]
        public void DoubleSidedSheets_StayIndependentInsideAndPairAsBordersAtTheHem()
        {
            var b = Fixtures.Grid(4, 4, 0.04f, 0.04f, out var idx);
            var front = b.VertexCount;
            var back = new int[front];
            for (var v = 0; v < front; v++) back[v] = b.Add(b.Positions[v], b.Uvs[v]);
            var n = b.Triangles.Count;
            for (var t = 0; t < n; t++)
            {
                var tri = b.Triangles[t];
                b.Tri(back[tri.x], back[tri.z], back[tri.y]);
            }
            using var topo = Classify(b);
            var inner = idx[2, 2];
            Assert.That(topo.Kind[inner], Is.EqualTo(VertexKind.Interior));
            Assert.That(topo.Kind[back[inner]], Is.EqualTo(VertexKind.Interior));
            // The hem is a real border in every configuration; the two sheets' hems move together.
            var hem = idx[2, 0];
            Assert.That(topo.Kind[hem], Is.EqualTo(VertexKind.Border));
            Assert.That(topo.Partner[hem], Is.EqualTo(back[hem]));
        }

        // Removing hidden triangles leaves the vertices only they used in the arrays decimation reads. One that sits
        // exactly on a live seam vertex, with its own UV and weights, must not count as a wedge there: the mesh decimates
        // as if it were absent, whether it is numbered after the live vertices or before them.
        [TestCase(false)]
        [TestCase(true)]
        public void AnOrphanOnASeamVertex_DecimatesAsIfAbsent(bool orphanFirst)
        {
            var plain = Fixtures.Cylinder(16, 8, 0.05f, 0.2f, seam: true, out var index);
            var offset = orphanFirst ? 1 : 0;
            var withOrphan = new MeshBuilder();
            void Orphan()
            {
                var o = withOrphan.Add(plain.Positions[index[0, 4]], new float2(0.3f, 0.7f));
                withOrphan.SetWeights(o, (5, 1f));
            }
            if (orphanFirst) Orphan();
            for (var v = 0; v < plain.VertexCount; v++) withOrphan.Add(plain.Positions[v], plain.Uvs[v]);
            if (!orphanFirst) Orphan();
            foreach (var t in plain.Triangles) withOrphan.Tri(t.x + offset, t.y + offset, t.z + offset);

            using (var a = Classify(plain))
            using (var b = Classify(withOrphan))
            {
                Assert.That(a.Kind[index[0, 4]], Is.EqualTo(VertexKind.Seam), "the orphan sits on a seam");
                for (var v = 0; v < plain.VertexCount; v++)
                    Assert.That(b.Kind[v + offset], Is.EqualTo(a.Kind[v]), $"vertex {v}");
            }

            using var expected = Fixtures.Decimate(plain);
            using var actual = Fixtures.Decimate(withOrphan);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var s = 0; s < expected.Length; s++)
                Assert.That((actual.Steps[s].Error, actual.Steps[s].TriangleCount), Is.EqualTo((expected.Steps[s].Error, expected.Steps[s].TriangleCount)), $"step {s}");
            for (var r = 0; r < expected.Records.Length; r++)
                Assert.That(actual.Records[r], Is.EqualTo(expected.Records[r] + offset), $"record {r}");
        }
    }
}
