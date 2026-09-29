using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Occlusion
{
    internal static class Reason
    {
        public const byte Covered = 0;
        public const byte Unweighted = 1;
        public const byte Swings = 2;
        public const byte SeeThrough = 3;
        public const byte Escaped = 4;
    }

    internal struct OccMaterial
    {
        public OccluderMode Mode;
        public FaceCulling Culling;
        public int Alpha;
    }

    internal struct AlphaInfo
    {
        public int Width;
        public int Height;
        public int Offset;
        /// <summary>A texel byte below this is see-through.</summary>
        public int ThresholdByte;
        public float4 ScaleOffset;
    }

    internal static class Cone
    {
        /// <summary>
        /// The rays in a frame whose z is the normal: the normal itself plus one ring per
        /// <paramref name="ringDeg"/> out to <paramref name="coneDeg"/>, each ring's azimuth count growing
        /// with its circumference from 8 to 24 so a wide cone is not sparser.
        /// </summary>
        public static NativeArray<float3> Directions(float coneDeg, float ringDeg, Allocator allocator)
        {
            var rings = math.max(1, (int)math.round(coneDeg / ringDeg));
            var step = math.radians(coneDeg) / rings;
            var count = 1;
            for (var r = 1; r <= rings; r++) count += Azimuths(step * r, step);
            var dirs = new NativeArray<float3>(count, allocator);
            var k = 0;
            dirs[k++] = new float3(0, 0, 1);
            for (var r = 1; r <= rings; r++)
            {
                var theta = step * r;
                var az = Azimuths(theta, step);
                math.sincos(theta, out var st, out var ct);
                for (var a = 0; a < az; a++)
                {
                    math.sincos(2f * math.PI * a / az, out var sp, out var cp);
                    dirs[k++] = math.normalize(new float3(cp * st, sp * st, ct));
                }
            }
            return dirs;
        }

        static int Azimuths(float theta, float step) =>
            math.min(24, math.max(8, (int)math.round(8f * math.sin(theta) / math.sin(step))));
    }

    /// <summary>
    /// The shared-weights rule for one sample. A ray is blocked only by an occluder face whose weight share
    /// on the sample's bones, widened by kin steps, reaches the share threshold and which renders opaque
    /// where the ray meets it. Every other face is transparent, so blocking is an any-hit query: the order
    /// of faces along a ray does not matter. The sample's own renderer is never consulted.
    /// </summary>
    internal struct CoverageKernel
    {
        const float BaryEps = 1e-6f;

        [ReadOnly] public NativeArray<float3> Cone;
        [ReadOnly] public NativeArray<int> BoneParent;
        [ReadOnly] public NativeArray<int> WeightStart;
        [ReadOnly] public NativeArray<BoneInfluence> Weights;
        [ReadOnly] public NativeArray<int3> Tris;
        [ReadOnly] public NativeArray<float2> Uv;
        [ReadOnly] public NativeArray<int> OccTri;
        [ReadOnly] public NativeArray<int> OccMesh;
        [ReadOnly] public NativeArray<OccMaterial> OccMat;
        [ReadOnly] public NativeArray<AlphaInfo> Alpha;
        [ReadOnly] public NativeArray<byte> Texels;
        [ReadOnly] public NativeArray<BvhNode> Nodes;
        [ReadOnly] public NativeArray<BvhTri> Prims;
        [ReadOnly] public NativeArray<int> MeshRoot;
        /// <summary>Occluder meshes with a tree, fewest live frames first, so a witness is found on geometry frames rarely move.</summary>
        [ReadOnly] public NativeArray<int> MeshOrder;
        /// <summary>Per occluder: deleted, so neither met nor blocking.</summary>
        [ReadOnly] public NativeArray<bool> Disabled;
        /// <summary>Per occluder: moved by the live frame under evaluation, so absent from the rest tree and present in the frame tree.</summary>
        [ReadOnly] public NativeArray<bool> Moved;
        [ReadOnly] public NativeArray<BvhNode> FrameNodes;
        [ReadOnly] public NativeArray<BvhTri> FramePrims;
        [ReadOnly] public NativeArray<int> SampleMesh;
        [ReadOnly] public NativeArray<int> SampleBoneStart;
        [ReadOnly] public NativeArray<int> SampleBones;
        /// <summary>Per sample: a reason decided before any ray (unweighted, no normal), or Covered to cast.</summary>
        [ReadOnly] public NativeArray<byte> SampleState;
        /// <summary>The mesh whose live frame is under evaluation, or -1 at rest.</summary>
        public int FrameMesh;
        public float Share;
        public float Reach;
        public float OriginOffset;
        public int Kin;

        public static ulong Bit(int mesh) => 1UL << math.min(mesh, 63);

        /// <summary>The rest bounds of the faces the frame under evaluation moves.</summary>
        public float3 RiskMin, RiskMax;

        /// <summary>
        /// Casts the sample's cone from <paramref name="p"/> about <paramref name="n"/>; returns Covered or the
        /// first failing ray's reason. With <paramref name="onlyRisky"/>, for a sample covered at rest and not
        /// moved by the frame, only rays through <see cref="RiskMin"/>..<see cref="RiskMax"/> are cast: any other
        /// ray's rest blockers are all unmoved, so it is still blocked.
        /// </summary>
        public byte Evaluate(int sample, float3 p, float3 n, out ulong witness, out int rays, bool onlyRisky = false)
        {
            witness = 0;
            rays = 0;
            var pre = SampleState[sample];
            if (pre != Reason.Covered) return pre;
            var mesh = SampleMesh[sample];

            // Every bone within Kin steps of the sample's own, as (ancestor, steps up to it): bone c is
            // allowed when some ancestor of c, j steps up, is listed with steps i and i + j <= Kin. That is
            // tree distance, the breadth-first parent-or-child walk, without a child table.
            var allowed = new FixedList512Bytes<int2>();
            for (var k = SampleBoneStart[sample]; k < SampleBoneStart[sample + 1]; k++)
            {
                var a = SampleBones[k];
                for (var i = 0; i <= Kin && a >= 0; i++)
                {
                    AddMin(ref allowed, a, i);
                    a = BoneParent[a];
                }
            }

            var up = math.abs(n.z) < 0.9f ? new float3(0, 0, 1) : new float3(1, 0, 0);
            var u = math.normalize(math.cross(n, up));
            var v = math.cross(n, u);
            var o = p + n * OriginOffset;
            var last = -1;
            for (var r = 0; r < Cone.Length; r++)
            {
                var c = Cone[r];
                var d = math.normalize(u * c.x + v * c.y + n * c.z);
                if (onlyRisky)
                {
                    var box = new BvhNode { Min = RiskMin, Max = RiskMax };
                    if (!Slab(box, o, new float3(SafeInv(d.x), SafeInv(d.y), SafeInv(d.z)), out _)) continue;
                }
                rays++;
                var res = CastRay(o, d, mesh, ref allowed, ref last, ref witness);
                if (res != Reason.Covered) return res;
            }
            return Reason.Covered;
        }

        static void AddMin(ref FixedList512Bytes<int2> list, int bone, int steps)
        {
            for (var i = 0; i < list.Length; i++)
            {
                if (list[i].x != bone) continue;
                if (list[i].y > steps) list[i] = new int2(bone, steps);
                return;
            }
            list.Add(new int2(bone, steps));
        }

        bool IsAllowed(int bone, ref FixedList512Bytes<int2> allowed)
        {
            var a = bone;
            for (var j = 0; j <= Kin && a >= 0; j++)
            {
                for (var i = 0; i < allowed.Length; i++)
                    if (allowed[i].x == a && allowed[i].y + j <= Kin) return true;
                a = BoneParent[a];
            }
            return false;
        }

        byte CastRay(float3 o, float3 d, int ownMesh, ref FixedList512Bytes<int2> allowed, ref int last, ref ulong witness)
        {
            bool metAny = false, metCoMoving = false;
            var inv = new float3(SafeInv(d.x), SafeInv(d.y), SafeInv(d.z));
            // In a frame, the rays re-cast are those toward the moved faces, which are then their likeliest blocker.
            if (FrameMesh >= 0 && FrameMesh != ownMesh && FrameNodes.Length > 0 &&
                Traverse(FrameNodes, FramePrims, 0, false, o, d, inv, ref allowed, ref metAny, ref metCoMoving))
            {
                witness |= Bit(FrameMesh);
                return Reason.Covered;
            }
            // The mesh that blocked the previous ray usually blocks this one.
            if (last >= 0 && Traverse(Nodes, Prims, MeshRoot[last], true, o, d, inv, ref allowed, ref metAny, ref metCoMoving))
            {
                witness |= Bit(last);
                return Reason.Covered;
            }
            for (var k = 0; k < MeshOrder.Length; k++)
            {
                var m = MeshOrder[k];
                if (m == ownMesh || m == last) continue;
                if (!Traverse(Nodes, Prims, MeshRoot[m], true, o, d, inv, ref allowed, ref metAny, ref metCoMoving)) continue;
                last = m;
                witness |= Bit(m);
                return Reason.Covered;
            }
            return metCoMoving ? Reason.SeeThrough : metAny ? Reason.Swings : Reason.Escaped;
        }

        static float SafeInv(float x) => math.abs(x) < 1e-12f ? (x < 0f ? -1e12f : 1e12f) : 1f / x;

        bool Slab(in BvhNode node, float3 o, float3 inv, out float tNear)
        {
            var t0 = (node.Min - o) * inv;
            var t1 = (node.Max - o) * inv;
            tNear = math.cmax(math.min(t0, t1));
            var tFar = math.cmin(math.max(t0, t1));
            return tNear <= tFar && tFar >= 0f && tNear <= Reach;
        }

        unsafe bool Traverse(NativeArray<BvhNode> nodes, NativeArray<BvhTri> prims, int root, bool skipMoved,
                             float3 o, float3 d, float3 inv, ref FixedList512Bytes<int2> allowed,
                             ref bool metAny, ref bool metCoMoving)
        {
            if (root < 0 || !Slab(nodes[root], o, inv, out _)) return false;
            var stack = stackalloc int[Bvh.MaxDepth + 8];
            var sp = 0;
            stack[sp++] = root;
            while (sp > 0)
            {
                var node = nodes[stack[--sp]];
                if (node.Count > 0)
                {
                    for (var i = node.First; i < node.First + node.Count; i++)
                    {
                        var t = prims[i];
                        if (!Intersect(t, o, d, out var det, out var bu, out var bv)) continue;
                        var occ = t.Occ;
                        if (Disabled[occ] || (skipMoved && Moved[occ])) continue;
                        metAny = true;
                        if (FaceShare(occ, ref allowed) < Share) continue;
                        if (!Renders(occ, det) || SeeThrough(occ, bu, bv))
                        {
                            metCoMoving = true;
                            continue;
                        }
                        return true;
                    }
                    continue;
                }
                var l = node.First;
                var hl = Slab(nodes[l], o, inv, out var tl);
                var hr = Slab(nodes[l + 1], o, inv, out var tr);
                if (hl && hr)
                {
                    // Nearer child on top: the first layer out is the likeliest blocker.
                    if (tl <= tr) { stack[sp++] = l + 1; stack[sp++] = l; }
                    else { stack[sp++] = l; stack[sp++] = l + 1; }
                }
                else if (hl) stack[sp++] = l;
                else if (hr) stack[sp++] = l + 1;
            }
            return false;
        }

        // Moller-Trumbore, both sides. det = -dot(d, cross(E1, E2)), and cross(E1, E2) is the face's front
        // normal under clockwise winding, so det < 0 means the front looks along the ray.
        bool Intersect(in BvhTri t, float3 o, float3 d, out float det, out float u, out float v)
        {
            u = v = 0f;
            var pv = math.cross(d, t.E2);
            det = math.dot(t.E1, pv);
            if (math.abs(det) < 1e-18f) return false;
            var invDet = 1f / det;
            var tv = o - t.V0;
            u = math.dot(tv, pv) * invDet;
            if (u < -BaryEps || u > 1f + BaryEps) return false;
            var qv = math.cross(tv, t.E1);
            v = math.dot(d, qv) * invDet;
            if (v < -BaryEps || u + v > 1f + BaryEps) return false;
            var dist = math.dot(t.E2, qv) * invDet;
            return dist > 0f && dist <= Reach;
        }

        /// <summary>The face's weight share on the allowed bones: the mean over its weighted corners of each corner's share.</summary>
        float FaceShare(int occ, ref FixedList512Bytes<int2> allowed)
        {
            var tri = Tris[OccTri[occ]];
            float total = 0f;
            var weighted = 0;
            for (var c = 0; c < 3; c++)
            {
                var vtx = tri[c];
                float sum = 0f, inSet = 0f;
                for (var k = WeightStart[vtx]; k < WeightStart[vtx + 1]; k++)
                {
                    var w = Weights[k];
                    if (w.Weight <= 0f) continue;
                    sum += w.Weight;
                    if (IsAllowed(w.Bone, ref allowed)) inSet += w.Weight;
                }
                if (sum <= 0f) continue;
                total += inSet / sum;
                weighted++;
            }
            return weighted > 0 ? total / weighted : 0f;
        }

        bool Renders(int occ, float det)
        {
            switch (OccMat[occ].Culling)
            {
                case FaceCulling.Off: return true;
                case FaceCulling.Front: return det > 0f;
                default: return det < 0f;
            }
        }

        bool SeeThrough(int occ, float bu, float bv)
        {
            var mat = OccMat[occ];
            if (mat.Mode != OccluderMode.Auto || mat.Alpha < 0) return false;
            var a = Alpha[mat.Alpha];
            var tri = Tris[OccTri[occ]];
            var uv = (1f - bu - bv) * Uv[tri.x] + bu * Uv[tri.y] + bv * Uv[tri.z];
            uv = uv * a.ScaleOffset.xy + a.ScaleOffset.zw;
            uv -= math.floor(uv);
            var x = uv.x * a.Width - 0.5f;
            var y = uv.y * a.Height - 0.5f;
            var x0 = (int)math.floor(x);
            var y0 = (int)math.floor(y);
            for (var dy = 0; dy <= 1; dy++)
            for (var dx = 0; dx <= 1; dx++)
            {
                var tx = Wrap(x0 + dx, a.Width);
                var ty = Wrap(y0 + dy, a.Height);
                if (Texels[a.Offset + ty * a.Width + tx] < a.ThresholdByte) return true;
            }
            return false;
        }

        static int Wrap(int i, int n)
        {
            var r = i % n;
            return r < 0 ? r + n : r;
        }
    }
}
