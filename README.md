# Mesh Optimizer

A non-destructive mesh optimizer for VRChat avatars, built as an [NDMF](https://github.com/bdunderscore/ndmf) plugin. Drop the component on an avatar, set a few things, and build: the avatar is decimated with a deformation-aware error metric, and triangles that are guaranteed hidden are removed. It is designed for what avatars actually do: toggled meshes, transparent garments, and faces carrying hundreds of shape keys.

Status: in development, not yet released.

## Ancestors

Projects this work studies and builds on, credited by name:

- [Meshia.MeshSimplification](https://github.com/RamType0/Meshia.MeshSimplification) by Ram.Type-0, and [touma-tw's fork](https://github.com/touma-tw/Meshia.MeshSimplification) (MIT): Burst-accelerated attribute-QEM simplification as an NDMF pass, the closest ancestor of this tool.
- [meshoptimizer](https://github.com/zeux/meshoptimizer) by Arseny Kapoulkine (MIT): the decimation core's half-edge collapse, its vertex classification into interior, border, seam and locked, and its seams collapsing as pairs follow meshoptimizer's simplifier, re-derived rather than copied.
- [vrc-blender-tools](https://github.com/Ryan6-VRC/vrc-blender-tools) (MIT): the co-moving coverage rule in `avatarprep/core/coverage.py`, a cone raycast in which only geometry riding the same bones blocks, whose semantics the occlusion module ports to Burst.
- [Avatar Optimizer](https://github.com/anatawa12/AvatarOptimizer) by anatawa12, and [VRCFury](https://github.com/VRCFury/VRCFury): their blend shape freezing is the prior art for deciding which shape keys an avatar animates, including the descriptor's visemes and eyelids, MMD world morphs, and a curve that only holds a key where it already is.

The decimation cost rests on published work: Garland & Heckbert 1997 (quadric error metrics), Hoppe 1999 (attribute quadrics, here for UV0), Mohr & Gleicher 2003 (one quadric per deformed configuration, here per live shape frame), and Knodt 2025 (a skin-weight-gradient edge term).

Anything copied rather than re-derived carries its license text in `NOTICE`.

## License

MIT. See `LICENSE`.
