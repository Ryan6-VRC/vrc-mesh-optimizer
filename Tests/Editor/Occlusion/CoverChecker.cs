using System;
using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    /// <summary>
    /// An independent statement of what the solver owes: every triangle it marks hidden is covered, at rest
    /// and in each configuration of the avatar, by faces that are kept. A configuration is one frame alone, or
    /// every frame that carries one nonzero configuration id, each on its own mesh, moved together. Brute force in double precision against
    /// every kept occluder face, with nothing of the solver's but its input types and the rule's definition:
    /// no tree, no witness record, no risk filter, no deletion bookkeeping, and its own cone, kin walk,
    /// face share, culling and alpha footprint.
    ///
    /// The rule it applies, per sample of a hidden triangle (its corners, and the lattice the rule samples
    /// inside a large one): the sample is weighted, and every ray of the cone about its geometric normal, on
    /// the side it renders, is
    /// blocked by a kept face of another always-on renderer, one its mesh lets block, that holds the share on the sample's kin bones
    /// and renders opaque where the ray meets it. A triangle whose material renders its back is owed cover on
    /// that side too. It is lenient in the solver's favour only by float noise (<see cref="EdgeSlack"/>), so
    /// two implementations' rounding never reads as a failure.
    /// </summary>
    internal static class CoverChecker
    {
        /// <summary>A hit this far outside a face, in barycentric terms, still blocks.</summary>
        const double EdgeSlack = 1e-7;

        public sealed class Report
        {
            public readonly List<string> Violations = new List<string>();
            public int HiddenChecked;
            public int SamplesChecked;
            public long RaysCast;
            public bool Ok => Violations.Count == 0;
            public override string ToString() => Ok ? $"ok ({HiddenChecked} hidden triangles, {SamplesChecked} samples, {RaysCast} rays)" : string.Join("\n", Violations);
        }

        sealed class MeshData
        {
            public double3[] Pos;
            public double3[] Nrm;          // unit: the faces' area-weighted normal, or the authored one where they have no area
            public (int bone, float w)[][] Weights;
            public int3[] Tris;
            public int[] Sub;
            public float2[] Uv;
            public List<Dictionary<int, double3>> Frames = new List<Dictionary<int, double3>>();
            public List<int> FrameIds = new List<int>();
            public bool Occludes;
            /// <summary>Per triangle, whether it may block; null for every triangle.</summary>
            public bool[] Blocks;
            public OccluderSubmesh[] Submeshes;
            public List<int>[] VertTris;
        }

        struct Face
        {
            public double3 A, B, C, Centre;
            public double Radius;
            public int Mesh, Tri;
        }

        /// <summary>
        /// With <paramref name="setback"/>, per mesh in metres, the rule a re-judge asks: a face of mesh b also blocks a ray
        /// behind a sample of mesh a, back along the ray's line to setback[b] below the sample's plane measured along its
        /// normal (and at most the reach), when <paramref name="behind"/>[a] names b by bit min(b, 63); null names every mesh.
        /// </summary>
        public static Report Check(IReadOnlyList<OcclusionMesh> meshes, int[] parents, bool[] vanishes,
                                   OcclusionSettings s, OcclusionResult[] results, int maxViolations = 8,
                                   float[] setback = null, ulong[] behind = null)
        {
            var c = new Checker(meshes, parents, vanishes, s, results, maxViolations) { Setback = setback, Behind = behind };
            c.Run();
            return c.R;
        }

        sealed class Checker
        {
            readonly MeshData[] M;
            readonly int[] parents;
            readonly List<int>[] neighbours;
            readonly bool[] vanishes;
            readonly OcclusionSettings s;
            readonly OcclusionResult[] results;
            readonly int maxViolations;
            readonly double3[] cone;
            public float[] Setback;
            public ulong[] Behind;
            public readonly Report R = new Report();

            public Checker(IReadOnlyList<OcclusionMesh> meshes, int[] parents, bool[] vanishes, OcclusionSettings s,
                           OcclusionResult[] results, int maxViolations)
            {
                this.parents = parents;
                this.vanishes = vanishes;
                this.s = s;
                this.results = results;
                this.maxViolations = maxViolations;
                neighbours = new List<int>[parents.Length];
                for (var b = 0; b < parents.Length; b++) neighbours[b] = new List<int>();
                for (var b = 0; b < parents.Length; b++)
                    if (parents[b] >= 0) { neighbours[b].Add(parents[b]); neighbours[parents[b]].Add(b); }
                M = new MeshData[meshes.Count];
                for (var m = 0; m < meshes.Count; m++) M[m] = Load(meshes[m]);
                cone = ConeDirections(s.ConeDegrees, s.RingDegrees);
            }

            static MeshData Load(OcclusionMesh om)
            {
                var e = om.Mesh;
                var d = new MeshData { Occludes = om.Occludes, Blocks = om.Blocks, Submeshes = om.Submeshes };
                var n = e.Positions.Length;
                d.Pos = new double3[n];
                d.Nrm = new double3[n];
                d.Uv = new float2[n];
                d.Weights = new (int, float)[n][];
                for (var v = 0; v < n; v++)
                {
                    d.Pos[v] = e.Positions[v];
                    var nn = (double3)e.Normals[v];
                    d.Nrm[v] = math.lengthsq(nn) > 1e-12 ? math.normalize(nn) : double3.zero;
                    d.Uv[v] = e.Uv0.IsCreated ? e.Uv0[v] : float2.zero;
                    var w = new List<(int, float)>();
                    for (var k = e.WeightStart[v]; k < e.WeightStart[v + 1]; k++) w.Add((e.Weights[k].Bone, e.Weights[k].Weight));
                    d.Weights[v] = w.ToArray();
                }
                d.Tris = e.Triangles.ToArray();
                d.Sub = e.TriangleSubmesh.IsCreated ? e.TriangleSubmesh.ToArray() : new int[d.Tris.Length];
                d.VertTris = new List<int>[n];
                for (var v = 0; v < n; v++) d.VertTris[v] = new List<int>();
                for (var t = 0; t < d.Tris.Length; t++)
                    for (var k = 0; k < 3; k++) if (!d.VertTris[d.Tris[t][k]].Contains(t)) d.VertTris[d.Tris[t][k]].Add(t);
                for (var v = 0; v < n; v++)
                {
                    var g = double3.zero;
                    foreach (var t in d.VertTris[v])
                    {
                        var tri = d.Tris[t];
                        g += math.cross(d.Pos[tri.y] - d.Pos[tri.x], d.Pos[tri.z] - d.Pos[tri.x]);
                    }
                    if (math.lengthsq(g) > 1e-30) d.Nrm[v] = math.normalize(g);
                }
                if (e.Frames.IsCreated)
                    foreach (var f in e.Frames)
                    {
                        var map = new Dictionary<int, double3>();
                        for (var k = f.DeltaStart; k < f.DeltaStart + f.DeltaCount; k++)
                        {
                            var sd = e.Deltas[k];
                            // A delta under the move epsilon does not move its vertex, by the settings' definition.
                            map[sd.Vertex] = sd.Delta;
                        }
                        d.Frames.Add(map);
                        d.FrameIds.Add(f.Configuration);
                    }
                return d;
            }

            OccluderSubmesh Sub(MeshData d, int t)
            {
                var i = d.Sub[t];
                return d.Submeshes != null && i < d.Submeshes.Length ? d.Submeshes[i] : null;
            }

            // --- one configuration: the rest state, one mesh's one frame, or the frames of one id -----------

            // The kept occluder faces, bucketed in a hashed grid so a ray tests the faces along it rather than
            // all of them. A face sits in every cell its bounds, padded, touch; a fault here can only drop a
            // face from a ray, which reads as a violation, never hides one.
            sealed class Config
            {
                public string Name;
                public double3[][] Pos;
                public double3[][] Nrm;
                /// <summary>The configuration moves some vertex, and some kept occluder face with it.</summary>
                public bool Moves, FacesMove;
                /// <summary>Per mesh: one of its frames is part of the configuration.</summary>
                public bool[] InFrame;
                public Face[] Faces;
                public double Cell;
                public Dictionary<long, List<int>> Grid;
                public int[] Stamp;
                public int Ray;
            }

            static long Key(long x, long y, long z) => ((x + (1L << 20)) << 42) | ((y + (1L << 20)) << 21) | (z + (1L << 20));

            // frameOf[m] is the frame mesh m takes in the configuration, or -1 for rest.
            Config Build(string name, int[] frameOf)
            {
                var c = new Config
                {
                    Name = name,
                    Pos = new double3[M.Length][],
                    Nrm = new double3[M.Length][],
                    InFrame = new bool[M.Length],
                };
                var faces = new List<Face>();
                for (var m = 0; m < M.Length; m++)
                {
                    var d = M[m];
                    c.Pos[m] = d.Pos;
                    c.Nrm[m] = d.Nrm;
                    if (frameOf[m] >= 0)
                    {
                        c.InFrame[m] = true;
                        var p = (double3[])d.Pos.Clone();
                        foreach (var kv in d.Frames[frameOf[m]])
                            if (math.length(kv.Value) > s.MoveEpsilon)
                            {
                                p[kv.Key] = d.Pos[kv.Key] + kv.Value;
                                c.Moves = true;
                            }
                        c.Pos[m] = p;
                        c.Nrm[m] = FrameNormals(d, p);
                    }
                    if (d.Occludes)
                        for (var t = 0; t < d.Tris.Length; t++)
                        {
                            if (results[m].HiddenTriangles[t] || d.Blocks != null && !d.Blocks[t]) continue;
                            var sub = Sub(d, t);
                            if (sub != null && sub.Mode == OccluderMode.NeverOccludes) continue;
                            if (TouchesVanishing(d, d.Tris[t])) continue;
                            var tri = d.Tris[t];
                            double3 a = c.Pos[m][tri.x], b = c.Pos[m][tri.y], cc = c.Pos[m][tri.z];
                            if (c.InFrame[m] && (math.any(a != d.Pos[tri.x]) || math.any(b != d.Pos[tri.y]) || math.any(cc != d.Pos[tri.z]))) c.FacesMove = true;
                            var centre = (a + b + cc) / 3.0;
                            var r = math.sqrt(math.max(math.distancesq(centre, a), math.max(math.distancesq(centre, b), math.distancesq(centre, cc))));
                            faces.Add(new Face { A = a, B = b, C = cc, Centre = centre, Radius = r * (1 + 1e-6) + 1e-9, Mesh = m, Tri = t });
                        }
                }
                c.Faces = faces.ToArray();
                c.Stamp = new int[c.Faces.Length];
                double size = 0;
                foreach (var f in c.Faces) size += 2 * f.Radius;
                c.Cell = math.clamp(c.Faces.Length > 0 ? size / c.Faces.Length : 0.01, 0.004, 0.05);
                c.Grid = new Dictionary<long, List<int>>();
                for (var i = 0; i < c.Faces.Length; i++)
                {
                    var f = c.Faces[i];
                    var pad = 1e-4 + EdgeSlack * 2 * f.Radius;
                    var lo = (int3)math.floor((math.min(f.A, math.min(f.B, f.C)) - pad) / c.Cell);
                    var hi = (int3)math.floor((math.max(f.A, math.max(f.B, f.C)) + pad) / c.Cell);
                    for (var x = lo.x; x <= hi.x; x++)
                    for (var y = lo.y; y <= hi.y; y++)
                    for (var z = lo.z; z <= hi.z; z++)
                    {
                        var k = Key(x, y, z);
                        if (!c.Grid.TryGetValue(k, out var list)) c.Grid[k] = list = new List<int>();
                        list.Add(i);
                    }
                }
                return c;
            }

            bool TouchesVanishing(MeshData d, int3 tri)
            {
                if (vanishes == null) return false;
                for (var k = 0; k < 3; k++)
                    foreach (var (bone, w) in d.Weights[tri[k]])
                        if (w > 0f && vanishes[bone]) return true;
                return false;
            }

            // An authored normal turns with its vertex's area-weighted face normal, the rotation taking the rest
            // one onto the frame's, so a frame that tilts a surface tilts its cone.
            double3[] FrameNormals(MeshData d, double3[] p)
            {
                var n = (double3[])d.Nrm.Clone();
                for (var v = 0; v < p.Length; v++)
                {
                    if (d.VertTris[v].Count == 0) continue;
                    double3 from = double3.zero, to = double3.zero;
                    foreach (var t in d.VertTris[v])
                    {
                        var tri = d.Tris[t];
                        from += math.cross(d.Pos[tri.y] - d.Pos[tri.x], d.Pos[tri.z] - d.Pos[tri.x]);
                        to += math.cross(p[tri.y] - p[tri.x], p[tri.z] - p[tri.x]);
                    }
                    var rest = math.lengthsq(d.Nrm[v]) > 0 ? d.Nrm[v] : SafeNormalize(from);
                    n[v] = Turn(rest, from, to);
                }
                return n;
            }

            static double3 SafeNormalize(double3 x) => math.lengthsq(x) > 1e-24 ? math.normalize(x) : double3.zero;

            static double3 Turn(double3 n, double3 from, double3 to)
            {
                if (math.lengthsq(to) <= 1e-30) return n;
                if (math.lengthsq(from) <= 1e-30) return math.normalize(to);
                var a = math.normalize(from);
                var b = math.normalize(to);
                var cos = math.dot(a, b);
                if (cos > 0.999999) return n;
                if (cos < -0.999) return b;
                var axis = math.normalize(math.cross(a, b));
                var sin = math.sqrt(math.max(0, 1 - cos * cos));
                // Rodrigues.
                var r = n * cos + math.cross(axis, n) * sin + axis * math.dot(axis, n) * (1 - cos);
                return math.normalize(r);
            }

            // --- the pass --------------------------------------------------------------------------------

            // Rest, named null; then each frame of id zero on its own; then each nonzero id, every mesh taking its
            // frame of that id. A mesh with two frames of one id is outside the contract and fails the check.
            IEnumerable<(string name, int[] frameOf)> Configurations()
            {
                int[] None()
                {
                    var a = new int[M.Length];
                    for (var i = 0; i < a.Length; i++) a[i] = -1;
                    return a;
                }
                yield return (null, None());
                var byId = new SortedDictionary<int, int[]>();
                for (var m = 0; m < M.Length; m++)
                    for (var f = 0; f < M[m].Frames.Count; f++)
                    {
                        var id = M[m].FrameIds[f];
                        if (id == 0)
                        {
                            var alone = None();
                            alone[m] = f;
                            yield return ($"frame {f} of mesh {m}", alone);
                            continue;
                        }
                        if (!byId.TryGetValue(id, out var frameOf)) byId[id] = frameOf = None();
                        if (frameOf[m] >= 0) R.Violations.Add($"mesh {m} carries frames {frameOf[m]} and {f} of configuration {id}");
                        frameOf[m] = f;
                    }
                foreach (var kv in byId) yield return ($"configuration {kv.Key}", kv.Value);
            }

            public void Run()
            {
                foreach (var (name, frameOf) in Configurations())
                {
                    var rest = name == null;
                    var cfg = Build(name ?? "rest", frameOf);
                    // A configuration that moves nothing is the rest state again. One that moves no kept occluder face
                    // leaves every mesh it does not move, and every blocking face, where the rest state has them.
                    if (!rest && !cfg.Moves) continue;
                    for (var m = 0; m < M.Length && R.Violations.Count < maxViolations; m++)
                    {
                        if (!rest && !cfg.InFrame[m] && !cfg.FacesMove) continue;
                        var d = M[m];
                        var vertexCache = new Dictionary<(int, bool), string>();
                        var latticeCache = new Dictionary<(double3, double3, string), string>();
                        for (var t = 0; t < d.Tris.Length && R.Violations.Count < maxViolations; t++)
                        {
                            if (!results[m].HiddenTriangles[t]) continue;
                            if (rest) R.HiddenChecked++;
                            var why = CheckTriangle(cfg, m, t, vertexCache, latticeCache);
                            if (why != null) R.Violations.Add($"mesh {m} triangle {t}, {cfg.Name}: {why}");
                        }
                    }
                }
            }

            string CheckTriangle(Config cfg, int m, int t, Dictionary<(int, bool), string> vertexCache,
                                 Dictionary<(double3, double3, string), string> latticeCache)
            {
                var d = M[m];
                var tri = d.Tris[t];
                var sub = Sub(d, t);
                var culling = sub?.CandidateCulling ?? sub?.Culling ?? FaceCulling.Back;
                var sides = culling == FaceCulling.Back ? new[] { false } : culling == FaceCulling.Front ? new[] { true } : new[] { false, true };

                // The lattice the rule samples inside the triangle, fixed by its rest shape.
                var n = 0;
                float3 ra = (float3)d.Pos[tri.x], rb = (float3)d.Pos[tri.y], rc = (float3)d.Pos[tri.z];
                var longest = math.sqrt(math.max(math.distancesq(ra, rb), math.max(math.distancesq(rb, rc), math.distancesq(rc, ra))));
                if (s.InteriorSpacing > 0f && longest > s.InteriorSpacing)
                {
                    n = math.max(2, (int)math.ceil(longest / s.InteriorSpacing));
                    if (n > 16) return $"hidden, though {longest:F4} m is too large to sample at the spacing";
                }
                if (cfg.InFrame[m] && s.InteriorSpacing > 0f)
                {
                    float3 fa = (float3)cfg.Pos[m][tri.x], fb = (float3)cfg.Pos[m][tri.y], fc = (float3)cfg.Pos[m][tri.z];
                    var fl = math.sqrt(math.max(math.distancesq(fa, fb), math.max(math.distancesq(fb, fc), math.distancesq(fc, fa))));
                    var need = fl > s.InteriorSpacing ? (int)math.ceil(fl / s.InteriorSpacing) : 1;
                    if (need > math.max(1, n)) return $"hidden, though the frame stretches it to {fl:F4} m, past its {n}-step lattice";
                }

                foreach (var back in sides)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var v = tri[k];
                        if (!vertexCache.TryGetValue((v, back), out var why))
                        {
                            why = CheckVertex(cfg, m, v, back);
                            vertexCache[(v, back)] = why;
                        }
                        if (why != null) return $"corner {v}{(back ? " (back side)" : "")}: {why}";
                    }
                    if (n == 0) continue;
                    for (var i = 0; i <= n; i++)
                    for (var j = 0; j <= n - i; j++)
                    {
                        var kk = n - i - j;
                        if (i == n || j == n || kk == n) continue;
                        var bary = new double3(i, j, kk) / n;
                        var why = CheckInterior(cfg, m, t, bary, back, latticeCache);
                        if (why != null) return $"lattice point {bary}{(back ? " (back side)" : "")}: {why}";
                    }
                }
                return null;
            }

            string CheckVertex(Config cfg, int m, int v, bool back)
            {
                var d = M[m];
                float raw = 0f;
                var bones = new List<int>();
                foreach (var (bone, w) in d.Weights[v])
                {
                    if (w <= 0f) continue;
                    raw += w;
                    if (w >= s.MinInfluence && bones.Count < 8) bones.Add(bone);
                }
                if (raw < s.MinRawWeight || bones.Count == 0) return "unweighted";
                var nrm = cfg.Nrm[m][v];
                if (math.lengthsq(nrm) < 1e-12)
                {
                    double3 g = double3.zero;
                    foreach (var t in d.VertTris[v])
                    {
                        var tri = d.Tris[t];
                        g += math.cross(cfg.Pos[m][tri.y] - cfg.Pos[m][tri.x], cfg.Pos[m][tri.z] - cfg.Pos[m][tri.x]);
                    }
                    nrm = SafeNormalize(g);
                    if (math.lengthsq(nrm) == 0) return "no usable normal";
                }
                return CheckSample(cfg, m, cfg.Pos[m][v], back ? -nrm : nrm, Allowed(bones));
            }

            // A lattice point on an edge is the same point, facing the same way on the same bones, for both triangles on the edge.
            string CheckInterior(Config cfg, int m, int t, double3 bary, bool back, Dictionary<(double3, double3, string), string> cache)
            {
                var d = M[m];
                var tri = d.Tris[t];
                var p = bary.x * cfg.Pos[m][tri.x] + bary.y * cfg.Pos[m][tri.y] + bary.z * cfg.Pos[m][tri.z];
                var nrm = bary.x * cfg.Nrm[m][tri.x] + bary.y * cfg.Nrm[m][tri.y] + bary.z * cfg.Nrm[m][tri.z];
                if (math.lengthsq(nrm) <= 1e-12)
                    nrm = math.cross(cfg.Pos[m][tri.y] - cfg.Pos[m][tri.x], cfg.Pos[m][tri.z] - cfg.Pos[m][tri.x]);
                if (math.lengthsq(nrm) <= 1e-24) return "no usable normal";
                nrm = math.normalize(nrm);
                var blend = new Dictionary<int, double>();
                for (var k = 0; k < 3; k++)
                    foreach (var (bone, w) in d.Weights[tri[k]])
                    {
                        if (w <= 0f) continue;
                        blend.TryGetValue(bone, out var have);
                        blend[bone] = have + w * bary[k];
                    }
                var bones = new List<int>();
                var sorted = new List<KeyValuePair<int, double>>(blend);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in sorted)
                    if (kv.Value >= s.MinInfluence && bones.Count < 8) bones.Add(kv.Key);
                if (bones.Count == 0) return "unweighted";
                if (back) nrm = -nrm;
                var key = (p, nrm, string.Join(",", bones));
                if (cache.TryGetValue(key, out var known)) return known;
                var why = CheckSample(cfg, m, p, nrm, Allowed(bones));
                cache[key] = why;
                return why;
            }

            HashSet<int> Allowed(List<int> bones)
            {
                // Breadth-first over parent-or-child links, KinSteps deep.
                var seen = new HashSet<int>(bones);
                var frontier = new List<int>(bones);
                for (var step = 0; step < s.KinSteps; step++)
                {
                    var next = new List<int>();
                    foreach (var b in frontier)
                        foreach (var nb in neighbours[b])
                            if (seen.Add(nb)) next.Add(nb);
                    frontier = next;
                }
                return seen;
            }

            string CheckSample(Config cfg, int m, double3 p, double3 n, HashSet<int> allowed)
            {
                R.SamplesChecked++;
                var up = math.abs(n.z) < 0.9 ? new double3(0, 0, 1) : new double3(1, 0, 0);
                var u = math.normalize(math.cross(n, up));
                var w = math.cross(n, u);
                var o = p + n * s.OriginOffset;
                // Behind the sample, per mesh: how far back along a ray of unit slant its faces count.
                var depth = new double[M.Length];
                var deepest = 0.0;
                for (var b = 0; b < M.Length && Setback != null; b++)
                {
                    if (Behind != null && (Behind[m] & (1UL << Math.Min(b, 63))) == 0) continue;
                    depth[b] = Setback[b];
                    deepest = Math.Max(deepest, depth[b]);
                }
                foreach (var c in cone)
                {
                    var dir = math.normalize(u * c.x + w * c.y + n * c.z);
                    R.RaysCast++;
                    var back = deepest > 0 && c.z > 0 ? Math.Min(deepest / c.z, s.Reach) : 0.0;
                    if (!Blocked(cfg, m, o - dir * back, dir, allowed, back, depth, c.z))
                        return $"the ray {dir} from {o} meets no kept co-moving opaque face";
                }
                return null;
            }

            bool Blocked(Config cfg, int own, double3 o, double3 dir, HashSet<int> allowed, double back, double[] depth, double slant)
            {
                var reach = (s.Reach + back) * (1 + 1e-6) + 1e-9;
                var h = cfg.Cell;
                cfg.Ray++;
                // Amanatides-Woo: every cell the ray passes through, nearest first, out to reach.
                var cell = (int3)math.floor(o / h);
                var step = new int3(dir.x >= 0 ? 1 : -1, dir.y >= 0 ? 1 : -1, dir.z >= 0 ? 1 : -1);
                double3 tMax = default, tDelta = default;
                for (var a = 0; a < 3; a++)
                {
                    if (math.abs(dir[a]) < 1e-15) { tMax[a] = double.PositiveInfinity; tDelta[a] = double.PositiveInfinity; continue; }
                    var boundary = (cell[a] + (step[a] > 0 ? 1 : 0)) * h;
                    tMax[a] = (boundary - o[a]) / dir[a];
                    tDelta[a] = h / math.abs(dir[a]);
                }
                while (true)
                {
                    if (cfg.Grid.TryGetValue(Key(cell.x, cell.y, cell.z), out var list))
                        foreach (var i in list)
                        {
                            if (cfg.Stamp[i] == cfg.Ray) continue;
                            cfg.Stamp[i] = cfg.Ray;
                            var f = cfg.Faces[i];
                            var from = back > 0 ? back - Math.Min(depth[f.Mesh] / slant, back) : 0.0;
                            if (BlocksRay(f, own, o, dir, from, reach, allowed)) return true;
                        }
                    var axis = tMax.x <= tMax.y && tMax.x <= tMax.z ? 0 : tMax.y <= tMax.z ? 1 : 2;
                    if (tMax[axis] > reach) return false;
                    cell[axis] += step[axis];
                    tMax[axis] += tDelta[axis];
                }
            }

            bool BlocksRay(in Face f, int own, double3 o, double3 dir, double from, double reach, HashSet<int> allowed)
            {
                if (f.Mesh == own) return false;
                var m = f.Mesh;
                if (!Hit(f, o, dir, from, reach, out var bu, out var bv)) return false;
                if (Share(M[m], M[m].Tris[f.Tri], allowed) < s.Share - 1e-6) return false;
                var sub = Sub(M[m], f.Tri);
                var facing = math.dot(dir, math.cross(f.B - f.A, f.C - f.A));
                var culling = sub?.Culling ?? FaceCulling.Back;
                var edgeOn = math.abs(facing) <= 1e-12 * math.lengthsq(f.B - f.A + f.C - f.A);
                if (!edgeOn && (culling == FaceCulling.Back && facing < 0 || culling == FaceCulling.Front && facing > 0)) return false;
                if (sub != null && sub.Mode == OccluderMode.Auto && sub.Alpha != null && SeeThrough(M[m], f.Tri, sub.Alpha, bu, bv)) return false;
                return true;
            }

            static bool Hit(in Face f, double3 o, double3 d, double from, double reach, out double u, out double v)
            {
                u = v = 0;
                var e1 = f.B - f.A;
                var e2 = f.C - f.A;
                var pv = math.cross(d, e2);
                var det = math.dot(e1, pv);
                if (math.abs(det) < 1e-24) return false;
                var inv = 1.0 / det;
                var tv = o - f.A;
                u = math.dot(tv, pv) * inv;
                if (u < -EdgeSlack || u > 1 + EdgeSlack) return false;
                var qv = math.cross(tv, e1);
                v = math.dot(d, qv) * inv;
                if (v < -EdgeSlack || u + v > 1 + EdgeSlack) return false;
                var t = math.dot(e2, qv) * inv;
                return t > from && t <= reach;
            }

            float Share(MeshData d, int3 tri, HashSet<int> allowed)
            {
                double total = 0;
                var weighted = 0;
                for (var k = 0; k < 3; k++)
                {
                    double sum = 0, inSet = 0;
                    foreach (var (bone, w) in d.Weights[tri[k]])
                    {
                        if (w <= 0f) continue;
                        sum += w;
                        if (allowed.Contains(bone)) inSet += w;
                    }
                    if (sum <= 0) continue;
                    total += inSet / sum;
                    weighted++;
                }
                return weighted > 0 ? (float)(total / weighted) : 0f;
            }

            static bool SeeThrough(MeshData d, int t, AlphaMap map, double bu, double bv)
            {
                var tri = d.Tris[t];
                var uv = (float)(1 - bu - bv) * d.Uv[tri.x] + (float)bu * d.Uv[tri.y] + (float)bv * d.Uv[tri.z];
                uv = uv * map.ScaleOffset.xy + map.ScaleOffset.zw;
                if (map.WrapU == AlphaWrap.Repeat) uv.x -= math.floor(uv.x);
                if (map.WrapV == AlphaWrap.Repeat) uv.y -= math.floor(uv.y);
                var x = uv.x * map.Width - 0.5f;
                var y = uv.y * map.Height - 0.5f;
                var x0 = (int)math.floor(x);
                var y0 = (int)math.floor(y);
                var threshold = (int)math.ceil(math.saturate(map.Threshold) * 255f);
                for (var dy = 0; dy <= 1; dy++)
                for (var dx = 0; dx <= 1; dx++)
                {
                    var tx = Texel(x0 + dx, map.Width, map.WrapU);
                    var ty = Texel(y0 + dy, map.Height, map.WrapV);
                    if (map.Alpha[ty * map.Width + tx] < threshold) return true;
                }
                return false;
            }

            // Unity's TextureWrapMode, per axis: Repeat tiles; Clamp holds the edge texel; Mirror tiles with every
            // other tile flipped; MirrorOnce reads the absolute coordinate, clamped.
            static int Texel(int i, int n, AlphaWrap mode)
            {
                var tile = (int)Math.Floor(i / (double)n);
                var within = i - tile * n;
                switch (mode)
                {
                    case AlphaWrap.Clamp: return Math.Min(Math.Max(i, 0), n - 1);
                    case AlphaWrap.Mirror: return (tile & 1) == 0 ? within : n - 1 - within;
                    case AlphaWrap.MirrorOnce: return Math.Min(i < 0 ? -1 - i : i, n - 1);
                    default: return within;
                }
            }
        }

        /// <summary>The cone as the rule defines it: the axis, then a ring per step out to the half-angle, 8 to 24 rays a ring by circumference.</summary>
        static double3[] ConeDirections(float coneDeg, float ringDeg)
        {
            var rings = Math.Max(1, (int)Math.Round(coneDeg / (double)ringDeg));
            var step = coneDeg * Math.PI / 180.0 / rings;
            var dirs = new List<double3> { new double3(0, 0, 1) };
            for (var r = 1; r <= rings; r++)
            {
                var theta = step * r;
                var az = Math.Min(24, Math.Max(8, (int)Math.Round(8.0 * Math.Sin(theta) / Math.Sin(step))));
                for (var a = 0; a < az; a++)
                {
                    var phi = 2.0 * Math.PI * a / az;
                    dirs.Add(new double3(Math.Cos(phi) * Math.Sin(theta), Math.Sin(phi) * Math.Sin(theta), Math.Cos(theta)));
                }
            }
            return dirs.ToArray();
        }
    }
}
