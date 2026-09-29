using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Occlusion
{
    /// <summary>Vertex-to-triangle adjacency and each vertex's area-weighted face normal at rest.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct AdjacencyJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Pos;
        [ReadOnly] public NativeArray<int3> Tris;
        public NativeArray<int> VTStart;   // V + 1
        public NativeArray<int> VTList;    // 3T
        public NativeArray<float3> GeoN;   // V, unnormalised

        public void Execute()
        {
            for (var t = 0; t < Tris.Length; t++)
            {
                var tri = Tris[t];
                VTStart[tri.x + 1]++; VTStart[tri.y + 1]++; VTStart[tri.z + 1]++;
                var g = math.cross(Pos[tri.y] - Pos[tri.x], Pos[tri.z] - Pos[tri.x]);
                GeoN[tri.x] += g; GeoN[tri.y] += g; GeoN[tri.z] += g;
            }
            for (var v = 0; v < Pos.Length; v++) VTStart[v + 1] += VTStart[v];
            var fill = new NativeArray<int>(Pos.Length, Allocator.Temp);
            for (var t = 0; t < Tris.Length; t++)
            {
                var tri = Tris[t];
                for (var c = 0; c < 3; c++)
                {
                    var v = tri[c];
                    VTList[VTStart[v] + fill[v]++] = t;
                }
            }
            fill.Dispose();
        }
    }

    /// <summary>One tree per occluder mesh over its occluder triangles at rest, all in one node pool.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct BuildTreesJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Pos;
        [ReadOnly] public NativeArray<int3> Tris;
        [ReadOnly] public NativeArray<int> OccTri;
        [ReadOnly] public NativeArray<int> MeshOccStart;   // meshes + 1
        public BvhSplit Split;
        public NativeList<BvhNode> Nodes;
        public NativeArray<BvhTri> Prims;                  // one per occluder, in leaf order
        public NativeArray<int> MeshRoot;

        public void Execute()
        {
            var bmin = new NativeArray<float3>(OccTri.Length, Allocator.Temp);
            var bmax = new NativeArray<float3>(OccTri.Length, Allocator.Temp);
            for (var o = 0; o < OccTri.Length; o++)
            {
                var t = Tris[OccTri[o]];
                bmin[o] = math.min(Pos[t.x], math.min(Pos[t.y], Pos[t.z]));
                bmax[o] = math.max(Pos[t.x], math.max(Pos[t.y], Pos[t.z]));
            }
            for (var m = 0; m < MeshRoot.Length; m++)
            {
                int start = MeshOccStart[m], count = MeshOccStart[m + 1] - start;
                var order = new NativeArray<int>(count, Allocator.Temp);
                for (var i = 0; i < count; i++) order[i] = start + i;
                MeshRoot[m] = Bvh.Build(bmin, bmax, order, Nodes, start, Split);
                for (var i = 0; i < count; i++)
                {
                    var o = order[i];
                    var t = Tris[OccTri[o]];
                    Prims[start + i] = new BvhTri { V0 = Pos[t.x], V1 = Pos[t.y], V2 = Pos[t.z], Occ = o };
                }
                order.Dispose();
            }
            bmin.Dispose();
            bmax.Dispose();
        }
    }

    /// <summary>
    /// One sample per vertex: its position, a usable normal, and its bones. Then, after all of them, a back
    /// sample for each vertex of a triangle whose material renders its back: the same point and bones, facing
    /// the other way, so that side is owed cover too.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct VertexSamplesJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Pos;
        [ReadOnly] public NativeArray<float3> Nrm;
        [ReadOnly] public NativeArray<float3> GeoN;
        [ReadOnly] public NativeArray<int> VertMesh;
        [ReadOnly] public NativeArray<int> WeightStart;
        [ReadOnly] public NativeArray<BoneInfluence> Weights;
        [ReadOnly] public NativeArray<bool> NeedsBack;
        public float MinRawWeight;
        public float MinInfluence;
        /// <summary>Out: per vertex, its back sample, or -1.</summary>
        public NativeArray<int> VertBack;
        public NativeList<float3> SamplePos;
        public NativeList<float3> SampleNrm;
        public NativeList<int> SampleMesh;
        public NativeList<int> SampleBoneStart;
        public NativeList<int> SampleBones;
        public NativeList<byte> SampleState;
        public NativeList<float3> SampleBary;
        public NativeList<bool> SampleBack;

        public void Execute()
        {
            SampleBoneStart.Add(0);
            for (var v = 0; v < Pos.Length; v++)
            {
                float raw = 0f;
                var kept = 0;
                for (var k = WeightStart[v]; k < WeightStart[v + 1]; k++)
                {
                    var w = Weights[k];
                    if (w.Weight <= 0f) continue;
                    raw += w.Weight;
                    if (w.Weight >= MinInfluence && kept < Samples.MaxBones)
                    {
                        SampleBones.Add(w.Bone);
                        kept++;
                    }
                }
                SampleBoneStart.Add(SampleBones.Length);
                // The cone stands on the geometry: the area-weighted normal of the faces using the vertex, the side
                // a viewer sees it from. An authored normal, which a toon mesh may lean far off the surface, is only
                // the fallback for a vertex whose faces have no area.
                var g = GeoN[v];
                var ok = true;
                var n = math.lengthsq(g) > 1e-30f ? math.normalize(g) : Samples.UsableNormal(Nrm[v], float3.zero, out ok);
                SamplePos.Add(Pos[v]);
                SampleNrm.Add(n);
                SampleMesh.Add(VertMesh[v]);
                SampleState.Add(raw < MinRawWeight || kept == 0 ? Reason.Unweighted : ok ? Reason.Covered : Reason.Escaped);
                SampleBary.Add(float3.zero);
                SampleBack.Add(false);
            }
            for (var v = 0; v < Pos.Length; v++)
            {
                VertBack[v] = -1;
                if (!NeedsBack[v]) continue;
                VertBack[v] = SamplePos.Length;
                for (var k = SampleBoneStart[v]; k < SampleBoneStart[v + 1]; k++) SampleBones.Add(SampleBones[k]);
                SampleBoneStart.Add(SampleBones.Length);
                SamplePos.Add(Pos[v]);
                SampleNrm.Add(-SampleNrm[v]);
                SampleMesh.Add(VertMesh[v]);
                SampleState.Add(SampleState[v]);
                SampleBary.Add(float3.zero);
                SampleBack.Add(true);
            }
        }
    }

    internal static class Samples
    {
        public const int MaxBones = 8;
        /// <summary>Lattice steps per edge at most. A triangle that needs more at the spacing is kept, never sampled coarser.</summary>
        public const int MaxSubdivision = 16;

        public static float3 UsableNormal(float3 n, float3 fallback, out bool ok)
        {
            ok = true;
            if (math.lengthsq(n) > 1e-12f) return math.normalize(n);
            if (math.lengthsq(fallback) > 1e-24f) return math.normalize(fallback);
            ok = false;
            return new float3(0, 1, 0);
        }
    }

    /// <summary>
    /// Interior samples on a lattice over each large triangle whose corners are all covered, so a hole
    /// in the cover smaller than the triangle and away from its corners still keeps it. A triangle that
    /// renders its back gets the lattice on each side.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct InteriorSamplesJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Pos;
        [ReadOnly] public NativeArray<int3> Tris;
        [ReadOnly] public NativeArray<int> TriMesh;
        [ReadOnly] public NativeArray<bool> TriTwoSided;
        [ReadOnly] public NativeArray<int> VertBack;
        [ReadOnly] public NativeArray<int> WeightStart;
        [ReadOnly] public NativeArray<BoneInfluence> Weights;
        [ReadOnly] public NativeArray<bool> Covered;
        public float Spacing;
        public float MinInfluence;
        public NativeArray<int> TriSampleStart;
        public NativeArray<int> TriSampleCount;
        /// <summary>Out: lattice steps per edge sampled, 0 for corners only.</summary>
        public NativeArray<int> TriSubdivision;
        /// <summary>Out: set for a triangle too large to sample at the spacing, which is then never hidden.</summary>
        public NativeArray<bool> TriVeto;
        public NativeList<float3> SamplePos;
        public NativeList<float3> SampleNrm;
        public NativeList<int> SampleMesh;
        public NativeList<int> SampleBoneStart;
        public NativeList<int> SampleBones;
        public NativeList<byte> SampleState;
        public NativeList<float3> SampleBary;
        public NativeList<bool> SampleBack;

        public void Execute()
        {
            if (Spacing <= 0f) return;
            for (var t = 0; t < Tris.Length; t++)
            {
                var tri = Tris[t];
                var twoSided = TriTwoSided[t];
                if (!Covered[tri.x] || !Covered[tri.y] || !Covered[tri.z]) continue;
                if (twoSided && (!Covered[VertBack[tri.x]] || !Covered[VertBack[tri.y]] || !Covered[VertBack[tri.z]])) continue;
                float3 a = Pos[tri.x], b = Pos[tri.y], c = Pos[tri.z];
                var longest = math.sqrt(math.max(math.distancesq(a, b), math.max(math.distancesq(b, c), math.distancesq(c, a))));
                if (longest <= Spacing) continue;
                var n = math.max(2, (int)math.ceil(longest / Spacing));
                if (n > Samples.MaxSubdivision)
                {
                    TriVeto[t] = true;
                    continue;
                }
                TriSubdivision[t] = n;
                TriSampleStart[t] = SamplePos.Length;
                var added = 0;
                for (var side = 0; side < (twoSided ? 2 : 1); side++)
                for (var i = 0; i <= n; i++)
                for (var j = 0; j <= n - i; j++)
                {
                    var k = n - i - j;
                    if (i == n || j == n || k == n) continue;
                    var bary = new float3(i, j, k) / n;
                    Add(t, tri, bary, side == 1);
                    added++;
                }
                TriSampleCount[t] = added;
            }
        }

        void Add(int t, int3 tri, float3 bary, bool back)
        {
            // The point as the triangle carries it: corners' positions and normals blended.
            var p = bary.x * Pos[tri.x] + bary.y * Pos[tri.y] + bary.z * Pos[tri.z];
            var n = bary.x * SampleNrm[tri.x] + bary.y * SampleNrm[tri.y] + bary.z * SampleNrm[tri.z];
            var geo = math.cross(Pos[tri.y] - Pos[tri.x], Pos[tri.z] - Pos[tri.x]);
            if (back) { n = -n; geo = -geo; }
            n = Samples.UsableNormal(n, geo, out var ok);

            // The point skins as the blend of its corners; its bones are the blend's, above the floor.
            var blend = new FixedList512Bytes<BoneInfluence>();
            for (var c = 0; c < 3; c++)
            {
                var vtx = tri[c];
                for (var k = WeightStart[vtx]; k < WeightStart[vtx + 1]; k++)
                {
                    var w = Weights[k];
                    if (w.Weight <= 0f) continue;
                    var add = w.Weight * bary[c];
                    var found = false;
                    for (var e = 0; e < blend.Length; e++)
                    {
                        if (blend[e].Bone != w.Bone) continue;
                        blend[e] = new BoneInfluence { Bone = w.Bone, Weight = blend[e].Weight + add };
                        found = true;
                        break;
                    }
                    if (!found && blend.Length < blend.Capacity) blend.Add(new BoneInfluence { Bone = w.Bone, Weight = add });
                }
            }
            var kept = 0;
            for (var pass = 0; pass < Samples.MaxBones; pass++)
            {
                var best = -1;
                for (var e = 0; e < blend.Length; e++)
                    if (blend[e].Weight >= MinInfluence && (best < 0 || blend[e].Weight > blend[best].Weight)) best = e;
                if (best < 0) break;
                SampleBones.Add(blend[best].Bone);
                blend[best] = new BoneInfluence { Bone = blend[best].Bone, Weight = -1f };
                kept++;
            }
            SampleBoneStart.Add(SampleBones.Length);
            SamplePos.Add(p);
            SampleNrm.Add(n);
            SampleMesh.Add(TriMesh[t]);
            SampleState.Add(kept == 0 ? Reason.Unweighted : ok ? Reason.Covered : Reason.Escaped);
            SampleBary.Add(bary);
            SampleBack.Add(back);
        }
    }

    /// <summary>Evaluates listed samples at rest.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct RestEvalJob : IJobParallelFor
    {
        public CoverageKernel K;
        [ReadOnly] public NativeArray<int> Work;
        [ReadOnly] public NativeArray<float3> SamplePos;
        [ReadOnly] public NativeArray<float3> SampleNrm;
        /// <summary>Set on a sample that was covered and is not now.</summary>
        public byte ExposeFlag;
        [NativeDisableParallelForRestriction] public NativeArray<bool> Covered;
        [NativeDisableParallelForRestriction] public NativeArray<byte> SampleReason;
        [NativeDisableParallelForRestriction] public NativeArray<byte> SampleFlags;
        [NativeDisableParallelForRestriction] public NativeArray<ulong> Witness;
        public NativeArray<int> Rays;

        public void Execute(int i)
        {
            var s = Work[i];
            var r = K.Evaluate(s, SamplePos[s], SampleNrm[s], out var w, out var rays);
            if (Covered[s] && r != Reason.Covered) SampleFlags[s] |= ExposeFlag;
            Covered[s] = r == Reason.Covered;
            SampleReason[s] = r;
            Witness[s] = w;
            Rays[i] = rays;
        }
    }

    internal struct FrameWork
    {
        public int Sample;
        public float3 Pos;
        public float3 Nrm;
        /// <summary>Unmoved by the frame and at risk only through the moved faces: cast only rays toward them.</summary>
        public bool OnlyRisky;
    }

    /// <summary>
    /// Everything one live frame puts at risk, and the geometry it moves. A frame moves one mesh, so it
    /// changes coverage in two ways only. The mesh's own samples whose position or normal it changes are
    /// re-cast against every other mesh at rest. Every other sample is at risk only if the frame moves a
    /// face that was its witness: a ray blocked at rest by an unmoved face stays blocked, and a face moved
    /// into a ray can only add cover. So a sample is re-cast when its witness set names this mesh and its
    /// cone can reach the moved faces' rest bounds.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct FramePrepJob : IJob
    {
        public int Mesh;
        public float Spacing;
        public int DeltaStart;
        public int DeltaCount;
        public bool MeshOccludes;
        public float MoveEpsilon;
        public float Reach;
        public float OriginOffset;
        public float ConeRadians;
        public BvhSplit Split;
        [ReadOnly] public NativeArray<ShapeDelta> Deltas;
        [ReadOnly] public NativeArray<float3> Pos;
        [ReadOnly] public NativeArray<float3> GeoN;
        [ReadOnly] public NativeArray<int> VTStart;
        [ReadOnly] public NativeArray<int> VTList;
        [ReadOnly] public NativeArray<int3> Tris;
        [ReadOnly] public NativeArray<int> TriOcc;
        [ReadOnly] public NativeArray<int> TriSampleStart;
        [ReadOnly] public NativeArray<int> TriSampleCount;
        [ReadOnly] public NativeArray<int> TriSubdivision;
        /// <summary>Set for a triangle the frame stretches past its sampled lattice: it is never hidden.</summary>
        public NativeArray<bool> TriVeto;
        [ReadOnly] public NativeArray<float3> SamplePos;
        [ReadOnly] public NativeArray<float3> SampleNrm;
        [ReadOnly] public NativeArray<float3> SampleBary;
        [ReadOnly] public NativeArray<bool> SampleBack;
        [ReadOnly] public NativeArray<int> VertBack;
        [ReadOnly] public NativeArray<int> SampleMesh;
        [ReadOnly] public NativeArray<ulong> Witness;
        [ReadOnly] public NativeArray<bool> Covered;
        [ReadOnly] public NativeArray<bool> Relevant;
        [ReadOnly] public NativeArray<int> RelevantList;
        public NativeArray<float3> DeltaScratch;
        public NativeArray<float3> NormalScratch;
        public NativeArray<bool> VertMark;
        public NativeArray<bool> TriMark;
        public NativeArray<bool> TriMark2;
        public NativeArray<bool> Moved;
        public NativeList<FrameWork> Work;
        public NativeList<int> MovedOcc;
        public NativeList<BvhNode> FrameNodes;
        public NativeList<BvhTri> FramePrims;
        /// <summary>Out: the rest bounds of the moved occluder faces, min then max.</summary>
        public NativeArray<float3> RiskBox;

        public void Execute()
        {
            Work.Clear();
            RiskBox[0] = RiskBox[1] = float3.zero;
            MovedOcc.Clear();
            FrameNodes.Clear();
            FramePrims.Clear();
            var moved = new NativeList<int>(Allocator.Temp);
            var eps2 = MoveEpsilon * MoveEpsilon;
            for (var k = DeltaStart; k < DeltaStart + DeltaCount; k++)
            {
                var d = Deltas[k];
                if (math.lengthsq(d.Delta) <= eps2) continue;
                DeltaScratch[d.Vertex] = d.Delta;
                moved.Add(d.Vertex);
            }
            if (moved.Length == 0)
            {
                moved.Dispose();
                return;
            }

            // Triangles the frame reshapes, and every corner of them: those corners' normals change.
            var tris = new NativeList<int>(Allocator.Temp);
            foreach (var v in moved)
                for (var k = VTStart[v]; k < VTStart[v + 1]; k++)
                {
                    var t = VTList[k];
                    if (TriMark[t]) continue;
                    TriMark[t] = true;
                    tris.Add(t);
                }
            // The rest lattice is only as fine as the rest triangle; one the frame stretches past it is kept.
            if (Spacing > 0f)
                foreach (var t in tris)
                {
                    var tri = Tris[t];
                    float3 a = At(tri.x), b = At(tri.y), c = At(tri.z);
                    var longest = math.sqrt(math.max(math.distancesq(a, b), math.max(math.distancesq(b, c), math.distancesq(c, a))));
                    var need = longest > Spacing ? (int)math.ceil(longest / Spacing) : 1;
                    if (need > math.max(1, TriSubdivision[t])) TriVeto[t] = true;
                }
            var affected = new NativeList<int>(Allocator.Temp);
            foreach (var t in tris)
                for (var c = 0; c < 3; c++)
                {
                    var v = Tris[t][c];
                    if (VertMark[v]) continue;
                    VertMark[v] = true;
                    affected.Add(v);
                }

            // A normal follows the rotation its area-weighted face normal takes, so authored smoothing holds.
            foreach (var a in affected)
            {
                var g = float3.zero;
                for (var k = VTStart[a]; k < VTStart[a + 1]; k++)
                {
                    var tri = Tris[VTList[k]];
                    float3 p0 = At(tri.x), p1 = At(tri.y), p2 = At(tri.z);
                    g += math.cross(p1 - p0, p2 - p0);
                }
                NormalScratch[a] = Rotate(SampleNrm[a], GeoN[a], g);
            }

            // The mesh's own samples the frame changes, each side of each.
            foreach (var a in affected)
            {
                if (Relevant[a] && Covered[a])
                    Work.Add(new FrameWork { Sample = a, Pos = At(a), Nrm = NormalScratch[a] });
                var back = VertBack[a];
                if (back >= 0 && Relevant[back] && Covered[back])
                    Work.Add(new FrameWork { Sample = back, Pos = At(a), Nrm = -NormalScratch[a] });
            }
            var tris2 = new NativeList<int>(Allocator.Temp);
            foreach (var a in affected)
                for (var k = VTStart[a]; k < VTStart[a + 1]; k++)
                {
                    var t = VTList[k];
                    if (TriMark2[t]) continue;
                    TriMark2[t] = true;
                    tris2.Add(t);
                    var tri = Tris[t];
                    for (var s = TriSampleStart[t]; s < TriSampleStart[t] + TriSampleCount[t]; s++)
                    {
                        if (!Relevant[s] || !Covered[s]) continue;
                        var b = SampleBary[s];
                        var p = b.x * At(tri.x) + b.y * At(tri.y) + b.z * At(tri.z);
                        var n = b.x * NormalAt(tri.x) + b.y * NormalAt(tri.y) + b.z * NormalAt(tri.z);
                        if (math.lengthsq(n) <= 1e-12f) n = math.cross(At(tri.y) - At(tri.x), At(tri.z) - At(tri.x));
                        if (SampleBack[s]) n = -n;
                        // Still degenerate, the triangle has no area in the frame and renders nothing.
                        Work.Add(new FrameWork { Sample = s, Pos = p, Nrm = math.lengthsq(n) > 1e-24f ? math.normalize(n) : SampleNrm[s] });
                    }
                }

            // Other meshes' samples whose cover the frame can take away.
            if (MeshOccludes)
            {
                var lo = new float3(float.MaxValue);
                var hi = new float3(float.MinValue);
                var bmin = new NativeList<float3>(Allocator.Temp);
                var bmax = new NativeList<float3>(Allocator.Temp);
                foreach (var t in tris)
                {
                    var occ = TriOcc[t];
                    if (occ < 0) continue;
                    var tri = Tris[t];
                    MovedOcc.Add(occ);
                    Moved[occ] = true;
                    lo = math.min(lo, math.min(Pos[tri.x], math.min(Pos[tri.y], Pos[tri.z])));
                    hi = math.max(hi, math.max(Pos[tri.x], math.max(Pos[tri.y], Pos[tri.z])));
                    float3 p0 = At(tri.x), p1 = At(tri.y), p2 = At(tri.z);
                    bmin.Add(math.min(p0, math.min(p1, p2)));
                    bmax.Add(math.max(p0, math.max(p1, p2)));
                    FramePrims.Add(new BvhTri { V0 = p0, V1 = p1, V2 = p2, Occ = occ });
                }
                if (MovedOcc.Length > 0)
                {
                    // Padded past float noise, so a ray that met a moved face at rest cannot slip outside the
                    // box it is tested against and be taken as still blocked.
                    var pad = 1e-5f + 1e-5f * math.cmax(hi - lo);
                    lo -= pad;
                    hi += pad;
                    RiskBox[0] = lo;
                    RiskBox[1] = hi;
                    var order = new NativeArray<int>(MovedOcc.Length, Allocator.Temp);
                    for (var i = 0; i < order.Length; i++) order[i] = i;
                    Bvh.Build(bmin.AsArray(), bmax.AsArray(), order, FrameNodes, 0, Split);
                    var unordered = new NativeArray<BvhTri>(FramePrims.AsArray(), Allocator.Temp);
                    for (var i = 0; i < order.Length; i++) FramePrims[i] = unordered[order[i]];
                    unordered.Dispose();
                    order.Dispose();

                    var bit = CoverageKernel.Bit(Mesh);
                    var centre = (lo + hi) * 0.5f;
                    var radius = math.length(hi - lo) * 0.5f;
                    foreach (var s in RelevantList)
                    {
                        if (!Covered[s] || SampleMesh[s] == Mesh || (Witness[s] & bit) == 0) continue;
                        var p = SamplePos[s];
                        var to = centre - p;
                        var dist = math.length(to);
                        // A ray starts OriginOffset along the normal, so it reaches that much past Reach from the sample.
                        if (dist - radius > Reach + OriginOffset) continue;
                        if (dist > radius)
                        {
                            var off = math.acos(math.clamp(math.dot(to / dist, SampleNrm[s]), -1f, 1f));
                            if (off - math.asin(math.saturate(radius / dist)) > ConeRadians + 1e-3f) continue;
                        }
                        Work.Add(new FrameWork { Sample = s, Pos = p, Nrm = SampleNrm[s], OnlyRisky = true });
                    }
                }
                bmin.Dispose();
                bmax.Dispose();
            }

            foreach (var v in moved) DeltaScratch[v] = float3.zero;
            foreach (var v in affected) VertMark[v] = false;
            foreach (var t in tris) TriMark[t] = false;
            foreach (var t in tris2) TriMark2[t] = false;
            moved.Dispose();
            tris.Dispose();
            tris2.Dispose();
            affected.Dispose();
        }

        float3 At(int v) => Pos[v] + DeltaScratch[v];
        float3 NormalAt(int v) => VertMark[v] ? NormalScratch[v] : SampleNrm[v];

        static float3 Rotate(float3 n, float3 from, float3 to)
        {
            // No area in the frame: the face draws nothing there, and keeps its cone.
            if (math.lengthsq(to) <= 1e-30f) return n;
            // No area at rest gave nothing to turn from: the frame's own face decides.
            if (math.lengthsq(from) <= 1e-30f) return math.normalize(to);
            var a = math.normalize(from);
            var b = math.normalize(to);
            var d = math.dot(a, b);
            if (d > 0.999999f) return n;
            if (d < -0.999f) return b;
            var q = math.normalize(new quaternion(new float4(math.cross(a, b), 1f + d)));
            return math.normalize(math.mul(q, n));
        }
    }

    /// <summary>Re-casts one frame's work items.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct FrameEvalJob : IJobParallelFor
    {
        public CoverageKernel K;
        [ReadOnly] public NativeArray<FrameWork> Work;
        public NativeArray<byte> Result;
        public NativeArray<int> Rays;
        public NativeArray<ulong> Witness;

        public void Execute(int i)
        {
            var w = Work[i];
            Result[i] = K.Evaluate(w.Sample, w.Pos, w.Nrm, out var witness, out var rays, w.OnlyRisky);
            Rays[i] = rays;
            Witness[i] = witness;
        }
    }

    /// <summary>Uncovers what a frame exposed, records what covered the rest, and puts the moved faces back.</summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct FrameApplyJob : IJob
    {
        [ReadOnly] public NativeArray<FrameWork> Work;
        [ReadOnly] public NativeArray<byte> Result;
        [ReadOnly] public NativeArray<int> Rays;
        [ReadOnly] public NativeArray<ulong> Witness;
        /// <summary>Per sample: every mesh that blocked one of its rays in any live frame, or at rest before the deletion pass re-cast it.</summary>
        public NativeArray<ulong> FrameWitness;
        [ReadOnly] public NativeArray<int> MovedOcc;
        public byte ExposeFlag;
        public NativeArray<bool> Covered;
        public NativeArray<byte> SampleReason;
        public NativeArray<byte> SampleFlags;
        public NativeArray<bool> Moved;
        public NativeReference<long> RayTotal;

        public void Execute()
        {
            long rays = 0;
            for (var i = 0; i < Work.Length; i++)
            {
                rays += Rays[i];
                var s = Work[i].Sample;
                if (Result[i] == Reason.Covered)
                {
                    FrameWitness[s] |= Witness[i];
                    continue;
                }
                if (!Covered[s]) continue;
                Covered[s] = false;
                SampleReason[s] = Result[i];
                SampleFlags[s] |= ExposeFlag;
            }
            RayTotal.Value += rays;
            foreach (var o in MovedOcc) Moved[o] = false;
        }
    }

    /// <summary>
    /// Triangles still hidden: listed as eligible, every corner and interior sample covered. Also lists the
    /// samples those triangles hang on, the only ones whose verdict still matters.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal struct CandidatesJob : IJob
    {
        [ReadOnly] public NativeArray<int3> Tris;
        [ReadOnly] public NativeArray<bool> TriTwoSided;
        [ReadOnly] public NativeArray<int> VertBack;
        [ReadOnly] public NativeArray<bool> Eligible;
        [ReadOnly] public NativeArray<bool> TriVeto;
        [ReadOnly] public NativeArray<bool> Covered;
        [ReadOnly] public NativeArray<int> TriSampleStart;
        [ReadOnly] public NativeArray<int> TriSampleCount;
        public NativeArray<bool> Candidate;
        public NativeArray<bool> Relevant;
        public NativeList<int> RelevantList;

        public void Execute()
        {
            for (var i = 0; i < RelevantList.Length; i++) Relevant[RelevantList[i]] = false;
            RelevantList.Clear();
            for (var t = 0; t < Tris.Length; t++)
            {
                var tri = Tris[t];
                var twoSided = TriTwoSided[t];
                var ok = Eligible[t] && !TriVeto[t] && Covered[tri.x] && Covered[tri.y] && Covered[tri.z];
                if (ok && twoSided) ok = Covered[VertBack[tri.x]] && Covered[VertBack[tri.y]] && Covered[VertBack[tri.z]];
                for (var s = TriSampleStart[t]; ok && s < TriSampleStart[t] + TriSampleCount[t]; s++) ok = Covered[s];
                Candidate[t] = ok;
                if (!ok) continue;
                for (var c = 0; c < 3; c++)
                {
                    Mark(tri[c]);
                    if (twoSided) Mark(VertBack[tri[c]]);
                }
                for (var s = TriSampleStart[t]; s < TriSampleStart[t] + TriSampleCount[t]; s++) Mark(s);
            }
        }

        void Mark(int s)
        {
            if (Relevant[s]) return;
            Relevant[s] = true;
            RelevantList.Add(s);
        }
    }
}
