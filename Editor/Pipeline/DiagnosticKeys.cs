namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>
    /// Every key the package reports a problem under. Each has a title, a ":description" and a ":hint" in both
    /// language files; each summary says what the problem means to the user reading NDMF's error report, and the
    /// substitutions it takes. NDMF shows a <see cref="Diagnostic.Warning"/> one as non-fatal and any other as information.
    /// </summary>
    internal static class DiagnosticKeys
    {
        /// <summary>A renderer left as it was: its mesh has Read/Write off and the avatar is built in play mode. {0} the renderer, {1} the mesh's name.</summary>
        public const string NotReadable = "MeshOptimizer:diag:not-readable";
        /// <summary>A renderer left as it was: a Cloth component addresses its vertices by index. {0} the renderer.</summary>
        public const string Cloth = "MeshOptimizer:diag:cloth";
        /// <summary>A stage threw on one renderer while every other was still optimized; one that failed at decimation keeps occlusion's removals, at extraction or writing it is left as it was. {0} the renderer, {1} the stage, {2} the exception's message.</summary>
        public const string RendererFailed = "MeshOptimizer:diag:renderer-failed";
        /// <summary>The occlusion solve threw, so no renderer has hidden triangles removed; decimation still runs. {0} the exception's message.</summary>
        public const string OcclusionFailed = "MeshOptimizer:diag:occlusion-failed";
        /// <summary>Re-judging threw, so every removed triangle stays removed, and one whose cover decimation took may show as a hole. {0} the exception's message.</summary>
        public const string RejudgeFailed = "MeshOptimizer:diag:rejudge-failed";
        /// <summary>The avatar draws more triangles than the budget for a reason other than the error ceiling: too much of it is never cut, hidden triangles were written back, or a write failed. {0} the triangles reached, {1} the budget, {2} of the triangles reached, the hidden ones written back because the cut took their cover.</summary>
        public const string BudgetNotMet = "MeshOptimizer:diag:budget-not-met";
        /// <summary>The avatar draws more triangles than the budget because reaching it would move the surface past the component's max error. {0} the triangles reached, {1} the budget, {2} the error ceiling in millimetres, which stopped the cut first.</summary>
        public const string BudgetCeilingHeld = "MeshOptimizer:diag:budget-ceiling-held";
        /// <summary>A renderer listed as always on is disabled or inactive as the avatar loads, so it hides nothing. {0} the renderer.</summary>
        public const string AlwaysOnIsOff = "MeshOptimizer:diag:always-on-is-off";
        /// <summary>A renderer listed as always on is not under the avatar being built, so it hides nothing on it. {0} the renderer, {1} the avatar being built.</summary>
        public const string AlwaysOnOutsideAvatar = "MeshOptimizer:diag:always-on-outside-avatar";
        /// <summary>The avatar carries more than one component, and only one's settings are used. {0} the paths of every component found, {1} the one used.</summary>
        public const string MultipleComponents = "MeshOptimizer:diag:multiple-components";
        /// <summary>The whole pass threw, and the avatar is left unchanged. {0} the exception's message.</summary>
        public const string PassFailed = "MeshOptimizer:diag:pass-failed";
        /// <summary>A see-through material override names a material no renderer uses at build, so it does nothing, and whatever material replaced it may still hide triangles. {0} the material the override names.</summary>
        public const string NeverOccludesUnmatched = "MeshOptimizer:diag:never-occludes-unmatched";

        /// <summary>A renderer left as it was: a vertex's position or texture coordinate is not a finite number. {0} the renderer, {1} the vertex's index.</summary>
        public const string NonFiniteVertex = "MeshOptimizer:diag:non-finite-vertex";
        /// <summary>A renderer left as it was: a shape key moves a vertex by a delta that is not a finite number. {0} the renderer, {1} the shape key's name, {2} the vertex's index.</summary>
        public const string NonFiniteShapeKey = "MeshOptimizer:diag:non-finite-shape-key";
        /// <summary>A renderer left as it was: two of its shape keys share a name, which a written mesh cannot keep apart. {0} the renderer, {1} the shape key's name.</summary>
        public const string DuplicateShapeKey = "MeshOptimizer:diag:duplicate-shape-key";
        /// <summary>The gate measurement this build was switched to run threw; the avatar is optimized as without it, and the report has no gate figures. {0} the exception's message.</summary>
        public const string GateFailed = "MeshOptimizer:diag:gate-failed";

        /// <summary>The key of a stage's name, which <see cref="RendererFailed"/> shows as its {1}.</summary>
        public static string Stage(PipelineStage stage) => "PipelineStage:" + stage;

        /// <summary>Every key above, which the localization test checks both language files against.</summary>
        public static readonly string[] All =
        {
            NotReadable, Cloth, RendererFailed, OcclusionFailed, RejudgeFailed, BudgetNotMet, BudgetCeilingHeld, AlwaysOnIsOff, AlwaysOnOutsideAvatar, MultipleComponents, PassFailed, NeverOccludesUnmatched,
            NonFiniteVertex, NonFiniteShapeKey, DuplicateShapeKey,
            GateFailed,
        };
    }
}
