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
/// triangles in the soup's order, each corner's material and texture coordinates taken from the nearest render
/// vertex within the closing weld's 1/32 on a render triangle in its plane; a soup triangle no render one lies
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
                var triangles = Enumerable.Range(0, triangleBlob.Length / 12)
                    .Select(k => (BitConverter.ToInt32(triangleBlob, k * 12), BitConverter.ToInt32(triangleBlob, k * 12 + 4), BitConverter.ToInt32(triangleBlob, k * 12 + 8))).ToList();
                // The soup is its members' meshes one after another, each in its own weld order: its connected
                // pieces, ordered by their first vertex, are written as meshes of their own in that order.
                foreach (var piece in Pieces(triangles, vertices.Length))
                {
                var materials = new List<string>();
                int MaterialIndex(string m)
                {
                    var at = materials.IndexOf(m);
                    if (at < 0)
                        materials.Add(m);
                    return at < 0 ? materials.Count - 1 : at;
                }
                // Each triangle's material and its corners' texcoords, then the faces rebuilt per material
                // (Polygons) and put in an order whose cut corners meet the vertices in the soup's order.
                var material = new int[piece.Count];
                var uv = new Dictionary<(int Triangle, int Vertex), Vector2>();
                for (var k = 0; k < piece.Count; k++)
                {
                    var (ia, ib, ic) = piece[k];
                    Vector3[] corners = [vertices[ia], vertices[ib], vertices[ic]];
                    var normal = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
                    var hits = corners.Select(c => Corner(render, c, normal)).ToArray();
                    if (hits.All(h => h is not null) && hits.Select(h => h!.Value.Material).Distinct().Count() == 1)
                    {
                        material[k] = MaterialIndex(hits[0]!.Value.Material);
                        (uv[(k, ia)], uv[(k, ib)], uv[(k, ic)]) = (hits[0]!.Value.Texcoord, hits[1]!.Value.Texcoord, hits[2]!.Value.Texcoord);
                        matched++;
                    }
                    else
                    {
                        material[k] = MaterialIndex(toolName ?? "materials/tools/toolsnodraw.vmat");
                        if (toolName is null) unknown++; else toolFaces++;
                    }
                }
                var faces = new List<(Vector3[], int, Vector2[]?)>();
                foreach (var (loop, m, cuts) in PolygonOrder(Polygons(piece, material, vertices), vertices, piece))
                {
                    Vector2[]? texcoords = null;
                    if (cuts.All(t => uv.ContainsKey((t, piece[t].A))))
                        texcoords = [.. loop.Select(v => uv[(cuts.First(t => piece[t].A == v || piece[t].B == v || piece[t].C == v), v)])];
                    faces.Add(([.. loop.Select(v => vertices[v])], m, texcoords));
                }
                result.Add(new Mesh(materials, faces, name));
                }
            }
        // DECOMPILE_WORLDDEBUG=1: each physics vertex's nearest render position (how they relate).
        if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
        {
            var all = render.Values.SelectMany(l => l.Select(c => c.Position)).Distinct().ToList();
            foreach (var mesh in result.Take(1))
                foreach (var v in mesh.Faces.SelectMany(f => f.Corners).Distinct().Take(12))
                {
                    var near = all.MinBy(q => Vector3.DistanceSquared(q, v));
                    notes.Add($"world debug: physics {v:R} nearest render {near:R} off {(v - near):R}");
                }
        }
        notes.Add($"world: {result.Count} mesh(es), the physics soups' connected pieces; {matched} triangles with a render face's material and texture coordinates, " +
                  $"{toolFaces} a tool material, {unknown} neither (toolsnodraw); render-only faces not rebuilt");
        return result;
    }

    /// <summary>A face rebuilt from soup triangles: its loop (soup vertex ids), material, the triangles it cuts
    /// into (indices into the piece) and the loop starts at which the triangulator cuts it into them.</summary>
    internal sealed record Polygon(int[] Loop, int Material, List<int> Triangles, List<int> Starts);

    /// <summary>
    /// The source's faces back, per material: triangles in one plane (unit normals within 1e-5, distances
    /// within 1e-3) that share an edge, the other way round, are merged one at a time, a merge kept only
    /// when PolygonTriangulator (the compile's own cut, on world positions as for a world mesh) cuts the
    /// merged loop, from some start, into exactly the triangles merged, so nothing is guessed: a face the
    /// compile would cut otherwise stays split.
    /// </summary>
    internal static List<Polygon> Polygons(List<(int A, int B, int C)> piece, int[] material, Vector3[] vertices)
    {
        static (int, int, int) Norm((int, int, int) t)
        {
            var (a, b, c) = t;
            return a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
        }
        var polygons = new List<Polygon>();
        for (var k = 0; k < piece.Count; k++)
            polygons.Add(new Polygon([piece[k].A, piece[k].B, piece[k].C], material[k], [k], [0, 1, 2]));
        Vector3 Normal(int k) => Vector3.Normalize(Vector3.Cross(vertices[piece[k].B] - vertices[piece[k].A], vertices[piece[k].C] - vertices[piece[k].A]));
        bool SamePlane(int k, int j)
        {
            var (n, m) = (Normal(k), Normal(j));
            return Vector3.Dot(n, m) > 1 - 1e-5f && MathF.Abs(Vector3.Dot(n, vertices[piece[k].A]) - Vector3.Dot(n, vertices[piece[j].A])) < 1e-3f;
        }
        List<int> StartsCutting(int[] loop, HashSet<(int, int, int)> want)
        {
            var starts = new List<int>();
            for (var r = 0; r < loop.Length; r++)
            {
                var rotated = loop.Skip(r).Concat(loop.Take(r)).ToArray();
                var cut = PolygonTriangulator.Triangulate([.. rotated.Select(v => vertices[v])]);
                if (cut.Length != (loop.Length - 2) * 3)
                    continue;
                var got = new HashSet<(int, int, int)>();
                for (var i = 0; i < cut.Length; i += 3)
                    got.Add(Norm((rotated[cut[i]], rotated[cut[i + 1]], rotated[cut[i + 2]])));
                if (got.SetEquals(want))
                    starts.Add(r);
            }
            return starts;
        }
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < polygons.Count && !merged; i++)
                for (var j = i + 1; j < polygons.Count && !merged; j++)
                {
                    var (p, q) = (polygons[i], polygons[j]);
                    if (p.Material != q.Material || !SamePlane(p.Triangles[0], q.Triangles[0]))
                        continue;
                    // A shared edge a -> b in p, b -> a in q: q's other corners go in between.
                    for (var x = 0; x < p.Loop.Length && !merged; x++)
                    {
                        int a = p.Loop[x], b = p.Loop[(x + 1) % p.Loop.Length];
                        var y = Array.IndexOf(q.Loop, b);
                        if (y < 0 || q.Loop[(y + 1) % q.Loop.Length] != a)
                            continue;
                        var between = Enumerable.Range(2, q.Loop.Length - 2).Select(s => q.Loop[(y + s) % q.Loop.Length]).ToArray();
                        if (between.Any(v => p.Loop.Contains(v)))
                            continue;
                        var loop = p.Loop.Take(x + 1).Concat(between).Concat(p.Loop.Skip(x + 1)).ToArray();
                        var want = p.Triangles.Concat(q.Triangles).Select(t => Norm(piece[t])).ToHashSet();
                        var starts = StartsCutting(loop, want);
                        if (starts.Count == 0)
                            continue;
                        polygons[i] = new Polygon(loop, p.Material, [.. p.Triangles, .. q.Triangles], starts);
                        polygons.RemoveAt(j);
                        merged = true;
                    }
                }
        }
        return polygons;
    }

    /// <summary>
    /// The faces in an order (each from one of its cutting starts) whose cut corners, in the triangulator's
    /// order, meet the piece's vertices in the soup's order (<see cref="FirstUseOrder"/> over faces): for
    /// each vertex in turn, the first unused face holding it with a start that brings its new vertices in
    /// ascending order and no other new one; then every face whose corners are all met. A face no step
    /// reaches goes at the end. Returns each face's loop from its start.
    /// </summary>
    internal static List<(int[] Loop, int Material, List<int> Triangles)> PolygonOrder(List<Polygon> polygons, Vector3[] vertices,
                                                                                       List<(int A, int B, int C)> piece)
    {
        static (int, int, int) Norm((int, int, int) t)
        {
            var (a, b, c) = t;
            return a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
        }
        var order = new List<(int[], int, List<int>)>();
        var seen = new HashSet<int>();
        var done = new bool[polygons.Count];
        int[] Cut(int[] loop, int start)
        {
            var rotated = loop.Skip(start).Concat(loop.Take(start)).ToArray();
            var cut = loop.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate([.. rotated.Select(v => vertices[v])]);
            return [.. cut.Select(i => rotated[i])];
        }
        void Emit(int f, int start)
        {
            done[f] = true;
            var p = polygons[f];
            order.Add(([.. p.Loop.Skip(start).Concat(p.Loop.Take(start))], p.Material, p.Triangles));
            foreach (var v in p.Loop)
                seen.Add(v);
        }
        // Among the starts that do, the one cutting the face's triangles in their shipped order (the soup's
        // order, which the BVH keeps within a leaf, and a face's triangles share one): the fewest
        // inversions of their indices, the first on a tie.
        int Inversions(Polygon p, int start)
        {
            var cut = Cut(p.Loop, start);
            var indices = new List<int>();
            for (var i = 0; i + 2 < cut.Length; i += 3)
            {
                var key = Norm((cut[i], cut[i + 1], cut[i + 2]));
                indices.Add(p.Triangles.FirstOrDefault(t => Norm(piece[t]) == key, -1));
            }
            var count = 0;
            for (var i = 0; i < indices.Count; i++)
                for (var j = i + 1; j < indices.Count; j++)
                    if (indices[i] > indices[j])
                        count++;
            return count;
        }
        var all = polygons.SelectMany(p => p.Loop).Distinct().Order().ToList();
        foreach (var next in all)
        {
            if (seen.Contains(next))
                continue;
            for (var f = 0; f < polygons.Count; f++)
            {
                if (done[f] || !polygons[f].Loop.Contains(next))
                    continue;
                var at = polygons[f].Starts.Where(s =>
                {
                    var fresh = Cut(polygons[f].Loop, s).Where(v => !seen.Contains(v)).Distinct().ToList();
                    var expected = all.SkipWhile(v => v != next).Take(fresh.Count).ToList();
                    return fresh.Count > 0 && fresh.SequenceEqual(expected);
                }).OrderBy(s => Inversions(polygons[f], s)).DefaultIfEmpty(-1).First();
                if (at < 0)
                    continue;
                Emit(f, at);
                for (var g = 0; g < polygons.Count; g++)
                    if (!done[g] && polygons[g].Loop.All(seen.Contains))
                        Emit(g, polygons[g].Starts.OrderBy(x => Inversions(polygons[g], x)).First());
                break;
            }
        }
        for (var f = 0; f < polygons.Count; f++)
            if (!done[f])
                Emit(f, polygons[f].Starts.OrderBy(x => Inversions(polygons[f], x)).First());
        return order;
    }

    /// <summary>The soup's connected pieces (triangles sharing a vertex), ordered by their lowest vertex, each
    /// in <see cref="FirstUseOrder"/> over its own vertices (soup indices kept).</summary>
    internal static List<List<(int A, int B, int C)>> Pieces(List<(int A, int B, int C)> triangles, int vertexCount)
    {
        var parent = Enumerable.Range(0, vertexCount).ToArray();
        int Find(int v) { while (parent[v] != v) v = parent[v] = parent[parent[v]]; return v; }
        foreach (var (a, b, c) in triangles)
        {
            parent[Find(b)] = Find(a);
            parent[Find(c)] = Find(a);
        }
        var groups = triangles.GroupBy(t => Find(t.A)).Select(g => g.ToList())
            .OrderBy(g => g.Min(t => Math.Min(t.A, Math.Min(t.B, t.C)))).ToList();
        var result = new List<List<(int, int, int)>>();
        foreach (var g in groups)
        {
            // Local numbering in soup order, so the piece's first vertex is its own 0.
            var used = g.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Order().ToList();
            var local = used.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            result.Add(g);
        }
        return result;
    }

    /// <summary>
    /// The soup's triangles in an order (each rotated, winding kept) whose first use of their corners is the
    /// soup's vertex order: RnMeshCreate keeps its input vertices in weld order (RnMeshBuilder), so a mesh
    /// whose faces meet the vertices in that order gives it back. For each vertex in turn, the first unused
    /// triangle (shipped order) holding it whose rotation brings its new vertices in ascending order and no
    /// other new one; then every triangle whose corners are all met. A triangle no such step reaches goes at
    /// the end, as shipped.
    /// </summary>
    internal static List<(int A, int B, int C)> FirstUseOrder(List<(int A, int B, int C)> triangles, int vertexCount)
    {
        var holding = new List<int>[vertexCount];
        for (var t = 0; t < triangles.Count; t++)
            foreach (var v in (int[])[triangles[t].A, triangles[t].B, triangles[t].C])
                (holding[v] ??= []).Add(t);
        var seen = new bool[vertexCount];
        var done = new bool[triangles.Count];
        var result = new List<(int, int, int)>(triangles.Count);
        void Emit(int t, (int, int, int) rotated)
        {
            done[t] = true;
            result.Add(rotated);
            foreach (var v in (int[])[rotated.Item1, rotated.Item2, rotated.Item3])
                seen[v] = true;
        }
        void Covered(int v)
        {
            foreach (var t in holding[v] ?? [])
                if (!done[t] && seen[triangles[t].A] && seen[triangles[t].B] && seen[triangles[t].C])
                    Emit(t, triangles[t]);
        }
        for (var next = 0; next < vertexCount; next++)
        {
            if (seen[next])
                continue;
            foreach (var t in holding[next] ?? [])
            {
                if (done[t])
                    continue;
                var (a, b, c) = triangles[t];
                (int, int, int)? pick = null;
                foreach (var r in ((int, int, int)[])[(a, b, c), (b, c, a), (c, a, b)])
                {
                    var fresh = new[] { r.Item1, r.Item2, r.Item3 }.Where(v => !seen[v]).Distinct().ToList();
                    if (fresh.Count > 0 && fresh.SequenceEqual(Enumerable.Range(next, fresh.Count)))
                    {
                        pick = r;
                        break;
                    }
                }
                if (pick is { } p)
                {
                    Emit(t, p);
                    foreach (var v in (int[])[p.Item1, p.Item2, p.Item3])
                        Covered(v);
                    break;
                }
            }
        }
        for (var t = 0; t < triangles.Count; t++)
            if (!done[t])
                result.Add(triangles[t]);
        return result;
    }

    /// <summary>A render corner: its material, texcoord and its triangle's (unnormalised) normal.</summary>
    private readonly record struct RenderCorner(Vector3 Position, string Material, Vector2 Texcoord, Vector3 Normal);

    /// <summary>Every render triangle's corners by 1/32 cell, from the world nodes' models.</summary>
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
                                var key = Cell(P(v));
                                if (!corners.TryGetValue(key, out var list))
                                    corners[key] = list = [];
                                list.Add(new RenderCorner(P(v), material, T(v), normal));
                            }
                        }
                    }
        }
        return corners;
    }

    /// <summary>The nearest render corner within the closing weld's 1/32 of <paramref name="at"/> on a triangle
    /// facing the same way (unit normals within 1e-4), or null. The render positions are that weld's
    /// representatives: another mesh's corner nearby wins (probe01: -256 where the soup has -256.00006,
    /// a mesh at origin 511.99994 against one at 512).</summary>
    private static (string Material, Vector2 Texcoord)? Corner(Dictionary<(int, int, int), List<RenderCorner>> render, Vector3 at, Vector3 normal)
    {
        if (normal.LengthSquared() == 0f)
            return null;
        var n = Vector3.Normalize(normal);
        var (cx, cy, cz) = Cell(at);
        RenderCorner? best = null;
        var bestDistance = float.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                    foreach (var c in render.GetValueOrDefault((cx + dx, cy + dy, cz + dz)) ?? [])
                        if (Vector3.DistanceSquared(c.Position, at) is var d && d <= Weld * Weld && d < bestDistance
                            && c.Normal.LengthSquared() > 0f && Vector3.Dot(Vector3.Normalize(c.Normal), n) > 0.9999f)
                            (best, bestDistance) = (c, d);
        return best is { } b ? (b.Material, b.Texcoord) : null;
    }

    private const float Weld = 1f / 32f;

    private static (int, int, int) Cell(Vector3 v) => ((int)MathF.Floor(v.X / Weld), (int)MathF.Floor(v.Y / Weld), (int)MathF.Floor(v.Z / Weld));
}
