using Source2.Compiler.Io;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// A path's lump keys (<see cref="PathKeys"/>) back to its CMapPathNode children. Every
/// node's position and tangents are solved through the forward (<see cref="PathControls"/>):
/// nothing is kept that does not give the lump's floats bit for bit.
/// </summary>
public static partial class EntityLumpAuthor
{
    /// <summary>
    /// The path's nodes, closedLoop and interpolationType from its lump keys, on
    /// <paramref name="path"/> (its origin and angles the lump's: the path's origin is its
    /// first node's, MapEntities.Read). Null when the lump has no pathNodes. What the lump
    /// does not hold is left as a node made in Hammer has it: a node key no path array
    /// carries at its class default (pathNodeColors is not traced, PathKeys), angles 0
    /// and scales 1.
    /// </summary>
    internal static MapEntities.Entity? UnbuildPath(MapEntities.Entity path, List<(string Key, KVObject Value)> keys,
                                                    FgdSchema? schema, List<string> notes)
    {
        string? Text(string key) => keys.FirstOrDefault(k => k.Key.EqualsAscii(key)).Value is { } v ? Plain(v) : null;
        if (Text("pathNodes") is not { } rowsText)
            return null;

        var rows = Rows(rowsText);
        var closed = Text("closed_loop") is { } loop && CNumbers.Atoi(loop) != 0;
        // A wrapped loop repeats its first node at the end (PathKeys, Wraps).
        var count = closed && rows.Count > 2 && schema?.HasFlag(path.ClassName, "supports_loop") == true ? rows.Count - 1 : rows.Count;

        var nodeClass = schema?.MetadataOf(path.ClassName, "path_node_class") ?? "path_node_generic";
        if (schema is null || !schema.IsPathNodeClass(nodeClass))
            nodeClass = "path_node_generic";
        var nodeKeys = NodeKeys(nodeClass, count, keys, schema);

        // Positions: each node's stored origin through the path's inverse matrix
        // (PathNode), so a node is a float that matrix takes to the row's first three.
        var toLocal = PathToLocal(path);
        var turn = Maps.MapMeshes.AngleMatrix(path.Angles);
        var frame = (float[])turn.Clone();
        (frame[3], frame[7], frame[11]) = (path.Origin.X, path.Origin.Y, path.Origin.Z);
        var origins = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var local = new Vector3(rows[i][0], rows[i][1], rows[i][2]);
            if (Preimage(v => Maps.MapMeshes.Transform(toLocal, v), Maps.MapMeshes.Transform(frame, local), local) is not { } here)
            {
                notes.Add($"path node {i}: no origin lands on ({local.X:R}, {local.Y:R}, {local.Z:R})");
                return null;
            }
            origins[i] = here;
        }

        // Tangents: the path's interpolationType 0 or 1 decides every node's (PathNode). The
        // factory's 1 first (rc 1810b3e90), then 0; a path neither gives is each node's own,
        // solved one tangent at a time.
        MapEntities.Entity With(int interpolation, MapEntities.PathNode[] nodes)
            => path with { PathNodes = nodes, ClosedLoop = closed, InterpolationType = interpolation };
        MapEntities.PathNode[] Nodes(Func<int, MapEntities.PathNode> make) => [.. Enumerable.Range(0, count).Select(make)];
        bool Lands(MapEntities.Entity e)
        {
            var controls = PathControls(e, schema).Controls;
            if (controls.Length != rows.Count * 9)
                return false;
            for (var k = 0; k < controls.Length; k++)
                if (BitConverter.SingleToInt32Bits(controls[k]) != BitConverter.SingleToInt32Bits(rows[k / 9][k % 9]))
                    return false;
            return true;
        }
        foreach (var interpolation in (int[])[1, 0])
        {
            var e = With(interpolation, Nodes(i => new MapEntities.PathNode(origins[i], nodeKeys[i])));
            if (Lands(e))
                return e;
        }

        // Each node's own types (a path type past 1): a type 0 or 1 tangent where one lands,
        // else type 3, the authored tangent, through the inverse rotation.
        var own = Nodes(i => new MapEntities.PathNode(origins[i], nodeKeys[i]));
        var wraps = closed && count > 1 && schema?.HasFlag(path.ClassName, "supports_loop") == true;
        for (var i = 0; i < count; i++)
            for (var side = 0; side < 2; side++)
            {
                var want = side == 0 ? new Vector3(rows[i][3], rows[i][4], rows[i][5]) : new Vector3(rows[i][6], rows[i][7], rows[i][8]);
                MapEntities.PathNode? found = null;
                foreach (var type in (int[])[0, 1])
                {
                    var node = side == 0 ? own[i] with { InTangentType = type } : own[i] with { OutTangentType = type };
                    var trial = (MapEntities.PathNode[])own.Clone();
                    trial[i] = node;
                    var row = PathNode(With(2, trial), trial, i, wraps, toLocal);
                    if (Same(side == 0 ? row.AsSpan(3, 3) : row.AsSpan(6, 3), want))
                    {
                        found = node;
                        break;
                    }
                }
                if (found is null && Preimage(v => Rotate(toLocal, v), Rotate(turn, want), want) is { } authored)
                    found = side == 0 ? own[i] with { InTangentType = 3, InTangent = authored } : own[i] with { OutTangentType = 3, OutTangent = authored };
                if (found is null)
                {
                    notes.Add($"path node {i}: no tangent lands on ({want.X:R}, {want.Y:R}, {want.Z:R})");
                    return null;
                }
                own[i] = found;
            }
        var mixed = With(2, own);
        if (Lands(mixed))
            return mixed;
        notes.Add("path: tangents solved one at a time do not land together");
        return null;
    }

    /// <summary>The path's node keys back per node: classname, then the node class's keys in
    /// its order, each from the path array it writes (write_to_path_key, the radius arrays)
    /// or at its default; node_name from pathNodeNames.</summary>
    private static List<KeyValuePair<string, string>>[] NodeKeys(string nodeClass, int count,
                                                                 List<(string Key, KVObject Value)> keys, FgdSchema? schema)
    {
        string? Text(string key) => keys.FirstOrDefault(k => k.Key.EqualsAscii(key)).Value is { } v ? Plain(v) : null;
        var names = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(Text("pathNodeNames") ?? "", @"(\d+): ([^;]*);"))
            names[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Groups[2].Value;
        var radius = Text("pathNodeRadiusScales") is { } r ? Words(r) : null;
        var height = Text("pathNodeRadiusHeightScales") is { } h ? Words(h) : null;

        var result = new List<KeyValuePair<string, string>>[count];
        for (var i = 0; i < count; i++)
        {
            var list = new List<KeyValuePair<string, string>> { new("classname", nodeClass) };
            foreach (var key in schema?.KeysOf(nodeClass) ?? [])
            {
                if (key.Name.EqualsAscii("classname") || IsPlacement(key.Name))
                    continue;
                string? value = null;
                // RadiusScales: both arrays absent means every node at 1, 1.
                if (key.Name.EqualsAscii("radius_scale"))
                    value = FloatText(key, radius is null ? 1f : CNumbers.Atof(radius[i]));
                else if (key.Name.EqualsAscii("radius_scale_height"))
                    value = FloatText(key, height is null ? 1f : CNumbers.Atof(height[i]));
                else if (key.WriteToPathKey is { Length: > 0 } pathKey)
                    // An array absent means no node held a value (PathKeys).
                    value = Text(pathKey) is { } array ? PathText(key, Words(array)[i]) : "";
                list.Add(new(key.Name, value ?? key.Default ?? ""));
            }
            if (names.TryGetValue(i, out var name))
            {
                var at = list.FindIndex(k => k.Key.EqualsAscii("node_name"));
                if (at >= 0)
                    list[at] = new("node_name", name);
                else
                    list.Add(new("node_name", name));
            }
            result[i] = list;
        }
        return result;
    }

    /// <summary>A float key's text: its default when that reads as the value, else the
    /// shortest text that does (Hammer keeps "2", not "2.0").</summary>
    private static string FloatText(FgdSchema.Key key, float value)
        => key.Default is { } d && CNumbers.Atof(d) == value ? d : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary><see cref="PathValue"/> backwards.</summary>
    private static string PathText(FgdSchema.Key key, string word) => key.Type switch
    {
        FgdSchema.FieldType.Boolean => word == "true" ? "1" : "0",
        FgdSchema.FieldType.Float => FloatText(key, CNumbers.Atof(word)),
        FgdSchema.FieldType.Integer => word,
        _ => word.Length >= 2 && word[0] == '"' && word[^1] == '"' ? word[1..^1] : word,
    };

    /// <summary>The entries of a KV3 text array (<see cref="Kv3Words"/>).</summary>
    private static string[] Words(string text)
        => [.. text.Trim().TrimStart('[').TrimEnd(']').Split(',').Select(w => w.Trim()).Where(w => w.Length > 0)];

    /// <summary>The rows of pathNodes (<see cref="Kv3Rows"/>), nine floats each.</summary>
    private static List<float[]> Rows(string text)
        => [.. Regex.Matches(text, @"\[([^\[\]]*)\]").Select(m => Words(m.Groups[1].Value)
                .Select(w => float.Parse(w, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray())];

    private static bool Same(ReadOnlySpan<float> a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a[0]) == BitConverter.SingleToInt32Bits(b.X)
           && BitConverter.SingleToInt32Bits(a[1]) == BitConverter.SingleToInt32Bits(b.Y)
           && BitConverter.SingleToInt32Bits(a[2]) == BitConverter.SingleToInt32Bits(b.Z);

    /// <summary>
    /// A vector <paramref name="forward"/> takes to <paramref name="want"/> bit for bit: the
    /// guess, else the nearest in float steps, searched in shells of growing step count up to
    /// <paramref name="reach"/> on each component. Null when none lands. Several may; the
    /// compile keeps nothing that tells them apart, and the nearest to the guess is taken.
    /// </summary>
    private static Vector3? Preimage(Func<Vector3, Vector3> forward, Vector3 guess, Vector3 want, int reach = 8)
    {
        for (var shell = 0; shell <= reach; shell++)
            for (var dx = -shell; dx <= shell; dx++)
                for (var dy = -shell; dy <= shell; dy++)
                    for (var dz = -shell; dz <= shell; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != shell)
                            continue;
                        var v = new Vector3(Step(guess.X, dx), Step(guess.Y, dy), Step(guess.Z, dz));
                        var got = forward(v);
                        if (BitConverter.SingleToInt32Bits(got.X) == BitConverter.SingleToInt32Bits(want.X)
                            && BitConverter.SingleToInt32Bits(got.Y) == BitConverter.SingleToInt32Bits(want.Y)
                            && BitConverter.SingleToInt32Bits(got.Z) == BitConverter.SingleToInt32Bits(want.Z))
                            return v;
                    }
        return null;
    }

    private static float Step(float value, int steps)
    {
        for (; steps > 0; steps--) value = MathF.BitIncrement(value);
        for (; steps < 0; steps++) value = MathF.BitDecrement(value);
        return value;
    }
}
