using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Liveness;
using Ryan6Vrc.MeshOptimizer.Writing;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace Ryan6Vrc.MeshOptimizer.Tests.Writing
{
    // A face can carry hundreds of shape keys; the writer runs once per renderer per build, so it has to stay
    // in seconds and in memory proportional to the mesh. A measurement: it reports rather than asserts a budget, and a
    // whole gate run leaves out every fixture named *Benchmark; run it by filter.
    public class MeshWriterBenchmark
    {
        const int Vertices = 30000;
        const int ShapeKeys = 900;
        const int MovedPerKey = 300;

        [Test, Explicit("measurement, run by name")]
        public void Writer_OnAFaceSizedMesh_With900ShapeKeys()
        {
            var random = new Random(1);
            var source = new Mesh { name = "face", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var built = Stopwatch.StartNew();
            source.vertices = Enumerable.Range(0, Vertices).Select(v => new Vector3(v % 173, v / 173, 0) * 0.001f).ToArray();
            source.normals = Enumerable.Repeat(Vector3.forward, Vertices).ToArray();
            source.uv = Enumerable.Range(0, Vertices).Select(v => new Vector2(v % 173 / 173f, v / 173 / 173f)).ToArray();
            source.triangles = Enumerable.Range(0, Vertices / 3 * 3).ToArray();
            var deltas = new Vector3[Vertices];
            var normals = new Vector3[Vertices];
            for (var k = 0; k < ShapeKeys; k++)
            {
                Array.Clear(deltas, 0, Vertices);
                Array.Clear(normals, 0, Vertices);
                var start = random.Next(Vertices - MovedPerKey);
                for (var i = 0; i < MovedPerKey; i++)
                {
                    deltas[start + i] = new Vector3(0, 0, (float)random.NextDouble() * 0.01f);
                    normals[start + i] = new Vector3((float)random.NextDouble() * 0.1f, 0, 0);
                }
                source.AddBlendShapeFrame($"key{k}", 100f, deltas, normals, null);
            }
            built.Stop();

            // Keep two triangles in three.
            var kept = Enumerable.Range(0, Vertices / 3).Where(t => t % 3 != 2).SelectMany(t => new[] { 3 * t, 3 * t + 1, 3 * t + 2 }).ToArray();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var managedBefore = GC.GetTotalMemory(true);
            var nativeBefore = Profiler.GetTotalAllocatedMemoryLong();
            var collections = GC.CollectionCount(0);

            var timer = Stopwatch.StartNew();
            var written = MeshWriter.Write(source, new[] { kept }, kept);
            timer.Stop();

            var managedAfter = GC.GetTotalMemory(false);
            var nativeAfter = Profiler.GetTotalAllocatedMemoryLong();
            var gen0 = GC.CollectionCount(0) - collections;

            // The extractor reads the same keys, every one live, for comparison.
            var extracted = Stopwatch.StartNew();
            var liveness = Enumerable.Repeat(ShapeKeyLiveness.Live(1f), ShapeKeys).ToArray();
            using (var eval = MeshExtractor.Extract(source, SkinningPose.Unskinned(Matrix4x4.identity, 0), liveness, Allocator.Persistent, out _))
            {
                extracted.Stop();
                Assert.That(eval.Frames.Length, Is.EqualTo(ShapeKeys));
            }

            var report = $"[MeshWriterTiming] source built in {built.ElapsedMilliseconds} ms; " +
                         $"write of {Vertices} vertices x {ShapeKeys} keys ({kept.Length} kept) took {timer.ElapsedMilliseconds} ms; " +
                         $"managed heap {(managedAfter - managedBefore) / 1048576.0:F1} MiB after, {gen0} gen-0 collections; " +
                         $"native allocated {(nativeAfter - nativeBefore) / 1048576.0:F1} MiB after; " +
                         $"extraction with every key live took {extracted.ElapsedMilliseconds} ms";
            UnityEngine.Debug.Log(report);
            TestContext.WriteLine(report);
            try
            {
                Assert.That(written.blendShapeCount, Is.EqualTo(ShapeKeys));
                Assert.That(written.vertexCount, Is.EqualTo(kept.Length));
            }
            finally
            {
                Object.DestroyImmediate(written);
                Object.DestroyImmediate(source);
            }
        }
    }
}
