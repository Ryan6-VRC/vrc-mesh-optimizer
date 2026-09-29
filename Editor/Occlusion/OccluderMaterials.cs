using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Runtime;
using Unity.Mathematics;
using UnityEditor;
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

        /// <summary>Overrides by the material the user named, resolved to its original.</summary>
        readonly Dictionary<Object, (Material named, OcclusionMode mode)> _overrides = new Dictionary<Object, (Material, OcclusionMode)>();
        /// <summary>Never-occludes overrides by their material's name, for <see cref="PlausibleClone"/>.</summary>
        readonly Dictionary<string, List<Object>> _neverByName = new Dictionary<string, List<Object>>();
        readonly HashSet<Object> _matched = new HashSet<Object>();
        readonly Dictionary<(Material, OcclusionMode), OccluderSubmesh> _read = new Dictionary<(Material, OcclusionMode), OccluderSubmesh>();

        public OccluderMaterials(IEnumerable<MaterialOcclusion> overrides)
        {
            if (overrides == null) return;
            foreach (var o in overrides)
                if (o != null && o.material != null) _overrides[Original(o.material)] = (o.material, o.mode);
            foreach (var kv in _overrides)
            {
                if (kv.Value.mode != OcclusionMode.NeverOccludes) continue;
                var name = CloneName(kv.Value.named.name);
                if (!_neverByName.TryGetValue(name, out var list)) _neverByName[name] = list = new List<Object>();
                list.Add(kv.Key);
            }
            // A name another kind of override also carries cannot say whose clone a material is, so it matches nothing.
            foreach (var kv in _overrides)
                if (kv.Value.mode != OcclusionMode.NeverOccludes) _neverByName.Remove(CloneName(kv.Value.named.name));
        }

        /// <summary>
        /// The material an NDMF plugin replaced this one from, following the chain back to the first, or the material
        /// itself. A plugin that clones a material and records the clone with <c>ObjectRegistry.RegisterReplacedObject</c>
        /// makes the clone's reference resolve to the original's, which is what the user named. VRCFury records nothing;
        /// <see cref="PlausibleClone"/> covers what it clones. The lookup does not create a reference, so it changes
        /// nothing for plugins that register replacements later.
        /// </summary>
        internal static Object Original(Material material)
        {
            var reference = ObjectRegistry.ActiveRegistry?.GetReference(material, false);
            return reference?.Object != null ? reference.Object : material;
        }

        /// <summary>The override for a material by its original, or a never-occludes one it is plausibly a clone of, noting that it matched.</summary>
        OcclusionMode ModeOf(Material material)
        {
            var original = Original(material);
            if (_overrides.TryGetValue(original, out var o))
            {
                _matched.Add(original);
                return o.mode;
            }
            if (!PlausibleClone(material, out var named)) return OcclusionMode.Automatic;
            // Several never-occludes overrides on one name all apply, but none can be said to have matched.
            if (named.Count == 1) _matched.Add(named[0]);
            return OcclusionMode.NeverOccludes;
        }

        /// <summary>
        /// Whether the material is plausibly a build-time clone of one a never-occludes override names. VRCFury clones a
        /// material with <c>Object.Instantiate</c>, gives the clone the original's name, records it nowhere, and may
        /// then change its shader (SPS, a Poiyomi lock) and its properties, so what survives is the name and that the
        /// clone is no asset. A wrong match here only keeps triangles, so it matches on exactly that, and also past
        /// Unity's " (Clone)" suffix. An always-opaque override never matches this way, since a wrong match there
        /// hides triangles.
        /// </summary>
        bool PlausibleClone(Material material, out List<Object> named)
        {
            named = null;
            return !EditorUtility.IsPersistent(material) && _neverByName.TryGetValue(CloneName(material.name), out named);
        }

        static string CloneName(string name)
        {
            const string suffix = " (Clone)";
            while (name.EndsWith(suffix)) name = name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        /// <summary>The overrides that match no material of any of <paramref name="renderers"/>.</summary>
        public List<Material> Unmatched(IEnumerable<Renderer> renderers)
        {
            foreach (var r in renderers)
                foreach (var m in r.sharedMaterials)
                    if (m != null) ModeOf(m);
            return _overrides.Where(kv => !_matched.Contains(kv.Key)).Select(kv => kv.Value.named).ToList();
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
            var mode = ModeOf(material);
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
        public static FaceCulling CullingOf(Material material) => CullingOf(material, CullProperty);

        /// <summary>A cull property read as <c>UnityEngine.Rendering.CullMode</c>; back when the material does not declare it.</summary>
        public static FaceCulling CullingOf(Material material, string property)
        {
            if (material == null || !material.HasProperty(property)) return FaceCulling.Back;
            switch (Mathf.RoundToInt(material.GetFloat(property)))
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
