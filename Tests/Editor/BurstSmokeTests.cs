using System.Linq;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests
{
    // The core is Burst jobs over native containers. This proves the venue running the suite can
    // schedule one and read its result, so a later failure is the algorithm's and not the venue's.
    public class BurstSmokeTests
    {
        [BurstCompile]
        struct SumOfSquaresJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Points;
            public NativeReference<float> Result;

            public void Execute()
            {
                var sum = 0f;
                for (var i = 0; i < Points.Length; i++) sum += math.lengthsq(Points[i]);
                Result.Value = sum;
            }
        }

        [Test]
        public void BurstJob_RunsAndReturnsItsResult()
        {
            using var points = new NativeArray<float3>(new[] { new float3(1, 2, 2), new float3(0, 3, 4) }, Allocator.TempJob);
            using var result = new NativeReference<float>(Allocator.TempJob);

            new SumOfSquaresJob { Points = points, Result = result }.Schedule().Complete();

            Assert.That(result.Value, Is.EqualTo(34f).Within(1e-6f));
        }

        // In the Editor, Burst runs a job compiled asynchronously as managed code until its compilation finishes, so a
        // build after a cold Burst cache would run the whole collapse loop under Mono, many times slower and rounded
        // differently from every later build. Every job in the core must wait for its compiled code.
        [Test]
        public void EveryCoreBurstJob_CompilesSynchronously()
        {
            var jobs = typeof(Ryan6Vrc.MeshOptimizer.Core.Decimation.Decimator).Assembly.GetTypes()
                .Select(t => (type: t, burst: t.GetCustomAttributes(typeof(BurstCompileAttribute), false).Cast<BurstCompileAttribute>().FirstOrDefault()))
                .Where(x => x.burst != null)
                .ToList();

            Assert.That(jobs, Is.Not.Empty);
            Assert.That(jobs.Where(x => !x.burst.CompileSynchronously).Select(x => x.type.FullName), Is.Empty);
        }
    }
}
