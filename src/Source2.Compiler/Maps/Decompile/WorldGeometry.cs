using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// The world's meshes back from the package. What survives (probe01, docs/DECOMPILE.md "World geometry"):
/// <list type="bullet">
/// <item>world_physics: one triangle soup per collision attribute and surface, every mesh of the world
/// merged, exact positions, its members' names joined; nothing per source mesh.</item>
/// <item>the world nodes: per material an aggregate model whose draw calls are the compile's visibility
/// clusters (probe01: 13 meshes of one material in 10, 2 in 27), exact positions and texcoords, packed
/// normals and tangents, after the T-junction fix and the 1/32 weld.</item>
/// </list>
/// So the source's split into meshes is gone. The world is rebuilt as one mesh per physics soup, its
/// triangles in the soup's order, each corner's material and texture coordinates taken from a render
/// vertex at exactly its position on a render triangle in its plane; a soup triangle no render one lies
/// under (a tool face) takes the soup's tool material.
/// </summary>
internal static class WorldGeometry
{
    public sealed record Mesh(List<string> Materials, List<(Vector3[] Corners, int Material, Vector2[]? Texcoords)> Faces, string Name);

    public static List<Mesh> Read(IReadOnlyDictionary<string, byte[]> files, string mapName, IReadOnlyDictionary<uint, string> toolMaterials,
                                  List<string> notes)
    {
        var result = new List<Mesh>();
        if (!files.TryGetValue($"maps/{mapName}/world_physics.vmdl_c", out var physicsBytes))
            return result;
        var render = RenderCorners(files, mapName);
        using var resource = new Resource();
        resource.Read(new MemoryStream(physicsBytes));
        var phys = resource.Blocks.FirstOrDefault(b => b.Type == BlockType.PHYS) switch
        {
            KeyValuesOrNTRO k => k.Data,
            BinaryKV3 k => k.Data.Root,
            _ => null,
        };
        static KVObject? Child(KVObject? parent, string key)
            => parent is not null && parent.IsCollection && parent.TryGetValue(key, out var child) ? child : null;
        var (matched, toolFaces, unknown) = (0, 0, 0);
        foreach (var part in Child(phys, "m_parts")?.Values ?? [])
            foreach (var shape in Child(Child(part, "m_rnShape"), "m_meshes")?.Values ?? [])
            {
                var body = Child(shape, "m_Mesh");
                var vertexBlob = Child(body, "m_Vertices")?.AsBlob().ToArray() ?? [];
                var triangleBlob = Child(body, "m_Triangles")?.AsBlob().ToArray() ?? [];
                var vertices = Enumerable.Range(0, vertexBlob.Length / 12)
                    .Select(i => new Vector3(BitConverter.ToSingle(vertexBlob, i * 12), BitConverter.ToSingle(vertexBlob, i * 12 + 4), BitConverter.ToSingle(vertexBlob, i * 12 + 8))).ToArray();
                var tool = (uint)(Child(shape, "m_nToolMaterialHash")?.ToInt64(null) ?? 0);
                var toolName = tool != 0 && toolMaterials.TryGetValue(tool, out var t) ? t : null;
                var name = Child(shape, "m_UserFriendlyName")?.ToString() ?? "";
                var materials = new List<string>();
                int MaterialIndex(string m)
                {
                    var at = materials.IndexOf(m);
                    if (at < 0)
                        materials.Add(m);
                    return at < 0 ? materials.Count - 1 : at;
                }
                var faces = new List<(Vector3[], int, Vector2[]?)>();
                for (var k = 0; k + 12 <= triangleBlob.Length; k += 12)
                {
                    Vector3[] corners = [vertices[BitConverter.ToInt32(triangleBlob, k)], vertices[BitConverter.ToInt32(triangleBlob, k + 4)], vertices[BitConverter.ToInt32(triangleBlob, k + 8)]];
                    var normal = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
                    var hits = corners.Select(c => Corner(render, c, normal)).ToArray();
                    if (hits.All(h => h is not null) && hits.Select(h => h!.Value.Material).Distinct().Count() == 1)
                    {
                        faces.Add((corners, MaterialIndex(hits[0]!.Value.Material), [.. hits.Select(h => h!.Value.Texcoord)]));
                        matched++;
                    }
                    else if (toolName is not null)
                    {
                        faces.Add((corners, MaterialIndex(toolName), null));
                        toolFaces++;
                    }
                    else
                    {
                        faces.Add((corners, MaterialIndex("materials/tools/toolsnodraw.vmat"), null));
                        unknown++;
                    }
                }
                result.Add(new Mesh(materials, faces, name));
            }
        // DECOMPILE_WORLDDEBUG=1: each physics vertex's nearest render position (how they relate).
        if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
        {
            var all = render.Keys.Select(k => new Vector3(BitConverter.Int32BitsToSingle(k.Item1), BitConverter.Int32BitsToSingle(k.Item2), BitConverter.Int32BitsToSingle(k.Item3))).ToList();
            foreach (var mesh in result.Take(1))
                foreach (var v in mesh.Faces.SelectMany(f => f.Corners).Distinct().Take(12))
                {
                    var near = all.MinBy(q => Vector3.DistanceSquared(q, v));
                    notes.Add($"world debug: physics {v:R} nearest render {near:R} off {(v - near):R}");
                }
        }
        notes.Add($"world: {result.Count} mesh(es) from the physics soups; {matched} triangles with a render face's material and texture coordinates, " +
                  $"{toolFaces} a tool material, {unknown} neither (toolsnodraw); render-only faces not rebuilt");
        return result;
    }

    /// <summary>A render corner: its material, texcoord and its triangle's (unnormalised) normal.</summary>
    private readonly record struct RenderCorner(string Material, Vector2 Texcoord, Vector3 Normal);

    /// <summary>Every render triangle's corners by exact position, from the world nodes' models.</summary>
    private static Dictionary<(int, int, int), List<RenderCorner>> RenderCorners(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        var corners = new Dictionary<(int, int, int), List<RenderCorner>>();
        foreach (var (path, bytes) in files.Where(f => f.Key.StartsWith($"maps/{mapName}/worldnodes/", StringComparison.Ordinal) && f.Key.EndsWith(".vmdl_c", StringComparison.Ordinal)))
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not Model model)
                continue;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                    {
                        var material = call.GetStringProperty("m_material") ?? "";
                        var vb = mesh.VBIB.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        var position = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase));
                        var texcoord = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == 0);
                        if (position.Format.ToString() != "R32G32B32_FLOAT" || texcoord.Format.ToString() != "R32G32_FLOAT")
                            continue;
                        Vector3 P(int v) => new(BitConverter.ToSingle(vb.Data, v * (int)vb.ElementSizeInBytes + (int)position.Offset),
                                                BitConverter.ToSingle(vb.Data, v * (int)vb.ElementSizeInBytes + (int)position.Offset + 4),
                                                BitConverter.ToSingle(vb.Data, v * (int)vb.ElementSizeInBytes + (int)position.Offset + 8));
                        Vector2 T(int v) => new(BitConverter.ToSingle(vb.Data, v * (int)vb.ElementSizeInBytes + (int)texcoord.Offset),
                                                BitConverter.ToSingle(vb.Data, v * (int)vb.ElementSizeInBytes + (int)texcoord.Offset + 4));
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = call.GetInt32Property("m_nStartIndex");
                        var count = call.GetInt32Property("m_nIndexCount");
                        var bas = call.GetInt32Property("m_nBaseVertex");
                        for (var i = start; i + 2 < start + count; i += 3)
                        {
                            int a = I(i) + bas, b = I(i + 1) + bas, c = I(i + 2) + bas;
                            var normal = Vector3.Cross(P(b) - P(a), P(c) - P(a));
                            foreach (var v in (int[])[a, b, c])
                            {
                                var key = Key(P(v));
                                if (!corners.TryGetValue(key, out var list))
                                    corners[key] = list = [];
                                list.Add(new RenderCorner(material, T(v), normal));
                            }
                        }
                    }
        }
        return corners;
    }

    /// <summary>The render corner at exactly <paramref name="at"/> on a triangle facing the same way
    /// (unit normals within 1e-4), or null.</summary>
    private static (string Material, Vector2 Texcoord)? Corner(Dictionary<(int, int, int), List<RenderCorner>> render, Vector3 at, Vector3 normal)
    {
        if (!render.TryGetValue(Key(at), out var list) || normal.LengthSquared() == 0f)
            return null;
        var n = Vector3.Normalize(normal);
        foreach (var c in list)
            if (c.Normal.LengthSquared() > 0f && Vector3.Dot(Vector3.Normalize(c.Normal), n) > 0.9999f)
                return (c.Material, c.Texcoord);
        return null;
    }

    private static (int, int, int) Key(Vector3 v) => (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
}
