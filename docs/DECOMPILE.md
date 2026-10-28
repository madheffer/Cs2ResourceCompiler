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

Not yet: an instance's copies inside a turned shared prefab map (s2c_prefabprobe11's 9 lights). A
copy's matrix there is the instance moved by the prefab against its group's placement
(`SettleWorld.Step`), whose translation rounds with the group's offset from the instance (1376 units
in the source): a second frame. In the map's own instances with an unturned group the copy's matrix
is the instance's own angles and a translation, so a group rebuilt at the origin and instances at
those translations reproduce it, the same problem `FrameSolver` solves: next.

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

atixref: 199 copies rebuilt as 143 instances of 13 groups and one nested group (template 630 inside
631's, 4 second-round collapses, blocks 7881 to 7888), every group solved on its first frame
candidate; every copy compiles as Valve's, ids and lump order included. (The first version's count
of 197 was read off the test's first 40 lines; the test had 848 differences, most from the block
order above.) Left on atixref, none of them instances: 9 template-lump angles (with prop 258's), the
4 rope paths and light 6478's precomputed bounds.

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
one.

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
2. Done for point entities, world layers, templates, prefabs, instances (multi-copy and nested)
   and settled props (stages 1 and 3 above); left: paths and cables, shared prefab families.
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

## Handoff (2026-10-28)

Branch research/vmap-decompile, worktree `D:\_cursor projects\source2-compiler-wt-decompile`,
last commit 01d1dd1, nothing pushed. Tracker 48.9% (18 of 44 subtasks done).

The check is `MapDecompilerTests.Entities_CompileAsTheSourceDoes` (20 maps, about 4 minutes;
`DECOMPILE_ALL=1` prints every difference, `DECOMPILE_LUMP=<file>` lists both sides of a lump).
Count the `unexplained difference(s)` from the summary blocks (`Failed ... Error Message`), not
from the interleaved test output, which pairs maps with the wrong counts.

| Map | Unexplained differences | What is left |
|---|---|---|
| 12 maps (probe01, probe_classes, ze_hold_em_p, ze_doom_p2_c_gameplay, prefabprobe 1/2/5/6/7, pinst, instnest, prefabinst) | 0 | passing |
| atixref | 33 | 4 rope paths (20 lines), 9 template-lump angles plus prop 258's (one ulp-scale turn), light 6478's precomputed bounds |
| s2c_prefabprobe3 | 36 | not itemised yet (atixref-based) |
| s2c_prefabprobe4 | 251 | not itemised yet (atixref-based) |
| s2c_prefabprobe9 | 883 | shared prefab families nested |
| s2c_prefabprobe11 | 28 | brush model taken from a later placement |
| s2c_prefabprobe12 | 28 | shared prefab copies inside turned prefabs |
| s2c_prefabprobe8 | 7 | the main map's own instances |
| s2c_pinst7 | 1 | the main map's own instances |

Next, in the order the user is likely to pick:
1. atixref's rope paths (`path_particle_rope_clientside`, rebuild CMapPath / CMapPathNode).
2. Template-lump angles: 236# and 257# angles one float step off (89.99999237 vs 89.9999771):
   find the forward's angle path for template members (MapInstances / point_template) in the
   decompile, then solve exactly; no fitting.
3. Light 6478's precomputed bounds and obb.
4. Itemise prefabprobe3 and 4 with `DECOMPILE_ALL=1`.
5. Shared prefab families (9, 11, 12), the main map's own instances (8, pinst7), turned outer
   collapses and deeper nesting.
6. Brush geometry: hull vertex order as an exact solve (GroupVertices hash-slot order), corner
   placement as an exact solve, render meshes and UVs, world meshes (plan 3 and 4).
7. Hammer: element defaults from Hammer's own map code, and an open-and-re-save check.

After each milestone: update `tools/map-survey/decompile_progress.json`, upload it as an asset of
the tracker and point `datasets/subtasks` at it (Progress tracker above).
