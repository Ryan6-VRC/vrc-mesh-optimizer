namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>The named tolerances the inspector offers, in its popup's order.</summary>
    internal enum TolerancePreset
    {
        Fine,
        Standard,
        Medium,
        Coarse,
        Custom,
    }

    /// <summary>
    /// The preset a tolerance reads as, and back. Nothing stores a preset: the component holds its tolerance alone, a saved
    /// component reads its preset from that, and any tolerance no preset names reads as <see cref="TolerancePreset.Custom"/>.
    /// Every value is exact in float, so a preset written and read back is the same preset.
    /// </summary>
    internal static class TolerancePresets
    {
        static readonly (TolerancePreset preset, float millimetres)[] Table =
        {
            (TolerancePreset.Fine, 0.5f),
            (TolerancePreset.Standard, 1f),
            (TolerancePreset.Medium, 2f),
            (TolerancePreset.Coarse, 3f),
        };

        /// <summary>The preset whose tolerance is exactly <paramref name="millimetres"/>, else Custom.</summary>
        public static TolerancePreset Of(float millimetres)
        {
            foreach (var (preset, value) in Table)
                if (value == millimetres) return preset;
            return TolerancePreset.Custom;
        }

        /// <summary>The tolerance a preset names, in millimetres; false for Custom, which names none.</summary>
        public static bool TryTolerance(TolerancePreset preset, out float millimetres)
        {
            foreach (var (p, value) in Table)
                if (p == preset)
                {
                    millimetres = value;
                    return true;
                }
            millimetres = 0f;
            return false;
        }

        /// <summary>The preset's label key in the catalogues.</summary>
        public static string Key(TolerancePreset preset) => "TolerancePreset:" + preset;
    }
}
