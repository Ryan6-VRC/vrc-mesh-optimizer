using System;
using System.Collections.Generic;
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
            // One common level: neither mesh's next step would have been as cheap as the level reached.
            for (var i = 0; i < 2; i++)
            {
                var seq = i == 0 ? _flat : _curved;
                if (r.Cuts[i] < seq.Length) Assert.That(seq.Steps[r.Cuts[i]].Error, Is.GreaterThan(r.Level), i == 0 ? "sheet" : "sphere");
            }
        }

        [Test]
        public void StepsThatTieTheLevel_AreAllTaken_WhateverTheEntryOrder()
        {
            // Two identical renderers: one common level cuts both alike, not whichever came first.
            var r = BudgetAllocator.ForBudget(new[] { AllocationEntry.From(_flat), AllocationEntry.From(_flat) }, 2 * _flat.InitialTriangleCount - 2);
            Assert.That(r.Cuts[0], Is.EqualTo(r.Cuts[1]));
            for (var i = 0; i < 2; i++)
                if (r.Cuts[i] < _flat.Length) Assert.That(_flat.Steps[r.Cuts[i]].Error, Is.GreaterThan(r.Level));
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

        // The sheet's errors count three times over, so a ceiling read on the raw error would cut it deeper than one read
        // on the weighted key the merge orders by.
        AllocationEntry[] Weighted => new[] { AllocationEntry.From(_flat, weight: 3f), AllocationEntry.From(_curved) };

        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        [Test]
        public void ACeilingBelowTheLevelABudgetNeeds_StopsAtIt_AsATolerance_AndSaysItHeld()
        {
            var free = BudgetAllocator.ForBudget(Weighted, Total / 4);
            Assert.That(free.TargetMet, Is.True);
            Assert.That(free.Level, Is.GreaterThan(0f));
            var ceiling = free.Level / 4;

            var r = BudgetAllocator.ForBudget(Weighted, Total / 4, ceiling);

            Assert.That(r.Level, Is.LessThanOrEqualTo(ceiling));
            Assert.That(r.TargetMet, Is.False);
            Assert.That(r.CeilingHeld, Is.True);
            Assert.That(r.TotalTriangles, Is.GreaterThan(Total / 4));
            var tolerance = BudgetAllocator.ForTolerance(Weighted, ceiling);
            Assert.That(r.Cuts, Is.EqualTo(tolerance.Cuts), "every step whose weighted error is at or below the ceiling, and none above");
            Assert.That(Bits(r.Level), Is.EqualTo(Bits(tolerance.Level)));
            for (var i = 0; i < 2; i++)
            {
                var e = Weighted[i];
                if (r.Cuts[i] < e.Steps.Length) Assert.That(e.Steps[r.Cuts[i]].Error * e.Weight, Is.GreaterThan(ceiling), i == 0 ? "sheet" : "sphere");
            }
        }

        [TestCase(1f)]
        [TestCase(2f)]
        public void ACeilingAtOrAboveTheLevelABudgetNeeds_ChangesNothing(float times)
        {
            var free = BudgetAllocator.ForBudget(Weighted, Total / 4);

            var r = BudgetAllocator.ForBudget(Weighted, Total / 4, free.Level * times);

            Assert.That(r.Cuts, Is.EqualTo(free.Cuts));
            Assert.That(Bits(r.Level), Is.EqualTo(Bits(free.Level)));
            Assert.That((r.TotalTriangles, r.TargetMet, r.CeilingHeld), Is.EqualTo((free.TotalTriangles, true, false)));
        }

        [Test]
        public void ABudgetTheFloorsStopShort_UnderACeilingNotReached_IsNotHeldByTheCeiling()
        {
            var floors = new[] { AllocationEntry.From(_flat, _flat.InitialTriangleCount / 2), AllocationEntry.From(_curved, _curved.InitialTriangleCount / 2) };
            var free = BudgetAllocator.ForBudget(floors, 10);
            Assert.That(free.TargetMet, Is.False);
            Assert.That(free.Level, Is.GreaterThan(0f));

            var r = BudgetAllocator.ForBudget(floors, 10, free.Level * 2);

            Assert.That(r.Cuts, Is.EqualTo(free.Cuts));
            Assert.That((r.TargetMet, r.CeilingHeld), Is.EqualTo((false, false)), "the floors stopped it, not the ceiling");
        }

        [TestCase(-1e-3f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void ANegativeOrNonFiniteCeiling_IsRefused_ByTheAllocator(float ceiling)
        {
            Assert.Throws<ArgumentException>(() => BudgetAllocator.ForBudget(Weighted, Total / 2, ceiling));
        }

        // With no ceiling the merge is the one before the ceiling existed, held here as the oracle: over a sweep of budgets,
        // with floors, weights and entries that tie, every cut, the level to the bit, the total and the verdict are its.
        [Test]
        public void TheDefaultCeiling_ReproducesTheAllocationWithout_StepForStep()
        {
            var sets = new[]
            {
                new[] { AllocationEntry.From(_flat), AllocationEntry.From(_curved) },
                Weighted,
                new[] { AllocationEntry.From(_flat, _flat.InitialTriangleCount * 3 / 4), AllocationEntry.From(_curved, _curved.InitialTriangleCount / 3, 0.5f) },
                new[] { AllocationEntry.From(_flat), AllocationEntry.From(_flat), AllocationEntry.From(_curved) },
            };
            var compared = 0;
            foreach (var entries in sets)
            {
                var total = 0;
                foreach (var e in entries) total += e.InitialTriangleCount;
                for (var budget = -1; budget <= total + 1; budget += Math.Max(1, total / 97))
                {
                    var oracle = WithoutACeiling(entries, budget);
                    foreach (var r in new[] { BudgetAllocator.ForBudget(entries, budget), BudgetAllocator.ForBudget(entries, budget, 0f) })
                    {
                        Assert.That(r.Cuts, Is.EqualTo(oracle.Cuts), $"budget {budget}");
                        Assert.That((Bits(r.Level), r.TotalTriangles, r.TargetMet, r.CeilingHeld),
                            Is.EqualTo((Bits(oracle.Level), oracle.TotalTriangles, oracle.TargetMet, false)), $"budget {budget}");
                    }
                    compared++;
                }
            }
            Assert.That(compared, Is.GreaterThan(300));
        }

        static AllocationResult WithoutACeiling(IReadOnlyList<AllocationEntry> entries, int triangleBudget)
        {
            var n = entries.Count;
            var cuts = new int[n];
            var limit = new int[n];
            var total = 0;
            for (var i = 0; i < n; i++)
            {
                limit[i] = entries[i].DeepestCut();
                total += entries[i].InitialTriangleCount;
            }
            float Key(AllocationEntry e, int step) => e.Steps[step].Error * e.Weight;
            var level = 0f;
            var queue = new SortedSet<(float key, int entry)>();
            for (var i = 0; i < n; i++)
                if (limit[i] > 0) queue.Add((Key(entries[i], 0), i));
            while (queue.Count > 0 && (total > triangleBudget || queue.Min.key <= level))
            {
                var (key, i) = queue.Min;
                queue.Remove(queue.Min);
                var e = entries[i];
                total += e.CountAt(cuts[i] + 1) - e.CountAt(cuts[i]);
                cuts[i]++;
                level = Math.Max(level, key);
                if (cuts[i] < limit[i]) queue.Add((Key(e, cuts[i]), i));
            }
            return new AllocationResult { Cuts = cuts, Level = level, TotalTriangles = total, TargetMet = total <= triangleBudget };
        }
    }
}
