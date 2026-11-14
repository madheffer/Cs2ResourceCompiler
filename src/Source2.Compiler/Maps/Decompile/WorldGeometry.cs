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

    /// <param name="surfaceOf">A material's surface property hash (its physics); with it a face the render
    /// does not show takes a render material of the surface the soup gives its triangle.</param>
    public static List<Mesh> Read(IReadOnlyDictionary<string, byte[]> files, string mapName, IReadOnlyDictionary<uint, string> toolMaterials,
                                  List<string> notes, Func<string, uint>? surfaceOf = null, Func<uint, string?>? surfaceName = null,
                                  IReadOnlySet<((int, int, int), (int, int, int), (int, int, int))>? propTriangles = null,
                                  Func<string, IReadOnlyList<string>, string?>? toolFor = null)
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
        var (matched, toolFaces, unknown, bySurface) = (0, 0, 0, 0);
        var surfaceHashes = Child(phys, "m_surfacePropertyHashes")?.Values.Select(v => (uint)v.ToInt64(null)).ToArray() ?? [];
        var renderMaterials = render.Values.SelectMany(l => l.Select(c => c.Material)).Distinct().Order(StringComparer.Ordinal).ToList();
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
                var allTriangles = Enumerable.Range(0, triangleBlob.Length / 12)
                    .Select(k => (A: BitConverter.ToInt32(triangleBlob, k * 12), B: BitConverter.ToInt32(triangleBlob, k * 12 + 4), C: BitConverter.ToInt32(triangleBlob, k * 12 + 8))).ToList();
                // Triangles a prop_static's mesh physics gives (StaticProps.FindMeshProps) are the prop's, not the
                // world's: left out, each kept triangle remembering its soup index for its per-triangle surface.
                var soupIndex = Enumerable.Range(0, allTriangles.Count)
                    .Where(k => propTriangles is null || !propTriangles.Contains(StaticProps.TriangleKey(vertices[allTriangles[k].A], vertices[allTriangles[k].B], vertices[allTriangles[k].C])))
                    .ToArray();
                var triangles = soupIndex.Select(k => allTriangles[k]).ToList();
                if (triangles.Count == 0)
                    continue;
                // A triangle nothing draws, in a shape with no tool material: the tool material whose collision is the
                // shape's own (group and interact-as, read from the game's tool materials: clip, window...), else
                // toolsnodraw, whose collision is the default's.
                string? attributeTool = null;
                if (toolName is null && toolFor is not null)
                {
                    var attribute = Child(phys, "m_collisionAttributes")?.Values.ElementAtOrDefault((int)(Child(shape, "m_nCollisionAttributeIndex")?.ToInt64(null) ?? -1));
                    var group = Child(attribute, "m_CollisionGroupString")?.ToString() ?? "";
                    var interactAs = Child(attribute, "m_InteractAsStrings")?.Values.Select(v => v.ToString() ?? "").ToList() ?? [];
                    if (group.Length > 0 && !group.Equals("default", StringComparison.OrdinalIgnoreCase) || interactAs.Count > 0)
                        attributeTool = toolFor(group, interactAs);
                }
                // Each triangle's material and its corners' texcoords (from the render), soup-wide.
                var soupMaterial = new string[triangles.Count];
                var uv = new Dictionary<(int Triangle, int Vertex), Vector2>();
                for (var k = 0; k < triangles.Count; k++)
                {
                    var (ia, ib, ic) = triangles[k];
                    Vector3[] corners = [vertices[ia], vertices[ib], vertices[ic]];
                    var normal = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
                    var hits = corners.Select(c => Corner(render, c, normal)).ToArray();
                    if (hits.All(h => h is not null) && hits.Select(h => h!.Value.Material).Distinct().Count() == 1)
                    {
                        soupMaterial[k] = hits[0]!.Value.Material;
                        (uv[(k, ia)], uv[(k, ib)], uv[(k, ic)]) = (hits[0]!.Value.Texcoord, hits[1]!.Value.Texcoord, hits[2]!.Value.Texcoord);
                        matched++;
                    }
                    else
                    {
                        soupMaterial[k] = toolName ?? attributeTool ?? "materials/tools/toolsnodraw.vmat";
                        if (toolName is null && attributeTool is null) unknown++; else toolFaces++;
                    }
                }
                // A face the render does not show (hidden against another, or render-only cut away) still
                // has its surface in the soup (m_Materials per triangle, else the shape's): it takes a
                // render material of that surface, a neighbour's across an edge first, else the only one
                // the map's render has. Its physics is the material's surface and settings, the same for
                // any material of that surface the map uses.
                if (surfaceOf is not null && toolName is null && attributeTool is null)
                {
                    var perTriangle = Child(body, "m_Materials")?.Values.Select(v => (int)v.ToInt64(null)).ToArray();
                    var shapeSurface = (int)(Child(shape, "m_nSurfacePropertyIndex")?.ToInt64(null) ?? 0);
                    uint? SurfaceAt(int k) => (perTriangle is { } pt && soupIndex[k] < pt.Length ? pt[soupIndex[k]] : shapeSurface) is var i && i >= 0 && i < surfaceHashes.Length ? surfaceHashes[i] : null;
                    var byEdge = new Dictionary<(int, int), List<int>>();
                    for (var k = 0; k < triangles.Count; k++)
                        foreach (var (x, y) in (ValueTuple<int, int>[])[(triangles[k].A, triangles[k].B), (triangles[k].B, triangles[k].C), (triangles[k].C, triangles[k].A)])
                        {
                            var key = (Math.Min(x, y), Math.Max(x, y));
                            if (!byEdge.TryGetValue(key, out var list))
                                byEdge[key] = list = [];
                            list.Add(k);
                        }
                    var shown = Enumerable.Range(0, triangles.Count).Select(k => uv.ContainsKey((k, triangles[k].A))).ToArray();
                    var surfaceCache = new Dictionary<string, uint>(StringComparer.Ordinal);
                    uint Surface(string m) => surfaceCache.TryGetValue(m, out var h) ? h : surfaceCache[m] = surfaceOf(m);
                    // Neighbours first, spreading through faces resolved here.
                    var pending = new Queue<int>(Enumerable.Range(0, triangles.Count).Where(k => !shown[k]));
                    var resolved = new bool[triangles.Count];
                    var stalls = 0;
                    while (pending.Count > 0 && stalls <= pending.Count)
                    {
                        var k = pending.Dequeue();
                        if (SurfaceAt(k) is not { } want)
                            continue;
                        var (a, b, c) = triangles[k];
                        var neighbour = ((ValueTuple<int, int>[])[(a, b), (b, c), (c, a)])
                            .SelectMany(e => byEdge[(Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))])
                            .Where(n => n != k && (shown[n] || resolved[n]) && Surface(soupMaterial[n]) == want)
                            .Select(n => soupMaterial[n]).FirstOrDefault();
                        var only = renderMaterials.Where(m => Surface(m) == want).Take(2).ToList();
                        var pick = neighbour ?? (only.Count == 1 ? only[0] : null);
                        if (pick is null)
                        {
                            pending.Enqueue(k);
                            stalls++;
                            continue;
                        }
                        (soupMaterial[k], resolved[k], stalls) = (pick, true, 0);
                        (bySurface, unknown) = (bySurface + 1, unknown - 1);
                    }
                    if (DecompileProfile.On)
                        foreach (var k in pending)
                        {
                            var want = SurfaceAt(k);
                            var candidates = want is { } w ? renderMaterials.Count(m => Surface(m) == w) : -1;
                            var hit = new[] { triangles[k].A, triangles[k].B, triangles[k].C }
                                .Count(v => Corner(render, vertices[v], Vector3.Cross(vertices[triangles[k].B] - vertices[triangles[k].A], vertices[triangles[k].C] - vertices[triangles[k].A])) is not null);
                            DecompileProfile.Count($"world.unresolved {(want is null ? "no surface" : candidates == 0 ? "no render material of its surface" : "several of its surface, no neighbour")} corners-on-render {hit}");
                        }
                    if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
                    {
                        var attributeIndex = (int)(Child(shape, "m_nCollisionAttributeIndex")?.ToInt64(null) ?? -1);
                        var attribute = Child(phys, "m_collisionAttributes")?.Values.ElementAtOrDefault(attributeIndex);
                        notes.Add($"world debug: soup {name}: {triangles.Count} triangles, {pending.Count} unresolved, tool {toolName ?? "-"}, attribute {attributeIndex}: " +
                                  $"group {Child(attribute, "m_CollisionGroupString")} as [{string.Join(",", Child(attribute, "m_InteractAsStrings")?.Values.Select(v => v.ToString()) ?? [])}] " +
                                  $"with [{string.Join(",", Child(attribute, "m_InteractWithStrings")?.Values.Select(v => v.ToString()) ?? [])}]");
                    }
                    if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
                        notes.Add($"world debug: surfaces wanted {string.Join(" ", pending.Select(SurfaceAt).Distinct())}; table {string.Join(" ", surfaceHashes.Select(h => $"{h}={surfaceName?.Invoke(h)}"))}; wanted counts {string.Join(" ", pending.GroupBy(SurfaceAt).Select(g => $"{surfaceName?.Invoke(g.Key ?? 0)}:{g.Count()}"))}; per-triangle {perTriangle?.Length ?? -1} shape {shapeSurface}; render {string.Join(" ", renderMaterials.Select(m => $"{Path.GetFileNameWithoutExtension(m)}={Surface(m)}"))}");
                }
                // The soup is its members' pieces one after another, each in its own weld order (Members),
                // each written as a mesh of its own in that order.
                var split = Members(triangles, soupMaterial, vertices.Length);
                if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
                {
                    notes.Add($"world debug: members {string.Join(" ", split.Select(m => $"{m.First}:{m.Triangles.Count}"))}");
                    for (var v = 0; v < Math.Min(12, vertices.Length); v++)
                        notes.Add($"world debug: vertex {v} {vertices[v]:R} in triangles {string.Join(",", Enumerable.Range(0, triangles.Count).Where(t => triangles[t].Item1 == v || triangles[t].Item2 == v || triangles[t].Item3 == v).Take(6).Select(t => $"{triangles[t]}"))}");
                }
                // Each member's faces in order (soup triangle indices), then the soup's order repaired through
                // the BVH (Repair), then each member written as a mesh.
                var ordered = new List<List<Face>>();
                foreach (var (member, first) in split)
                {
                    List<(int A, int B, int C)> piece = [.. member.Select(k => triangles[k])];
                    var material = new int[piece.Count];
                    ordered.Add([.. PolygonOrder(Polygons(piece, material, vertices), vertices, piece, first)
                        .Select(f => new Face(f.Loop, [.. f.Triangles.Select(t => member[t])], [.. f.Starts]))]);
                }
                // DECOMPILE_WORLD_SEARCH=1: the BVH-driven repair (Repair); off by default, as it does not
                // converge yet and costs a full RnMeshCreate per try (docs/DECOMPILE.md "World geometry").
                var repaired = Environment.GetEnvironmentVariable("DECOMPILE_WORLD_SEARCH") == "1" ? Repair(ordered, triangles, vertices, notes) : null;
                foreach (var faces in ordered)
                {
                    var materials = new List<string> { soupMaterial[faces[0].Triangles[0]] };
                    var written = new List<(Vector3[], int, Vector2[]?)>();
                    foreach (var f in faces)
                    {
                        Vector2[]? texcoords = null;
                        if (f.Triangles.All(t => uv.ContainsKey((t, triangles[t].A))))
                            texcoords = [.. f.Loop.Select(v => uv[(f.Triangles.First(t => triangles[t].A == v || triangles[t].B == v || triangles[t].C == v), v)])];
                        written.Add(([.. f.Loop.Select(v => vertices[v])], 0, texcoords));
                    }
                    result.Add(new Mesh(materials, written, name));
                }
                if (repaired is { } r)
                    notes.Add($"world: soup of {triangles.Count} triangles: {r}");
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
                  $"{toolFaces} a tool material, {bySurface} by their surface (no render face), {unknown} neither (toolsnodraw); render-only faces not rebuilt");
        return result;
    }

    /// <summary>A face as written: its loop from its start (soup vertex ids), the soup triangles it cuts into,
    /// and its loop from every start that cuts it into them.</summary>
    internal sealed record Face(int[] Loop, List<int> Triangles, List<int[]> Starts)
    {
        public int[] Loop { get; set; } = Loop;
    }

    /// <summary>
    /// The forward's soup for these faces: each face's triangles in the triangulator's cut order from its
    /// start (a world mesh's, on world positions), member after member, as the physics gathers them;
    /// vertices numbered by first use (the weld keeps clusters in the order they form). Returns the input's
    /// triangles (ids into the returned positions) and each one's face.
    /// </summary>
    private static (int[] Indices, Vector3[] Positions, int[] FaceOf) SoupInput(List<Face> faces, Vector3[] vertices)
    {
        var id = new Dictionary<int, int>();
        var positions = new List<Vector3>();
        var indices = new List<int>();
        var faceOf = new List<int>();
        for (var f = 0; f < faces.Count; f++)
        {
            var loop = faces[f].Loop;
            var cut = loop.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate([.. loop.Select(v => vertices[v])]);
            for (var i = 0; i + 2 < cut.Length; i += 3)
            {
                foreach (var j in (int[])[cut[i], cut[i + 1], cut[i + 2]])
                {
                    var v = loop[j];
                    if (!id.TryGetValue(v, out var k))
                    {
                        id[v] = k = positions.Count;
                        positions.Add(vertices[v]);
                    }
                    indices.Add(k);
                }
                faceOf.Add(f);
            }
        }
        return ([.. indices], [.. positions], [.. faceOf]);
    }

    /// <summary>
    /// The soup's face order repaired until RnMeshCreate (our port, exact) gives the shipped triangles: the
    /// faces' input is built (<see cref="SoupInput"/>) and run through the BVH, and at the first output
    /// triangle that is not the shipped one, either the face holding both is cut from another start that
    /// puts the shipped one first, or the shipped triangle's face is moved just before ours where it brings
    /// no vertex in at either place (so the vertex order stays). Triangles are compared by their corners'
    /// positions, rotation aside (RnMeshCreate turns each by its edges). Stops when the output is the
    /// shipped list, when no repair applies, or after a bound; returns what it reached, null with no faces.
    /// </summary>
    /// <summary>
    /// The ways to write these soup triangles as faces of one or two triangles: each pair sharing an edge
    /// the other way round, in one plane (as <see cref="Polygons"/> merges), that the triangulator cuts back
    /// into the pair from some start, or the triangle alone; each face with its cutting starts.
    /// </summary>
    private static IEnumerable<List<Face>> Pairings(List<int> left, List<(int A, int B, int C)> soup, Vector3[] vertices)
    {
        if (left.Count == 0)
        {
            yield return [];
            yield break;
        }
        var t = left[0];
        var rest = left.Skip(1).ToList();
        int[] Loop(int k) => [soup[k].A, soup[k].B, soup[k].C];
        int[][] Rotations(int[] l) => [.. Enumerable.Range(0, l.Length).Select(r => l.Skip(r).Concat(l.Take(r)).ToArray())];
        foreach (var tail in Pairings(rest, soup, vertices))
            yield return [new Face(Loop(t), [t], [.. Rotations(Loop(t))]), .. tail];
        Vector3 Normal(int k) => Vector3.Normalize(Vector3.Cross(vertices[soup[k].B] - vertices[soup[k].A], vertices[soup[k].C] - vertices[soup[k].A]));
        foreach (var u in rest)
        {
            var (p, q) = (Loop(t), Loop(u));
            var (n, m) = (Normal(t), Normal(u));
            if (!(Vector3.Dot(n, m) > 1 - 1e-5f && MathF.Abs(Vector3.Dot(n, vertices[p[0]]) - Vector3.Dot(n, vertices[q[0]])) < 1e-3f))
                continue;
            int[]? loop = null;
            for (var x = 0; x < 3 && loop is null; x++)
            {
                int a = p[x], b = p[(x + 1) % 3];
                var y = Array.IndexOf(q, b);
                if (y >= 0 && q[(y + 1) % 3] == a && !p.Contains(q[(y + 2) % 3]))
                    loop = [.. p.Take(x + 1), q[(y + 2) % 3], .. p.Skip(x + 1)];
            }
            if (loop is null)
                continue;
            var want = new HashSet<(int, int, int)> { Norm(soup[t]), Norm(soup[u]) };
            var starts = Rotations(loop).Where(r =>
            {
                var cut = PolygonTriangulator.Triangulate([.. r.Select(v => vertices[v])]);
                return cut.Length == 6 && new HashSet<(int, int, int)> { Norm((r[cut[0]], r[cut[1]], r[cut[2]])), Norm((r[cut[3]], r[cut[4]], r[cut[5]])) }.SetEquals(want);
            }).ToList();
            if (starts.Count == 0)
                continue;
            foreach (var tail in Pairings([.. rest.Where(x => x != u)], soup, vertices))
                yield return [new Face(starts[0], [t, u], starts), .. tail];
        }
    }

    private static IEnumerable<List<T>> Orders<T>(List<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }
        for (var i = 0; i < items.Count; i++)
            foreach (var tail in Orders([.. items.Where((_, j) => j != i)]))
                yield return [items[i], .. tail];
    }

    /// <summary>
    /// An entry order for a BVH node that gives the shipped order below it, as close to our input order
    /// (<paramref name="rank"/>) as the partition allows. A leaf keeps its entry, so its triangles in the
    /// shipped order (<paramref name="shippedRank"/>); a split by count keeps it too (left half, right half);
    /// a SAH split is the swap partition (the left ones keep their order, the right ones go round a queue:
    /// one arriving joins the back, a left one arriving after any right one sends the front to the back),
    /// which run backwards is deterministic: from the end, a left arrival is the left child's last and turns
    /// the queue back (its back to the front), a right arrival is the queue's back. Which of the two each
    /// step from the end takes is our order's later one. Reads <paramref name="trace"/> in pre-order.
    /// </summary>
    private static List<int> Preimage(List<(int[] Entry, HashSet<int>? Left, bool ByCount)> trace, ref int at,
                                      Dictionary<int, int> shippedRank, int[] rank, int[] faceOf, Dictionary<int, List<bool>>? record = null)
    {
        var node = at;
        var (entry, left, byCount) = trace[at++];
        if (left is null)
            return [.. entry.OrderBy(t => shippedRank.GetValueOrDefault(t, int.MaxValue))];
        var a = Preimage(trace, ref at, shippedRank, rank, faceOf, record);
        var q = Preimage(trace, ref at, shippedRank, rank, faceOf, record);
        var choices = record is null ? null : record[node] = [];
        if (byCount)
            return [.. a, .. q];
        var queue = new LinkedList<int>(q);
        var backwards = new List<int>();
        var (i, rights) = (a.Count - 1, q.Count);
        while (i >= 0 || rights > 0)
        {
            // A face's other triangle first (faces stay whole), else our order's later one.
            var glue = backwards.Count > 0 ? faceOf[backwards[^1]] : -1;
            var takeLeft = i >= 0 && (rights == 0 || (faceOf[a[i]] == glue) != (faceOf[queue.Last!.Value] == glue)
                ? i >= 0 && (rights == 0 || faceOf[a[i]] == glue)
                : rank[a[i]] > rank[queue.Last!.Value]);
            choices?.Add(takeLeft);
            if (takeLeft)
            {
                backwards.Add(a[i--]);
                if (rights > 0)
                {
                    var back = queue.Last!.Value;
                    queue.RemoveLast();
                    queue.AddFirst(back);
                }
            }
            else
            {
                backwards.Add(queue.Last!.Value);
                queue.RemoveLast();
                rights--;
            }
        }
        backwards.Reverse();
        return backwards;
    }

    /// <summary>
    /// The faces in an order following <paramref name="priority"/> (per face) as far as the soup's vertex
    /// order allows: each step the most wanted face with a start whose cut brings in exactly the next
    /// vertices (none for a face already met), that start cutting its triangles most nearly in
    /// <paramref name="triangleRank"/>'s order; a face no step can take goes at the end.
    /// </summary>
    private static List<Face> Project(List<Face> faces, double[] priority, Func<int, int> triangleRank, Vector3[] vertices)
    {
        // Per face and start: the cut's corners and its triangles (soup ids).
        var cuts = faces.Select(f => f.Starts.Select(loop =>
        {
            var cut = loop.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate([.. loop.Select(v => vertices[v])]);
            var corners = cut.Select(i => loop[i]).ToArray();
            var triangles = Enumerable.Range(0, corners.Length / 3)
                .Select(i => f.Triangles.FirstOrDefault(t => t >= 0 && Norm(TriangleOf(t)) == Norm((corners[i * 3], corners[i * 3 + 1], corners[i * 3 + 2])), -1)).ToArray();
            return (Loop: loop, Corners: corners, Triangles: triangles);
        }).ToList()).ToList();
        (int, int, int) TriangleOf(int t) => soupTriangles![t];
        var done = new bool[faces.Count];
        var seen = new HashSet<int>();
        var next = 0;
        var result = new List<Face>();
        var byPriority = Enumerable.Range(0, faces.Count).OrderBy(f => priority[f]).ToList();
        while (result.Count < faces.Count)
        {
            var (pick, start) = (-1, -1);
            foreach (var f in byPriority)
            {
                if (done[f])
                    continue;
                var bestInversions = int.MaxValue;
                for (var s = 0; s < cuts[f].Count; s++)
                {
                    var fresh = cuts[f][s].Corners.Where(v => !seen.Contains(v)).Distinct().ToList();
                    if (!fresh.Select((v, i) => v == next + i).All(x => x))
                        continue;
                    var ranks = cuts[f][s].Triangles.Select(triangleRank).ToList();
                    var inversions = 0;
                    for (var x = 0; x < ranks.Count; x++)
                        for (var y = x + 1; y < ranks.Count; y++)
                            if (ranks[x] > ranks[y])
                                inversions++;
                    if (inversions < bestInversions)
                        (bestInversions, start) = (inversions, s);
                }
                if (start >= 0)
                {
                    pick = f;
                    break;
                }
            }
            if (pick < 0)
            {
                pick = byPriority.First(f => !done[f]);
                start = 0;
            }
            done[pick] = true;
            foreach (var v in cuts[pick][start].Corners)
                if (seen.Add(v))
                    next = Math.Max(next, v + 1);
            result.Add(faces[pick] with { Loop = cuts[pick][start].Loop });
        }
        return result;
    }

    /// <summary>
    /// The input built from its end with every BVH node's swap partition run backwards (see
    /// <see cref="Preimage"/>), so each leaf gets the shipped order by construction; what is searched is
    /// each SAH node's choice, step by step from the end, of a left arrival (the left child's next from its
    /// end, turning the queue back when any right one came before) or a right one (the queue's back, pulled
    /// from the right child's end while any is left). Each triangle the root gives must continue a face
    /// from its end in one of its starts' cut, and a face once whole must leave the vertex order: going
    /// backwards, a vertex whose last remaining use is placed is first used there, so it must be the
    /// highest one not yet placed (corners from the cut's last). The inverted order's choices
    /// (<paramref name="prefer"/>) are tried first. Returns the faces with their starts in input order, or
    /// null when <paramref name="budget"/> steps find none.
    /// </summary>
    private static List<Face>? BackwardSearch(List<(int[] Entry, HashSet<int>? Left, bool ByCount)> trace, Func<int, int> soupOf,
                                              Vector3[] vertices, List<(int A, int B, int C)> soup, Dictionary<int, List<bool>> prefer, int[] rankOfSoup, long budget, out string report)
    {
        var n = trace.Count;
        var (lc, rc, leafOrder) = (new int[n], new int[n], new int[n][]);
        var (isLeaf, byCount) = (new bool[n], new bool[n]);
        var (remL, remR, virt, leafRem, emitted) = (new int[n], new int[n], new int[n], new int[n], new int[n]);
        var (front, slots, gen) = (new List<int>[n], new List<int>[n], new int[n]);
        var at = 0;
        int Read()
        {
            var i = at++;
            var (entry, left, count) = trace[i];
            (front[i], slots[i]) = ([], []);
            if (left is null)
            {
                isLeaf[i] = true;
                leafOrder[i] = [.. entry.Select(soupOf).Order()];
                leafRem[i] = entry.Length;
                return i;
            }
            byCount[i] = count;
            lc[i] = Read();
            rc[i] = Read();
            (remL[i], remR[i]) = (left.Count, entry.Length - left.Count);
            virt[i] = remR[i];
            return i;
        }
        Read();

        // The faces each soup triangle can end (from the input's end, so it is the face's last one): itself
        // alone from any corner, or a quad with a neighbour across an edge (the other way round, in one
        // plane, as Polygons merges) that the triangulator cuts from some start into the neighbour then it.
        var ends = new List<(int Partner, int[] Loop, int[] Corners)>[soup.Count];
        var byEdge = new Dictionary<(int, int), int>();
        for (var t = 0; t < soup.Count; t++)
            foreach (var (x, y) in (ValueTuple<int, int>[])[(soup[t].A, soup[t].B), (soup[t].B, soup[t].C), (soup[t].C, soup[t].A)])
                byEdge[(x, y)] = t;
        Vector3 Normal(int k) => Vector3.Normalize(Vector3.Cross(vertices[soup[k].B] - vertices[soup[k].A], vertices[soup[k].C] - vertices[soup[k].A]));
        for (var t = 0; t < soup.Count; t++)
        {
            int[] own = [soup[t].A, soup[t].B, soup[t].C];
            ends[t] = [.. Enumerable.Range(0, 3).Select(r => (-1, (int[])[own[r], own[(r + 1) % 3], own[(r + 2) % 3]], (int[])[own[r], own[(r + 1) % 3], own[(r + 2) % 3]]))];
            for (var x = 0; x < 3; x++)
            {
                if (!byEdge.TryGetValue((own[(x + 1) % 3], own[x]), out var u) || u == t)
                    continue;
                var (n1, n2) = (Normal(t), Normal(u));
                if (!(Vector3.Dot(n1, n2) > 1 - 1e-5f && MathF.Abs(Vector3.Dot(n1, vertices[own[0]]) - Vector3.Dot(n1, vertices[soup[u].A])) < 1e-3f))
                    continue;
                int[] other = [soup[u].A, soup[u].B, soup[u].C];
                var third = other.First(v => !own.Contains(v));
                int[] loop = [.. own.Take(x + 1), third, .. own.Skip(x + 1)];
                for (var r = 0; r < 4; r++)
                {
                    var rotated = loop.Skip(r).Concat(loop.Take(r)).ToArray();
                    var cut = PolygonTriangulator.Triangulate([.. rotated.Select(v => vertices[v])]);
                    if (cut.Length != 6)
                        continue;
                    var corners = cut.Select(i => rotated[i]).ToArray();
                    if (Norm((corners[0], corners[1], corners[2])) == Norm(soup[u]) && Norm((corners[3], corners[4], corners[5])) == Norm(soup[t]))
                        ends[t].Add((u, rotated, corners));
                }
            }
        }
        var uses = new int[vertices.Length];
        foreach (var t in soup)
        {
            uses[t.A]++;
            uses[t.B]++;
            uses[t.C]++;
        }
        var unplaced = vertices.Length;
        var total = soup.Count;
        var chosen = new List<(int Last, int End)>();
        var (openPartner, openLast, openEnd) = (-1, -1, -1);
        long steps = 0;
        var done = false;
        var (deepest, deepestAt) = (0, "");
        List<Face>? found = null;
        // Debugging: DECOMPILE_TRUTH=<file of the source's input triangles, as SoupInputsOfTwoMaps dumps them>
        // keeps the search to that order, to see where the model loses it.
        var truth = Truth(soup, vertices);
        var truthLost = "";
        var rankCandidates = Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_RANK") == "1";
        var greedySlots = Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_SLOTS") != "all";
        var lagBound = int.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_LAG"), out var lb) ? lb : 8;
        if (truth is not null && truth.All(t => t >= 0))
        {
            // The source's own choices: its input forward through the builder, each node's entry read from
            // the end.
            Physics.RnMeshBuilder.Trace = [];
            Physics.RnMeshBuilder.Create([.. truth.SelectMany(t => (int[])[soup[t].A, soup[t].B, soup[t].C])], vertices, null, new Physics.RnMeshBuilder.Options { Weld = false });
            var truthTrace = Physics.RnMeshBuilder.Trace;
            Physics.RnMeshBuilder.Trace = null;
            var given = prefer;
            prefer = [];
            for (var i = 0; i < truthTrace.Count; i++)
                if (truthTrace[i].Left is { } l && !truthTrace[i].ByCount)
                    prefer[i] = [.. truthTrace[i].Entry.Reverse().Select(l.Contains)];
            if (Environment.GetEnvironmentVariable("DECOMPILE_TRUTH_COMPARE") == "1")
            {
                var differ = prefer.Select(kv => (Node: kv.Key, Size: kv.Value.Count, Diff: given.TryGetValue(kv.Key, out var g) ? kv.Value.Zip(g).Count(x => x.First != x.Second) : -1)).Where(x => x.Diff != 0).ToList();
                Console.Error.WriteLine($"truth compare: {differ.Count} of {prefer.Count} SAH nodes differ, {differ.Sum(x => x.Diff)} steps: {string.Join(" ", differ.Take(30).Select(x => $"{x.Node}({x.Size}):{x.Diff}"))}");
                prefer = given;
            }
        }

        // A node's queue from the back: the right child's entry not yet taken (virt, taken from its end),
        // then front[i], what turning put at the front (front[i][0] frontmost). Turning takes the queue's
        // back without needing to know it: from the right child's entry it reserves the next one (a
        // placeholder, slots[i]), whose identity is drawn when a later take from that entry needs the ones
        // before it (GenAll), or when the placeholder itself comes out.
        void Pull(int i, Action<int> k)
        {
            if (done || ++steps > budget)
                return;
            if (isLeaf[i])
            {
                if (leafRem[i] == 0)
                    return;
                var t = leafOrder[i][--leafRem[i]];
                k(t);
                leafRem[i]++;
                return;
            }
            if (byCount[i])
            {
                if (remR[i] > 0)
                {
                    remR[i]--;
                    Pull(rc[i], k);
                    remR[i]++;
                }
                else if (remL[i] > 0)
                {
                    remL[i]--;
                    Pull(lc[i], k);
                    remL[i]++;
                }
                return;
            }
            var step = emitted[i];
            var leftFirst = prefer.TryGetValue(i, out var p) && step < p.Count ? p[step] : remL[i] > 0;
            foreach (var takeLeft in leftFirst ? (bool[])[true, false] : [false, true])
            {
                if (done)
                    return;
                emitted[i]++;
                if (takeLeft && remL[i] > 0)
                {
                    remL[i]--;
                    Pull(lc[i], t =>
                    {
                        if (remR[i] == 0)
                        {
                            k(t);
                            return;
                        }
                        if (virt[i] > 0)
                        {
                            virt[i]--;
                            slots[i].Add(-1);
                            front[i].Insert(0, -slots[i].Count);
                            k(t);
                            front[i].RemoveAt(0);
                            slots[i].RemoveAt(slots[i].Count - 1);
                            virt[i]++;
                        }
                        else if (front[i].Count > 0)
                        {
                            var back = front[i][^1];
                            front[i].RemoveAt(front[i].Count - 1);
                            front[i].Insert(0, back);
                            k(t);
                            front[i].RemoveAt(0);
                            front[i].Add(back);
                        }
                    });
                    remL[i]++;
                }
                else if (!takeLeft && remR[i] > 0)
                {
                    remR[i]--;
                    PopBack(i, k);
                    remR[i]++;
                }
                emitted[i]--;
            }
        }
        // The reserved slots' identities, in their order, from the right child's entry.
        void GenAll(int i, Action k)
        {
            if (gen[i] == slots[i].Count)
            {
                k();
                return;
            }
            var at = gen[i];
            var taken = false;
            Pull(rc[i], x =>
            {
                // Greedy by default (DECOMPILE_SEARCH_SLOTS=all tries every identity): a slot's identity
                // only matters later, and trying them all here multiplies out before anything is checked.
                if (taken && greedySlots)
                    return;
                taken = true;
                slots[i][at] = x;
                gen[i]++;
                GenAll(i, k);
                gen[i]--;
                slots[i][at] = -1;
            });
        }
        void PopBack(int i, Action<int> k)
        {
            if (virt[i] > 0)
            {
                virt[i]--;
                GenAll(i, () => Pull(rc[i], k));
                virt[i]++;
            }
            else if (front[i].Count > 0)
            {
                var x = front[i][^1];
                front[i].RemoveAt(front[i].Count - 1);
                if (x >= 0)
                    k(x);
                else
                    GenAll(i, () => k(slots[i][-x - 1]));
                front[i].Add(x);
            }
        }
        // A face is whole: its corners from the cut's last, each vertex whose last use this is must be the
        // highest one not yet placed.
        void Close(int last, int end, Action k)
        {
            var corners = ends[last][end].Corners;
            var undo = new List<(int Vertex, bool Placed)>();
            var ok = true;
            for (var c = corners.Length - 1; c >= 0 && ok; c--)
            {
                var v = corners[c];
                var placedHere = --uses[v] == 0 && v == unplaced - 1;
                ok = uses[v] > 0 || placedHere;
                if (placedHere)
                    unplaced--;
                undo.Add((v, placedHere));
            }
            // A face bringing no vertex stays near its corners: at most lagBound vertices come in between
            // its highest corner and it (a search bound, not a rule: probe01's source has at most 7; the
            // result is checked against the shipped triangles all the same).
            if (ok && undo.All(u => !u.Placed) && unplaced - 1 - corners.Max() > lagBound)
                ok = false;
            if (ok)
            {
                chosen.Add((last, end));
                k();
                chosen.RemoveAt(chosen.Count - 1);
            }
            for (var c = undo.Count - 1; c >= 0; c--)
            {
                uses[undo[c].Vertex]++;
                if (undo[c].Placed)
                    unplaced++;
            }
        }
        var visits = new long[soup.Count + 1];
        void Step(int placed)
        {
            visits[placed]++;
            if (placed > deepest)
                (deepest, deepestAt) = (placed, $"triangles {string.Join(" ", chosen.TakeLast(4).Select(c => c.Last))}, {unplaced} vertices unplaced");
            if (placed == total)
            {
                if (unplaced == 0)
                {
                    done = true;
                    found = [.. chosen.AsEnumerable().Reverse().Select(c =>
                    {
                        var (partner, loop, _) = ends[c.Last][c.End];
                        List<int> triangles = partner < 0 ? [c.Last] : [partner, c.Last];
                        return new Face(loop, triangles, [.. ends[c.Last].Where(x => x.Partner == partner && (partner >= 0 || x.Loop.Length == 3)).Select(x => x.Loop)]);
                    })];
                }
                return;
            }
            // DECOMPILE_SEARCH_RANK=1: the triangles the root can give next, latest in our order first, each
            // tried down every path that gives it (listing them draws the reserved slots, which branches);
            // otherwise each node's preferred choice first.
            IEnumerable<int> wanted = [-1];
            if (rankCandidates)
            {
                var candidates = new HashSet<int>();
                if (openPartner >= 0)
                    candidates.Add(openPartner);
                else
                    Pull(0, t => candidates.Add(t));
                wanted = candidates.OrderByDescending(t => rankOfSoup[t]);
            }
            foreach (var want in wanted)
            Pull(0, t =>
            {
                if (want >= 0 && t != want)
                    return;
                if (truth is not null && t != truth[total - 1 - placed])
                {
                    if (placed >= deepest)
                        truthLost = $"at {placed} from the end the root gave {t}, the source has {truth[total - 1 - placed]}";
                    return;
                }
                if (openPartner >= 0)
                {
                    if (t != openPartner)
                        return;
                    var (keep, keepLast) = (openPartner, openLast);
                    openPartner = -1;
                    Close(openLast, openEnd, () => Step(placed + 1));
                    (openPartner, openLast) = (keep, keepLast);
                    return;
                }
                for (var k = 0; k < ends[t].Count && !done; k++)
                {
                    if (ends[t][k].Partner < 0)
                        Close(t, k, () => Step(placed + 1));
                    else
                    {
                        (openPartner, openLast, openEnd) = (ends[t][k].Partner, t, k);
                        Step(placed + 1);
                        openPartner = -1;
                    }
                }
            });
        }
        var thread = new Thread(() => Step(0), 1 << 30);
        thread.Start();
        thread.Join();
        report = found is not null ? $"backward search found an order in {steps} steps"
            : $"backward search found none in {steps} steps (visits by depth {string.Join(",", visits.Take(deepest + 1))}; deepest {deepest} of {total} triangles: {deepestAt}{(truth is null ? "" : $"; truth {string.Join(",", truth.TakeLast(10))}; {truthLost}")})";
        return found;
    }

    /// <summary>
    /// RnMeshCreate's BVH (its pre-order trace) run forward one triangle at a time, so a search can place
    /// faces and take them back: a SAH node sends a left triangle on at once (turning its queue first, the
    /// swap partition's front-to-back, when any right one waits) and queues a right one, which go on in
    /// queue order once the node has all of its triangles; a split by count sends its first ones left; a
    /// leaf must get the shipped triangles in their order, or the arrival fails.
    /// </summary>
    private sealed class BvhRun
    {
        private readonly int[] leftChild, rightChild, size;
        private readonly HashSet<int>?[] leftSet;
        private readonly bool[] byCount;
        private readonly int[][] leafOrder;
        private readonly int[] arrived, head, leftArrived;
        private readonly bool[] flushed;
        private readonly List<int>[] queue;
        private readonly List<(int Node, byte Kind)> log = [];
        public long Arrivals;
        public readonly long[] FlushFailures, CyclicFailures;
        private readonly Dictionary<int, int>[] firstLeafRank;
        private readonly int[] parent;
        private readonly bool[] isLeft;
        private readonly int[][] sets;
        /// <summary>The leaf the last failing arrival reached.</summary>
        public int FailedLeaf = -1;
        private readonly int[] nodeCount;
        /// <summary>
        /// Asked on each push into a node's queue (the node and its queue from the front): can the right
        /// child still get an entry from it? See ForwardSearch.
        /// </summary>
        public Func<int, List<int>, bool>? Feasible;
        public readonly long[] FeasibleFailures;
        public int RightChild(int n) => rightChild[n];
        public int[] Set(int n) => sets[n];
        public int NodeCount(int n) => nodeCount[n];
        public Dictionary<int, int> FirstLeafRank(int n) => firstLeafRank[n];

        /// <summary>
        /// The triangles whose input order a node's entry depends on: a left child's entry is its parent's
        /// restricted to it, so up the left links it is the input's restricted to it; a right child's is its
        /// parent's queue, which depends on the parent's whole entry.
        /// </summary>
        public int[] Depends(int n)
        {
            var x = n;
            while (true)
            {
                var a = x;
                while (parent[a] >= 0 && isLeft[a])
                    a = parent[a];
                if (parent[a] < 0)
                    return sets[x];
                x = parent[a];
            }
        }
        public int Size(int n) => size[n];

        public BvhRun(List<(int[] Entry, HashSet<int>? Left, bool ByCount)> trace, Func<int, int> soupOf)
        {
            var n = trace.Count;
            (leftChild, rightChild, size, leftSet, byCount, leafOrder) = (new int[n], new int[n], new int[n], new HashSet<int>?[n], new bool[n], new int[n][]);
            (arrived, head, queue, leftArrived, flushed) = (new int[n], new int[n], new List<int>[n], new int[n], new bool[n]);
            (FlushFailures, CyclicFailures) = (new long[n], new long[n]);
            firstLeafRank = new Dictionary<int, int>[n];
            FeasibleFailures = new long[n];
            (parent, isLeft, sets, nodeCount) = (new int[n], new bool[n], new int[n][], new int[n]);
            parent[0] = -1;
            var at = 0;
            int Read()
            {
                var i = at++;
                var (entry, left, count) = trace[i];
                sets[i] = [.. entry.Select(soupOf)];
                size[i] = entry.Length;
                queue[i] = [];
                byCount[i] = count;
                if (left is null)
                {
                    leafOrder[i] = [.. entry.Select(soupOf).Order()];
                    nodeCount[i] = 1;
                    return i;
                }
                leftSet[i] = [.. left.Select(soupOf)];
                leftChild[i] = Read();
                rightChild[i] = Read();
                (parent[leftChild[i]], isLeft[leftChild[i]]) = (i, true);
                parent[rightChild[i]] = i;
                var leaf = rightChild[i];
                while (leftSet[leaf] is not null)
                    leaf = leftChild[leaf];
                firstLeafRank[i] = leafOrder[leaf].Select((t, k) => (t, k)).ToDictionary(x => x.t, x => x.k);
                nodeCount[i] = at - i;
                return i;
            }
            Read();
        }

        public int Mark => log.Count;

        /// <summary>A hash of everything a later arrival depends on: each node's counts and queue.</summary>
        public ulong StateHash()
        {
            var h = 1469598103934665603UL;
            void Mix(long x) => h = (h ^ (ulong)x) * 1099511628211UL;
            for (var n = 0; n < arrived.Length; n++)
            {
                Mix(arrived[n]);
                if (leftSet[n] is null)
                    continue;
                Mix(leftArrived[n]);
                Mix(flushed[n] ? 1 : 0);
                if (!flushed[n])
                    for (var i = head[n]; i < queue[n].Count; i++)
                        Mix(queue[n][i] + 7);
            }
            return h;
        }

        public bool Arrive(int t) => Arrive(0, t);

        private bool Arrive(int n, int t)
        {
            Arrivals++;
            if (leftSet[n] is not { } left)
            {
                if (arrived[n] >= leafOrder[n].Length || leafOrder[n][arrived[n]] != t)
                {
                    FailedLeaf = n;
                    return false;
                }
                arrived[n]++;
                log.Add((n, 0));
                return true;
            }
            arrived[n]++;
            log.Add((n, 0));
            bool ok;
            if (byCount[n])
                ok = Arrive(arrived[n] <= size[leftChild[n]] ? leftChild[n] : rightChild[n], t);
            else if (left.Contains(t))
            {
                leftArrived[n]++;
                log.Add((n, 3));
                if (queue[n].Count > head[n] && !flushed[n])
                {
                    queue[n].Add(queue[n][head[n]]);
                    head[n]++;
                    log.Add((n, 2));
                }
                ok = Arrive(leftChild[n], t);
            }
            else
            {
                queue[n].Add(t);
                log.Add((n, 1));
                ok = true;
                // The right child's leftmost leaf gets its triangles in the shipped order, and a queue's
                // cyclic order never changes once in (turns move the front to the back, a new one joins
                // where the back meets the front), so the queued ones of that leaf must already stand in
                // a turn of its order: one descent going round.
                if (!flushed[n] && firstLeafRank[n].TryGetValue(t, out _))
                {
                    var (descents, count, first, last) = (0, 0, -1, -1);
                    for (var i = head[n]; i < queue[n].Count; i++)
                        if (firstLeafRank[n].TryGetValue(queue[n][i], out var r))
                        {
                            if (count++ == 0)
                                first = r;
                            else if (r < last)
                                descents++;
                            last = r;
                        }
                    if (count >= 2 && first < last)
                        descents++;
                    if (count >= 2 && descents > 1)
                    {
                        FailedLeaf = n;
                        CyclicFailures[n]++;
                        return false;
                    }
                }
                if (Feasible is not null && !flushed[n] && size[rightChild[n]] >= 8 && !Feasible(n, queue[n].GetRange(head[n], queue[n].Count - head[n])))
                {
                    FailedLeaf = n;
                    FeasibleFailures[n]++;
                    return false;
                }
                // The last right one: the queue's cyclic order is fixed from here, and each left one still to
                // come turns it once, so the right child's entry is known now.
                var rights = size[n] - size[leftChild[n]];
                if (queue[n].Count - head[n] == rights)
                {
                    flushed[n] = true;
                    log.Add((n, 4));
                    var turns = (size[leftChild[n]] - leftArrived[n]) % rights;
                    for (var i = 0; i < rights && ok; i++)
                        ok = Arrive(rightChild[n], queue[n][head[n] + (turns + i) % rights]);
                    if (!ok)
                        FlushFailures[n]++;
                }
            }
            return ok;
        }

        public void Undo(int mark)
        {
            while (log.Count > mark)
            {
                var (n, kind) = log[^1];
                log.RemoveAt(log.Count - 1);
                if (kind == 0)
                    arrived[n]--;
                else if (kind == 3)
                    leftArrived[n]--;
                else if (kind == 4)
                    flushed[n] = false;
                else
                {
                    queue[n].RemoveAt(queue[n].Count - 1);
                    if (kind == 2)
                        head[n]--;
                }
            }
        }
    }

    /// <summary>
    /// The input built from its start: each step a face (a soup triangle alone from any corner, or a quad
    /// with a coplanar neighbour that the triangulator cuts into the two in that order) whose corners bring
    /// in exactly the next vertices, or none; a face bringing none placed within <paramref name="lagBound"/>
    /// vertices of its highest corner (a search bound), so a triangle whose corners are all met and whose
    /// window has passed ends the branch; every triangle sent through the BVH as it is placed
    /// (<see cref="BvhRun"/>: a leaf getting one out of the shipped order ends the branch). Faces are tried
    /// in our order (<paramref name="rankOfSoup"/>). Returns the faces in input order, or null within
    /// <paramref name="budget"/> arrivals.
    /// </summary>
    private static List<Face>? ForwardSearch(List<(int[] Entry, HashSet<int>? Left, bool ByCount)> trace, Func<int, int> soupOf,
                                             Vector3[] vertices, List<(int A, int B, int C)> soup, int[] rankOfSoup, long budget, int lagBound,
                                             int[]? truth, out string report)
    {
        var bvh = new BvhRun(trace, soupOf);
        // The big-node check: a queue's cyclic order is fixed once in, so whatever the right child gets will
        // hold the queued triangles in a turn of their queue order, the ones still to come anywhere among
        // them. Whether the right child's subtree takes some such entry is a small search of its own (no
        // faces, no vertices: a relaxation, so its no is a real no), the turns limited to those the right
        // child's leftmost leaf allows. Out of budget counts as yes; answers are kept per queue state. Off by default: on probe01 it rejects millions of queue states yet the search gets no deeper.
        var oracleBudget = long.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_ORACLE"), out var ob) ? ob : 0;
        if (oracleBudget > 0)
        {
            var subRuns = new Dictionary<int, BvhRun>();
            var answers = new Dictionary<(int, ulong), bool>();
            bvh.Feasible = (n, c) =>
            {
                var rank = bvh.FirstLeafRank(n);
                if (c.Count(rank.ContainsKey) < 2)
                    return true;
                var h = (ulong)n * 1099511628211UL;
                foreach (var x in c)
                    h = (h ^ (ulong)(x + 1)) * 1099511628211UL;
                if (answers.TryGetValue((n, h), out var known))
                    return known;
                var r = bvh.RightChild(n);
                if (!subRuns.TryGetValue(r, out var sub))
                    subRuns[r] = sub = new BvhRun(trace.GetRange(r, bvh.NodeCount(r)), soupOf);
                var queued = c.ToHashSet();
                var free = bvh.Set(r).Where(t => !queued.Contains(t)).ToList();
                var used = new bool[free.Count];
                var start = sub.Arrivals;
                var exhausted = false;
                bool Fit(List<int> seq, int i, int placedFree)
                {
                    if (i == seq.Count && placedFree == free.Count)
                        return true;
                    if (sub.Arrivals - start > oracleBudget)
                    {
                        exhausted = true;
                        return true;
                    }
                    var mark = sub.Mark;
                    if (i < seq.Count)
                    {
                        if (sub.Arrive(seq[i]) && Fit(seq, i + 1, placedFree))
                            return true;
                        sub.Undo(mark);
                    }
                    for (var f = 0; f < free.Count; f++)
                    {
                        if (used[f])
                            continue;
                        used[f] = true;
                        if (sub.Arrive(free[f]) && Fit(seq, i, placedFree + 1))
                            return true;
                        sub.Undo(mark);
                        used[f] = false;
                    }
                    return false;
                }
                var ok = false;
                for (var cut = 0; cut < c.Count && !ok; cut++)
                {
                    var seq = c.Skip(cut).Concat(c.Take(cut)).ToList();
                    var ranks = seq.Where(rank.ContainsKey).Select(x => rank[x]).ToList();
                    if (ranks.Zip(ranks.Skip(1)).Any(q => q.Second < q.First))
                        continue;
                    ok = Fit(seq, 0, 0);
                    sub.Undo(0);
                    Array.Clear(used);
                }
                if (!exhausted)
                    answers[(n, h)] = ok;
                return ok;
            };
        }
        // DECOMPILE_TRUTH_PREFIX=n: keep to the source's order only for its first n triangles.
        var truthPrefix = int.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_TRUTH_PREFIX"), out var tp) ? tp : int.MaxValue;
        if (truth is not null && Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
        {
            // Where each SAH node's triangles go (L/R) along the source's input, for the first few nodes.
            for (var i = 0; i < Math.Min(trace.Count, 12); i++)
                if (trace[i].Left is { } l)
                {
                    var set = trace[i].Entry.Select(soupOf).ToHashSet();
                    var left = l.Select(soupOf).ToHashSet();
                    Console.Error.WriteLine($"node {i} ({set.Count}, {left.Count} left): {new string([.. truth.Where(set.Contains).Select(t => left.Contains(t) ? 'L' : 'R')])}");
                }
        }
        var (begins, partners) = Begins(soup, vertices);
        // DECOMPILE_SEARCH_DUMP=<file>: the problem as JSON (soup corners, faces each triangle can begin,
        // the BVH trace in pre-order as soup ids, the source's order when known) for an outside solver.
        if (Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_DUMP") is { Length: > 0 } dumpFile)
            File.WriteAllText(dumpFile, System.Text.Json.JsonSerializer.Serialize(new
            {
                vertices = vertices.Length,
                soup = soup.Select(t => (int[])[t.A, t.B, t.C]),
                begins = begins.Select(b => b.Select(x => new { partner = x.Partner, corners = x.Corners })),
                trace = trace.Select(n => new { set = n.Entry.Select(soupOf), left = n.Left?.Select(soupOf), byCount = n.ByCount }),
                truth,
            }));
        var top = soup.Select(t => Math.Max(t.A, Math.Max(t.B, t.C))).ToArray();
        var byVertex = new List<int>[vertices.Length];
        for (var v = 0; v < vertices.Length; v++)
            byVertex[v] = [];
        for (var t = 0; t < soup.Count; t++)
            foreach (var v in (int[])[soup[t].A, soup[t].B, soup[t].C])
                byVertex[v].Add(t);
        var byTop = Enumerable.Range(0, soup.Count).OrderBy(t => top[t]).ToArray();
        var placed = new bool[soup.Count];
        var chosen = new List<(int First, int Begin)>();
        var total = soup.Count;
        List<Face>? found = null;
        var (deepest, visits) = (0, new long[soup.Count + 1]);
        var truthLost = "";
        // Covered triangles (every corner met) whose window has passed: each must have gone already, or
        // still be able to go in a quad whose partner brings a vertex in.
        bool Stale(int next)
        {
            foreach (var t in byTop)
            {
                if (top[t] >= next - 1 - lagBound)
                    break;
                if (!placed[t] && !partners[t].Any(u => !placed[u] && top[u] >= next - 1 - lagBound))
                    return true;
            }
            return false;
        }
        // Conflict-directed backjumping: a branch's failure names the decisions (by depth) that caused it;
        // a decision not among them is skipped on the way back. A leaf getting a triangle out of order
        // blames the decisions that placed the triangles its entry depends on (BvhRun.Depends); anything
        // else blames the decision just before (plain backtracking). Skipping can only lose solutions,
        // never accept a wrong one: the result is checked against the shipped triangles.
        var depthOf = new int[soup.Count];
        var depends = new Dictionary<int, int[]>();
        var jumps = 0L;
        HashSet<int> Before(int depth) => depth > 0 ? [depth - 1] : [];
        // States already searched to failure: the same triangles placed, the same next vertex and the same
        // BVH state have the same future (a quad and its two triangles alone place the same sequence).
        var failed = new HashSet<(ulong, ulong, int)>();
        var zobrist = Enumerable.Range(0, soup.Count).Select(t => (ulong)(new Random(t * 7919 + 13).NextInt64())).ToArray();
        var placedHash = 0UL;
        long repeats = 0;
        HashSet<int>? Dfs(int count, int next)
        {
            var depth = chosen.Count;
            var key = (placedHash, bvh.StateHash(), next);
            if (failed.Contains(key))
            {
                repeats++;
                return Before(depth);
            }
            var result = Explore(count, next, depth);
            if (result is not null && bvh.Arrivals < budget)
                failed.Add(key);
            return result;
        }
        HashSet<int>? Explore(int count, int next, int depth)
        {
            visits[count]++;
            deepest = Math.Max(deepest, count);
            if (count == total)
            {
                found = [.. chosen.Select(c =>
                {
                    var (partner, loop, _) = begins[c.First][c.Begin];
                    List<int> triangles = partner < 0 ? [c.First] : [c.First, partner];
                    return new Face(loop, triangles, [.. begins[c.First].Where(x => x.Partner == partner && (partner >= 0 || x.Loop.Length == 3)).Select(x => x.Loop)]);
                })];
                return null;
            }
            if (Stale(next))
            {
                if (truth is not null)
                    truthLost = $"at {count} (next vertex {next}) a covered triangle's window passed: {string.Join(" ", byTop.Where(t => !placed[t] && top[t] < next - 1 - lagBound).Take(4).Select(t => $"{t}(top {top[t]})"))}";
                return Before(depth);
            }
            if (truth is not null && count < total)
            {
                var t0 = truth[count];
                truthLost = $"at {count} (next vertex {next}) the source's {t0} [{soup[t0]}] top {top[t0]}, placed {placed[t0]}";
            }
            // Candidates: the triangles holding the next vertex, and the covered ones still in their window.
            var candidates = new HashSet<int>();
            if (next < vertices.Length)
                foreach (var t in byVertex[next])
                    if (!placed[t])
                        candidates.Add(t);
            foreach (var t in byTop.Reverse())
            {
                if (top[t] < next - 1 - lagBound - 1)
                    break;
                if (top[t] < next && !placed[t])
                    candidates.Add(t);
            }
            foreach (var t in byTop)
            {
                if (top[t] >= next - 2 - lagBound)
                    break;
                if (!placed[t] && partners[t].Any(u => !placed[u] && top[u] < next && top[u] >= next - 1 - lagBound))
                    candidates.Add(t);
            }
            var conflict = new HashSet<int>();
            foreach (var t in candidates.OrderBy(t => rankOfSoup[t]))
            {
                if (truth is not null && count < truthPrefix && t != truth[count])
                    continue;
                // A face bringing no vertex is tried from one start per partner: its corners' order changes
                // nothing (RnMeshCreate turns each triangle by its edges).
                var coveredTried = new HashSet<int>();
                for (var b = 0; b < begins[t].Count; b++)
                {
                    if (bvh.Arrivals >= budget)
                        return [];
                    var (partner, _, corners) = begins[t][b];
                    if (partner >= 0 && (placed[partner] || (truth is not null && count < truthPrefix && (count + 1 >= total || truth[count + 1] != partner))))
                        continue;
                    if (corners.All(v => v < next) && !coveredTried.Add(partner))
                        continue;
                    var fresh = next;
                    var valid = true;
                    foreach (var v in corners)
                        if (v >= fresh)
                        {
                            if (v != fresh)
                            {
                                valid = false;
                                break;
                            }
                            fresh++;
                        }
                    if (!valid || (fresh == next && next - 1 - corners.Max() > lagBound))
                        continue;
                    var mark = bvh.Mark;
                    if (bvh.Arrive(t) && (partner < 0 || bvh.Arrive(partner)))
                    {
                        placed[t] = true;
                        depthOf[t] = depth;
                        placedHash ^= zobrist[t];
                        if (partner >= 0)
                        {
                            placed[partner] = true;
                            depthOf[partner] = depth;
                            placedHash ^= zobrist[partner];
                        }
                        chosen.Add((t, b));
                        var child = Dfs(count + (partner < 0 ? 1 : 2), fresh);
                        if (child is null)
                            return null;
                        chosen.RemoveAt(chosen.Count - 1);
                        placed[t] = false;
                        placedHash ^= zobrist[t];
                        if (partner >= 0)
                        {
                            placed[partner] = false;
                            placedHash ^= zobrist[partner];
                        }
                        bvh.Undo(mark);
                        if (bvh.Arrivals >= budget)
                            return [];
                        if (!child.Contains(depth))
                        {
                            jumps++;
                            return child;
                        }
                        child.Remove(depth);
                        conflict.UnionWith(child);
                        continue;
                    }
                    if (truth is not null)
                        truthLost += $"; fails in the BVH (begin {b})";
                    var leaf = bvh.FailedLeaf;
                    bvh.Undo(mark);
                    if (!depends.TryGetValue(leaf, out var dep))
                        depends[leaf] = dep = bvh.Depends(leaf);
                    foreach (var x in dep)
                        if (placed[x])
                            conflict.Add(depthOf[x]);
                }
            }
            if (conflict.Count == 0)
                return Before(depth);
            return conflict;
        }
        var thread = new Thread(() => Dfs(0, 0), 1 << 30);
        thread.Start();
        thread.Join();
        report = found is not null ? $"forward search found an order in {bvh.Arrivals} arrivals ({jumps} backjumps)"
            : $"forward search found none in {bvh.Arrivals} arrivals ({jumps} backjumps, {repeats} repeated states; deepest {deepest} of {total}; visits by depth {string.Join(",", visits.Take(deepest + 1))}{(truthLost == "" ? "" : "; " + truthLost)})";
        report += $"; feasibility failures {string.Join(" ", bvh.FeasibleFailures.Select((c, i) => (c, i)).Where(x => x.c > 0).OrderByDescending(x => x.c).Take(4).Select(x => $"node {x.i}: {x.c}"))}";
        report += $"; cyclic failures {string.Join(" ", bvh.CyclicFailures.Select((c, i) => (c, i)).Where(x => x.c > 0).OrderByDescending(x => x.c).Take(4).Select(x => $"node {x.i}: {x.c}"))}";
        report += $"; flush failures {string.Join(" ", bvh.FlushFailures.Select((c, i) => (c, i)).Where(x => x.c > 0).OrderByDescending(x => x.c).Take(6).Select(x => $"node {x.i} ({bvh.Size(x.i)}): {x.c}"))}";
        report = report.Replace("visits by depth " + string.Join(",", visits.Take(deepest + 1)), $"visits ending {string.Join(",", visits.Skip(Math.Max(0, deepest - 5)).Take(6))}");
        return found;
    }

    /// <summary>
    /// A soup's input order as a constraint problem, for an outside solver (<see cref="OrderSolver"/>):
    /// the soup's corners (vertex ids), each quad a triangle can begin (it first, then its partner, with the
    /// corner starts the pair can take, as rotations of each one's corners), the BVH trace in pre-order as
    /// soup ids, the lag window and a time limit.
    /// </summary>
    public sealed record OrderProblem(int Vertices, int[][] Soup, (int First, int Second, (int First, int Second)[] Starts)[] Quads,
                                      (int[] Set, int[]? Left, bool ByCount)[] Trace, int Lag, double Seconds);

    /// <summary>
    /// Solves an <see cref="OrderProblem"/>: soup ids by input slot (null when none was found in time) and a
    /// report. Set by the host (Source2.Compiler.Solvers' WorldOrderCpSat), so this library takes no solver
    /// dependency; unset, the forward search runs on its own.
    /// </summary>
    public static Func<OrderProblem, (int[]? Order, string Report)>? OrderSolver;

    private static int[]? SolveOrder(List<(int[] Entry, HashSet<int>? Left, bool ByCount)> trace, Func<int, int> soupOf, Vector3[] vertices,
                                     List<(int A, int B, int C)> soup, int lag, out string report)
    {
        report = "";
        if (OrderSolver is null)
            return null;
        var (begins, _) = Begins(soup, vertices);
        var quads = new Dictionary<(int, int), HashSet<(int, int)>>();
        for (var t = 0; t < soup.Count; t++)
            foreach (var (u, _, c) in begins[t])
                if (u >= 0)
                {
                    int[] own = [soup[t].A, soup[t].B, soup[t].C], other = [soup[u].A, soup[u].B, soup[u].C];
                    if (!quads.TryGetValue((t, u), out var starts))
                        quads[(t, u)] = starts = [];
                    starts.Add((Array.IndexOf(own, c[0]), Array.IndexOf(other, c[3])));
                }
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_SECONDS"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1800;
        var problem = new OrderProblem(vertices.Length, [.. soup.Select(t => (int[])[t.A, t.B, t.C])],
            [.. quads.Select(q => (q.Key.Item1, q.Key.Item2, q.Value.Order().ToArray()))],
            [.. trace.Select(n => (n.Entry.Select(soupOf).ToArray(), n.Left?.Select(soupOf).ToArray(), n.ByCount))], lag, seconds);
        var (order, said) = OrderSolver(problem);
        report = said;
        return order;
    }

    /// <summary>
    /// The faces each soup triangle can begin: itself alone from each corner, or a quad (with a coplanar
    /// neighbour across an edge) that the triangulator cuts into it and then that neighbour; and each
    /// triangle's possible quad partners.
    /// </summary>
    internal static (List<(int Partner, int[] Loop, int[] Corners)>[] Begins, List<int>[] Partners) Begins(List<(int A, int B, int C)> soup, Vector3[] vertices)
    {
        var byEdge = new Dictionary<(int, int), int>();
        for (var t = 0; t < soup.Count; t++)
            foreach (var (x, y) in (ValueTuple<int, int>[])[(soup[t].A, soup[t].B), (soup[t].B, soup[t].C), (soup[t].C, soup[t].A)])
                byEdge[(x, y)] = t;
        Vector3 Normal(int k) => Vector3.Normalize(Vector3.Cross(vertices[soup[k].B] - vertices[soup[k].A], vertices[soup[k].C] - vertices[soup[k].A]));
        var begins = new List<(int Partner, int[] Loop, int[] Corners)>[soup.Count];
        var partners = new List<int>[soup.Count];
        for (var t = 0; t < soup.Count; t++)
        {
            int[] own = [soup[t].A, soup[t].B, soup[t].C];
            begins[t] = [.. Enumerable.Range(0, 3).Select(r => (-1, (int[])[own[r], own[(r + 1) % 3], own[(r + 2) % 3]], (int[])[own[r], own[(r + 1) % 3], own[(r + 2) % 3]]))];
            partners[t] = [];
            for (var x = 0; x < 3; x++)
            {
                if (!byEdge.TryGetValue((own[(x + 1) % 3], own[x]), out var u) || u == t)
                    continue;
                var (n1, n2) = (Normal(t), Normal(u));
                if (!(Vector3.Dot(n1, n2) > 1 - 1e-5f && MathF.Abs(Vector3.Dot(n1, vertices[own[0]]) - Vector3.Dot(n1, vertices[soup[u].A])) < 1e-3f))
                    continue;
                partners[t].Add(u);
                int[] other = [soup[u].A, soup[u].B, soup[u].C];
                var third = other.First(v => !own.Contains(v));
                int[] loop = [.. own.Take(x + 1), third, .. own.Skip(x + 1)];
                for (var r = 0; r < 4; r++)
                {
                    var rotated = loop.Skip(r).Concat(loop.Take(r)).ToArray();
                    var cut = PolygonTriangulator.Triangulate([.. rotated.Select(v => vertices[v])]);
                    if (cut.Length != 6)
                        continue;
                    var corners = cut.Select(i => rotated[i]).ToArray();
                    if (Norm((corners[0], corners[1], corners[2])) == Norm(soup[t]) && Norm((corners[3], corners[4], corners[5])) == Norm(soup[u]))
                        begins[t].Add((u, rotated, corners));
                }
            }
        }
        return (begins, partners);
    }

    /// <summary>Debugging: DECOMPILE_TRUTH=&lt;file of the source's input triangles, as SoupInputsOfTwoMaps
    /// dumps them&gt; as soup triangle ids in input order (each matched within 1/32), to keep a search to it.</summary>
    private static int[]? Truth(List<(int A, int B, int C)> soup, Vector3[] vertices)
    {
        int[]? truth = null;
        // DECOMPILE_TRUTH_IDS=<file>: an order as soup triangle ids (an outside solver's answer).
        if (Environment.GetEnvironmentVariable("DECOMPILE_TRUTH_IDS") is { Length: > 0 } idsFile && File.Exists(idsFile))
        {
            var ids = File.ReadAllText(idsFile).Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            if (ids.Length == soup.Count)
                return ids;
        }
        if (Environment.GetEnvironmentVariable("DECOMPILE_TRUTH") is { Length: > 0 } truthFile && File.Exists(truthFile))
        {
            var lines = File.ReadAllLines(truthFile);
            if (lines.Length == soup.Count)
                truth = [.. lines.Select(line =>
                {
                    var c = line.Split(' ').Select(q => q.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).Select(q => new Vector3(q[0], q[1], q[2])).ToArray();
                    bool Near(Vector3 a, Vector3 b) => MathF.Max(MathF.Abs(a.X - b.X), MathF.Max(MathF.Abs(a.Y - b.Y), MathF.Abs(a.Z - b.Z))) <= 1 / 32f;
                    return Enumerable.Range(0, soup.Count).FirstOrDefault(t => Enumerable.Range(0, 3).Any(r =>
                        Near(c[0], vertices[(r == 0 ? soup[t].A : r == 1 ? soup[t].B : soup[t].C)]) &&
                        Near(c[1], vertices[(r == 0 ? soup[t].B : r == 1 ? soup[t].C : soup[t].A)]) &&
                        Near(c[2], vertices[(r == 0 ? soup[t].C : r == 1 ? soup[t].A : soup[t].B)])), -1);
                })];
        }
        return truth;
    }

    [ThreadStatic] private static List<(int A, int B, int C)>? soupTriangles;

    internal static string? Repair(List<List<Face>> members, List<(int A, int B, int C)> shipped, Vector3[] vertices, List<string> notes)
    {
        if (members.Sum(m => m.Count) == 0)
            return null;
        soupTriangles = shipped;
        static (int, int, int) Bits(Vector3 v) => (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
        static string Key(Vector3 a, Vector3 b, Vector3 c)
        {
            (int, int, int)[] k = [Bits(a), Bits(b), Bits(c)];
            var r = Enumerable.Range(0, 3).MinBy(i => k[i]);
            return $"{k[r]}{k[(r + 1) % 3]}{k[(r + 2) % 3]}";
        }
        var want = shipped.Select(t => Key(vertices[t.A], vertices[t.B], vertices[t.C])).ToList();
        var wantAt = new Dictionary<string, int>();
        for (var k = 0; k < want.Count; k++)
            wantAt.TryAdd(want[k], k);
        // The output for an order, scored by its leading run that agrees with the shipped triangles, then
        // how many more sit in place ((run << 20) + in place; -1 when its vertices are not the shipped ones,
        // which a reorder must keep), and the input's face per triangle key.
        (int Score, List<string> Got, Dictionary<string, int> FaceOf) Run(List<Face> flat)
        {
            var (indices, positions, faceOf) = SoupInput(flat, vertices);
            var mesh = Physics.RnMeshBuilder.Create(indices, positions, null);
            var byKey = new Dictionary<string, int>();
            for (var t = 0; t < faceOf.Length; t++)
                byKey.TryAdd(Key(positions[indices[t * 3]], positions[indices[t * 3 + 1]], positions[indices[t * 3 + 2]]), faceOf[t]);
            if (mesh is null || mesh.Vertices.Length != vertices.Length || !mesh.Vertices.Select(Bits).SequenceEqual(vertices.Select(Bits)))
                return (-1, [], byKey);
            var got = mesh.Triangles.Select(t => Key(mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C])).ToList();
            var (score, run) = (0, -1);
            for (var k = 0; k < Math.Min(got.Count, want.Count); k++)
                if (got[k] == want[k])
                    score++;
                else if (run < 0)
                    run = k;
            if (run < 0)
                run = Math.Min(got.Count, want.Count);
            return ((run << 20) + score, got, byKey);
        }
        static int InPlace(int score) => score & 0xfffff;
        // Each face's member, kept through restarts and moves, so the members come back as runs.
        var memberOf = new Dictionary<Face, int>(ReferenceEqualityComparer.Instance);
        for (var m = 0; m < members.Count; m++)
            foreach (var f in members[m])
                memberOf[f] = m;
        Face Keep(Face old, Face made)
        {
            memberOf[made] = memberOf[old];
            return made;
        }
        var flat = members.SelectMany(m => m).ToList();
        var (moves, restarts, pairings, inverted) = (0, 0, 0, 0);
        var searched = "no search";
        var current = Run(flat);
        if (current.Score < 0)
            return "the decompile's own soup input does not give the shipped vertices";

        // The partitions inverted: an input that gives the shipped leaves, nearest our order, put back
        // into faces under the vertex order; again from the result while it gets further.
        for (var pass = 0; pass < 32 && InPlace(current.Score) < want.Count; pass++)
        {
            var (indices, positions, faceOf) = SoupInput(flat, vertices);
            var shippedRank = new Dictionary<int, int>();
            for (var t = 0; t < faceOf.Length; t++)
                if (wantAt.TryGetValue(Key(positions[indices[t * 3]], positions[indices[t * 3 + 1]], positions[indices[t * 3 + 2]]), out var r))
                    shippedRank[t] = r;
            Physics.RnMeshBuilder.Trace = [];
            Physics.RnMeshBuilder.Create(indices, positions, null);
            var trace = Physics.RnMeshBuilder.Trace;
            Physics.RnMeshBuilder.Trace = null;
            var at = 0;
            var pattern = new Dictionary<int, List<bool>>();
            var preimage = Preimage(trace, ref at, shippedRank, [.. Enumerable.Range(0, faceOf.Length)], faceOf, pattern);
            var position = new int[faceOf.Length];
            for (var i = 0; i < preimage.Count; i++)
                position[preimage[i]] = i;
            if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
            {
                var raw = preimage.SelectMany(t => (int[])[indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2]]).ToArray();
                var check = Physics.RnMeshBuilder.Create(raw, positions, null)!;
                var rawGot = check.Triangles.Select(t => Key(check.Vertices[t.A], check.Vertices[t.B], check.Vertices[t.C])).ToList();
                var rawRun = 0;
                while (rawRun < rawGot.Count && rawGot[rawRun] == want[rawRun])
                    rawRun++;
                var split = Enumerable.Range(0, flat.Count).Count(f => Enumerable.Range(0, faceOf.Length).Where(t => faceOf[t] == f).Select(t => position[t]).ToList() is var ps && ps.Max() - ps.Min() != ps.Count - 1);
                var firstSeen = new List<int>();
                var met = new HashSet<int>();
                foreach (var v in raw)
                    if (met.Add(v))
                        firstSeen.Add(Array.FindIndex(vertices, x => Bits(x) == Bits(positions[v])));
                var outOfOrder = firstSeen.Where((v, i) => v != i).Count();
                notes.Add($"world debug: pass {pass}: {outOfOrder} vertices first met out of order; raw preimage output agrees for {rawRun} of {want.Count}, in place {Enumerable.Range(0, Math.Min(rawGot.Count, want.Count)).Count(k => rawGot[k] == want[k])}; {split} faces not contiguous in it; leaves {trace.Count(x => x.Left is null)}");
            }
            // Soup triangle -> its input position in the preimage, through the face's cut.
            var soupPosition = new Dictionary<int, int>();
            var priority = new double[flat.Count];
            Array.Fill(priority, double.MaxValue);
            {
                var t = 0;
                for (var f = 0; f < flat.Count; f++)
                {
                    var loop = flat[f].Loop;
                    var cut = loop.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate([.. loop.Select(v => vertices[v])]);
                    for (var i = 0; i + 2 < cut.Length; i += 3, t++)
                    {
                        var key = Norm((loop[cut[i]], loop[cut[i + 1]], loop[cut[i + 2]]));
                        var soup = flat[f].Triangles.FirstOrDefault(x => Norm(shipped[x]) == key, -1);
                        if (soup >= 0)
                            soupPosition[soup] = position[t];
                        priority[f] = Math.Min(priority[f], position[t]);
                    }
                }
            }
            // The search, guided by the inverted order.
            {
                var soupOf = new int[faceOf.Length];
                var t = 0;
                foreach (var face in flat)
                {
                    var loop = face.Loop;
                    var cut = loop.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate([.. loop.Select(v => vertices[v])]);
                    for (var i = 0; i + 2 < cut.Length; i += 3, t++)
                        soupOf[t] = face.Triangles.FirstOrDefault(x => Norm(shipped[x]) == Norm((loop[cut[i]], loop[cut[i + 1]], loop[cut[i + 2]])), -1);
                }
                var budget = long.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_BUDGET"), out var b) ? b : 5_000_000;
                // Each node's choices first as our own order makes them (its entry read from the end), or
                // with DECOMPILE_SEARCH_PREFER=inverted as the inverted order's.
                var own = new Dictionary<int, List<bool>>();
                for (var i = 0; i < trace.Count; i++)
                    if (trace[i].Left is { } l && !trace[i].ByCount)
                        own[i] = [.. trace[i].Entry.Reverse().Select(l.Contains)];
                // Triangles tried in our order, or with DECOMPILE_SEARCH_GUIDE=preimage in the inverted order's
                // (which gives every leaf already).
                var rankOfSoup = new int[shipped.Count];
                for (var i = 0; i < soupOf.Length; i++)
                    if (soupOf[i] >= 0)
                        rankOfSoup[soupOf[i]] = Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_GUIDE") == "preimage" ? position[i] : i;
                // DECOMPILE_SEARCH_DIRECTION=backward: from the input's end (exact BVH, hidden choices);
                // =forward: from its start on its own; otherwise the order solver's answer (OrderSolver,
                // when the host set one) followed by the forward search, which puts the faces back.
                var direction = Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_DIRECTION");
                var lag = int.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_LAG"), out var lg) ? lg : 8;
                var solved = "";
                var guide = Truth(shipped, vertices) ?? (direction is "forward" or "backward" ? null : SolveOrder(trace, x => soupOf[x], vertices, shipped, lag, out solved));
                var found = direction == "backward"
                    ? BackwardSearch(trace, x => soupOf[x], vertices, shipped,
                        Environment.GetEnvironmentVariable("DECOMPILE_SEARCH_PREFER") == "inverted" ? pattern : own, rankOfSoup, budget, out searched)
                    : ForwardSearch(trace, x => soupOf[x], vertices, shipped, rankOfSoup, budget, lag, guide, out searched);
                if (solved.Length > 0)
                    searched = $"{solved}; {searched}";
                if (found is not null)
                {
                    var memberOfTriangle = flat.SelectMany(x => x.Triangles.Select(t => (t, m: memberOf[x]))).ToDictionary(x => x.t, x => x.m);
                    foreach (var face in found)
                        memberOf[face] = memberOfTriangle[face.Triangles[0]];
                    var r = Run(found);
                    if (r.Score > current.Score)
                    {
                        (flat, current) = (found, r);
                        break;
                    }
                    searched += $" but it scores {InPlace(r.Score)}";
                }
            }
            var projected = Project(flat, priority, x => soupPosition.GetValueOrDefault(x, int.MaxValue), vertices);
            for (var f = 0; f < projected.Count; f++)
                memberOf[projected[f]] = memberOf[flat.First(x => ReferenceEquals(x.Triangles, projected[f].Triangles))];
            var tried = Run(projected);
            if (tried.Score <= current.Score)
                break;
            (flat, current) = (projected, tried);
            inverted++;
        }

        // Then local tries: at the mismatches in turn, the faces of the shipped and our triangle from their
        // other starts, paired otherwise with each other, and at every place; failing those, any other face
        // just before and after the first mismatch's two (a left-going triangle arriving between two right
        // ones swaps them in the partition). The best try scoring more is taken.
        for (var round = 0; round < 4 * flat.Count + 16 && InPlace(current.Score) < want.Count; round++)
        {
            var best = (Score: current.Score, Flat: (List<Face>?)null, Kind: "");
            void Try(List<Face> order, string kind)
            {
                var r = Run(order);
                if (r.Score > best.Score)
                    best = (r.Score, order, kind);
            }
            List<Face> Moved(int from, int to)
            {
                var order = flat.ToList();
                var face = order[from];
                order.RemoveAt(from);
                order.Insert(to, face);
                return order;
            }
            var tried = new HashSet<int>();
            for (var k = 0; k < want.Count && best.Flat is null; k++)
            {
                if (k < current.Got.Count && current.Got[k] == want[k])
                    continue;
                foreach (var key in (string[])[want[k], k < current.Got.Count ? current.Got[k] : want[k]])
                {
                    if (!current.FaceOf.TryGetValue(key, out var f) || !tried.Add(f))
                        continue;
                    foreach (var loop in flat[f].Starts.Where(l => !l.SequenceEqual(flat[f].Loop)))
                    {
                        var order = flat.ToList();
                        order[f] = Keep(flat[f], flat[f] with { Loop = loop });
                        Try(order, "restart");
                    }
                    var other = k < current.Got.Count && current.FaceOf.TryGetValue(key == want[k] ? current.Got[k] : want[k], out var g) ? g : f;
                    if (other != f && flat[f].Triangles.Count + flat[other].Triangles.Count <= 4)
                    {
                        var (lo, hi) = (Math.Min(f, other), Math.Max(f, other));
                        foreach (var pairing in Pairings([.. flat[f].Triangles, .. flat[other].Triangles], shipped, vertices))
                            foreach (var faces in Orders(pairing))
                            {
                                var order = flat.ToList();
                                order.RemoveAt(hi);
                                order.RemoveAt(lo);
                                order.InsertRange(lo, faces.Select(x => Keep(flat[lo], x)));
                                Try(order, "pairing");
                            }
                    }
                    for (var at = 0; at < flat.Count; at++)
                        if (at != f)
                            Try(Moved(f, at), "move");
                }
            }
            if (best.Flat is null)
            {
                var k = 0;
                while (k < current.Got.Count && current.Got[k] == want[k])
                    k++;
                if (k < current.Got.Count && current.FaceOf.TryGetValue(want[k], out var f1) && current.FaceOf.TryGetValue(current.Got[k], out var f2))
                    for (var h = 0; h < flat.Count; h++)
                        foreach (var at in (int[])[f1, f1 + 1, f2, f2 + 1])
                        {
                            var to = h < at ? at - 1 : at;
                            if (h != f1 && h != f2 && to != h && to >= 0 && to < flat.Count)
                                Try(Moved(h, to), "move");
                        }
            }
            if (best.Flat is null)
                break;
            if (best.Kind == "restart") restarts++; else if (best.Kind == "pairing") pairings++; else moves++;
            flat = best.Flat;
            current = Run(flat);
        }
        if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
        {
            var k = 0;
            while (k < current.Got.Count && current.Got[k] == want[k])
                k++;
            if (k < want.Count && k < current.Got.Count)
            {
                string Describe(string key)
                {
                    if (!current.FaceOf.TryGetValue(key, out var f))
                        return "not in our input";
                    var seen = new HashSet<int>(flat.Take(f).SelectMany(x => x.Loop));
                    return $"face {f} of {flat.Count} (member {memberOf[flat[f]]}, {flat[f].Loop.Length} corners [{string.Join(",", flat[f].Loop)}], {flat[f].Starts.Count} starts, new [{string.Join(",", flat[f].Loop.Where(v => !seen.Contains(v)))}]), shipped at {want.IndexOf(key)}, ours at {current.Got.IndexOf(key)}";
                }
                notes.Add($"world debug: first mismatch {k}: shipped {Describe(want[k])}; ours {Describe(current.Got[k])}");
            }
        }
        // Back into members: runs of one member's faces.
        members.Clear();
        foreach (var f in flat)
        {
            if (members.Count == 0 || memberOf[members[^1][0]] != memberOf[f])
                members.Add([]);
            members[^1].Add(f);
        }
        soupTriangles = null;
        var prefix = current.Score >> 20;
        return InPlace(current.Score) == want.Count && current.Got.Count == want.Count
            ? $"BVH output exact ({searched}; {inverted} inversion passes, {moves} faces moved, {restarts} restarted, {pairings} paired otherwise)"
            : $"BVH output has {InPlace(current.Score)} of {want.Count} triangles in place, the first {prefix} in a run ({searched}; {inverted} inversion passes, {moves} faces moved, {restarts} restarted, {pairings} paired otherwise)";
    }

    /// <summary>A face rebuilt from soup triangles: its loop (soup vertex ids), material, the triangles it cuts
    /// into (indices into the piece) and the loop starts at which the triangulator cuts it into them.</summary>
    internal sealed record Polygon(int[] Loop, int Material, List<int> Triangles, List<int> Starts);

    private static (int, int, int) Norm((int, int, int) t)
    {
        var (a, b, c) = t;
        return a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
    }

    /// <summary>
    /// The source's faces back, per material: triangles in one plane (unit normals within 1e-5, distances
    /// within 1e-3) that share an edge, the other way round, are merged one at a time, a merge kept only
    /// when PolygonTriangulator (the compile's own cut, on world positions as for a world mesh) cuts the
    /// merged loop, from some start, into exactly the triangles merged, so nothing is guessed: a face the
    /// compile would cut otherwise stays split. Neighbours are found by directed edge.
    /// </summary>
    /// <summary>The most triangles a rebuilt face holds (DECOMPILE_FACE_TRIANGLES, default 2: quads). The
    /// triangulator check alone also merges neighbouring coplanar source faces (ze_hold_em_p's strip of
    /// quads at z 153 into one long face, whose vertices no single run then brings in).</summary>
    private static int MaxFaceTriangles => int.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_FACE_TRIANGLES"), out var n) ? n : 2;

    internal static List<Polygon> Polygons(List<(int A, int B, int C)> piece, int[] material, Vector3[] vertices)
    {
        var polygons = new List<Polygon?>();
        var byEdge = new Dictionary<(int, int), int>();
        void Index(int f)
        {
            var loop = polygons[f]!.Loop;
            for (var i = 0; i < loop.Length; i++)
                byEdge[(loop[i], loop[(i + 1) % loop.Length])] = f;
        }
        void Unindex(int f)
        {
            var loop = polygons[f]!.Loop;
            for (var i = 0; i < loop.Length; i++)
                if (byEdge.TryGetValue((loop[i], loop[(i + 1) % loop.Length]), out var g) && g == f)
                    byEdge.Remove((loop[i], loop[(i + 1) % loop.Length]));
        }
        for (var k = 0; k < piece.Count; k++)
        {
            polygons.Add(new Polygon([piece[k].A, piece[k].B, piece[k].C], material[k], [k], [0, 1, 2]));
            Index(k);
        }
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
        // Faces of two triangles (the default): every pair that may merge, the closest in vertex index first
        // (a source face brings its vertices in one run, so a real pair's ids lie together; on a coplanar
        // strip a triangle can also pair with its neighbour quad's, which the triangulator accepts too).
        if (MaxFaceTriangles == 2)
        {
            var pairs = new List<(int Span, int I, int J, int[] Loop, List<int> Starts)>();
            for (var i = 0; i < polygons.Count; i++)
            {
                var p = polygons[i]!;
                for (var x = 0; x < 3; x++)
                {
                    int a = p.Loop[x], b = p.Loop[(x + 1) % 3];
                    if (!byEdge.TryGetValue((b, a), out var j) || j <= i || polygons[j] is not { } q)
                        continue;
                    if (p.Material != q.Material || !SamePlane(i, j))
                        continue;
                    var y = Array.IndexOf(q.Loop, b);
                    var other = q.Loop[(y + 2) % 3];
                    if (p.Loop.Contains(other))
                        continue;
                    var loop = p.Loop.Take(x + 1).Append(other).Concat(p.Loop.Skip(x + 1)).ToArray();
                    var starts = StartsCutting(loop, [Norm(piece[i]), Norm(piece[j])]);
                    if (starts.Count > 0)
                        pairs.Add((loop.Max() - loop.Min(), i, j, loop, starts));
                }
            }
            var paired = new bool[polygons.Count];
            foreach (var (_, i, j, loop, starts) in pairs.OrderBy(x => x.Span).ThenBy(x => x.I).ThenBy(x => x.J))
            {
                if (paired[i] || paired[j])
                    continue;
                (paired[i], paired[j]) = (true, true);
                polygons[i] = new Polygon(loop, polygons[i]!.Material, [i, j], starts);
                polygons[j] = null;
            }
            return [.. polygons.OfType<Polygon>()];
        }
        var queue = new Queue<int>(Enumerable.Range(0, polygons.Count));
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            if (polygons[i] is not { } p)
                continue;
            for (var x = 0; x < p.Loop.Length; x++)
            {
                int a = p.Loop[x], b = p.Loop[(x + 1) % p.Loop.Length];
                if (!byEdge.TryGetValue((b, a), out var j) || j == i || polygons[j] is not { } q)
                    continue;
                if (p.Material != q.Material || !SamePlane(p.Triangles[0], q.Triangles[0])
                    || p.Triangles.Count + q.Triangles.Count > MaxFaceTriangles)
                    continue;
                var y = Array.IndexOf(q.Loop, b);
                var between = Enumerable.Range(2, q.Loop.Length - 2).Select(t => q.Loop[(y + t) % q.Loop.Length]).ToArray();
                if (between.Any(v => p.Loop.Contains(v)))
                    continue;
                var loop = p.Loop.Take(x + 1).Concat(between).Concat(p.Loop.Skip(x + 1)).ToArray();
                var want = p.Triangles.Concat(q.Triangles).Select(t => Norm(piece[t])).ToHashSet();
                var starts = StartsCutting(loop, want);
                if (starts.Count == 0)
                    continue;
                Unindex(i);
                Unindex(j);
                // The merged face keeps the earlier one's place.
                var (keep, drop) = i < j ? (i, j) : (j, i);
                polygons[keep] = new Polygon(loop, p.Material, [.. p.Triangles, .. q.Triangles], starts);
                polygons[drop] = null;
                Index(keep);
                queue.Enqueue(keep);
                break;
            }
        }
        return [.. polygons.OfType<Polygon>()];
    }

    /// <summary>
    /// The faces in an order (each from one of its cutting starts) whose cut corners, in the triangulator's
    /// order, meet the member's vertices in the soup's order (<see cref="FirstUseOrder"/> over faces; the
    /// vertices before <paramref name="first"/> are earlier members', met already): for each vertex in turn,
    /// the first unused face holding it with a start that brings its new vertices in ascending order and no
    /// other new one, the start cutting its triangles in their shipped order where several do (the BVH
    /// keeps a leaf's triangles in input order); then every face whose corners are all met. A face no step
    /// reaches goes at the end. Returns each face's loop from its start.
    /// </summary>
    internal static List<(int[] Loop, int Material, List<int> Triangles, List<int[]> Starts)> PolygonOrder(List<Polygon> polygons, Vector3[] vertices,
                                                                                       List<(int A, int B, int C)> piece, int first = 0)
    {
        var order = new List<(int[], int, List<int>, List<int[]>)>();
        var done = new bool[polygons.Count];
        var holding = new Dictionary<int, List<int>>();
        for (var f = 0; f < polygons.Count; f++)
            foreach (var v in polygons[f].Loop.Distinct())
                (holding.TryGetValue(v, out var l) ? l : holding[v] = []).Add(f);
        var seen = new HashSet<int>(holding.Keys.Where(v => v < first));
        int[] Cut(int[] loop, int start)
        {
            var rotated = loop.Skip(start).Concat(loop.Take(start)).ToArray();
            var cut = loop.Length == 3 ? [0, 1, 2] : PolygonTriangulator.Triangulate([.. rotated.Select(v => vertices[v])]);
            return [.. cut.Select(i => rotated[i])];
        }
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
        int Best(int f) => polygons[f].Starts.OrderBy(x => Inversions(polygons[f], x)).First();
        void Emit(int f, int start)
        {
            done[f] = true;
            var p = polygons[f];
            order.Add(([.. p.Loop.Skip(start).Concat(p.Loop.Take(start))], p.Material, p.Triangles,
                       [.. p.Starts.Select(x => p.Loop.Skip(x).Concat(p.Loop.Take(x)).ToArray())]));
            foreach (var v in p.Loop)
                seen.Add(v);
        }
        var own = holding.Keys.Where(v => v >= first).Order().ToList();
        var position = own.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        foreach (var next in own)
        {
            if (seen.Contains(next))
                continue;
            // Of the faces that can bring it in, the one bringing the fewest new vertices (the most attached
            // to what is already met: ze_hold_em_p's first block takes its top, sharing an edge with the face
            // before, ahead of a side sharing none), then the earliest.
            (int Face, int Start, int Fresh)? pick = null;
            foreach (var f in holding[next])
            {
                if (done[f])
                    continue;
                var fresh = polygons[f].Loop.Count(v => !seen.Contains(v));
                if (pick is { } p && p.Fresh <= fresh)
                    continue;
                var at = polygons[f].Starts.Where(s =>
                {
                    var cutFresh = Cut(polygons[f].Loop, s).Where(v => !seen.Contains(v)).Distinct().ToList();
                    var expected = own.Skip(position[next]).Take(cutFresh.Count);
                    return cutFresh.Count > 0 && cutFresh.SequenceEqual(expected);
                }).OrderBy(s => Inversions(polygons[f], s)).DefaultIfEmpty(-1).First();
                if (at >= 0)
                    pick = (f, at, fresh);
            }
            if (pick is { } chosen)
            {
                Emit(chosen.Face, chosen.Start);
                foreach (var g in polygons[chosen.Face].Loop.SelectMany(v => holding[v]).Distinct().Order().ToList())
                    if (!done[g] && polygons[g].Loop.All(seen.Contains))
                        Emit(g, Best(g));
            }
        }
        for (var f = 0; f < polygons.Count; f++)
            if (!done[f])
                Emit(f, Best(f));
        return order;
    }

    /// <summary>
    /// The soup's members (each a source mesh's piece of one material, as the physics gathers them) back from
    /// its vertex order: RnMeshCreate's weld keeps clusters in the order they form, so each member's new
    /// vertices are a run of their own, in member order. A triangle belongs to the member that introduced
    /// its highest vertex. Walking the vertices in order, a new member starts at a vertex whose introducing
    /// triangles share no vertex with the current member's run, or carry another material. Returns each
    /// member's triangles (soup order) and its first vertex.
    /// </summary>
    internal static List<(List<int> Triangles, int First)> Members(List<(int A, int B, int C)> triangles, string[] material, int vertexCount)
    {
        var introducing = new List<int>[vertexCount];
        for (var t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            (introducing[Math.Max(a, Math.Max(b, c))] ??= []).Add(t);
        }
        var members = new List<(List<int>, int)>();
        List<int>? current = null;
        var start = 0;
        var last = -1;
        string? currentMaterial = null;
        for (var v = 0; v < vertexCount; v++)
        {
            if (introducing[v] is not { } list)
                continue;
            var joins = current is not null && list.All(t => material[t] == currentMaterial)
                        && list.Any(t => new[] { triangles[t].A, triangles[t].B, triangles[t].C }.Any(u => u >= start && u < v));
            if (!joins)
            {
                // Every vertex past the last one an earlier member introduced is this member's.
                start = last + 1;
                current = [];
                currentMaterial = material[list[0]];
                members.Add((current, start));
            }
            current!.AddRange(list);
            last = v;
        }
        return [.. members.Select(m => (m.Item1.Order().ToList(), m.Item2))];
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
                        // A map mesh's draw may split its streams (SplitDepthStream: positions in one buffer,
                        // the rest in the next, one vertex numbering): each stream from the buffer that has it.
                        var buffers = call.GetArray("m_vertexBuffers").Select(b => (Buffer: mesh.VBIB.VertexBuffers[b.GetInt32Property("m_hBuffer")],
                                                                                        Offset: (int)(b.ContainsKey("m_nBindOffsetBytes") ? b.GetInt32Property("m_nBindOffsetBytes") : 0))).ToList();
                        var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        var withPosition = buffers.FirstOrDefault(b => b.Buffer.InputLayoutFields.Any(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase)));
                        var withTexcoord = buffers.FirstOrDefault(b => b.Buffer.InputLayoutFields.Any(f => f.SemanticName.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == 0));
                        if (withPosition.Buffer.InputLayoutFields is null || withTexcoord.Buffer.InputLayoutFields is null)
                            continue;
                        var (pb, tb) = (withPosition.Buffer, withTexcoord.Buffer);
                        var position = pb.InputLayoutFields.First(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase));
                        var texcoord = tb.InputLayoutFields.First(f => f.SemanticName.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == 0);
                        if (position.Format.ToString() != "R32G32B32_FLOAT" || texcoord.Format.ToString() != "R32G32_FLOAT")
                        {
                            if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
                                Console.Error.WriteLine($"world debug: render draw {path} {material} skipped: {string.Join(" | ", buffers.Select(b => string.Join(", ", b.Buffer.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}@{f.Offset}"))))}");
                            continue;
                        }
                        Vector3 P(int v) => new(BitConverter.ToSingle(pb.Data, withPosition.Offset + v * (int)pb.ElementSizeInBytes + (int)position.Offset),
                                                BitConverter.ToSingle(pb.Data, withPosition.Offset + v * (int)pb.ElementSizeInBytes + (int)position.Offset + 4),
                                                BitConverter.ToSingle(pb.Data, withPosition.Offset + v * (int)pb.ElementSizeInBytes + (int)position.Offset + 8));
                        Vector2 T(int v) => new(BitConverter.ToSingle(tb.Data, withTexcoord.Offset + v * (int)tb.ElementSizeInBytes + (int)texcoord.Offset),
                                                BitConverter.ToSingle(tb.Data, withTexcoord.Offset + v * (int)tb.ElementSizeInBytes + (int)texcoord.Offset + 4));
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = call.GetInt32Property("m_nStartIndex");
                        var count = call.GetInt32Property("m_nIndexCount");
                        var bas = call.GetInt32Property("m_nBaseVertex");
                        if (Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DUMP") is { Length: > 0 } dump)
                            File.AppendAllLines(dump, [$"# {path} {material} {start} {count}", .. Enumerable.Range(0, count / 3)
                                .Select(k => string.Join(" ", Enumerable.Range(0, 3).Select(c => P(I(start + k * 3 + c) + bas)).Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}")))]);
                        // DECOMPILE_LMDENSITY=<file>: each triangle's world and lightmap-texcoord areas (TEXCOORD3).
                        if (Environment.GetEnvironmentVariable("DECOMPILE_LMDENSITY") is { Length: > 0 } density
                            && buffers.Select(b => (b, f: b.Buffer.InputLayoutFields.FirstOrDefault(f => f.SemanticName.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == 3)))
                                      .FirstOrDefault(x => x.f.Format.ToString() == "R16G16_UNORM") is { b.Buffer.Data: not null } lm)
                        {
                            Vector2 L(int v) => new(BitConverter.ToUInt16(lm.b.Buffer.Data, lm.b.Offset + v * (int)lm.b.Buffer.ElementSizeInBytes + (int)lm.f.Offset) / 65535f,
                                                    BitConverter.ToUInt16(lm.b.Buffer.Data, lm.b.Offset + v * (int)lm.b.Buffer.ElementSizeInBytes + (int)lm.f.Offset + 2) / 65535f);
                            var lines = new List<string>();
                            for (var i = start; i + 2 < start + count; i += 3)
                            {
                                int a = I(i) + bas, b = I(i + 1) + bas, c = I(i + 2) + bas;
                                var world = Vector3.Cross(P(b) - P(a), P(c) - P(a)).Length() / 2;
                                var (la, lb, lc) = (L(a), L(b), L(c));
                                var uvArea = MathF.Abs(((lb.X - la.X) * (lc.Y - la.Y)) - ((lc.X - la.X) * (lb.Y - la.Y))) / 2;
                                var centre = (P(a) + P(b) + P(c)) / 3;
                                lines.Add($"{Path.GetFileNameWithoutExtension(material)} {centre.X:F1} {centre.Y:F1} {centre.Z:F1} {world:G6} {uvArea:G6}");
                            }
                            File.AppendAllLines(density, lines);
                        }
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
