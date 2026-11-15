using System.Text;
using Source2.Compiler;
using Source2.Compiler.Cli;

// s2c - drive the standalone Source 2 resource compiler from a shell.
//
// Nothing here shells out. Every subcommand is a direct call into
// Source2.Compiler, which is the whole point: these are compiles that would
// otherwise need Valve's resourcecompiler.exe and a Workshop Tools install.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

try
{
    return args[0] switch
    {
        "compile" => Commands.Compile(args[1..]),
        "decompile" => Commands.Decompile(args[1..]),
        "texture" => Commands.Texture(args[1..]),
        "sheet" => Commands.Sheet(args[1..]),
        "svg" => Commands.Svg(args[1..]),
        "sound" => Commands.Sound(args[1..]),
        "id" => Commands.ResourceId(args[1..]),
        "inspect" => Commands.Inspect(args[1..]),
        "compile-addon" => CompileAddonCommand.Run(args[1..]),
        "reauthor" => Commands.Reauthor(args[1..]),
        "selftest" => SelfTest.Run(args[1..]),
        // main's resource parity tools (merged 2026-11-05).
        "kv3-parity" => Kv3Parity.Run(args[1..]),
        "nmclip-parity" => NmClipParity.Run(args[1..]),
        "container-parity" => ContainerParity.Run(args[1..]),
        "reencode-parity" => ReencodeParity.Run(args[1..]),
        "kv3-types" => Kv3Types.Run(args[1..]),
        "red2-survey" => Red2Survey.Run(args[1..]),
        _ => Unknown(args[0]),
    };
}
catch (Exception ex)
{
    // S2C_DEBUG=1: the whole exception, stack included.
    Console.Error.WriteLine("error: " + (Environment.GetEnvironmentVariable("S2C_DEBUG") == "1" ? ex.ToString() : ex.Message));
    return 1;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"unknown command '{cmd}'");
    Usage();
    return 2;
}

static void Usage()
{
    Console.WriteLine("""
    s2c - a Source 2 resource compiler that does not use resourcecompiler.exe

      s2c compile   <in.vdata|.vsndevts|.vpcf|.vagrp> [-o <out_c>] [--ship-as <path>]
                    KV3 text source -> compiled container, authored from scratch.
      s2c compile   <in.js> [-o <out.vjs_c>] [--ship-as <path>] [--search-path <mod>]
                    cs_script source -> .vjs_c, as resourcecompiler writes it on a
                    user's install (no Panorama preprocessor). Under content/<mod>/
                    the path and mod come from the file's own location.

      s2c decompile <in.*_c> [-o <out.txt>] [--block DATA|RED2|CTRL|PHYS|MDAT]
                    Compiled resource -> KV3 text. Defaults to the DATA block;
                    --block reads any other KV3-backed block, which is where a
                    model keeps its collision and every resource its identity.

      s2c texture   <image.png|jpg|...> -o <out.vtex_c> [--template <any.vtex_c>]
                    [--format bc7|bc5|bc4|bc3|bc1|rgba] [--no-mips] [--max-dim N]
                    Image -> compiled texture: mip chain, block compression and
                    the full stock extradata set the CS2 streamer expects.
                    Needs no template; pass one only to carry its encoding
                    semantics (a packed normal map's mip algorithm, say).

      s2c sheet     <in.mks> [-o <out.vtex_c>] [--format ...] [--ship-as <path>]
                    Sprite-sheet script -> animated texture: packs the frames
                    into an atlas and writes the SHEET block a particle system
                    animates through. Frame images resolve next to the script.

      s2c svg       <in.svg> -o <out.vsvg_c> [--template <any.vsvg_c>]
                    Raw SVG -> compiled Panorama vector graphic. Needs no
                    template.

      s2c sound     <in.wav> -o <out.vsnd_c> [--template <any.vsnd_c>]
                    Uncompressed PCM WAV -> compiled sound container. Needs no
                    template.

      s2c compile-addon <addon> [--cs2 <dir>] [--only <content path>]... [--bc7 amd|intel] [-n]
                    The addon's own .vmat and .vtex sources -> game/csgo_addons/<addon>,
                    with their child textures, as resourcecompiler writes them when run
                    on each asset. A map compile never compiles the materials it uses
                    (GROUND_TRUTH 255): run this before compile-map. Files whose bytes
                    are already right are left alone; -n compiles and writes nothing.

      s2c id        <resource/path.vtex>
                    Print the 64-bit RERL id the engine looks that path up by.

      s2c inspect   <in.*_c>
                    Container facts: resource version, blocks, RED2 compiler
                    identity + input dependencies, RERL entries and their ids.

      s2c selftest  [--cs2 <CS2 install dir>]
                    Compile one of every supported type and report PASS/timing.
                    Every row runs with no game install; --cs2 (or $CS2_DIR)
                    additionally enables the rows that read stock files to
                    compare against, such as the RERL id check.

    Maps (compile-map, map-physics, build-cubemaps, vis, nav and the decompiler)
    are cs2-map-compiler's, a repository of its own built on this one's package.

    Templates: optional everywhere. Every type here is authored outright, so a
    donor file is only ever a way to carry a detail this compiler does not infer
    (a packed normal map's mip algorithm, say). When one is given it supplies the
    header frame only - never the metadata, which is authored fresh either way. See the README, and docs/RESOURCES.md for the measured
    differences against resourcecompiler.exe.
    """);
}
