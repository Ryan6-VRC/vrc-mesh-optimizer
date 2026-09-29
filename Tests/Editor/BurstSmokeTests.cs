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
    }
}
