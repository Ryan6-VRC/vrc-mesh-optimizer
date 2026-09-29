using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Topology;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>
    /// The collapse loop: serial, because each collapse changes its neighbours' costs. Holds only the job's
    /// inputs and outputs; the working state lives in <see cref="CollapseEngine"/>, built inside Execute.
    /// </summary>
    [BurstCompile]
    internal struct CollapseJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float2> Uv0;
        [ReadOnly] public NativeArray<int> WeightStart;
        [ReadOnly] public NativeArray<BoneInfluence> Weights;
        [ReadOnly] public NativeArray<int> Origin;
        [ReadOnly] public NativeArray<int> PositionId;
        [ReadOnly] public NativeArray<int> Partner;
        [ReadOnly] public NativeArray<VertexKind> Kind;
        [ReadOnly] public NativeArray<int> DeltaStart;
        [ReadOnly] public NativeArray<int> DeltaFrame;
        [ReadOnly] public NativeArray<float3> DeltaValue;
        [ReadOnly] public NativeArray<byte> ProtectLocked;
        [ReadOnly] public NativeArray<float> ProtectMultiplier;
        [ReadOnly] public NativeArray<float3> NormalRest;
        [ReadOnly] public NativeArray<int> NormalStart;
        [ReadOnly] public NativeArray<int> NormalCount;
        [ReadOnly] public NativeArray<FrameNormal> NormalFrames;

        public NativeArray<int3> Triangles;
        public NativeArray<Quadric> Rest;
        public NativeArray<UvQuadric> Uv;
        public NativeArray<int> SparseStart;
        public NativeArray<int> SparseCount;
        public NativeList<FrameQuadric> Sparse;
        public int LiveTriangles;
        public DecimationSettings Settings;

        public NativeList<CollapseStep> Steps;
        public NativeList<int2> Records;
        /// <summary>[0] peak frame-quadric pool entries, [1] clusters left blocked by validity.</summary>
        public NativeArray<long> Stats;

        public void Execute()
        {
            var engine = new CollapseEngine(ref this);
            engine.Run(ref this);
        }
    }

    internal struct Candidate
    {
        public int U1, V1, U2, V2;
        public float Cost;
        public float Key;
    }

    internal struct CollapseEngine
    {
        // Inputs (views).
        NativeArray<float3> _positions;
        NativeArray<float2> _uv0;
        NativeArray<int> _weightStart;
        NativeArray<BoneInfluence> _weights;
        NativeArray<int> _origin;
        NativeArray<int> _positionId;
        NativeArray<int> _partner;
        NativeArray<VertexKind> _kind;
        NativeArray<int> _deltaStart;
        NativeArray<int> _deltaFrame;
        NativeArray<float3> _deltaValue;
        NativeArray<int3> _tri;
        NativeArray<Quadric> _rest;
        NativeArray<UvQuadric> _uv;
        NativeArray<int> _sparseStart;
        NativeArray<int> _sparseCount;
        NativeList<FrameQuadric> _sparse;
        NativeArray<float3> _normalRest;
        NativeArray<int> _normalStart;
        NativeArray<int> _normalCount;
        NativeArray<FrameNormal> _normalFrames;
        DecimationSettings _s;

        // Working state.
        NativeArray<double3> _p;
        NativeArray<byte> _alive;
        NativeArray<int> _cornerNext;
        NativeArray<int> _firstCorner;
        NativeArray<byte> _dead;
        NativeArray<byte> _removable;
        NativeArray<float> _multiplier;
        NativeArray<int> _sparseCap;
        NativeArray<int> _stamp;
        int _stampValue;
        long _wasted;
        long _peak;

        // Heap over cluster representatives.
        NativeList<int> _heap;
        NativeArray<int> _heapPos;
        NativeArray<float> _heapKey;

        // Scratch.
        NativeList<int> _nexts;
        NativeList<int> _prevs;
        NativeList<Candidate> _cand;
        NativeList<int> _nu;
        NativeList<int> _nv;
        NativeList<int> _opp;
        NativeList<int> _affected;
        NativeList<FrameQuadric> _merged;

        int _live;
        float _level;

        const double TinyWeight = 1e-30;
        const float TieBreak = 1e-4f;

        public CollapseEngine(ref CollapseJob job)
        {
            _positions = job.Positions;
            _uv0 = job.Uv0;
            _weightStart = job.WeightStart;
            _weights = job.Weights;
            _origin = job.Origin;
            _positionId = job.PositionId;
            _partner = job.Partner;
            _kind = job.Kind;
            _deltaStart = job.DeltaStart;
            _deltaFrame = job.DeltaFrame;
            _deltaValue = job.DeltaValue;
            _tri = job.Triangles;
            _rest = job.Rest;
            _uv = job.Uv;
            _sparseStart = job.SparseStart;
            _sparseCount = job.SparseCount;
            _sparse = job.Sparse;
            _normalRest = job.NormalRest;
            _normalStart = job.NormalStart;
            _normalCount = job.NormalCount;
            _normalFrames = job.NormalFrames;
            _s = job.Settings;
            _live = job.LiveTriangles;
            _level = 0f;
            _stampValue = 0;
            _wasted = 0;
            _peak = _sparse.Length;

            var w = _origin.Length;
            var t = _tri.Length;
            _p = new NativeArray<double3>(w, Allocator.Temp);
            _alive = new NativeArray<byte>(t, Allocator.Temp);
            _cornerNext = new NativeArray<int>(t * 3, Allocator.Temp);
            _firstCorner = new NativeArray<int>(w, Allocator.Temp);
            _dead = new NativeArray<byte>(w, Allocator.Temp);
            _removable = new NativeArray<byte>(w, Allocator.Temp);
            _multiplier = new NativeArray<float>(w, Allocator.Temp);
            _sparseCap = new NativeArray<int>(w, Allocator.Temp);
            _stamp = new NativeArray<int>(w, Allocator.Temp);
            _heap = new NativeList<int>(w, Allocator.Temp);
            _heapPos = new NativeArray<int>(w, Allocator.Temp);
            _heapKey = new NativeArray<float>(w, Allocator.Temp);
            _nexts = new NativeList<int>(32, Allocator.Temp);
            _prevs = new NativeList<int>(32, Allocator.Temp);
            _cand = new NativeList<Candidate>(32, Allocator.Temp);
            _nu = new NativeList<int>(64, Allocator.Temp);
            _nv = new NativeList<int>(64, Allocator.Temp);
            _opp = new NativeList<int>(8, Allocator.Temp);
            _affected = new NativeList<int>(64, Allocator.Temp);
            _merged = new NativeList<FrameQuadric>(64, Allocator.Temp);

            for (var i = 0; i < w; i++)
            {
                _p[i] = (double3)_positions[_origin[i]];
                _firstCorner[i] = -1;
                _heapPos[i] = -1;
                _sparseCap[i] = _sparseCount[i];
            }
            for (var i = 0; i < t; i++)
            {
                var tri = _tri[i];
                if (tri.x < 0) continue;
                _alive[i] = 1;
                for (var k = 0; k < 3; k++)
                {
                    var c = i * 3 + k;
                    _cornerNext[c] = _firstCorner[tri[k]];
                    _firstCorner[tri[k]] = c;
                }
            }
            for (var i = 0; i < w; i++)
            {
                var p = _partner[i];
                var locked = _kind[i] == VertexKind.Locked || IsProtected(job.ProtectLocked, i) || IsProtected(job.ProtectLocked, p);
                _removable[i] = (byte)(locked ? 0 : 1);
                _multiplier[i] = math.max(MultiplierOf(job.ProtectMultiplier, i), MultiplierOf(job.ProtectMultiplier, p));
            }
        }

        bool IsProtected(NativeArray<byte> locked, int w) => locked.Length > 0 && locked[_origin[w]] != 0;

        float MultiplierOf(NativeArray<float> mult, int w) => mult.Length > 0 ? math.max(mult[_origin[w]], 0f) : 1f;

        int Rep(int w) => math.min(w, _partner[w]);

        public void Run(ref CollapseJob job)
        {
            for (var w = 0; w < _origin.Length; w++)
            {
                if (Rep(w) != w || _removable[w] == 0) continue;
                if (BestKey(w, out var key)) HeapSet(w, key);
            }

            while (_heap.Length > 0)
            {
                if (_live <= _s.TargetTriangleCount) break;
                var r = HeapPop();
                Candidates(r);
                SortCandidates();
                var found = -1;
                for (var i = 0; i < _cand.Length; i++)
                {
                    if (Valid(_cand[i]))
                    {
                        found = i;
                        break;
                    }
                }
                if (found < 0)
                {
                    job.Stats[1]++;
                    continue; // blocked until its neighbourhood changes
                }
                var c = _cand[found];
                if (_heap.Length > 0 && c.Key > _heapKey[_heap[0]])
                {
                    HeapSet(r, c.Key);
                    continue;
                }
                if (c.Cost > _s.MaxError) break;
                Apply(c, ref job);
            }
            job.Stats[0] = _peak;
        }

        // ---------------------------------------------------------------- candidates and cost

        void Candidates(int r)
        {
            _cand.Clear();
            var u1 = r;
            var u2 = _partner[r] != r ? _partner[r] : -1;
            if (u2 < 0)
            {
                if (_kind[r] == VertexKind.Interior)
                {
                    Neighbours(u1);
                    for (var i = 0; i < _nexts.Length; i++) AddCandidate(u1, _nexts[i], -1, -1);
                }
                else
                {
                    OpenEdges(u1, out var a, out var b);
                    if (a >= 0) AddCandidate(u1, a, -1, -1);
                    if (b >= 0 && b != a) AddCandidate(u1, b, -1, -1);
                }
                return;
            }
            // The partner's side runs the other way: u1's outward neighbour pairs with u2's inward one.
            OpenEdges(u1, out var a1, out var b1);
            OpenEdges(u2, out var a2, out var b2);
            if (a1 >= 0 && b2 >= 0 && _positionId[a1] == _positionId[b2]) AddCandidate(u1, a1, u2, b2);
            if (b1 >= 0 && a2 >= 0 && _positionId[b1] == _positionId[a2]) AddCandidate(u1, b1, u2, a2);
        }

        void AddCandidate(int u1, int v1, int u2, int v2)
        {
            var cost = Cost(u1, v1, u2, v2, out var length);
            _cand.Add(new Candidate { U1 = u1, V1 = v1, U2 = u2, V2 = v2, Cost = cost, Key = cost + TieBreak * length });
        }

        bool BestKey(int r, out float key)
        {
            Candidates(r);
            key = float.PositiveInfinity;
            for (var i = 0; i < _cand.Length; i++) key = math.min(key, _cand[i].Key);
            return _cand.Length > 0;
        }

        void SortCandidates()
        {
            for (var i = 1; i < _cand.Length; i++)
            {
                var x = _cand[i];
                var j = i - 1;
                while (j >= 0 && _cand[j].Key > x.Key)
                {
                    _cand[j + 1] = _cand[j];
                    j--;
                }
                _cand[j + 1] = x;
            }
        }

        float3 Delta(int w, int frame) => DeltaLookup.Find(_deltaStart, _deltaFrame, _deltaValue, _origin[w], frame);

        float Cost(int u1, int v1, int u2, int v2, out float length)
        {
            var restE = _rest[u1].Evaluate(_p[v1]);
            var restW = _rest[u1].W;
            var uvE = _uv[u1].Evaluate(_p[v1], Uv(v1));
            if (u2 >= 0)
            {
                restE += _rest[u2].Evaluate(_p[v2]);
                restW += _rest[u2].W;
                uvE += _uv[u2].Evaluate(_p[v2], Uv(v2));
            }
            var eRest2 = restW > TinyWeight ? restE / restW : 0.0;
            var max2 = eRest2;
            var excess = 0.0;

            var i = _sparseStart[u1];
            var ie = i + _sparseCount[u1];
            var j = u2 >= 0 ? _sparseStart[u2] : 0;
            var je = u2 >= 0 ? j + _sparseCount[u2] : 0;
            while (i < ie || j < je)
            {
                var fi = i < ie ? _sparse[i].Frame : int.MaxValue;
                var fj = j < je ? _sparse[j].Frame : int.MaxValue;
                var f = math.min(fi, fj);
                var q1 = fi == f ? _sparse[i].Q : _rest[u1];
                var e = q1.Evaluate(_p[v1] + (double3)Delta(v1, f));
                var wsum = q1.W;
                if (u2 >= 0)
                {
                    var q2 = fj == f ? _sparse[j].Q : _rest[u2];
                    e += q2.Evaluate(_p[v2] + (double3)Delta(v2, f));
                    wsum += q2.W;
                }
                if (fi == f) i++;
                if (fj == f) j++;
                var e2 = wsum > TinyWeight ? e / wsum : 0.0;
                max2 = math.max(max2, e2);
                excess += math.max(0.0, e2 - eRest2);
            }

            double agg2;
            switch (_s.Aggregation)
            {
                case ShapeAggregation.Sum:
                    agg2 = eRest2 + excess;
                    break;
                case ShapeAggregation.Blend:
                    var b = math.saturate((double)_s.AggregationBlend);
                    agg2 = (1.0 - b) * max2 + b * (eRest2 + excess);
                    break;
                default:
                    agg2 = max2;
                    break;
            }

            var uvw = (double)_s.UvWeight;
            var uv2 = restW > TinyWeight ? uvw * uvw * uvE / restW : 0.0;

            var len = math.distance(_p[u1], _p[v1]);
            var d = SkinDistance(u1, v1);
            if (u2 >= 0)
            {
                len = math.max(len, math.distance(_p[u2], _p[v2]));
                d = math.max(d, SkinDistance(u2, v2));
            }
            var skin = (double)_s.SkinWeightPenalty * d * len;
            length = (float)len;
            return (float)(math.sqrt(agg2 + uv2 + skin * skin) * _multiplier[u1]);
        }

        float2 Uv(int w) => _uv0.Length > 0 ? _uv0[_origin[w]] : float2.zero;

        /// <summary>Half the L1 distance between two vertices' influence distributions: 0 alike, 1 disjoint.</summary>
        double SkinDistance(int a, int b)
        {
            var oa = _origin[a];
            var ob = _origin[b];
            if (oa == ob || _weightStart.Length == 0) return 0;
            var sum = 0.0;
            for (var i = _weightStart[oa]; i < _weightStart[oa + 1]; i++)
                sum += math.abs(_weights[i].Weight - WeightOf(ob, _weights[i].Bone, out _));
            for (var i = _weightStart[ob]; i < _weightStart[ob + 1]; i++)
            {
                WeightOf(oa, _weights[i].Bone, out var present);
                if (!present) sum += math.abs(_weights[i].Weight);
            }
            return 0.5 * sum;
        }

        float WeightOf(int o, int bone, out bool present)
        {
            present = false;
            var sum = 0f;
            for (var i = _weightStart[o]; i < _weightStart[o + 1]; i++)
            {
                if (_weights[i].Bone != bone) continue;
                sum += _weights[i].Weight;
                present = true;
            }
            return sum;
        }

        // ---------------------------------------------------------------- fans

        /// <summary>Fills _nexts with w's distinct neighbours over its live triangles.</summary>
        void Neighbours(int w)
        {
            _nexts.Clear();
            for (var c = _firstCorner[w]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _tri[t];
                var k = c - t * 3;
                AddUnique(ref _nexts, tri[(k + 1) % 3]);
                AddUnique(ref _nexts, tri[(k + 2) % 3]);
            }
        }

        static void AddUnique(ref NativeList<int> list, int x)
        {
            for (var i = 0; i < list.Length; i++)
                if (list[i] == x) return;
            list.Add(x);
        }

        static bool Contains(in NativeList<int> list, int x)
        {
            for (var i = 0; i < list.Length; i++)
                if (list[i] == x) return true;
            return false;
        }

        /// <summary>w's open edges as the live triangles now stand: -1 none, -2 more than one.</summary>
        void OpenEdges(int w, out int outward, out int inward)
        {
            _nexts.Clear();
            _prevs.Clear();
            for (var c = _firstCorner[w]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _tri[t];
                var k = c - t * 3;
                _nexts.Add(tri[(k + 1) % 3]);
                _prevs.Add(tri[(k + 2) % 3]);
            }
            outward = Unmatched(_nexts, _prevs);
            inward = Unmatched(_prevs, _nexts);
        }

        static int Unmatched(in NativeList<int> a, in NativeList<int> b)
        {
            var r = -1;
            for (var i = 0; i < a.Length; i++)
            {
                if (Contains(b, a[i])) continue;
                if (r == -1) r = a[i];
                else if (r != a[i]) return -2;
            }
            return r;
        }

        // ---------------------------------------------------------------- validity

        bool Valid(in Candidate c)
        {
            if (c.U2 >= 0 && _kind[c.U1] == VertexKind.Seam)
            {
                if (!LinkHolds(c.U1, c.V1, c.U2, c.V2)) return false;
            }
            else
            {
                if (!LinkHolds(c.U1, c.V1, -1, -1)) return false;
                if (c.U2 >= 0 && !LinkHolds(c.U2, c.V2, -1, -1)) return false;
            }
            if (!NoFlip(c.U1, c.V1)) return false;
            if (c.U2 >= 0 && !NoFlip(c.U2, c.V2)) return false;
            return true;
        }

        /// <summary>
        /// The link condition at position level: the only positions adjacent to both ends are the far corners
        /// of the triangles the collapse removes. Anything more and the collapse would fold the surface onto
        /// itself or duplicate a triangle.
        /// </summary>
        bool LinkHolds(int u1, int v1, int u2, int v2)
        {
            _nu.Clear();
            _nv.Clear();
            _opp.Clear();
            var pu = _positionId[u1];
            var pv = _positionId[v1];
            NeighbourPositions(u1, pu, pv, ref _nu);
            if (u2 >= 0) NeighbourPositions(u2, pu, pv, ref _nu);
            NeighbourPositions(v1, pu, pv, ref _nv);
            if (v2 >= 0) NeighbourPositions(v2, pu, pv, ref _nv);
            // A seam target's other side is the same surface point, so its neighbours count too.
            AddSeamPartner(v1, v2, pu, pv);
            AddSeamPartner(v2, v1, pu, pv);
            FarCorners(u1, v1, ref _opp);
            if (u2 >= 0) FarCorners(u2, v2, ref _opp);

            SortUnique(ref _nu);
            SortUnique(ref _nv);
            SortUnique(ref _opp);
            var shared = 0;
            int a = 0, b = 0;
            while (a < _nu.Length && b < _nv.Length)
            {
                if (_nu[a] == _nv[b])
                {
                    shared++;
                    a++;
                    b++;
                }
                else if (_nu[a] < _nv[b]) a++;
                else b++;
            }
            if (shared > _opp.Length) return false;
            // The edge half of the link condition: no triangle u keeps may land on one v already has, which
            // would fold two faces back to back.
            if (!NoDuplicateFace(u1, v1)) return false;
            if (u2 >= 0 && !NoDuplicateFace(u2, v2)) return false;
            return true;
        }

        bool NoDuplicateFace(int u, int v)
        {
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _tri[t];
                if (tri.x == v || tri.y == v || tri.z == v) continue;
                var k = c - t * 3;
                var px = _positionId[tri[(k + 1) % 3]];
                var py = _positionId[tri[(k + 2) % 3]];
                for (var d = _firstCorner[v]; d != -1; d = _cornerNext[d])
                {
                    var s = d / 3;
                    if (_alive[s] == 0) continue;
                    var other = _tri[s];
                    var hasX = _positionId[other.x] == px || _positionId[other.y] == px || _positionId[other.z] == px;
                    var hasY = _positionId[other.x] == py || _positionId[other.y] == py || _positionId[other.z] == py;
                    if (hasX && hasY) return false;
                }
            }
            return true;
        }

        void AddSeamPartner(int v, int other, int pu, int pv)
        {
            if (v < 0 || _kind[v] != VertexKind.Seam) return;
            var p = _partner[v];
            if (p == v || p == other) return;
            NeighbourPositions(p, pu, pv, ref _nv);
        }

        void NeighbourPositions(int w, int pu, int pv, ref NativeList<int> into)
        {
            for (var c = _firstCorner[w]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _tri[t];
                for (var k = 0; k < 3; k++)
                {
                    var pid = _positionId[tri[k]];
                    if (pid != pu && pid != pv) into.Add(pid);
                }
            }
        }

        void FarCorners(int u, int v, ref NativeList<int> into)
        {
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _tri[t];
                if (tri.x != v && tri.y != v && tri.z != v) continue;
                for (var k = 0; k < 3; k++)
                    if (tri[k] != u && tri[k] != v) into.Add(_positionId[tri[k]]);
            }
        }

        static void SortUnique(ref NativeList<int> list)
        {
            if (list.Length < 2) return;
            list.Sort();
            var n = 1;
            for (var i = 1; i < list.Length; i++)
                if (list[i] != list[n - 1]) list[n++] = list[i];
            list.Length = n;
        }

        /// <summary>
        /// No triangle that moves with u may flip or collapse to a sliver, at rest or in any live frame that
        /// moves u's one-ring (every other frame leaves the ring as it is at rest).
        /// </summary>
        bool NoFlip(int u, int v)
        {
            var fs = _sparseStart[u];
            var fe = fs + _sparseCount[u];
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _tri[t];
                if (tri.x == v || tri.y == v || tri.z == v) continue;
                var k = c - t * 3;
                var x = tri[(k + 1) % 3];
                var y = tri[(k + 2) % 3];
                var reference = _normalRest[v] + _normalRest[x] + _normalRest[y];
                if (!TriangleHolds(_p[u], _p[v], _p[x], _p[y], reference)) return false;
                for (var i = fs; i < fe; i++)
                {
                    var f = _sparse[i].Frame;
                    reference = NormalIn(v, f) + NormalIn(x, f) + NormalIn(y, f);
                    if (!TriangleHolds(
                            _p[u] + (double3)Delta(u, f), _p[v] + (double3)Delta(v, f),
                            _p[x] + (double3)Delta(x, f), _p[y] + (double3)Delta(y, f), reference))
                        return false;
                }
            }
            return true;
        }

        /// <summary>The source surface's normal at w in a frame: as at rest unless the frame moves w's ring.</summary>
        float3 NormalIn(int w, int frame)
        {
            var lo = _normalStart[w];
            var hi = lo + _normalCount[w] - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var m = _normalFrames[mid].Frame;
                if (m == frame) return _normalFrames[mid].N;
                if (m < frame) lo = mid + 1;
                else hi = mid - 1;
            }
            return _normalRest[w];
        }

        /// <summary>
        /// The moved triangle (u, x, y) becoming (v, x, y) in one configuration: rejected if it turns too far in
        /// this step, shrinks to a sliver, or ends up facing against the source surface at its corners, which
        /// catches a flip built up over many small steps.
        /// </summary>
        bool TriangleHolds(double3 pu, double3 pv, double3 px, double3 py, float3 reference)
        {
            var nb = math.cross(px - pu, py - pu);
            var nb2 = math.lengthsq(nb);
            if (nb2 <= 1e-36) return true; // already degenerate in this configuration: nothing to protect
            var na = math.cross(px - pv, py - pv);
            var na2 = math.lengthsq(na);
            var ratio = (double)_s.DegenerateRatio;
            if (na2 <= ratio * ratio * nb2) return false;
            if (math.dot(nb, na) <= _s.FlipCosine * math.sqrt(nb2 * na2)) return false;
            return math.lengthsq(reference) < 1e-6f || math.dot(na, (double3)reference) > 0.0;
        }

        // ---------------------------------------------------------------- applying

        void Apply(in Candidate c, ref CollapseJob job)
        {
            var killed = CollapseWedge(c.U1, c.V1);
            if (c.U2 >= 0) killed += CollapseWedge(c.U2, c.V2);
            _live -= killed;
            _level = math.max(_level, c.Cost);
            var start = job.Records.Length;
            job.Records.Add(new int2(c.U1, c.V1));
            if (c.U2 >= 0) job.Records.Add(new int2(c.U2, c.V2));
            job.Steps.Add(new CollapseStep { Error = _level, TriangleCount = _live, RecordStart = start, RecordCount = job.Records.Length - start });
            _dead[c.U1] = 1;
            if (c.U2 >= 0) _dead[c.U2] = 1;
            UpdateAround(c.V1, c.V2);
        }

        int CollapseWedge(int u, int v)
        {
            var killed = 0;
            var head = -1;
            var tail = -1;
            for (var c = _firstCorner[u]; c != -1;)
            {
                var next = _cornerNext[c];
                var t = c / 3;
                if (_alive[t] != 0)
                {
                    var tri = _tri[t];
                    if (tri.x == v || tri.y == v || tri.z == v)
                    {
                        _alive[t] = 0;
                        killed++;
                    }
                    else
                    {
                        tri[c - t * 3] = v;
                        _tri[t] = tri;
                        if (tail == -1) head = c;
                        else _cornerNext[tail] = c;
                        tail = c;
                    }
                }
                c = next;
            }
            if (tail != -1)
            {
                _cornerNext[tail] = _firstCorner[v];
                _firstCorner[v] = head;
            }
            _firstCorner[u] = -1;
            MergeQuadrics(u, v);
            return killed;
        }

        /// <summary>v's quadrics absorb u's. Where only one side holds a frame, the other side's rest quadric
        /// stands in for it, since that side's ring does not move in that frame.</summary>
        void MergeQuadrics(int u, int v)
        {
            var ru = _rest[u];
            var rv = _rest[v];
            _merged.Clear();
            var i = _sparseStart[u];
            var ie = i + _sparseCount[u];
            var j = _sparseStart[v];
            var je = j + _sparseCount[v];
            while (i < ie || j < je)
            {
                var fu = i < ie ? _sparse[i].Frame : int.MaxValue;
                var fv = j < je ? _sparse[j].Frame : int.MaxValue;
                if (fu < fv)
                {
                    _merged.Add(new FrameQuadric { Frame = fu, Q = Quadric.Sum(rv, _sparse[i].Q) });
                    i++;
                }
                else if (fv < fu)
                {
                    _merged.Add(new FrameQuadric { Frame = fv, Q = Quadric.Sum(_sparse[j].Q, ru) });
                    j++;
                }
                else
                {
                    _merged.Add(new FrameQuadric { Frame = fu, Q = Quadric.Sum(_sparse[i].Q, _sparse[j].Q) });
                    i++;
                    j++;
                }
            }
            _rest[v] = Quadric.Sum(rv, ru);
            var uv = _uv[v];
            uv.Add(_uv[u]);
            _uv[v] = uv;

            var n = _merged.Length;
            if (n > _sparseCap[v])
            {
                _wasted += _sparseCap[v];
                var cap = n + n / 2;
                _sparseStart[v] = _sparse.Length;
                _sparseCap[v] = cap;
                _sparse.Resize(_sparse.Length + cap, NativeArrayOptions.UninitializedMemory);
                _peak = math.max(_peak, _sparse.Length);
            }
            var at = _sparseStart[v];
            for (var k = 0; k < n; k++) _sparse[at + k] = _merged[k];
            _sparseCount[v] = n;
            _wasted += _sparseCap[u];
            _sparseCount[u] = 0;
            _sparseCap[u] = 0;
            if (_wasted > 4096 && _wasted * 2 > _sparse.Length) CompactPool();
        }

        void CompactPool()
        {
            var copy = new NativeList<FrameQuadric>(_sparse.Length - (int)_wasted, Allocator.Temp);
            for (var w = 0; w < _sparseStart.Length; w++)
            {
                if (_dead[w] != 0 || _sparseCap[w] == 0)
                {
                    _sparseCap[w] = 0;
                    continue;
                }
                var start = copy.Length;
                var count = _sparseCount[w];
                for (var k = 0; k < count; k++) copy.Add(_sparse[_sparseStart[w] + k]);
                _sparseStart[w] = start;
                _sparseCap[w] = count;
            }
            _sparse.Clear();
            _sparse.AddRange(copy.AsArray());
            _wasted = 0;
            copy.Dispose();
        }

        void UpdateAround(int v1, int v2)
        {
            _stampValue++;
            _affected.Clear();
            Touch(v1);
            if (v2 >= 0) Touch(v2);
            for (var i = 0; i < _affected.Length; i++)
            {
                var r = _affected[i];
                if (_dead[r] != 0 || _removable[r] == 0) continue;
                if (BestKey(r, out var key)) HeapSet(r, key);
                else HeapRemove(r);
            }
        }

        /// <summary>Marks the clusters of v and its one-ring, dropping dead corners from v's list on the way.</summary>
        void Touch(int v)
        {
            Mark(Rep(v));
            var prev = -1;
            for (var c = _firstCorner[v]; c != -1;)
            {
                var next = _cornerNext[c];
                var t = c / 3;
                if (_alive[t] == 0)
                {
                    if (prev == -1) _firstCorner[v] = next;
                    else _cornerNext[prev] = next;
                }
                else
                {
                    var tri = _tri[t];
                    Mark(Rep(tri.x));
                    Mark(Rep(tri.y));
                    Mark(Rep(tri.z));
                    prev = c;
                }
                c = next;
            }
        }

        void Mark(int r)
        {
            if (_stamp[r] == _stampValue) return;
            _stamp[r] = _stampValue;
            _affected.Add(r);
        }

        // ---------------------------------------------------------------- heap

        void HeapSet(int id, float key)
        {
            var i = _heapPos[id];
            if (i < 0)
            {
                _heapKey[id] = key;
                _heap.Add(id);
                _heapPos[id] = _heap.Length - 1;
                SiftUp(_heap.Length - 1);
                return;
            }
            var old = _heapKey[id];
            _heapKey[id] = key;
            if (key < old) SiftUp(i);
            else SiftDown(i);
        }

        void HeapRemove(int id)
        {
            var i = _heapPos[id];
            if (i < 0) return;
            var lastIndex = _heap.Length - 1;
            var last = _heap[lastIndex];
            _heap[i] = last;
            _heapPos[last] = i;
            _heap.RemoveAtSwapBack(lastIndex);
            _heapPos[id] = -1;
            if (i < _heap.Length)
            {
                SiftUp(i);
                SiftDown(_heapPos[last]);
            }
        }

        int HeapPop()
        {
            var id = _heap[0];
            HeapRemove(id);
            return id;
        }

        void SiftUp(int i)
        {
            var id = _heap[i];
            var key = _heapKey[id];
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                var pid = _heap[parent];
                if (_heapKey[pid] <= key) break;
                _heap[i] = pid;
                _heapPos[pid] = i;
                i = parent;
            }
            _heap[i] = id;
            _heapPos[id] = i;
        }

        void SiftDown(int i)
        {
            var id = _heap[i];
            var key = _heapKey[id];
            var n = _heap.Length;
            while (true)
            {
                var l = i * 2 + 1;
                if (l >= n) break;
                var r = l + 1;
                var m = r < n && _heapKey[_heap[r]] < _heapKey[_heap[l]] ? r : l;
                if (_heapKey[_heap[m]] >= key) break;
                _heap[i] = _heap[m];
                _heapPos[_heap[i]] = i;
                i = m;
            }
            _heap[i] = id;
            _heapPos[id] = i;
        }
    }
}
