using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.MeshOptimizer.Animators
{
    /// <summary>A controller as authored on the avatar, and the Transform its curve paths are relative to.</summary>
    internal readonly struct AuthoredController
    {
        public readonly RuntimeAnimatorController Controller;
        public readonly Transform Owner;

        public AuthoredController(RuntimeAnimatorController controller, Transform owner)
        {
            Controller = controller;
            Owner = owner;
        }
    }

    /// <summary>
    /// The controllers the avatar descriptor holds before a build: what stands in for NDMF's animator services where there
    /// is no build. Only the descriptor's playable layers are read; a controller another tool merges in at build (Modular
    /// Avatar, VRCFury) is not, so a key only such a controller drives reads as not driven.
    /// </summary>
    internal static class AuthoredControllers
    {
        /// <summary>Every custom controller in the descriptor's base and special layers; their paths are from the avatar root.</summary>
        public static List<AuthoredController> Of(VRCAvatarDescriptor descriptor) =>
            (descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
            .Concat(descriptor.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
            .Where(layer => !layer.isDefault && layer.animatorController != null)
            .Select(layer => new AuthoredController(layer.animatorController, descriptor.transform))
            .ToList();
    }
}
