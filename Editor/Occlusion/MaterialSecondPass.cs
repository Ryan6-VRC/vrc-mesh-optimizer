using System;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>
    /// The second pass a two-pass transparent material draws over the same triangles, with a cull of its own: set to
    /// the front, as lilToon sets it for two-pass, it draws the back of a triangle the main pass culls. Like an outline,
    /// only a triangle that might be hidden is judged by it.
    /// </summary>
    internal static class MaterialSecondPass
    {
        /// <summary>Poiyomi Two Pass's second pass cull. Only that shader declares it, and it always draws the pass.</summary>
        public const string PoiyomiCull = "_Cull2";

        /// <summary>
        /// lilToon's back pass cull. Every lilToon transparent variant declares it, but only the TwoPass variants draw the
        /// FORWARD_BACK pass it culls.
        /// </summary>
        public const string LilToonCull = "_PreCull";

        /// <summary>The sides the material's second pass draws, or null when it has none.</summary>
        public static FaceCulling? SidesOf(Material material)
        {
            if (material == null || material.shader == null) return null;
            if (material.HasProperty(PoiyomiCull)) return OccluderMaterials.CullingOf(material, PoiyomiCull);
            if (material.HasProperty(LilToonCull) && (MaterialOutline.HasPass(material, "FORWARD_BACK") || IsLilToonTwoPassName(material.shader.name)))
                return OccluderMaterials.CullingOf(material, LilToonCull);
            return null;
        }

        /// <summary>lilToon's own test for a two-pass variant: "TwoPass" in the last segment of the shader's name.</summary>
        internal static bool IsLilToonTwoPassName(string name)
        {
            var separator = name.LastIndexOf('/');
            return separator != -1 && name.IndexOf("TwoPass", separator + 1, StringComparison.Ordinal) != -1;
        }
    }
}
