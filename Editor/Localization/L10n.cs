using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Localization
{
    /// <summary>
    /// The package's strings, through NDMF's localizer, so the language follows NDMF's language switcher.
    /// A key's tooltip is the key followed by ":tooltip". A missing key falls back to English, then to the
    /// key itself in angle brackets.
    /// </summary>
    /// <remarks>
    /// Built on first use rather than at domain load: the localizer reads its files once, when constructed, and on
    /// a first import they may not be loadable yet at domain load. Both languages are ones NDMF itself registers,
    /// so the switcher offers them either way.
    /// </remarks>
    internal static class L10n
    {
        const string Folder = "Packages/com.ryan6vrc.mesh-optimizer/Editor/Localization/";

        public static readonly Localizer Localizer = new Localizer("en-us", () => new[] { "en-us.po", "ja-jp.po" }
            .Select(file => AssetDatabase.LoadAssetAtPath<LocalizationAsset>(Folder + file))
            .Where(asset => asset != null)
            .ToList());

        public static string Tr(string key) => Localizer.GetLocalizedString(key);

        /// <summary>
        /// The key's string, or <paramref name="english"/> when the localizer has none, as when its files were not yet
        /// loadable. For text a build logs or records, which must never become the key in angle brackets.
        /// </summary>
        public static string Tr(string key, string english) => Localizer.TryGetLocalizedString(key, out var s) ? s : english;

        public static string Tooltip(string key) => Localizer.TryGetLocalizedString(key + ":tooltip", out var tip) ? tip : null;

        /// <summary>A label and its tooltip, for an IMGUI field.</summary>
        public static GUIContent G(string key) => new GUIContent(Tr(key), Tooltip(key));
    }
}
