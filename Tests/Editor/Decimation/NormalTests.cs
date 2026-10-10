using System;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// The normal term (<see cref="NormalSettings"/>): each fixture is cut at a 1 mm tolerance and its shading measured as a
    /// viewer gets it (<see cref="Shading"/>), with the term off and at its default weight.
    /// </summary>
    public class NormalTests
    {
        static DecimationSettings Off(DecimationSettings s)
        {
            s.Normal.Weight = 0f;
            return s;
        }

        static (Shading shading, int triangles) AtTolerance(MeshBuilder b, Func<DecimationSettings, DecimationSettings> tweak, float spacing)
        {
            using var seq = Fixtures.Decimate(b, tweak);
            using var cut = seq.Cut(Fixtures.CutAtError(seq, NormalFixtures.Tolerance), Allocator.Persistent);
            var tris = Fixtures.ToArray(cut.Triangles);
            return (NormalFixtures.Measure(b, tris, spacing), tris.Length);
        }

        static (Shading off, Shading on) Compare(MeshBuilder b, float spacing)
        {
            var (off, offTris) = AtTolerance(b, Off, spacing);
            var (on, onTris) = AtTolerance(b, null, spacing);
            TestContext.WriteLine($"{b.Triangles.Count} tris; off: {offTris}, {off}");
            TestContext.WriteLine($"on: {onTris}, {on}");
            return (off, on);
        }

        // Nothing in a flat band's geometry marks its rims, so without the term it collapses to two triangles, and the rims'
        // tilt spreads across the whole band. The term keeps the rim strips as narrow as the source's.
        [Test]
        public void ATiltedRim_OnAFlatBand_KeepsItsShading()
        {
            var (off, on) = Compare(NormalFixtures.TiltedBand(), 0.002f);
            Assert.That(off.Share10, Is.GreaterThan(0.5f), "the fixture reproduces the defect with the term off");
            Assert.That(on.Share10, Is.LessThan(0.02f));
        }

        // The same across a UV seam, whose two sides collapse as a pair, each priced against its own side's normals.
        [Test]
        public void ATiltedRim_AcrossASeam_KeepsItsShading()
        {
            var b = NormalFixtures.TiltedBand();
            NormalFixtures.SplitColumn(b, 20);
            var (off, on) = Compare(b, 0.002f);
            Assert.That(off.Share10, Is.GreaterThan(0.5f), "the fixture reproduces the defect with the term off");
            Assert.That(on.Share10, Is.LessThan(0.02f));
        }

        // Without the term a bent strap's front takes corners past its rims, where the normals face sideways and back.
        [Test]
        public void AThinShell_KeepsItsFrontOffItsBack()
        {
            var (off, on) = Compare(NormalFixtures.RoundedShell(), 0.001f);
            Assert.That(off.Share45, Is.GreaterThan(0.05f), "the fixture reproduces the defect with the term off");
            Assert.That(on.Share45, Is.LessThan(0.001f));
            Assert.That(on.Share10, Is.LessThan(0.03f));
            // The outline bound already holds the open ends, so less of the drop is left for the term to remove.
            Assert.That(on.MaxDistance, Is.LessThan(off.MaxDistance), "the front drops no farther toward the back");
            Assert.That(on.MaxDistance, Is.LessThan(0.0013f), "and stays where the term has held it");
        }

        // A coin 10 mm across is all rim at a 1 mm tolerance: without the term its faces take the rim's tilted normals
        // wholesale. The term prices a small region below a large one showing the same tilt, so it keeps less here.
        [Test]
        public void ASmallBevelledCoin_KeepsItsShading()
        {
            var (off, on) = Compare(NormalFixtures.Coin(), 0.0005f);
            Assert.That(off.Share45, Is.GreaterThan(0.05f), "the fixture reproduces the defect with the term off");
            Assert.That(on.Share45, Is.LessThan(0.005f));
            Assert.That(on.Share10, Is.LessThan(0.15f));
            Assert.That(on.RmsDegrees, Is.LessThan(off.RmsDegrees * 0.35f));
        }

        // A sphere's normals are a linear function of position, and its shading holds under decimation without the term;
        // the term must leave it about as reducible as before.
        [Test]
        public void ASmoothSphere_StaysAsReducible([Values(0.05f, 0.02f)] float radius)
        {
            var b = NormalFixtures.SmoothSphere(radius);
            var (off, offTris) = AtTolerance(b, Off, radius * 0.08f);
            var (on, onTris) = AtTolerance(b, null, radius * 0.08f);
            TestContext.WriteLine($"r {radius * 1000} mm, {b.Triangles.Count} tris: off {offTris}, {off}; on {onTris}, {on}");
            Assert.That(off.Share10, Is.LessThan(0.001f), "nothing to mis-shade");
            Assert.That(onTris, Is.LessThanOrEqualTo(offTris * 1.05f));
        }

        // Decimation without the term never reads the normals the mesh carries: at weight 0 they cannot change one
        // collapse, whatever they are and whether the mesh carries any.
        [Test]
        public void AtWeightZero_TheMeshsNormalsChangeNothing()
        {
            foreach (var (name, b) in new[]
                     {
                         ("band", NormalFixtures.TiltedBand()), ("coin", NormalFixtures.Coin()), ("shell", NormalFixtures.RoundedShell()),
                         ("framed sphere", DecimationTests.FramedSphere()), ("seamed cylinder", Fixtures.Cylinder(24, 20, 0.05f, 0.4f, true, out _)),
                     })
            {
                using var authored = Record(b, NormalsAs.Authored);
                using var scrambled = Record(b, NormalsAs.Scrambled);
                using var none = Record(b, NormalsAs.None);
                Assert.That(authored.Length, Is.GreaterThan(10), $"{name}: a sequence to compare");
                AssertSameSequence(authored, scrambled, $"{name}, scrambled normals");
                AssertSameSequence(authored, none, $"{name}, no normals");
            }
        }

        // A normal with a non-finite component shades nothing a viewer can read, and must not switch the term off around it:
        // in a quadric it evaluates to a non-finite error, which a clamp at zero would read as free, and it passes to every
        // vertex that absorbs it. One such vertex leaves the coin no less kept than it is intact.
        [Test]
        public void ANonFiniteNormal_DoesNotSwitchTheTermOffAroundIt([Values(float.NaN, float.PositiveInfinity)] float bad)
        {
            var b = NormalFixtures.Coin();
            var clean = TrianglesAtTolerance(b, -1, bad);
            foreach (var vertex in new[] { 0, b.VertexCount / 2 })
            {
                var broken = TrianglesAtTolerance(b, vertex, bad);
                TestContext.WriteLine($"{clean} triangles intact, {broken} with a {bad} normal at vertex {vertex}");
                Assert.That(broken, Is.GreaterThanOrEqualTo(clean), $"a {bad} normal at vertex {vertex}");
            }
        }

        static int TrianglesAtTolerance(MeshBuilder b, int vertex, float value)
        {
            var mesh = b.Build();
            try
            {
                if (vertex >= 0) mesh.Normals[vertex] = new float3(0f, value, 1f);
                using var seq = Decimator.Run(mesh, DecimationSettings.Default, default, Allocator.Persistent);
                using var cut = seq.Cut(Fixtures.CutAtError(seq, NormalFixtures.Tolerance), Allocator.Persistent);
                return cut.Triangles.Length;
            }
            finally
            {
                mesh.Dispose();
            }
        }

        [Test]
        public void ANegativeOrNonFiniteWeight_OrNormalsThatDoNotMatchThePositions_AreRefused()
        {
            var b = NormalFixtures.Coin();
            Assert.Throws<ArgumentException>(() => Fixtures.Decimate(b, s => { s.Normal.Weight = -1f; return s; }).Dispose());
            Assert.Throws<ArgumentException>(() => Fixtures.Decimate(b, s => { s.Normal.Weight = float.NaN; return s; }).Dispose());
            Assert.Throws<ArgumentException>(() => Fixtures.Decimate(b, s => { s.Normal.Weight = float.PositiveInfinity; return s; }).Dispose());
            var mesh = b.Build();
            try
            {
                mesh.Normals.Dispose();
                mesh.Normals = new NativeArray<float3>(3, Allocator.Persistent);
                var m = mesh;
                Assert.Throws<ArgumentException>(() => Decimator.Run(m, DecimationSettings.Default, default, Allocator.Persistent).Dispose());
            }
            finally
            {
                mesh.Dispose();
            }
        }

        enum NormalsAs
        {
            Authored,
            Scrambled,
            None,
        }

        static CollapseSequence Record(MeshBuilder b, NormalsAs normals)
        {
            var mesh = b.Build();
            try
            {
                if (normals == NormalsAs.None)
                {
                    mesh.Normals.Dispose();
                    mesh.Normals = default;
                }
                else if (normals == NormalsAs.Scrambled)
                {
                    var random = new Unity.Mathematics.Random(7);
                    for (var v = 0; v < mesh.Normals.Length; v++) mesh.Normals[v] = random.NextFloat3Direction();
                }
                return Decimator.Run(mesh, Off(DecimationSettings.Default), default, Allocator.Persistent);
            }
            finally
            {
                mesh.Dispose();
            }
        }

        static void AssertSameSequence(CollapseSequence expected, CollapseSequence actual, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.That(BitConverter.SingleToInt32Bits(actual.Steps[i].Error), Is.EqualTo(BitConverter.SingleToInt32Bits(expected.Steps[i].Error)), $"{what}: step {i}'s error");
                Assert.That(actual.Steps[i].TriangleCount, Is.EqualTo(expected.Steps[i].TriangleCount), $"{what}: step {i}'s triangles");
            }
            Assert.That(actual.Records.ToArray(), Is.EqualTo(expected.Records.ToArray()), what);
        }
    }
}
