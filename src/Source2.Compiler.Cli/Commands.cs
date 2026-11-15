using Source2.Compiler.KeyValues;
using System.Text;

namespace Source2.Compiler.Cli;

/// <summary>
/// One method per subcommand. Each is a thin argument-parsing shell over a
/// single call into <c>Source2.Compiler</c>, so what the CLI can do is exactly
/// what the library can do.
/// </summary>
internal static class Commands
{
    public static int Compile(string[] a)
    {
        var input = Positional(a, "input file");
        var ext = Path.GetExtension(input);
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
            return CompileScript(a, input);
        var outPath = Opt(a, "-o") ?? input + "_c";

        // RED2 records where the source lived, content-relative. Use --ship-as
        // when you know the real path; otherwise put the file under its type's
        // canonical folder, which is what resourcecompiler would have seen.
        var shipAs = Opt(a, "--ship-as") ?? Source2ContainerAuthor.SuggestSourcePath(ext, Path.GetFileName(input));

        var bytes = Kv3SourceCompiler.Compile(File.ReadAllBytes(input), ext, shipAs);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes)");
        return 0;
    }

    // A cs_script source: RED2 records its path inside its mod tree and the tree, which for a file under
    // content/<mod>/... (content/csgo_addons/<addon>/...) are read from the path itself; --ship-as and
    // --search-path set them for a file kept anywhere else.
    private static int CompileScript(string[] a, string input)
    {
        var full = Path.GetFullPath(input).Replace('\\', '/');
        var at = full.LastIndexOf("/content/", StringComparison.OrdinalIgnoreCase);
        string? searchPath = null, relative = null;
        if (at >= 0)
        {
            var parts = full[(at + "/content/".Length)..].Split('/');
            var depth = parts[0].Equals("csgo_addons", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 ? 2 : 1;
            searchPath = string.Join('/', parts[..depth]);
            relative = string.Join('/', parts[depth..]);
        }
        searchPath = Opt(a, "--search-path") ?? searchPath ?? "csgo";
        relative = Opt(a, "--ship-as") ?? relative ?? "scripts/" + Path.GetFileName(input);
        var outPath = Opt(a, "-o") ?? Path.ChangeExtension(input, ".vjs_c");
        var bytes = ScriptAuthor.Compile(relative, searchPath, File.ReadAllBytes(input));
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes, {searchPath}:{relative})");
        return 0;
    }

    public static int Decompile(string[] a)
    {
        var input = Positional(a, "input file");
        var res = CompiledResource.Read(File.ReadAllBytes(input));
        var block = (Opt(a, "--block") ?? "DATA").ToUpperInvariant();
        var text = ResourceText.BlockToKv3(res, block, out var why);
        if (text is null)
        {
            Console.Error.WriteLine("cannot decompile: " + why);
            return 1;
        }

        var outPath = Opt(a, "-o");
        if (outPath is null)
            Console.Out.Write(text);
        else { File.WriteAllText(outPath, text); Console.WriteLine($"{outPath}  ({text.Length:n0} chars)"); }
        return 0;
    }

    public static int Texture(string[] a)
    {
        var input = Positional(a, "image file");
        var outPath = Require(Opt(a, "-o"), "-o <out.vtex_c>");
        // --template is optional: without one the container is authored outright.
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;

        var def = new PackageResourceBuilder.TextureDef
        {
            ImageBytes = File.ReadAllBytes(input),
            GenerateMipmaps = !a.Contains("--no-mips"),
            Compression = ParseFormat(Opt(a, "--format")),
            SourceName = Opt(a, "--ship-as"),
        };
        if (Opt(a, "--max-dim") is { } md)
            def.MaxDimension = int.Parse(md);

        var bytes = PackageResourceBuilder.BuildTexture(template, def);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes, {def.Compression}, "
                        + $"encoder {PackageResourceBuilder.Bc7CacheTag(def.Bc7Mode)})");
        return 0;
    }

    /// <summary>
    /// Compile an <c>.mks</c> sprite-sheet script into an animated
    /// <c>.vtex_c</c>: pack its frame images into an atlas, write the SHEET
    /// block describing the sequences, and encode the atlas.
    /// </summary>
    public static int Sheet(string[] a)
    {
        var input = Positional(a, "mks script");
        var outPath = Opt(a, "-o") ?? Path.ChangeExtension(input, ".vtex_c");
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;
        var root = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";

        var script = MksSource.Parse(File.ReadAllBytes(input));

        // Frame images resolve relative to the script, the way mksheet reads them.
        using var packed = SheetAtlas.Pack(script, name =>
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Frame image not found next to the script: {name}");
            return File.ReadAllBytes(path);
        });

        var atlas = packed.Atlas;
        var rgba = new byte[atlas.Width * atlas.Height * 4];
        atlas.GetPixelSpan()[..rgba.Length].CopyTo(rgba);

        var def = new PackageResourceBuilder.TextureDef
        {
            RawRgba = rgba,
            RawWidth = atlas.Width,
            RawHeight = atlas.Height,
            // A mip chain would blend neighbouring frames of the atlas into each
            // other, which is why stock sheets ship NO_LOD with a single level.
            GenerateMipmaps = false,
            Flags = PackageResourceBuilder.VTexFlags.NO_LOD,
            Compression = ParseFormat(Opt(a, "--format")),
            SourceName = Opt(a, "--ship-as") ?? "materials/vpkeditor/" + Path.GetFileName(input),
            SheetData = SpriteSheet.Write(packed.Sequences),
        };

        var bytes = PackageResourceBuilder.BuildTexture(template, def);
        File.WriteAllBytes(outPath, bytes);

        var frames = script.Sequences.Sum(s => s.Frames.Count);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes, {atlas.Width}x{atlas.Height} atlas, "
                        + $"{script.Sequences.Count} sequence(s), {frames} frame(s), {def.Compression})");
        return 0;
    }

    public static int Svg(string[] a)
    {
        var input = Positional(a, "svg file");
        var outPath = Require(Opt(a, "-o"), "-o <out.vsvg_c>");
        // --template is optional: without one the container is authored outright.
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;

        var bytes = PackageResourceBuilder.BuildPanoramaSvg(template, File.ReadAllBytes(input), Opt(a, "--ship-as"));
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes)");
        return 0;
    }

    public static int Sound(string[] a)
    {
        var input = Positional(a, "wav file");
        var outPath = Require(Opt(a, "-o"), "-o <out.vsnd_c>");
        // --template is optional: without one the container is authored outright.
        var template = Opt(a, "--template") is { } t ? File.ReadAllBytes(t) : null;

        var bytes = PackageResourceBuilder.BuildSound(template, File.ReadAllBytes(input), Opt(a, "--ship-as"));
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{outPath}  ({bytes.Length:n0} bytes)");
        return 0;
    }

    public static int ResourceId(string[] a)
    {
        var path = Positional(a, "resource path");
        Console.WriteLine($"{Source2ResourceId.ForPath(path):x16}  {path}");
        return 0;
    }

    /// <summary>
    /// Re-author one of Valve's compiled map resources through OUR writer and say
    /// how the result compares. This is the unit the staged pipeline swaps: a file
    /// that comes back byte-identical is proven, and one that differs is the
    /// candidate worth putting in front of the game.
    /// </summary>
    public static int Reauthor(string[] a)
    {
        var input = Positional(a, "compiled map resource");
        var outPath = Opt(a, "-o");

        var original = File.ReadAllBytes(input);
        var name = Path.GetFileName(input);
        var valve = CompiledResource.Read(original);

        // "world.vwrld_c" is compiled from a ".vwrld", which is what the author
        // needs to stamp the right identity on it.
        var compiled = Path.GetExtension(name);
        var sourceExtension = compiled.EndsWith("_c", StringComparison.Ordinal)
            ? compiled[..^2]
            : compiled;

        byte[] ours;
        if (sourceExtension is ".vrman")
        {
            ours = ResourceManifestAuthor.Author(ResourceManifestAuthor.ReadData(valve.Block("DATA")!.Data));
        }
        else if (valve.Kv3("DATA") is { } tree && valve.Kv3Format("DATA") is { } format)
        {
            ours = Source2ContainerAuthor.AuthorKv3Tree(tree, format, sourceExtension);
        }
        else
        {
            Console.Error.WriteLine($"cannot re-author {sourceExtension}: its DATA block is not a KV3 tree. "
                                  + "A map root needs its child list, so use the pipeline for .vmap_c.");
            return 2;
        }

        // Bytes are the wrong verdict on their own. KV3 binary has several valid
        // encodings of one tree - string table order, compression choice - so our
        // writer reproducing the TREE while differing in bytes is a pass, and it
        // is what the authoring tests assert. Say which of the two happened.
        var identical = ours.AsSpan().SequenceEqual(original);
        var mine = CompiledResource.Read(ours);

        // A manifest's DATA is a bare string list rather than KV3, so fall back to
        // comparing that block's bytes instead of calling it "not compared".
        var theirTree = ResourceText.DataBlockToKv3(valve, out _);
        var ourTree = ResourceText.DataBlockToKv3(mine, out _);
        var sameTree = theirTree is not null
            ? theirTree == ourTree
            : DataBytes(original, valve).Span.SequenceEqual(DataBytes(ours, mine).Span);
        var theirRefs = References(valve);
        var ourRefs = References(mine);

        Console.WriteLine($"{name}");
        Console.WriteLine($"  valve {original.Length,10:n0} bytes   ours {ours.Length,10:n0} bytes");
        Console.WriteLine($"  bytes      {(identical ? "IDENTICAL" : "differ: " + BlockDelta(name + "_c", original, ours))}");
        Console.WriteLine($"  DATA       {(sameTree ? "IDENTICAL" : "DIFFERS")}"
                        + (theirTree is null ? " (raw bytes; this type's DATA is not KV3)" : " (decoded tree)"));
        Console.WriteLine($"  references {(theirRefs.SetEquals(ourRefs) ? $"IDENTICAL ({theirRefs.Count})" : $"DIFFER ({theirRefs.Count} vs {ourRefs.Count})")}");
        Console.WriteLine($"  resver     {(valve.Version == mine.Version ? "same" : $"{valve.Version} vs {mine.Version}")}");

        var usable = sameTree && theirRefs.SetEquals(ourRefs) && valve.Version == mine.Version;
        Console.WriteLine($"  VERDICT    {(identical ? "byte-exact" : usable ? "equivalent, worth an in-game test" : "NOT EQUIVALENT")}");

        if (outPath is not null)
        {
            File.WriteAllBytes(outPath, ours);
            Console.WriteLine($"  wrote {outPath}");
        }
        return usable ? 0 : 1;
    }

    /// <summary>A container's DATA block payload.</summary>
    private static ReadOnlyMemory<byte> DataBytes(byte[] bytes, CompiledResource resource)
        => resource.Block("DATA") is { } block ? block.Data : default;

    /// <summary>Every resource id a container's RERL names.</summary>
    private static HashSet<ulong> References(CompiledResource resource)
        => resource.Block("RERL") is { } rerl ? [.. ResourceRefs.Read(rerl.Data).Select(r => r.Id)] : [];

    /// <summary>
    /// Which blocks of a compiled resource actually differ. A resource whose only
    /// difference is RED2 is the same asset compiled somewhere else: that block
    /// carries compile identity, and its m_nFingerprint changes with the install
    /// root alone.
    /// </summary>
    private static string BlockDelta(string path, byte[] one, byte[] two)
    {
        if (!path.EndsWith("_c", StringComparison.Ordinal))
            return "not a compiled resource";
        try
        {
            var a = ResourceFile.Read(one);
            var b = ResourceFile.Read(two);

            // Every block, paired with the same occurrence of its type in the other
            // (a model with two draw sets has two MVTX and two MIDX).
            var names = a.Blocks.Select(x => x.Type).Union(b.Blocks.Select(x => x.Type)).ToList();
            var parts = new List<string>();
            foreach (var name in names)
            {
                var ofA = a.Blocks.Where(x => x.Type == name).ToList();
                var ofB = b.Blocks.Where(x => x.Type == name).ToList();
                for (var k = 0; k < Math.Max(ofA.Count, ofB.Count); k++)
                {
                    var label = Math.Max(ofA.Count, ofB.Count) > 1 ? $"{name}{k}" : $"{name}";
                    if (k >= ofA.Count || k >= ofB.Count)
                    {
                        parts.Add($"{label}:only-in-{(k >= ofA.Count ? "candidate" : "reference")}");
                        continue;
                    }
                    ReadOnlyMemory<byte> first = ofA[k].Data;
                    ReadOnlyMemory<byte> second = ofB[k].Data;
                    if (!first.Span.SequenceEqual(second.Span))
                        parts.Add($"{label}:differs({first.Length:n0}->{second.Length:n0})");
                }
            }
            return parts.Count == 0 ? "blocks identical, container framing differs" : string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return "unreadable: " + ex.Message[..Math.Min(60, ex.Message.Length)];
        }
    }

    public static int Inspect(string[] a)
    {
        var input = Positional(a, "compiled resource");
        var res = CompiledResource.Read(File.ReadAllBytes(input));

        Console.WriteLine($"resver         {res.Version}");
        Console.WriteLine($"blocks         {string.Join(' ', res.File.Blocks.Select(b => b.Type))}");

        if (res.Kv3("RED2") is { } edit)
        {
            Console.WriteLine("RED2 special dependencies");
            foreach (var d in edit.GetArray("m_SpecialDependencies") ?? [])
                Console.WriteLine($"  {d.GetStringProperty("m_String"),-38} {d.GetStringProperty("m_CompilerIdentifier"),-28} fp={d.GetUInt32Property("m_nFingerprint")} user={d.GetUInt32Property("m_nUserData")}");
            Console.WriteLine("RED2 input dependencies");
            foreach (var d in edit.GetArray("m_InputDependencies") ?? [])
                Console.WriteLine($"  {d.GetStringProperty("m_RelativeFilename"),-58} crc={d.GetUInt32Property("m_nFileCRC")}");
        }

        var rerl = res.Block("RERL") is { } r0 ? ResourceRefs.Read(r0.Data) : [];
        Console.WriteLine($"RERL           {rerl.Count} entries");
        foreach (var r in rerl)
        {
            var agrees = Source2ResourceId.ForPath(r.Name) == r.Id ? "ok" : "MISMATCH";
            Console.WriteLine($"  {r.Id:x16} {agrees,-8} {r.Name}");
        }
        // --data: the DATA block as text; --block <type>: that block (PHYS, CTRL, ...).
        if (a.Contains("--data"))
            Console.WriteLine(ResourceText.DataBlockToKv3(res, out var why) ?? why);
        if (Opt(a, "--block") is { } blockType)
            Console.WriteLine(ResourceText.BlockToKv3(res, blockType, out var why2) ?? why2);
        return 0;
    }

    // argument plumbing

    private static PackageResourceBuilder.TextureCompression ParseFormat(string? f) => (f ?? "bc7").ToLowerInvariant() switch
    {
        "bc7" => PackageResourceBuilder.TextureCompression.BC7,
        "bc5" => PackageResourceBuilder.TextureCompression.BC5,
        "bc4" => PackageResourceBuilder.TextureCompression.BC4,
        "bc3" or "dxt5" => PackageResourceBuilder.TextureCompression.BC3,
        "bc1" or "dxt1" => PackageResourceBuilder.TextureCompression.BC1,
        "rgba" or "none" => PackageResourceBuilder.TextureCompression.None,
        _ => throw new ArgumentException($"unknown --format '{f}' (bc7|bc5|bc4|bc3|bc1|rgba)"),
    };

    private static string Positional(string[] a, string what)
        => a.FirstOrDefault(x => !x.StartsWith('-'))
           ?? throw new ArgumentException($"missing {what}");

    private static string? Opt(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static string Require(string? v, string what)
        => v ?? throw new ArgumentException($"missing {what}");
}
