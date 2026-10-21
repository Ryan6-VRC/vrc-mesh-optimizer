using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>One collapse: a vertex, or a seam or border pair moving together, removed onto its target.</summary>
    internal struct CollapseStep
    {
        /// <summary>The error level reached, in metres: the running maximum of every collapse's error so far,
        /// so it never decreases along the sequence.</summary>
        public float Error;
        /// <summary>Live triangles after this step.</summary>
        public int TriangleCount;
        /// <summary>This step's (removed, target) pairs are Records[RecordStart .. + RecordCount): one, or two
        /// for a pair.</summary>
        public int RecordStart;
        public int RecordCount;
    }

    /// <summary>
    /// A mesh's whole decimation, recorded once: cut it at any step count to get a mesh at that error level.
    /// Vertex indices in <see cref="Records"/> are working vertices (see <see cref="Topology.MeshTopology"/>);
    /// <see cref="Cut"/> translates to source indices.
    /// </summary>
    internal struct CollapseSequence : IDisposable
    {
        public NativeArray<CollapseStep> Steps;
        /// <summary>(removed, target) working-vertex pairs, grouped by step.</summary>
        public NativeArray<int2> Records;
        /// <summary>Working vertex to source vertex.</summary>
        public NativeArray<int> WorkingOrigin;
        /// <summary>Per source triangle over working vertices; int3(-1) for a triangle dropped as degenerate input or excluded.</summary>
        public NativeArray<int3> WorkingTriangles;
        public int InitialTriangleCount;

        public int Length => Steps.Length;

        /// <summary>Triangles after the first <paramref name="stepCount"/> steps.</summary>
        public int TriangleCountAt(int stepCount) => stepCount == 0 ? InitialTriangleCount : Steps[stepCount - 1].TriangleCount;

        /// <summary>Error level after the first <paramref name="stepCount"/> steps, in metres.</summary>
        public float ErrorAt(int stepCount) => stepCount == 0 ? 0f : Steps[stepCount - 1].Error;

        /// <summary>The mesh after the first <paramref name="stepCount"/> steps, in source indices; the caller owns the result.</summary>
        public CutResult Cut(int stepCount, Allocator allocator)
        {
            if (stepCount < 0 || stepCount > Length) throw new ArgumentOutOfRangeException(nameof(stepCount), stepCount, $"a cut lies in 0..{Length}");
            var result = new CutResult();
            try
            {
                result.Triangles = new NativeList<int3>(TriangleCountAt(stepCount), allocator);
                result.SourceTriangle = new NativeList<int>(TriangleCountAt(stepCount), allocator);
                RunCut(stepCount, result);
            }
            catch
            {
                result.Dispose();
                throw;
            }
            return result;
        }

        void RunCut(int stepCount, CutResult result)
        {
            new CutJob
            {
                Steps = Steps,
                Records = Records,
                WorkingOrigin = WorkingOrigin,
                WorkingTriangles = WorkingTriangles,
                StepCount = stepCount,
                Triangles = result.Triangles,
                SourceTriangle = result.SourceTriangle,
            }.Run();
        }

        public void Dispose()
        {
            if (Steps.IsCreated) Steps.Dispose();
            if (Records.IsCreated) Records.Dispose();
            if (WorkingOrigin.IsCreated) WorkingOrigin.Dispose();
            if (WorkingTriangles.IsCreated) WorkingTriangles.Dispose();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct CutJob : IJob
        {
            [ReadOnly] public NativeArray<CollapseStep> Steps;
            [ReadOnly] public NativeArray<int2> Records;
            [ReadOnly] public NativeArray<int> WorkingOrigin;
            [ReadOnly] public NativeArray<int3> WorkingTriangles;
            public int StepCount;
            public NativeList<int3> Triangles;
            public NativeList<int> SourceTriangle;

            public void Execute()
            {
                var parent = new NativeArray<int>(WorkingOrigin.Length, Allocator.Temp);
                for (var i = 0; i < parent.Length; i++) parent[i] = i;
                var records = StepCount == 0 ? 0 : Steps[StepCount - 1].RecordStart + Steps[StepCount - 1].RecordCount;
                for (var i = 0; i < records; i++) parent[Records[i].x] = Records[i].y;

                for (var t = 0; t < WorkingTriangles.Length; t++)
                {
                    var tri = WorkingTriangles[t];
                    if (tri.x < 0) continue;
                    var r = new int3(Find(ref parent, tri.x), Find(ref parent, tri.y), Find(ref parent, tri.z));
                    // A collapse kills exactly the triangles holding both its ends, which is exactly the
                    // triangles whose resolved corners coincide.
                    if (r.x == r.y || r.y == r.z || r.x == r.z) continue;
                    var s = new int3(WorkingOrigin[r.x], WorkingOrigin[r.y], WorkingOrigin[r.z]);
                    Triangles.Add(s);
                    SourceTriangle.Add(t);
                }
            }

            static int Find(ref NativeArray<int> parent, int v)
            {
                var root = v;
                while (parent[root] != root) root = parent[root];
                while (parent[v] != root)
                {
                    var next = parent[v];
                    parent[v] = root;
                    v = next;
                }
                return root;
            }
        }
    }

    /// <summary>A cut through a collapse sequence, in source indices.</summary>
    internal struct CutResult : IDisposable
    {
        /// <summary>Surviving triangles, each corner naming the source vertex it now uses.</summary>
        public NativeList<int3> Triangles;
        /// <summary>Per surviving triangle, the source triangle it came from (its submesh is that one's).</summary>
        public NativeList<int> SourceTriangle;

        public void Dispose()
        {
            if (Triangles.IsCreated) Triangles.Dispose();
            if (SourceTriangle.IsCreated) SourceTriangle.Dispose();
        }
    }
}
