# The map decompiler: moved to cs2-map-decompiler

The map decompiler (a compiled CS2 map package back to its Hammer `.vmap`) left this repository on
2026-11-15. It is its own repository now, **cs2-map-decompiler** (`dev@192.168.20.32:repos/cs2-map-decompiler.git`,
locally `D:\_cursor projects\cs2-map-decompiler`), which builds against this one as its `compiler/` submodule.
Its docs/DECOMPILE.md is the old one, continued; links here to "docs/DECOMPILE.md" mean that file.

Where things went:

| was here | now |
|---|---|
| `src/Source2.Compiler/Maps/Decompile/` | cs2-map-decompiler `src/Cs2MapDecompiler/` (same namespace) |
| `src/Source2.Compiler.Solvers/` (CP-SAT world order) | `src/Cs2MapDecompiler.Solvers/` |
| `s2c decompile-map`, `decompile-scripts`, `decompile-sweep`, `vmap-check`, `vmap-world-compare`, `world-charts` | `cs2-map-decompiler <same command>` (`cli/Cs2MapDecompiler.Cli/`) |
| tests MapDecompilerTests, BlendFacesTests, CornerForTests, TemplatePlacementTests, MapDecompilerFrameProbe, MapDecompilerHullProbe | `tests/Cs2MapDecompiler.Tests/` |
| EntityRenderProbe.RenderFilterOnTheSource, WorldPhysicsAuthorTests' WPBUILD_FLATTEN step | there as EntityRenderFilterProbe, WorldPhysicsFlattenProbe (on its own copy of WorldPhysicsAuthorTests) |
| `tools/map-survey/decompile_*`, `vmap_compare.py`, `vmap_schema.py`, `world_order_cpsat.py` | its `tools/` |
| branch `research/vmap-decompile` | its history (`docs/commit-map.txt` maps the old commit ids) |

What stays here, because the compiler owns it: `EntityLumpAuthor.Inverse*.cs` (the lump author run backwards),
`Dmx/DmxBinaryWriter.cs`, `Maps/MapVariables.cs`, every forward fix the decompiler's round trips found, the
debug switches (WORLDPHYS_PIECE_TRIS, WPBUILD_* and DECOMPILE_SOUPS in WorldPhysicsAuthorTests, which compare any
two maps), the probe map generators in `tools/map-survey/`, `hammer_autosave.py`, cubehost, and the ground truth
ledger.

The two repositories do not rely on each other. Nothing here names or serves the decompiler: no
InternalsVisibleTo for it, no hook for its tests, no code of it. The decompiler is a consumer of this
library only: it pins a commit of research/map-authoring as its `compiler/` submodule, opts into the
internals it uses by itself (Krafs.Publicizer), and carries its own copies of the test fixtures it needs
(its tools/split/copy_compiler_fixtures.py). A change here that renames or reshapes an internal it uses
shows up in its build when it next moves the pin, not here.

The ledger stays one numbered list, here (docs/GROUND_TRUTH.md); cs2-map-decompiler keeps copies of its entries.
At the merge, the decompile line's entries that shared a number with this line's moved past 295: 182 -> 296,
187 -> 297, 276 -> 298, 277 -> 299, 278 -> 300, 291 -> 301, and the decompiler's new 292 -> 302 (each entry notes its
old number). The next free number is 306 (303-305 are the compiler line's since the split).
