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
        var roundTrip = a.Contains("--roundtrip");
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
                Dictionary<string, byte[]>? files = null;
                string? map = null;
                var task = Task.Run(() =>
                {
                    files = read();
                    row["files"] = files.Count;
                    row["packageBytes"] = files.Values.Sum(f => (long)f.Length);
                    map = Maps.Decompile.MapDecompiler.MapNameOf(files);
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
                    if (roundTrip)
                        row["roundTrip"] = RoundTrip(result, files!, map!, schema);
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

    /// <summary>
    /// The decompiled map's entities compiled again by our forward author (EntityLumpSet, matched
    /// byte for byte with resourcecompiler on our probes) beside the package's, entity by entity
    /// under hammerUniqueId: how many come back alike, and the first key that differs on the rest.
    /// Compared as text: compile_source_id, value types, placement as a string or an array and a
    /// brush model's slashes are what an older lump writes differently (GROUND_TRUTH 265), so they
    /// are not compared. Not settled (props a settle moved differ) and no smart prop locators.
    /// </summary>
    private static Dictionary<string, object> RoundTrip(Maps.Decompile.MapDecompiler.Result result, Dictionary<string, byte[]> files,
                                                        string map, FgdSchema schema)
    {
        try
        {
            var document = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
            Maps.MapPrefabs.Attach(document, path => result.Prefabs.TryGetValue(path.Replace('\\', '/'), out var bytes)
                ? Maps.MapNodeAngles.Apply(DmxBinary.Read(bytes)) : null);
            Maps.MapDeformers.Apply(document, schema);
            var walked = MapEntities.From(document);
            var walkedIds = walked.GroupBy(e => e.IdPath).ToDictionary(g => g.Key, g => string.Join(",", g.Select(e => e.ClassName)));
            var lumps = EntityLumpSet.Author(walked, schema, map, MapEntities.FixupEntityNames(document), document);
            var shipped = Shapes(files.Where(f => f.Key.EndsWith(".vents_c", StringComparison.OrdinalIgnoreCase)
                                                  && f.Key.StartsWith($"maps/{map}/", StringComparison.OrdinalIgnoreCase)).Select(f => (f.Key, f.Value)));
            var ours = Shapes(lumps.Select(l => (l.Path + "_c", l.Bytes)));
            // exact: the same lines in the same order. keys: every key both sides have holds the same
            // value and Valve's lump has no key ours lacks (ours may have more: today's FGD fills keys
            // an older FGD did not declare, and fills in its own order). Otherwise the entity differs:
            // a value, a key only Valve's has, or its connections.
            int exact = 0, keysAlike = 0, differ = 0, missing = 0;
            var valueDiff = new Dictionary<string, (int Count, string Example)>();
            var valveOnly = new Dictionary<string, (int Count, string Example)>();
            var oursOnly = new Dictionary<string, (int Count, string Example)>();
            var missingClasses = new Dictionary<string, (int Count, string Example)>();
            static void Count(Dictionary<string, (int Count, string Example)> d, string key, string example)
                => d[key] = d.TryGetValue(key, out var k) ? (k.Count + 1, k.Example) : (1, example);
            static (string Key, string Value) Split(string line)
            {
                if (line.StartsWith('@'))
                    return (line, "");
                var at = line.IndexOf('=');
                return (line[..at], line[(at + 1)..]);
            }
            foreach (var (id, theirs) in shipped)
            {
                var mine = ours.GetValueOrDefault(id) ?? [];
                foreach (var t in theirs)
                {
                    var match = mine.FindIndex(m => m.SequenceEqual(t));
                    if (match >= 0)
                    {
                        exact++;
                        mine.RemoveAt(match);
                        continue;
                    }
                    var cls = t.FirstOrDefault(l => l.StartsWith("classname=", StringComparison.Ordinal));
                    var pick = mine.FindIndex(m => m.Contains(cls ?? ""));
                    if (pick < 0)
                    {
                        missing++;
                        var read = result.Entities.Where(e => e.IdPath == id).Select(e => e.Entity.ClassName).ToList();
                        var left = string.Join(",", mine.Select(m => m.FirstOrDefault(l => l.StartsWith("classname=", StringComparison.Ordinal)) ?? "?"));
                        Count(missingClasses, cls ?? "?", $"{id}: decompile read {(read.Count == 0 ? "none" : string.Join(",", read))}; walked {walkedIds.GetValueOrDefault(id) ?? "none"}; ours under the id: {(left.Length == 0 ? "none" : left)}");
                        continue;
                    }
                    var m0 = mine[pick];
                    mine.RemoveAt(pick);
                    var v = t.Select(Split).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
                    var o = m0.Select(Split).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
                    var alike = true;
                    var name = (cls ?? "?")["classname=".Length..];
                    foreach (var (key, value) in v)
                    {
                        if (!o.TryGetValue(key, out var ov))
                        {
                            alike = false;
                            Count(valveOnly, key.StartsWith('@') ? "(connection)" : key, $"{name} {id}: {key}={value}");
                        }
                        else if (ov != value && !Rounded(value, ov))
                        {
                            alike = false;
                            Count(valueDiff, key, $"{name} {id}: valve {value} | ours {ov}");
                        }
                    }
                    foreach (var key in o.Keys.Where(k => !v.ContainsKey(k)))
                        Count(oursOnly, key.StartsWith('@') ? "(connection)" : key, $"{name} {id}: {key}={o[key]}");
                    if (alike)
                        keysAlike++;
                    else
                        differ++;
                }
            }
            static List<Dictionary<string, object>> Top(Dictionary<string, (int Count, string Example)> d)
                => [.. d.OrderByDescending(k => k.Value.Count).Take(12)
                        .Select(k => new Dictionary<string, object> { ["key"] = k.Key, ["count"] = k.Value.Count, ["example"] = k.Value.Example })];
            return new()
            {
                ["exact"] = exact, ["keysAlike"] = keysAlike, ["differ"] = differ, ["missing"] = missing,
                ["extra"] = ours.Values.Sum(l => l.Count),
                ["valueDiff"] = Top(valueDiff), ["valveOnly"] = Top(valveOnly), ["oursOnly"] = Top(oursOnly),
                ["missingClasses"] = Top(missingClasses),
            };
        }
        catch (Exception e)
        {
            return new() { ["error"] = $"{e.GetType().Name}: {e.Message}", ["at"] = e.StackTrace?.Split('\n').Take(4).Select(x => x.Trim()).ToList() ?? [] };
        }
    }

    /// <summary>
    /// Valve's value is a "%f" string (six decimals, as a lump that writes placement and some floats
    /// as strings does) and ours, a float or floats, prints to it under "%f": the same value.
    /// </summary>
    private static bool Rounded(string valve, string ours)
    {
        var v = valve.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var o = ours.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (v.Length != o.Length || v.Length == 0)
            return false;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        for (var i = 0; i < v.Length; i++)
        {
            var dot = v[i].IndexOf('.');
            if (dot < 0 || v[i].Length - dot - 1 != 6
                || !float.TryParse(o[i], System.Globalization.NumberStyles.Float, c, out var f)
                || Zero(f.ToString("F6", c)) != Zero(v[i]))
                return false;
        }
        return true;

        // "%f" prints a negative zero "-0.000000"; the float's sign is not what is compared.
        static string Zero(string t) => t == "-0.000000" ? "0.000000" : t;
    }

    /// <summary>Each lump entity as "key=value" lines (and its connections), grouped by hammerUniqueId.</summary>
    private static Dictionary<string, List<List<string>>> Shapes(IEnumerable<(string Path, byte[] Bytes)> lumps)
    {
        var all = new Dictionary<string, List<List<string>>>(StringComparer.Ordinal);
        foreach (var (path, bytes) in lumps)
        {
            using var resource = new ValveResourceFormat.Resource { FileName = Path.GetFileName(path) };
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not EntityLump lump)
                continue;
            foreach (var entity in lump.Data.GetArray("m_entityKeyValues"))
            {
                if (entity.GetSubCollection("keyValues3Data")?.GetSubCollection("values") is not { } values)
                    continue;
                var lines = new List<string>();
                var id = "";
                foreach (var kv in values.Children)
                {
                    if (kv.Key == "compile_source_id")
                        continue;
                    var text = Text(kv.Key, kv.Value);
                    if (kv.Key == "hammerUniqueId")
                        id = text;
                    lines.Add($"{kv.Key}={text}");
                }
                foreach (var c in entity.GetArray("m_connections"))
                    lines.Add($"@{c.GetStringProperty("m_outputName")}>{c.GetStringProperty("m_targetName")}.{c.GetStringProperty("m_inputName")}" +
                              $"({c.GetStringProperty("m_overrideParam")}) {Text("", (c.TryGetValue("m_flDelay", out var delay) ? delay : null))} {Text("", (c.TryGetValue("m_nTimesToFire", out var times) ? times : null))}");
                if (!all.TryGetValue(id, out var list))
                    all[id] = list = [];
                list.Add(lines);
            }
        }
        return all;

        static string Text(string key, KVObject? v)
        {
            if (v is null)
                return "";
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (v.IsArray)
                return string.Join(" ", v.Values.Select(x => Number(x.ToString(c) ?? "")));
            var s = v.ToString(c) ?? "";
            if (key is "origin" or "angles" or "scales")
                return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (key == "model")
                return s.Replace('\\', '/');
            return Number(s);
        }

        static string Number(string s)
            => !s.Any(char.IsLetter) && !Regex.IsMatch(s, @"\.\d{6}$") && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? ((float)d).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : s;
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
