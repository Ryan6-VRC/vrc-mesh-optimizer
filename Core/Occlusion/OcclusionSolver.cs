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
    /// normal. A ray is blocked only by a face of an always-on renderer other than the sample's own, whose
    /// weight share on the sample's bones, widened by kin steps, reaches the share threshold, and which
    /// renders opaque where the ray meets it; every other face is transparent. A sample is covered when
    /// every ray is blocked, and a triangle is hidden when all its samples are covered. Poses decide nothing:
    /// geometry on the same bones moves together, so what hides a surface at rest hides it in every pose.
    ///
    /// Shape keys: coverage holds at rest and in each live frame on its own at its driven maximum, and a
    /// frame is evaluated only where it can change a verdict (see <see cref="FramePrepJob"/>).
    ///
    /// Deletion: a triangle hidden only by another hidden triangle would be exposed when both go. So after
    /// the first verdict, the hidden occluder faces are removed and every hidden candidate whose cover rests
    /// on a mesh that lost faces is re-evaluated, live frames included; only what stays covered is hidden.
    /// Each hidden triangle is then covered by faces that are kept.
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
            for (var m = 0; m < meshes.Count; m++) results[m] = run.Result(m, allocator);
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
            public NativeArray<BoneInfluence> Weights;
            public NativeArray<int3> Tris;
            public NativeArray<int> OccTri, OccMesh, MeshOccStart, MeshRoot, MeshOrder, BoneParent;
            public NativeArray<OccMaterial> OccMat;
            public NativeArray<AlphaInfo> Alpha;
            public NativeArray<byte> Texels;
            public NativeList<BvhNode> Nodes;
            public NativeArray<BvhTri> Prims;
            public NativeArray<ShapeDelta> Deltas;
            public readonly List<(int mesh, int start, int count)> Frames = new List<(int, int, int)>();
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
                    vertBase[m + 1] = vertBase[m] + v;
                    triBase[m + 1] = triBase[m] + t;
                    weightCount += e.Weights.Length;
                    if (e.Frames.IsCreated) frameCount[m] = e.Frames.Length;
                    if (e.Deltas.IsCreated) deltaCount += e.Deltas.Length;
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
                    }
                    for (var k = 0; k < e.Weights.Length; k++)
                        if ((uint)e.Weights[k].Bone >= (uint)boneCount)
                            throw new ArgumentException($"mesh {m}: an influence names bone {e.Weights[k].Bone}, outside the table of {boneCount}");
                    w += e.Weights.Length;
                    WeightStart[vb + v] = w;

                    var subCount = om.Submeshes?.Length ?? 0;
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
                        mats.Add(new OccMaterial { Mode = sub?.Mode ?? OccluderMode.Auto, Culling = sub?.Culling ?? FaceCulling.Back, Alpha = alpha });
                    }

                    for (var t = 0; t < e.TriangleCount; t++)
                    {
                        var tri = e.Triangles[t];
                        if (math.any(tri < 0) || math.any(tri >= v)) throw new ArgumentException($"mesh {m}: triangle {t} names a vertex outside 0..{v - 1}");
                        Tris[tb + t] = tri + vb;
                        TriMesh[tb + t] = m;
                    }

                    if (e.Frames.IsCreated)
                        for (var f = 0; f < e.Frames.Length; f++)
                        {
                            var fr = e.Frames[f];
                            if (fr.DeltaStart < 0 || fr.DeltaCount < 0 || fr.DeltaStart + fr.DeltaCount > (e.Deltas.IsCreated ? e.Deltas.Length : 0))
                                throw new ArgumentException($"mesh {m}: frame {f} runs outside Deltas");
                            Frames.Add((m, d + fr.DeltaStart, fr.DeltaCount));
                        }
                    if (e.Deltas.IsCreated)
                        for (var k = 0; k < e.Deltas.Length; k++)
                        {
                            var sd = e.Deltas[k];
                            if ((uint)sd.Vertex >= (uint)v) throw new ArgumentException($"mesh {m}: a shape delta names vertex {sd.Vertex}, outside 0..{v - 1}");
                            Deltas[d + k] = new ShapeDelta { Vertex = sd.Vertex + vb, Delta = sd.Delta };
                        }
                    d += e.Deltas.IsCreated ? e.Deltas.Length : 0;
                }

                // Occluders: faces of always-on renderers, less any submesh that never occludes and any face
                // that can vanish with a bone.
                var vanishes = bones.Vanishes;
                if (vanishes.IsCreated && vanishes.Length != boneCount) throw new ArgumentException($"bones.Vanishes has {vanishes.Length} entries for {boneCount} bones");
                var occTri = new List<int>();
                var meshOccStart = new int[meshCount + 1];
                for (var m = 0; m < meshCount; m++)
                {
                    meshOccStart[m] = occTri.Count;
                    var e = meshes[m].Mesh;
                    for (var t = 0; t < e.TriangleCount; t++)
                    {
                        var g = triBase[m] + t;
                        TriOcc[g] = -1;
                        if (!occludes[m]) continue;
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
                D(ref Tris); D(ref TriMesh); D(ref TriOcc); D(ref VTStart); D(ref VTList); D(ref Deltas);
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
            NativeList<int> SampleMesh, SampleBoneStart, SampleBones;
            NativeList<byte> SampleState, SampleReason, SampleFlags;
            NativeList<bool> Covered, Relevant;
            NativeList<ulong> Witness, FrameWitness;
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
            NativeReference<long> FrameRays;
            NativeArray<float3> RiskBox;
            long Rays;
            readonly Stopwatch prepWatch = new Stopwatch();

            public Run(Scene s, OcclusionSettings cfg)
            {
                S = s;
                Cfg = cfg;
                const Allocator a = Allocator.Persistent;
                int V = s.Pos.Length, T = s.Tris.Length, O = math.max(1, s.OccTri.Length);
                Cone = Occlusion.Cone.Directions(cfg.ConeDegrees, cfg.RingDegrees, a);
                SamplePos = new NativeList<float3>(V, a); SampleNrm = new NativeList<float3>(V, a); SampleBary = new NativeList<float3>(V, a);
                SampleMesh = new NativeList<int>(V, a); SampleBoneStart = new NativeList<int>(V + 1, a); SampleBones = new NativeList<int>(4 * V, a);
                SampleState = new NativeList<byte>(V, a);
                SampleReason = new NativeList<byte>(V, a); SampleFlags = new NativeList<byte>(V, a);
                Covered = new NativeList<bool>(V, a); Relevant = new NativeList<bool>(V, a); Witness = new NativeList<ulong>(V, a); FrameWitness = new NativeList<ulong>(V, a);
                RelevantList = new NativeList<int>(V, a);
                TriSampleStart = new NativeArray<int>(T, a); TriSampleCount = new NativeArray<int>(T, a);
                TriSubdivision = new NativeArray<int>(T, a); TriVeto = new NativeArray<bool>(T, a);
                Disabled = new NativeArray<bool>(O, a); Moved = new NativeArray<bool>(O, a);
                Eligible = new NativeArray<bool>(T, a); Candidate = new NativeArray<bool>(T, a);
                EmptyNodes = new NativeArray<BvhNode>(0, a); EmptyPrims = new NativeArray<BvhTri>(0, a);
                DeltaScratch = new NativeArray<float3>(V, a); NormalScratch = new NativeArray<float3>(V, a);
                VertMark = new NativeArray<bool>(V, a); TriMark = new NativeArray<bool>(T, a); TriMark2 = new NativeArray<bool>(T, a);
                Work = new NativeList<FrameWork>(1024, a); MovedOcc = new NativeList<int>(1024, a);
                FrameNodes = new NativeList<BvhNode>(1024, a); FramePrims = new NativeList<BvhTri>(1024, a);
                FrameRays = new NativeReference<long>(a);
                RiskBox = new NativeArray<float3>(2, a);
            }

            public void Execute(ref OcclusionStats stats)
            {
                stats.BuildMs = S.BuildMs;
                stats.OccluderTriangles = S.OccTri.Length;
                stats.BvhNodes = S.Nodes.Length;
                stats.LiveFrames = S.Frames.Count;
                int V = S.Pos.Length, T = S.Tris.Length;

                var sw = Stopwatch.StartNew();
                new VertexSamplesJob
                {
                    Pos = S.Pos, Nrm = S.Nrm, GeoN = S.GeoN, VertMesh = S.VertMesh, WeightStart = S.WeightStart, Weights = S.Weights,
                    MinRawWeight = Cfg.MinRawWeight, MinInfluence = Cfg.MinInfluence,
                    SamplePos = SamplePos, SampleNrm = SampleNrm, SampleMesh = SampleMesh, SampleBoneStart = SampleBoneStart,
                    SampleBones = SampleBones, SampleState = SampleState, SampleBary = SampleBary,
                }.Run();
                ResizeState();
                using (var referenced = new NativeList<int>(V, Allocator.TempJob))
                {
                    for (var v = 0; v < V; v++) if (S.VTStart[v + 1] > S.VTStart[v]) referenced.Add(v);
                    stats.VertexSamples = referenced.Length;
                    RestEval(referenced.AsArray(), 0);
                }
                new InteriorSamplesJob
                {
                    Pos = S.Pos, Tris = S.Tris, TriMesh = S.TriMesh, WeightStart = S.WeightStart, Weights = S.Weights,
                    Covered = Covered.AsArray(), Spacing = Cfg.InteriorSpacing, MinInfluence = Cfg.MinInfluence,
                    TriSampleStart = TriSampleStart, TriSampleCount = TriSampleCount, TriSubdivision = TriSubdivision, TriVeto = TriVeto,
                    SamplePos = SamplePos, SampleNrm = SampleNrm, SampleMesh = SampleMesh, SampleBoneStart = SampleBoneStart,
                    SampleBones = SampleBones, SampleState = SampleState, SampleBary = SampleBary,
                }.Run();
                ResizeState();
                stats.InteriorSamples = SamplePos.Length - V;
                var interior = new NativeArray<int>(SamplePos.Length - V, Allocator.TempJob);
                try
                {
                    for (var i = 0; i < interior.Length; i++) interior[i] = V + i;
                    RestEval(interior, 0);
                }
                finally
                {
                    interior.Dispose();
                }
                for (var t = 0; t < T; t++) Eligible[t] = true;
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
                // pass, every live frame and this pass together: which samples are re-cast, which witnesses
                // survive the re-cast, and which frames are re-run for them. A review found one hole here
                // that no test caught. If it is wrong, the test that breaks it layers three or more covers
                // with some hidden, puts live frames on the kept ones that move them away, orders meshes so
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
                SampleReason.Resize(n, NativeArrayOptions.ClearMemory);
                SampleFlags.Resize(n, NativeArrayOptions.ClearMemory);
                Witness.Resize(n, NativeArrayOptions.ClearMemory);
                FrameWitness.Resize(n, NativeArrayOptions.ClearMemory);
            }

            CoverageKernel Kernel(int frameMesh, NativeArray<BvhNode> frameNodes, NativeArray<BvhTri> framePrims) => new CoverageKernel
            {
                Cone = Cone, BoneParent = S.BoneParent, WeightStart = S.WeightStart, Weights = S.Weights, Tris = S.Tris, Uv = S.Uv,
                OccTri = S.OccTri, OccMat = S.OccMat, Alpha = S.Alpha, Texels = S.Texels,
                Nodes = S.Nodes.AsArray(), Prims = S.Prims, MeshRoot = S.MeshRoot, MeshOrder = S.MeshOrder,
                Disabled = Disabled, Moved = Moved, FrameNodes = frameNodes, FramePrims = framePrims,
                SampleMesh = SampleMesh.AsArray(), SampleBoneStart = SampleBoneStart.AsArray(), SampleBones = SampleBones.AsArray(),
                SampleState = SampleState.AsArray(), FrameMesh = frameMesh,
                Share = Cfg.Share, Reach = Cfg.Reach, OriginOffset = Cfg.OriginOffset, Kin = Cfg.KinSteps,
            };

            void RestEval(NativeArray<int> work, byte exposeFlag)
            {
                if (work.Length == 0) return;
                using var rays = new NativeArray<int>(work.Length, Allocator.TempJob);
                new RestEvalJob
                {
                    K = Kernel(-1, EmptyNodes, EmptyPrims), Work = work, SamplePos = SamplePos.AsArray(), SampleNrm = SampleNrm.AsArray(),
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
                    Tris = S.Tris, Eligible = Eligible, TriVeto = TriVeto, Covered = Covered.AsArray(), TriSampleStart = TriSampleStart,
                    TriSampleCount = TriSampleCount, Candidate = Candidate, Relevant = Relevant.AsArray(), RelevantList = RelevantList,
                }.Run();
            }

            void Frames(byte exposeFlag)
            {
                foreach (var (mesh, start, count) in S.Frames)
                {
                    if (RelevantList.Length == 0) return;
                    prepWatch.Start();
                    new FramePrepJob
                    {
                        Mesh = mesh, Spacing = Cfg.InteriorSpacing, DeltaStart = start, DeltaCount = count, MeshOccludes = S.MeshRoot[mesh] >= 0,
                        MoveEpsilon = Cfg.MoveEpsilon, Reach = Cfg.Reach, ConeRadians = math.radians(Cfg.ConeDegrees), Split = Cfg.Split,
                        Deltas = S.Deltas, Pos = S.Pos, GeoN = S.GeoN, VTStart = S.VTStart, VTList = S.VTList, Tris = S.Tris, TriOcc = S.TriOcc,
                        TriSampleStart = TriSampleStart, TriSampleCount = TriSampleCount, TriSubdivision = TriSubdivision, TriVeto = TriVeto,
                        SamplePos = SamplePos.AsArray(),
                        SampleNrm = SampleNrm.AsArray(), SampleBary = SampleBary.AsArray(), SampleMesh = SampleMesh.AsArray(),
                        Witness = Witness.AsArray(), Covered = Covered.AsArray(), Relevant = Relevant.AsArray(), RelevantList = RelevantList.AsArray(),
                        DeltaScratch = DeltaScratch, NormalScratch = NormalScratch, VertMark = VertMark, TriMark = TriMark, TriMark2 = TriMark2,
                        Moved = Moved, Work = Work, MovedOcc = MovedOcc, FrameNodes = FrameNodes, FramePrims = FramePrims,
                        RiskBox = RiskBox,
                    }.Run();
                    prepWatch.Stop();
                    var n = Work.Length;
                    using var result = new NativeArray<byte>(n, Allocator.TempJob);
                    using var rays = new NativeArray<int>(n, Allocator.TempJob);
                    using var witness = new NativeArray<ulong>(n, Allocator.TempJob);
                    if (n > 0)
                    {
                        var k = Kernel(mesh, FrameNodes.AsArray(), FramePrims.AsArray());
                        k.RiskMin = RiskBox[0];
                        k.RiskMax = RiskBox[1];
                        new FrameEvalJob { K = k, Work = Work.AsArray(), Result = result, Rays = rays, Witness = witness }.Schedule(n, 16).Complete();
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
                var r = new OcclusionResult
                {
                    HiddenTriangles = new NativeArray<bool>(tc, allocator),
                    CoveredVertices = new NativeArray<bool>(vc, allocator),
                };
                NativeArray<bool>.Copy(Candidate, tb, r.HiddenTriangles, 0, tc);
                var d = new DeclineCounts();
                for (var i = 0; i < vc; i++)
                {
                    var v = vb + i;
                    if (S.VTStart[v + 1] == S.VTStart[v]) continue;
                    if (Covered[v])
                    {
                        r.CoveredVertices[i] = true;
                        continue;
                    }
                    switch (SampleReason[v])
                    {
                        case Reason.Unweighted: d.Unweighted++; break;
                        case Reason.Swings: d.Swings++; break;
                        case Reason.SeeThrough: d.SeeThrough++; break;
                        default: d.Escaped++; break;
                    }
                    if ((SampleFlags[v] & ExposedByFrame) != 0) d.ExposedByFrame++;
                    if ((SampleFlags[v] & ExposedByDeletion) != 0) d.ExposedByDeletion++;
                }
                r.Declined = d;
                return r;
            }

            public void Dispose()
            {
                void D<T>(ref NativeArray<T> x) where T : struct { if (x.IsCreated) x.Dispose(); }
                void L<T>(ref NativeList<T> x) where T : unmanaged { if (x.IsCreated) x.Dispose(); }
                D(ref Cone); D(ref TriSampleStart); D(ref TriSampleCount); D(ref TriSubdivision); D(ref TriVeto); D(ref Disabled); D(ref Moved); D(ref Eligible); D(ref Candidate);
                D(ref EmptyNodes); D(ref EmptyPrims); D(ref RiskBox); D(ref DeltaScratch); D(ref NormalScratch); D(ref VertMark); D(ref TriMark); D(ref TriMark2);
                L(ref SamplePos); L(ref SampleNrm); L(ref SampleBary); L(ref SampleMesh); L(ref SampleBoneStart); L(ref SampleBones);
                L(ref SampleState); L(ref SampleReason); L(ref SampleFlags); L(ref Covered); L(ref Relevant);
                L(ref Witness); L(ref FrameWitness); L(ref RelevantList); L(ref Work); L(ref MovedOcc); L(ref FrameNodes); L(ref FramePrims);
                if (FrameRays.IsCreated) FrameRays.Dispose();
            }
        }
    }
}
