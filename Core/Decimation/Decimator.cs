using Ryan6Vrc.MeshOptimizer.Core.Clearance;
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
        /// <summary>Collapses refused by the clearance rule alone.</summary>
        public long ClearanceRefused;
    }

    /// <summary>
    /// Records a mesh's half-edge collapse sequence. A collapse removes one vertex (or a seam or border pair
    /// together) onto a neighbour and moves nothing: every surviving vertex keeps its position, weights, UVs,
    /// normals and shape deltas exactly as authored, so the output is a subset of the source vertices.
    /// </summary>
    public static class Decimator
    {
        public static CollapseSequence Run(in EvalMesh mesh, in DecimationSettings settings, in VertexProtection protection, Allocator allocator) =>
            Run(mesh, settings, protection, default, allocator, out _);

        /// <summary>
        /// Records the sequence keeping clear of the other layers in <paramref name="clearance"/>, when its settings
        /// enable the rule and it holds any; otherwise as the overload without it.
        /// </summary>
        public static CollapseSequence Run(in EvalMesh mesh, in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance,
            Allocator allocator, out ClearanceStats stats) =>
            Run(mesh, settings, protection, clearance, default, allocator, out stats);

        /// <summary>
        /// As the overload without <paramref name="rig"/>; with it, each collapse's error is also measured in bends of the
        /// rig's bones (see <see cref="BendSettings"/>), so a surface spanning a joint keeps what it needs to follow the
        /// joint when it bends.
        /// </summary>
        public static CollapseSequence Run(in EvalMesh mesh, in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance,
            in BendRig rig, Allocator allocator, out ClearanceStats stats) =>
            Run(mesh, default, settings, protection, clearance, rig, allocator, out stats);

        /// <summary>
        /// As the overload without <paramref name="excluded"/>, but a triangle it flags takes no part, as one that repeats a
        /// vertex takes none: the sequence is the one recorded from a copy of the mesh without those triangles, while every
        /// triangle keeps its index in <paramref name="mesh"/>, so <see cref="CutResult.SourceTriangle"/> names the mesh's
        /// triangles and <see cref="ClearanceInput.TriangleLayer"/> has one entry per triangle of it, an excluded one's unread.
        /// </summary>
        /// <param name="excluded">Per triangle of <paramref name="mesh"/>; unset excludes none.</param>
        public static CollapseSequence Run(in EvalMesh mesh, NativeArray<bool> excluded, in DecimationSettings settings, in VertexProtection protection,
            in ClearanceInput clearance, in BendRig rig, Allocator allocator, out ClearanceStats stats)
        {
            using var topology = MeshTopology.Build(mesh, excluded, settings.Topology, Allocator.Persistent);
            using var bends = Bends.Build(mesh, rig, settings.Bend, Allocator.Persistent);
            var field = default(ClearanceField);
            var bank = default(QuadricBank);
            try
            {
                if (settings.Clearance.Enabled && clearance.IsActive)
                {
                    var c = settings.Clearance;
                    if (!(c.Share >= 0f && c.Share < 0.5f))
                        throw new System.ArgumentException($"clearance share {c.Share} is outside [0, 0.5): at one half or more, two layers each giving up their share can meet");
                    if (!(c.Reach > 0f))
                        throw new System.ArgumentException($"clearance reach {c.Reach} m must be above 0; turn the rule off with Clearance.Enabled instead");
                    if (c.Reach != clearance.Surface.Reach)
                        throw new System.ArgumentException($"clearance reach {c.Reach} m differs from the {clearance.Surface.Reach} m the surface was built with; build it with the reach decimation runs with");
                    var live = new NativeArray<bool>(topology.Triangles.Length, Allocator.Persistent);
                    try
                    {
                        for (var t = 0; t < live.Length; t++) live[t] = topology.Triangles[t].x >= 0;
                        field = ClearanceField.Measure(mesh.Positions, mesh.Triangles, live, clearance, settings.Clearance.Reach, Allocator.Persistent);
                    }
                    finally
                    {
                        live.Dispose();
                    }
                }
                bank = QuadricBank.Build(mesh, topology, settings, bends, Allocator.Persistent);
                var sequence = Collapse(mesh, topology, ref bank, settings, protection, clearance, field, bends, allocator, out var collapseStats);
                stats = new ClearanceStats { NearVertices = field.NearVertices, Refused = collapseStats.ClearanceRefused };
                return sequence;
            }
            finally
            {
                bank.Dispose();
                field.Dispose();
            }
        }

        /// <summary>The collapse stage alone. Consumes the bank: its quadrics are accumulated in place, and its
        /// frame quadrics are moved into a growable pool and released.</summary>
        internal static CollapseSequence Collapse(in EvalMesh mesh, in MeshTopology topology, ref QuadricBank bank,
            in DecimationSettings settings, in VertexProtection protection, Allocator allocator, out CollapseStats stats) =>
            Collapse(mesh, topology, ref bank, settings, protection, default, default, allocator, out stats);

        internal static CollapseSequence Collapse(in EvalMesh mesh, in MeshTopology topology, ref QuadricBank bank,
            in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance, in ClearanceField field,
            Allocator allocator, out CollapseStats stats)
        {
            using var none = Bends.None(Allocator.Persistent);
            return Collapse(mesh, topology, ref bank, settings, protection, clearance, field, none, allocator, out stats);
        }

        /// <summary>The collapse stage, keeping clear of the layers in <paramref name="clearance"/> where <paramref name="field"/>
        /// has been measured; its triangle clearances are updated in place as collapses replace triangles. <paramref name="bends"/>
        /// must be the ones the bank was built with.</summary>
        internal static CollapseSequence Collapse(in EvalMesh mesh, in MeshTopology topology, ref QuadricBank bank,
            in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance, in ClearanceField field,
            in Bends bends, Allocator allocator, out CollapseStats stats)
        {
            var active = field.Triangle.IsCreated;
            using var nodes = new OptionalArray<Core.Gate.TreeNode>(active ? clearance.Surface.Nodes : default);
            using var prims = new OptionalArray<Core.Gate.TreePrim>(active ? clearance.Surface.Prims : default);
            using var primLayer = new OptionalArray<int>(active ? clearance.Surface.PrimLayer : default);
            using var nodeLayer = new OptionalArray<int>(active ? clearance.Surface.NodeLayer : default);
            using var primClearance = new OptionalArray<float>(active ? clearance.Surface.PrimClearance : default);
            using var nodeClearance = new OptionalArray<float>(active ? clearance.Surface.NodeClearance : default);
            using var triangleLayer = new OptionalArray<int>(active ? clearance.TriangleLayer : default);
            using var vertexClearance = new OptionalArray<float>(field.Vertex);
            using var triangleClearance = new OptionalArray<float>(field.Triangle);
            using var starClearance = new OptionalArray<float>(field.Star);
            using var uv0 = new OptionalArray<float2>(mesh.Uv0);
            using var vertexNormals = new OptionalArray<float3>(bank.NormalField.Length > 0 ? mesh.Normals : default);
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
            using var statsOut = new NativeArray<long>(3, Allocator.Persistent);

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
                VertexNormals = vertexNormals.Array,
                Triangles = triangles,
                Rest = bank.Rest,
                Uv = bank.Uv,
                NormalField = bank.NormalField,
                SparseStart = bank.SparseStart,
                SparseCount = bank.SparseCount,
                Sparse = sparse,
                LiveTriangles = topology.LiveTriangleCount,
                ClearNodes = nodes.Array,
                ClearPrims = prims.Array,
                ClearPrimLayer = primLayer.Array,
                ClearNodeLayer = nodeLayer.Array,
                ClearPrimClearance = primClearance.Array,
                ClearNodeClearance = nodeClearance.Array,
                TriangleLayer = triangleLayer.Array,
                VertexClearance = vertexClearance.Array,
                TriangleClearance = triangleClearance.Array,
                StarClearance = starClearance.Array,
                Bends = bends,
                Settings = settings,
                Steps = steps,
                Records = records,
                Stats = statsOut,
            }.Run();

            stats = new CollapseStats
            {
                PeakPoolBytes = statsOut[0] * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<FrameQuadric>(),
                Blocked = statsOut[1],
                ClearanceRefused = statsOut[2],
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
