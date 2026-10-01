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
        /// <summary>{0} the exception's message.</summary>
        public const string RejudgeFailed = "MeshOptimizer:diag:rejudge-failed";
        /// <summary>{0} the triangles reached, {1} the budget, {2} of the triangles reached, the hidden ones written back because the cut took their cover.</summary>
        public const string BudgetNotMet = "MeshOptimizer:diag:budget-not-met";
        /// <summary>{0} the triangles reached, {1} the budget, {2} the error ceiling in millimetres, which stopped the cut first.</summary>
        public const string BudgetCeilingHeld = "MeshOptimizer:diag:budget-ceiling-held";
        /// <summary>{0} the renderer.</summary>
        public const string AlwaysOnIsOff = "MeshOptimizer:diag:always-on-is-off";
        /// <summary>{0} the paths of every component found, {1} the one used.</summary>
        public const string MultipleComponents = "MeshOptimizer:diag:multiple-components";
        /// <summary>{0} the exception's message.</summary>
        public const string PassFailed = "MeshOptimizer:diag:pass-failed";
        /// <summary>{0} the material the override names.</summary>
        public const string NeverOccludesUnmatched = "MeshOptimizer:diag:never-occludes-unmatched";

        /// <summary>{0} the renderer, {1} the vertex's index.</summary>
        public const string NonFiniteVertex = "MeshOptimizer:diag:non-finite-vertex";
        /// <summary>{0} the renderer, {1} the shape key's name, {2} the vertex's index.</summary>
        public const string NonFiniteShapeKey = "MeshOptimizer:diag:non-finite-shape-key";
        /// <summary>{0} the renderer, {1} the shape key's name.</summary>
        public const string DuplicateShapeKey = "MeshOptimizer:diag:duplicate-shape-key";
        /// <summary>{0} the exception's message.</summary>
        public const string GateFailed = "MeshOptimizer:diag:gate-failed";

        /// <summary>The key of a stage's name, which <see cref="RendererFailed"/> shows as its {1}.</summary>
        public static string Stage(PipelineStage stage) => "PipelineStage:" + stage;

        public static readonly string[] All =
        {
            NotReadable, Cloth, RendererFailed, OcclusionFailed, RejudgeFailed, BudgetNotMet, BudgetCeilingHeld, AlwaysOnIsOff, MultipleComponents, PassFailed, NeverOccludesUnmatched,
            NonFiniteVertex, NonFiniteShapeKey, DuplicateShapeKey,
            GateFailed,
        };
    }
}
