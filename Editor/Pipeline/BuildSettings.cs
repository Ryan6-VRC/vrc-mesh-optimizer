using System;
using Ryan6Vrc.MeshOptimizer.Core.Decimation;
using Ryan6Vrc.MeshOptimizer.Gate;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>
    /// The decimation settings a build runs with, whether it judges its removals again, what stands in its clearance
    /// surface, and what the gate measures once the meshes are applied. The component exposes none of them, and the
    /// decimation defaults live in <see cref="DecimationSettings.Default"/> alone; this is the one place a test or a
    /// measurement can hand a build other values, since a build reaches the pipeline through NDMF and not through a caller.
    /// Nothing in the package overrides them.
    /// </summary>
    internal static class BuildSettings
    {
        internal static DecimationSettings Decimation { get; private set; } = DecimationSettings.Default;

        /// <summary>Whether a build's cut judges the removed triangles again (<see cref="PipelineInput.Rejudge"/>); on unless overridden.</summary>
        internal static bool Rejudging { get; private set; } = true;

        /// <summary>Every build until the returned scope is disposed runs with <paramref name="decimation"/>; disposing restores what was before.</summary>
        internal static IDisposable Override(DecimationSettings decimation)
        {
            var before = Decimation;
            Decimation = decimation;
            return new Scope(() => Decimation = before);
        }

        /// <summary>Every build until the returned scope is disposed re-judges or not as <paramref name="rejudging"/> says; disposing restores what was before.</summary>
        internal static IDisposable OverrideRejudging(bool rejudging)
        {
            var before = Rejudging;
            Rejudging = rejudging;
            return new Scope(() => Rejudging = before);
        }

        /// <summary>Whether a build's clearance surface holds the triangles occlusion removed (<see cref="PipelineInput.RemovedAsNeighbours"/>); on unless overridden.</summary>
        internal static bool RemovedAsNeighbours { get; private set; } = true;

        /// <summary>Every build until the returned scope is disposed runs with <see cref="RemovedAsNeighbours"/> as <paramref name="on"/> says; disposing restores what was before.</summary>
        internal static IDisposable OverrideRemovedAsNeighbours(bool on)
        {
            var before = RemovedAsNeighbours;
            RemovedAsNeighbours = on;
            return new Scope(() => RemovedAsNeighbours = before);
        }

        /// <summary>Whether a build's re-judge lets a cut renderer cover from behind, within its recorded error (<see cref="PipelineInput.Setback"/>); on unless overridden.</summary>
        internal static bool Setback { get; private set; } = true;

        /// <summary>Every build until the returned scope is disposed runs with <see cref="Setback"/> as <paramref name="on"/> says; disposing restores what was before.</summary>
        internal static IDisposable OverrideSetback(bool on)
        {
            var before = Setback;
            Setback = on;
            return new Scope(() => Setback = before);
        }

        /// <summary>What a build measures with the gate once its meshes are applied (<see cref="PipelineInput.Gate"/>); null, measuring nothing, unless overridden.</summary>
        internal static GateRunOptions Gate { get; private set; }

        /// <summary>Every build until the returned scope is disposed measures the gate with <paramref name="options"/>, null for not at all; disposing restores what was before.</summary>
        /// <remarks>Named apart from <see cref="OverrideGate(bool)"/>, since a runner reaching a scope by name alone would find two.</remarks>
        internal static IDisposable OverrideGateWith(GateRunOptions options)
        {
            var before = Gate;
            Gate = options;
            return new Scope(() => Gate = before);
        }

        /// <summary>
        /// Every build until the returned scope is disposed measures the gate, when <paramref name="on"/>, in the default pose
        /// bank (<see cref="PoseBank.Default"/>) with the geometric channel on, or not at all; disposing restores what was before.
        /// </summary>
        internal static IDisposable OverrideGate(bool on) =>
            OverrideGateWith(on ? new GateRunOptions { Poses = PoseBank.Default, Geometry = true } : null);

        sealed class Scope : IDisposable
        {
            readonly Action _restore;
            bool _done;

            public Scope(Action restore) => _restore = restore;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _restore();
            }
        }
    }
}
