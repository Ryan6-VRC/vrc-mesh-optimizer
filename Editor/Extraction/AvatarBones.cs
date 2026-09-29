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
