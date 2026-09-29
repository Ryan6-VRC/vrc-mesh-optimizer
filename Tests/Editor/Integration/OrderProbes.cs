using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Ryan6Vrc.MeshOptimizer.OrderProbes.AvatarOptimizerProbe))]
[assembly: ExportsPlugin(typeof(Ryan6Vrc.MeshOptimizer.OrderProbes.TextureCompressorProbe))]
[assembly: ExportsPlugin(typeof(Ryan6Vrc.MeshOptimizer.OrderProbes.TexTransToolProbe))]

// Stand-ins, under the qualified names of the plugins the optimizer must run before, for a test venue that has
// none of them installed. NDMF breaks ties between unconstrained passes by plugin type name, and this namespace
// sorts before the optimizer's own, so each probe would run first unless the optimizer's constraint holds. They
// exist only where this package's tests compile.
namespace Ryan6Vrc.MeshOptimizer.OrderProbes
{
    internal sealed class AvatarOptimizerProbe : Plugin<AvatarOptimizerProbe>
    {
        public override string QualifiedName => "com.anatawa12.avatar-optimizer";
        protected override void Configure() => InPhase(BuildPhase.Optimizing).Run("Order probe: com.anatawa12.avatar-optimizer", _ => { });
    }

    internal sealed class TextureCompressorProbe : Plugin<TextureCompressorProbe>
    {
        public override string QualifiedName => "dev.limitex.avatar-compressor.texture";
        protected override void Configure() => InPhase(BuildPhase.Optimizing).Run("Order probe: dev.limitex.avatar-compressor.texture", _ => { });
    }

    internal sealed class TexTransToolProbe : Plugin<TexTransToolProbe>
    {
        public override string QualifiedName => "net.rs64.tex-trans-tool";
        protected override void Configure() => InPhase(BuildPhase.Optimizing).Run("Order probe: net.rs64.tex-trans-tool", _ => { });
    }
}
