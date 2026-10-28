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
    public void Entities_CompileAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();

        var result = MapDecompiler.Decompile(MapDecompiler.ReadPackage(package), map, schema);
        var decompiled = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
        var truth = DmxBinary.ReadFile(source);

        var ours = Lumps(decompiled, map, schema, source);
        var expected = Lumps(truth, map, schema, source);
        output.WriteLine($"{map}: {result.Entities.Count} entities, {result.Placeholders} placeholders, {result.Notes.Count} notes, " +
                         $"{result.Vmap.Length:n0} bytes");
        foreach (var note in result.Notes.GroupBy(n => Regex.Replace(n, @"^\S+ \S+: ", "")).Take(15))
            output.WriteLine($"  note x{note.Count()}: {note.Key}");

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), ours.Keys.Order(StringComparer.OrdinalIgnoreCase));
        // A brush entity with no geometry does not ship (EntityLumpAuthor.ReachesTheLump), and the
        // decompile does not rebuild geometry yet: those are left out of the truth side, counted.
        var brush = result.Entities.Where(e => e.BrushModel is not null).Select(e => e.IdPath).ToHashSet(StringComparer.Ordinal);
        var unexplained = new List<string>();
        var explained = new Dictionary<string, int>();
        foreach (var (path, bytes) in expected)
        {
            var truthSide = EntityLumpComparison.Read(bytes, path);
            var withoutBrush = truthSide.Where(e => !brush.Contains(e.HammerId)).ToList();
            if (withoutBrush.Count != truthSide.Count)
                explained["brush entity: geometry not rebuilt, so it does not ship"] =
                    explained.GetValueOrDefault("brush entity: geometry not rebuilt, so it does not ship") + truthSide.Count - withoutBrush.Count;
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
        foreach (var line in unexplained.Take(40))
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
    }

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
    /// instances are collapsed.</summary>
    private static Dictionary<string, byte[]> Lumps(DmxBinary.Document document, string map, FgdSchema schema, string source)
    {
        Maps.MapPrefabs.Attach(document, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        Maps.MapDeformers.Apply(document, schema);
        return EntityLumpSet.Author(MapEntities.From(document), schema, map, MapEntities.FixupEntityNames(document), document)
                            .ToDictionary(l => l.Path, l => l.Bytes, StringComparer.OrdinalIgnoreCase);
    }
}
