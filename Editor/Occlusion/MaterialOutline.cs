using System;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>
    /// The outline a material draws. lilToon and Poiyomi draw one as a second pass over the same triangles, each pushed
    /// out along its normal and culled by <see cref="CullProperty"/>, front by default, so what shows of it is its back:
    /// on an open garment, the inside. Only a triangle that might be hidden is judged by it. As cover an outline counts
    /// for nothing, since reading a surface as blocking more deletes more.
    /// </summary>
    internal static class MaterialOutline
    {
        /// <summary>The outline pass's cull, as <c>UnityEngine.Rendering.CullMode</c>, in lilToon and Poiyomi alike.</summary>
        public const string CullProperty = "_OutlineCull";

        /// <summary>The sides the material's outline draws, or null when it draws none. An outline whose cull is not declared is taken to draw both.</summary>
        public static FaceCulling? SidesOf(Material material)
        {
            if (material == null || material.shader == null || !Draws(material)) return null;
            return material.HasProperty(CullProperty) ? OccluderMaterials.CullingOf(material, CullProperty) : FaceCulling.Off;
        }

        /// <summary>
        /// Poiyomi switches its outline with a toggle, and its outline pass clips every pixel while the toggle is under
        /// 0.01; locking a material whose toggle is off generates a shader with neither the toggle nor the pass, so it
        /// reads as no outline below. lilToon has no switch: an outline variant is a shader of its own, which lilToon tells
        /// by name and which carries a FORWARD_OUTLINE pass; every variant declares the outline's properties, so they say
        /// nothing. A pass named for an outline in any other shader is taken to draw one.
        /// </summary>
        static bool Draws(Material m)
        {
            if (m.HasProperty("_EnableOutlines"))
                return m.GetFloat("_EnableOutlines") >= 0.01f &&
                       !(m.HasProperty("_OutlineClipAtZeroWidth") && m.GetFloat("_OutlineClipAtZeroWidth") != 0f && PoiyomiWidthIsZero(m));
            // Poiyomi 7's toggle, named for the locker, which comments out what it switches off; its outline always clips a zero width.
            if (m.HasProperty("_commentIfZero_EnableOutlinePass"))
                return m.GetFloat("_commentIfZero_EnableOutlinePass") != 0f && !PoiyomiWidthIsZero(m);
            if (!HasOutlinePass(m) && !IsLilToonOutlineName(m.shader.name)) return false;
            // lilToon moves a zero-width hull to nowhere only when told to delete it; otherwise the hull lies flush on the
            // surface and still draws its back.
            return !(m.HasProperty("_OutlineWidth") && m.HasProperty("_OutlineDeleteMesh") &&
                     m.GetFloat("_OutlineWidth") == 0f && m.GetFloat("_OutlineDeleteMesh") != 0f);
        }

        /// <summary>
        /// Whether Poiyomi's zero-width clip drops every pixel: it clips where the width, scaled by a mask of at most 1,
        /// is under 0.001, and AudioLink adds between the two components of its size modifier to the width while it plays.
        /// </summary>
        static bool PoiyomiWidthIsZero(Material m)
        {
            if (!m.HasProperty("_LineWidth") || m.GetFloat("_LineWidth") > 0f) return false;
            if (!m.HasProperty("_AudioLinkOutlineSize")) return true;
            var mod = m.GetVector("_AudioLinkOutlineSize");
            return Mathf.Max(mod.x, mod.y) <= 0f;
        }

        /// <summary>Unity may report a pass name in upper case, so the match ignores case.</summary>
        static bool HasOutlinePass(Material m)
        {
            for (var p = 0; p < m.passCount; p++)
                if (m.GetPassName(p).IndexOf("OUTLINE", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// lilToon's own test for an outline variant: "Outline" in the last segment of the shader's name, or a last segment
        /// under "[Optional] OutlineOnly", the convention its custom shaders follow.
        /// </summary>
        internal static bool IsLilToonOutlineName(string name)
        {
            var separator = name.LastIndexOf('/');
            if (separator == -1 || separator + 1 == name.Length) return false;
            if (name.IndexOf("Outline", separator + 1, StringComparison.Ordinal) != -1) return true;
            const string outlineOnly = "/[Optional] OutlineOnly";
            return separator >= outlineOnly.Length && string.CompareOrdinal(name, separator - outlineOnly.Length, outlineOnly, 0, outlineOnly.Length) == 0;
        }
    }
}
