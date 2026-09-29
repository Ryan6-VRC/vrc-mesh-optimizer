using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Runtime
{
    /// <summary>
    /// Settings for the build-time mesh optimizer, one per avatar. Data only: the build reads it, applies
    /// it to the avatar's clone and removes it, so nothing here reaches the uploaded avatar.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Mesh Optimizer/Mesh Optimizer")]
    public sealed class MeshOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        public bool decimate = true;
        public bool removeHiddenTriangles = true;

        public TargetKind target = TargetKind.TriangleBudget;
        /// <summary>Triangles for the whole avatar, used when <see cref="target"/> is a budget.</summary>
        [Min(0)] public int triangleBudget = 70000;
        /// <summary>Largest surface deviation allowed, in millimetres, used when <see cref="target"/> is a tolerance.</summary>
        [Min(0)] public float tolerance = 0.5f;

        /// <summary>
        /// Renderers that are always visible. Only these may hide another renderer's triangles; nothing
        /// derives this list from animators, reactive components or menus.
        /// </summary>
        public List<Renderer> alwaysOn = new List<Renderer>();

        public List<MaterialOcclusion> materials = new List<MaterialOcclusion>();
        public List<RendererSettings> renderers = new List<RendererSettings>();
    }

    public enum TargetKind
    {
        TriangleBudget,
        Tolerance,
    }

    /// <summary>How a material's surface counts when it stands in front of another triangle.</summary>
    public enum OcclusionMode
    {
        /// <summary>Opaque by its render queue and render type, otherwise sampled from its alpha.</summary>
        Automatic,
        ForceOpaque,
        NeverOccludes,
    }

    [Serializable]
    public sealed class MaterialOcclusion
    {
        public Material material;
        public OcclusionMode mode;
    }

    [Serializable]
    public sealed class RendererSettings
    {
        public Renderer renderer;
        public bool excludeFromDecimation;
        /// <summary>Relative weight of this renderer's error when the triangle budget is shared out. 1 is neutral.</summary>
        [Min(0)] public float protection = 1f;
    }
}
