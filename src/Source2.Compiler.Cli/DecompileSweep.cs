using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValveKeyValue;
using ValvePak;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>decompile-sweep &lt;workshop content dir&gt; --report &lt;out.jsonl&gt; [--cs2 dir] [--only id,...]
/// [--timeout seconds]</c>: runs the map decompiler on every map of every workshop item
/// (steamapps/workshop/content/730/&lt;id&gt;) and writes one JSON line per map: whether it ran,
/// how long it took, what it rebuilt and its notes grouped. These maps have no Hammer source, so
/// this measures only that the decompile runs and what it leaves out (docs/DECOMPILE.md item 19).
///
/// A workshop item's map sits nested in it as maps/&lt;map&gt;.vpk, with the map's custom materials
/// in the outer package: the material hash table takes the game's names plus the item's own.
///
/// Resumable: maps already in the report are skipped. Before each map the key is written to
/// &lt;report&gt;.current; a map that runs past the timeout is written as "timeout" and the process
/// exits with 3 (a hung thread cannot be stopped), so a driver restarts it
/// (tools/map-survey/decompile_sweep.py, which also records a hard crash from .current).
/// </summary>
internal static class DecompileSweep
{
    public static int Run(string[] a)
    {
        var root = a.FirstOrDefault(x => !x.StartsWith('-')) ?? throw new ArgumentException("missing the workshop content dir");
        var report = Opt(a, "--report") ?? throw new ArgumentException("missing --report <out.jsonl>");
        var cs2 = Opt(a, "--cs2") ?? Environment.GetEnvironmentVariable("CS2_DIR") ?? throw new ArgumentException("--cs2 <CS2 dir> (or CS2_DIR)");
        var timeout = TimeSpan.FromSeconds(double.Parse(Opt(a, "--timeout") ?? "900"));
        var only = Opt(a, "--only")?.Split(',').ToHashSet();
        var game = Path.Combine(cs2, "game");

        var done = new HashSet<string>();
        if (File.Exists(report))
            foreach (var line in File.ReadLines(report))
                if (line.Length > 0)
                    done.Add(JsonDocument.Parse(line).RootElement.GetProperty("key").GetString()!);

        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        var gameNames = Maps.Decompile.BrushGeometry.GameMaterialNames(game);
        var gameContent = new Maps.GameContent(Path.Combine(game, "csgo", "pak01_dir.vpk"));
        var current = report + ".current";

        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var item = Path.GetFileName(dir);
            if (only is not null && !only.Contains(item))
                continue;
            if (OuterPackage(dir) is not { } outer)
            {
                Write(report, done, new() { ["key"] = item, ["item"] = item, ["status"] = "nopackage" });
                continue;
            }
            List<(string Map, Func<Dictionary<string, byte[]>> Files)> maps;
            List<string> addonMaterials;
            try
            {
                (maps, addonMaterials) = MapsOf(outer);
            }
            catch (Exception e)
            {
                Write(report, done, new() { ["key"] = item, ["item"] = item, ["status"] = "badpackage", ["error"] = $"{e.GetType().Name}: {e.Message}" });
                continue;
            }
            if (maps.Count == 0)
            {
                Write(report, done, new() { ["key"] = item, ["item"] = item, ["status"] = "nomap" });
                continue;
            }
            var materials = Maps.Decompile.BrushGeometry.MaterialTable(gameNames.Concat(addonMaterials));
            foreach (var (mapEntry, read) in maps)
            {
                var key = $"{item}/{mapEntry}";
                if (done.Contains(key))
                    continue;
                File.WriteAllText(current, key);
                var row = new Dictionary<string, object?> { ["key"] = key, ["item"] = item, ["entry"] = mapEntry, ["addonMaterials"] = addonMaterials.Count };
                var clock = Stopwatch.StartNew();
                var task = Task.Run(() =>
                {
                    var files = read();
                    row["files"] = files.Count;
                    row["packageBytes"] = files.Values.Sum(f => (long)f.Length);
                    var map = Maps.Decompile.MapDecompiler.MapNameOf(files);
                    row["map"] = map;
                    row["lumpFormat"] = LumpFormat(files);
                    row["compileTimestamp"] = CompileTimestamp(files, map);
                    return Maps.Decompile.MapDecompiler.Decompile(files, map, schema, materials, gameContent);
                });
                try
                {
                    if (!task.Wait(timeout))
                    {
                        row["status"] = "timeout";
                        row["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                        Write(report, done, row);
                        Console.WriteLine($"{key}: timeout after {timeout.TotalSeconds} s");
                        File.Delete(current);
                        Environment.Exit(3);
                    }
                    var result = task.Result;
                    row["status"] = "ok";
                    row["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    row["entities"] = result.Entities.Count;
                    row["placeholders"] = result.Placeholders;
                    row["prefabs"] = result.Prefabs.Count;
                    row["vmapBytes"] = result.Vmap.Length;
                    row["notes"] = result.Notes.Count;
                    row["noteGroups"] = result.Notes
                        .GroupBy(NoteKind)
                        .OrderByDescending(g => g.Count())
                        .Select(g => new Dictionary<string, object> { ["kind"] = g.Key, ["count"] = g.Count(), ["example"] = g.First() })
                        .ToList();
                }
                catch (AggregateException ae) when (ae.InnerException is { } e)
                {
                    row["status"] = "error";
                    row["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    row["error"] = $"{e.GetType().Name}: {e.Message}";
                    row["at"] = e.StackTrace?.Split('\n').Take(6).Select(s => s.Trim()).ToList();
                }
                Write(report, done, row);
                Console.WriteLine($"{key}: {row["status"]} {row["seconds"]} s");
            }
        }
        File.Delete(current);
        return 0;
    }

    /// <summary>Which id key the map's lumps carry: "compile_source_id" (today's resourcecompiler),
    /// "hammerUniqueId" (an older one, which names Hammer's node ids instead), both, or none.</summary>
    private static string LumpFormat(IReadOnlyDictionary<string, byte[]> files)
    {
        bool source = false, hammer = false;
        foreach (var (path, bytes) in files)
        {
            if (!path.EndsWith(".vents_c", StringComparison.OrdinalIgnoreCase))
                continue;
            using var resource = new ValveResourceFormat.Resource { FileName = Path.GetFileName(path) };
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not ValveResourceFormat.ResourceTypes.EntityLump lump)
                continue;
            foreach (var entity in lump.Data.GetArray("m_entityKeyValues"))
                if (entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values") is { } values)
                {
                    source |= values.ContainsKey("compile_source_id");
                    hammer |= values.ContainsKey("hammerUniqueId");
                }
        }
        return source && hammer ? "both" : source ? "compile_source_id" : hammer ? "hammerUniqueId" : "none";
    }

    /// <summary>The world's m_nCompileTimestamp (unix seconds), when the package has a world.vwrld_c.</summary>
    private static long? CompileTimestamp(IReadOnlyDictionary<string, byte[]> files, string map)
    {
        var world = files.Keys.FirstOrDefault(k => k.EndsWith(".vwrld_c", StringComparison.OrdinalIgnoreCase));
        if (world is null)
            return null;
        var bytes = files[world];
        using var resource = new ValveResourceFormat.Resource { FileName = "world.vwrld_c" };
        resource.Read(new MemoryStream(bytes));
        return resource.DataBlock is KeyValuesOrNTRO kv && kv.Data.ContainsKey("m_nCompileTimestamp")
            ? kv.Data.GetIntegerProperty("m_nCompileTimestamp")
            : null;
    }

    /// <summary>A note with its entity and parenthesised details taken out, so notes of one kind group.</summary>
    private static string NoteKind(string note) =>
        Regex.Replace(Regex.Replace(Regex.Replace(note, @"^\S+ \S+: ", ""), @"\([^)]*\)", ""), @"[\w./-]*\d[\w./-]*", "#").Trim();

    /// <summary>The item's package: its _dir.vpk, or its one .vpk.</summary>
    private static string? OuterPackage(string dir)
    {
        var vpks = Directory.GetFiles(dir, "*.vpk");
        return vpks.FirstOrDefault(v => v.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
               ?? vpks.FirstOrDefault(v => !Regex.IsMatch(Path.GetFileName(v), @"_\d{3}\.vpk$"));
    }

    /// <summary>The maps an item holds (nested maps/&lt;map&gt;.vpk, or the package itself when it holds a
    /// maps/&lt;map&gt;.vmap_c), and the item's own material names.</summary>
    private static (List<(string, Func<Dictionary<string, byte[]>>)>, List<string>) MapsOf(string outer)
    {
        using var package = new Package();
        package.Read(outer);
        var materials = (package.Entries.GetValueOrDefault("vmat_c") ?? []).Select(e => e.GetFullPath()).ToList();
        var maps = new List<(string, Func<Dictionary<string, byte[]>>)>();
        foreach (var entry in package.Entries.GetValueOrDefault("vpk") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || path.Count(c => c == '/') != 1)
                continue;
            maps.Add((path, () =>
            {
                using var p = new Package();
                p.Read(outer);
                var bytes = Io.VpkEntries.Read(p, p.FindEntry(path)!);
                using var inner = new Package();
                inner.SetFileName(Path.GetFileName(path));
                inner.Read(new MemoryStream(bytes));
                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in inner.Entries.Values)
                    foreach (var e in group)
                        files[e.GetFullPath()] = Io.VpkEntries.Read(inner, e);
                return files;
            }));
        }
        if (maps.Count == 0 && (package.Entries.GetValueOrDefault("vmap_c") ?? []).Any(e => e.GetFullPath().Count(c => c == '/') == 1))
            maps.Add(("(package)", () => Maps.Decompile.MapDecompiler.ReadPackage(outer)));
        return (maps, materials);
    }

    private static void Write(string report, HashSet<string> done, Dictionary<string, object?> row)
    {
        File.AppendAllText(report, JsonSerializer.Serialize(row) + "\n");
        done.Add((string)row["key"]!);
    }

    private static string? Opt(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
