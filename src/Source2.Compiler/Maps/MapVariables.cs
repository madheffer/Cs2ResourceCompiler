using Source2.Compiler.Io;

namespace Source2.Compiler.Maps;

/// <summary>
/// A map's variables (the root's CMapVariableSet, mapVariables) written into the keys bound to them.
/// A node binds a key to a variable by name (variableTargetKeys[i] to variableNames[i], the node's table at
/// CMapNode +0x3d0, serialized by rc 0923 180f93000); a prefab placement overrides a variable of its map
/// (variableOverrideNames/Values, CMapPrefab +0x588/+0x5a8).
///
/// <para>resourcecompiler applies them as a map loads (0923 18107d160, called from the prefab load
/// 180f96d40 once the prefab holds its map's set): for each of the set's variables in order, its value
/// (18107a810), then into every node of the map whose binding names that variable (1810ec600, by name,
/// case-blind), which sets the bound key to the value through the key's attribute when they differ
/// (node vf 0x7a0). The value is the placing prefab's override when its override names hold the
/// variable's (180fa0570, case-blind, the first match; an empty override counts), else the variable's
/// own value. On s2c_varprobe (ledger 187) a top-level spawn's targetname ships its map's
/// "map_level", the overridden placement "over_relay" and priority 5 (Int32 by the usual typing), the
/// other placement its map's defaults.</para>
///
/// <para>Not ported: variables driven by a choice group (m_ChoiceGroups, searched first by 18107a810)
/// throw; the rewrite 18107c510 a non-empty value goes through when the placing prefab's flag +0x55f is
/// set (a transform of vector values by the placement), and keys of the 0x24/0x25/0x26 attribute types
/// 1810ec600 converts, are not measured. Whether the walk 18107d160 makes (180f86e70, flags 0, 1, 1)
/// reaches nodes of prefabs nested in the map is not measured either: only the map's own nodes take its
/// variables here.</para>
/// </summary>
public static class MapVariables
{
    /// <summary>
    /// Write <paramref name="doc"/>'s variables into its nodes' bound keys, as placed by
    /// <paramref name="prefab"/> (null for the map being compiled). Run on a document loaded for this
    /// placement alone: the keys are changed in place.
    /// </summary>
    public static void Apply(DmxBinary.Document doc, DmxBinary.Element? prefab)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Root?.Get<DmxBinary.Element>("mapVariables") is not { } set)
            return;
        var names = Strings(set, "variableNames");
        if (names.Count == 0)
            return;
        if (set.GetElements("m_ChoiceGroups").Any())
            throw new NotSupportedException("map variables driven by a choice group are not ported");
        var values = Strings(set, "variableValues");
        var overrideNames = prefab is null ? [] : Strings(prefab, "variableOverrideNames");
        var overrideValues = prefab is null ? [] : Strings(prefab, "variableOverrideValues");

        var bound = doc.Elements.Where(e => Strings(e, "variableNames").Count > 0).ToList();
        for (var v = 0; v < names.Count; v++)
        {
            var name = names[v];
            var at = overrideNames.FindIndex(n => Tier0Strings.EqualsIgnoreCase(n, name));
            var value = at >= 0 ? (at < overrideValues.Count ? overrideValues[at] : "")
                                : (v < values.Count ? values[v] : "");
            foreach (var node in bound)
            {
                if (node.Get<DmxBinary.Element>("entity_properties") is not { } props)
                    continue;
                var keys = Strings(node, "variableTargetKeys");
                var variables = Strings(node, "variableNames");
                for (var i = 0; i < variables.Count && i < keys.Count; i++)
                {
                    if (!Tier0Strings.EqualsIgnoreCase(variables[i], name))
                        continue;
                    var key = props.Attributes.Keys.FirstOrDefault(k => Tier0Strings.EqualsIgnoreCase(k, keys[i])) ?? keys[i];
                    if (props.Get<string>(key) is { } current && Tier0Strings.EqualsIgnoreCase(current, value))
                        continue;
                    props.Set(key, DmxBinary.AttrType.String, value);
                }
            }
        }
    }

    private static List<string> Strings(DmxBinary.Element e, string name)
        => e.Get<object?[]>(name)?.Select(x => x as string ?? "").ToList() ?? [];
}
