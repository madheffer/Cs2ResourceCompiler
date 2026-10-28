using Source2.Compiler.Io;
using System.Globalization;
using System.Numerics;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The lump's entities back to the source keys that compile to them: each step undoes
/// one rule of <see cref="BuildEntity"/>, and nothing is recovered that the forward
/// rules do not account for (docs/DECOMPILE.md). The test of a step is the forward
/// compile: an entity read back here and authored again must give the lump's entity.
/// </summary>
public static partial class EntityLumpAuthor
{
    /// <summary>One lump entity, back as the source states it.</summary>
    /// <param name="Entity">The source entity (keys in source order, placement, outputs).</param>
    /// <param name="SourceId">Its compile_source_id: its index in the compile's walk.</param>
    /// <param name="IdPath">Its hammerUniqueId: prefab node ids, then its own, joined by ':'.</param>
    /// <param name="BrushModel">The model the compile built for its geometry, when it had any.</param>
    /// <param name="Dropped">Keys the compile adds that were taken off, for the report.</param>
    public sealed record SourceEntity(MapEntities.Entity Entity, int SourceId, string IdPath, string? BrushModel,
                                      IReadOnlyList<string> Dropped)
    {
        /// <summary>The lump entity it was read from, for a second reading (an instance copy's).</summary>
        public KVObject? Raw { get; init; }
    }

    /// <summary>
    /// A lump entity (one element of <c>m_entityKeyValues</c>) back to its source.
    /// <paramref name="fixup"/> is the name prefix the map's fixup gave it ("[PR#]") or null.
    /// </summary>
    /// <param name="instanced">The entity is an instance's copy, whose table is a new entity's
    /// (<see cref="KeyTable"/>'s instanced branch: classname, targetname, the class's keys in its
    /// finalized order with the template's values, then the template's undeclared keys reversed);
    /// it is read back as its template's keys.</param>
    public static SourceEntity Unbuild(KVObject lumpEntity, FgdSchema? schema, string? fixup, bool instanced = false)
    {
        var values = Child(Child(lumpEntity, "keyValues3Data"), "values")
                     ?? throw new InvalidDataException("lump entity without keyValues3Data.values");
        var keys = values.Children.Select(p => (p.Key, p.Value)).ToList();
        string? Text(string key) => keys.FirstOrDefault(k => k.Key.EqualsAscii(key)).Value is { } v ? Plain(v) : null;

        var className = Text("classname") ?? throw new InvalidDataException("lump entity without classname");
        var isWorld = className.EqualsAscii("worldspawn");
        var dropped = new List<string>();

        // The table is written backwards between the compile's own keys: worldspawn
        // leads with compile_source_id, every other entity follows its table with it,
        // and origin always follows (BuildEntity). Placement never sits in the table.
        var end = keys.FindIndex(k => k.Key.EqualsAscii(isWorld ? "origin" : "compile_source_id"));
        if (end < 0)
            throw new InvalidDataException($"{className}: no {(isWorld ? "origin" : "compile_source_id")} in the lump entity");
        var start = isWorld && keys.Count > 0 && keys[0].Key.EqualsAscii("compile_source_id") ? 1 : 0;
        var region = keys.GetRange(start, end - start);
        // What follows the table and precedes the compile's id: the node's bone pose,
        // then the keys the class ships (GameKeysOf), each only when the table lacks it.
        var gameKeys = new HashSet<string>((schema?.GameKeysOf(className) ?? []).Select(k => k.Key), Tier0Strings.IgnoreCase);
        string? boneTransforms = null;
        while (region.Count > 0 && (gameKeys.Contains(region[^1].Key) || region[^1].Key.EqualsAscii("boneTransforms")))
        {
            if (region[^1].Key.EqualsAscii("boneTransforms"))
                boneTransforms = Plain(region[^1].Value);
            dropped.Add(region[^1].Key);
            region.RemoveAt(region.Count - 1);
        }
        var table = Enumerable.Reverse(region).ToList();

        // A source key named hammerUniqueId takes the id path where it stands; the
        // trailing one is then not written (BuildEntity).
        var idInTable = table.Any(k => k.Key.EqualsAscii("hammerUniqueId"));

        // The preprocess appends its keys after the defaults (Preprocess): the bake
        // resources, their custom flags and the light units; a baked build's light step
        // appends its own after those (LightWrites: BakedShadowAssignment, LightPrecompute).
        // Taken off the tail only.
        var preprocessed = PreprocessKeys(className, schema);
        while (table.Count > 0 && (preprocessed.Contains(table[^1].Key)
                                   || table[^1].Key.StartsWith("precomputedobb", StringComparison.OrdinalIgnoreCase)))
        {
            dropped.Add(table[^1].Key);
            table.RemoveAt(table.Count - 1);
        }

        // The defaults the compile filled in follow the source's keys in the class's
        // finalized order, each at its default (KeyTable). The shortest source part
        // whose remainder is exactly that fill is the source: a key the mapper set to
        // its default and one the compile filled in compile alike, and in this split
        // both land where the forward table puts them.
        // An instance's copy takes every declared key from its template, defaults filled, in the
        // class's order, and the template's undeclared keys after them in reverse: the template's
        // keys are the declared ones as they stand and the undeclared ones turned back.
        if (instanced && schema is not null && schema.KeysOf(className).Count > 0)
        {
            var declared = table.Where(k => k.Key.EqualsAscii("classname") || k.Key.EqualsAscii("targetname") || schema.KeyOf(className, k.Key) is not null).ToList();
            var undeclared = table.Where(k => !declared.Contains(k)).Reverse().ToList();
            table = [.. declared, .. undeclared];
        }
        var split = instanced ? table.Count : DefaultsStart(className, table, schema);
        for (var i = split; i < table.Count; i++)
            dropped.Add(table[i].Key);

        // A bake resource with no custom flag was empty in the source: the preprocess filled
        // it, or the export put the baked texture over it (Preprocess, ExportLighting).
        var bakeResources = new HashSet<string>((schema?.BakeResourcesOf(className) ?? []).Select(r => r.Key), Tier0Strings.IgnoreCase);
        var flagged = new HashSet<string>(keys.Where(k => k.Key.StartsWith("custom", StringComparison.OrdinalIgnoreCase))
                                              .Select(k => k.Key["custom".Length..]), Tier0Strings.IgnoreCase);

        var sourceKeys = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in table.Take(split))
        {
            if (bakeResources.Contains(key) && !flagged.Contains(key))
            {
                sourceKeys.Add(new(key, ""));
                continue;
            }
            var declared = schema?.KeyOf(className, key);
            var text = Plain(value);
            if (declared is not null ? declared.Type == FgdSchema.FieldType.EntityName && TakesFixup(declared) : key.EqualsAscii("targetname"))
                text = Unfix(text, fixup);
            if (declared?.Type == FgdSchema.FieldType.Color)
                text = ColorText(value);
            // The snapshot the compile generates from the node snapshot_mesh names is written
            // over whatever the source held (EntityLumpSet.WithParticleSnapshot); a source
            // without the key gets it appended, which this does not undo (not measured).
            if (key.EqualsAscii("snapshot_file") && Text("snapshot_mesh") is { } mesh
                && text.StartsWith("maps/", StringComparison.Ordinal)
                && text.EndsWith($"/particle_snapshots/node_{mesh}.vsnap", StringComparison.Ordinal))
                text = "";
            sourceKeys.Add(new(key, text));
        }

        var idPath = Text("hammerUniqueId") ?? "";
        var nodeId = int.TryParse(idPath.Split(':')[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
        if (idInTable)
            // The source's own value is gone; the id path it was given compiles the same.
            dropped.Add("hammerUniqueId (source key)");
        var sourceId = (int)(Number(keys.FirstOrDefault(k => k.Key.EqualsAscii("compile_source_id")).Value) ?? -1);

        var model = keys.FirstOrDefault(k => k.Key.EqualsAscii("model") && k.Value.Flag == KVFlag.ResourceName && keys.IndexOf(k) > end);
        string? brushModel = model.Value is { } m && Plain(m).Contains("/entities/", StringComparison.Ordinal) ? Plain(m) : null;

        var entity = new MapEntities.Entity(
            className, nodeId, sourceKeys,
            Vector(keys, "origin") ?? Vector3.Zero,
            Vector(keys, "angles") ?? Vector3.Zero,
            Vector(keys, "scales") ?? Vector3.One,
            isWorld,
            UnbuildConnections(Child(lumpEntity, "m_connections"), fixup),
            HasGeometry: brushModel is not null)
        { BoneTransforms = boneTransforms };
        return new SourceEntity(entity, sourceId, idPath, brushModel, dropped) { Raw = lumpEntity };
    }

    /// <summary>Where the compile's default fill starts in a table in fill order.</summary>
    private static int DefaultsStart(string className, List<(string Key, KVObject Value)> table, FgdSchema? schema)
    {
        if (schema is null)
            return table.Count;
        var classKeys = schema.KeysOf(className).Where(k => !IsPlacement(k.Name) && !IsElementName(k.Name)).ToList();
        var probe = new MapEntities.Entity(className, 0, [], Vector3.Zero, Vector3.Zero, Vector3.One, false, []);
        for (var k = 0; k <= table.Count; k++)
        {
            var present = new HashSet<string>(table.Take(k).Select(t => t.Key), Tier0Strings.IgnoreCase);
            var expected = classKeys.Where(c => !present.Contains(c.Name)).ToList();
            if (expected.Count != table.Count - k)
                continue;
            var match = true;
            for (var i = 0; i < expected.Count && match; i++)
            {
                var (key, value) = table[k + i];
                match = key.EqualsAscii(expected[i].Name)
                        && Plain(value) == Plain(Typed(expected[i], DefaultOf(probe, expected[i]), null, schema));
            }
            if (match)
                return k;
        }
        return table.Count;
    }

    /// <summary>The keys <see cref="Preprocess"/> can append to a table of the class.</summary>
    private static HashSet<string> PreprocessKeys(string className, FgdSchema? schema)
    {
        var set = new HashSet<string>(Tier0Strings.IgnoreCase);
        foreach (var r in schema?.BakeResourcesOf(className) ?? [])
        {
            set.Add(r.Key);
            set.Add("custom" + r.Key);
        }
        if (LegacyLights.Contains(className))
        {
            set.Add("brightness_lumens");
            set.Add("brightness_legacy");
        }
        foreach (var key in BakeWrittenKeys)
            set.Add(key);
        // Light_SyncBrightnessUnits' keys, for the classes it has a record of (LightUnits.Sync).
        if (className is "light_barn" or "light_rect" or "light_omni2")
            foreach (var key in (string[])["brightness", "brightness_lumens", "brightness_nits", "brightness_candelas", "brightness_legacy"])
                set.Add(key);
        return set;
    }

    /// <summary>The keys a baked build's light step sets on a light's node (BakedShadowAssignment.Assign,
    /// LightPrecompute's bounds and box). directlight is not among them: every light class declares
    /// it, so it is in the table already and is set where it stands.</summary>
    private static readonly string[] BakeWrittenKeys =
    [
        "light_path_uniqueid", "light_map_uniqueid", "desiredstationary", "bakedshadowindex", "precomputedfieldsvalid",
        "precomputedboundsmins", "precomputedboundsmaxs", "precomputedobbextent", "precomputedobbangles",
        "precomputedobborigin", "precomputedsubfrusta",
    ];

    private static List<MapEntities.Connection> UnbuildConnections(KVObject? array, string? fixup)
    {
        var list = new List<MapEntities.Connection>();
        foreach (var c in array?.Values ?? [])
        {
            string S(string key) => Child(c, key) is { } v ? Plain(v) : "";
            list.Add(new MapEntities.Connection(
                S("m_outputName"), Unfix(S("m_targetName"), fixup), S("m_inputName"),
                Unfix(S("m_overrideParam"), fixup),
                (float)(Child(c, "m_flDelay") is { } d ? d.ToDouble(CultureInfo.InvariantCulture) : 0),
                Child(c, "m_nTimesToFire") is { } t ? t.ToInt32(CultureInfo.InvariantCulture) : -1));
        }
        return list;
    }

    /// <summary>A name with the fixup's prefix taken off, the inverse of <see cref="Fixup"/>.</summary>
    private static string Unfix(string name, string? fixup)
        => fixup is not null && name.StartsWith(fixup, StringComparison.Ordinal) ? name[fixup.Length..] : name;

    /// <summary>A colour as Hammer writes one: its channels, the alpha 255 the lump leaves out
    /// written back (<see cref="Color"/> drops it; <see cref="CNumbers.ToColor"/> reads it so).</summary>
    private static string ColorText(KVObject value)
    {
        var channels = value.IsArray ? value.Values.Select(Plain).ToList() : [Plain(value)];
        return channels.Count == 3 ? string.Join(' ', channels) + " 255" : string.Join(' ', channels);
    }

    /// <summary>
    /// A lump value as source text: a boolean as 1 or 0, a number as the shortest text
    /// of the 32-bit float the compile widened (<see cref="Typed"/>), an array's items
    /// joined by spaces, a string as it is.
    /// </summary>
    internal static string Plain(KVObject value)
    {
        var c = CultureInfo.InvariantCulture;
        if (value.IsArray)
            return string.Join(' ', value.Values.Select(Plain));
        return value.ValueType switch
        {
            KVValueType.Null => "",
            KVValueType.Boolean => value.ToBoolean(c) ? "1" : "0",
            KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => ((float)value.ToDouble(c)).ToString(c),
            _ => value.ToString(c) ?? "",
        };
    }

    private static KVObject? Child(KVObject? parent, string key)
        => parent is not null && parent.IsCollection && parent.TryGetValue(key, out var child) ? child : null;

    private static long? Number(KVObject? value)
        => value is null ? null : value.ValueType is KVValueType.Boolean ? (value.ToBoolean(CultureInfo.InvariantCulture) ? 1 : 0)
                                                                          : value.ToInt64(CultureInfo.InvariantCulture);

    private static Vector3? Vector(List<(string Key, KVObject Value)> keys, string name)
    {
        var v = keys.FirstOrDefault(k => k.Key.EqualsAscii(name)).Value;
        if (v is null)
            return null;
        var c = CultureInfo.InvariantCulture;
        if (v.IsArray)
        {
            var p = v.Values.Select(x => (float)x.ToDouble(c)).ToArray();
            return p.Length >= 3 ? new Vector3(p[0], p[1], p[2]) : null;
        }
        // An older compile's six-decimal string (Vector).
        var f = CNumbers.FloatArray(Plain(v), 3);
        return new Vector3(f[0], f[1], f[2]);
    }
}
