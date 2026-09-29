using NUnit.Framework;
using Unity.Burst;

// Outside any namespace, so NUnit runs it before every test in the assembly and no filter selects a test without it.
// With Burst compilation off, every job in the core runs as managed code, which rounds differently from what a build
// runs, and a tolerance fails somewhere with nothing saying why; so the run fails here first, naming the setting. It
// reads the setting and never writes it: writing it saves a preference every Editor of the user's on the machine reads.
[SetUpFixture]
public class BurstIsOn
{
    [OneTimeSetUp]
    public void BurstCompilationIsEnabled() =>
        Assert.That(BurstCompiler.Options.IsEnabled,
            "Burst compilation is off in this Editor, so every job would run as managed code and every figure be of it: turn on " +
            "the Editor preference Jobs > Burst > Enable Compilation, or start the Editor without --burst-disable-compilation " +
            "and UNITY_BURST_DISABLE_COMPILATION");
}
