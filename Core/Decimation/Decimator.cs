using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>What a collapse run cost, for measurement.</summary>
    internal struct CollapseStats
    {
        /// <summary>Largest the frame-quadric pool grew, in bytes.</summary>
        public long PeakPoolBytes;
        /// <summary>Times a cluster's every candidate failed validity when it reached the top of the queue.</summary>
        public long Blocked;
    }

    /// <summary>
    /// Records a mesh's half-edge collapse sequence. A collapse removes one vertex (or a seam or border pair
    /// together) onto a neighbour and moves nothing: every surviving vertex keeps its position, weights, UVs,
    /// normals and shape deltas exactly as authored, so the output is a subset of the source vertices.
    /// </summary>
    public static class Decimator
    {
        public static CollapseSequence Run(in EvalMesh mesh, in DecimationSettings settings, in VertexProtection protection, Allocator allocator)
        {
            using var topology = MeshTopology.Build(mesh, settings.Topology, Allocator.Persistent);
            var bank = QuadricBank.Build(mesh, topology, settings, Allocator.Persistent);
            try
            {
                return Collapse(mesh, topology, ref bank, settings, protection, allocator, out _);
            }
            finally
            {
                bank.Dispose();
            }
        }

        /// <summary>The collapse stage alone. Consumes the bank: its quadrics are accumulated in place, and its
        /// frame quadrics are moved into a growable pool and released.</summary>
        internal static CollapseSequence Collapse(in EvalMesh mesh, in MeshTopology topology, ref QuadricBank bank,
            in DecimationSettings settings, in VertexProtection protection, Allocator allocator, out CollapseStats stats)
        {
            using var uv0 = new OptionalArray<float2>(mesh.Uv0);
            using var weightStart = new OptionalArray<int>(mesh.WeightStart);
            using var weights = new OptionalArray<BoneInfluence>(mesh.Weights);
            using var locked = new OptionalArray<byte>(protection.Locked);
            using var multiplier = new OptionalArray<float>(protection.CostMultiplier);

            using var sparse = new NativeList<FrameQuadric>(bank.Sparse.Length + bank.Sparse.Length / 2 + 16, Allocator.Persistent);
            sparse.AddRange(bank.Sparse);
            bank.Sparse.Dispose();
            bank.Sparse = default;

            using var triangles = new NativeArray<int3>(topology.Triangles, Allocator.Persistent);
            using var steps = new NativeList<CollapseStep>(topology.LiveTriangleCount / 2 + 1, Allocator.Persistent);
            using var records = new NativeList<int2>(topology.LiveTriangleCount / 2 + 1, Allocator.Persistent);
            using var statsOut = new NativeArray<long>(2, Allocator.Persistent);

            new CollapseJob
            {
                Positions = mesh.Positions,
                Uv0 = uv0.Array,
                WeightStart = weightStart.Array,
                Weights = weights.Array,
                Origin = topology.Origin,
                PositionId = topology.Position,
                Partner = topology.Partner,
                Kind = topology.Kind,
                DeltaStart = topology.DeltaStart,
                DeltaFrame = topology.DeltaFrame,
                DeltaValue = topology.DeltaValue,
                ProtectLocked = locked.Array,
                ProtectMultiplier = multiplier.Array,
                NormalRest = bank.NormalRest,
                NormalStart = bank.NormalStart,
                NormalCount = bank.NormalCount,
                NormalFrames = bank.NormalFrames,
                Triangles = triangles,
                Rest = bank.Rest,
                Uv = bank.Uv,
                SparseStart = bank.SparseStart,
                SparseCount = bank.SparseCount,
                Sparse = sparse,
                LiveTriangles = topology.LiveTriangleCount,
                Settings = settings,
                Steps = steps,
                Records = records,
                Stats = statsOut,
            }.Run();

            stats = new CollapseStats
            {
                PeakPoolBytes = statsOut[0] * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<FrameQuadric>(),
                Blocked = statsOut[1],
            };
            var sequence = new CollapseSequence
            {
                InitialTriangleCount = topology.LiveTriangleCount,
                SourceVertexCount = topology.SourceVertexCount,
            };
            try
            {
                sequence.Steps = steps.ToArray(allocator);
                sequence.Records = records.ToArray(allocator);
                sequence.WorkingOrigin = new NativeArray<int>(topology.Origin, allocator);
                sequence.WorkingTriangles = new NativeArray<int3>(topology.Triangles, allocator);
                return sequence;
            }
            catch
            {
                sequence.Dispose();
                throw;
            }
        }
    }
}
