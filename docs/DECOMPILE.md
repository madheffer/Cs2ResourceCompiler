# Decompiling a compiled map back to a Hammer .vmap

The goal: take a shipped map package (`maps/<map>.vpk`) and get back a `.vmap` a mapper can open
in Hammer, fix, and compile again, as close as possible to what the author saved. Uses: maps whose
author has left CS2, fixing a map after a CS2 update breaks it, and a large supply of real maps for
testing our compiler (decompile, compile with ours, compare with the shipped package).

This file is the 2026-10-28 measurement of VRF's decompiler (`MapExtract`) against maps whose
Hammer source we have, and what is missing for the goal above. Branch `research/vmap-decompile`.

## Progress tracker

Live page: https://claude.ai/artifact/8gxTQS9dAE7SEe2es6unz7 ("Map decompiler progress"). It lists
every module of the decompiler, each module's subtasks with a weight and how much is done (0 to 1),
and the test or map that checks it; the module and overall percentages are worked out on the page
from those rows (done weighted by weight).

Rule (user, 2026-10-28): **update it after every milestone.** A subtask counts as done only when it
is checked: the decompiled map compiled by our compiler or Valve's resourcecompiler and compared with
Valve's compile of the true Hammer source. To update: edit the rows in
`tools/map-survey/decompile_progress.json`, upload the file to the page as an asset, and point the
page's `subtasks` source at the new upload with `updated` set.

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
  `env_particle_glow` `snapshot_mesh` 3 -> 0. The brightness keys are the ones the compile's
  light preprocess rewrites (`EntityLumpAuthor.Preprocess`, `LightUnits.Sync`); VRF copies the
  rewritten values back as if the mapper had typed them.
- Not losses, though the compare flags them: `point_worldtext`'s colour has no alpha because the
  compile drops a 255 alpha (`EntityLumpAuthor.Color`) and the reader puts it back
  (`CNumbers.ToColor`); `local.origin`, `parentAttachmentName`, `useLocalOffset` are ordinary
  string keys in Hammer's own save, there with empty values.
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

## Our decompiler, stage 1: entities (2026-10-28)

`s2c decompile-map <maps/<map>.vpk> -o <out.vmap>` (`Maps/Decompile/MapDecompiler.cs`).

- **The file.** Our own binary DMX writer (`Dmx/DmxBinaryWriter.cs`), each attribute written with
  its DMX type, which the reader now keeps (`DmxBinary.Element.Types`). Checked by reading,
  writing and reading again four Hammer saves (probe_classes, s2c_instnest, ze_hold_em_p,
  atixref's 9.9 MB): the graphs are equal, element for element and type code for type code
  (`DmxBinaryWriterTests`). The string table is in first-use order; Hammer's own order is not
  ported, so the file equals a Hammer save as a graph, not byte for byte.
- **The shape.** vmap 40, every element with the attribute set, order and types of Hammer's save
  (editorbuild 9806; `VmapBuilder.cs`, measured with `tools/map-survey/vmap_schema.py`). VRF writes
  vmap 29 and leaves the upgrade to Hammer.
- **The entities.** `EntityLumpAuthor.Unbuild` is the forward `BuildEntity` run backwards, rule by
  rule: the key table read back to front between the compile's own keys; the FGD defaults it
  filled taken off (the shortest source part whose rest is exactly the fill, which compiles the
  same either way); the light preprocess's and the bake's appended keys taken off; bake
  resources the compile filled emptied; the `[PR#]` fixup taken off names and targets; typed
  values back to text; outputs back to `DmeConnectionData`. Template copies (`<id>#entitylumpname`)
  give way to their originals; a member the template removed is placed back through the
  template's transform and its `&0000` names restored. World layer lumps become
  `CMapWorldLayer` nodes. Entities are written in `compile_source_id` order, and a position no
  lump holds (a hidden, editor-only or compile-only node) gets an editor-only `info_target`, so
  the walk numbers everything as the compile did.

The test (`MapDecompilerTests`) decompiles Valve's package and runs our lump author on the
result and on the true source: the two must agree on every lump, entity, value, type, key order
and output. Passing, with only the listed gaps, on probe01, probe_classes, ze_hold_em_p and
ze_doom_p2_c_gameplay. The gaps, all one cause: **geometry is not rebuilt** (a brush entity with
no mesh does not ship, 5 / 122 / 40 / 304 of them; a template's member index shifts behind one;
probe_classes' particle snapshot names a world mesh).

Through Valve's compiler too: ze_hold_em_p decompiled, compiled `-world -fshallow` by
resourcecompiler (exit 0, no errors) as `s2c_dec_holdem`, and its default_ents compared with
Valve's compile of the true source (`ResourceCompilerOfTheDecompile_AgainstValve`): all 139
non-brush entities equal in every key, except the light bounds the bake clips against world
geometry (37 lights) and the keys that name the map (`worldname`, probe and cubemap paths,
handshake), which differ because the test map has another name.

Not passing yet: **atixref** (instances: their copies share their template's compile_source_id
and use the instanced key table, `KeyTable`'s `Instanced` branch; and path_particle_rope paths,
whose nodes are CMapPathNode children). Prefab entities (`a:b` id paths) are written as nodes
of the map.

## Stage 2: brush entity geometry (2026-10-28)

Each brush entity's model (`maps/<map>/entities/<name>_<id>.vmdl_c`) gives back its hulls
(`Maps/Decompile/BrushGeometry.cs`), one CMapMesh per hull under the entity:

- **Faces**: the hull's own (RnHull half-edges: next, twin, origin, face), wound counter-clockwise
  about the outward plane as Hammer's faces are. A hull is its piece's convex hull and its
  vertices are exactly the piece's welded corners (BrushHulls.Build keeps them), so a convex
  piece comes back as drawn; a concave one comes back as its hull.
- **Material**: exact, from the hull's `m_nToolMaterialHash` (MurmurHash2 of the fixed-up name,
  `ResourceNames.ToolMaterialHash`) looked up over every .vmat in csgo and core pak01 and the
  package (`BrushGeometry.GameMaterials`). A class with `auto_apply_material` (the triggers)
  hashes the applied material, which is what it compiles with anyway.
- **Placement**: the mesh stands at the entity's origin, unturned, as Hammer leaves a mesh drawn
  under an entity. Each corner is chosen so the compile's two moves (`BrushHulls.PiecesWithCorners`:
  the mesh's CTransform to the world, the entity's inverse back) land on the hull's vertex bit for
  bit (`MapDecompiler.CornerFor`): a grid value (1 down to 1/1024) when one lands exactly, else
  the inverse refined by Newton steps and searched on the world float grid.
- **Hull order**: the model's part takes its shapes through `WorldCollision.PartOrder`, a CRT qsort
  on shape type after every insert; with one type that is a fixed permutation of the input, so
  the meshes are written in its inverse and the hulls come out in the model's order.
- **Mesh shape**: Hammer's half-edge arrays and streams (`VmapBuilder.AddMesh`), the streams named
  `<name>:0` as readers find them; texture axes Hammer's world alignment at scale 0.25 (texture
  coordinates are not recovered).

Measured through our forward (`MapDecompilerTests.BrushPhysics_CompilesAsTheSourceDoes`, our entity
model builder on the decompile against it on the true source, PHYS block for block):

| map | models | PHYS exact | same hull point sets | differ |
|---|---|---|---|---|
| probe01 | 5 | 5 | 0 | 0 |
| probe_classes | 121 | 86 | 33 | 2 |
| ze_hold_em_p | 32 | 31 | 1 | 0 |
| ze_doom_p2_c_gameplay | 274 | 229 | 44 | 1 |

And through Valve's: ze_hold_em_p decompiled and compiled `-world -fshallow` by resourcecompiler
(38 compiled, 0 failed, where the true source fails 8 models on missing materials): **31 of its 32
entity models have PHYS equal to Valve's compile of the true source**, the one left a same-point-set
case. Its lump: every entity equal but the 8 brush entities whose models Valve's own compile failed
(no geometry reached the package), the keys naming the map, and the light bounds the bake clips
against world geometry (stage 3).

What is left in stage 2:
- **Vertex order within a hull** (the same-point-set rows: vertices identical, mass properties and
  areas differ in the last bits). The compile collects a piece's points through a hash set keyed
  on their numbering (`BrushHulls.GroupVertices`), the numbering the triangulated faces' first-meet
  order, and the hull builder's output order follows its input's. Inverting that means choosing the
  face and corner order that numbers the points into the hull's order.
- Three models with a corner no searched point places exactly (listed in the test).
- **Render meshes**: a brush entity with visible faces gets them back from its hulls with world
  alignment; the render mesh's UVs and render-only faces are not read yet.
- Triangle-mesh shapes (physicsType mesh), and brush entities whose model the compile failed.

## Stage 3: prefabs and instances (2026-10-28, in progress)

What has to be rebuilt, and why it is not optional: the compile treats a prefab's or an instance's
contents differently from a node of the map, and the difference is in the lump, so only the same
structure compiles to the same output.

**Prefabs (single placements done).** A node a prefab's map holds ships with its id path
(`hammerUniqueId` "108:3": prefab node 108, node 3 of its map; "108:5:3" nested;
`MapEntities.Entity.IdPath`). The decompile writes one map per prefab chain
(`maps/prefabs/<map>/prefab_<chain>.vmap`, the files `Result.Prefabs`) and a CMapPrefab node naming it
(`VmapBuilder.AddPrefab`, at the origin unturned, its nodes at their compiled placement):
- Numbering (`MapEntities.Walk`): a prefab's map is walked where its parent's children end, its world
  one index ahead of its nodes, so each scope is written in compile_source_id order with its prefabs
  after its own nodes, and a prefab's world at its block's first index.
- Name fixup: a node of a prefab with fixupEntityNames ships its names as "[PR#]" + the outer prefix +
  own + "_", own its node id, or its targetName with useTargetNameAsPrefix; one without keeps the outer
  prefix (`MapEntities.Walk`, `EntityLumpAuthor.BuildEntity`). The decompile reads each prefab's from
  its nodes' names (`MapDecompiler.PrefabFixups`) and takes it off before the inverse.
- Passing (`Entities_CompileAsTheSourceDoes`): s2c_prefabprobe, s2c_prefabprobe2 (c2m2's gameplay map
  as a prefab, its brush entities included).
- Not yet: one map placed by several prefabs (one source: its nodes share compile_source_ids,
  `MapEntities.SourceIds`, and every second placement ships its keys forwards, `KeysForward`;
  s2c_prefabprobe9, 11, 12), and prefab maps that hold instances (s2c_prefabprobe5, 7).

**Instances (the forward rules the inverse follows; `MapInstances.Expand`).** A CMapInstance names a
CMapGroup (its target); the group's entities are walked and numbered but never ship; each collapse
writes one copy of them:
- the copy's `compile_source_id` is its template's walk index;
- its id is its collapse's block start plus the template node's slot in the group, every node counted
  (meshes and groups too); blocks are allocated in collapse order (rounds of the document tree, a
  prefab taking a block of its map's copy count plus one in the first round) starting past the
  highest node id of every element plus the nodes the loader creates (smart prop locators);
- it lands in the lump where the instance's parent's subtree ends;
- its key table is a new entity's (`KeyTable`'s Instanced branch: classname, targetname, the class's
  keys in finalized order, the template's undeclared keys reversed);
- its placement is the instances' transforms over the template's.

Done (2026-10-28): copies made inside a prefab's map. A plain-id node numbered at or past a
prefab's block is such a copy (the map's own nodes are walked before its first prefab's world). Its
prefab's map gets the template group at the copies' walk position (`MapDecompiler.TemplatePlan`: a
walk-index gap between two copies is editor-only placeholders, which walk and whose copies ship
nothing since a copy keeps its template's Hidden; the rest of the id gap is empty groups, which take a
slot and do not walk), keys read back through the instanced key table
(`EntityLumpAuthor.Unbuild(instanced: true)`), and an instance at the map's root with its group's
transform, so the collapse moves nothing (`SettleWorld.BakedPlacement`: no delta when the two agree).
The copies' ids are then matched by running `MapInstances.Expand` on the rebuilt maps and padding the
prefab map ahead of the collapse by what the copies come out short (each node a map gains moves every
later block by one). Passing: s2c_prefabprobe5 and 7 (an instance in a prefab, and that prefab inside
another), s2c_prefabinst, s2c_prefabprobe6. Ids the decompile makes up come from below the ones the
compile shows, so none raises the highest id the blocks start past.

Open, all the same gap, several collapses of one template: s2c_prefabprobe3, 4, 9, 11, 12 and atixref
(nested instances, collapse rounds, and the instance transforms of every copy after the first, to be
solved exactly through `BakedPlacement`), and instances of the map's own (s2c_prefabprobe8, s2c_pinst7:
their copies' ids and lump places).

So the inverse rebuilds each instance as an instance: copies grouped by collapse (shared template
indices, contiguous id blocks), one template group per distinct set, the instance nodes, and node ids
and group sizes chosen so the allocation gives the copies' ids; single copies included, since a copy
written as a plain node keeps its large id and moves every later block. s2c_pinst and s2c_instnest
already compile the same flattened (each template copied once, at the map's end), which is why they
pass, but are not rebuilt as instances yet.

## Ground truth for the decompiler

The decompiler is checked against maps whose Hammer source we have, compiled by Valve's
resourcecompiler: a decompile is right when what it gives compiles to what the source compiles
to. The s2c_rc_probe maps are already that: authored here with Hammer or generated by our
scripts and saved through Hammer, compiled by Valve's tools. The next stages add probe maps
for what each stage brings back (brush entities of every tool material, world meshes with
known face splits, a prefab, an instance, a rope and a cable, layers, a template that removes
its members), each compiled -world and full.

## Plan

1. Done: `s2c decompile-map`, our own vmap 40 writer.
2. Done for point entities, world layers and templates (stage 1 above); left: instances, paths
   and cables, prefabs.
3. Brush entities from their entity models: hulls done (stage 2); left the hull vertex order,
   render meshes (UVs, render-only faces) and triangle-mesh shapes.
4. World meshes from the world node, split by whatever per-source-mesh identity the compile
   keeps, not by connectivity. Which ids survive (draw call / fragment indices, node ids) is not
   known yet: settle it from the decompile of the world builder before writing this step.
5. Recover what can be recovered of Hammer-only nodes: cables from the lump's cable entities and
   their render models, prop_static baked to world from the world node, prefabs from the
   hammeruniqueid id path. VRF reads `a:b:c` as instance nesting; our port of the writer
   (`MapEntities.Entity.IdPath`, NodeIdPath_Format) says it is the CMapPrefab node ids, then the
   node's own. Instances leave no id path: their copies are new entities.
6. A round-trip test per map: decompile, compile with ours, `map-diff` against the shipped
   package; the parity matrix's maps first, then the workshop corpus.
