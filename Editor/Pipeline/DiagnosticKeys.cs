namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>
    /// Every key the package reports a problem under. Each has a title, a ":description" and a ":hint" in both
    /// language files; the substitutions each takes are noted.
    /// </summary>
    internal static class DiagnosticKeys
    {
        /// <summary>{0} the renderer, {1} the mesh's name.</summary>
        public const string NotReadable = "MeshOptimizer:diag:not-readable";
        /// <summary>{0} the renderer.</summary>
        public const string Cloth = "MeshOptimizer:diag:cloth";
        /// <summary>{0} the renderer, {1} the stage, {2} the exception's message.</summary>
        public const string RendererFailed = "MeshOptimizer:diag:renderer-failed";
        /// <summary>{0} the exception's message.</summary>
        public const string OcclusionFailed = "MeshOptimizer:diag:occlusion-failed";
        /// <summary>{0} the triangles reached, {1} the budget.</summary>
        public const string BudgetNotMet = "MeshOptimizer:diag:budget-not-met";
        /// <summary>{0} the renderer.</summary>
        public const string AlwaysOnIsOff = "MeshOptimizer:diag:always-on-is-off";
        /// <summary>{0} the paths of every component found, {1} the one used.</summary>
        public const string MultipleComponents = "MeshOptimizer:diag:multiple-components";
        /// <summary>{0} the exception's message.</summary>
        public const string PassFailed = "MeshOptimizer:diag:pass-failed";

        public static readonly string[] All =
        {
            NotReadable, Cloth, RendererFailed, OcclusionFailed, BudgetNotMet, AlwaysOnIsOff, MultipleComponents, PassFailed,
        };
    }
}
