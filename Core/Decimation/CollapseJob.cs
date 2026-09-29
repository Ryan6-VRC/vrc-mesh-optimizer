using Ryan6Vrc.MeshOptimizer.Core.Clearance;
using Ryan6Vrc.MeshOptimizer.Core.Geometry;
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
    [BurstCompile(CompileSynchronously = true)]
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
        /// <summary>Per source vertex, the normal the mesh carries; empty when the normal term is off.</summary>
        [ReadOnly] public NativeArray<float3> VertexNormals;

        public NativeArray<int3> Triangles;
        public NativeArray<Quadric> Rest;
        public NativeArray<UvQuadric> Uv;
        /// <summary>Per working vertex, its normal quadric; empty when the normal term is off.</summary>
        public NativeArray<NormalQuadric> NormalField;
        public NativeArray<int> SparseStart;
        public NativeArray<int> SparseCount;
        public NativeList<FrameQuadric> Sparse;
        public int LiveTriangles;
        public DecimationSettings Settings;

        /// <summary>The other layers, empty when the clearance rule is off. See <see cref="ClearanceSurface"/>.</summary>
        [ReadOnly] public NativeArray<TreeNode> ClearNodes;
        [ReadOnly] public NativeArray<TreePrim> ClearPrims;
        [ReadOnly] public NativeArray<int> ClearPrimLayer;
        [ReadOnly] public NativeArray<int> ClearNodeLayer;
        [ReadOnly] public NativeArray<float> ClearPrimClearance;
        [ReadOnly] public NativeArray<float> ClearNodeClearance;
        /// <summary>Per triangle, its layer; per source vertex, its clearance before decimation.</summary>
        [ReadOnly] public NativeArray<int> TriangleLayer;
        [ReadOnly] public NativeArray<float> VertexClearance;
        [ReadOnly] public NativeArray<float> StarClearance;
        /// <summary>Per triangle, the least clearance of the source surface it covers; updated as collapses replace triangles.</summary>
        public NativeArray<float> TriangleClearance;

        /// <summary>The mesh's bends; see <see cref="Decimation.Bends"/>. Empty arrays when there are none.</summary>
        public Bends Bends;

        public NativeList<CollapseStep> Steps;
        public NativeList<int2> Records;
        /// <summary>[0] peak frame-quadric pool entries, [1] clusters left blocked by validity, [2] collapses the clearance rule refused.</summary>
        public NativeArray<long> Stats;

        public void Execute()
        {
            var engine = new CollapseEngine(ref this);
            engine.Run();
        }
    }

    internal struct Candidate
    {
        public int U1, V1, U2, V2;
        public float Cost;
        public float Key;
        /// <summary>The largest error in any bend, in metres; 0 without bends.</summary>
        public float Bend;
    }

    internal struct CollapseEngine
    {
        /// <summary>
        /// The job, whose arrays and lists this reads and writes in place: a copy of a native container shares its storage,
        /// so the triangles, quadrics and pool the job holds are the ones the collapses change, and the steps and records it
        /// holds are the ones they append to.
        /// </summary>
        CollapseJob _in;
        bool _shading;
        bool _clearance;
        bool _bent;
        long _refused;

        // Working state.
        NativeArray<double3> _p;
        NativeArray<byte> _alive;
        NativeArray<int> _cornerNext;
        NativeArray<int> _firstCorner;
        NativeArray<byte> _dead;
        NativeArray<byte> _removable;
        NativeArray<float> _multiplier;
        NativeArray<int> _sparseCap;
        /// <summary>Per vertex, how many of its pool entries are shape frames; its bends follow them.</summary>
        NativeArray<int> _shapeCount;
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
        NativeList<int> _frames;

        int _live;
        float _level;

        const double TinyWeight = 1e-30;
        const float TieBreak = 1e-4f;
        /// <summary>The fraction of its crossing a collapse may add without counting as lengthening it: above the float
        /// rounding of a sum of segment lengths, and relative, so ten thousand collapses on one crossing lengthen it by
        /// at most a hundredth, where an absolute allowance would add up without bound.</summary>
        const float CrossingSlack = 1e-6f;
        /// <summary>
        /// Metres a new triangle may stand off its own layer's source where it crosses another layer. A total length
        /// that does not grow could still move: shortened against one place, lengthened against another, or wandering
        /// across one large triangle. Held to its own source, a crossing stays where the source crossed, give or take
        /// this over the sine of the angle the layers meet at: within the gate's 5 mm for any angle over 6 degrees.
        /// </summary>
        const float CrossingDrift = 0.0005f;

        public CollapseEngine(ref CollapseJob job)
        {
            _in = job;
            _shading = job.NormalField.Length > 0;
            _clearance = job.ClearNodes.Length > 0 && job.TriangleClearance.Length == job.Triangles.Length;
            _bent = job.Bends.Active;
            _refused = 0;
            _live = job.LiveTriangles;
            _level = 0f;
            _stampValue = 0;
            _wasted = 0;
            _peak = _in.Sparse.Length;

            var w = _in.Origin.Length;
            var t = _in.Triangles.Length;
            _p = new NativeArray<double3>(w, Allocator.Temp);
            _alive = new NativeArray<byte>(t, Allocator.Temp);
            _cornerNext = new NativeArray<int>(t * 3, Allocator.Temp);
            _firstCorner = new NativeArray<int>(w, Allocator.Temp);
            _dead = new NativeArray<byte>(w, Allocator.Temp);
            _removable = new NativeArray<byte>(w, Allocator.Temp);
            _multiplier = new NativeArray<float>(w, Allocator.Temp);
            _sparseCap = new NativeArray<int>(w, Allocator.Temp);
            _shapeCount = new NativeArray<int>(w, Allocator.Temp);
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
            _frames = new NativeList<int>(64, Allocator.Temp);

            for (var i = 0; i < w; i++)
            {
                _p[i] = (double3)_in.Positions[_in.Origin[i]];
                _firstCorner[i] = -1;
                _heapPos[i] = -1;
                _sparseCap[i] = _in.SparseCount[i];
                var e = _in.SparseStart[i] + _in.SparseCount[i];
                while (e > _in.SparseStart[i] && _in.Sparse[e - 1].Frame >= Bends.FrameBase) e--;
                _shapeCount[i] = e - _in.SparseStart[i];
            }
            for (var i = 0; i < t; i++)
            {
                var tri = _in.Triangles[i];
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
                var p = _in.Partner[i];
                var locked = _in.Kind[i] == VertexKind.Locked || IsProtected(job.ProtectLocked, i) || IsProtected(job.ProtectLocked, p);
                _removable[i] = (byte)(locked ? 0 : 1);
                _multiplier[i] = math.max(MultiplierOf(job.ProtectMultiplier, i), MultiplierOf(job.ProtectMultiplier, p));
            }
        }

        bool IsProtected(NativeArray<byte> locked, int w) => locked.Length > 0 && locked[_in.Origin[w]] != 0;

        /// <summary>A vertex's cost multiplier, never under 1: a multiplier only keeps a vertex longer. NaN reads as 1.</summary>
        float MultiplierOf(NativeArray<float> mult, int w) => mult.Length > 0 ? math.max(mult[_in.Origin[w]], 1f) : 1f;

        int Rep(int w) => math.min(w, _in.Partner[w]);

        public void Run()
        {
            for (var w = 0; w < _in.Origin.Length; w++)
            {
                if (Rep(w) != w || _removable[w] == 0) continue;
                if (BestKey(w, out var key)) HeapSet(w, key);
            }

            while (_heap.Length > 0)
            {
                if (_live <= _in.Settings.TargetTriangleCount) break;
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
                    _in.Stats[1]++;
                    continue; // blocked until its neighbourhood changes
                }
                var c = _cand[found];
                if (_heap.Length > 0 && c.Key > _heapKey[_heap[0]])
                {
                    HeapSet(r, c.Key);
                    continue;
                }
                // The heap orders by key, which carries a length tie-break, so one cost over the limit does not
                // prove every remaining cost is: drop this cluster and keep going.
                if (c.Cost > _in.Settings.MaxError) continue;
                Apply(c);
            }
            _in.Stats[0] = _peak;
            _in.Stats[2] = _refused;
        }

        // ---------------------------------------------------------------- candidates and cost

        void Candidates(int r)
        {
            _cand.Clear();
            var u1 = r;
            var u2 = _in.Partner[r] != r ? _in.Partner[r] : -1;
            if (u2 < 0)
            {
                if (_in.Kind[r] == VertexKind.Interior)
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
            if (a1 >= 0 && b2 >= 0 && _in.PositionId[a1] == _in.PositionId[b2]) AddCandidate(u1, a1, u2, b2);
            if (b1 >= 0 && a2 >= 0 && _in.PositionId[b1] == _in.PositionId[a2]) AddCandidate(u1, b1, u2, a2);
        }

        void AddCandidate(int u1, int v1, int u2, int v2)
        {
            var cost = Cost(u1, v1, u2, v2, out var length, out var bend);
            _cand.Add(new Candidate { U1 = u1, V1 = v1, U2 = u2, V2 = v2, Cost = cost, Key = cost + TieBreak * length, Bend = bend });
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

        float3 Delta(int w, int frame) => DeltaLookup.Find(_in.DeltaStart, _in.DeltaFrame, _in.DeltaValue, _in.Origin[w], frame);

        float Cost(int u1, int v1, int u2, int v2, out float length, out float bend)
        {
            bend = 0f;
            var restE = _in.Rest[u1].Evaluate(_p[v1]);
            var restW = _in.Rest[u1].W;
            var uvE = _in.Uv[u1].Evaluate(_p[v1], Uv(v1));
            if (u2 >= 0)
            {
                restE += _in.Rest[u2].Evaluate(_p[v2]);
                restW += _in.Rest[u2].W;
                uvE += _in.Uv[u2].Evaluate(_p[v2], Uv(v2));
            }
            var eRest2 = restW > TinyWeight ? restE / restW : 0.0;
            var max2 = eRest2;
            var excess = 0.0;

            var i = _in.SparseStart[u1];
            var ie = ShapeEnd(u1);
            var j = u2 >= 0 ? _in.SparseStart[u2] : 0;
            var je = u2 >= 0 ? ShapeEnd(u2) : 0;
            while (i < ie || j < je)
            {
                var fi = i < ie ? _in.Sparse[i].Frame : int.MaxValue;
                var fj = j < je ? _in.Sparse[j].Frame : int.MaxValue;
                var f = math.min(fi, fj);
                var q1 = fi == f ? _in.Sparse[i].Q : _in.Rest[u1];
                var e = q1.Evaluate(_p[v1] + (double3)Delta(v1, f));
                var wsum = q1.W;
                if (u2 >= 0)
                {
                    var q2 = fj == f ? _in.Sparse[j].Q : _in.Rest[u2];
                    e += q2.Evaluate(_p[v2] + (double3)Delta(v2, f));
                    wsum += q2.W;
                }
                if (fi == f) i++;
                if (fj == f) j++;
                var e2 = wsum > TinyWeight ? e / wsum : 0.0;
                max2 = math.max(max2, e2);
                excess += math.max(0.0, e2 - eRest2);
            }
            if (_bent)
            {
                var bend2 = 0.0;
                BendError(u1, v1, u2, v2, eRest2, ref max2, ref excess, ref bend2);
                bend = (float)math.sqrt(bend2);
            }

            double agg2;
            switch (_in.Settings.Aggregation)
            {
                case ShapeAggregation.Sum:
                    agg2 = eRest2 + excess;
                    break;
                case ShapeAggregation.Blend:
                    var b = math.saturate((double)_in.Settings.AggregationBlend);
                    agg2 = (1.0 - b) * max2 + b * (eRest2 + excess);
                    break;
                default:
                    agg2 = max2;
                    break;
            }

            var uvw = (double)_in.Settings.UvWeight;
            var uv2 = restW > TinyWeight ? uvw * uvw * uvE / restW : 0.0;

            var len = math.distance(_p[u1], _p[v1]);
            var d = SkinDistance(u1, v1);
            if (u2 >= 0)
            {
                len = math.max(len, math.distance(_p[u2], _p[v2]));
                d = math.max(d, SkinDistance(u2, v2));
            }
            var skin = (double)_in.Settings.SkinWeightPenalty * d * len;
            length = (float)len;
            // The removed vertex's triangles move onto the target and ride its bones afterwards, so the collapse is priced
            // at the larger growth of the two, and of both pairs for a seam or border pair.
            var mult = math.max(_multiplier[u1], _multiplier[v1]);
            if (u2 >= 0) mult = math.max(mult, math.max(_multiplier[u2], _multiplier[v2]));
            var sum2 = agg2 + uv2 + skin * skin;
            // Off, the term touches nothing, so the sequence is exactly the one without it.
            if (_shading) sum2 += Shading2(u1, v1, u2, v2);
            return (float)(math.sqrt(sum2) * mult);
        }

        float2 Uv(int w) => _in.Uv0.Length > 0 ? _in.Uv0[_in.Origin[w]] : float2.zero;

        /// <summary>
        /// The normal term's error squared, in metres squared (see <see cref="NormalSettings"/>): how far each target's
        /// normal lies from the normal fields of the surface its removed vertex stands for, summed over a seam or border
        /// pair's two sides, each side with its own normals.
        /// </summary>
        double Shading2(int u1, int v1, int u2, int v2)
        {
            var e = _in.NormalField[u1].Evaluate(_p[v1], NormalQuadric.Readable(_in.VertexNormals[_in.Origin[v1]]));
            if (u2 >= 0) e += _in.NormalField[u2].Evaluate(_p[v2], NormalQuadric.Readable(_in.VertexNormals[_in.Origin[v2]]));
            var w = (double)_in.Settings.Normal.Weight;
            return w * w * e;
        }

        /// <summary>Half the L1 distance between two vertices' influence distributions: 0 alike, 1 disjoint.</summary>
        double SkinDistance(int a, int b)
        {
            var oa = _in.Origin[a];
            var ob = _in.Origin[b];
            if (oa == ob || _in.WeightStart.Length == 0) return 0;
            var sum = 0.0;
            for (var i = _in.WeightStart[oa]; i < _in.WeightStart[oa + 1]; i++)
                sum += math.abs(_in.Weights[i].Weight - WeightOf(ob, _in.Weights[i].Bone, out _));
            for (var i = _in.WeightStart[ob]; i < _in.WeightStart[ob + 1]; i++)
            {
                WeightOf(oa, _in.Weights[i].Bone, out var present);
                if (!present) sum += math.abs(_in.Weights[i].Weight);
            }
            return 0.5 * sum;
        }

        float WeightOf(int o, int bone, out bool present)
        {
            present = false;
            var sum = 0f;
            for (var i = _in.WeightStart[o]; i < _in.WeightStart[o + 1]; i++)
            {
                if (_in.Weights[i].Bone != bone) continue;
                sum += _in.Weights[i].Weight;
                present = true;
            }
            return sum;
        }

        // ---------------------------------------------------------------- bends

        /// <summary>The end of w's shape frames in the pool: its bends follow them, to the end of its list.</summary>
        int ShapeEnd(int w) => _in.SparseStart[w] + _shapeCount[w];

        int ListEnd(int w) => _in.SparseStart[w] + _in.SparseCount[w];

        /// <summary>
        /// w's quadric in a bend, in its own home: the one it holds, or else its rest quadric, since then everything it
        /// covers moves rigidly with its home. Frames are asked for in ascending order, so <paramref name="cursor"/>, which
        /// starts at <see cref="ShapeEnd"/>, only ever moves forward through w's bends.
        /// </summary>
        Quadric BendQuadric(int w, int frame, ref int cursor)
        {
            var end = ListEnd(w);
            while (cursor < end && _in.Sparse[cursor].Frame < frame) cursor++;
            return cursor < end && _in.Sparse[cursor].Frame == frame ? _in.Sparse[cursor].Q : _in.Rest[w];
        }

        bool HomeOf(int w, int k) => _in.Bends.HomeIn(_in.Origin[w], _in.Bends.BoneOf(k));

        /// <summary>
        /// Adds to _frames the bends a collapse of u onto v can move: those u holds a quadric for, those that bend v's own
        /// surroundings, and those in which u and v have different homes. In any other bend u covers only surface that
        /// moves rigidly with v, and the error there is the rest error.
        /// </summary>
        void BendFrames(int u, int v)
        {
            for (var i = ShapeEnd(u); i < ListEnd(u); i++) _frames.Add(_in.Sparse[i].Frame);
            _in.Bends.AddMixed(_in.Origin[v], ref _frames);
            _in.Bends.AddApart(_in.Origin[u], _in.Origin[v], ref _frames);
        }

        /// <summary>u's error in bend k with v standing where the bend puts it, both read in u's home.</summary>
        double BendError(int u, int v, int k, ref int cursor, out double weight)
        {
            var q = BendQuadric(u, Bends.FrameBase + k, ref cursor);
            weight = q.W;
            return q.Evaluate(_in.Bends.InHome(_in.Origin[v], _p[v], k, HomeOf(u, k)));
        }

        /// <summary>Joins each bend's error to the rest and shape errors, as a shape frame's is joined.</summary>
        void BendError(int u1, int v1, int u2, int v2, double eRest2, ref double max2, ref double excess, ref double bend2)
        {
            _frames.Clear();
            BendFrames(u1, v1);
            if (u2 >= 0) BendFrames(u2, v2);
            SortUnique(ref _frames);
            var c1 = ShapeEnd(u1);
            var c2 = u2 >= 0 ? ShapeEnd(u2) : 0;
            for (var i = 0; i < _frames.Length; i++)
            {
                var k = _frames[i] - Bends.FrameBase;
                var e = BendError(u1, v1, k, ref c1, out var wsum);
                if (u2 >= 0)
                {
                    e += BendError(u2, v2, k, ref c2, out var w2);
                    wsum += w2;
                }
                var e2 = wsum > TinyWeight ? e / wsum : 0.0;
                max2 = math.max(max2, e2);
                bend2 = math.max(bend2, e2);
                excess += math.max(0.0, e2 - eRest2);
            }
        }

        /// <summary>
        /// Where the surface a collapse replaces lies near another layer, the clearance rule's share of the gap bounds the
        /// collapse's error in every bend too: the rest rule keeps the layers apart at rest, where the surface near a joint
        /// is near flat, and this keeps the chord a decimated triangle cuts across a bent joint within the same share. Where
        /// the surface touches or crosses another layer there is no gap to share, and the crossing rule governs alone.
        /// </summary>
        bool BendClearanceHolds(in Candidate c)
        {
            var gap = math.min(FanClearance(c.U1), _in.StarClearance[_in.Origin[c.V1]]);
            if (c.U2 >= 0) gap = math.min(gap, math.min(FanClearance(c.U2), _in.StarClearance[_in.Origin[c.V2]]));
            if (!(gap > 0f) || gap >= _in.Settings.Clearance.Reach) return true;
            return c.Bend <= _in.Settings.Bend.ClearanceShare * gap;
        }

        /// <summary>
        /// Appends to _merged v's bend quadrics once it absorbs u's, each in v's home: u's, carried into v's home where
        /// u's differs, joined to v's. A bend in which neither held one and their homes differ gains one here, since the
        /// joined surface no longer moves as one piece. Runs before v's rest quadric absorbs u's, which it reads.
        /// </summary>
        void MergeBends(int u, int v)
        {
            _frames.Clear();
            for (var i = ShapeEnd(u); i < ListEnd(u); i++) _frames.Add(_in.Sparse[i].Frame);
            for (var i = ShapeEnd(v); i < ListEnd(v); i++) _frames.Add(_in.Sparse[i].Frame);
            _in.Bends.AddApart(_in.Origin[u], _in.Origin[v], ref _frames);
            SortUnique(ref _frames);
            var cu = ShapeEnd(u);
            var cv = ShapeEnd(v);
            for (var i = 0; i < _frames.Length; i++)
            {
                var f = _frames[i];
                var k = f - Bends.FrameBase;
                var qu = _in.Bends.Carry(BendQuadric(u, f, ref cu), k, HomeOf(u, k), HomeOf(v, k));
                _merged.Add(new FrameQuadric { Frame = f, Q = Quadric.Sum(BendQuadric(v, f, ref cv), qu) });
            }
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
                var tri = _in.Triangles[t];
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
                var tri = _in.Triangles[t];
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
            if (c.U2 >= 0 && _in.Kind[c.U1] == VertexKind.Seam)
            {
                if (!LinkHolds(c.U1, c.V1, c.U2, c.V2)) return false;
            }
            else
            {
                if (!LinkHolds(c.U1, c.V1, -1, -1)) return false;
                if (c.U2 >= 0 && !LinkHolds(c.U2, c.V2, -1, -1)) return false;
            }
            if (!Survives(c.U1, c.V1)) return false;
            if (c.U2 >= 0 && !Survives(c.U2, c.V2)) return false;
            if (!NoFlip(c.U1, c.V1)) return false;
            if (c.U2 >= 0 && !NoFlip(c.U2, c.V2)) return false;
            if (_clearance && (!ClearanceHolds(c.U1, c.V1) || c.U2 >= 0 && !ClearanceHolds(c.U2, c.V2)))
            {
                _refused++;
                return false;
            }
            if (_clearance && _bent && _in.Settings.Bend.ClearanceShare > 0f && c.Bend > 0f && !BendClearanceHolds(c))
            {
                _refused++;
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- clearance

        /// <summary>The least clearance of the source surface under u's live triangles: what the fan that replaces them covers. 0 where it touches or crosses another layer.</summary>
        float FanClearance(int u)
        {
            var least = float.PositiveInfinity;
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] != 0) least = math.min(least, _in.TriangleClearance[t]);
            }
            return least;
        }

        /// <summary>
        /// The clearance rule (see <see cref="ClearanceSettings"/>): no triangle the collapse of u onto v makes may come
        /// nearer a source triangle of another layer than (1 - Share) of the gap there, and, where the surface under u's
        /// triangles or around a new triangle's corners touches or crosses another layer, no collapse may lengthen the
        /// crossing either. The gap is kept even beside a crossing, since a little way off there is one, and a layer
        /// judged only by the crossing could reach the other's source there and meet it. The corners' own clearances
        /// settle most triangles without a search.
        /// </summary>
        bool ClearanceHolds(int u, int v)
        {
            var touches = !(FanClearance(u) > 0f);
            var keep = 1f - _in.Settings.Clearance.Share;
            var pv = _in.Positions[_in.Origin[v]];
            var cv = _in.VertexClearance[_in.Origin[v]];
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _in.Triangles[t];
                if (tri.x == v || tri.y == v || tri.z == v) continue;
                var k = c - t * 3;
                var x = _in.Origin[tri[(k + 1) % 3]];
                var y = _in.Origin[tri[(k + 2) % 3]];
                var px = _in.Positions[x];
                var py = _in.Positions[y];
                // A source triangle P of another layer is held to keep * its gap, which is no more than the reach, and no
                // more than P's distance to a corner (the corners lie on this layer's source): d + the longest edge. So
                // nothing nearer than keep * min(Reach, edge / (1 - keep)) can be refused, and the corner bound often
                // proves that.
                var least = math.min(_in.StarClearance[_in.Origin[v]], math.min(_in.StarClearance[x], _in.StarClearance[y]));
                // A source triangle that touches or crosses a layer has no gap to hold a new triangle to; where a corner's
                // surroundings touch too, the search below cannot see it, and the crossing check must.
                if (!(least > 0f)) touches = true;
                var edge = math.max(math.distance(pv, px), math.max(math.distance(px, py), math.distance(py, pv)));
                var most = keep * math.min(_in.Settings.Clearance.Reach, edge / (1f - keep));
                if (ClearanceBound.Of(pv, px, py, cv, _in.VertexClearance[x], _in.VertexClearance[y]) >= most) continue;
                if (ClearanceSurface.AnyWithin(_in.ClearNodes, _in.ClearPrims, _in.ClearPrimLayer, _in.ClearNodeLayer, _in.ClearPrimClearance, _in.ClearNodeClearance,
                        pv, px, py, _in.TriangleLayer[t], keep, least)) return false;
            }
            return !touches || CrossingHolds(u, v);
        }

        /// <summary>
        /// Where the surface already touches or crosses another layer there is no clearance to keep, and the rule
        /// instead forbids lengthening or moving the crossing: the triangles the collapse makes may pass through the
        /// other layers along no longer a line than the triangles they replace did, and only where they stand within
        /// <see cref="CrossingDrift"/> of their own source. Touching and not crossing, they may not cross.
        /// </summary>
        bool CrossingHolds(int u, int v)
        {
            var before = 0f;
            var after = 0f;
            var pv = _in.Positions[_in.Origin[v]];
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _in.Triangles[t];
                var layer = _in.TriangleLayer[t];
                float3 a = _in.Positions[_in.Origin[tri.x]], b = _in.Positions[_in.Origin[tri.y]], d = _in.Positions[_in.Origin[tri.z]];
                before += ClearanceSurface.CrossingLength(_in.ClearNodes, _in.ClearPrims, _in.ClearPrimLayer, _in.ClearNodeLayer, a, b, d, layer);
                if (tri.x == v || tri.y == v || tri.z == v) continue;
                var k = c - t * 3;
                var px = _in.Positions[_in.Origin[tri[(k + 1) % 3]]];
                var py = _in.Positions[_in.Origin[tri[(k + 2) % 3]]];
                after += ClearanceSurface.CrossingLength(_in.ClearNodes, _in.ClearPrims, _in.ClearPrimLayer, _in.ClearNodeLayer, pv, px, py, layer, CrossingDrift, out var strays);
                if (strays) return false;
            }
            return after <= before * (1f + CrossingSlack) + 1e-9f;
        }

        /// <summary>
        /// False when the collapse would leave v with no triangle at all: u and v then share every triangle
        /// either has, and removing them deletes the last of a connected piece, which decimation never does.
        /// </summary>
        bool Survives(int u, int v)
        {
            if (HasTriangleWithout(u, v)) return true;
            return HasTriangleWithout(v, u);
        }

        bool HasTriangleWithout(int w, int other)
        {
            for (var c = _firstCorner[w]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _in.Triangles[t];
                if (tri.x != other && tri.y != other && tri.z != other) return true;
            }
            return false;
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
            var pu = _in.PositionId[u1];
            var pv = _in.PositionId[v1];
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
                var tri = _in.Triangles[t];
                if (tri.x == v || tri.y == v || tri.z == v) continue;
                var k = c - t * 3;
                var px = _in.PositionId[tri[(k + 1) % 3]];
                var py = _in.PositionId[tri[(k + 2) % 3]];
                for (var d = _firstCorner[v]; d != -1; d = _cornerNext[d])
                {
                    var s = d / 3;
                    if (_alive[s] == 0) continue;
                    var other = _in.Triangles[s];
                    var hasX = _in.PositionId[other.x] == px || _in.PositionId[other.y] == px || _in.PositionId[other.z] == px;
                    var hasY = _in.PositionId[other.x] == py || _in.PositionId[other.y] == py || _in.PositionId[other.z] == py;
                    if (hasX && hasY) return false;
                }
            }
            return true;
        }

        void AddSeamPartner(int v, int other, int pu, int pv)
        {
            if (v < 0 || _in.Kind[v] != VertexKind.Seam) return;
            var p = _in.Partner[v];
            if (p == v || p == other) return;
            NeighbourPositions(p, pu, pv, ref _nv);
        }

        void NeighbourPositions(int w, int pu, int pv, ref NativeList<int> into)
        {
            for (var c = _firstCorner[w]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _in.Triangles[t];
                for (var k = 0; k < 3; k++)
                {
                    var pid = _in.PositionId[tri[k]];
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
                var tri = _in.Triangles[t];
                if (tri.x != v && tri.y != v && tri.z != v) continue;
                for (var k = 0; k < 3; k++)
                    if (tri[k] != u && tri[k] != v) into.Add(_in.PositionId[tri[k]]);
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
        /// moves one of its corners (every other frame leaves it as it is at rest). u's frames cover the source
        /// surface it stands for, but a corner reached through earlier collapses need not be a corner of that
        /// surface, so the frames moving v and the triangle's other corners are checked too.
        /// </summary>
        bool NoFlip(int u, int v)
        {
            var fs = _in.SparseStart[u];
            var fe = ShapeEnd(u);
            for (var c = _firstCorner[u]; c != -1; c = _cornerNext[c])
            {
                var t = c / 3;
                if (_alive[t] == 0) continue;
                var tri = _in.Triangles[t];
                if (tri.x == v || tri.y == v || tri.z == v) continue;
                var k = c - t * 3;
                var x = tri[(k + 1) % 3];
                var y = tri[(k + 2) % 3];
                var reference = _in.NormalRest[v] + _in.NormalRest[x] + _in.NormalRest[y];
                if (!TriangleHolds(_p[u], _p[v], _p[x], _p[y], reference)) return false;
                for (var i = fs; i < fe; i++)
                    if (!HoldsIn(u, v, x, y, _in.Sparse[i].Frame)) return false;
                if (!HoldsInFramesOf(v, u, v, x, y, fs, fe)) return false;
                if (!HoldsInFramesOf(x, u, v, x, y, fs, fe)) return false;
                if (!HoldsInFramesOf(y, u, v, x, y, fs, fe)) return false;
            }
            return true;
        }

        /// <summary>The moved triangle (u, x, y) becoming (v, x, y) holds in frame f.</summary>
        bool HoldsIn(int u, int v, int x, int y, int f)
        {
            var reference = NormalIn(v, f) + NormalIn(x, f) + NormalIn(y, f);
            return TriangleHolds(
                _p[u] + (double3)Delta(u, f), _p[v] + (double3)Delta(v, f),
                _p[x] + (double3)Delta(x, f), _p[y] + (double3)Delta(y, f), reference);
        }

        /// <summary>The moved triangle holds in every frame that moves corner w and is not among u's frames, Sparse[fs .. fe).</summary>
        bool HoldsInFramesOf(int w, int u, int v, int x, int y, int fs, int fe)
        {
            var o = _in.Origin[w];
            for (var j = _in.DeltaStart[o]; j < _in.DeltaStart[o + 1]; j++)
            {
                var f = _in.DeltaFrame[j];
                if (HasFrame(fs, fe, f)) continue;
                if (!HoldsIn(u, v, x, y, f)) return false;
            }
            return true;
        }

        /// <summary>Whether frame f is among Sparse[lo .. hi), which is frame ascending.</summary>
        bool HasFrame(int lo, int hi, int f)
        {
            hi--;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var m = _in.Sparse[mid].Frame;
                if (m == f) return true;
                if (m < f) lo = mid + 1;
                else hi = mid - 1;
            }
            return false;
        }

        /// <summary>The source surface's normal at w in a frame: as at rest unless the frame moves w's ring.</summary>
        float3 NormalIn(int w, int frame)
        {
            var lo = _in.NormalStart[w];
            var hi = lo + _in.NormalCount[w] - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var m = _in.NormalFrames[mid].Frame;
                if (m == frame) return _in.NormalFrames[mid].N;
                if (m < frame) lo = mid + 1;
                else hi = mid - 1;
            }
            return _in.NormalRest[w];
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
            var ratio = (double)_in.Settings.DegenerateRatio;
            if (na2 <= ratio * ratio * nb2) return false;
            if (math.dot(nb, na) <= _in.Settings.FlipCosine * math.sqrt(nb2 * na2)) return false;
            return math.lengthsq(reference) < 1e-6f || math.dot(na, (double3)reference) > 0.0;
        }

        // ---------------------------------------------------------------- applying

        void Apply(in Candidate c)
        {
            var killed = CollapseWedge(c.U1, c.V1);
            if (c.U2 >= 0) killed += CollapseWedge(c.U2, c.V2);
            _live -= killed;
            _level = math.max(_level, c.Cost);
            var start = _in.Records.Length;
            _in.Records.Add(new int2(c.U1, c.V1));
            if (c.U2 >= 0) _in.Records.Add(new int2(c.U2, c.V2));
            _in.Steps.Add(new CollapseStep { Error = _level, TriangleCount = _live, RecordStart = start, RecordCount = _in.Records.Length - start });
            _dead[c.U1] = 1;
            if (c.U2 >= 0) _dead[c.U2] = 1;
            UpdateAround(c.V1, c.V2);
        }

        int CollapseWedge(int u, int v)
        {
            // The fan that replaces u's triangles covers what they covered, so it inherits their least clearance.
            var clearance = _clearance ? FanClearance(u) : 0f;
            var killed = 0;
            var head = -1;
            var tail = -1;
            for (var c = _firstCorner[u]; c != -1;)
            {
                var next = _cornerNext[c];
                var t = c / 3;
                if (_alive[t] != 0)
                {
                    var tri = _in.Triangles[t];
                    if (tri.x == v || tri.y == v || tri.z == v)
                    {
                        _alive[t] = 0;
                        killed++;
                    }
                    else
                    {
                        tri[c - t * 3] = v;
                        _in.Triangles[t] = tri;
                        if (_clearance) _in.TriangleClearance[t] = clearance;
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
            var ru = _in.Rest[u];
            var rv = _in.Rest[v];
            _merged.Clear();
            var i = _in.SparseStart[u];
            var ie = ShapeEnd(u);
            var j = _in.SparseStart[v];
            var je = ShapeEnd(v);
            while (i < ie || j < je)
            {
                var fu = i < ie ? _in.Sparse[i].Frame : int.MaxValue;
                var fv = j < je ? _in.Sparse[j].Frame : int.MaxValue;
                if (fu < fv)
                {
                    _merged.Add(new FrameQuadric { Frame = fu, Q = Quadric.Sum(rv, _in.Sparse[i].Q) });
                    i++;
                }
                else if (fv < fu)
                {
                    _merged.Add(new FrameQuadric { Frame = fv, Q = Quadric.Sum(_in.Sparse[j].Q, ru) });
                    j++;
                }
                else
                {
                    _merged.Add(new FrameQuadric { Frame = fu, Q = Quadric.Sum(_in.Sparse[i].Q, _in.Sparse[j].Q) });
                    i++;
                    j++;
                }
            }
            var shapes = _merged.Length;
            if (_bent) MergeBends(u, v);
            _in.Rest[v] = Quadric.Sum(rv, ru);
            // v's quadrics now stand for u's surface too, so v's removal is priced at the size either can be shown at.
            _multiplier[v] = math.max(_multiplier[v], _multiplier[u]);
            var uv = _in.Uv[v];
            uv.Add(_in.Uv[u]);
            _in.Uv[v] = uv;
            if (_shading)
            {
                var field = _in.NormalField[v];
                field.Add(_in.NormalField[u]);
                _in.NormalField[v] = field;
            }

            var n = _merged.Length;
            if (n > _sparseCap[v])
            {
                _wasted += _sparseCap[v];
                var cap = n + n / 2;
                _in.SparseStart[v] = _in.Sparse.Length;
                _sparseCap[v] = cap;
                _in.Sparse.Resize(_in.Sparse.Length + cap, NativeArrayOptions.UninitializedMemory);
                _peak = math.max(_peak, _in.Sparse.Length);
            }
            var at = _in.SparseStart[v];
            for (var k = 0; k < n; k++) _in.Sparse[at + k] = _merged[k];
            _in.SparseCount[v] = n;
            _shapeCount[v] = shapes;
            _wasted += _sparseCap[u];
            _in.SparseCount[u] = 0;
            _shapeCount[u] = 0;
            _sparseCap[u] = 0;
            if (_wasted > 4096 && _wasted * 2 > _in.Sparse.Length) CompactPool();
        }

        void CompactPool()
        {
            var copy = new NativeList<FrameQuadric>(_in.Sparse.Length - (int)_wasted, Allocator.Temp);
            for (var w = 0; w < _in.SparseStart.Length; w++)
            {
                if (_dead[w] != 0 || _sparseCap[w] == 0)
                {
                    _sparseCap[w] = 0;
                    continue;
                }
                var start = copy.Length;
                var count = _in.SparseCount[w];
                for (var k = 0; k < count; k++) copy.Add(_in.Sparse[_in.SparseStart[w] + k]);
                _in.SparseStart[w] = start;
                _sparseCap[w] = count;
            }
            _in.Sparse.Clear();
            _in.Sparse.AddRange(copy.AsArray());
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
                    var tri = _in.Triangles[t];
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
