"""
Extract Shapez 2 building meshes into glTF (.glb) files, preserving ALL UV maps
(Unity meshes carry 2-3 UV channels; OBJ keeps only one, which is why an OBJ
round-trip loses data).

glTF stores every UV set as TEXCOORD_n; Blender's glTF importer turns each into a
separate UV map, so nothing is lost. Also carries normals, tangents and vertex
colours.

The script is generic: pass one or more building-name prefixes (e.g. `Painter`,
`Cutter`, `Splitter`) and it auto-discovers every mesh whose name starts with a
prefix, groups them into logical parts by parsing the `_LOD<n>` suffix, and writes
one .glb per LOD level. Prefix (not substring) matching keeps `Painter` from also
catching `VirtualTopMostPainter` and `Cutter` from catching `HalfCutter`. Each .glb contains every part present at
that LOD as its own named node, so importing a file gives you the whole building
assembled from its parts in shared model space.

Grouping is derived from the actual mesh names, not a hardcoded table:
  <Building>[_<Variant>]_<Part>_LOD<n>   ->  part "<Part>" at LOD n
The leading building prefix and a leading "Default" variant token are stripped from
the node/part name (so `Painter_Main_LOD0` and `Painter_Default_Main_LOD4` both land
as part "Main"). Meshes with no `_LOD` suffix are treated as a single-LOD part.

Coordinate convention: Unity is left-handed (Y-up, +Z forward); glTF is right-handed.
We negate X on positions/normals/tangents and reverse triangle winding, so the model
appears correctly (not mirrored, normals outward) in Blender. UV V is flipped
(Unity bottom-left origin -> glTF top-left) so UV maps aren't upside down. See README.md.

Game path: set the `SPZ2_PATH` environment variable (the same one the .csproj files
use) to the game's `shapez 2_Data\\Managed` directory. The asset files live in its
parent (`shapez 2_Data`), which this script derives automatically.

Usage:
  python scripts/extract_meshes.py Painter                 # export the painter
  python scripts/extract_meshes.py Painter Cutter          # export several buildings
  python scripts/extract_meshes.py Painter --list          # just list matching meshes
  python scripts/extract_meshes.py Painter -o my/out/dir   # override output root

Requires: UnityPy, pygltflib, numpy  (all pip-installable).
"""
import argparse
import os
import re
import sys
from collections import defaultdict

import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
import pygltflib as gl

# Asset files that hold building meshes. Different buildings' meshes may live in
# any of these, so all are scanned and results deduped by mesh name.
ASSET_FILES = ("resources.assets", "sharedassets0.assets", "sharedassets1.assets")

# Trailing "_LOD<n>" (case-insensitive); the part name is everything before it.
_LOD_RE = re.compile(r"_LOD(\d+)$", re.IGNORECASE)


def game_data_dir():
    """Resolve the `shapez 2_Data` asset directory from the SPZ2_PATH env var.

    SPZ2_PATH points at `shapez 2_Data\\Managed` (that's where the .csproj hint
    paths resolve SPZGameAssembly.dll etc.), so the assets sit one level up.
    """
    spz2 = os.environ.get("SPZ2_PATH")
    if not spz2:
        print("ERROR: SPZ2_PATH is not set. Point it at the game's "
              "'shapez 2_Data\\Managed' directory (same var the .csproj files use).",
              file=sys.stderr)
        sys.exit(1)
    # SPZ2_PATH = ...\shapez 2_Data\Managed  ->  data dir is its parent.
    data = os.path.dirname(os.path.normpath(spz2))
    if not os.path.isdir(data):
        print(f"ERROR: derived asset directory does not exist: {data}\n"
              f"(from SPZ2_PATH={spz2})", file=sys.stderr)
        sys.exit(1)
    return data


def _match(name, pattern):
    """A mesh belongs to a building if its name starts with the pattern
    (case-insensitive). Prefix (not substring) matching keeps `Painter` from
    catching `VirtualTopMostPainter` and `Cutter` from catching `HalfCutter` —
    those are distinct buildings, not painter/cutter parts."""
    return name.lower().startswith(pattern.lower())


def discover_meshes(data_dir, patterns):
    """Scan all asset files, return {mesh_name: unity_obj} for names whose prefix
    matches any pattern (case-insensitive). Deduped by mesh name (first file wins)."""
    found = {}
    for fname in ASSET_FILES:
        path = os.path.join(data_dir, fname)
        if not os.path.exists(path):
            continue
        env = UnityPy.load(path)
        for obj in env.objects:
            if obj.type.name != "Mesh":
                continue
            name = obj.peek_name()
            if name not in found and any(_match(name, p) for p in patterns):
                found[name] = obj
    return found


def parse_name(mesh_name, patterns):
    """Return (part_name, lod). lod is None if the mesh has no _LOD suffix.

    Strips the trailing _LOD<n>, then a leading building prefix (the matched
    pattern token) and a leading "Default" variant token, so parts group across
    the game's inconsistent prefixes (e.g. Painter_Main vs Painter_Default_Main
    both -> "Main")."""
    stem = mesh_name
    m = _LOD_RE.search(stem)
    lod = int(m.group(1)) if m else None
    if m:
        stem = stem[:m.start()]

    # Strip the building prefix (whichever pattern this mesh matched), then a
    # leading Default_ variant token. Longest pattern first so a more specific
    # match wins. Prefix match, so the pattern is always at the start.
    for pat in sorted(patterns, key=len, reverse=True):
        if stem.lower().startswith(pat.lower()):
            rest = stem[len(pat):].lstrip("_")
            if rest:  # don't blank out a mesh that *is* just the building name
                stem = rest
            break
    if stem.lower().startswith("default_"):
        stem = stem[len("default_"):]

    return (stem or mesh_name), lod


def group_meshes(found, patterns):
    """Group discovered meshes into {building_key: {part: {lod: mesh_name}}}.

    building_key is the matched pattern (lower-cased) so multiple buildings passed
    in one invocation each get their own output directory."""
    groups = defaultdict(lambda: defaultdict(dict))
    for name in sorted(found):
        # Attribute the mesh to the longest matching pattern (the building it
        # belongs to) for its output directory.
        key = max((p for p in patterns if _match(name, p)), key=len).lower()
        part, lod = parse_name(name, patterns)
        # Meshes with no _LOD suffix are treated as a single-LOD part (LOD 0).
        groups[key][part][lod if lod is not None else 0] = name
    return groups


def decode(mesh):
    """Return (positions, normals, tangents, uvs[list], colors, triangles) as numpy arrays."""
    h = MeshHandler(mesh)
    h.process()
    pos = np.asarray(h.m_Vertices, dtype=np.float32).reshape(-1, 3)
    normals = np.asarray(h.m_Normals, dtype=np.float32).reshape(-1, 3) if h.m_Normals else None
    tangents = np.asarray(h.m_Tangents, dtype=np.float32).reshape(-1, 4) if h.m_Tangents else None
    uvs = []
    for i in range(8):
        uv = getattr(h, f"m_UV{i}", None)
        if uv:
            arr = np.asarray(uv, dtype=np.float32).reshape(-1, 2)
            # Unity UV origin is bottom-left, glTF's is top-left, so V must be
            # flipped or every map (incl. the UV0 palette-lookup) lands upside down.
            arr[:, 1] = 1.0 - arr[:, 1]
            uvs.append(arr)
    colors = np.asarray(h.m_Colors, dtype=np.float32).reshape(-1, 4) if h.m_Colors else None
    tris = []
    for submesh in h.get_triangles():
        for t in submesh:
            tris.append(t)
    idx = np.asarray(tris, dtype=np.uint32).reshape(-1, 3)

    # Unity (left-handed) -> glTF (right-handed): negate X, reverse winding.
    pos = pos.copy(); pos[:, 0] *= -1.0
    if normals is not None:
        normals = normals.copy(); normals[:, 0] *= -1.0
    if tangents is not None:
        tangents = tangents.copy(); tangents[:, 0] *= -1.0; tangents[:, 3] *= -1.0
    idx = idx[:, ::-1]
    return pos, normals, tangents, uvs, colors, idx


def _pad(buf):
    while len(buf) % 4:
        buf.append(0)


def build_glb(parts, out_path):
    """parts: list of (node_name, decoded-tuple). Writes a .glb with one mesh/node each."""
    blob = bytearray()
    bviews, accessors, meshes, nodes = [], [], [], []

    def add_accessor(arr, comp_type, gl_type, is_indices=False):
        _pad(blob)
        offset = len(blob)
        data = arr.tobytes()
        blob.extend(data)
        target = gl.ELEMENT_ARRAY_BUFFER if is_indices else gl.ARRAY_BUFFER
        bviews.append(gl.BufferView(buffer=0, byteOffset=offset, byteLength=len(data), target=target))
        acc = gl.Accessor(
            bufferView=len(bviews) - 1, componentType=comp_type,
            count=int(arr.shape[0]), type=gl_type,
        )
        if not is_indices:
            acc.max = arr.max(axis=0).tolist()
            acc.min = arr.min(axis=0).tolist()
        accessors.append(acc)
        return len(accessors) - 1

    for node_name, (pos, normals, tangents, uvs, colors, idx) in parts:
        attribs = gl.Attributes()
        attribs.POSITION = add_accessor(pos.astype(np.float32), gl.FLOAT, "VEC3")
        if normals is not None:
            attribs.NORMAL = add_accessor(normals.astype(np.float32), gl.FLOAT, "VEC3")
        if tangents is not None:
            attribs.TANGENT = add_accessor(tangents.astype(np.float32), gl.FLOAT, "VEC4")
        for i, uv in enumerate(uvs):
            setattr(attribs, f"TEXCOORD_{i}", add_accessor(uv.astype(np.float32), gl.FLOAT, "VEC2"))
        if colors is not None:
            attribs.COLOR_0 = add_accessor(colors.astype(np.float32), gl.FLOAT, "VEC4")
        idx_acc = add_accessor(idx.reshape(-1).astype(np.uint32), gl.UNSIGNED_INT, "SCALAR", is_indices=True)
        meshes.append(gl.Mesh(primitives=[gl.Primitive(attributes=attribs, indices=idx_acc)], name=node_name))
        nodes.append(gl.Node(mesh=len(meshes) - 1, name=node_name))

    gltf = gl.GLTF2(
        scene=0,
        scenes=[gl.Scene(nodes=list(range(len(nodes))))],
        nodes=nodes, meshes=meshes, accessors=accessors, bufferViews=bviews,
        buffers=[gl.Buffer(byteLength=len(blob))],
    )
    gltf.set_binary_blob(bytes(blob))
    gltf.save(out_path)
    print(f"  wrote {os.path.relpath(out_path)}  ({len(parts)} parts, {len(blob)//1024} KiB)")


def export_building(key, part_lods, found, out_root):
    """Write one .glb per LOD for a single building group."""
    out_dir = os.path.join(out_root, key)
    os.makedirs(out_dir, exist_ok=True)
    # Union of all LOD levels present across this building's parts.
    all_lods = sorted({lod for lods in part_lods.values() for lod in lods})
    decoded = {}  # mesh_name -> decoded tuple (decode each mesh once)
    for lod in all_lods:
        parts = []
        for part in sorted(part_lods):
            mesh_name = part_lods[part].get(lod)
            if not mesh_name:
                continue
            if mesh_name not in decoded:
                decoded[mesh_name] = decode(found[mesh_name].read())
            parts.append((part, decoded[mesh_name]))
        if parts:
            build_glb(parts, os.path.join(out_dir, f"{key}_LOD{lod}.glb"))


def main():
    ap = argparse.ArgumentParser(
        description="Extract Shapez 2 building meshes to glTF .glb (preserves all UV maps).")
    ap.add_argument("patterns", nargs="+", metavar="NAME",
                    help="Building name / mesh-name prefix to match (e.g. Painter Cutter). "
                         "Case-insensitive prefix match; pass several to extract multiple buildings.")
    ap.add_argument("--list", action="store_true",
                    help="Only list matching mesh names (grouped by part/LOD); do not export.")
    ap.add_argument("-o", "--out", default=None,
                    help="Output root directory (default: <repo>/.extracted).")
    args = ap.parse_args()

    data_dir = game_data_dir()
    found = discover_meshes(data_dir, args.patterns)
    if not found:
        print(f"No meshes matched {args.patterns} in {data_dir}", file=sys.stderr)
        sys.exit(1)

    groups = group_meshes(found, args.patterns)

    if args.list:
        for key in sorted(groups):
            print(f"{key}:")
            for part in sorted(groups[key]):
                lods = groups[key][part]
                for lod in sorted(lods):
                    print(f"  [LOD{lod}] {part:<20} <- {lods[lod]}")
        print(f"\n{len(found)} mesh(es) across {len(groups)} building group(s).")
        return

    out_root = args.out or os.path.join(os.path.dirname(__file__), "..", ".extracted")
    for key in sorted(groups):
        print(f"{key}:")
        export_building(key, groups[key], found, out_root)


if __name__ == "__main__":
    main()
