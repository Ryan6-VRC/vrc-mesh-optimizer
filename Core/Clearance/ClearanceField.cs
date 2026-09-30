using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Clearance
{
    /// <summary>What decimation needs to keep one mesh clear of the other layers.</summary>
    internal struct ClearanceInput
    {
        /// <summary>Every layer's kept surface, this mesh's included.</summary>
        public ClearanceSurface Surface;
        /// <summary>Per triangle of the mesh being decimated, its layer in <see cref="Surface"/>; a triangle decimation drops is never read.</summary>
        public NativeArray<int> TriangleLayer;

        public bool IsActive => Surface.IsCreated && Surface.TriangleCount > 0 && TriangleLayer.IsCreated;
    }

    /// <summary>What the clearance rule saw and did on one mesh.</summary>
    internal struct ClearanceStats
    {
        /// <summary>Vertices with another layer within the reach: the ones the rule can act on.</summary>
        public int NearVertices;
        /// <summary>Collapses that passed every other check and were refused for coming too near another layer.</summary>
        public long Refused;
        /// <summary>
        /// Of <see cref="Refused"/>, those refused only because a source triangle whose nearest layer is a third one was
        /// held to the pair's own gap: the rule reading only the gap to the nearest layer of any would have let them pass.
        /// </summary>
        public long RefusedByPairGap;
    }

    /// <summary>
    /// A mesh's clearance from the other layers before decimation, capped at the reach: per source vertex, and per
    /// triangle, where 0 means the triangle touches or crosses another layer.
    /// </summary>
    internal struct ClearanceField : IDisposable
    {
        public NativeArray<float> Vertex;
        public NativeArray<float> Triangle;
        /// <summary>Per source vertex, the least clearance of the live triangles around it: the gap its neighbourhood had.</summary>
        public NativeArray<float> Star;

        public void Dispose()
        {
            if (Vertex.IsCreated) Vertex.Dispose();
            if (Triangle.IsCreated) Triangle.Dispose();
            if (Star.IsCreated) Star.Dispose();
        }

        /// <summary>
        /// <paramref name="live"/> is per triangle of <paramref name="triangles"/>: false for one decimation drops as degenerate.
        /// <paramref name="nearVertices"/> counts the vertices of live triangles with another layer within the reach.
        /// </summary>
        public static ClearanceField Measure(NativeArray<float3> positions, NativeArray<int3> triangles, NativeArray<bool> live, in ClearanceInput input,
            float reach, Allocator allocator, out int nearVertices)
        {
            nearVertices = 0;
            if (input.TriangleLayer.Length != triangles.Length)
                throw new ArgumentException($"the mesh has {triangles.Length} triangles and {input.TriangleLayer.Length} layers; give one layer per triangle");
            var field = new ClearanceField();
            try
            {
                field.Vertex = new NativeArray<float>(positions.Length, allocator);
                field.Triangle = new NativeArray<float>(triangles.Length, allocator);
                using var vertexLayer = new NativeArray<int>(positions.Length, Allocator.Persistent);
                new VertexLayerJob { Triangles = triangles, Live = live, TriangleLayer = input.TriangleLayer, VertexLayer = vertexLayer }.Run();
                new VertexJob
                {
                    Surface = input.Surface, Positions = positions, VertexLayer = vertexLayer, Reach = reach, Clearance = field.Vertex,
                }.Schedule(positions.Length, 256).Complete();
                new TriangleJob
                {
                    Surface = input.Surface, Positions = positions, Triangles = triangles, Live = live, TriangleLayer = input.TriangleLayer,
                    VertexClearance = field.Vertex, Reach = reach, Clearance = field.Triangle,
                }.Schedule(triangles.Length, 128).Complete();
                field.Star = new NativeArray<float>(positions.Length, allocator);
                new StarJob { Triangles = triangles, Live = live, Triangle = field.Triangle, Reach = reach, Star = field.Star }.Run();
                for (var v = 0; v < positions.Length; v++)
                    if (vertexLayer[v] >= 0 && field.Vertex[v] < reach) nearVertices++;
                return field;
            }
            catch
            {
                field.Dispose();
                throw;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct StarJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<bool> Live;
            [ReadOnly] public NativeArray<float> Triangle;
            public float Reach;
            public NativeArray<float> Star;

            public void Execute()
            {
                for (var v = 0; v < Star.Length; v++) Star[v] = Reach;
                for (var t = 0; t < Triangles.Length; t++)
                {
                    if (!Live[t]) continue;
                    var tri = Triangles[t];
                    var c = Triangle[t];
                    Star[tri.x] = math.min(Star[tri.x], c);
                    Star[tri.y] = math.min(Star[tri.y], c);
                    Star[tri.z] = math.min(Star[tri.z], c);
                }
            }
        }

        /// <summary>Each vertex's layer, from any live triangle using it; -2 for a vertex no live triangle uses.</summary>
        [BurstCompile(CompileSynchronously = true)]
        struct VertexLayerJob : IJob
        {
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<bool> Live;
            [ReadOnly] public NativeArray<int> TriangleLayer;
            public NativeArray<int> VertexLayer;

            public void Execute()
            {
                for (var v = 0; v < VertexLayer.Length; v++) VertexLayer[v] = -2;
                for (var t = 0; t < Triangles.Length; t++)
                {
                    if (!Live[t]) continue;
                    var tri = Triangles[t];
                    VertexLayer[tri.x] = TriangleLayer[t];
                    VertexLayer[tri.y] = TriangleLayer[t];
                    VertexLayer[tri.z] = TriangleLayer[t];
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct VertexJob : IJobParallelFor
        {
            public ClearanceSurface Surface;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int> VertexLayer;
            public float Reach;
            public NativeArray<float> Clearance;

            public void Execute(int v)
            {
                var layer = VertexLayer[v];
                Clearance[v] = layer < 0 ? Reach : Surface.PointClearance(Positions[v], layer, Reach);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct TriangleJob : IJobParallelFor
        {
            public ClearanceSurface Surface;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<int3> Triangles;
            [ReadOnly] public NativeArray<bool> Live;
            [ReadOnly] public NativeArray<int> TriangleLayer;
            [ReadOnly] public NativeArray<float> VertexClearance;
            public float Reach;
            public NativeArray<float> Clearance;

            public void Execute(int t)
            {
                if (!Live[t])
                {
                    Clearance[t] = Reach;
                    return;
                }
                var tri = Triangles[t];
                float3 a = Positions[tri.x], b = Positions[tri.y], c = Positions[tri.z];
                float ca = VertexClearance[tri.x], cb = VertexClearance[tri.y], cc = VertexClearance[tri.z];
                if (ClearanceBound.Of(a, b, c, ca, cb, cc) >= Reach)
                {
                    Clearance[t] = Reach;
                    return;
                }
                // The triangle is no farther than its nearest corner, so that bounds the search.
                var cap = math.min(Reach, math.min(ca, math.min(cb, cc)));
                Clearance[t] = Surface.TriangleClearance(a, b, c, TriangleLayer[t], cap);
            }
        }
    }

    internal static class ClearanceBound
    {
        /// <summary>
        /// A lower bound on triangle abc's clearance from its corners' clearances: no point of the triangle is farther
        /// from a corner than that corner's longer edge, so none is nearer another layer than the corner's clearance
        /// less that edge.
        /// </summary>
        public static float Of(float3 a, float3 b, float3 c, float ca, float cb, float cc)
        {
            var ab = math.distance(a, b);
            var bc = math.distance(b, c);
            var ca2 = math.distance(c, a);
            return math.max(ca - math.max(ab, ca2), math.max(cb - math.max(ab, bc), cc - math.max(bc, ca2)));
        }
    }
}
