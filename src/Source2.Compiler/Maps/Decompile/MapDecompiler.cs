using System.Numerics;
using Source2.Compiler.Io;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A compiled map package back to a Hammer <c>.vmap</c> (docs/DECOMPILE.md). This first
/// stage brings back the entities: every lump's entities, through
/// <see cref="EntityLumpAuthor.Unbuild"/>, placed in the compile's walk order.
/// Geometry, prefabs, instances, paths and world layers are not brought back yet; the
/// report says what each map is missing.
/// </summary>
public static class MapDecompiler
{
    /// <summary>What a decompile wrote and left out.</summary>
    /// <param name="Vmap">The .vmap file.</param>
    /// <param name="Entities">Entities written, the world included.</param>
    /// <param name="Placeholders">Walk positions with nothing in any lump (a hidden, editor-only
    /// or compile-only node), each filled with an editor-only info_target so the walk numbers
    /// the rest as the compile did.</param>
    /// <param name="Notes">What is not brought back, one line each.</param>
    public sealed record Result(byte[] Vmap, IReadOnlyList<EntityLumpAuthor.SourceEntity> Entities,
                                int Placeholders, IReadOnlyList<string> Notes);

    private const string LayerLumpPrefix = "world_layer_";

    /// <summary>The class of a walk position nothing shipped from.</summary>
    public const string PlaceholderClass = "info_target";

    /// <summary>Decompile the map in <paramref name="files"/> (path to bytes: a package's
    /// entries) named <paramref name="mapName"/>.</summary>
    public static Result Decompile(IReadOnlyDictionary<string, byte[]> files, string mapName, FgdSchema schema)
    {
        var notes = new List<string>();
        var lumps = files.Where(f => f.Key.EndsWith(".vents_c", StringComparison.OrdinalIgnoreCase)
                                     && f.Key.StartsWith($"maps/{mapName}/", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        if (lumps.Count == 0)
            throw new InvalidDataException($"no entity lump for maps/{mapName} in the package");

        // Every lump's raw entities first: the fixup prefix is a property of the map. A
        // template lump (<id path>#<key>) comes last: its entities are COPIES, placed
        // relative to the template and renamed (EntityLumpSet), and the original, where the
        // template kept it, is the one to write.
        var raw = new List<(string Lump, KVObject Entity)>();
        foreach (var (path, bytes) in lumps.OrderBy(l => IsTemplateLump(l.Key) ? 1 : 0))
            foreach (var e in LumpEntities(bytes, path))
                raw.Add((path, e));
        var fixup = raw.Any(r => Name(r.Entity).StartsWith(EntityLumpAuthor.NameFixupPrefix, StringComparison.Ordinal))
            ? EntityLumpAuthor.NameFixupPrefix : null;

        var entities = new List<EntityLumpAuthor.SourceEntity>();
        var layerOf = new Dictionary<EntityLumpAuthor.SourceEntity, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (lump, e) in raw)
        {
            var source = EntityLumpAuthor.Unbuild(e, schema, fixup);
            if (IsTemplateLump(lump) && !seen.Contains(source.IdPath))
                source = FromTemplateCopy(source, lump, entities, notes);
            // A template lump (<id>#entitylumpname) holds a copy of every entity a
            // point_template names, each with the original's hammerUniqueId
            // (EntityLumpSet): one source node.
            if (source.IdPath.Length > 0 && !seen.Add(source.IdPath))
                continue;
            if (source.IdPath.Contains(':'))
                notes.Add($"{source.Entity.ClassName} {source.IdPath}: placed by a prefab; written as a node of the map");
            if (source.BrushModel is not null)
                notes.Add($"{source.Entity.ClassName} {source.IdPath}: brush geometry ({source.BrushModel}) not rebuilt");
            // A world layer ships as world_layer_<name> (EntityLumpSet).
            var lumpName = Path.GetFileNameWithoutExtension(lump);
            if (lumpName.StartsWith(LayerLumpPrefix, StringComparison.OrdinalIgnoreCase))
                layerOf[source] = lumpName[LayerLumpPrefix.Length..];
            entities.Add(source);
        }

        var world = entities.FirstOrDefault(e => e.Entity.IsWorld)
                    ?? throw new InvalidDataException("no worldspawn in the lumps");
        var builder = new VmapBuilder(mapName);
        // The lump's mapUsageType is the literal the compile writes for every map (BuildEntity),
        // so it says nothing of the source's.
        builder.SetWorld(world.Entity.NodeId, world.Entity.Keys, world.Entity.Connections, fixup is not null, "standard");

        // The compile numbers nodes in walk order (compile_source_id), the world 0; writing
        // them as world children in that order walks them the same way. A position no lump
        // holds is a node that walked and shipped nothing: a placeholder keeps the count.
        // A layer's entities sit in a layer node; a run of them broken by others opens a
        // second node of the same name, so the walk still meets them in order.
        var rest = entities.Where(e => !e.Entity.IsWorld).OrderBy(e => e.SourceId).ToList();
        var nextNode = entities.Max(e => e.Entity.NodeId) + 1;
        var placeholders = 0;
        var at = 1;
        string? openLayer = null;
        DmxBinary.Element? layerNode = null;
        foreach (var e in rest)
        {
            for (; at < e.SourceId; at++, placeholders++)
            {
                builder.AddEntity(nextNode++, [new("classname", PlaceholderClass)], Vector3.Zero, Vector3.Zero, Vector3.One, [],
                                  editorOnly: true);
                openLayer = null;
            }
            var layer = layerOf.GetValueOrDefault(e);
            if (layer is not null && layer != openLayer)
                layerNode = builder.AddLayer(nextNode++, layer);
            openLayer = layer;
            var x = e.Entity;
            builder.AddEntity(x.NodeId, x.Keys, x.Origin, x.Angles, x.Scales, x.Connections,
                              parent: layer is null ? null : layerNode);
            at = e.SourceId + 1;
        }
        if (placeholders > 0)
            notes.Add($"{placeholders} walk positions shipped nothing (hidden, editor-only or compile-only nodes); filled with editor-only {PlaceholderClass}");

        return new Result(builder.Write(), entities, placeholders, notes);
    }

    private static bool IsTemplateLump(string path) => Path.GetFileName(path).Contains('#');

    /// <summary>The suffix the template pass gives a member's name and every reference to it
    /// (EntityLumpSet.Rename).</summary>
    private const string TemplateRename = "&0000";

    /// <summary>
    /// A template member whose original the template removed: the copy placed back where the
    /// template's transform puts it, and the names the template pass suffixed restored. Only a
    /// point_template places its copies relative to itself; any other template class puts them
    /// at the origin, and where they stood is lost.
    /// </summary>
    private static EntityLumpAuthor.SourceEntity FromTemplateCopy(EntityLumpAuthor.SourceEntity copy, string lump,
                                                                  List<EntityLumpAuthor.SourceEntity> written, List<string> notes)
    {
        var owner = Path.GetFileName(lump).Split('#')[0];
        var template = written.FirstOrDefault(w => w.IdPath.Replace(':', '_') == owner);
        var e = copy.Entity;
        static string Unrename(string s) => s.EndsWith(TemplateRename, StringComparison.Ordinal) ? s[..^TemplateRename.Length] : s;
        var keys = e.Keys.Select(k => new KeyValuePair<string, string>(k.Key, Unrename(k.Value))).ToList();
        var connections = e.Connections.Select(c => c with { TargetName = Unrename(c.TargetName), OverrideParam = Unrename(c.OverrideParam) }).ToList();
        var (origin, angles) = (e.Origin, e.Angles);
        // _template_lump_ent_index is the copy's place in the lump, written by the pass.
        keys.RemoveAll(k => k.Key.EqualsAscii("_template_lump_ent_index"));
        if (template is not null && template.Entity.ClassName.EqualsAscii("point_template"))
        {
            var m = TemplateTransform.Concat(TemplateTransform.AngleMatrix(template.Entity.Origin, template.Entity.Angles),
                                             TemplateTransform.AngleMatrix(origin, angles));
            (origin, angles) = (new Vector3(m[3], m[7], m[11]), TemplateTransform.MatrixAngles(m));
        }
        else
            notes.Add($"{e.ClassName} {copy.IdPath}: only a copy in {Path.GetFileName(lump)} survives, and that template places it at the origin; its placement is lost");
        return copy with { Entity = e with { Keys = keys, Connections = connections, Origin = origin, Angles = angles } };
    }

    /// <summary>Every entry of a map package, by path.</summary>
    public static Dictionary<string, byte[]> ReadPackage(string vpk)
    {
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in package.Entries.Values)
            foreach (var entry in group)
                files[entry.GetFullPath()] = Io.VpkEntries.Read(package, entry);
        return files;
    }

    /// <summary>The map a package holds: its one maps/&lt;name&gt;.vmap_c.</summary>
    public static string MapNameOf(IReadOnlyDictionary<string, byte[]> files)
    {
        var maps = files.Keys.Where(k => k.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)
                                         && k.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase)
                                         && k.Count(c => c == '/') == 1).ToList();
        return maps.Count == 1
            ? Path.GetFileNameWithoutExtension(maps[0])
            : throw new InvalidDataException($"the package holds {maps.Count} maps/*.vmap_c, not one");
    }

    private static IEnumerable<KVObject> LumpEntities(byte[] bytes, string path)
    {
        using var resource = new Resource { FileName = Path.GetFileName(path) };
        resource.Read(new MemoryStream(bytes));
        var data = resource.DataBlock as EntityLump
                   ?? throw new InvalidDataException($"{path} has no entity lump DATA block");
        return data.Data.GetArray("m_entityKeyValues").ToList();
    }

    private static string Name(KVObject entity)
    {
        if (!entity.TryGetValue("keyValues3Data", out var kv) || !kv.TryGetValue("values", out var values))
            return "";
        return values.TryGetValue("targetname", out var name) ? EntityLumpAuthor.Plain(name) : "";
    }
}
