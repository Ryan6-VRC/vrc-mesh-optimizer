using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Unity.Collections;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Decimation
{
    /// <summary>The avatar's bones as decimation bends them (<see cref="BendRig"/>).</summary>
    internal static class BendRigs
    {
        /// <summary>Per bone id, the skin weight every one of <paramref name="meshes"/>' vertices put on it.</summary>
        public static float[] SkinWeightPerBone(IEnumerable<EvalMesh> meshes, int bones)
        {
            var weight = new float[bones];
            foreach (var mesh in meshes)
                foreach (var w in mesh.Weights)
                    if (w.Bone >= 0 && w.Bone < bones) weight[w.Bone] += w.Weight;
            return weight;
        }

        /// <summary>
        /// The avatar's bones as decimation bends them, in avatar-root space. A bone runs toward the child whose subtree
        /// carries the most of <paramref name="weight"/>, the skin weight per bone id: the next bone of its chain, which moves
        /// the most surface, rather than a ribbon or accessory hung on it, which may have more transforms and far less
        /// surface. Among children carrying none, the one with the most transforms under it; null weighs every bone alike.
        /// </summary>
        public static BendRig Of(AvatarBones bones, float[] weight)
        {
            var n = bones.Count;
            var worldToRoot = RendererExtraction.WorldToRoot(bones.Root);
            var mass = new double[n];
            var size = new int[n];
            // Ids are depth first, so a child's id is above its parent's and walking back sums subtrees.
            for (var b = n - 1; b >= 0; b--)
            {
                size[b] += 1;
                if (weight != null && b < weight.Length) mass[b] += weight[b];
                var p = bones.Parents[b];
                if (p < 0) continue;
                size[p] += size[b];
                mass[p] += mass[b];
            }
            var chain = new int[n];
            for (var b = 0; b < n; b++) chain[b] = -1;
            for (var b = 0; b < n; b++)
            {
                var p = bones.Parents[b];
                if (p < 0) continue;
                var c = chain[p];
                if (c < 0 || mass[b] > mass[c] || mass[b] == mass[c] && size[b] > size[c]) chain[p] = b;
            }
            var rig = new BendRig
            {
                Parent = bones.ParentArray(Allocator.Persistent),
                Position = new NativeArray<float3>(n, Allocator.Persistent),
                Axis = new NativeArray<float3>(n, Allocator.Persistent),
                Right = new NativeArray<float3>(n, Allocator.Persistent),
            };
            for (var b = 0; b < n; b++)
            {
                var t = bones.Transforms[b];
                rig.Position[b] = worldToRoot.MultiplyPoint3x4(t.position);
                rig.Right[b] = math.normalizesafe((float3)worldToRoot.MultiplyVector(t.right));
            }
            for (var b = 0; b < n; b++)
                rig.Axis[b] = chain[b] >= 0 ? math.normalizesafe(rig.Position[chain[b]] - rig.Position[b]) : float3.zero;
            return rig;
        }
    }
}
