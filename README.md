# Mesh Optimizer

A non-destructive mesh optimizer for VRChat avatars, built as an [NDMF](https://github.com/bdunderscore/ndmf) plugin. Drop the component on an avatar, set a few things, and build: the avatar is decimated with a deformation-aware error metric, and triangles that are guaranteed hidden are removed. It is designed for what avatars actually do: toggled meshes, transparent garments, and faces carrying hundreds of shape keys.

Status: in development, not yet released.

## Ancestors

Projects this work studies and builds on, credited by name:

- [Meshia.MeshSimplification](https://github.com/RamType0/Meshia.MeshSimplification) by Ram.Type-0, and [touma-tw's fork](https://github.com/touma-tw/Meshia.MeshSimplification) (MIT): Burst-accelerated attribute-QEM simplification as an NDMF pass, the closest ancestor of this tool.
- [Avatar Optimizer](https://github.com/anatawa12/AvatarOptimizer) by anatawa12, and [VRCFury](https://github.com/VRCFury/VRCFury): their blend shape freezing is the prior art for deciding which shape keys an avatar animates, including the descriptor's visemes and eyelids, MMD world morphs, and a curve that only holds a key where it already is.

Anything copied rather than re-derived carries its license text in `NOTICE`.

## License

MIT. See `LICENSE`.
