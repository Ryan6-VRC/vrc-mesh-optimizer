using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Occlusion
{
    /// <summary>
    /// Decides which triangles are hidden under the shared-weights rule.
    ///
    /// Rays leave each sample (every vertex, and lattice points inside large triangles) in a cone about its
    /// geometric normal, the area-weighted normal of the faces using it; an authored normal decides nothing a
    /// viewer can see, and is used only where the faces have no area. A ray is blocked only by a face of an always-on renderer other than the sample's own, whose
    /// weight share on the sample's bones, widened by kin steps, reaches the share threshold, and which
    /// renders opaque where the ray meets it; every other face is transparent. A sample is covered when
    /// every ray is blocked, and a triangle is hidden when all its samples are covered. A triangle whose
    /// material renders its back (culling Off or Front) is sampled on both sides, so it is hidden only
    /// when each side is covered. Poses decide nothing:
    /// geometry on the same bones moves together, so what hides a surface at rest hides it in every pose.
    ///
    /// Shape keys: rest is the avatar as it stands, every key at its held weight. A configuration is a state of the
    /// whole avatar: every frame carrying one nonzero configuration id, each moving its own mesh, or one frame of id
    /// zero alone, with every other mesh at rest (see <see cref="ShapeFrame.Configuration"/>). Coverage holds at rest
    /// and in every configuration, and a configuration is evaluated only where it can change a verdict (see
    /// <see cref="FramePrepJob"/>). A configuration moves candidates and occluders alike, so a candidate is judged
    /// against the occluders that move with it where they move to.
    ///
    /// Deletion: a triangle hidden only by another hidden triangle would be exposed when both go. So after
    /// the first verdict, the hidden occluder faces are removed and every hidden candidate whose cover rests
    /// on a mesh that lost faces is re-evaluated, configurations included; only what stays covered is hidden.
    /// Each hidden triangle is then covered by faces that are kept.
    ///
    /// Masks: a mesh may name which of its triangles are candidates and which block (<see cref="OcclusionMesh.Candidate"/>,
    /// <see cref="OcclusionMesh.Blocks"/>). Only candidates are sampled and judged, and only blocking faces of always-on
    /// meshes are occluders. Unset, every triangle is both, which is the rule above to the bit.
    ///
    /// Setback: a solve that asks again after occluders moved may give each a setback (<see cref="OcclusionMesh.Setback"/>)
    /// and each candidate the meshes that covered it before (<see cref="OcclusionMesh.CoveredBy"/>, as a solve reports them
    /// in <see cref="OcclusionResult.CoveredBy"/>). A face of such a mesh then blocks a sample it covered before from up to
    /// its setback behind the sample, so a cover that moved past the sample by no more than that still covers it; any other
    /// face blocks only in front, as above. With no setback, every ray is the rule's own to the bit.
    /// </summary>
    public static class OcclusionSolver
    {
        const byte ExposedByFrame = 1;
        const byte ExposedByDeletion = 2;

        /// <summary>
        /// One result per mesh, in the order given, allocated with <paramref name="allocator"/> for the caller
        /// to dispose. Refuses by name any input that does not index consistently.
        /// </summary>
        public static OcclusionResult[] Solve(IReadOnlyList<OcclusionMesh> meshes, in OcclusionBones bones,
                                              in OcclusionSettings settings, Allocator allocator, out OcclusionStats stats)
        {
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            if (!bones.Parents.IsCreated) throw new ArgumentException("bones.Parents is not created; pass the avatar's bone parent table");
            settings.Validate();
            var total = Stopwatch.StartNew();
            stats = default;
            using var scene = new Scene(meshes, bones, settings);
            using var run = new Run(scene, settings);
            run.Execute(ref stats);
            var results = new OcclusionResult[meshes.Count];
            try
            {
                for (var m = 0; m < meshes.Count; m++) results[m] = run.Result(m, allocator);
            }
            catch
            {
                // The caller receives nothing, so nothing it would have disposed may outlive the throw.
                foreach (var r in results) r.Dispose();
                throw;
            }
            stats.TotalMs = total.Elapsed.TotalMilliseconds;
            return results;
        }

        /// <summary>Every mesh flattened into one index space, and the occluder trees.</summary>
        sealed class Scene : IDisposable
        {
            public readonly int MeshCount;
            public readonly int[] VertBase, TriBase, FrameCount;
            public readonly bool[] Occludes;
            public NativeArray<float3> Pos, Nrm, GeoN;
            public NativeArray<float2> Uv;
            public NativeArray<int> VertMesh, WeightStart, TriMesh, TriOcc, VTStart, VTList;
            /// <summary>Per triangle: a candidate whose material renders its back. Per vertex: some such triangle uses it.</summary>
            public NativeArray<bool> TriTwoSided, NeedsBack;
            /// <summary>Per triangle: it may be hidden (<see cref="OcclusionMesh.Candidate"/>). Per vertex: some such triangle uses it, so it is sampled.</summary>
            public NativeArray<bool> TriCandidate, VertCandidate;
            /// <summary>Per vertex and per triangle, <see cref="OcclusionMesh.CoveredBy"/> and <see cref="OcclusionMesh.InteriorCoveredBy"/>, every bit set where unset.</summary>
            public NativeArray<ulong> VertCoveredBy, TriCoveredBy;
            /// <summary>Per mesh, <see cref="OcclusionMesh.Setback"/>.</summary>
            public NativeArray<float> MeshSetback;
            /// <summary>Some mesh has a setback, so some sample may cast from behind itself.</summary>
            public bool AnySetback;
            public NativeArray<BoneInfluence> Weights;
            public NativeArray<int3> Tris;
            public NativeArray<int> OccTri, OccMesh, MeshOccStart, MeshRoot, MeshOrder, BoneParent;
            public NativeArray<OccMaterial> OccMat;
            public NativeArray<AlphaInfo> Alpha;
            public NativeArray<byte> Texels;
            public NativeList<BvhNode> Nodes;
            public NativeArray<BvhTri> Prims;
            /// <summary>Grouped by configuration: each configuration's deltas, mesh by mesh, are one run.</summary>
            public NativeArray<ShapeDelta> Deltas;
            /// <summary>Per configuration, its run of <see cref="Deltas"/>.</summary>
            public readonly List<(int start, int count)> Configurations = new List<(int, int)>();
            public int FrameTotal;
            public double BuildMs;

            public Scene(IReadOnlyList<OcclusionMesh> meshes, in OcclusionBones bones, in OcclusionSettings settings)
            {
                try { Flatten(meshes, bones, settings, out MeshCount, out VertBase, out TriBase, out FrameCount, out Occludes); }
                catch { Dispose(); throw; }
            }

            void Flatten(IReadOnlyList<OcclusionMesh> meshes, in OcclusionBones bones, in OcclusionSettings settings,
                         out int meshCount, out int[] vertBase, out int[] triBase, out int[] frameCount, out bool[] occludes)
            {
                meshCount = meshes.Count;
                vertBase = new int[meshCount + 1];
                triBase = new int[meshCount + 1];
                frameCount = new int[meshCount];
                occludes = new bool[meshCount];
                var boneCount = bones.Parents.Length;
                int weightCount = 0, deltaCount = 0;
                // Each configuration's members as (mesh, frame): a frame of id zero alone, or every frame of one nonzero id.
                var members = new List<List<(int mesh, int frame)>>();
                var byId = new Dictionary<int, int>();
                for (var m = 0; m < meshCount; m++)
                {
                    var om = meshes[m] ?? throw new ArgumentException($"mesh {m} is null");
                    var e = om.Mesh;
                    var v = e.Positions.IsCreated ? e.Positions.Length : 0;
                    var t = e.Triangles.IsCreated ? e.Triangles.Length : 0;
                    if (!e.Normals.IsCreated || e.Normals.Length != v) throw new ArgumentException($"mesh {m}: Normals must match Positions ({v})");
                    if (e.Uv0.IsCreated && e.Uv0.Length != v) throw new ArgumentException($"mesh {m}: Uv0 has {e.Uv0.Length} entries for {v} vertices");
                    if (!e.WeightStart.IsCreated || e.WeightStart.Length != v + 1 || !e.Weights.IsCreated) throw new ArgumentException($"mesh {m}: WeightStart must have VertexCount + 1 ({v + 1}) entries over a created Weights");
                    if (e.TriangleSubmesh.IsCreated && e.TriangleSubmesh.Length != t) throw new ArgumentException($"mesh {m}: TriangleSubmesh has {e.TriangleSubmesh.Length} entries for {t} triangles");
                    if (om.Candidate != null && om.Candidate.Length != t) throw new ArgumentException($"mesh {m}: Candidate has {om.Candidate.Length} entries for {t} triangles");
                    if (om.Blocks != null && om.Blocks.Length != t) throw new ArgumentException($"mesh {m}: Blocks has {om.Blocks.Length} entries for {t} triangles");
                    if (!(om.Setback >= 0f) || float.IsInfinity(om.Setback)) throw new ArgumentException($"mesh {m}: Setback is {om.Setback}; it must be finite and not negative");
                    if (om.CoveredBy != null && om.CoveredBy.Length != v) throw new ArgumentException($"mesh {m}: CoveredBy has {om.CoveredBy.Length} entries for {v} vertices");
                    if (om.InteriorCoveredBy != null && om.InteriorCoveredBy.Length != t) throw new ArgumentException($"mesh {m}: InteriorCoveredBy has {om.InteriorCoveredBy.Length} entries for {t} triangles");
                    vertBase[m + 1] = vertBase[m] + v;
                    triBase[m + 1] = triBase[m] + t;
                    weightCount += e.Weights.Length;
                    if (e.Frames.IsCreated)
                    {
                        frameCount[m] = e.Frames.Length;
                        for (var f = 0; f < e.Frames.Length; f++)
                        {
                            var fr = e.Frames[f];
                            if (fr.DeltaStart < 0 || fr.DeltaCount < 0 || fr.DeltaStart + fr.DeltaCount > (e.Deltas.IsCreated ? e.Deltas.Length : 0))
                                throw new ArgumentException($"mesh {m}: frame {f} runs outside Deltas");
                            deltaCount += fr.DeltaCount;
                            if (fr.Configuration == 0)
                            {
                                members.Add(new List<(int, int)> { (m, f) });
                                continue;
                            }
                            if (!byId.TryGetValue(fr.Configuration, out var c))
                            {
                                byId[fr.Configuration] = c = members.Count;
                                members.Add(new List<(int, int)>());
                            }
                            foreach (var (other, of) in members[c])
                                if (other == m)
                                    throw new ArgumentException($"mesh {m} carries frames {of} and {f} of configuration {fr.Configuration}; a mesh takes at most one frame in a state of the avatar");
                            members[c].Add((m, f));
                        }
                    }
                    occludes[m] = om.Occludes;
                }
                int V = vertBase[meshCount], T = triBase[meshCount];
                const Allocator a = Allocator.Persistent;
                Pos = new NativeArray<float3>(V, a);
                Nrm = new NativeArray<float3>(V, a);
                GeoN = new NativeArray<float3>(V, a);
                Uv = new NativeArray<float2>(V, a);
                VertMesh = new NativeArray<int>(V, a);
                WeightStart = new NativeArray<int>(V + 1, a);
                Weights = new NativeArray<BoneInfluence>(weightCount, a);
                Tris = new NativeArray<int3>(T, a);
                TriMesh = new NativeArray<int>(T, a);
                TriOcc = new NativeArray<int>(T, a);
                TriTwoSided = new NativeArray<bool>(T, a);
                NeedsBack = new NativeArray<bool>(V, a);
                TriCandidate = new NativeArray<bool>(T, a);
                VertCandidate = new NativeArray<bool>(V, a);
                VertCoveredBy = new NativeArray<ulong>(V, a);
                TriCoveredBy = new NativeArray<ulong>(T, a);
                MeshSetback = new NativeArray<float>(meshCount, a);
                VTStart = new NativeArray<int>(V + 1, a);
                VTList = new NativeArray<int>(3 * T, a);
                Deltas = new NativeArray<ShapeDelta>(deltaCount, a);
                BoneParent = new NativeArray<int>(bones.Parents, a);
                for (var b = 0; b < boneCount; b++)
                    if (BoneParent[b] < -1 || BoneParent[b] >= boneCount || BoneParent[b] == b)
                        throw new ArgumentException($"bone {b} names parent {BoneParent[b]}, outside the table of {boneCount}");

                var mats = new List<OccMaterial>();
                var maps = new List<AlphaMap>();
                var matBase = new int[meshCount];
                int w = 0, d = 0;
                for (var m = 0; m < meshCount; m++)
                {
                    var om = meshes[m];
                    var e = om.Mesh;
                    int vb = vertBase[m], v = vertBase[m + 1] - vb, tb = triBase[m];
                    NativeArray<float3>.Copy(e.Positions, 0, Pos, vb, v);
                    NativeArray<float3>.Copy(e.Normals, 0, Nrm, vb, v);
                    if (e.Uv0.IsCreated) NativeArray<float2>.Copy(e.Uv0, 0, Uv, vb, v);
                    NativeArray<BoneInfluence>.Copy(e.Weights, 0, Weights, w, e.Weights.Length);
                    for (var i = 0; i < v; i++)
                    {
                        VertMesh[vb + i] = m;
                        WeightStart[vb + i] = w + e.WeightStart[i];
                        VertCoveredBy[vb + i] = om.CoveredBy?[i] ?? ulong.MaxValue;
                    }
                    for (var t = 0; t < e.TriangleCount; t++) TriCoveredBy[tb + t] = om.InteriorCoveredBy?[t] ?? ulong.MaxValue;
                    MeshSetback[m] = om.Setback;
                    AnySetback |= om.Setback > 0f;
                    for (var k = 0; k < e.Weights.Length; k++)
                        if ((uint)e.Weights[k].Bone >= (uint)boneCount)
                            throw new ArgumentException($"mesh {m}: an influence names bone {e.Weights[k].Bone}, outside the table of {boneCount}");
                    w += e.Weights.Length;
                    WeightStart[vb + v] = w;

                    // A mesh with triangles has at least the implicit submesh 0, material or not.
                    var subCount = math.max(om.Submeshes?.Length ?? 0, e.TriangleCount > 0 ? 1 : 0);
                    for (var t = 0; t < e.TriangleCount; t++)
                        if (e.TriangleSubmesh.IsCreated) subCount = math.max(subCount, e.TriangleSubmesh[t] + 1);
                    matBase[m] = mats.Count;
                    for (var s = 0; s < subCount; s++)
                    {
                        var sub = om.Submeshes != null && s < om.Submeshes.Length ? om.Submeshes[s] : null;
                        var alpha = -1;
                        if (sub?.Alpha != null && sub.Mode == OccluderMode.Auto)
                        {
                            alpha = maps.IndexOf(sub.Alpha);
                            if (alpha < 0) { alpha = maps.Count; maps.Add(sub.Alpha); }
                        }
                        mats.Add(new OccMaterial
                        {
                            Mode = sub?.Mode ?? OccluderMode.Auto, Culling = sub?.Culling ?? FaceCulling.Back, Alpha = alpha,
                            DrawsBack = (sub?.CandidateCulling ?? sub?.Culling ?? FaceCulling.Back) != FaceCulling.Back,
                        });
                    }

                    for (var t = 0; t < e.TriangleCount; t++)
                    {
                        var tri = e.Triangles[t];
                        if (math.any(tri < 0) || math.any(tri >= v)) throw new ArgumentException($"mesh {m}: triangle {t} names a vertex outside 0..{v - 1}");
                        Tris[tb + t] = tri + vb;
                        TriMesh[tb + t] = m;
                        if (om.Candidate != null && !om.Candidate[t]) continue;
                        TriCandidate[tb + t] = true;
                        VertCandidate[vb + tri.x] = VertCandidate[vb + tri.y] = VertCandidate[vb + tri.z] = true;
                        // A material that renders a face's back shows it to a viewer behind the face.
                        if (!mats[matBase[m] + (e.TriangleSubmesh.IsCreated ? e.TriangleSubmesh[t] : 0)].DrawsBack) continue;
                        TriTwoSided[tb + t] = true;
                        NeedsBack[vb + tri.x] = NeedsBack[vb + tri.y] = NeedsBack[vb + tri.z] = true;
                    }

                    if (e.Deltas.IsCreated)
                        for (var k = 0; k < e.Deltas.Length; k++)
                            if ((uint)e.Deltas[k].Vertex >= (uint)v) throw new ArgumentException($"mesh {m}: a shape delta names vertex {e.Deltas[k].Vertex}, outside 0..{v - 1}");
                }

                // Each configuration's deltas as one run, its members in mesh order, over the flattened vertices.
                foreach (var list in members)
                {
                    var start = d;
                    foreach (var (m, f) in list)
                    {
                        var e = meshes[m].Mesh;
                        var fr = e.Frames[f];
                        for (var k = fr.DeltaStart; k < fr.DeltaStart + fr.DeltaCount; k++)
                            Deltas[d++] = new ShapeDelta { Vertex = e.Deltas[k].Vertex + vertBase[m], Delta = e.Deltas[k].Delta };
                        FrameTotal++;
                    }
                    Configurations.Add((start, d - start));
                }

                // Occluders: faces of always-on renderers that block, less any submesh that never occludes and any
                // face that can vanish with a bone.
                var vanishes = bones.Vanishes;
                if (vanishes.IsCreated && vanishes.Length != boneCount) throw new ArgumentException($"bones.Vanishes has {vanishes.Length} entries for {boneCount} bones");
                var occTri = new List<int>();
                var meshOccStart = new int[meshCount + 1];
                for (var m = 0; m < meshCount; m++)
                {
                    meshOccStart[m] = occTri.Count;
                    var e = meshes[m].Mesh;
                    var blocks = meshes[m].Blocks;
                    for (var t = 0; t < e.TriangleCount; t++)
                    {
                        var g = triBase[m] + t;
                        TriOcc[g] = -1;
                        if (!occludes[m] || (blocks != null && !blocks[t])) continue;
                        var mat = mats[matBase[m] + (e.TriangleSubmesh.IsCreated ? e.TriangleSubmesh[t] : 0)];
                        if (mat.Mode == OccluderMode.NeverOccludes) continue;
                        if (vanishes.IsCreated && TouchesVanishing(Tris[g], vanishes)) continue;
                        TriOcc[g] = occTri.Count;
                        occTri.Add(g);
                    }
                }
                meshOccStart[meshCount] = occTri.Count;
                OccTri = new NativeArray<int>(occTri.ToArray(), a);
                OccMesh = new NativeArray<int>(occTri.Count, a);
                OccMat = new NativeArray<OccMaterial>(occTri.Count, a);
                for (var o = 0; o < occTri.Count; o++)
                {
                    var g = occTri[o];
                    var m = TriMesh[g];
                    var e = meshes[m].Mesh;
                    OccMesh[o] = m;
                    OccMat[o] = mats[matBase[m] + (e.TriangleSubmesh.IsCreated ? e.TriangleSubmesh[g - triBase[m]] : 0)];
                }
                MeshOccStart = new NativeArray<int>(meshOccStart, a);

                var texelCount = 0;
                foreach (var map in maps) texelCount += map.Alpha.Length;
                Texels = new NativeArray<byte>(math.max(1, texelCount), a);
                Alpha = new NativeArray<AlphaInfo>(math.max(1, maps.Count), a);
                var off = 0;
                for (var i = 0; i < maps.Count; i++)
                {
                    var map = maps[i];
                    NativeArray<byte>.Copy(map.Alpha, 0, Texels, off, map.Alpha.Length);
                    Alpha[i] = new AlphaInfo
                    {
                        Width = map.Width, Height = map.Height, Offset = off,
                        ThresholdByte = (int)math.ceil(math.saturate(map.Threshold) * 255f),
                        ScaleOffset = map.ScaleOffset,
                        WrapU = map.WrapU,
                        WrapV = map.WrapV,
                    };
                    off += map.Alpha.Length;
                }

                var sw = Stopwatch.StartNew();
                new AdjacencyJob { Pos = Pos, Tris = Tris, VTStart = VTStart, VTList = VTList, GeoN = GeoN }.Run();
                Nodes = new NativeList<BvhNode>(math.max(1, occTri.Count), a);
                Prims = new NativeArray<BvhTri>(math.max(1, occTri.Count), a);
                MeshRoot = new NativeArray<int>(meshCount, a);
                new BuildTreesJob
                {
                    Pos = Pos, Tris = Tris, OccTri = OccTri, MeshOccStart = MeshOccStart, Split = settings.Split,
                    Nodes = Nodes, Prims = Prims, MeshRoot = MeshRoot,
                }.Run();
                BuildMs = sw.Elapsed.TotalMilliseconds;

                // Traversal order: meshes whose faces frames move least first, so witnesses sit on steady geometry.
                var order = new List<int>();
                for (var m = 0; m < meshCount; m++) if (MeshRoot[m] >= 0) order.Add(m);
                var fc = frameCount;
                order.Sort((x, y) => fc[x] != fc[y] ? fc[x].CompareTo(fc[y]) : x.CompareTo(y));
                MeshOrder = new NativeArray<int>(order.ToArray(), a);
            }

            bool TouchesVanishing(int3 tri, NativeArray<bool> vanishes)
            {
                for (var c = 0; c < 3; c++)
                    for (var k = WeightStart[tri[c]]; k < WeightStart[tri[c] + 1]; k++)
                        if (Weights[k].Weight > 0f && vanishes[Weights[k].Bone]) return true;
                return false;
            }

            public void Dispose()
            {
                void D<T>(ref NativeArray<T> x) where T : struct { if (x.IsCreated) x.Dispose(); }
                D(ref Pos); D(ref Nrm); D(ref GeoN); D(ref Uv); D(ref VertMesh); D(ref WeightStart); D(ref Weights);
                D(ref Tris); D(ref TriMesh); D(ref TriOcc); D(ref TriTwoSided); D(ref NeedsBack); D(ref TriCandidate); D(ref VertCandidate); D(ref VertCoveredBy); D(ref TriCoveredBy); D(ref MeshSetback); D(ref VTStart); D(ref VTList); D(ref Deltas);
                D(ref OccTri); D(ref OccMesh); D(ref OccMat); D(ref MeshOccStart); D(ref MeshRoot); D(ref MeshOrder);
                D(ref BoneParent); D(ref Texels); D(ref Alpha); D(ref Prims);
                if (Nodes.IsCreated) Nodes.Dispose();
            }
        }

        /// <summary>The samples, their verdicts, and the passes over them.</summary>
        sealed class Run : IDisposable
        {
            readonly Scene S;
            readonly OcclusionSettings Cfg;
            NativeArray<float3> Cone;
            NativeList<float3> SamplePos, SampleNrm, SampleBary;
            NativeList<bool> SampleBack;
            NativeArray<int> VertBack;
            NativeList<int> SampleMesh, SampleBoneStart, SampleBones;
            NativeList<byte> SampleState, SampleReason, SampleFlags;
            NativeList<bool> Covered, Relevant, SampleMark;
            NativeList<ulong> Witness, FrameWitness;
            /// <summary>Per sample: the meshes whose faces behind it may cover it, and how far behind it its rays start (see <see cref="CoverageKernel.Evaluate"/>).</summary>
            NativeList<ulong> SampleCoveredBy;
            NativeList<float> SampleStretch;
            NativeList<int> RelevantList;
            NativeArray<int> TriSampleStart, TriSampleCount, TriSubdivision;
            NativeArray<bool> TriVeto;
            NativeArray<bool> Disabled, Moved, Eligible, Candidate;
            NativeArray<BvhNode> EmptyNodes;
            NativeArray<BvhTri> EmptyPrims;
            NativeArray<float3> DeltaScratch, NormalScratch;
            NativeArray<bool> VertMark, TriMark, TriMark2;
            NativeList<FrameWork> Work;
            NativeList<int> MovedOcc;
            NativeList<BvhNode> FrameNodes;
            NativeList<BvhTri> FramePrims;
            NativeList<FrameTree> FrameTrees;
            NativeArray<FrameTree> EmptyTrees;
            NativeReference<long> FrameRays;
            long Rays;
            /// <summary>The smallest cosine between a ray of the cone and its axis, of those leaning less than a right angle.</summary>
            float MinSlant = 1f;
            readonly Stopwatch prepWatch = new Stopwatch();

            public Run(Scene s, OcclusionSettings cfg)
            {
                S = s;
                Cfg = cfg;
                try { Allocate(); }
                catch { Dispose(); throw; }
            }

            // Every buffer the passes use; the constructor disposes those made so far if one fails.
            void Allocate()
            {
                const Allocator a = Allocator.Persistent;
                int V = S.Pos.Length, T = S.Tris.Length, O = math.max(1, S.OccTri.Length);
                Cone = Occlusion.Cone.Directions(Cfg.ConeDegrees, Cfg.RingDegrees, a);
                foreach (var c in Cone) if (c.z > 0f) MinSlant = math.min(MinSlant, c.z);
                SamplePos = new NativeList<float3>(V, a); SampleNrm = new NativeList<float3>(V, a); SampleBary = new NativeList<float3>(V, a);
                SampleMesh = new NativeList<int>(V, a); SampleBoneStart = new NativeList<int>(V + 1, a); SampleBones = new NativeList<int>(4 * V, a);
                SampleState = new NativeList<byte>(V, a);
                SampleBack = new NativeList<bool>(V, a);
                VertBack = new NativeArray<int>(V, a);
                SampleReason = new NativeList<byte>(V, a); SampleFlags = new NativeList<byte>(V, a);
                Covered = new NativeList<bool>(V, a); Relevant = new NativeList<bool>(V, a); SampleMark = new NativeList<bool>(V, a); Witness = new NativeList<ulong>(V, a); FrameWitness = new NativeList<ulong>(V, a);
                RelevantList = new NativeList<int>(V, a);
                SampleCoveredBy = new NativeList<ulong>(V, a); SampleStretch = new NativeList<float>(V, a);
                TriSampleStart = new NativeArray<int>(T, a); TriSampleCount = new NativeArray<int>(T, a);
                TriSubdivision = new NativeArray<int>(T, a); TriVeto = new NativeArray<bool>(T, a);
                Disabled = new NativeArray<bool>(O, a); Moved = new NativeArray<bool>(O, a);
                Eligible = new NativeArray<bool>(T, a); Candidate = new NativeArray<bool>(T, a);
                EmptyNodes = new NativeArray<BvhNode>(0, a); EmptyPrims = new NativeArray<BvhTri>(0, a);
                DeltaScratch = new NativeArray<float3>(V, a); NormalScratch = new NativeArray<float3>(V, a);
                VertMark = new NativeArray<bool>(V, a); TriMark = new NativeArray<bool>(T, a); TriMark2 = new NativeArray<bool>(T, a);
                Work = new NativeList<FrameWork>(1024, a); MovedOcc = new NativeList<int>(1024, a);
                FrameNodes = new NativeList<BvhNode>(1024, a); FramePrims = new NativeList<BvhTri>(1024, a);
                FrameTrees = new NativeList<FrameTree>(16, a); EmptyTrees = new NativeArray<FrameTree>(0, a);
                FrameRays = new NativeReference<long>(a);
            }

            public void Execute(ref OcclusionStats stats)
            {
                stats.BuildMs = S.BuildMs;
                stats.OccluderTriangles = S.OccTri.Length;
                stats.BvhNodes = S.Nodes.Length;
                stats.LiveFrames = S.FrameTotal;
                stats.Configurations = S.Configurations.Count;
                var V = S.Pos.Length;

                var sw = Stopwatch.StartNew();
                new VertexSamplesJob
                {
                    Pos = S.Pos, Nrm = S.Nrm, GeoN = S.GeoN, VertMesh = S.VertMesh, WeightStart = S.WeightStart, Weights = S.Weights,
                    NeedsBack = S.NeedsBack, MinRawWeight = Cfg.MinRawWeight, MinInfluence = Cfg.MinInfluence, VertBack = VertBack,
                    SamplePos = SamplePos, SampleNrm = SampleNrm, SampleMesh = SampleMesh, SampleBoneStart = SampleBoneStart,
                    SampleBones = SampleBones, SampleState = SampleState, SampleBary = SampleBary, SampleBack = SampleBack,
                }.Run();
                ResizeState();
                CoverBehind(interior: false);
                using (var referenced = new NativeList<int>(V, Allocator.TempJob))
                {
                    for (var v = 0; v < V; v++) if (S.VertCandidate[v]) referenced.Add(v);
                    for (var v = 0; v < V; v++) if (VertBack[v] >= 0) referenced.Add(VertBack[v]);
                    stats.VertexSamples = referenced.Length;
                    RestEval(referenced.AsArray(), 0);
                }
                var firstInterior = SamplePos.Length;
                new InteriorSamplesJob
                {
                    Pos = S.Pos, Tris = S.Tris, TriMesh = S.TriMesh, TriCandidate = S.TriCandidate, TriTwoSided = S.TriTwoSided, VertBack = VertBack,
                    WeightStart = S.WeightStart, Weights = S.Weights,
                    Covered = Covered.AsArray(), Spacing = Cfg.InteriorSpacing, MinInfluence = Cfg.MinInfluence,
                    TriSampleStart = TriSampleStart, TriSampleCount = TriSampleCount, TriSubdivision = TriSubdivision, TriVeto = TriVeto,
                    SamplePos = SamplePos, SampleNrm = SampleNrm, SampleMesh = SampleMesh, SampleBoneStart = SampleBoneStart,
                    SampleBones = SampleBones, SampleState = SampleState, SampleBary = SampleBary, SampleBack = SampleBack,
                }.Run();
                ResizeState();
                CoverBehind(interior: true);
                stats.InteriorSamples = SamplePos.Length - firstInterior;
                var interior = new NativeArray<int>(SamplePos.Length - firstInterior, Allocator.TempJob);
                try
                {
                    for (var i = 0; i < interior.Length; i++) interior[i] = firstInterior + i;
                    RestEval(interior, 0);
                }
                finally
                {
                    interior.Dispose();
                }
                NativeArray<bool>.Copy(S.TriCandidate, Eligible);
                Candidates();
                stats.RestMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                Frames(ExposedByFrame);
                Candidates();
                stats.FramesMs = sw.Elapsed.TotalMilliseconds;

                // Take the hidden occluder faces away and re-decide every hidden triangle without them.
                // Only a sample with a witness, at rest or in any frame, on a mesh that lost faces can lose
                // cover: every ray of any other has a blocker on a mesh that keeps all its faces.
                //
                // Least-proven part of the module. Its soundness rests on bookkeeping that spans the rest
                // pass, every configuration and this pass together: which samples are re-cast, which witnesses
                // survive the re-cast, and which frames are re-run for them. A review found one hole here
                // that no test caught. If it is wrong, the test that breaks it layers three or more covers
                // with some hidden, puts configurations on the kept ones that move them away, orders meshes so
                // a deleted face is found first, and checks that a triangle is hidden only if it is covered
                // with every hidden face removed, in every frame.
                ulong lostFaces = 0;
                for (var o = 0; o < S.OccTri.Length; o++)
                {
                    Disabled[o] = Candidate[S.OccTri[o]];
                    if (Disabled[o]) lostFaces |= CoverageKernel.Bit(S.OccMesh[o]);
                }
                if (lostFaces != 0)
                {
                    sw.Restart();
                    NativeArray<bool>.Copy(Candidate, Eligible);
                    using (var again = new NativeList<int>(RelevantList.Length, Allocator.TempJob))
                    {
                        foreach (var s in RelevantList) if (((Witness[s] | FrameWitness[s]) & lostFaces) != 0) again.Add(s);
                        stats.DeletionPassSamples = again.Length;
                        // The re-cast replaces the rest witness. A frame pass 1 skipped because a now-deleted face
                        // covered the sample must still be re-run, so the old dependency is kept.
                        foreach (var s in again) FrameWitness[s] |= Witness[s];
                        RestEval(again.AsArray(), ExposedByDeletion);
                    }
                    Candidates();
                    KeepRelevantOnlyIfCoveredBy(lostFaces);
                    Frames(ExposedByDeletion);
                    Candidates();
                    stats.DeletionPassMs = sw.Elapsed.TotalMilliseconds;
                }
                stats.Rays = Rays + FrameRays.Value;
                stats.FramePrepMs = prepWatch.Elapsed.TotalMilliseconds;
            }

            void ResizeState()
            {
                var n = SamplePos.Length;
                Covered.Resize(n, NativeArrayOptions.ClearMemory);
                Relevant.Resize(n, NativeArrayOptions.ClearMemory);
                SampleMark.Resize(n, NativeArrayOptions.ClearMemory);
                SampleReason.Resize(n, NativeArrayOptions.ClearMemory);
                SampleFlags.Resize(n, NativeArrayOptions.ClearMemory);
                Witness.Resize(n, NativeArrayOptions.ClearMemory);
                FrameWitness.Resize(n, NativeArrayOptions.ClearMemory);
                SampleCoveredBy.Resize(n, NativeArrayOptions.ClearMemory);
                SampleStretch.Resize(n, NativeArrayOptions.ClearMemory);
            }

            /// <summary>
            /// Each sample's cover from behind, when some mesh has a setback: the meshes it names, from its vertex for a
            /// corner sample on either side or from its triangle for an interior one, and the deepest setback among them.
            /// Unset, every stretch stays zero and every ray starts where the rule starts it.
            /// </summary>
            void CoverBehind(bool interior)
            {
                if (!S.AnySetback) return;
                var deepest = new float[64];
                for (var m = 0; m < S.MeshCount; m++)
                {
                    var b = math.min(m, 63);
                    deepest[b] = math.max(deepest[b], S.MeshSetback[m]);
                }
                var stretchOf = new Dictionary<ulong, float>();
                void Set(int s, ulong names)
                {
                    SampleCoveredBy[s] = names;
                    if (!stretchOf.TryGetValue(names, out var stretch))
                    {
                        for (var b = 0; b < 64; b++)
                            if ((names & (1UL << b)) != 0) stretch = math.max(stretch, deepest[b]);
                        stretchOf[names] = stretch;
                    }
                    SampleStretch[s] = stretch;
                }
                if (interior)
                {
                    for (var t = 0; t < S.Tris.Length; t++)
                        for (var s = TriSampleStart[t]; s < TriSampleStart[t] + TriSampleCount[t]; s++) Set(s, S.TriCoveredBy[t]);
                    return;
                }
                for (var v = 0; v < S.Pos.Length; v++)
                {
                    Set(v, S.VertCoveredBy[v]);
                    if (VertBack[v] >= 0) Set(VertBack[v], S.VertCoveredBy[v]);
                }
            }

            CoverageKernel Kernel(NativeArray<FrameTree> frameTrees, NativeArray<BvhNode> frameNodes, NativeArray<BvhTri> framePrims) => new CoverageKernel
            {
                Cone = Cone, BoneParent = S.BoneParent, WeightStart = S.WeightStart, Weights = S.Weights, Tris = S.Tris, Uv = S.Uv,
                OccTri = S.OccTri, OccMat = S.OccMat, Alpha = S.Alpha, Texels = S.Texels,
                Nodes = S.Nodes.AsArray(), Prims = S.Prims, MeshRoot = S.MeshRoot, MeshOrder = S.MeshOrder,
                Disabled = Disabled, Moved = Moved, FrameTrees = frameTrees, FrameNodes = frameNodes, FramePrims = framePrims,
                SampleMesh = SampleMesh.AsArray(), SampleBoneStart = SampleBoneStart.AsArray(), SampleBones = SampleBones.AsArray(),
                SampleState = SampleState.AsArray(),
                MeshSetback = S.MeshSetback, SampleCoveredBy = SampleCoveredBy.AsArray(), SampleStretch = SampleStretch.AsArray(),
                Share = Cfg.Share, Reach = Cfg.Reach, OriginOffset = Cfg.OriginOffset, Kin = Cfg.KinSteps,
            };

            void RestEval(NativeArray<int> work, byte exposeFlag)
            {
                if (work.Length == 0) return;
                using var rays = new NativeArray<int>(work.Length, Allocator.TempJob);
                new RestEvalJob
                {
                    K = Kernel(EmptyTrees, EmptyNodes, EmptyPrims), Work = work, SamplePos = SamplePos.AsArray(), SampleNrm = SampleNrm.AsArray(),
                    ExposeFlag = exposeFlag, Covered = Covered.AsArray(), SampleReason = SampleReason.AsArray(),
                    SampleFlags = SampleFlags.AsArray(), Witness = Witness.AsArray(), Rays = rays,
                }.Schedule(work.Length, 32).Complete();
                for (var i = 0; i < rays.Length; i++) Rays += rays[i];
            }

            void KeepRelevantOnlyIfCoveredBy(ulong meshes)
            {
                var kept = 0;
                for (var i = 0; i < RelevantList.Length; i++)
                {
                    var s = RelevantList[i];
                    if (((Witness[s] | FrameWitness[s]) & meshes) != 0) RelevantList[kept++] = s;
                    else Relevant[s] = false;
                }
                RelevantList.Resize(kept, NativeArrayOptions.UninitializedMemory);
            }

            void Candidates()
            {
                new CandidatesJob
                {
                    Tris = S.Tris, TriTwoSided = S.TriTwoSided, VertBack = VertBack, Eligible = Eligible, TriVeto = TriVeto, Covered = Covered.AsArray(), TriSampleStart = TriSampleStart,
                    TriSampleCount = TriSampleCount, Candidate = Candidate, Relevant = Relevant.AsArray(), RelevantList = RelevantList,
                }.Run();
            }

            void Frames(byte exposeFlag)
            {
                foreach (var (start, count) in S.Configurations)
                {
                    if (RelevantList.Length == 0) return;
                    prepWatch.Start();
                    new FramePrepJob
                    {
                        Spacing = Cfg.InteriorSpacing, DeltaStart = start, DeltaCount = count,
                        MoveEpsilon = Cfg.MoveEpsilon, Reach = Cfg.Reach, OriginOffset = Cfg.OriginOffset, ConeRadians = math.radians(Cfg.ConeDegrees), Split = Cfg.Split,
                        MinSlant = MinSlant, SampleStretch = SampleStretch.AsArray(),
                        Deltas = S.Deltas, Pos = S.Pos, GeoN = S.GeoN, VTStart = S.VTStart, VTList = S.VTList, Tris = S.Tris, TriMesh = S.TriMesh, TriOcc = S.TriOcc,
                        TriSampleStart = TriSampleStart, TriSampleCount = TriSampleCount, TriSubdivision = TriSubdivision, TriVeto = TriVeto,
                        SamplePos = SamplePos.AsArray(),
                        SampleNrm = SampleNrm.AsArray(), SampleBary = SampleBary.AsArray(), SampleBack = SampleBack.AsArray(), VertBack = VertBack,
                        SampleMesh = SampleMesh.AsArray(),
                        Witness = Witness.AsArray(), Covered = Covered.AsArray(), Relevant = Relevant.AsArray(), RelevantList = RelevantList.AsArray(),
                        DeltaScratch = DeltaScratch, NormalScratch = NormalScratch, VertMark = VertMark, TriMark = TriMark, TriMark2 = TriMark2,
                        SampleMark = SampleMark.AsArray(), Moved = Moved, Work = Work, MovedOcc = MovedOcc,
                        FrameTrees = FrameTrees, FrameNodes = FrameNodes, FramePrims = FramePrims,
                    }.Run();
                    prepWatch.Stop();
                    var n = Work.Length;
                    using var result = new NativeArray<byte>(n, Allocator.TempJob);
                    using var rays = new NativeArray<int>(n, Allocator.TempJob);
                    using var witness = new NativeArray<ulong>(n, Allocator.TempJob);
                    if (n > 0)
                    {
                        new FrameEvalJob { K = Kernel(FrameTrees.AsArray(), FrameNodes.AsArray(), FramePrims.AsArray()), Work = Work.AsArray(), Result = result, Rays = rays, Witness = witness }.Schedule(n, 16).Complete();
                    }
                    new FrameApplyJob
                    {
                        Work = Work.AsArray(), Result = result, Rays = rays, Witness = witness, FrameWitness = FrameWitness.AsArray(),
                        MovedOcc = MovedOcc.AsArray(), ExposeFlag = exposeFlag,
                        Covered = Covered.AsArray(), SampleReason = SampleReason.AsArray(), SampleFlags = SampleFlags.AsArray(),
                        Moved = Moved, RayTotal = FrameRays,
                    }.Run();
                }
            }

            public OcclusionResult Result(int m, Allocator allocator)
            {
                int tb = S.TriBase[m], tc = S.TriBase[m + 1] - tb;
                int vb = S.VertBase[m], vc = S.VertBase[m + 1] - vb;
                var r = new OcclusionResult();
                try
                {
                    r.HiddenTriangles = new NativeArray<bool>(tc, allocator);
                    r.CoveredVertices = new NativeArray<bool>(vc, allocator);
                    r.FrontCovered = new NativeArray<bool>(vc, allocator);
                    r.CoveredBy = new NativeArray<ulong>(vc, allocator);
                    r.InteriorCoveredBy = new NativeArray<ulong>(tc, allocator);
                }
                catch
                {
                    r.Dispose();
                    throw;
                }
                NativeArray<bool>.Copy(Candidate, tb, r.HiddenTriangles, 0, tc);
                // What covered each sample: its rest witness, replaced by a deletion re-cast, and every earlier one kept beside it.
                ulong CoveredBy(int s) => Witness[s] | FrameWitness[s];
                for (var t = 0; t < tc; t++)
                {
                    ulong by = 0;
                    for (var s = TriSampleStart[tb + t]; s < TriSampleStart[tb + t] + TriSampleCount[tb + t]; s++) by |= CoveredBy(s);
                    r.InteriorCoveredBy[t] = by;
                }
                var d = new DeclineCounts();
                for (var i = 0; i < vc; i++)
                {
                    var v = vb + i;
                    if (!S.VertCandidate[v]) continue;
                    r.FrontCovered[i] = Covered[v];
                    var back = VertBack[v];
                    r.CoveredBy[i] = CoveredBy(v) | (back >= 0 ? CoveredBy(back) : 0);
                    if (Covered[v] && (back < 0 || Covered[back]))
                    {
                        r.CoveredVertices[i] = true;
                        continue;
                    }
                    // The front's failure if it failed, else the back's.
                    var s = Covered[v] ? back : v;
                    switch (SampleReason[s])
                    {
                        case Reason.Unweighted: d.Unweighted++; break;
                        case Reason.Swings: d.Swings++; break;
                        case Reason.SeeThrough: d.SeeThrough++; break;
                        default: d.Escaped++; break;
                    }
                    if ((SampleFlags[s] & ExposedByFrame) != 0) d.ExposedByFrame++;
                    if ((SampleFlags[s] & ExposedByDeletion) != 0) d.ExposedByDeletion++;
                }
                r.Declined = d;
                return r;
            }

            public void Dispose()
            {
                void D<T>(ref NativeArray<T> x) where T : struct { if (x.IsCreated) x.Dispose(); }
                void L<T>(ref NativeList<T> x) where T : unmanaged { if (x.IsCreated) x.Dispose(); }
                D(ref Cone); D(ref VertBack); D(ref TriSampleStart); D(ref TriSampleCount); D(ref TriSubdivision); D(ref TriVeto); D(ref Disabled); D(ref Moved); D(ref Eligible); D(ref Candidate);
                D(ref EmptyNodes); D(ref EmptyPrims); D(ref EmptyTrees); D(ref DeltaScratch); D(ref NormalScratch); D(ref VertMark); D(ref TriMark); D(ref TriMark2);
                L(ref SamplePos); L(ref SampleNrm); L(ref SampleBary); L(ref SampleMesh); L(ref SampleBoneStart); L(ref SampleBones);
                L(ref SampleState); L(ref SampleBack); L(ref SampleReason); L(ref SampleFlags); L(ref Covered); L(ref Relevant); L(ref SampleMark);
                L(ref Witness); L(ref FrameWitness); L(ref SampleCoveredBy); L(ref SampleStretch); L(ref RelevantList); L(ref Work); L(ref MovedOcc); L(ref FrameTrees); L(ref FrameNodes); L(ref FramePrims);
                if (FrameRays.IsCreated) FrameRays.Dispose();
            }
        }
    }
}
