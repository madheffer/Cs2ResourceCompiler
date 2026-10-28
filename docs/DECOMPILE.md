# Decompiling a compiled map back to a Hammer .vmap

The goal: take a shipped map package (`maps/<map>.vpk`) and get back a `.vmap` a mapper can open
in Hammer, fix, and compile again, as close as possible to what the author saved. Uses: maps whose
author has left CS2, fixing a map after a CS2 update breaks it, and a large supply of real maps for
testing our compiler (decompile, compile with ours, compare with the shipped package).

This file is the 2026-10-28 measurement of VRF's decompiler (`MapExtract`) against maps whose
Hammer source we have, and what is missing for the goal above. Branch `research/vmap-decompile`.

## How it was measured

- VRF: vpkstuff's vendored copy (`vpkstuff/ValveResource/ValveResourceFormat-master`, 2026-10-27),
  `CLI` built to the session scratchpad, run as
  `Source2Viewer-CLI -i <map>.vpk -f maps/<map>.vmap_c -d -o <out> --game csgo/gameinfo.gi`.
- Structure: [`tools/map-survey/vmap_compare.py`](../tools/map-survey/vmap_compare.py) reads both
  `.vmap` files (the DMX reader in `tools/map-survey/dmx.py`) and compares node types, meshes,
  entities paired by `nodeID`, their keys (case-blind, numbers by value, empty = missing) and
  outputs.
- Round trip: the decompiled `probe_classes.vmap` put in place of the source (source restored and
  cmp-checked afterwards), both compiled `-world -fshallow` with `tools/pipeline/compile_fresh.py`,
  the two packages compared with `s2c map-diff`.

## Results

### probe_classes (s2c_rc_probe, 777 entities of most classes)

Decompiles without error. Recompiles with resourcecompiler (exit 0).

| | source | VRF |
|---|---|---|
| vmap format / editorbuild | 40 / 9806 | 29 / 8600 |
| entities | 777 | 760 (759 paired by nodeID) |
| meshes / faces / vertices | 141 / 892 / 1214 | 148 / 1043 / 1399 |
| world meshes | 19 | 27 (welded and re-split by connectivity) |
| CMapNavData, nodeInstanceData, thumbnail, asset refs | 1, 17, yes, 5 | 0, 0, no, 0 |

- Entities lost (stripped by the compile, nothing left in the lump): `visibility_hint`,
  `info_cull_triangles`, `point_scale_reference_human`, `path_generic` and its `path_node_*`,
  `path_node_particle_rope`, `cable_static`, 2 `prop_static`.
- Of the 759 paired entities none loses an authored key; outputs (`connectionsData`) are equal on
  every one. Positions, angles, scales equal.
- Values written wrong: `light_barn` brightness `1.5` -> `-Infinity` (and `brightness_legacy`,
  `brightness_lumens` -> 0), `light_omni2` `brightness_candelas` / `brightness_nits` -> 0,
  `light_environment` gets `brightness_legacy` `-nan(ind)`, `cable_dynamic` spawnflags 1 -> 0,
  `point_worldtext` colour loses its alpha, `env_particle_glow` `snapshot_mesh` 3 -> 0.
- Compiler keys left in: `compile_source_id` on every entity, `local.origin/angles/scales`,
  `light_map_uniqueid`, `light_path_uniqueid`, `bakedshadowindex`, probe `handshake` and
  `light_probe_size_*` / `light_probe_atlas_*`, `lightprobetexture_dlshd`, `vrf_stripped_*`, the
  FGD defaults the lump fills in (`rendercolor`, `fademindist`, `renderfx` ... on ~70 entities each),
  and `model` on brush entities pointing at the compiled `maps/<map>/entities/*.vmdl`.
- Mesh streams: texcoord, normal, tangent, texture axes and scale, material index all come back;
  `PerVertexLighting` (17 meshes) does not. `toolslightmapres` faces are lost.
- The vsnap is written to `maps/node_3.vsnap`, not `maps/<map>/particle_snapshots/`.

Round trip (`-world -fshallow`, both packages): 134 files vs 132, **39 identical**, 93 differ
(88 `.vmdl_c`: every brush entity model's PHYS and most MVTX/MDAT; the world node, its aggregate,
`default_ents.vents_c`, `.vmap_c`, `.vwrld_c`), 2 missing (the vsnap, one entity model).

### s2c_instnest (instances, groups, cables, deformers)

Decompiles without error. Everything Hammer-only is flattened:

| node type | source | VRF |
|---|---|---|
| CMapInstance / CMapGroup | 4 / 6 | 0 / 0 |
| CMapCable / CMapPathNode | 8 / 26 | 0 / 0 |
| CMapDeformerPath / Lattice / Simple | 6 / 3 / 2 | 0 |
| CMapMesh (faces) | 36 (262) | 151 (1930) |
| entities | 148 | 90 |

Cables and deformed meshes come back as their baked output triangles; 42 `prop_static` (baked
to world) and 26 cable path nodes are gone.

### atixref (ze_atix reference)

**Fails.** The whole decompile aborts on one mesh: `InvalidDataException: Unexpected
VERTEXGENERICINTEGERDATA attribute format R32_UINT` (`VBIB.GetScalarAttributeArray` accepts only
R32_FLOAT; called from `ModelExtract.FillDatamodelVertexData` for a world scene object). No
`.vmap` is written.

### Workshop sweep

The 11 most recently updated ze maps in the workshop folder (isla_nublar_p, parkers_pit_p,
traak_p, crashbandicoot_p, greencity_p, grau_p, parking_p, samosbor, resident_evil_3,
sky_bridge_a01_fix3, ratatouille): every one decompiles, 1 to 15 s each, .vmap 38 to 150 MB,
no exception. So the R32_UINT crash is something in atixref's content, not every new compile;
still, one unhandled vertex format kills the whole map. These were compared only for "does it
run": we have no source for them.

Each logs 72 to 4,800 "Failed to load": custom materials and models that live in the outer
workshop package while the map is in the nested `maps/<map>.vpk`. Decompiling the inner package
alone leaves VRF without them (texture sizes for UVs, entity models). A tool has to mount both.

## What VRF's MapExtract does not do (from its source)

- Clutter scene objects (`// TODO: Clutter`), physics spheres and capsules, reference meshes.
- World geometry is rebuilt from render triangles welded across every aggregate and split by
  connectivity: the author's mesh boundaries, per-mesh settings (`smoothingAngle`, `bakelighting`,
  `lightGroup`, `cubeMapName`, `visexclude`, physics type/group) and the half-edge topology the
  author drew (quads, n-gons, subdivision) are not recovered, only re-guessed (`Untriangulate`).
- Hammer-only data the compile throws away: instances and prefabs (collapsed into the map),
  groups, selection sets, visgroups / layers beyond the world layers, cables and deformers (baked),
  path nodes, nav markup settings (`CMapNavData`), map variables, stored cameras, cordons.
- Writes vmap format 29, so Hammer upgrades it on open.

## Conclusion

VRF gives a usable *viewing and salvage* decompile of simple maps: entities and their I/O come
back almost intact, brushes come back as textured meshes, and the result compiles. It does not
give a map "as it would be from Hammer": it crashes on our ze reference, mangles light
brightness, leaves compiler keys in, loses every Hammer-only node, and its recompile is far from
the original package (39 of 134 files).

It has to be extended, and under the independence rule (memory "vrf-independence-goal") the
extension belongs in our own code, not more VRF patches. We are better placed than VRF to do it:
the compiler port already runs every step forward (entity lump author, world physics, cables,
path deformers, instances and prefabs, nav markups, prop_static baking), so each decompile step
can be written as the inverse of a step we have, and checked by compiling the result with our
own compiler and comparing with the shipped package.

## Plan

1. `s2c decompile-map <map.vpk> -o <out.vmap>`: our own writer of vmap format 40 (DMX binary 9,
   we read it already in `Dmx/DmxBinary.cs`).
2. Entities from the lump, inverting `EntityLumpAuthor`: strip every key the compile adds
   (`compile_source_id`, `local.*`, bake keys, FGD defaults the lump filled in), restore the
   authored value forms (brightness per light class), un-flatten point_template child lumps.
3. Brush entities from their entity models (PHYS hulls and render mesh), tool materials from the
   collision attributes, as VRF does but with the vertex formats we already decode (`Vbib`).
4. World meshes from the world node, split by whatever per-source-mesh identity the compile
   keeps, not by connectivity. Which ids survive (draw call / fragment indices, node ids) is not
   known yet: settle it from the decompile of the world builder before writing this step.
5. Recover what can be recovered of Hammer-only nodes: cables from the lump's cable entities and
   their render models, prop_static baked to world from the world node, instances from the
   hammeruniqueid lineage. VRF reads `a:b:c` as the instance nesting; to be confirmed from the
   compiler's writer before we rely on it (GROUND_TRUTH rule).
6. A round-trip test per map: decompile, compile with ours, `map-diff` against the shipped
   package; the parity matrix's maps first, then the workshop corpus.
