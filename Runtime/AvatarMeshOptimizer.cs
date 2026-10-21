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
        /// <summary>Whether the build collapses edges to lower the triangle count, keeping the shape wherever the avatar bends or animates.</summary>
        public bool decimate = true;
        /// <summary>Whether the build deletes triangles that an <see cref="alwaysOn"/> renderer covers from every direction within a cone around the surface normal.</summary>
        public bool removeHiddenTriangles = true;

        /// <summary>
        /// What decimation aims for: a triangle count for the whole avatar, or a tolerance. Tolerance is the default, since the
        /// reduction then follows from what each mesh can lose unseen, where a fixed budget forces whatever error reaching it takes.
        /// </summary>
        public TargetKind target = TargetKind.Tolerance;
        /// <summary>Triangles for the whole avatar, used when <see cref="target"/> is a budget.</summary>
        [Min(0)] public int triangleBudget = 70000;
        /// <summary>
        /// The most surface deviation a budget may spend, in millimetres, used when <see cref="target"/> is a budget and read
        /// as a tolerance is read: a budget reachable only past it is cut to it and reported unmet. 0 turns it off.
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
        /// front is covered stays unless its back is covered too. Off by default, since what it keeps is mostly skin under
        /// clothing; it is for a garment whose inside is seen through its outline. An outline never counts as cover either way.
        /// </summary>
        public bool outlinesShowInside;

        /// <summary>Materials whose surface counts as cover other than <see cref="OcclusionMode.Automatic"/> decides; a material not listed is Automatic.</summary>
        public List<MaterialOcclusion> materials = new List<MaterialOcclusion>();
        /// <summary>Per-renderer decimation settings; a renderer not listed is decimated at protection 1.</summary>
        public List<RendererSettings> renderers = new List<RendererSettings>();

        /// <summary>
        /// The share of its triangles the viseme renderer keeps whatever the target: lip sync and expressions deform it most
        /// and it is the surface other players watch, so an aggressive target is spent elsewhere first.
        /// </summary>
        [Range(0f, 1f)] public float faceFloor = 0.5f;
        /// <summary>Whether decimation keeps every finger vertex, found through the humanoid mapping.</summary>
        public bool lockFingers = true;
        /// <summary>
        /// With <see cref="lockFingers"/> on, a vertex with at least this much weight on a finger is a finger vertex. Kept
        /// above 0, where every vertex would count.
        /// </summary>
        [Range(0.001f, 1f)] public float fingerWeight = 0.01f;
    }

    /// <summary>What decimation aims for. A saved component holds this as a number, so a member's number never changes.</summary>
    public enum TargetKind
    {
        /// <summary>The whole avatar cut to <see cref="AvatarMeshOptimizer.triangleBudget"/> triangles, within <see cref="AvatarMeshOptimizer.maxError"/>.</summary>
        TriangleBudget,
        /// <summary>Every mesh cut as far as <see cref="AvatarMeshOptimizer.tolerance"/> allows.</summary>
        Tolerance,
    }

    /// <summary>How a material's surface counts when it stands in front of another triangle.</summary>
    public enum OcclusionMode
    {
        /// <summary>Opaque by its render queue and render type; otherwise sampled from its alpha, and see-through when its alpha cannot be read.</summary>
        Automatic,
        /// <summary>Hides what is behind it wherever it is hit, whatever its alpha.</summary>
        ForceOpaque,
        /// <summary>Hides nothing behind it.</summary>
        NeverOccludes,
    }

    /// <summary>One material override: how <see cref="material"/> counts as cover.</summary>
    [Serializable]
    public sealed class MaterialOcclusion
    {
        public Material material;
        public OcclusionMode mode;
    }

    /// <summary>One renderer's decimation settings.</summary>
    [Serializable]
    public sealed class RendererSettings
    {
        public Renderer renderer;
        /// <summary>Whether decimation leaves this renderer whole; its hidden triangles are still removed.</summary>
        public bool excludeFromDecimation;
        /// <summary>
        /// How many times over this renderer's error counts against the target, a budget or a tolerance alike. 1 is
        /// neutral; higher keeps more of it. Floored at <see cref="ProtectionLimit.Min"/>: below that the renderer's error limit
        /// is effectively gone, and it can be cut almost to nothing whatever the target.
        /// </summary>
        [Min(ProtectionLimit.Min)] public float protection = 1f;
    }

    /// <summary>The lowest protection the inspector accepts and the cut honors; a lower saved value cuts as this does.</summary>
    public static class ProtectionLimit
    {
        public const float Min = 0.1f;
    }
}
