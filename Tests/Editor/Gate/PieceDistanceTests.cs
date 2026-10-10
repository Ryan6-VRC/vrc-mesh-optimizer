using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Gate;
using Ryan6Vrc.MeshOptimizer.Tests.Decimation;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Gate
{
    public class PieceDistanceTests
    {
        static PieceDistanceResult Measure(MeshBuilder b, int3[] written, float level)
        {
            using var positions = new NativeArray<float3>(b.Positions.ToArray(), Allocator.TempJob);
            using var source = new NativeArray<int3>(b.Triangles.ToArray(), Allocator.TempJob);
            using var cut = new NativeArray<int3>(written, Allocator.TempJob);
            return PieceDistance.Measure(positions, source, cut, level);
        }

        /// <summary>A rim: a strip 1 mm wide bent through a quarter circle of 5 mm radius, and the two triangles a cut to its ends leaves.</summary>
        static MeshBuilder Rim(out int3[] chord, out float sagitta)
        {
            const float radius = 0.005f;
            var b = Fixtures.Strip(12, radius, math.PI / 2f, 0.001f, out var left, out var right);
            chord = new[] { new int3(left[0], left[12], right[12]), new int3(left[0], right[12], right[0]) };
            sagitta = radius * (1f - math.cos(math.PI / 4f));
            return b;
        }

        [Test]
        public void AMeshWrittenAsItsSource_ReadsNothing()
        {
            var b = OutlineTests.Plate(out _, out _, out _);
            var read = Measure(b, b.Triangles.ToArray(), 1e-6f);
            Assert.That(read.Max, Is.LessThan(1e-7f));
            Assert.That(read.Over, Is.Zero);
        }

        [Test]
        public void ARimCutToAChord_ReadsHowFarTheChordStandsOffTheRim()
        {
            var b = Rim(out var chord, out var sagitta);
            var read = Measure(b, chord, sagitta / 2f);
            TestContext.WriteLine($"sagitta {sagitta * 1000:F3} mm, read {read.Max * 1000:F3} mm");
            Assert.That(read.Max, Is.EqualTo(sagitta).Within(sagitta * 0.02f));
            Assert.That(read.Over, Is.EqualTo(2));
            Assert.That(Measure(b, chord, sagitta * 1.01f).Over, Is.Zero, "nothing past a level above the reading");
        }

        // The case the measure exists for: another piece lies exactly where the cut lands, as a face lies under a rim
        // stretched across it. Read against the nearest surface of any piece the chord would stand on that face.
        [Test]
        public void AnotherPieceUnderTheCut_DoesNotHideIt()
        {
            var b = Rim(out var chord, out var sagitta);
            var corners = chord.SelectMany(t => new[] { t.x, t.y, t.z }).Distinct().ToArray();
            var copy = corners.ToDictionary(v => v, v => b.Add(b.Positions[v]));
            foreach (var t in chord) b.Tri(copy[t.x], copy[t.y], copy[t.z]);
            var read = Measure(b, chord, sagitta / 2f);
            Assert.That(read.Max, Is.EqualTo(sagitta).Within(sagitta * 0.02f));
            Assert.That(read.Over, Is.EqualTo(2));
        }
    }
}
