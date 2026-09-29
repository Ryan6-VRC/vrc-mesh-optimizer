using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>
    /// Avatar-wide bone ids, one per Transform, and the parent table the occlusion rule walks for kin. Every
    /// Transform under the avatar root is numbered depth first, the root 0; a Transform outside the root, which a
    /// renderer can name as a bone, gets the next free id and no parent.
    /// </summary>
    internal sealed class AvatarBones
    {
        readonly Dictionary<Transform, int> _ids = new Dictionary<Transform, int>();
        readonly List<Transform> _transforms = new List<Transform>();
        readonly List<int> _parents = new List<int>();

        public AvatarBones(Transform root)
        {
            Root = root;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                Add(t, t == root || !_ids.TryGetValue(t.parent, out var parent) ? -1 : parent);
        }

        public Transform Root { get; }
        public int Count => _transforms.Count;
        public IReadOnlyList<Transform> Transforms => _transforms;
        /// <summary>Parent id per bone id, -1 for the root and for bones outside it.</summary>
        public IReadOnlyList<int> Parents => _parents;

        public int Id(Transform t) => _ids.TryGetValue(t, out var id) ? id : Add(t, -1);

        public NativeArray<int> ParentArray(Allocator allocator) => new NativeArray<int>(_parents.ToArray(), allocator);

        /// <summary>Per bone id, true when the bone or any ancestor is flagged: what rides a bone goes where it goes.</summary>
        public bool[] CarryDown(bool[] flags)
        {
            var result = new bool[flags.Length];
            var known = new bool[flags.Length];
            var chain = new List<int>();
            for (var b = 0; b < flags.Length; b++)
            {
                // Walk up to the nearest settled ancestor, then settle the chain on the way back down.
                chain.Clear();
                var at = b;
                while (at >= 0 && at < flags.Length && !known[at])
                {
                    chain.Add(at);
                    at = _parents[at];
                }
                var inherited = at >= 0 && at < flags.Length && result[at];
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var c = chain[i];
                    inherited |= flags[c];
                    result[c] = inherited;
                    known[c] = true;
                }
            }
            return result;
        }

        int Add(Transform t, int parent)
        {
            var id = _transforms.Count;
            _ids[t] = id;
            _transforms.Add(t);
            _parents.Add(parent);
            return id;
        }
    }
}
