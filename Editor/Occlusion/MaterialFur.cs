using System;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>
    /// Fur a material grows off its triangles. A fur pass extrudes shells and strands from every triangle it draws, along
    /// a fur vector centimetres long and bent by gravity, with a cull of its own, so fur shows past whatever covers the
    /// surface it grows from: past a hem, or through cloth lying within its length. A triangle drawn with fur is therefore
    /// never hidden, and since the strands are emitted per triangle, the fur's density is its triangle count, which no
    /// surface error measures, so decimation keeps every one of those triangles too.
    /// </summary>
    internal static class MaterialFur
    {
        /// <summary>
        /// Whether the material draws fur. lilToon names its fur passes FORWARD_FUR, FORWARD_ADD_FUR and their _PRE twins,
        /// and its fur-only shaders take them by UsePass, which keeps the name. Poiyomi's fur shaders name their fur passes
        /// Base and Add like any other, so they are told by the shader's name, which a locked Poiyomi shader keeps within
        /// its own.
        /// </summary>
        public static bool Draws(Material material)
        {
            if (material == null || material.shader == null) return false;
            if (MaterialOutline.HasPass(material, "FUR")) return true;
            var name = material.shader.name;
            return name.IndexOf(".poiyomi/", StringComparison.Ordinal) >= 0 && name.IndexOf("Lil Fur", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Per submesh, whether it is drawn with fur. Unity draws a material slot past the last submesh over the last
        /// submesh again, so a fur material there makes the last submesh furred.
        /// </summary>
        public static bool[] Submeshes(Renderer renderer, int submeshCount)
        {
            var materials = renderer.sharedMaterials;
            var result = new bool[submeshCount];
            for (var s = 0; s < submeshCount && s < materials.Length; s++) result[s] = Draws(materials[s]);
            if (submeshCount > 0)
                for (var k = submeshCount; k < materials.Length; k++)
                    result[submeshCount - 1] |= Draws(materials[k]);
            return result;
        }

        /// <summary>
        /// A triangle drawn with fur grows it past its cover, so it is never deleted: clears <paramref name="hidden"/> on
        /// every triangle of a furred submesh. Like <see cref="UnstableBones.Keep"/>, this only clears flags after the
        /// solve. Returns how many triangles it kept.
        /// </summary>
        public static int Keep(Renderer renderer, int submeshCount, in EvalMesh eval, bool[] hidden)
        {
            var fur = Submeshes(renderer, submeshCount);
            var kept = 0;
            for (var t = 0; t < hidden.Length; t++)
            {
                if (!hidden[t] || !fur[eval.TriangleSubmesh[t]]) continue;
                hidden[t] = false;
                kept++;
            }
            return kept;
        }
    }
}
