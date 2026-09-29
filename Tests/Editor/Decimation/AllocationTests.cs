using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Allocation;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    public class AllocationTests
    {
        CollapseSequence _flat;
        CollapseSequence _curved;

        [OneTimeSetUp]
        public void Record()
        {
            // A gently waved sheet and a sphere of similar triangle counts: the sheet has less to lose.
            var sheet = Fixtures.Grid(20, 20, 0.2f, 0.2f, out _);
            for (var v = 0; v < sheet.VertexCount; v++)
            {
                var p = sheet.Positions[v];
                sheet.Positions[v] = new float3(p.x, p.y, 0.002f * math.sin(p.x * 20f));
            }
            _flat = Fixtures.Decimate(sheet);
            _curved = Fixtures.Decimate(Fixtures.Icosphere(3, 0.1f));
        }

        [OneTimeTearDown]
        public void Release()
        {
            _flat.Dispose();
            _curved.Dispose();
        }

        int Total => _flat.InitialTriangleCount + _curved.InitialTriangleCount;

        [Test]
        public void UnderASharedBudget_TheFlatterMeshGivesUpMore()
        {
            var r = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat), AllocationEntry.From(_curved) }, Total / 2);
            Assert.That(r.TargetMet);
            Assert.That(r.TotalTriangles, Is.LessThanOrEqualTo(Total / 2));
            var flatKept = _flat.TriangleCountAt(r.Cuts[0]) / (float)_flat.InitialTriangleCount;
            var curvedKept = _curved.TriangleCountAt(r.Cuts[1]) / (float)_curved.InitialTriangleCount;
            TestContext.WriteLine($"level {r.Level * 1000:F3} mm: sheet keeps {flatKept:P0}, sphere keeps {curvedKept:P0}");
            Assert.That(flatKept, Is.LessThan(curvedKept));
            // One common level: neither mesh's next step would have been cheaper than the level reached.
            Assert.That(_flat.ErrorAt(r.Cuts[0]), Is.LessThanOrEqualTo(r.Level));
            Assert.That(_curved.ErrorAt(r.Cuts[1]), Is.LessThanOrEqualTo(r.Level));
        }

        [Test]
        public void AFloor_IsHonoured_AndTheOtherMeshMakesUpTheDifference()
        {
            var free = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat), AllocationEntry.From(_curved) }, Total / 2);
            var floor = _flat.InitialTriangleCount * 3 / 4;
            var r = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat, floor), AllocationEntry.From(_curved) }, Total / 2);
            Assert.That(_flat.TriangleCountAt(r.Cuts[0]), Is.GreaterThanOrEqualTo(floor));
            Assert.That(r.Cuts[1], Is.GreaterThan(free.Cuts[1]), "the sphere is cut deeper to cover the floor");
        }

        [Test]
        public void AnUnreachableBudget_SaysSo()
        {
            var floorFlat = _flat.InitialTriangleCount;
            var floorCurved = _curved.InitialTriangleCount;
            var r = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat, floorFlat), AllocationEntry.From(_curved, floorCurved) }, 10);
            Assert.That(r.TargetMet, Is.False);
            Assert.That(r.Cuts, Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void AWeight_ShiftsTrianglesTowardTheWeightedMesh()
        {
            var even = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat), AllocationEntry.From(_curved) }, Total / 2);
            // Weighting the sheet makes its error count for more, so it keeps triangles the sphere then gives up.
            var favoured = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat, weight: 1000f), AllocationEntry.From(_curved) }, Total / 2);
            TestContext.WriteLine($"sheet keeps {_flat.TriangleCountAt(even.Cuts[0])} tris at weight 1, {_flat.TriangleCountAt(favoured.Cuts[0])} at weight 1000");
            Assert.That(_flat.TriangleCountAt(favoured.Cuts[0]), Is.GreaterThan(_flat.TriangleCountAt(even.Cuts[0])));
            Assert.That(_curved.TriangleCountAt(favoured.Cuts[1]), Is.LessThan(_curved.TriangleCountAt(even.Cuts[1])));
        }

        [Test]
        public void ATolerance_CutsEachMeshToTheSameErrorLevel()
        {
            const float tolerance = 2e-4f;
            var r = BudgetAllocator.ForTolerance(new[] { AllocationEntry.From(_flat), AllocationEntry.From(_curved) }, tolerance);
            for (var i = 0; i < 2; i++)
            {
                var seq = i == 0 ? _flat : _curved;
                Assert.That(seq.ErrorAt(r.Cuts[i]), Is.LessThanOrEqualTo(tolerance));
                if (r.Cuts[i] < seq.Length) Assert.That(seq.Steps[r.Cuts[i]].Error, Is.GreaterThan(tolerance), "cut as deep as the tolerance allows");
            }
            Assert.That(_flat.TriangleCountAt(r.Cuts[0]) / (float)_flat.InitialTriangleCount,
                Is.LessThan(_curved.TriangleCountAt(r.Cuts[1]) / (float)_curved.InitialTriangleCount));
        }
    }
}
