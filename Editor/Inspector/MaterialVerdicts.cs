using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;
using L10n = Ryan6Vrc.MeshOptimizer.Localization.L10n;

namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>What <see cref="OcclusionMode.Automatic"/> makes of a material's surface as cover.</summary>
    internal enum VerdictKind
    {
        /// <summary>Blocks wherever it is hit.</summary>
        Opaque,
        /// <summary>Blocks only where its alpha map reads solid.</summary>
        ByAlpha,
        /// <summary>Blocks nothing.</summary>
        SeeThrough,
    }

    internal readonly struct MaterialVerdict
    {
        public readonly VerdictKind Kind;
        /// <summary>With <see cref="VerdictKind.ByAlpha"/>, the share of the alpha map's texels that read solid; 1 otherwise when opaque, 0 when see-through.</summary>
        public readonly float SolidShare;

        public MaterialVerdict(VerdictKind kind, float solidShare)
        {
            Kind = kind;
            SolidShare = solidShare;
        }
    }

    /// <summary>
    /// The inspector's reading of what Automatic decides for a material, from the call the build's occlusion makes for a
    /// material no override names (<c>AlphaReader.Read</c> at Automatic, in <c>OccluderMaterials</c>), so the two cannot
    /// disagree about the same material. Reading samples textures through the GPU, so it runs only when the user asks,
    /// and each verdict is kept until its material is edited.
    /// </summary>
    internal static class MaterialVerdicts
    {
        static readonly Dictionary<Material, (MaterialVerdict verdict, int dirtyCount)> Checked =
            new Dictionary<Material, (MaterialVerdict, int)>();

        public static MaterialVerdict Of(Material material)
        {
            var surface = AlphaReader.Read(material, OcclusionMode.Automatic);
            if (!surface.Occludes) return new MaterialVerdict(VerdictKind.SeeThrough, 0f);
            if (surface.Alpha == null) return new MaterialVerdict(VerdictKind.Opaque, 1f);
            var map = surface.Alpha;
            var solid = 0;
            foreach (var a in map.Alpha)
                if (a >= map.SolidFrom) solid++;
            return new MaterialVerdict(VerdictKind.ByAlpha, map.Alpha.Length == 0 ? 0f : (float)solid / map.Alpha.Length);
        }

        public static void Check(Material material)
        {
            if (material != null) Checked[material] = (Of(material), EditorUtility.GetDirtyCount(material));
        }

        /// <summary>The verdict kept for a material, unless it was never checked or has been edited since.</summary>
        public static bool TryGet(Material material, out MaterialVerdict verdict)
        {
            verdict = default;
            if (material == null || !Checked.TryGetValue(material, out var kept)) return false;
            if (kept.dirtyCount != EditorUtility.GetDirtyCount(material))
            {
                Checked.Remove(material);
                return false;
            }
            verdict = kept.verdict;
            return true;
        }

        /// <summary>The kept verdict as a label with its tooltip, or the not-checked line.</summary>
        public static GUIContent Describe(Material material)
        {
            if (!TryGet(material, out var verdict)) return L10n.G(InspectorKeys.VerdictUnchecked);
            switch (verdict.Kind)
            {
                case VerdictKind.Opaque:
                    return L10n.G(InspectorKeys.VerdictOpaque);
                case VerdictKind.ByAlpha:
                    // Floored, so a map with one see-through texel never reads 100.
                    var percent = Mathf.FloorToInt(verdict.SolidShare * 100f);
                    return new GUIContent(string.Format(L10n.Tr(InspectorKeys.VerdictByAlpha), percent), L10n.Tooltip(InspectorKeys.VerdictByAlpha));
                default:
                    return L10n.G(InspectorKeys.VerdictSeeThrough);
            }
        }

        /// <summary>The override a user would set on a material Automatic misreads: the opposite of what it read.</summary>
        public static OcclusionMode Opposite(VerdictKind kind) =>
            kind == VerdictKind.Opaque ? OcclusionMode.NeverOccludes : OcclusionMode.ForceOpaque;
    }
}
