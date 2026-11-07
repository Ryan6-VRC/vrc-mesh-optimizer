using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Animators;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Pipeline
{
    /// <summary>
    /// Which renderers no animation can show. A renderer is never shown when it is off as the avatar loads, disabled or
    /// under a GameObject that is inactive below the root, and no clip animates what keeps it off: the renderer's enabled
    /// state, or that GameObject's active state on its own path. Any curve counts whatever its values, its layer's mask or
    /// its layer's weight, so a curve the pass misreads can only keep a renderer counted. This is the rule the unused-object
    /// sweeps of d4rk Avatar Optimizer and Avatar Optimizer apply, read more cautiously; either may still keep a renderer it
    /// rules unused, such as one listed in d4rk's exclusions or one another component depends on.
    /// </summary>
    internal sealed class NeverShown
    {
        readonly HashSet<Transform> _activated = new HashSet<Transform>();
        readonly HashSet<Transform> _enabled = new HashSet<Transform>();

        /// <summary>A curve on a GameObject's active state or a renderer's enabled state.</summary>
        public static bool Switches(EditorCurveBinding binding) =>
            binding.propertyName == "m_IsActive" && binding.type == typeof(GameObject) ||
            binding.propertyName == "m_Enabled" && typeof(Renderer).IsAssignableFrom(binding.type);

        /// <param name="curves">Every curve on an active or enabled state any clip can play (<see cref="Switches"/>).</param>
        public NeverShown(IEnumerable<AnimatedCurve> curves)
        {
            foreach (var c in curves)
                (c.Binding.type == typeof(GameObject) ? _activated : _enabled).Add(c.Target);
        }

        /// <summary>Whether no animation can show <paramref name="renderer"/>, under the avatar at <paramref name="root"/>.</summary>
        public bool Of(Renderer renderer, Transform root)
        {
            if (!renderer.enabled && !_enabled.Contains(renderer.transform)) return true;
            for (var t = renderer.transform; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf && !_activated.Contains(t)) return true;
            return false;
        }
    }
}
