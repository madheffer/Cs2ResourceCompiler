# Models: the ModelDoc compiler (.vmdl -> .vmdl_c)

Goal (user, 2026-11-06): compile ModelDoc sources to `.vmdl_c` 1:1 with resourcecompiler (within
Valve's own run-to-run variance), with no Valve tool in the loop. `ModelDocCompiler`
(`src/Source2.Compiler/Models/`) is the entry point; it is an `IResourceCompiler` for `.vmdl`.
Everything not yet ported is refused with a `NotSupportedException` naming what is missing, never
approximated.

## How a .vmdl compiles (what is ported)

1. **The source.** KV3 text, `rootNode` a `RootNode` whose `children` are the node lists. Ported
   node classes: `RootNode`, `RenderMeshList`, `RenderMeshFile` (one, options `name`, `filename`,
   `import_scale`, a default `import_filter`). The root's `model_archetype` and
   `primary_associated_entity` go to RED2's searchable data.
2. **The mesh file** (`DmxModelMeshes`): ModelCompile_LoadAndMergeMeshes (rc 180344980) ->
   FUN_180d49a80 -> the DmeModel walk FUN_180d49d20 -> DmeMeshToCMesh (180d47b90). See the class
   comment for the rules; in short:
   - DMX `binary_seqids` 9 reads with the `binary` 9 layout (same element headers and attributes;
     ladder1's DMX parses to its last byte).
   - The mesh's base state named `bind` (FUN_1818be180, V_stricmp_fast); its `vertexFormat` fields
     with data, in that order (180d463b0); `name$k` -> stream `name`, index k (180d473d0).
   - One vertex per triangle corner (options +0x8a 0), triangles copied as listed (FUN_1818bde50).
   - Values through the DmeDag's matrix: position by Matrix3x4_TransformPoint times import_scale,
     directions rotated and renormalised (180d46590), `flipVCoordinates` flips v.
   - Face sets grouped by material + "%f" lightmap scale in a case-insensitive symbol table.
3. **The mesh compile** is the map compile's, already 1:1 on world node and entity models
   (`Maps/WorldNodeModels.Build` with `mapMesh: false`, `preserveTangents: false`):
   ModelCompile_WeldMesh (1803447d0) runs CMesh_ComputeTangents (UseMikkTSpace) on every mesh of
   a .vmdl, then the weld, the merge (180344ae0), the draw build (NodeDraw: weld, vertex cache,
   overdraw), the layout from the material's input signature, meshopt encoding.
4. **The container** (`WorldNodeModelAuthor.Container`): MVTX, MIDX, MDAT, CTRL, RERL, RED2,
   DATA, resource version 1. Differences from a node model: CTRL names the mesh after the
   RenderMeshFile; MDAT `m_bEmbeddedMapMesh` false; RED2 lists the .vmdl and each mesh file with
   their CRC32 (sorted by path), IsChildResource 0, and records the arguments the model compile
   reads as FloatArgs at their defaults (ModelCompile_ReadMeshArgs 1802f9cc0; meshlets_max_* -1.0f).

Materials: the layout needs each material's vertex input signature (its `.vmat_c` INSG).
`ModelDocSettings.CompiledMaterial` supplies the compiled material; the material compiler
(`Materials/MaterialCompiler`) writes those 1:1, so the whole chain can run without RC.

## Parity

References: `tools/pipeline/compile_model_ref.py s2c_model_probe <models> --runs 2` (Valve's
standalone resourcecompiler; both runs compared for Valve's own variance), corpus copied into the
probe addon by `tools/pipeline/model_probe_corpus.py` from dkr_remix. Test:
`ModelDocCompilerTests`.

| corpus | models | exact | notes |
|---|---|---|---|
| ladder1 (render only, one DMX, one material) | 1 | 1 | Valve's two runs identical |
| dkr_remix render-only (`model_corpus_render.txt`) | 111 | 95 | 9 refused; 6 differ by one tangent frame word (OPEN 1.c7), skybox_bottom by the fourth layout. Valve's runs identical on all 111 |

## Roadmap (by how many corpus models each unblocks)

dkr_remix + c2m2 node-class signatures (1,026 .vmdl): 630 render + PhysicsHullFile +
PhysicsBodyMarkup; 111 render only; 80 the same with prop_data GenericGameData; 55 with
DefaultMaterialGroup; 42 with MaterialGroup; then skeletons, animations, hitboxes, attachments,
break pieces, body groups, jiggle bones, IK, LODs, cloth, morphs.

1. Render-only leftovers the corpus shows (refusals counted by the corpus test): polygon faces
   (FUN_18136f810 after FUN_18125b510), DmeDag transforms off the identity (FUN_1818c0150,
   FUN_181258890), DmeDag children (FUN_180d49bb0), several face sets per material (CMesh_Append,
   FUN_1812d6c70), several RenderMeshFiles, import filters.
2. PhysicsShapeList / PhysicsHullFile / PhysicsBodyMarkup: PHYS block, DATA m_refPhysicsData,
   RED2 physics counts. The hull cook (RnHullCreate, QEM simplifier, region SVM) is ported for
   map physics (docs/PHYSICS.md); the ModelDoc hull node's options (faceMergeAngle,
   maxHullVertices, import_mode, optimization_algorithm) need their readers.
3. GenericGameData (prop_data and friends: m_keyValueText).
4. MaterialGroupList / DefaultMaterialGroup / MaterialGroup (m_materialGroups).
5. Skeleton, BoneMarkupList, skinned meshes (jointCount, blend weights and indices).
6. AnimationList / AnimFile (ANIM, AGRP, ASEQ blocks), hitboxes, attachments.
7. FBX sources (17 in dkr_remix): FBX SDK behaviour, after DMX is complete.
8. The rest: break pieces, body groups, LODs, jiggle bones, IK, cloth, morphs, mesh operation stacks.

## Open questions (ground truth not read yet; ledger 263)

- The fourth layout (1802f5680) for streams a non-map mesh's material leaves unread: an extra unbound
  vertex buffer (skybox_bottom). Refused.
- The draw-set match on the depth layouts and on texcoord precision / SNORM (1802f69f0, 181364720):
  refused where the corpus shows it matters (urban_trees_*, coaster_railing_01).
- One packed tangent frame word on six models: the shared recompute (OPEN 1.c7).
- The hull sample's references are compiled (`%TEMP%/model_refs/s2c_model_probe_hull`,
  `model_corpus_hull.txt`, 79 models) for the PhysicsHullFile step.

- Matrix3x4_TransformPoint's float order for a non-identity DmeDag (only the identity is accepted).
- What DmeMesh +0x100 counts (FUN_180cb1b50), which sets NoCollapse on merged face-set meshes.
