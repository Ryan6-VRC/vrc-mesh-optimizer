using System;
using System.Collections.Generic;
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
    internal static class Decimator
    {
        public static CollapseSequence Run(in EvalMesh mesh, in DecimationSettings settings, in VertexProtection protection, Allocator allocator) =>
            Run(mesh, settings, protection, default, default, allocator, out _);

        /// <summary>
        /// Records the sequence keeping clear of the other layers in <paramref name="clearance"/>, when its settings
        /// enable the rule and it holds any, and with each collapse's error also measured in bends of <paramref name="rig"/>'s
        /// bones (see <see cref="BendSettings"/>), so a surface spanning a joint keeps what it needs to follow the joint when
        /// it bends. Either left unset takes no part.
        /// </summary>
        public static CollapseSequence Run(in EvalMesh mesh, in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance,
            in BendRig rig, Allocator allocator, out ClearanceStats stats)
        {
            using var pending = Schedule(mesh, default, settings, protection, clearance, rig);
            return pending.Complete(allocator, out stats);
        }

        /// <summary>
        /// Starts recording what <see cref="Run(in EvalMesh, in DecimationSettings, in VertexProtection, in ClearanceInput, in BendRig, Allocator, out ClearanceStats)"/>
        /// records, on a worker thread, and returns once it is under way: meshes scheduled before any is completed are
        /// recorded side by side, each exactly as alone. <paramref name="mesh"/>, <paramref name="protection"/> and
        /// <paramref name="clearance"/> are read until <see cref="PendingSequence.Complete"/>, so they must outlive it;
        /// <paramref name="excluded"/> and <paramref name="rig"/> are read before this returns. A triangle
        /// <paramref name="excluded"/> flags takes no part, as one that repeats a vertex takes none: the sequence is the one
        /// recorded from a copy of the mesh without those triangles, while every triangle keeps its index in
        /// <paramref name="mesh"/>, so <see cref="CutResult.SourceTriangle"/> names the mesh's triangles and
        /// <see cref="ClearanceInput.TriangleLayer"/> has one entry per triangle of it, an excluded one's unread.
        /// </summary>
        /// <param name="excluded">Per triangle of <paramref name="mesh"/>; unset excludes none.</param>
        internal static PendingSequence Schedule(in EvalMesh mesh, NativeArray<bool> excluded, in DecimationSettings settings, in VertexProtection protection,
            in ClearanceInput clearance, in BendRig rig)
        {
            var pending = new PendingSequence();
            try
            {
                var topology = pending.Own(MeshTopology.Build(mesh, excluded, settings.Topology, Allocator.Persistent));
                var bends = pending.Own(Bends.Build(mesh, rig, settings.Bend, Allocator.Persistent));
                var field = default(ClearanceField);
                if (settings.Clearance.Enabled && clearance.IsActive)
                {
                    var c = settings.Clearance;
                    if (!(c.Share >= 0f && c.Share < 0.5f))
                        throw new System.ArgumentException($"clearance share {c.Share} is outside [0, 0.5): at one half or more, two layers each giving up their share can meet");
                    if (!(c.Reach > 0f))
                        throw new System.ArgumentException($"clearance reach {c.Reach} m must be above 0; turn the rule off with Clearance.Enabled instead");
                    if (!(c.CrossingAllowance >= 0f) || float.IsPositiveInfinity(c.CrossingAllowance))
                        throw new System.ArgumentException($"crossing allowance {c.CrossingAllowance} m must be finite and 0 or more; below 0, surface that touches another layer without crossing it could never collapse, and at infinity any crossing could lengthen");
                    if (c.Reach != clearance.Surface.Reach)
                        throw new System.ArgumentException($"clearance reach {c.Reach} m differs from the {clearance.Surface.Reach} m the surface was built with; build it with the reach decimation runs with");
                    var live = new NativeArray<bool>(topology.Triangles.Length, Allocator.Persistent);
                    try
                    {
                        for (var t = 0; t < live.Length; t++) live[t] = topology.Triangles[t].x >= 0;
                        field = pending.Own(ClearanceField.Measure(mesh.Positions, mesh.Triangles, live, clearance, settings.Clearance.Reach, Allocator.Persistent));
                    }
                    finally
                    {
                        live.Dispose();
                    }
                }
                pending.NearVertices = field.NearVertices;
                var bank = QuadricBank.Build(mesh, topology, settings, bends, Allocator.Persistent);
                try
                {
                    pending.Start(mesh, topology, ref bank, settings, protection, clearance, field, bends);
                }
                finally
                {
                    // Owned as it stands after the start, whose pool has moved out of it.
                    pending.Own(bank);
                }
                return pending;
            }
            catch
            {
                pending.Dispose();
                throw;
            }
        }

        /// <summary>The collapse stage alone. Consumes the bank: its quadrics are accumulated in place, and its
        /// frame quadrics are moved into a growable pool and released. Keeps clear of the layers in <paramref name="clearance"/>
        /// where <paramref name="field"/> has been measured; its triangle clearances are updated in place as collapses replace
        /// triangles. <paramref name="bends"/> must be the ones the bank was built with.</summary>
        internal static CollapseSequence Collapse(in EvalMesh mesh, in MeshTopology topology, ref QuadricBank bank,
            in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance, in ClearanceField field,
            in Bends bends, Allocator allocator, out CollapseStats stats)
        {
            using var pending = new PendingSequence();
            pending.Start(mesh, topology, ref bank, settings, protection, clearance, field, bends);
            return pending.Finish(allocator, out stats);
        }
    }

    /// <summary>
    /// One mesh's collapse sequence while a worker thread records it (<see cref="Decimator.Schedule"/>). It owns what the
    /// recording reads and writes, apart from the caller's inputs, until <see cref="Complete"/> hands the sequence over.
    /// Dispose waits for the recording and frees all of it, so one abandoned after a failure leaks nothing.
    /// </summary>
    internal sealed class PendingSequence : IDisposable
    {
        readonly List<IDisposable> _owned = new List<IDisposable>();
        JobHandle _handle;
        NativeArray<int> _origin;
        NativeArray<int3> _workingTriangles;
        int _liveTriangles;
        NativeList<CollapseStep> _steps;
        NativeList<int2> _records;
        NativeArray<long> _stats;

        /// <summary>Vertices with another layer within the clearance reach, as measured before recording.</summary>
        internal int NearVertices;

        internal T Own<T>(T disposable) where T : IDisposable
        {
            _owned.Add(disposable);
            return disposable;
        }

        /// <summary>Schedules the collapse job over what <see cref="Decimator.Collapse"/> is given, consuming the bank's pool.</summary>
        internal void Start(in EvalMesh mesh, in MeshTopology topology, ref QuadricBank bank,
            in DecimationSettings settings, in VertexProtection protection, in ClearanceInput clearance, in ClearanceField field, in Bends bends)
        {
            var active = field.Triangle.IsCreated;
            var none = Own(active ? default : ClearanceSurface.None(Allocator.Persistent));
            var triangleLayer = Own(new OptionalArray<int>(active ? clearance.TriangleLayer : default));
            var vertexClearance = Own(new OptionalArray<float>(field.Vertex));
            var triangleClearance = Own(new OptionalArray<float>(field.Triangle));
            var starClearance = Own(new OptionalArray<float>(field.Star));
            var uv0 = Own(new OptionalArray<float2>(mesh.Uv0));
            var vertexNormals = Own(new OptionalArray<float3>(bank.NormalField.Length > 0 ? mesh.Normals : default));
            var weightStart = Own(new OptionalArray<int>(mesh.WeightStart));
            var weights = Own(new OptionalArray<BoneInfluence>(mesh.Weights));
            var locked = Own(new OptionalArray<byte>(protection.Locked));
            var multiplier = Own(new OptionalArray<float>(protection.CostMultiplier));

            var sparse = Own(new NativeList<FrameQuadric>(bank.Sparse.Length + bank.Sparse.Length / 2 + 16, Allocator.Persistent));
            sparse.AddRange(bank.Sparse);
            bank.Sparse.Dispose();
            bank.Sparse = default;

            var triangles = Own(new NativeArray<int3>(topology.Triangles, Allocator.Persistent));
            _steps = Own(new NativeList<CollapseStep>(topology.LiveTriangleCount / 2 + 1, Allocator.Persistent));
            _records = Own(new NativeList<int2>(topology.LiveTriangleCount / 2 + 1, Allocator.Persistent));
            _stats = Own(new NativeArray<long>(3, Allocator.Persistent));
            _origin = topology.Origin;
            _workingTriangles = topology.Triangles;
            _liveTriangles = topology.LiveTriangleCount;

            _handle = new CollapseJob
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
                Surface = active ? clearance.Surface : none,
                TriangleLayer = triangleLayer.Array,
                VertexClearance = vertexClearance.Array,
                TriangleClearance = triangleClearance.Array,
                StarClearance = starClearance.Array,
                Bends = bends,
                Settings = settings,
                Steps = _steps,
                Records = _records,
                Stats = _stats,
            }.Schedule();
        }

        /// <summary>Waits for the recording and returns the sequence, allocated with <paramref name="allocator"/>; the caller owns it.</summary>
        public CollapseSequence Complete(Allocator allocator, out ClearanceStats stats)
        {
            var sequence = Finish(allocator, out var collapseStats);
            stats = new ClearanceStats { NearVertices = NearVertices, Refused = collapseStats.ClearanceRefused };
            return sequence;
        }

        internal CollapseSequence Finish(Allocator allocator, out CollapseStats stats)
        {
            _handle.Complete();
            stats = new CollapseStats
            {
                PeakPoolBytes = _stats[0] * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<FrameQuadric>(),
                Blocked = _stats[1],
                ClearanceRefused = _stats[2],
            };
            var sequence = new CollapseSequence { InitialTriangleCount = _liveTriangles };
            try
            {
                sequence.Steps = _steps.ToArray(allocator);
                sequence.Records = _records.ToArray(allocator);
                sequence.WorkingOrigin = new NativeArray<int>(_origin, allocator);
                sequence.WorkingTriangles = new NativeArray<int3>(_workingTriangles, allocator);
                return sequence;
            }
            catch
            {
                sequence.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _handle.Complete();
            for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
        }
    }
}
