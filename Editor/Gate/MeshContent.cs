using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Gate
{
    /// <summary>What a mesh holds, as one number, so two builds' meshes compare by content rather than by what was counted of them.</summary>
    internal static class MeshContent
    {
        /// <summary>
        /// FNV-1a over everything a written mesh holds, in order: positions, normals, tangents, colours, UV0 to UV3, bone
        /// weights and their counts, bind poses, each submesh's topology and indices, and every shape key's name, frame
        /// weights and deltas. Bounds, UV4 to UV7 and attribute formats are left out: the writer copies bounds from the source
        /// and every attribute byte for byte from the vertices it keeps, so where these agree those do. Printed as 16 hex
        /// digits, it reads the same as the measurement runner's hash of the same mesh, which hashes the same bytes in this
        /// order.
        /// </summary>
        public static ulong Hash(Mesh m)
        {
            ulong h = 14695981039346656037UL;
            void I(int x) { unchecked { for (var k = 0; k < 4; k++) { h ^= (byte)(x >> (8 * k)); h *= 1099511628211UL; } } }
            void Fl(float x) => I(BitConverter.SingleToInt32Bits(x));
            void V3(Vector3[] a) { I(a.Length); foreach (var v in a) { Fl(v.x); Fl(v.y); Fl(v.z); } }
            V3(m.vertices); V3(m.normals);
            var tangents = m.tangents; I(tangents.Length); foreach (var t in tangents) { Fl(t.x); Fl(t.y); Fl(t.z); Fl(t.w); }
            var colors = m.colors32; I(colors.Length); foreach (var c in colors) I(c.r | c.g << 8 | c.b << 16 | c.a << 24);
            var uv = new List<Vector4>();
            for (var ch = 0; ch < 4; ch++) { uv.Clear(); m.GetUVs(ch, uv); I(uv.Count); foreach (var u in uv) { Fl(u.x); Fl(u.y); Fl(u.z); Fl(u.w); } }
            var per = m.GetBonesPerVertex(); I(per.Length); foreach (var b in per) I(b);
            var weights = m.GetAllBoneWeights(); I(weights.Length); foreach (var w in weights) { I(w.boneIndex); Fl(w.weight); }
            var poses = m.bindposes; I(poses.Length); foreach (var p in poses) for (var k = 0; k < 16; k++) Fl(p[k]);
            I(m.subMeshCount);
            for (var s = 0; s < m.subMeshCount; s++) { I((int)m.GetTopology(s)); var idx = m.GetIndices(s); I(idx.Length); foreach (var i in idx) I(i); }
            I(m.blendShapeCount);
            var dv = new Vector3[m.vertexCount]; var dn = new Vector3[m.vertexCount]; var dt = new Vector3[m.vertexCount];
            for (var k = 0; k < m.blendShapeCount; k++)
            {
                foreach (var ch in m.GetBlendShapeName(k)) I(ch);
                var frames = m.GetBlendShapeFrameCount(k); I(frames);
                for (var f = 0; f < frames; f++)
                {
                    Fl(m.GetBlendShapeFrameWeight(k, f));
                    m.GetBlendShapeFrameVertices(k, f, dv, dn, dt);
                    V3(dv); V3(dn); V3(dt);
                }
            }
            return h;
        }
    }
}
