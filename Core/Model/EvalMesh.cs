using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Model
{
    /// <summary>
    /// One skin influence. <see cref="Bone"/> is an avatar-wide id, one per Transform, so influences
    /// compare across renderers.
    /// </summary>
    public struct BoneInfluence
    {
        public int Bone;
        public float Weight;
    }

    /// <summary>A live shape key frame: a run of <see cref="EvalMesh.Deltas"/>.</summary>
    public struct ShapeFrame
    {
        public int DeltaStart;
        public int DeltaCount;
    }

    /// <summary>
    /// One vertex's displacement in one frame, in avatar-root space, already scaled to the largest
    /// weight the frame is ever driven to.
    /// </summary>
    public struct ShapeDelta
    {
        public int Vertex;
        public float3 Delta;
    }

    /// <summary>
    /// A renderer's geometry as the algorithms evaluate it: avatar-root space, metres, skinned at the
    /// pose the avatar stands in, with every frozen shape key applied. One space and one unit across
    /// renderers is what makes an error comparable between them and lets one occlude another.
    ///
    /// Indices are the source mesh's own vertex indices. The algorithms never create or move a vertex,
    /// so their whole output is stated over these indices and the source mesh is never rebuilt from
    /// this struct.
    ///
    /// A configuration is the rest state or one entry of <see cref="Frames"/>. Vertex v stands at
    /// Positions[v] in the rest state, and at Positions[v] plus the frame's delta for v, if it has
    /// one, in that frame's configuration.
    /// </summary>
    public struct EvalMesh : IDisposable
    {
        public NativeArray<float3> Positions;
        /// <summary>Unit length, avatar-root space.</summary>
        public NativeArray<float3> Normals;
        public NativeArray<float2> Uv0;

        public NativeArray<int3> Triangles;
        /// <summary>Submesh index per triangle. A material boundary is where this differs across an edge.</summary>
        public NativeArray<int> TriangleSubmesh;

        /// <summary>Length VertexCount + 1. Vertex v's influences are Weights[WeightStart[v] .. WeightStart[v + 1]).</summary>
        public NativeArray<int> WeightStart;
        /// <summary>Per vertex: weight descending, summing to 1. An unskinned renderer has one influence per vertex, its own Transform.</summary>
        public NativeArray<BoneInfluence> Weights;

        /// <summary>Live frames only. Frozen keys are already in Positions, and dead keys are not evaluated.</summary>
        public NativeArray<ShapeFrame> Frames;
        /// <summary>Grouped by frame, vertex ascending within a frame. A vertex a frame does not move has no entry.</summary>
        public NativeArray<ShapeDelta> Deltas;

        public int VertexCount => Positions.Length;
        public int TriangleCount => Triangles.Length;

        public void Dispose()
        {
            if (Positions.IsCreated) Positions.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (Uv0.IsCreated) Uv0.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (TriangleSubmesh.IsCreated) TriangleSubmesh.Dispose();
            if (WeightStart.IsCreated) WeightStart.Dispose();
            if (Weights.IsCreated) Weights.Dispose();
            if (Frames.IsCreated) Frames.Dispose();
            if (Deltas.IsCreated) Deltas.Dispose();
        }
    }
}
