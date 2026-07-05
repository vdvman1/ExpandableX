# Model sources

The `.blend` files in this folder are the source models for ExpandableX's
building visuals. Each was produced by either:

- **extracting** the corresponding mesh from Shapez 2's own game files, or
- starting from an asset provided in the **official Shapez 2 mod art
  guidelines** ("Mod Support – Art – How to create an asset"),

and then **modifying** it to work with ExpandableX's dynamic connector system —
e.g. splitting the building body from the connector "bridge" geometry so
connectors can be shown/hidden/rotated per variant, and adding LOD levels.

The exported `.fbx` files under `src/ExpandableX/Resources/` — the meshes the
mod actually loads at runtime — are derived from these `.blend` files and are
covered by the same terms below.

## Copyright & licensing

These models are **derivative works** based on assets owned by **tobspr Games**
(the developer of Shapez 2). Copyright in the original meshes and geometry
remains with **tobspr Games**; the modifications made here do not transfer or
claim any ownership of that underlying work.

tobspr Games officially supports modding and, through the Shapez 2 mod art
guidelines ("Mod Support – Art – How to create an asset"), distributes source
`.blend` files (some used as a starting point here) and documents how to create
art for mods. We take this as **implied permission** to use and adapt these
assets for a mod. We are **not aware of an explicit written license** that
grants this, so if tobspr Games requests it we will promptly remove or replace
the affected assets.

**These asset files are NOT covered by this repository's MIT `LICENSE`.** That
MIT license applies to ExpandableX's own source code only. The models in this
folder — and the `.fbx` derived from them — remain subject to tobspr Games'
rights and to Shapez 2's terms.

ExpandableX is an unofficial, fan-made mod and is not affiliated with or
endorsed by tobspr Games. Shapez 2 and its original assets © tobspr Games.
