using System.Globalization;
using System.Numerics;
using Source2.Compiler.Io;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Models;

/// <summary>
/// A model DMX's render meshes as the model compiler loads them (ModelCompile_LoadAndMergeMeshes,
/// resourcecompiler 180344980, 20260923): FUN_180d49a80 walks the DmeModel (FUN_180d49d20) and hands
/// each DmeDag's DmeMesh to DmeMeshToCMesh (180d47b90) with the options record it builds
/// (+0x84 field mask -1, +0x89 0, +0x8a 0, +0x8b 1, +0x8c 1, +0x8d 0).
/// <list type="bullet">
/// <item>The fields are the bind state's vertexFormat entries that hold data, in that order
/// (180d463b0, mask -1 takes all thirteen standard slots and the rest); a field <c>name$k</c> is the
/// stream <c>name</c> with semantic index k (FUN_18189a2b0 / FUN_18189a200), its float count from the
/// attribute type (180d473d0: Vector2 2, Vector3 and QAngle 3, Vector4, Quaternion and Color 4).
/// A texcoord stream is high precision when any of its values has |u| or |v| over 16 (180d473d0).</item>
/// <item>+0x8a 0: every triangle corner is a vertex of its own, in face order; the indices count up.
/// A face of three corners is copied as listed (FUN_1818bde50); a longer one is triangulated.</item>
/// <item>Each corner's value of a field is the field's data at its index list's entry for the face
/// vertex (a field without an index list takes the position's). 180d46590 copies it: the first
/// position-named Vector3 through the DmeDag's matrix (Matrix3x4_TransformPoint) times the
/// import scale; another direction (a name with position, bentnormal or normal; or tangentu,
/// tangentv or tangent unless speed, balance or wrinkle comes first) turned by the matrix
/// (Matrix3x4_Rotate) and renormalised, a Vector4's w kept; a Vector2's v flipped (1 - v) when the
/// vertex data's flipVCoordinates is set; anything else as stored.</item>
/// <item>+0x8b 1: one DmeMesh's face-set meshes are grouped by "&lt;material&gt;&lt;%f of the lightmap scale&gt;"
/// in a case-insensitive CUtlSymbolTable (FUN_180d46130(.., 1, 0, 0x20)); each group's later
/// meshes are appended to its first (FUN_1812d6c70), in first-use order.</item>
/// </list>
/// Not ported yet, and refused rather than approximated: skinned meshes (jointCount, blend weights),
/// delta states (morphs), polygons of more than three corners (FUN_18136f810's triangulation),
/// DmeDag children of a DmeDag (FUN_180d49bb0), and a DmeDag or bind transform that is not the
/// identity (FUN_1818c0150, FUN_181258890 on non-trivial matrices).
/// </summary>
internal static class DmxModelMeshes
{
    /// <summary>One CMesh: its name (the DmeMesh's), material, lightmap scale and the corners.</summary>
    public sealed record Mesh(string Name, string Material, float LightmapScale, WorldNodeEntry Entry);

    /// <summary>The meshes of <paramref name="doc"/>, a model DMX, at <paramref name="scale"/> (the RenderMeshFile's import_scale).</summary>
    public static List<Mesh> Load(DmxBinary.Document doc, float scale)
    {
        var root = doc.Root ?? throw new InvalidDataException("The DMX has no root element.");
        var model = root.Get<DmxBinary.Element>("model") ?? throw new InvalidDataException("The DMX root has no model.");
        var joints = model.GetElements("jointList").ToList();
        // FUN_1818b59f0: the model's base state named "bind" (V_stricmp_fast).
        var bind = model.GetElements("baseStates").FirstOrDefault(b => Io.Tier0Strings.EqualsIgnoreCase(b.Name, "bind"))?.GetElements("transforms").ToList() ?? [];
        var meshes = new List<Mesh>();
        foreach (var dag in model.GetElements("children"))
        {
            // FUN_180d49d20: a joint's matrix is its bind transform's, anything else its own transform's.
            var j = joints.IndexOf(dag);
            var transform = j >= 0 && j < bind.Count ? bind[j] : dag.Get<DmxBinary.Element>("transform");
            RequireIdentity(transform, dag.Name);
            if (dag.GetElements("children").Any())
                throw new NotSupportedException($"DmeDag '{dag.Name}' has children; FUN_180d49bb0 is not ported yet.");
            // The grouping table is DmeMeshToCMesh's own (a local), so face sets merge within one DmeMesh.
            if (dag.Get<DmxBinary.Element>("shape") is { Type: "DmeMesh" } mesh)
                meshes.AddRange(Group([.. FromMesh(mesh, scale)]));
        }
        return meshes;
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    private static void RequireIdentity(DmxBinary.Element? transform, string dag)
    {
        if (transform == null)
            return;
        var p = transform.GetValue<Vector3>("position") ?? Vector3.Zero;
        var q = transform.GetValue<Quaternion>("orientation") ?? Quaternion.Identity;
        if (p != Vector3.Zero || q != Quaternion.Identity)
            throw new NotSupportedException($"DmeDag '{dag}' is not at the identity; the DmeTransform to matrix path is not ported yet.");
    }

    private sealed record Field(string Name, int Index, int Count, byte Type, object?[] Data, int[]? Indices);

    private static IEnumerable<Mesh> FromMesh(DmxBinary.Element mesh, float scale)
    {
        // DmeMeshToCMeshes / FUN_180d47970: the mesh's base state named "bind" (FUN_1818be180, V_stricmp_fast).
        var state = mesh.GetElements("baseStates").FirstOrDefault(b => Io.Tier0Strings.EqualsIgnoreCase(b.Name, "bind"))
                    ?? throw new InvalidDataException($"DmeMesh '{mesh.Name}' has no base state named bind.");
        if (mesh.GetElements("deltaStates").Any())
            throw new NotSupportedException($"DmeMesh '{mesh.Name}' has delta states (morphs); not ported yet.");
        if (state.GetValue<int>("jointCount") is > 0)
            throw new NotSupportedException($"DmeMesh '{mesh.Name}' is skinned; skinned meshes are not ported yet.");
        var fields = new List<Field>();
        foreach (var name in state.Get<object?[]>("vertexFormat")?.OfType<string>() ?? [])
        {
            if (state.Attributes.GetValueOrDefault(name) is not object?[] data)
                continue;
            var type = AttributeType(data);
            var count = type switch
            {
                0x22 or 0x23 or 0x27 => 1,
                0x29 => 2,
                0x2a or 0x2c => 3,
                0x28 or 0x2b or 0x2d => 4,
                0x2e => 16,
                _ => 0,
            };
            var indices = (state.Attributes.GetValueOrDefault(name + "Indices") as object?[])?.Select(x => (int)x!).ToArray();
            var dollar = name.IndexOf('$', StringComparison.Ordinal);
            var stream = dollar >= 0 ? name[..dollar] : name;
            var semantic = dollar >= 0 && int.TryParse(name[(dollar + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : 0;
            fields.Add(new Field(stream, semantic, count, type, data, indices));
        }

        var position = fields.FindIndex(f => f.Name.ContainsAscii("position"));
        if (position < 0)
            throw new InvalidDataException($"DmeMesh '{mesh.Name}' has no position field.");
        var flipV = state.GetValue<bool>("flipVCoordinates") == true;
        var stride = fields.Sum(f => f.Count);
        var streams = fields.Select(f => new WrbMeshEntry.Stream(f.Name, f.Index, f.Count,
            (byte)(Io.Tier0Strings.EqualsIgnoreCase(f.Name, "texcoord") && f.Data.Any(v => v is Vector2 t && (MathF.Abs(t.X) > 16f || MathF.Abs(t.Y) > 16f)) ? 1 : 0), f.Type)).ToList();

        foreach (var faceSet in mesh.GetElements("faceSets"))
        {
            var faces = faceSet.Get<object?[]>("faces")?.Select(x => (int)x!).ToArray() ?? [];
            var corners = new List<int>();
            for (var at = 0; at < faces.Length;)
            {
                var n = 0;
                while (at + n < faces.Length && faces[at + n] != -1)
                    n++;
                if (n == 3)
                    corners.AddRange(faces.AsSpan(at, 3));
                else if (n > 3)
                    throw new NotSupportedException($"DmeMesh '{mesh.Name}' has a face of {n} corners; polygon triangulation (FUN_18136f810) is not ported yet.");
                at += n + 1;
            }
            if (corners.Count == 0)
                continue;
            var vertices = new float[corners.Count * stride];
            for (var c = 0; c < corners.Count; c++)
            {
                var to = c * stride;
                for (var f = 0; f < fields.Count; f++)
                {
                    var field = fields[f];
                    var lookup = field.Indices ?? fields[position].Indices;
                    var value = lookup != null ? lookup[corners[c]] : corners[c];
                    Copy(field, value, f == position, flipV, scale, vertices.AsSpan(to, field.Count));
                    to += field.Count;
                }
            }
            var material = faceSet.Get<DmxBinary.Element>("material")?.Get<string>("mtlName") ?? "";
            material = Path.ChangeExtension(material, ".vmat").Replace('\\', '/');
            var facts = new WrbMeshEntry { Material = material, Field28 = Vector4.One, Stride = stride, Streams = streams };
            yield return new Mesh(mesh.Name, material, 1f, new WorldNodeEntry(material, stride, vertices, [.. Enumerable.Range(0, corners.Count)], facts));
        }
    }

    // The attribute type as the datamodel numbers it: the DMX array type (scalar + 32).
    private static byte AttributeType(object?[] data) => data.FirstOrDefault() switch
    {
        int => 0x22,
        float => 0x23,
        TimeSpan => 0x27,
        byte[] { Length: 4 } => 0x28,
        Vector2 => 0x29,
        Vector3 => 0x2a,
        Vector4 => 0x2b,
        Quaternion => 0x2d,
        Matrix4x4 => 0x2e,
        _ => 0,
    };

    // 180d46590 with the identity matrix (the only one Load accepts so far).
    private static void Copy(Field field, int index, bool isPosition, bool flipV, float scale, Span<float> to)
    {
        var value = field.Data[index];
        var direction = isPosition || WorldNodeModels.DmeDirection(field.Name);
        switch (value)
        {
            case Vector2 t:
                to[0] = t.X;
                to[1] = flipV ? 1f - t.Y : t.Y;
                break;
            case Vector3 v when !direction:
                (to[0], to[1], to[2]) = (v.X, v.Y, v.Z);
                break;
            case Vector3 v when isPosition:
                var p = MeshVectors.TransformPoint(Identity, v);
                (to[0], to[1], to[2]) = (p.X * scale, p.Y * scale, p.Z * scale);
                break;
            case Vector3 v:
                var d = MeshVectors.Normalise(MeshVectors.Rotate(Identity, v));
                (to[0], to[1], to[2]) = (d.X, d.Y, d.Z);
                break;
            case Vector4 v when direction:
                var r = MeshVectors.Normalise(MeshVectors.Rotate(Identity, new Vector3(v.X, v.Y, v.Z)));
                (to[0], to[1], to[2], to[3]) = (r.X, r.Y, r.Z, v.W);
                break;
            case Vector4 v:
                (to[0], to[1], to[2], to[3]) = (v.X, v.Y, v.Z, v.W);
                break;
            case Quaternion q:
                (to[0], to[1], to[2], to[3]) = (q.X, q.Y, q.Z, q.W);
                break;
            case byte[] { Length: 4 } c:
                for (var k = 0; k < 4; k++)
                    to[k] = c[k] / 255f;
                break;
            case float f:
                to[0] = f;
                break;
            default:
                throw new NotSupportedException($"Vertex field '{field.Name}' of type {field.Type:x} is not ported yet.");
        }
    }

    // DmeMeshToCMesh's +0x8b grouping: "<material><%f>" case-insensitively, first use first, later meshes appended.
    private static List<Mesh> Group(List<Mesh> meshes)
    {
        var groups = new List<(string Key, List<Mesh> Members)>();
        foreach (var m in meshes)
        {
            var key = m.Material + m.LightmapScale.ToString("F6", CultureInfo.InvariantCulture);
            var g = groups.FindIndex(x => Io.Tier0Strings.EqualsIgnoreCase(x.Key, key));
            if (g < 0)
                groups.Add((key, [m]));
            else
                groups[g].Members.Add(m);
        }
        return [.. groups.Select(g => g.Members.Skip(1).Aggregate(g.Members[0], Append))];
    }

    /// <summary>
    /// CMesh_Append (FUN_1812d6c70, keepIndices 0): each of the added mesh's streams is matched to
    /// the mesh's by semantic index and name (V_stricmp_fast); when every stream lands at its own
    /// offset the added vertices are copied as one block, and its indices are offset by the
    /// vertices before them. The face sets of one DmeMesh always share their streams; meshes whose
    /// streams differ (new streams added, values copied stream by stream) are refused for now.
    /// </summary>
    private static Mesh Append(Mesh to, Mesh add)
    {
        var (a, b) = (to.Entry.Facts.Streams, add.Entry.Facts.Streams);
        if (a.Count != b.Count || a.Zip(b).Any(p => p.First.Index != p.Second.Index || p.First.Count != p.Second.Count
                                                   || !Io.Tier0Strings.EqualsIgnoreCase(p.First.Name, p.Second.Name)))
            throw new NotSupportedException("Meshes with different streams share a material; CMesh_Append's stream-by-stream copy is not ported yet.");
        var count = to.Entry.Vertices.Length / to.Entry.Stride;
        var e = to.Entry with
        {
            Vertices = [.. to.Entry.Vertices, .. add.Entry.Vertices],
            Indices = [.. to.Entry.Indices, .. add.Entry.Indices.Select(i => i + count)],
        };
        return to with { Entry = e };
    }
}
