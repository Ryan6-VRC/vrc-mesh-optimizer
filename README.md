# Mesh Optimizer

A non-destructive mesh optimizer for VRChat avatars, built as an [NDMF](https://github.com/bdunderscore/ndmf) plugin. Drop the component on an avatar, set a few things, and build: the avatar is decimated with a deformation-aware error metric, and triangles that are guaranteed hidden are removed. It is designed for what avatars actually do: toggled meshes, transparent garments, and faces carrying hundreds of shape keys.

Status: in development, not yet released.

## Target

A component added fresh targets a tolerance of 1 mm: each mesh is reduced as far as it can go while its surface stays within a millimetre of the original, and the triangle count is whatever that leaves. On the real avatars it was measured on, 1 mm removed about 45% of their triangles with no hole to see through and little new clipping between layers, while holding a heavily dressed avatar to a fixed 70,000 triangles forced errors over 6 mm. A triangle count remains available as the target, starting at 70,000, the PC line between the Poor and Very Poor performance ranks. A count is cut no further than Max error, 5 mm unless set otherwise: one reachable only past it is reported unmet rather than reached by distorting the avatar, and 0 turns the limit off. A component saved with either target keeps it, and one saved before Max error existed reads 5 mm.

## Ancestors

Projects this work studies and builds on, credited by name:

- [Meshia.MeshSimplification](https://github.com/RamType0/Meshia.MeshSimplification) by Ram.Type-0, and [touma-tw's fork](https://github.com/touma-tw/Meshia.MeshSimplification) (MIT): Burst-accelerated attribute-QEM simplification as an NDMF pass, the closest ancestor of this tool.
- [meshoptimizer](https://github.com/zeux/meshoptimizer) by Arseny Kapoulkine (MIT): the decimation core's half-edge collapse, its vertex classification into interior, border, seam and locked, and its seams collapsing as pairs follow meshoptimizer's simplifier, re-derived rather than copied.
- [vrc-blender-tools](https://github.com/Ryan6-VRC/vrc-blender-tools) (MIT): the co-moving coverage rule in `avatarprep/core/coverage.py`, a cone raycast in which only geometry riding the same bones blocks, whose semantics the occlusion module ports to Burst.
- [Avatar Optimizer](https://github.com/anatawa12/AvatarOptimizer) by anatawa12, and [VRCFury](https://github.com/VRCFury/VRCFury): their blend shape freezing is the prior art for deciding which shape keys an avatar animates, including the descriptor's visemes and eyelids, MMD world morphs, and a curve that only holds a key where it already is.

The decimation cost rests on published work: Garland & Heckbert 1997 (quadric error metrics), Hoppe 1999 (attribute quadrics, here for UV0 and for the vertex normals the shader interpolates), Mohr & Gleicher 2003 (one quadric per deformed configuration, here per live shape frame), and Knodt 2025 (a skin-weight-gradient edge term).

Decimation keeps layers of clothing from being driven through one another. Making decimation aware of nearby meshes follows Ghazanfarpour, Mellado, Himeur, Barthe & Jessel 2020 (proximity-aware decimation of multiple meshes), which penalises collapses where meshes come close; here it is a hard limit instead, each layer keeping to its share of the gap to its neighbours. The triangle distance tests follow Ericson, Real-Time Collision Detection (2005).

The visual-error gate's geometric channel, a two-sided distance between the source and optimized surfaces sampled over their area, follows Metro (Cignoni, Rocchini & Scopigno 1998).

Anything copied rather than re-derived carries its license text in `NOTICE`.

## License

MIT. See `LICENSE`.
