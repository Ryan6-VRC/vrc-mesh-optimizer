namespace Ryan6Vrc.MeshOptimizer.Inspector
{
    /// <summary>
    /// The catalogue keys the inspector draws beyond the component's own field labels, which are
    /// <c>MeshOptimizer:&lt;field&gt;</c>. Every one is a constant here, so the localization test finds them all.
    /// </summary>
    internal static class InspectorKeys
    {
        public const string Advanced = "MeshOptimizer:advanced";
        public const string Preset = "MeshOptimizer:preset";
        public const string Preview = "MeshOptimizer:preview";
        public const string PreviewOff = "MeshOptimizer:preview:off";
        public const string PreviewPending = "MeshOptimizer:preview:pending";
        public const string PreviewResult = "MeshOptimizer:preview:result";
        public const string PreviewFailed = "MeshOptimizer:preview:failed";
        public const string PreviewNoteDescriptor = "MeshOptimizer:preview:note:descriptor";
        public const string PreviewNoteEveryKeyLive = "MeshOptimizer:preview:note:every-key-live";
        public const string PreviewProgress = "MeshOptimizer:preview:progress";
        public const string PreviewProgressInfo = "MeshOptimizer:preview:progress:info";
        public const string ListEmpty = "MeshOptimizer:list:empty";
        public const string ColumnExclude = "MeshOptimizer:column:exclude";
        public const string AlwaysOnHelp = "MeshOptimizer:alwaysOn:help";
        public const string AlwaysOnOutside = "MeshOptimizer:alwaysOn:outside";
        public const string MaterialsHelp = "MeshOptimizer:materials:help";
        public const string Check = "MeshOptimizer:check";
        public const string Override = "MeshOptimizer:override";
        public const string Verdict = "MeshOptimizer:verdict";
        public const string VerdictOpaque = "MeshOptimizer:verdict:opaque";
        public const string VerdictByAlpha = "MeshOptimizer:verdict:by-alpha";
        public const string VerdictSeeThrough = "MeshOptimizer:verdict:see-through";
        public const string VerdictUnchecked = "MeshOptimizer:verdict:unchecked";
    }
}
