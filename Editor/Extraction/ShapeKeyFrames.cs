using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>
    /// One shape key's frames, loaded from a mesh, evaluated at any weight the way a renderer blends them:
    /// from nothing to the first frame below its weight, linearly between frames, and along the last segment
    /// beyond the last frame. Reused across keys so a face with hundreds of keys allocates once.
    /// </summary>
    internal sealed class ShapeKeyFrames
    {
        readonly int _vertexCount;
        readonly List<Vector3[]> _positions = new List<Vector3[]>();
        readonly List<Vector3[]> _normals = new List<Vector3[]>();
        float[] _weights = Array.Empty<float>();

        public ShapeKeyFrames(int vertexCount) => _vertexCount = vertexCount;

        public int FrameCount { get; private set; }
        public float FrameWeight(int frame) => _weights[frame];
        /// <summary>The weight of the last frame, at which the key is fully applied.</summary>
        public float FullWeight => _weights[FrameCount - 1];

        public void Load(Mesh mesh, int key)
        {
            FrameCount = mesh.GetBlendShapeFrameCount(key);
            if (_weights.Length < FrameCount) _weights = new float[FrameCount];
            while (_positions.Count < FrameCount)
            {
                _positions.Add(new Vector3[_vertexCount]);
                _normals.Add(new Vector3[_vertexCount]);
            }
            for (var f = 0; f < FrameCount; f++)
            {
                _weights[f] = mesh.GetBlendShapeFrameWeight(key, f);
                mesh.GetBlendShapeFrameVertices(key, f, _positions[f], _normals[f], null);
            }
        }

        /// <summary>Adds the key's displacement at <paramref name="weight"/> to the accumulators; a null accumulator is skipped.</summary>
        public void AddAt(float weight, Vector3[] positions, Vector3[] normals)
        {
            var (f0, a0, f1, a1) = Blend(weight);
            Add(_positions, f0, a0, f1, a1, positions);
            Add(_normals, f0, a0, f1, a1, normals);
        }

        /// <summary>Writes the key's displacement at <paramref name="weight"/> over <paramref name="positions"/>.</summary>
        public void PositionsAt(float weight, Vector3[] positions)
        {
            Array.Clear(positions, 0, positions.Length);
            var (f0, a0, f1, a1) = Blend(weight);
            Add(_positions, f0, a0, f1, a1, positions);
        }

        /// <summary>The key's displacement at a weight is a0 × frame f0 + a1 × frame f1; f1 is -1 when one frame suffices.</summary>
        internal (int f0, float a0, int f1, float a1) Blend(float weight)
        {
            var n = FrameCount;
            if (n == 1 || weight <= _weights[0])
                return (0, _weights[0] != 0f ? weight / _weights[0] : 1f, -1, 0f);
            var i = n - 2;
            for (var f = 0; f < n - 1; f++)
                if (weight <= _weights[f + 1]) { i = f; break; }
            var t = (weight - _weights[i]) / (_weights[i + 1] - _weights[i]);
            return (i, 1f - t, i + 1, t);
        }

        void Add(List<Vector3[]> frames, int f0, float a0, int f1, float a1, Vector3[] into)
        {
            if (into == null) return;
            var d0 = frames[f0];
            if (f1 < 0)
            {
                for (var v = 0; v < _vertexCount; v++) into[v] += a0 * d0[v];
                return;
            }
            var d1 = frames[f1];
            for (var v = 0; v < _vertexCount; v++) into[v] += a0 * d0[v] + a1 * d1[v];
        }
    }
}
