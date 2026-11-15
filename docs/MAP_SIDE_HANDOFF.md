# Handoff: the map side of the split (2026-11-15)

You are picking up the repo split after the resource library was finished. Read this, then docs/MAP_SPLIT.md
(the plan and what went where) and docs/INDEPENDENCE.md ("Checked against CS2, not against VRF").

## What the user decided

- **Two repos.**
  - **Source2ResourceCompiler** is this repo, with non-map resources only. It is box `repos/rc-standalone.git`.
    The user called it "s2-compiler" when they gave the go-ahead, so confirm the name before pushing. GitHub is
    `madheffer/Source2MapTools`, which the user renames to Source2ResourceCompiler themselves.
  - **cs2-map-compiler** is a new repo for the map compiler. The decompiler is a side part of it, so the existing
    cs2-map-decompiler repo folds in. It depends on the resource compiler as the pinned `Source2.Compiler`
    package from the s2c feed, the way vpkstuff does.
  - The map repo may rely on the resource compiler, never the other way round.
  - No submodules, no InternalsVisibleTo across repos, no Publicizer.
  - s2port is left alone for now; later it imports the package the same way.
- **No VRF in any project, tests included.** Own what we use. Copying VRF's files as they are drags in most of
  VRF, so port each reader onto our own container and KV3 stack, using VRF's code (MIT) as the source.
- **1:1 with CS2, not with VRF.** Being equal to VRF proves nothing: check every port against CS2 itself.
  - CS2 itself means Valve's bytes, tier0 or RC run as an oracle, a decompile, or the GPU.
  - The ground-truth ledger rule holds: every rule needs evidence (docs/GROUND_TRUTH.md).
- **Finish:** commit both repos and push both to the box. The existing box repo becomes the resource compiler,
  and cs2-map-compiler gets a new bare repo on the box. Push to GitHub only if the user asks.

## Where things are

- **Work:** branch `split/maps`, in the worktree `D:\_cursor projects\source2-compiler-wt-split`. It is local only;
  nothing is pushed.
- **Commits on top of be02a14, oldest first:**
  - 09fe202, e00f7ec: the decompile line's compiler-side work (old docs/SPLIT_HANDOFF.md step 1).
  - e206ca9: the library split into `src/Source2.Compiler` and `src/Source2.MapCompiler`.
  - d0e977d, 077c346: MAP_SPLIT plan updates.
  - d273809: the resource library without VRF.
  - 87b7fcc, 275ff4f, 39b7d36: the CS2 audit and the GPU-measured BC decode (ledger 310).
- **`research/map-authoring`** is checked out in the main checkout `D:\_cursor projects\source2-compiler`. Another
  session ("Map compiler mako/shaurma differences") works there. Since be02a14 it gained 2ed1982, 5777a0b,
  6779804, cdfc567 and e987bd8 (ledgers 307-309 and 311), and maybe more by now.
  - Don't touch its working tree.
  - Message that session before you change files it is editing.
  - Ledger numbers: 310 is taken on `split/maps`, 311 by the map session. Agree the next ones with it.
- **The resource library** (`src/Source2.Compiler`) has no VRF reference. The test
  `VrfRequirementsTests.ResourceLibrary_DoesNotDependOnVrf` keeps it that way.
- **Still on VRF:** the map compiler (`src/Source2.MapCompiler`, 38 files), `Source2.Compiler.Gpu` (11),
  `Source2.Compiler.Cli` (8) and the tests (119).
  - `src/Source2.MapCompiler/Legacy/` holds the VRF-based container layer the map code still calls:
    ResourceBuilder, AuthoredKv3, Kv3V5Writer, ValveLayout, MaterialAuthor, ResourceDecompiler, DirFileLoader,
    VrfRequirements and LegacyContainer.

## Steps, in order

1. **Rebase `split/maps` onto `research/map-authoring`.**
   - Git follows the moved files.
   - `EntityModelAuthor.Container` needs a hand merge: e987bd8 added an optional PHYS parameter, and this branch
     changed a line in the same function.
   - Rebuild, and rerun the test sets named in the commits.
2. **Move the map side off VRF.** For each item, check the result against CS2 and add a ledger entry.
   - **Legacy/ onto the resource library's own stack:**
     - VRF containers and blocks become `ResourceFile`, `CompiledResource` and `Kv3Binary`.
       `Source2ContainerAuthor` shows the pattern (d273809).
     - The RERL is sorted by `V_stricmp_fast`, as RC's writer 18016c0e0 sorts it, via `EditInfo.Red2Sorted`.
       Forgetting this broke ModelDoc's corpus once.
     - `Kv3V5Writer` becomes `Kv3BlockWriter`. On Valve's own maps the two are equal on every block, and on pak01
       the new one is better (Kv3WriterAbProbe).
   - **VRF readers that have no port yet,** ported onto our stack:
     - compiled shaders: `VfxProgramData` and `ShaderCollection`, including the uncommitted VCS 72 change (see
       Gotchas);
     - `PhysAggregateData` and the Rubikon shapes;
     - World, WorldNode, SmartProp, EntityLump, NavMeshFile and the VXVS block.
   - **Consumers:**
     - `Source2.Compiler.Gpu/GpuTextures` uses VRF's `Texture`. Switch it to `CompiledTexture`.
     - The CPU bake (`Lighting/Bake/BakeTextures.cs`, `RtxMaterials.cs`) moves to `CompiledTexture.DecodeTexels`
       (exact GPU floats; `srgb: true` for colour textures).
   - **`VfxEval` audit.** It feeds the map compile through material dynamic expressions. Check it against
     materialsystem2's expression interpreter over every pak01 material.
   - **CLI and tests** move off VRF too; they are most of the 119 test files.
3. **Delete** `third_party/`, `tools/vendor.cs`, `Source2CompilerVrfProject`, `VrfRequirements*` and Legacy/.
4. **Split the CLI and the tests** into resource and map projects.
   - compile-service runs `s2c compile-map` and references the compiler by path, so decide the map executable's
     name with the user.
5. **Release the resource package** (`tools/release.ps1`) with the newly public API.
   - vpkstuff consumes 0.1.153 plus its own vendored `Source2.Compiler.Vrf` adapter.
   - Check that nothing vpkstuff uses broke. Its uses are KeyValues, Anim, Textures, Materials, Shaders and VRF;
     it uses no map types.
6. **Make cs2-map-compiler.**
   - Take the map paths with their history (`git filter-repo`), plus cs2-map-decompiler's content.
   - Reference the resource compiler as the pinned package.
   - Move the map docs and the map ledger entries, keeping their numbers.
   - Decide where tools/re (the Ghidra scripts and ADDRESSES.md) lives; both repos use it, so ask the user.
7. **Strip the map code from this repo.** Release again and point compile-service at the map repo.
8. **Do a fresh-clone build of both**, then push both to the box.
   - After a reachability check, delete `research/vmap-decompile`, `split/decompiler-out` and the
     `source2-compiler-wt-decompile` worktree (old handoff step 6).
   - docs/SPLIT_HANDOFF.md in the main checkout is superseded by this file.

## Oracles and probes built this session

| tool | judges | run |
|---|---|---|
| `tools/re/kv3_tier0_oracle.py` + `Kv3TierZeroDump` | our KV3 reader, by tier0.dll's own LoadKV3/SaveKV3 loaded in-process (no game); `--binary` is bit for bit | `KV3T0=<out>|<exts>|<n>`, then `python tools/re/kv3_tier0_oracle.py <out> --binary` |
| `Kv3WriterAbProbe` | our KV3 writer and the legacy one against Valve's bytes | `KV3AB=<exts>|<limit>`, `KV3AB_VPK=<map vpk>` for a Valve-compiled map |
| `MaterialSignatureAbProbe` | material input signatures, ours against the old reader | `MATSIGAB=1` |
| `TextureDecodeGpuProbe` | our texture decode, every format and sRGB form, against the GPU | `TEXGPU=<per format>|<max side>` |
| `BcVendorDecodeProbe`, `BcSrgbDecodeProbe`, `BcD3D11DecodeProbe` | measure a GPU's BC and sRGB decode, check them on random blocks, compare D3D11 with Vulkan | `BCVENDOR=<out>|<n>`, `BCSRGB=<out>|<n>`, `D3D11BC=<n>`; pack with `tools/re/bc_gpu_tables.py <out> <vendor>` |

For tier0 functions with no export, ReVa runs headless: `tools/re/reva_serve.py` (memory: reva-headless). The
`KeyValues3` layout tier0's loader needs is in kv3_tier0_oracle.py.

## Audit items still open (docs/INDEPENDENCE.md)

- **Animation decode, skeleton, Vbib.** These feed export only. The CS2 oracle is animationsystem's segment
  decoders: reached through RTTI from CCompressedAnimQuaternion's vtable 1806a68a8 in build 1023, or with Frida
  or cubehost once CS2 is closed.
- **KV3 v2 and the legacy VKV3 container.** pak01 has none; use an older workshop corpus.
- **AMD BC tables.** These need an AMD GPU; run BcVendorDecodeProbe and BcSrgbDecodeProbe there.
- **The shader-side texture codecs** (YCoCg, hemi-octahedral normals, normal reconstruction), against CS2's shaders.

## Gotchas

- **CS2 is usually open.**
  - Run builds and tests at BelowNormal priority with `DOTNET_PROCESSOR_COUNT=4`.
  - Never use Frida, cubehost or cs2.exe while CS2 is open (VAC).
  - Kill every process you start.
- **NuGet audit:** restore fails with NU1903 (an ILCompiler advisory pulled in through VRF). Build with
  `-p:NuGetAudit=false` until VRF is gone.
- **The VCS 72 shader change is uncommitted.** The vendored VRF in the main checkout has VCS 72 support that is
  in none of `third_party/patches`. A fresh `tools/vendor.cs` lacks it, and every test that reads CS2's compiled
  shaders then fails ("versions 59 through 71"). Until the shader reader is ported, copy the main checkout's
  `third_party/ValveResourceFormat` working-tree diff, as this worktree did. Port that change with the reader.
- **Embedded resources with ".bin" in the name** need `WithCulture="false"`, or MSBuild moves them to a
  satellite assembly.
- **Commits:** author `madheffer <6407692+madheffer@users.noreply.github.com>`. Stage files by explicit path
  and read `git status` before every commit.
- **Heredocs:** scripts with backslashes go through a file, not a bash heredoc.
