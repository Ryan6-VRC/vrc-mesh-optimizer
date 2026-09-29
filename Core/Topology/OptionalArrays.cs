using System;
using Unity.Collections;

namespace Ryan6Vrc.MeshOptimizer.Core.Topology
{
    /// <summary>
    /// A job refuses a container that was never allocated, and an evaluation mesh may leave its optional
    /// arrays (frames, deltas, submeshes, weights) unassigned. <see cref="Array"/> is the given array, or an
    /// empty one this owns and disposes.
    /// </summary>
    internal struct OptionalArray<T> : IDisposable where T : unmanaged
    {
        public NativeArray<T> Array;
        bool _owned;

        public OptionalArray(NativeArray<T> array)
        {
            _owned = !array.IsCreated;
            Array = _owned ? new NativeArray<T>(0, Allocator.Persistent) : array;
        }

        public void Dispose()
        {
            if (_owned && Array.IsCreated) Array.Dispose();
        }
    }
}
