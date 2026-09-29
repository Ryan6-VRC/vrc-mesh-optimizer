using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Mathematics;
using UnityEngine;
using CoreAlphaMap = Ryan6Vrc.MeshOptimizer.Core.Occlusion.AlphaMap;
using EditorAlphaMap = Ryan6Vrc.MeshOptimizer.Extraction.AlphaMap;

namespace Ryan6Vrc.MeshOptimizer.Occlusion
{
    /// <summary>
    /// How each submesh of an always-on renderer blocks rays: whether it can, where its alpha lets rays through,
    /// and which side of its faces renders. Every guess here errs toward blocking less, because a surface counted
    /// as cover that is not deletes triangles someone can see, while the reverse only keeps a few.
    /// </summary>
    internal sealed class OccluderMaterials
    {
        /// <summary>
        /// The cull property lilToon and Poiyomi both declare, as <c>UnityEngine.Rendering.CullMode</c> (0 off,
        /// 1 front, 2 back) and bind to their drawing passes with <c>Cull [_Cull]</c>.
        /// </summary>
        public const string CullProperty = "_Cull";

        readonly Dictionary<Material, OcclusionMode> _overrides = new Dictionary<Material, OcclusionMode>();
        readonly Dictionary<(Material, OcclusionMode), OccluderSubmesh> _read = new Dictionary<(Material, OcclusionMode), OccluderSubmesh>();

        public OccluderMaterials(IEnumerable<MaterialOcclusion> overrides)
        {
            if (overrides == null) return;
            foreach (var o in overrides)
                if (o != null && o.material != null) _overrides[o.material] = o.mode;
        }

        /// <summary>Per submesh of the renderer, its material's part in occlusion. Each material is read once.</summary>
        public OccluderSubmesh[] For(Renderer renderer, int submeshCount)
        {
            var materials = renderer.sharedMaterials;
            var result = new OccluderSubmesh[submeshCount];
            for (var s = 0; s < submeshCount; s++)
                result[s] = For(s < materials.Length ? materials[s] : null);
            return result;
        }

        OccluderSubmesh For(Material material)
        {
            // A slot with no material draws nothing a player sees.
            if (material == null) return new OccluderSubmesh { Mode = OccluderMode.NeverOccludes };
            var mode = _overrides.TryGetValue(material, out var m) ? m : OcclusionMode.Automatic;
            if (_read.TryGetValue((material, mode), out var known)) return known;

            var surface = AlphaReader.Read(material, mode);
            var sub = new OccluderSubmesh { Culling = CullingOf(material) };
            if (!surface.Occludes) sub.Mode = OccluderMode.NeverOccludes;
            else if (surface.Alpha == null) sub.Mode = mode == OcclusionMode.ForceOpaque ? OccluderMode.Opaque : OccluderMode.Auto;
            else sub.Alpha = Convert(surface.Alpha);
            _read[(material, mode)] = sub;
            return sub;
        }

        /// <summary>
        /// The side of a face the material draws. A shader with the <see cref="CullProperty"/> convention is read from
        /// it; Unity's Standard and legacy shaders, which state no cull, draw the front only, and so does any other
        /// shader without the property, since back is Unity's default and draws less than off. Extra passes (an
        /// outline hull, a two-pass transparent's back pass) and extra material slots only add pixels, so reading the
        /// main pass alone blocks less than the renderer does.
        /// </summary>
        public static FaceCulling CullingOf(Material material)
        {
            if (material == null || !material.HasProperty(CullProperty)) return FaceCulling.Back;
            switch (Mathf.RoundToInt(material.GetFloat(CullProperty)))
            {
                case 0: return FaceCulling.Off;
                case 1: return FaceCulling.Front;
                default: return FaceCulling.Back;
            }
        }

        /// <summary>The Editor's alpha map, as occlusion samples it: the same texels, square, threshold and wrap.</summary>
        public static CoreAlphaMap Convert(EditorAlphaMap map) =>
            new CoreAlphaMap(map.Width, map.Height, map.Alpha, map.SolidFrom)
            {
                ScaleOffset = new float4(map.MainST.x, map.MainST.y, map.MainST.z, map.MainST.w),
                WrapU = Wrap(map.WrapU),
                WrapV = Wrap(map.WrapV),
            };

        static AlphaWrap Wrap(TextureWrapMode mode) => mode switch
        {
            TextureWrapMode.Clamp => AlphaWrap.Clamp,
            TextureWrapMode.Mirror => AlphaWrap.Mirror,
            TextureWrapMode.MirrorOnce => AlphaWrap.MirrorOnce,
            _ => AlphaWrap.Repeat,
        };
    }
}
