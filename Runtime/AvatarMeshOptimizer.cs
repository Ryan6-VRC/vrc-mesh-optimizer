using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Runtime
{
    /// <summary>
    /// Settings for the build-time mesh optimizer, one per avatar. Data only: the build reads it, applies
    /// it to the avatar's clone and removes it, so nothing here reaches the uploaded avatar.
    /// Unity titles the Inspector with the last segment of <see cref="AddComponentMenu"/>, so it reads "Mesh
    /// Optimizer" while the class name stays clear of the <c>Ryan6Vrc.MeshOptimizer</c> namespace.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Mesh Optimizer/Mesh Optimizer")]
    public sealed class AvatarMeshOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        public bool decimate = true;
        public bool removeHiddenTriangles = true;

        /// <summary>
        /// A tolerance for a component added fresh: the reduction then follows from what each mesh can lose unseen, where a
        /// fixed budget forces whatever error reaching it takes. A saved component keeps the target it holds.
        /// </summary>
        public TargetKind target = TargetKind.Tolerance;
        /// <summary>Triangles for the whole avatar, used when <see cref="target"/> is a budget.</summary>
        [Min(0)] public int triangleBudget = 70000;
        /// <summary>
        /// The most surface deviation a budget may spend, in millimetres, used when <see cref="target"/> is a budget and read
        /// as a tolerance is read: a budget reachable only past it is cut to it and reported unmet. 0 turns it off. A
        /// component saved before this field existed reads the default too, so a saved budget stops here rather than cutting
        /// to its count at any error.
        /// </summary>
        [Min(0)] public float maxError = 5f;
        /// <summary>Largest surface deviation allowed, in millimetres, used when <see cref="target"/> is a tolerance.</summary>
        [Min(0)] public float tolerance = 1f;

        /// <summary>
        /// Renderers that are always visible. Only these may hide another renderer's triangles; nothing
        /// derives this list from animators, reactive components or menus.
        /// </summary>
        public List<Renderer> alwaysOn = new List<Renderer>();

        /// <summary>
        /// Whether a material's outline counts as drawing the triangles under it when they might be hidden. An outline
        /// culled front draws its triangles' backs, which show only from inside the garment; with this on, a triangle whose
        /// front is covered stays unless its back is covered too. Off by default, and off for a component saved before it
        /// existed, since what it keeps is mostly skin under clothing; it is for a garment whose inside is seen through its
        /// outline. An outline never counts as cover either way.
        /// </summary>
        public bool outlinesShowInside;

        public List<MaterialOcclusion> materials = new List<MaterialOcclusion>();
        public List<RendererSettings> renderers = new List<RendererSettings>();

        /// <summary>
        /// The share of its triangles the viseme renderer keeps whatever the target: lip sync and expressions deform it most
        /// and it is the surface other players watch, so an aggressive target is spent elsewhere first. A component saved
        /// before this field existed reads the default.
        /// </summary>
        [Range(0f, 1f)] public float faceFloor = 0.5f;
        /// <summary>Whether decimation keeps every finger vertex, found through the humanoid mapping. On for a component saved before this field existed.</summary>
        public bool lockFingers = true;
        /// <summary>
        /// With <see cref="lockFingers"/> on, a vertex with at least this much weight on a finger is a finger vertex. Kept
        /// above 0, where every vertex would count. A component saved before this field existed reads the default.
        /// </summary>
        [Range(0.001f, 1f)] public float fingerWeight = 0.01f;
    }

    /// <summary>A saved component holds this as a number, so a value's number never changes.</summary>
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
        /// <summary>
        /// How many times over this renderer's error counts against the target, a budget or a tolerance alike. 1 is
        /// neutral; higher keeps more of it.
        /// </summary>
        [Min(0)] public float protection = 1f;
    }
}
