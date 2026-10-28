using System.Text.RegularExpressions;
using Source2.Compiler;
using Source2.Compiler.Maps.Decompile;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The decompile's entities against the Hammer source they were compiled from, through
/// the forward compile: Valve's package is decompiled, and our lump author run on the
/// decompiled .vmap must give what it gives on the true source, lump for lump, entity
/// for entity, value, type, key order and connection. Our author is the yardstick
/// rather than Valve's lump because it is the same code on both sides: what it does not
/// yet do (EntityLumpAgainstValveTests' gaps) cancels out, and only the decompile shows.
///
/// <para>A difference must be one of the gaps written down in <see cref="Gaps"/>; the
/// rest fail the test, so a gap closing shows as a list that can shrink.</para>
/// </summary>
public partial class MapDecompilerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "ze_doom_p2_c_gameplay")]
    [InlineData("s2c_rc_probe", "probe01")]
    // Prefabs and instances (docs/DECOMPILE.md stage 3): a prefab (s2c_prefabprobe), one instance
    // (s2c_pinst), an instance placed by a prefab (5), a prefab's prefab and instances (7),
    // nested instances with paths and deformers (s2c_instnest), and atixref's 144 instances.
    [InlineData("s2c_rc_probe", "s2c_prefabprobe")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe2")]
    [InlineData("s2c_rc_probe", "s2c_pinst")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe5")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe7")]
    [InlineData("s2c_rc_probe", "s2c_instnest")]
    [InlineData("s2c_rc_probe", "atixref")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe3")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe4")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe6")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe8")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe9")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe11")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe12")]
    [InlineData("s2c_rc_probe", "s2c_pinst7")]
    [InlineData("s2c_rc_probe", "s2c_prefabinst")]
    public void Entities_CompileAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();

        var result = MapDecompiler.Decompile(MapDecompiler.ReadPackage(package), map, schema, StockMaterials());
        var decompiled = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
        var truth = DmxBinary.ReadFile(source);

        // Both sides settle as the compile does. A prop Valve's settle put to sleep ships with "Start
        // asleep", which the settle skips (SettleWorld.IsEligible), so the decompile's settled pose
        // stands; where the settle refuses the source, neither side settles.
        var (expected, settled) = Lumps(truth, map, schema, source, settle: true);
        var (ours, _) = Lumps(decompiled, map, schema, source, settled, result);
        output.WriteLine($"{map}: {result.Entities.Count} entities, {result.Placeholders} placeholders, {result.Notes.Count} notes, " +
                         $"{result.Vmap.Length:n0} bytes{(settled ? "" : ", not settled")}");
        foreach (var note in result.Notes.GroupBy(n => Regex.Replace(n, @"^\S+ \S+: ", "")).Take(15))
            output.WriteLine($"  note x{note.Count()}: {note.Key}");

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), ours.Keys.Order(StringComparer.OrdinalIgnoreCase));
        // A brush entity with no geometry does not ship (EntityLumpAuthor.ReachesTheLump), and the
        // decompile does not rebuild geometry yet: those are left out of the truth side, counted.
        // A brush entity whose model the package lacks (the compile failed it) or holds no hull
        // comes back with no geometry, does not ship (EntityLumpAuthor.ReachesTheLump), and is
        // left out of the truth side, counted.
        var brush = result.Notes.Where(n => n.EndsWith("no geometry", StringComparison.Ordinal))
                                .Select(n => n.Split(' ')[1].TrimEnd(':')).ToHashSet(StringComparer.Ordinal);
        var unexplained = new List<string>();
        var explained = new Dictionary<string, int>();
        foreach (var (path, bytes) in expected)
        {
            var truthSide = EntityLumpComparison.Read(bytes, path);
            var withoutBrush = truthSide.Where(e => !brush.Contains(e.HammerId)).ToList();
            if (withoutBrush.Count != truthSide.Count)
                explained["brush entity with no model or no hull: no geometry, so it does not ship"] =
                    explained.GetValueOrDefault("brush entity with no model or no hull: no geometry, so it does not ship") + truthSide.Count - withoutBrush.Count;
            var oursSide = EntityLumpComparison.Read(ours[path], path).Where(e => !brush.Contains(e.HammerId)).ToList();
            // DECOMPILE_LUMP=<lump file name>: both sides' entities of that lump, in order.
            if (Environment.GetEnvironmentVariable("DECOMPILE_LUMP") is { Length: > 0 } listed && Path.GetFileName(path) == listed)
                foreach (var (side, list) in new[] { ("truth", truthSide), ("ours", EntityLumpComparison.Read(ours[path], path).ToList()) })
                    output.WriteLine($"  {side}: " + string.Join(", ", list.Select(e => $"{e.ClassName}#{e.HammerId}")));
            foreach (var line in EntityLumpComparison.Diff(withoutBrush, oursSide))
            {
                var gap = Gaps.FirstOrDefault(g => g.Pattern.IsMatch(line));
                // A template's copies are numbered by their place in its lump, so a brush member
                // the decompile cannot ship yet moves every later member's index.
                if (gap.Reason is null && withoutBrush.Count != truthSide.Count && line.Contains("_template_lump_ent_index", StringComparison.Ordinal))
                    gap = (null!, "template member index moved by a brush member that does not ship");
                if (gap.Reason is null)
                    unexplained.Add($"{Path.GetFileName(path)}: {line}");
                else
                    explained[gap.Reason] = explained.GetValueOrDefault(gap.Reason) + 1;
            }
        }
        foreach (var (reason, count) in explained)
            output.WriteLine($"  gap x{count}: {reason}");
        // DECOMPILE_ALL=1: every unexplained line, not the first 40.
        foreach (var line in unexplained.Take(Environment.GetEnvironmentVariable("DECOMPILE_ALL") is { Length: > 0 } ? int.MaxValue : 40))
            output.WriteLine("  UNEXPLAINED " + line);
        Assert.True(unexplained.Count == 0, $"{unexplained.Count} unexplained difference(s); first: {unexplained.FirstOrDefault()}");
    }

    /// <summary>
    /// <c>DECOMPILE_RC=&lt;valve.vpk&gt;|&lt;rc.vpk&gt;</c>: Valve's default_ents from a map's own
    /// package against resourcecompiler's compile of our decompile of it, every difference
    /// counted by key. Reports, does not assert: the two builds may differ in options.
    /// </summary>
    [Fact]
    public void ResourceCompilerOfTheDecompile_AgainstValve()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_RC") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        static byte[] DefaultEnts(string vpk) => MapDecompiler.ReadPackage(vpk)
            .First(f => f.Key.EndsWith("/entities/default_ents.vents_c", StringComparison.OrdinalIgnoreCase)).Value;
        var valve = EntityLumpComparison.Read(DefaultEnts(p[0]));
        var rc = EntityLumpComparison.Read(DefaultEnts(p[1]));
        var report = EntityLumpComparison.Diff(valve, rc);
        output.WriteLine($"valve {valve.Count} entities, rc of the decompile {rc.Count}; {report.Count} difference(s)");
        foreach (var g in report.GroupBy(l => Regex.Replace(l, @"^\[[^\]]+\] ", "").Split(':')[0]).OrderByDescending(g => g.Count()).Take(40))
            output.WriteLine($"  {g.Count(),5} x {g.Key}   e.g. {g.First()[..Math.Min(160, g.First().Length)]}");

        // The brush entities' models, by file name (the two maps' folders differ): PHYS against PHYS.
        static Dictionary<string, byte[]> Models(string vpk) => MapDecompiler.ReadPackage(vpk)
            .Where(f => f.Key.Contains("/entities/", StringComparison.Ordinal) && f.Key.EndsWith(".vmdl_c", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetFileName(f.Key), f => f.Value);
        var (va, vb) = (Models(p[0]), Models(p[1]));
        int exact = 0, missing = 0;
        var differ = new List<string>();
        foreach (var (name, bytes) in va)
        {
            if (!vb.TryGetValue(name, out var other)) { missing++; continue; }
            var (ta, tb) = (WorldPhysicsAuthorTests.Trees(bytes).GetValueOrDefault("PHYS"), WorldPhysicsAuthorTests.Trees(other).GetValueOrDefault("PHYS"));
            var d = ta is null || tb is null ? (ta is null && tb is null ? [] : ["PHYS on one side only"]) : KvTreeDiff.Diff(ta, tb);
            if (d.Count == 0) exact++; else differ.Add($"{name}: {d.Count} differences, first {d[0]}");
        }
        output.WriteLine($"entity models: valve {va.Count}, rc of the decompile {vb.Count}; {exact} PHYS equal, {differ.Count} differ, {missing} missing");
        foreach (var line in differ.Take(15))
            output.WriteLine("  " + line[..Math.Min(220, line.Length)]);
    }

    /// <summary>
    /// The brush entities' collision, through the forward compile: our entity model builder
    /// (Physics.EntityPhysicsModels) run on the decompiled meshes and on the true source, PHYS
    /// block against PHYS block for every model. A model either matches whole, or its hulls are
    /// the same point sets (geometry equal, the bytes not: the builder's output order follows its
    /// input's), or it differs; each counted, the differing ones listed.
    /// </summary>
    [Theory]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "ze_doom_p2_c_gameplay")]
    [InlineData("s2c_rc_probe", "probe01")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe2")]
    public void BrushPhysics_CompilesAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var cs2 = CS2Fixtures.StockPak();
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || cs2 is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, ".."));
        using var content = new Maps.GameContent(cs2, Path.Combine(game, "csgo_addons", addon));

        var result = MapDecompiler.Decompile(MapDecompiler.ReadPackage(package), map, schema, StockMaterials());
        var decompiled = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
        Maps.MapPrefabs.Attach(decompiled, PrefabLoader(result));
        Maps.MapDeformers.Apply(decompiled, schema);
        var truth = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(truth, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        Maps.MapDeformers.Apply(truth, schema);

        Dictionary<string, Physics.EntityPhysicsModels.Model> Models(DmxBinary.Document d)
            => Physics.EntityPhysicsModels.Build(d, map, content.Material, content.CollisionProperty, schema, [], content.SmartProp)
                      .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var expected = Models(truth);
        var ours = Models(decompiled);

        int exact = 0, sameGeometry = 0;
        var differing = new List<string>();
        foreach (var (path, model) in expected.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!ours.TryGetValue(path, out var mine))
            {
                differing.Add($"{path} ({model.ClassName}): no model from the decompile");
                continue;
            }
            var a = WorldPhysicsAuthorTests.Trees(Physics.EntityPhysicsModels.Author(model, content.SurfaceName))["PHYS"];
            var b = WorldPhysicsAuthorTests.Trees(Physics.EntityPhysicsModels.Author(mine, content.SurfaceName))["PHYS"];
            var diff = KvTreeDiff.Diff(a, b);
            if (diff.Count == 0)
            {
                exact++;
                continue;
            }
            var (ha, hb) = (HullPointSets(model), HullPointSets(mine));
            if (ha.Count == hb.Count && ha.Zip(hb).All(p => p.First.SetEquals(p.Second))
                && model.Physics.AllShapes.Select(x => (x.Attribute, x.ToolMaterialHash)).SequenceEqual(mine.Physics.AllShapes.Select(x => (x.Attribute, x.ToolMaterialHash))))
            {
                sameGeometry++;
                if (sameGeometry <= 3)
                    output.WriteLine($"  same point sets: {path} ({model.ClassName}): " + string.Join(" | ", diff.Take(12)));
            }
            else
                differing.Add($"{path} ({model.ClassName}): hulls {ha.Count} vs {hb.Count}; {diff.Count} differences, first {diff[0]}");
            // DECOMPILE_ORDER=<model path part>: both sides' shapes in order, material hash and centroid.
            if (Environment.GetEnvironmentVariable("DECOMPILE_ORDER") is { Length: > 0 } part && path.Contains(part, StringComparison.Ordinal))
                foreach (var (side, m) in new[] { ("truth", model), ("ours", mine) })
                    output.WriteLine($"  {side}: " + string.Join(" ", m.Physics.AllShapes.Select(x => $"{x.ToolMaterialHash}@{x.Hull?.Centroid}")));
        }
        foreach (var path in ours.Keys.Where(k => !expected.ContainsKey(k)))
            differing.Add($"{path}: only from the decompile");
        output.WriteLine($"{map}: {expected.Count} models; {exact} PHYS exact, {sameGeometry} same hull point sets, {differing.Count} differ");
        foreach (var group in result.Notes.Where(n => !n.Contains("walk positions")).GroupBy(n => Regex.Replace(n, @"^\S+ \S+: |\([^)]*\)|maps/\S+", "").Trim()))
            output.WriteLine($"  note x{group.Count()}: {group.Key}");
        foreach (var line in differing.Take(25))
            output.WriteLine("  DIFFERS " + line);
        // Same point sets with other bytes: the hull's vertex and face order, which follows the
        // order the compile collects a piece's points in (BrushHulls.GroupVertices' hash slots over
        // the triangulated faces' first-meet numbering), is not inverted yet. Those count as a gap;
        // a model that differs otherwise must be one of these.
        var unexplained = differing.Where(d => !KnownHullMisses.Any(k => d.StartsWith(k, StringComparison.Ordinal))).ToList();
        Assert.True(unexplained.Count == 0, $"{unexplained.Count} model(s) differ: {unexplained.FirstOrDefault()}");
    }

    /// <summary>Models with a corner the fit cannot place exactly (MapDecompiler.CornerFor: no grid value
    /// and no point within the search lands on the hull's vertex), each a few float steps off.</summary>
    private static readonly string[] KnownHullMisses =
    [
        "maps/ze_doom_p2_c_gameplay/entities/teleportrandom2_458.vmdl",
        "maps/probe_classes/entities/probe_func_nav_blocker_2333.vmdl",
        "maps/probe_classes/entities/probe_trigger_wind_2863.vmdl",
    ];

    /// <summary>A decompile's prefab maps, as MapPrefabs.Attach loads them (by targetMapPath).</summary>
    private static Func<string, DmxBinary.Document?> PrefabLoader(MapDecompiler.Result result)
        => path => result.Prefabs.TryGetValue(path.Replace('\\', '/'), out var bytes)
            ? Maps.MapNodeAngles.Apply(DmxBinary.Read(bytes)) : null;

    /// <summary>Each hull's vertices as a set, in shape order.</summary>
    private static List<HashSet<System.Numerics.Vector3>> HullPointSets(Physics.EntityPhysicsModels.Model model)
        => [.. model.Physics.AllShapes.Where(s => s.Hull is not null).Select(s => s.Hull!.VertexPositions.ToHashSet())];

    private static IReadOnlyDictionary<uint, string>? _stockMaterials;

    /// <summary>The game's material names by tool material hash (csgo and core pak01).</summary>
    private static IReadOnlyDictionary<uint, string> StockMaterials()
        => _stockMaterials ??= CS2Fixtures.StockPak() is { } csgo
            ? BrushGeometry.GameMaterials(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csgo)!, "..")))
            : new Dictionary<uint, string>();

    /// <summary>What the decompile does not bring back yet, each with its reason.</summary>
    private static readonly (Regex Pattern, string Reason)[] Gaps =
    [
        // EntityLumpSet.WithParticleSnapshot writes the path only when the node snapshot_mesh
        // names is in the map; probe_classes' node 3 is a world mesh.
        (new(@"\] snapshot_file: value valve String/ResourceName:maps/[^ ]+/particle_snapshots/node_\d+\.vsnap, ours String/ResourceName:$"),
         "particle snapshot from a mesh node: world geometry not rebuilt"),
    ];

    /// <summary>The lumps our author makes of a map, as EntityLumpAgainstValveTests builds them:
    /// prefabs attached from the addon's content and deformers applied, the document passed so
    /// instances are collapsed, and settled when <paramref name="settle"/> and the settle takes the
    /// map (whether it did is returned).</summary>
    private static (Dictionary<string, byte[]> Lumps, bool Settled) Lumps(DmxBinary.Document document, string map, FgdSchema schema,
                                                                       string source, bool settle, MapDecompiler.Result? decompiled = null)
    {
        Maps.MapPrefabs.Attach(document, decompiled is null
            ? Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!)
            : PrefabLoader(decompiled));
        Maps.MapDeformers.Apply(document, schema);
        Dictionary<string, Maps.SettleWorld.Settlement>? settled = null;
        if (settle)
            try { settled = SettleLumpTests.Settle(document, source); }
            catch (NotSupportedException) { }
        // With the smart props' locators, which take node ids before any instance copy
        // (EntityLumpAgainstValveTests passes them too): atixref's radiator moves every copy id by one.
        return (EntityLumpSet.Author(MapEntities.From(document), schema, map, MapEntities.FixupEntityNames(document), document,
                                     MapFixtures.SmartPropLocators, settled)
                             .ToDictionary(l => l.Path, l => l.Bytes, StringComparer.OrdinalIgnoreCase), settled is not null);
    }
}
