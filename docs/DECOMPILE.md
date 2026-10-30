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
- **Vertex order within a hull: done 10-29.** The compile collects a piece's points through a hash
  set keyed on their numbering (`BrushHulls.GroupVertices`), the numbering the triangulated faces'
  first-meet order, and the hull builder lists its first tetrahedron and then each point it adds:
  the order is mostly the geometry's, and the input order breaks ties (a box is all ties). The
  decompile (`MapDecompiler.HullOrder`) runs the forward's chain for one mesh
  (`EntityPhysicsModels.BrushMeshHulls`: Pieces, Inputs, the map builder's hull, ShapePoints, the
  compile's hull and its identity transform) on the faces as read, then on a seeded search over face
  orders and first corners (3000 tries), and keeps the first whose hull is the shipped one vertex
  for vertex in order. Brush entity PHYS exact: probe_classes 119 of 121 (was 86), ze_doom_p2_c
  272 of 274, ze_hold_em_p 32 of 32, s2c_prefabprobe2 21 of 23; one same-point-set hull left
  (ze_doom), past the budget.
- **Corners: an exact solve where the package allows, 10-29.** `CornerFor` searches world points the
  entity's inverse takes exactly to the hull vertex (16 floats a side), each reached from a local float
  per axis (the mesh's move is a plain add, which rounds). Each mesh is then judged by the forward's
  one-mesh chain (`SamePoints`: the hull's points bit for bit): the hull build can move a vertex a float
  off its corner's placed point (probe_classes' func_nav_blocker, two vertices 3.8e-6 low in z), so
  for a vertex no corner lands on, the corners landing up to 2 floats a side are combined until the
  chain gives the shipped points (`Nudged`); then a mesh turned with its entity, then one at the
  world's origin (a pivot is Hammer's to place). Brush entity PHYS exact: probe_classes 121 of 121,
  ze_hold_em_p 32 of 32, probe01 5 of 5, ze_doom_p2_c_gameplay 272 of 274 (teleportrandom2: a corner
  not found; teleportlab_4: the order not found in the budget), s2c_prefabprobe2 21 of 23 (two inside
  its prefab, whose placement the one-mesh chain does not model, so neither check runs there).
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

Shared prefab maps (2026-10-28, partial): placements under one parent whose nodes carry the same
inner ids and compile_source_ids are one map (`MapEntities.SourceIds`); the first in lump order is
written, the others become CMapPrefab nodes naming its file, their transform worked out from one node
(the rotation between the two placements' angles, the origin through `SettleWorld.AngleMatrixDouble`
in double) and checked on every node through `SettleWorld.PrefabPlacement`. Unturned and integer
placements land; turned ones miss in the last bits (s2c_prefabprobe11: 10 of 73 nodes at 90 degrees,
34 of 73 at 45; probe 9 likewise). The cause is information the compile erases: a turned placement
lands bit for bit only in the prefab map's own local frame (where the mapper's prefab origin was),
and in exact arithmetic every frame shifted by a vector, with the prefab origins moved to match,
gives the same placements, so only float rounding tells them apart. The same holds for the instance
copies of a group copied more than once (their group's origin).

The frame is solved exactly (user, 2026-10-28: an exact solver over frames; `Maps/Decompile/FrameSolver.cs`).
Measured first (`MapDecompilerFrameProbe`, the true sources): right-angle turns land in any frame,
a 45 degree turn only in the true one (s2c_prefabprobe11's prefab 110: 78/78 at the true frame, 55
shifted one unit, 74 at (16, 16)). The solver: the first placement unturned at an integer origin,
searched by grid level (multiples of 4096 first, down to 1, within 32768 units and 20 million
frames); each node's local coordinate among the floats the first placement's translation rounds back
to the stored one (a node at -447.81329 is placed at -6591.8135, so stored minus frame is not it);
each later placement's turn from the nodes' angles and its origin from the node with the fewest
preimages; each node's own angles among the stored ones and up to 6 floats either side on each
component (a translated prefab rebuilds its nodes' angles through `Moved`: a local yaw of 180 is
placed at 179.99995); nothing kept that `SettleWorld.PrefabPlacement` does not land bit for bit on
every node of every placement. s2c_prefabprobe11: the frame found is the source's own, prefab 108 at
(-6144, 0, 0), after 615 frames.

An instance's copies inside a turned shared prefab map (2026-10-29, s2c_prefabprobe11 and 12's 9
lights): a copy's matrix there is the instance moved by the prefab against its group's placement
(`SettleWorld.Step`), whose translation rounds with both places, so the instance and its group are
not interchangeable (in exact arithmetic only their offset counts). `FrameSolver.LocateCopies`
puts the instance where the first template's first copy stands in the map's frame (as the source
has it: instance (2682, 3332, 579) over its copy) and searches the group's offset D from it by grid
level (0, then multiples of 4096 down to 1), each template then located through
`SettleWorld.NestedPlacement` for every placement; nothing kept that does not land every copy bit
for bit. probe11: D = (4096, 0, 0) lands all 9 after 8 offsets (the source's is 1376, later in the
search order; an offset that lands compiles the same, and the first found is taken). Before this the template writer also wrote each copy at its raw
lump placement, not the solved local one (`TemplatePlan`'s writer), which put a shared map's copies
one prefab origin off.

Families nested in families (2026-10-29, s2c_prefabprobe9: s2c_fixmid placed 3 times turned 0,
90 and 180, each placing s2c_fixinner 3 times, and s2c_fixinner once more at the root turned 270):
- one family across parents and depths: placements share indices wherever they stand (the walk
  meets a loaded map once), so scopes are grouped by signature in lump order, parent before child,
  and a scope under a placement placed again joins none (it is that placement's own, written with
  the first);
- the inner map's frame is solved over all its placements, those under the outer map's later
  placements too (113:2157 is 112:2156's map through 113 and 2157), ten here;
- the solve gives composed transforms (the inner prefab node moved by the outer's matrix,
  `SettleWorld.PrefabPlacement`), so the family's own placements are moved into the outer map's
  frame (`IntoOuterFrame`: the origin the outer's unturned first placement moves onto it, checked
  on every node through both prefabs), and each later outer placement is checked on its nested
  nodes as well (`LandNested`);
- `FrameSolver` used to keep only the first origin that landed its reference node and drop the
  turn when that origin missed others; it now tries every origin that lands it (up to 16, a few
  floats either side and the whole-unit rounding): 112:2158 at (5120, 1024) derives as
  (5120.001, 1024.0006);
- copy blocks are paired with placements by place (`CopyBlocks`), since collapses run in the
  bake's rounds (115's a round before the nine nested ones), each pairing then landed bit for bit;
- a prefab map's template instance stands before its prefabs when its copies ship ahead of their
  nodes (fixmid's collapses first in their round);
- copy ids: one map at a time cannot set the steps between collapses (fixinner 3 nodes short puts
  fixmid's placements 9 too close), so with shared maps every collapse's first copy is matched at
  once (`MatchEveryCollapse`): one node more in each prefab map, and the main map's highest id, is
  measured through the forward on the rebuild, and the fewest paddings that land every collapse
  exactly (least squares over column subsets, never negative) are written.

A brush entity of a shared map whose model the package lacks in every placement (s2c_prefabprobe11
and 12's func_brush 2138: no impmodel_*_2138 at all, Valve's compile failed it, the lump still
names it) is noted as having no geometry for each placement, not only the first.

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

### The map's own instances (2026-10-28)

`Maps/Decompile/InstancePlan.cs`, from the forward's rules:
- copies: a node of the map whose id is at or past the lowest id carrying a repeated
  compile_source_id (copies' ids start past every node, MapInstances.Expand);
- groups: two template indices are one group's when they have as many copies, the k-th of each a
  constant id apart, and that offset less than the spacing between either's collapses (a block holds
  one copy of each template; equal patterns farther apart are two groups placed alike: atixref's
  templates 631 and 655, 164 apart);
- widths: each group's width is its least gap between consecutive blocks; a wider gap holds
  collapses that ship no copy (groups of prop_statics and meshes, whose copies bake into the world:
  atixref's 6755 and 6762), rebuilt as a stand-in group of empty nodes instanced once;
- landing: a collapse's copies land before the first shipped node after them in their own lump
  file (a world layer's lump walks apart), so its instance goes in a group placed at that walk index
  (its subtree ends there), or at the root when they land at the walk's end;
- order: the bake collapses in tree order, so the instances are written in block order as the walk
  reaches each landing place: a run landing at i in a group placed at i, and a root instance between
  two runs as a world child there (it lands at the end wherever it stands). atixref's 17 instances
  under group 6261 collapse between root instances (blocks 7704 to 7770), so writing every root
  instance after the walk, as the first version did, put their blocks first and moved 400 copy ids;
- nested: a group whose every collapse lands inside a distinct collapse of one other group (its
  copies between that collapse's, which run unbroken around them) and whose blocks follow that
  group's is instanced from inside its template, a round later (MapInstances.Expand: an instance in
  a target group is reached only through the group's copies, the copy inherits its outer copy's
  EmitAt). Its instance takes a free slot of the outer template after the templates whose copies
  come first; its group is written at the origin and its instance there, so with the outer collapse
  unturned the second step moves by the first one's matrix, and the inner templates are solved as
  the outer group's last members. Every inner copy is then checked through both steps
  (`SettleWorld.BakedPlacement` over the outer instance and the inner one);
- placement: with the group unturned its inverse only translates, so the group is written at the
  origin and `FrameSolver` places the instances, collapses for placements; the z axis is solved on
  its own (a yaw-only matrix leaves z to the translation, fl(local z + instance z), and only some
  splits land), frames are searched over two lattices (the origin on the grid, and the origin that
  puts the first node on its own grid: atixref's templates stand at their group's origin while their
  instances are off the grid), and a turn must land the reference node's angles as well as positions
  (a node at its group's origin lands its position under any turn).

One collapse (2026-10-29, s2c_pinst7): no index repeats, but its copies land where the instance's
parent's subtree ends, after nodes walked past their template, and a lump file's walked nodes ship
in walk order, so a node shipped after one of a higher index is a copy (the lowest such id starts
the copies, as above). A single collapse that lands in walk order compiles the same flattened and
is left so.

Collapses in a later round (2026-10-29, s2c_prefabprobe8: an instance of a group that holds only an
instance, beside a prefab with instances of its own): nothing in the copies' order shows it, only
their ids, which lie past the prefab's block (the inner instance collapses in round 2, after the
prefab took its block in round 1). Written flattened, the map's highest id is the copies' own and
moves the prefab's copies past theirs (2360 for 2271). So when a rebuild's prefab copies come out
past their ids, `MapDecompiler.Decompile` retries readings: the map's n highest nodes as one
collapse (n from 1) through w wrapper groups (0 to 2; a wrapper is a group holding only the next
instance, at the origin, instanced unturned), the main map's highest id padded so the first copy
lands, and keeps the first whose forward (`MapInstances.Expand` on the rebuild) gives every copy of
the map and its prefabs its id. probe8: 3 copies through 1 wrapper. A root instance whose copies
ship ahead of a prefab's nodes is written before that prefab (the bake collapses in tree order and
a copy lands after the maps of the prefabs collapsed before it). Not tried with shared prefab maps
(their frame solves would run again for every reading).

atixref: 199 copies rebuilt as 143 instances of 13 groups and one nested group (template 630 inside
631's, 4 second-round collapses, blocks 7881 to 7888), every group solved on its first frame
candidate; every copy compiles as Valve's, ids and lump order included. (The first version's count
of 197 was read off the test's first 40 lines; the test had 848 differences, most from the block
order above.) Left on atixref, none of them instances: 9 template-lump angles (with prop 258's), the
4 rope paths and light 6478's precomputed bounds; all closed later on 10-28 (Paths, Template members
below): atixref now compiles exactly as Valve's.

### Settled props

The compile settles every prop that can move (docs/SETTLE.md) and writes its settled origin and
angles back, and "Start asleep" when every body fell asleep. The authored pose is not in the
package: the settle is a 30 s physics run, many poses to one, so it does not invert. But it does not
need to: the settle skips a prop that has "Start asleep" (`SettleWorld.IsEligible`, from
FUN_180f1e490's list), so the decompile writes the settled pose and flag as the package holds them,
and the recompile keeps them. Hammer shows the props where they rest in game. The test settles both
sides as the compile does (`SettleLumpTests.Settle`; neither side when the settle refuses the
source): atixref's 81 settled props compile exactly as Valve's, and the atixref-based probes 3 and
4 drop from 219 to 36 and 479 to 251 differences. Open: a prop that settles but does not fall
asleep ships without the flag and would be settled again from its resting pose; none of atixref's 81 is
one. s2c_prefabprobe3's prop 2140:2239 is one: c2m2's prop falls out of the world, Valve's 30 s
leave it at z -11680 still falling, and the test's settle of the decompile drops it to -16373.

### Paths (2026-10-28)

A CMapPath ships as one entity whose nodes are folded into keys after hammerUniqueId
(`EntityLumpAuthor.PathKeys`, FUN_1810b5480): pathNodes (nine floats a node: the position
through the path's inverse node matrix, then the in and out tangents), pathNodeNames, the radius
arrays, closed_loop and one array per node key with a write_to_path_key. The nodes walk right
after the path and ship nothing (`MapEntities.Walk`). The inverse
(`EntityLumpAuthor.InversePath.cs`, `VmapBuilder.AddPath`):
- the path's origin is its first node's (MapEntities.Read), so the lump's origin is node 0's;
- each node's position is a float the inverse matrix takes to the row's first three, bit for bit
  (`Preimage`: the forward matrix's guess, then floats nearest to it);
- tangents: interpolationType 1 (the factory's, rc 1810b3e90) and then 0 decide every node's
  tangents; the first that lands every row is kept. Otherwise each node's own types, 0 or 1 where
  they land, else 3 with the authored tangent solved through the inverse rotation. For two nodes 0
  and 1 give the same tangents, so atixref's ropes (0 in the source) come back as 1; nothing in the
  lump tells them apart;
- node keys: classname (the path class's path_node_class), then the node class's keys in order,
  each from its path array or its default; a float key keeps its default text when that reads as
  the value (Hammer keeps "2", not "2.0"); pathNodeColors is not traced (PathKeys), so color_tint
  stays at its default;
- the nodes take the walk indices after the path, and ids after the path's where free and below
  the highest id the lump shows (otherwise from the made-up pool).
### World geometry (2026-10-29, survey and a first reader)

What a package keeps of the world's own meshes (probe01; `MapDecompilerTests.WorldGeometrySurvey`,
`DECOMPILE_WORLD=<vpk>`):
- **world_physics**: one triangle soup per collision attribute and surface, every world mesh merged
  (probe01: 196 vertices and 280 triangles, and an 8-vertex tool soup with its tool hash), its
  members' names joined (`m_UserFriendlyName`); nothing per source mesh.
- **world nodes**: per material an aggregate model (`n0_lr0_agg_merge_<material>`) whose draw calls are
  the compile's visibility clusters, not source meshes (probe01: 13 dev_measuregeneric01 pieces in 10
  fragments, 2 reflectivity_30 pieces in 27); float positions and texcoords, a 16-bit lightmap UV and a
  packed tangent frame (lossy), after the T-junction fix and the 1/32 weld.

So the split into source meshes is not in the package, and the target is meshes that compile to the
same output. `WorldGeometry` (an experiment, `DECOMPILE_WORLD_MESHES=1`):
- **Positions: the soups'.** A soup vertex is the source corner plus its mesh's origin in float
  (probe01's meshes stand at origins like 511.99994, so -768 lands -256.00006), exactly what the forward
  computes; the render positions are the closing 1/32 weld's representatives (another mesh's -256 wins).
  A mesh at the world's origin with the soup's positions as corners adds them back exactly, so no
  origin has to be recovered.
- **Materials and texcoords: the render's,** from the nearest render corner within 1/32 on a triangle in
  the same plane (all 280 of probe01's soup triangles; a tool soup's faces its tool hash's material).
- **Pieces:** each soup split into connected pieces, ordered by their first vertex, written in the
  inverse of PartOrder (the qsort the pieces reach the soups through), each piece's triangles in an order
  whose first use of the corners is the soup's vertex order (`FirstUseOrder`: RnMeshCreate keeps its input
  vertices in weld order).

Checked with WPBUILD on the decompiled .vmap (`WorldPhysicsAuthorTests.BuildsFromTheMap`, a 4th field
naming the package's map), probe01: CTRL, DATA, the manifest and every PHYS field exact, both soups'
vertices exact in order and their BVH nodes exact, but **the triangles' order** (soup 0 from its 7th,
the tool soup from its 5th) and RED2's physics_shape_count / physics_shape_mesh_count (16 source meshes
against 12 pieces: touching meshes weld into one piece).

Faces (10-29 night): each piece's triangles are merged back into the source's faces (`Polygons`:
coplanar, one material, sharing an edge the other way round, a merge kept only when our port of the
compile's triangulator cuts the merged loop, from some start, into exactly those triangles), and the
faces ordered (`PolygonOrder`) so their cut corners meet the soup's vertices in order, choosing among the
starts that do the one cutting its triangles in their shipped order (the BVH keeps a leaf's triangles in
input order). `WorldPhysicsAuthorTests.SoupInputsOfTwoMaps` (`DECOMPILE_SOUPS=<addon>|<source>|<decompile>`)
puts the RnMeshCreate input built from the true source beside the decompile's, triangle by triangle.
probe01: the tool soup now exact; the main soup's input agrees for its first 10 triangles, its output's
vertices for the first 108.

Open, in order:
1. **The order of faces that bring no new vertex.** At triangle 10 of probe01's main soup the source
   cuts a face whose corners were all met already before another such face; nothing in the soup's vertex
   order tells the two apart, and the shipped triangles are in BVH order.

   **BVH-driven search (10-29 late, behind `DECOMPILE_WORLD_SEARCH=1`; model exact, search not
   converging).** What was measured, all on probe01 against its true input (the source .vmap through our
   forward, which is exact; `SoupInputsOfTwoMaps` with `DECOMPILE_SOUPS_DUMP=<dir>` writes both inputs):
   - The source's faces are quads and 8 lone triangles (fan cuts (a,b,c),(a,c,d)); 13 of its 144 faces
     pair otherwise than ours: some coplanar neighbours are two triangle faces, and some quads pair across
     another neighbour than the closest-index one. So pairing is part of what has to be found.
   - Every box mesh has the same eight-quad face order, and ours parts from it where two faces can each
     bring the next vertex (vertex 11: the source cuts [6,8,11,9] before [9,11,4,3]); the vertex order
     cannot tell those apart.
   - The first vertex use along the true input is the shipped vertex order (196 of 196).
   - The render's index buffers regroup faces (pairs, permuted per draw call); they are not the source
     order (`DECOMPILE_RENDER_DUMP=<file>`).
   - RnMeshCreate's BVH partition (`RnMeshBuilder.Sah`) is a swap partition: the left ones keep their
     order; the right ones go round a queue (one arriving joins the back, a left one arriving after any
     right one sends the front to the back). Run backwards it is deterministic per choice: from the end,
     a left arrival is the left child's last and turns the queue back, a right arrival is the queue's
     back. `WorldGeometry.Preimage` builds an input that gives every shipped leaf (280 of 280 in place),
     but it splits faces and breaks the vertex order. `RnMeshBuilder.Trace` gives each node's entry and
     split.
   - `WorldGeometry.BackwardSearch` builds the input from its end with every node's partition run
     backwards (the leaves come out shipped by construction; a turn reserves the queue's back lazily and
     draws it from the right child only when needed). Each triangle the root gives ends a face (alone,
     from any corner, or a quad with a coplanar neighbour cut in that order), and a whole face must keep
     the vertex order (backwards: a vertex whose last use is placed must be the highest not yet placed).
     **The model is exact:** given the source's own per-node choices (`DECOMPILE_TRUTH=<source_0.txt>`)
     it finds an order in 2194 steps, and the decompile's world physics is then PHYS-exact (RED2's piece
     count aside). **The free search does not converge:** faces that bring no vertex are free under both
     the face and the vertex rule, so a wrong place shows only many steps later (deepest 23 of 280 in 20M
     steps with our order's node choices first; those differ from the source's at 228 steps over 42 of
     75 nodes, `DECOMPILE_TRUTH_COMPARE=1`). Listing each step's candidates in our order
     (`DECOMPILE_SEARCH_RANK=1`) branches on the reserved slots and stalls sooner.
   - Before the search, `Repair` runs the inversion (`Preimage` put back into faces under the vertex
     order) and local tries (restarts, re-pairings, moves; best in place 262 of 280). Neither reaches
     exact, and both cost a full RnMeshCreate per try, so the repair is off by default.
   **10-30: forward search (now the default direction; `DECOMPILE_SEARCH_DIRECTION=backward` for the
   other).** `WorldGeometry.ForwardSearch` places faces from the input's start (lone triangles or quads,
   chosen as it goes) and sends each triangle through `BvhRun`, which runs every node's partition
   forward with an undo log. Measured and added, in order:
   - In the source, of 144 faces 120 bring vertices in and the 24 that bring none trail their highest
     corner by at most 7 introductions (each stays in its mesh). The search keeps such a face within
     `DECOMPILE_SEARCH_LAG` (default 8) introductions, a search bound only (the result is checked against
     the shipped triangles); a covered face is tried from one start, as its corner order changes nothing.
   - **A node's right child's entry is known at the node's last right arrival**, not only when the node
     is whole: a queue's cyclic order never changes once in, and each left arrival still to come turns
     it once (`BvhRun.Arrive`, the flush). On probe01 the root's 56 right triangles arrive as one block,
     so its right subtree is checked at triangle ~224 instead of 280.
   - On each push of a triangle of the right child's leftmost leaf (whose order in the right child's
     entry is fixed), the queued ones of that leaf must already stand in a turn of its order.
   - Conflict-directed backjumping: a leaf's failure blames the decisions that placed the triangles its
     entry depends on (`BvhRun.Depends`: up the left links the input's own order, past a right turn the
     whole entry of the node above it); a table of failed states (placed set, next vertex, BVH state)
     catches repeats (a quad and its two triangles alone place the same sequence).
   Model check: along the source's order the forward search is PHYS-exact. Free: deepest 164 of 280
   (23 before), stuck at node 3 (54 triangles, its right side arriving in three groups). Even given the
   source's first 220 triangles it cannot finish the last 60 within 50M arrivals: what tells their orders
   apart are the flushes of nodes 1 and 2 (224 and 90 triangles), whose dependency sets are nearly every
   decision.
   **10-30 later: the big-node check (`DECOMPILE_SEARCH_ORACLE=<arrivals>`, off by default).** On each
   push into a node whose right child holds 8+ triangles, a sub-search on that right child's subtree
   asks whether some entry still fits: the queued triangles in a turn of their queue order (turns limited
   by the leftmost leaf), the rest anywhere. It is a relaxation (no faces, no vertices), so its no is
   a real no; out of budget counts as yes; answers are kept per queue state. Along the source's order it
   never refuses (PHYS-exact, 2195 arrivals). Measured on probe01 with 50M arrivals:
   | source's first n | before | with the check |
   |---|---|---|
   | 220 | deepest 278, fails | deepest 277, fails (6544 s; refusals node 2: 599k, 49: 87k, 52: 42k) |
   | 100 | — | deepest 223 (refusals node 0: 3.1M) |
   | 0 (free) | deepest 164 | deepest 164 (refusals node 3: 6.3M, node 4: 258k) |
   It prunes millions of states but the search gets no deeper: the dead ends are not decided by any
   one right child's entry but by the faces (pairing and vertex rule) together with the BVH.
   **10-30 evening: SOLVED on probe01 as one CP-SAT model** (`tools/map-survey/world_order_cpsat.py`,
   OR-Tools; the problem dumped by `DECOMPILE_SEARCH_DUMP=<json>`, the answer read back as soup ids by
   `DECOMPILE_TRUTH_IDS=<file>`). Free (no source data): an order in 490 s at Idle on 4 workers, **probe01
   world PHYS 0 differences** through WPBUILD, though only 60 of its 280 slots equal the source's (orders
   from fixed prefixes, 166 and 228 equal, are exact too: the shipped bytes do not pin one order). The
   model:
   - Per BVH node, p[x] = x's place in the node's entry (all different). Leaves: the shipped order. Split
     by count: the left set's places < a, children's places the node's (minus a on the right).
   - SAH node (swap partition), the closed form: with l[k] the place of the k-th left (increasing, through
     the left child's inverse), a right one at place d ends in the right child at place (follow
     d -> l[d] while d < a) - a. Why: Lomuto's left at i swaps a[i] with the right block's front, so a cell
     d >= a is final once written, and a left at place q writes the cell of its own left rank. Encoded as
     label[d] = label[l[d]] (d < a), label[d] = d - a (d >= a), right place = label[p[x]]. Checked equal
     to BvhRun on 20000 random nodes (`--test-partition`).
   - Input: root places are slots; each triangle alone or in one quad (its second in the next slot, the
     pair's corner starts from `begins`); corner times 3·slot + corner index under the start; vertex ids
     are first-use order: first(v) < first(v+1).
   - Redundant/bounds: each leaf's order is a precedence chain on its left-chain top's places; each
     connected piece takes one block of slots in vertex-range order (the soup is built mesh after mesh;
     true on the source: 11 pieces, all contiguous); the lag window (`--lag 8`, a search bound, the source
     needs 7, face-level: lone triangle or quad by the pair's highest corner).
   Measured: along the source's order 2 s; source prefix 220: 15 s; halves alone 36/77 s. Without the
   piece blocks the free solve did not finish in 500 s.
   **C# (10-30 night):** `src/Source2.Compiler.Solvers/WorldOrderCpSat.cs` (Google.OrTools 9.15), a
   project of its own so Source2.Compiler takes no solver dependency: the core builds
   `WorldGeometry.OrderProblem` and calls the `WorldGeometry.OrderSolver` hook, which `s2c decompile-map`
   installs. With `DECOMPILE_WORLD_SEARCH=1` (still off by default) the solver's order guides the forward
   search, which puts the faces back (`DECOMPILE_SEARCH_DIRECTION=forward|backward` skips the solver,
   `DECOMPILE_SEARCH_SECONDS`, default 1800). probe01 end to end: Optimal in 1054 s, world PHYS 0
   differences.
   **Left on probe01: RED2 physics_shape_count / _mesh_count 16 vs 4.** The count is the source's mesh
   pieces (16: nine boxes, the skybox, four boxes welded into one connected piece, two reflectivity meshes
   welded into another; `WORLDPHYS_PIECES=<file>` lists them). The soup welds across meshes, so where
   the meshes split changes no PHYS byte: the physics alone does not decide it. The render aggregates
   do not map 1:1 either (`agg_merge_*`: 28 draws for the 2 reflectivity meshes, 10 for the 13 boxes,
   merged and split again), so the split comes with the render-mesh port (Brush/world render item).
   Next: ze_hold_em_p (one connected piece, so no piece blocks; prop_static hulls first).

### prop_static from the world's hulls (2026-10-30)

A prop_static leaves the entity lump, and baked to the world (`baketoworld`) it leaves no render
object either (ze_hold_em_p's world node: `m_sceneObjects` empty, its fixtures merged into
`agg_merge_*`). Its hulls stay in world_physics.vmdl_c. `StaticProps` (Maps/Decompile) rebuilds them:
- Each prop hull is the model hull cooked in the model's space and then moved vertex by vertex, in
  order, by the node transform (`StaticPropHulls.Cook` + `RnHullBuilder.Transform`). So a shipped
  hull pairs vertex for vertex with a model hull of as many vertices, and a least-squares fit gives
  the placement nearly.
- The exact placement is searched around the fit: per angle the value, its whole degree and its
  1/1000, each ±4 float steps (and a turn away past 90); per origin axis on its own, from the
  component the first vertex asks for, ±16 steps.
- A placement holds when the forward's whole hull matches: vertices **and planes**. On ze, yaw 180
  and 180 + 2 float steps round every vertex alike, but turn the planes and the inertia.
- The models tried are the .vmap_c's RERL `models/*.vmdl`. A prop is all of its model's hulls under
  one placement.
- The order comes from the part's type sort (meshes 3, hulls 2, CRT qsort after every insert), which
  shuffles the first few of a run depending on where the props stood among the meshes. The props
  are written as one run after k world meshes, for the first k (most first) whose inverse puts each
  prop's hulls in a row (ze: after 2 of 14).

ze_hold_em_p (`DECOMPILE_WORLD_MESHES=1`, the CLI passing the game's models): 86 prop_statics from
all 129 hulls (43 fluorescent fixtures with 2 hulls, 43 bulbs with 1). Our forward on the decompile:
every hull exact. What still differs is the world soup's: material ids, triangle order, the mesh
piece count (Valve 9, ours 14 connected pieces) and the surface table order with it (a world
material's "concrete" missing).

Not covered: scaled props (moved before the cook), a model's spheres, capsules and meshes, deformed
props, props without physics (placement in the render only), and keys other than model (skin,
baketoworld and the rest are not in the physics).

**World faces' materials (10-30, later).** ze's 50 "toolsnodraw" faces were hr_conc_i_tintdark: its
aggregate draw splits its streams (SplitDepthStream: positions alone in the first vertex buffer,
texcoords and the rest in the second, one vertex numbering), and `RenderCorners` read only the first.
It now takes each stream from the buffer that has it: all 4548 of ze's soup triangles get a render
material, and "concrete" is back in the surface list. A fallback by surface (the soup's per-triangle
`m_Materials` into the part's `m_surfacePropertyHashes`, a neighbour's material of that surface first,
else the only render material with it) covers faces no render shows; ze needs none of it now.

**Left on ze's world physics: node 409.** The source's world is three meshes: nodes 12 and 13 (6
triangles each) and node 409, a subdivided, vertex-painted mesh of three materials (36 slab faces
tessellated to 4480 triangles, a blend material split by paint layer, hence the soup's name
" [concrete_block]"). Its pieces, as our tessellation path cuts them (`WORLDPHYS_PIECES`): slabs 0, 2,
concrete_9 16, slabs 0, hr_conc 30, hr_conc 8, slabs 4480 (two empty pieces count). That gives
Valve's 9 mesh pieces, the surface registration order (wet before concrete_block) and every
per-triangle surface id. A flat decompiled mesh cannot give that split: it needs node 409 rebuilt as
its subdivision cage with its paint (the inverse of the stitched subdivision bake).
   Debug switches: `DECOMPILE_SEARCH_BUDGET` (default 5M), `DECOMPILE_SEARCH_GUIDE=preimage` (worse:
   deepest 87), `DECOMPILE_TRUTH_PREFIX=n`, `DECOMPILE_SEARCH_PREFER=inverted`, `DECOMPILE_SEARCH_SLOTS=all`,
   `DECOMPILE_WORLDDEBUG=1`.
2. **Members (10-29 night): split by vertex runs; large maps run.** `WorldGeometry.Members` walks the
   soup's vertices in order: a triangle belongs to the member that introduced its highest vertex, and a
   new member starts where the triangles introducing a vertex share none with the current run or carry
   another material. Faces are rebuilt and ordered through edge and vertex indexes, so ze_hold_em_p
   decompiles in 6 s and its forward world physics builds in 4 s (WPBUILD had not finished in 15
   minutes on one connected mesh). Touching meshes still join one member (probe01: 4 against Valve's
   16; only RED2's piece count notices). Three ordering rules settled on the two maps' soup inputs
   (`SoupInputsOfTwoMaps`, triangles compared rotation aside, as RnMeshCreate turns each by its edges):
   - faces are quads at most (`DECOMPILE_FACE_TRIANGLES`, default 2): the triangulator check alone
     merges neighbouring coplanar source faces (ze's strip of quads at z 153 into one long face);
   - triangles pair into quads closest in vertex index first: a source face brings its vertices in one
     run, and on a coplanar strip a triangle can also pair with the next quad's;
   - of the faces that can bring the next vertex in order, the one bringing the fewest new vertices
     (ze's first block: its top, sharing an edge with the face before, ahead of a side sharing none).
   Now: probe01's soup vertices exact in order (its input agrees for 10 triangles, its output for 14);
   ze_hold_em_p's input agrees for 24 triangles, its vertices for 52. Both part at a face that brings
   no vertex the order decides: the BVH-driven search (open item 1) is what settles those. On ze the
   world's 129 prop_static hulls (not rebuilt yet) also register surfaces first and shift every mesh's
   surface indices. The soups' names list their members' source mesh names (`[concrete_block];
   [concrete_block]`), a member count and names where a map names its meshes.
3. Render-only faces (in no soup) and the T-junction triangles (render only, the compile's own).

### Element defaults from Hammer's map code (2026-10-29, partly)

resourcecompiler's mapdoclib declares a node's attributes in its constructor, each through the attribute
adder 180cdf760 (name, type, storage) and a typed setter (180ce8160 int, 180ce9bc0 bool, 180ce8cf0
string, 180ce8340 float, 180ce9890 element): only about 30 functions call it (58 sites).
- CMapRootElement (180f687f0): isprefab (false), editorbuild, editorversion, itemFile (""),
  defaultcamera, an element at +0x160 (3dcameras), world, visbility, mapVariables, rootSelectionSet,
  m_ReferencedMeshSnapshots, m_bIsCordoning (false), m_bCordonsVisible (false), nodeInstanceData: the
  fields and order `VmapBuilder` writes.
- CMapWorld (180fc0fa0): its bases first, CMapNode's nodeID, referenceID, children, variableTargetKeys,
  variableNames (180f8f920), the entity base's relayPlugData, connectionsData (180fef250),
  entity_properties (180f359c0), then nextDecalID (0), fixupEntityNames (true), mapUsageType
  ("standard"): as written.
- Not found this way: the placement and flag fields (origin, angles, scales, transformLocked,
  force_hidden, editorOnly) and the class fields (a mesh's, a cable's); they are declared some other
  way, likely the tool attribute tables hammer.dll's CConcreteToolAttr templates come from. Open.

### Prefab file names (2026-10-29)

The map's .vmap_c records the files its compile read (RED2 input dependencies, sorted, CRC each:
`MapDecompiler.MapInputs`), the prefab maps among them (s2c_prefabprobe9: maps/s2c_fixinner.vmap and
maps/s2c_fixmid.vmap). With one prefab map written (a shared family counts once) and one recorded,
the decompile's prefab map takes that file's base name, still under maps/prefabs/<map>/ so a
decompile never overwrites the source's own file. With more, nothing in the package pairs a file with
its placements (the order is alphabetical), so the maps keep their chain names and the recorded names
are noted.

### Cables (2026-10-29)

A cable class (@CableClass: cable_dynamic, cable_static; `FgdSchema.IsCableClass`) comes back as a
CMapCable (`VmapBuilder.AddCable`, the field order of a Hammer save): its nodes as for a path, its
tint from rendercolor ("%i %i %i", alpha 255: it never ships), its model's settings from
`CableSettings`:
- the render mesh is the render tube: samples x (sides + 1) vertices, (samples - 1) x sides x 6
  indices, so the side count and sample count are exact; its positions are stored coarsely (about
  2.5e-4 near the origin, 2e-3 further out, another order), so the radius only that closely;
- the spacing shows only through each node pair's sample count; any spacing with the same counts
  compiles the same (render and physics), and the first a mapper would type is taken (16, whole
  units, halves);
- a collision cable's physics mesh is exact: settings are kept only when the forward's mesh
  (`EntityPhysicsModels.CableShape` through `RnMeshBuilder.Create`, whose 1/32 weld joins a closed
  loop's last ring to its first) has the shipped vertices bit for bit; radius in float steps, flip
  and error searched;
- the material is the draw call's; the texture settings and particleSnapshotSpacing are not recovered.

Results: s2c_cablecoll's 15 collision cables and s2c_prefabinst's all exact; s2c_cablerender's nine
render-only cables with exact sides and samples and the radius to the mesh's precision (noted not
exact); every cable's lump exact. A cable_static ships nothing of its own in the lump (its tube is
world geometry) and is not recovered; a cable whose material is missing has no model and takes the
factory settings, noted.

Turned paths: several origins land on a row's position, and a node's type 0/1 tangents follow its
neighbours' origins, so the nearest preimage can miss (s2c_cablerender's cable at 10 35 -20). When it
does, every landing origin of each node (up to 32, nearest first) is kept and the choice that lands
every row is found pair by pair (a row depends on its node and its two neighbours); open paths only.

### Template members and the light step (2026-10-28)

A point_template's removed member comes back from its copy (`MapDecompiler.FromTemplateCopy`).
The copy is `TemplateTransform.Relative` of the member: its translation depends on the member's
origin only and its angles on the member's angles only, so the two are solved apart
(`TemplatePlacement`), each kept only when the forward gives the copy's floats bit for bit. The
matrix inverse is the first guess only: near a pitch of 90 the read-back is ill-conditioned
(atixref's lights at (90, 0, 0) come back 89.99998 and land 89.999992), so the guess's decimal
roundings and nearby floats are tried, fewest decimals first; several values can land and the one a
mapper would type is taken.

Keys a lit compile's light step writes in place are not the source's: the precomputed bounds and
box are traced again (`LightPrecompute`; atixref's light 6478 holds stale ones from an earlier
placement in the source), and directlight goes 3 to 2 on a stationary light with no shadow
(`BakedShadowAssignment`). The decompile keeps the package's values, which a lit recompile gives
again; the test's truth side has no light step and ships the source's, so the test accepts those keys
only where ours equal what Valve's package holds. One case inverts exactly: desiredstationary 1
with bakedshadowindex -1 is written only with directlight 1 on a light of mode 3 (no free shadow
channel), and for a non-legacy class the mode is directlight itself, so the source's was 3
(s2c_prefabprobe11's later placements).

### A prefab map's own instances (2026-10-28)

A prefab map whose copies hold more than one collapse (in id order the template indices start
again) is planned as the map's own instances are (`InstancePlan.Build` over the prefab map's nodes
and copies), and the copy ids padded as for one collapse (`MatchCopyIds`). One collapse stays with
`TemplatePlan`, which until now kept the first collapse and dropped the rest without a note.
s2c_prefabprobe3: c2m2's 12 copies come back as 4 instances of 1 group, exact. Not planned: a prefab
map that places prefabs (their blocks sit among its collapses'; InstancePlan does not model them)
and any prefab map of a map with shared prefab maps (s2c_prefabprobe9's planned group landed 78
walk indices late). s2c_prefabprobe4 (atixref placed as a prefab at (1024, 512, 64) turned 90) is
exact since 10-29 (Handoff, item 1).

### Valve's resourcecompiler on the decompiles (2026-10-29, CS2 closed)

`tools/map-survey/decompile_rc_batch.ps1 -SameOptions` decompiles each map, compiles the decompile
with resourcecompiler `-world -fshallow`, compiles a renamed copy of the true source
(`s2c_src_<map>.vmap`) under the same options, and compares the two packages
(`ResourceCompilerOfTheDecompile_AgainstValve`: the `s2c_src_`/`s2c_dec_` prefix taken out of every
value, a handshake compared as its index past its own map path's hash, which the four probe volumes
checked land on exactly). The package's own build is the wrong yardstick: a full lit build adds the
light step's keys, and its source may be an older revision. All five decompiles compile.

With the light step's keys explained as the decompile check does (`decompile_rc_batch.ps1` passes the
map's own package as a third path: a key counts as explained where the decompile's build holds what
that package shipped):

| Map | Lump differences (first run, then with the rules below) | Entity model PHYS | What is left |
|---|---|---|---|
| s2c_pinst7 | 27 (vs the package's full build), 5, now 3 | 5/5 equal | func_brush 2138 (failed model, below) and the order/count it moves |
| s2c_prefabprobe8 | 51, 8, now 4 | 10/10 equal | as pinst7 (2138 twice) |
| s2c_prefabinst | 38, 15, now 13 | 5 equal, 5 vertex-order hulls, 2 missing | as pinst7; its two cable_dynamic copies (not rebuilt as CMapCable: no model, no pathNodes) |
| atixref | 2859, 2608, now 2138 | 104/142 equal | 28 failed-model or render-only brush entities; every other entity under the same id, class and origin; the rest is precomputed light bounds, traced by resourcecompiler against a world the decompile has no meshes for (plan 4) |
| s2c_prefabprobe4 | 1117, 699, now 2135 | 96/147 equal | as atixref (29 brush entities), plus two settled props' poses (2140:477, the source settled again, which varies run to run); 4 light keys explained |

Before the rules below, atixref's and probe4's copies shipped under other ids: our forward and
Valve's resourcecompiler gave the same decompiled .vmap different blocks. Now our lump author on
each decompiled .vmap gives Valve's ids for every entity (atixref: all 736, `DECOMPILE_IDS`).

Two rules came out of it, each measured on a probe compiled by resourcecompiler:
- **A hollow node takes no slot in an instance's block, and a hollow target's collapse no block**
  (`MapInstances.Hollow`: a CMapGroup with only hollow nodes under it, none included, or a
  CMapInstance of a hollow group). Measured with `tools/map-survey/slot_probe_map.py`: [light, X,
  light] copies with B's id = A's + 1 + the slots X takes.

  | X | slots | walks | probe |
  |---|---|---|---|
  | empty group; group holding an empty group; two empty groups | 0 | no | s2c_slotprobe |
  | group holding an info_target (or an editor-only one) | 2 | 1 | s2c_slotprobe |
  | info_target, editor-only info_target | 1 | yes | s2c_slotprobe |
  | prop_static with or without its model | 1 | yes | s2c_slotprobe3 |
  | CMapMesh with faces (two: 2) | 1 | no | s2c_slotprobe3 |
  | CMapMesh with no faces (two: 0); group holding one | 0 | no | s2c_slotprobe8 |
  | CMapMesh whose faces are all toolsskip | 1 | no | s2c_slotprobe8 |
  | instance of an empty group (two: 0) | 0 | | s2c_slotprobe4 |
  | instance of a group holding a faceless mesh | 0 | | s2c_slotprobe8 |
  | instance of a group holding a mesh with faces / a toolsskip mesh / an info_target | 1 | | s2c_slotprobe4, 8 |
  | group holding only an instance of an empty group | 0 | | s2c_slotprobe6 |
  | [light, instance of a group, mesh with faces] as a whole group, no B | 4 wide | | s2c_slotprobe7 |

  (s2c_slotprobe3, 4 and 7's "empty mesh" is a mesh with faces: the generator took the first mesh
  with no node children, which every mesh is. Probe 8 has the faceless one.) A top-level instance of
  an empty group takes no block (s2c_slotprobe5). An empty group is not removed at load:
  s2c_slotprobe2's, 106 ids past the rest, moves the first block by as much (the map's highest node
  id still counts it). Our forward counted every child and every collapse, so the decompile's
  empty-group fillers and its one-id stand-ins (instances of empty groups) agreed with our forward
  and not with Valve's. The fillers are now 16-unit toolsskip boxes (`VmapBuilder.AddSlot`: a slot,
  no walk, and nothing in Valve's world: s2c_slotprobe8 and 9, the same map with the toolsskip meshes
  faceless, compile to the same world_physics byte for byte and render meshes of one size). Our own
  world builder does not drop toolsskip faces yet (open). A one-id leftover between two collapses is
  no stand-in (none is under 2 ids) but the next group's lead, the slots before its first shipping
  template (atixref's group of 663 leads with a prop_static): `InstancePlan` solves gap = W(c) +
  L(next) + H with H 0 or at least 2. A prefab's block skips hollow nodes too (`PrefabSlots`;
  s2c_prefabslot, `tools/map-survey/prefab_slot_probe_map.py`: a prefab map of a light and five empty
  groups makes a block as wide as one of the light alone), so the decompile pads prefab maps with
  toolsskip boxes and the main map (its highest id) with empty groups (`Pad`). SettleWorld's prefab
  walk and MapWorldOrder skip hollow nodes the same way (no slot, no copy).
- **A brush entity with no meshes does not ship** (s2c_brushless, `brushless_probe_map.py`):
  func_brush, trigger_multiple, func_button, func_door, func_breakable and func_water with no
  children are all dropped; a func_brush whose mesh names a missing material ships (its model
  compile fails). Our forward already drops them. A decompiled brush entity whose model the
  package lacks (the source's compile failed it) has no geometry to rebuild and so is dropped by a
  recompile: pinst7's 2138, atixref's 22 doors, buttons and breakables. func_water's models are
  render-only (no hulls) and come back with render meshes (plan 3).

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
2. Done for point entities, world layers, templates, prefabs, instances (multi-copy and nested),
   paths and settled props (stages 1 and 3 above); left: cables, shared prefab families, turned
   prefabs with instances.
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

## Handoff (2026-10-29)

Branch research/vmap-decompile, worktree `D:\_cursor projects\source2-compiler-wt-decompile`,
nothing pushed. Tracker 59.7% (22 of 44 subtasks done, most others partly).

The check is `MapDecompilerTests.Entities_CompileAsTheSourceDoes` (20 maps, about 16 minutes, most of it probe4's frame searches;
`DECOMPILE_ALL=1` prints every difference, `DECOMPILE_LUMP=<file>` lists both sides of a lump).
Count the `unexplained difference(s)` from the summary blocks (`Failed ... Error Message`), not
from the interleaved test output, which pairs maps with the wrong counts.
`BrushPhysics_CompilesAsTheSourceDoes` on s2c_prefabprobe2 fails as it did before this work (one
of stage 2's 3 differing hulls, carousel_door01_108_27).

| Map | Unexplained differences | What is left |
|---|---|---|
| 19 maps (probe01, probe_classes, ze_hold_em_p, ze_doom_p2_c_gameplay, atixref, prefabprobe 1/2/4/5/6/7/8/9/11/12, pinst, pinst7, instnest, prefabinst) | 0 | passing (10-29: pinst7, probe8, 9, 11, 12 added) |
| s2c_prefabprobe3 | 2 | prop 2140:2239 still falling when Valve's settle ends (no Start asleep), settled again by ours: not invertible from the package |

The whole check now takes about 3 minutes at Idle on 4 cores (probe4 1 of them, probe9 and atixref 20 s each),
1.5 minutes with CS2 closed. `DECOMPILE_SAVE=<dir>` writes the decompile it judges; `DECOMPILE_IDS=<vmap>|<map>`
lists our lump author's ids on any .vmap (`LumpIdsOfAFile`).

CS2-closed session 10-29 evening (d167724):
- Done: `tools/map-survey/decompile_rc_batch.ps1 -SameOptions` (refuses to run with cs2.exe up;
  `-CompareOnly` re-reads the packages of a run): Valve's resourcecompiler on the five decompiles,
  against its build of each true source under the same options. Results and the two rules it found
  (hollow nodes, ledger 182; brushless brush entities) in "Valve's resourcecompiler on the
  decompiles" above. Our forward on each decompiled .vmap now gives Valve's ids.
- Done: the cable probes, compiled `-world`: s2c_cablecoll (17 collision cables) and the new
  s2c_cablerender (`tools/physics/cable_render_map.py`: materials, tint, secondary material, texture
  scale/orientation/offsets, shape settings with collision off). Packages kept at
  `%TEMP%\fresh_compile\s2c_cablecoll.world.vpk` and `s2c_cablerender.world.vpk` (and in the addon's
  game folder). Their full builds failed in vrad3: a GPU hang (Aftermath: MMU page fault in
  `ray_tracing_02`, -vulkan -gpuraytracing) five times on each map, where probe_cable's full build
  worked the same day; the cable models are in the -world builds, which is what item 4 reads.
- Left, CS2 closed: the Hammer open-and-re-save (item 8). engine2 registers `open_asset` ("Opens an
  asset in its primary editor"; Hammer for a .vmap) and hammer.dll `hammer_force_autosave`, so the
  check can run over netcon without clicking Save: `cs2.exe -tools -insecure -addon s2c_rc_probe
  -netconport 29000 -novid`, `open_asset maps/s2c_dec_s2c_pinst7.vmap`, `hammer_force_autosave`, then
  diff the autosave against ours. Not tried yet (the user's CS2 came up first).
- Decided (user, 10-29): a brush entity whose model the source compile failed (missing material) has
  no geometry in the package and stays out of the decompile, with its note ("its model ... is not in
  the package (the compile failed it); no geometry"); a recompile drops it where Valve kept it. No
  placeholder mesh that fails the same way. The RC check still lists them as "missing from ours".
- Open from it: our world builder does not drop toolsskip faces (the decompile's fillers are
  toolsskip boxes).

Done this session: atixref's ropes (CMapPath rebuilt, positions and tangents solved), template
members' placement as an exact solve, light-step keys (test checks them against the package;
directlight 1 with desiredstationary inverted to 3), a prefab map's own multi-collapse instances.

Next, in the order the user is likely to pick:
1. Turned prefabs with instances. Done 10-29 for probe4 (exact): `FrameSolver` takes a
   right-angle first placement (`quarter`), each collapse tried unturned, then turned by the right
   angle nearest its copies' common yaw, then the one nearest the most common yaw across the scope
   (group 745's copies stand at 0, its template turned against the prefab's 90). Turns are tested
   as the load holds them (`SettleWorld.SetAngles` via MapNodeAngles: -89.99995 written lands as
   270.00005, a float off: lights 8379/8381), and about the nearest whole degree too (a turn of 0
   derives as 5.3e-5). A frame search stops at 2M frames or 50M landings, whichever comes first
   (group 731's tries cost 7000 landings a frame and one ran over an hour; the most a solved frame
   took is 1.08M). Nested groups the one-step solve cannot land (probe4's group 701, pitched
   (90 90 0), under turned collapses) go through `InstancePlan.NestedSolve`: the outer group solved
   on its own members (each frame kept only if the nested solve then lands), the inner instance,
   its group and the first template sharing their angles with the template at the group's origin,
   as atixref's source has them (6207, 6203, 6201); the instance and group origins searched axis by
   axis, the group origin over integers then finer grids (the source's -456.4531 turns 55.66162
   into 55.66165). `VmapBuilder.AddGroup` writes the group's placement. `DECOMPILE_FRAME_LOG=1`
   prints each try, where its first node is, and what it cost. Still open: the turned copies in 11
   and 12 (shared prefab maps, item 3).
2. The main map's own instances (probe8, pinst7): done 10-29 (one collapse; later rounds through
   wrappers; "The map's own instances" above). `DECOMPILE_TREE=1` prints both node trees,
   `DECOMPILE_TREE=2` with placements and the true prefab maps' trees.
3. Shared prefab families: done 10-29, 9, 11 and 12 exact (copies in turned placements,
   LocateCopies; families nested in families above; their func_brush had no model in any placement).
4. Cables: done 10-29 ("Cables" above): lumps exact, collision cables' settings exact; open: a
   render-only cable's radius (the render mesh's precision), texture settings, cable_static (world).
5. A prefab map that places prefabs, with instances of its own (InstancePlan with prefab blocks).
6. The falling prop (probe3): a prop the settle leaves awake is not invertible from the package; at
   most note it.
7. Brush geometry: hull vertex order and corners done 10-29 (451 of 455 models' PHYS exact; left:
   two in s2c_prefabprobe2's prefab, whose placement the one-mesh chain does not model, and two on
   ze_doom); render meshes and UVs, world meshes (plan 3 and 4).
8. Hammer: element defaults from Hammer's own map code, and an open-and-re-save check.

After each milestone: update `tools/map-survey/decompile_progress.json`, upload it as an asset of
the tracker and point `datasets/subtasks` at it (Progress tracker above).
