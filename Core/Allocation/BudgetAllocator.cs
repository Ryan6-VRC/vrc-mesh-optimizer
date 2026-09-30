using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Unity.Collections;

namespace Ryan6Vrc.MeshOptimizer.Core.Allocation
{
    /// <summary>One renderer's recorded decimation, and how far it may be cut.</summary>
    public struct AllocationEntry
    {
        public NativeArray<CollapseStep> Steps;
        public int InitialTriangleCount;
        /// <summary>The fewest triangles this renderer may be cut to.</summary>
        public int FloorTriangles;
        /// <summary>Importance: this renderer's errors count this many times over. 1 is neutral; must be above 0.</summary>
        public float Weight;

        public static AllocationEntry From(in CollapseSequence sequence, int floorTriangles = 0, float weight = 1f) => new AllocationEntry
        {
            Steps = sequence.Steps,
            InitialTriangleCount = sequence.InitialTriangleCount,
            FloorTriangles = floorTriangles,
            Weight = weight,
        };

        internal int CountAt(int cut) => cut == 0 ? InitialTriangleCount : Steps[cut - 1].TriangleCount;

        /// <summary>The deepest cut that keeps at least the floor.</summary>
        internal int DeepestCut()
        {
            var cut = 0;
            while (cut < Steps.Length && Steps[cut].TriangleCount >= FloorTriangles) cut++;
            return cut;
        }
    }

    public struct AllocationResult
    {
        /// <summary>Per entry, the step count to cut its sequence at.</summary>
        public int[] Cuts;
        /// <summary>The weighted error level every cut reaches, in metres.</summary>
        public float Level;
        public int TotalTriangles;
        /// <summary>False when floors, locks, the recorded sequences or the error ceiling stop short of the target; the
        /// cuts are then as deep as those allow.</summary>
        public bool TargetMet;
        /// <summary>True when the error ceiling stopped a budget short: steps were left that would have cut deeper, each
        /// above the ceiling. False when the target was met, or when floors, locks or the sequences ran out first.</summary>
        public bool CeilingHeld;
    }

    /// <summary>
    /// Cuts several renderers' collapse sequences at one common error level, so triangles go where they buy
    /// the most error: a flat lining gives up more than a curved face. Each renderer's error is multiplied by
    /// its weight before comparison, and no renderer is cut below its floor.
    /// </summary>
    public static class BudgetAllocator
    {
        /// <summary>
        /// The lowest common weighted error level whose cuts total at most <paramref name="triangleBudget"/>. Above 0,
        /// <paramref name="errorCeiling"/> (metres) is the highest weighted level it may reach: no step whose weighted
        /// error exceeds it is taken, so a budget reachable only above it is cut as a tolerance of the ceiling would cut
        /// it, and reported unmet (<see cref="AllocationResult.CeilingHeld"/>). 0 is no ceiling.
        /// </summary>
        public static AllocationResult ForBudget(IReadOnlyList<AllocationEntry> entries, int triangleBudget, float errorCeiling = 0f)
        {
            if (!(errorCeiling >= 0f) || float.IsPositiveInfinity(errorCeiling))
                throw new ArgumentException($"budget error ceiling {errorCeiling} m must be finite and 0 or more; 0 turns it off");
            var n = entries.Count;
            var cuts = new int[n];
            var limit = new int[n];
            var total = 0;
            for (var i = 0; i < n; i++)
            {
                Validate(entries[i], i);
                limit[i] = entries[i].DeepestCut();
                total += entries[i].InitialTriangleCount;
            }
            var level = 0f;
            // Take steps across renderers in order of weighted error until the total fits. Each sequence's
            // error is non-decreasing, so this merge takes exactly the steps at or below the final level. A
            // common level means every step at or below it, so it goes on to take the steps that tie with the
            // level reached (many at zero, on flat meshes) rather than leaving them to entry order; a step never
            // adds triangles, so once the total fits it stays fitted. A ceiling ends the merge at the first step above
            // it: every step left is at least that step's error, so every step at or below the ceiling is taken.
            var queue = new SortedSet<(float key, int entry)>();
            for (var i = 0; i < n; i++)
                if (limit[i] > 0) queue.Add((Key(entries[i], 0), i));
            var ceilinged = errorCeiling > 0f;
            while (queue.Count > 0 && (!ceilinged || queue.Min.key <= errorCeiling) && (total > triangleBudget || queue.Min.key <= level))
            {
                var (key, i) = queue.Min;
                queue.Remove(queue.Min);
                var e = entries[i];
                total += e.CountAt(cuts[i] + 1) - e.CountAt(cuts[i]);
                cuts[i]++;
                level = Math.Max(level, key);
                if (cuts[i] < limit[i]) queue.Add((Key(e, cuts[i]), i));
            }
            return new AllocationResult
            {
                Cuts = cuts, Level = level, TotalTriangles = total, TargetMet = total <= triangleBudget,
                // Without a ceiling the merge ends over the budget only once the queue is empty.
                CeilingHeld = total > triangleBudget && queue.Count > 0,
            };
        }

        /// <summary>Every renderer cut as deep as its weighted error stays within <paramref name="tolerance"/> metres.</summary>
        public static AllocationResult ForTolerance(IReadOnlyList<AllocationEntry> entries, float tolerance)
        {
            var n = entries.Count;
            var cuts = new int[n];
            var total = 0;
            var level = 0f;
            for (var i = 0; i < n; i++)
            {
                var e = entries[i];
                Validate(e, i);
                var limit = e.DeepestCut();
                var cut = 0;
                while (cut < limit && Key(e, cut) <= tolerance) cut++;
                cuts[i] = cut;
                total += e.CountAt(cut);
                if (cut > 0) level = Math.Max(level, Key(e, cut - 1));
            }
            return new AllocationResult { Cuts = cuts, Level = level, TotalTriangles = total, TargetMet = true };
        }

        static float Key(in AllocationEntry e, int step) => e.Steps[step].Error * e.Weight;

        static void Validate(in AllocationEntry e, int index)
        {
            if (!e.Steps.IsCreated) throw new ArgumentException($"entry {index} has no recorded steps");
            if (!(e.Weight > 0f)) throw new ArgumentException($"entry {index} has weight {e.Weight}; a weight must be above 0");
        }
    }
}
