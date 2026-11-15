# Map split: resource compiler and map tools as two repos (plan, 2026-11-15)

Agreed with the user on 2026-11-15:

| Repo | Contents | Depends on |
|---|---|---|
| **Source2ResourceCompiler** (this repo; GitHub `madheffer/Source2MapTools`, renamed by the user) | Every non-map resource compiler: textures, materials, models (ModelDoc), KV3 documents, sounds, SVG, anims, particles, scripts, surface properties, and the readers/writers they share | Nothing of ours |
| **Map tools** (map compiler + map decompiler, one repo) | `.vmap` compile (world nodes, vis, physics world, lighting, cubemaps, Steam Audio, nav, entity lump) and the decompiler | The resource compiler, as the `Source2.Compiler` package from the s2c feed, pinned |
| s2port | Unchanged for now | Later the same package, like vpkstuff |

The resource compiler never references map code. No submodules, no `InternalsVisibleTo`, no Publicizer. vpkstuff
keeps importing `Source2.Compiler`; what it uses (KeyValues, Anim, Textures, Materials, Shaders, Vrf,
`FgdSchema`) stays in the package. `FgdSchema` lives in `Maps/` today, so it moves to the resource side.

## What went where (step 2, done on `split/maps`)

`src/Source2.MapCompiler` (assembly `Source2.MapCompiler`, 142k lines) references `src/Source2.Compiler`
(44k lines), which references nothing map. Files moved with `git mv`; namespaces are unchanged, so the
public API of the `Source2.Compiler` package keeps every type it had that stays on the resource side.

- **Map project:** all of `Maps/`, `Lighting/`, `Cubemaps/`, `SteamAudio/`, `Simulation/` (the vphysics
  settle) and `Physics/` (nothing on the resource side builds physics: ModelDoc's models are static), plus
  `Texture/Rc/Bc6hHueShift.cs` (lightmaps only) and the world containers (`WorldNodeSceneTrees`,
  `WorldPhysicsAuthor`, `VisibilityAuthor`, `WorldTrees`, `EntityModelAuthor`). Its embedded resources
  (`estimate_zen4_*.bin`, `cubemap_engine_materials.txt`) keep their logical names.
- **Back to the resource side, as the model builder ModelDoc shares with world nodes** (`Models/Build/`,
  namespace still `Source2.Compiler.Maps`): `WorldNodeModels`, `WorldNodeModel`, `NodeDraw`, `MeshTangents`,
  `TangentFrame`, `UvDensity`, `WrbMeshEntry`, `MaterialStreams`, `MapNodeAngles` (the DMX reader applies it),
  `VectorNormalizeSlow`, `ResourcePath`, `MeshWeld`; `Containers/WorldNodeModelAuthor`,
  `WorldNodeModelTrees`; both `MsvcSort`s (`Simulation/`).
- **Seams cut:**
  - `WorldNodeCompile.Entry` is now the top-level `WorldNodeEntry` (`WorldNodeCompile.cs` aliases `Entry`
    to it, so its body is unchanged).
  - `WorldNodeModels.BuildPropAggregate` takes the draws (`PropAggregateDraw`) and texcoord formats instead
    of the map's `NodePropAggregates.Aggregate`; `Aggregate.ModelDraws` converts.
  - `MeshVectors` (new, resource side) holds the bodies of `NodeMeshEntries.Rotate/Copy/Normalise`,
    `MapMeshes.Transform`, `TJunctionFix.Normalise`, `SettleWorld.NameHash/SetAngles`, moved unchanged; the map
    methods forward to it, so both sides run the one copy.
  - `Source2ContainerAuthor.MapRoot.cs` (a partial, which can't span assemblies) is now `MapRootAuthor` in the
    map project.
- **Made public for the map project** (no behaviour change): `Lz4HcStream`, `AuthoredKv3` + `Block`,
  `Source2ContainerAuthor.SetResourceVersion` / `InputDependency`, `DmxBinary.Element`'s constructor,
  `MeshWeld`, `NodeDraw`, `WorldNodeModels` (+ `ToHalf`), `TangentFrame.Basis`,
  `WrbMeshEntry.TexcoordsPastSixteen`, `VectorNormalizeSlow`, `Maps.MsvcSort`, and the members the build named
  (`git diff` on `split/maps` lists each).
- `Source2.Compiler.Gpu`, `Source2.Compiler.Cli` and the tests reference both projects for now.

Found on the way: `third_party/patches` holds 14 VRF patches, but the main checkout's VRF also carries
uncommitted VCS 72 shader support (`CompiledShader/*`). A fresh clone's vendor step does not get it, and
`EntityLumpAgainstValveTests` (and every test reading CS2's compiled shaders) fails there with
"Only VCS file versions 59 through 71". It has to become a patch before either repo builds standalone.

## Steps

1. **Finish the old handoff's combine** (docs/SPLIT_HANDOFF.md step 1): the compiler-side work of the
   decompile line (f583b6a, e1bccd3: DMX writer, `EntityLumpAuthor.Inverse`, `MapVariables`, ledger
   296-302) onto `research/map-authoring`, so nothing is lost before files move.
2. **Split in place, in this repo**, on a branch (done for the library, see above). Left: map CLI commands to a
   `Source2.MapCompiler.Cli`, `Source2.Compiler.Gpu` with them, tests split into `Source2.Compiler.Tests` and
   `Source2.MapCompiler.Tests`. Build and test: same results as before the move.
   Before it lands: rebase `split/maps` onto `research/map-authoring`, which gained 2ed1982 (ledger 307) and
   5777a0b (ledger 308) from the map session after be02a14; neither touches the seams above.
3. **Release the resource package** (`tools/release.ps1`) with the newly public API, so the map repo can pin it.
4. **Make the map repo.** The existing `cs2-map-decompiler` repo becomes it (name to settle), with history for
   the moved files (`git filter-repo --path ...`). It drops its `compiler` submodule and Publicizer, and
   references the resource compiler as the pinned package. Map docs (vis, lighting, nav, cubemaps, world nodes)
   and their ledger entries move with it; the ledger numbers stay as they are, so cross-references stay valid.
5. **Remove the map code from this repo**, release again, and point compile-service (which references the
   compiler by path and compiles maps) at the map repo.
6. Fresh-clone build of both repos; delete the old branches (`research/vmap-decompile`,
   `split/decompiler-out`) and the wt-decompile worktree once everything is reachable.

## Still to settle

- The map repo's name, and whether it is public on GitHub.
- Where the shared RE tooling goes (`tools/re`, `tools/ghidra_scripts`, `docs/ADDRESSES.md`): both repos use
  the Ghidra scripts and addresses.
