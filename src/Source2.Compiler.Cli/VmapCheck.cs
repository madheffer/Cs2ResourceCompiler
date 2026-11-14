using Source2.Compiler.Dmx;

namespace Source2.Compiler.Cli;

/// <summary>
/// `s2c vmap-check <file.vmap>...`: every CDmePolygonMesh in a .vmap checked against the half-edge
/// rules a mesh Hammer loads keeps (opposite pairs, next a permutation, face and boundary loops that
/// close, one face per directed edge, one fan per vertex, indices in range). Counts per rule and
/// file, with the first examples; meant to set our decompiles beside Valve-saved sources.
/// </summary>
internal static class VmapCheck
{
    public static int Run(string[] a)
    {
        var verbose = a.Contains("--verbose");
        var bad = 0;
        foreach (var path in a.Where(x => !x.StartsWith('-')))
        {
            DmxBinary.Document doc;
            try
            {
                doc = DmxBinary.ReadFile(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(path)}: not read ({ex.Message})");
                continue;
            }
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var examples = new Dictionary<string, string>();
            var meshes = 0;
            var stats = new Dictionary<string, long> { ["faces"] = 0, ["edges"] = 0, ["vertices"] = 0, ["boundaryEdges"] = 0, ["largestFaces"] = 0 };
            foreach (var data in doc.OfType("CDmePolygonMesh"))
            {
                meshes++;
                foreach (var problem in Check(data, stats))
                {
                    var rule = problem.Split(':')[0];
                    counts[rule] = counts.GetValueOrDefault(rule) + 1;
                    examples.TryAdd(rule, $"mesh {meshes - 1} \"{data.Name}\": {problem}");
                }
            }
            Console.WriteLine($"{Path.GetFileName(path)}: {meshes:n0} meshes, {stats["faces"]:n0} faces, {stats["edges"]:n0} half-edges, " +
                              $"{stats["vertices"]:n0} vertices, {stats["boundaryEdges"]:n0} boundary half-edges, largest mesh {stats["largestFaces"]:n0} faces");
            foreach (var (rule, n) in counts)
                Console.WriteLine($"  {n,8:n0} x {rule}   (e.g. {examples[rule]})");
            if (counts.Count == 0)
                Console.WriteLine("  every mesh keeps every rule");
            bad += counts.Count;
        }
        return bad == 0 ? 0 : 1;
    }

    private static int[] Ints(DmxBinary.Element data, string name)
        => (data.Get<object?[]>(name) ?? []).Select(x => x is int i ? i : int.MinValue).ToArray();

    private static IEnumerable<string> Check(DmxBinary.Element data, Dictionary<string, long> stats)
    {
        var to = Ints(data, "edgeVertexIndices");
        var opposite = Ints(data, "edgeOppositeIndices");
        var next = Ints(data, "edgeNextIndices");
        var face = Ints(data, "edgeFaceIndices");
        var faceFirst = Ints(data, "faceEdgeIndices");
        var vertexEdge = Ints(data, "vertexEdgeIndices");
        var vertexData = Ints(data, "vertexDataIndices");
        var edgeData = Ints(data, "edgeDataIndices");
        var edgeVertexData = Ints(data, "edgeVertexDataIndices");
        var faceData = Ints(data, "faceDataIndices");
        int E = to.Length, F = faceFirst.Length, V = vertexEdge.Length;
        stats["faces"] += F;
        stats["edges"] += E;
        stats["vertices"] += V;
        stats["largestFaces"] = Math.Max(stats["largestFaces"], F);

        if (opposite.Length != E || next.Length != E || face.Length != E)
        {
            yield return $"edge arrays differ in length: to {E}, opposite {opposite.Length}, next {next.Length}, face {face.Length}";
            yield break;
        }
        if (vertexData.Length != V)
            yield return $"vertexDataIndices length {vertexData.Length} != vertices {V}";
        if (edgeVertexData.Length != E && edgeVertexData.Length != 0)
            yield return $"edgeVertexDataIndices length {edgeVertexData.Length} != half-edges {E}";
        if (edgeData.Length != 0 && edgeData.Length != E / 2 && edgeData.Length != E)
            yield return $"edgeDataIndices length {edgeData.Length} vs half-edges {E}";
        if (faceData.Length != F)
            yield return $"faceDataIndices length {faceData.Length} != faces {F}";

        for (var e = 0; e < E; e++)
        {
            if (to[e] < 0 || to[e] >= V)
            {
                yield return $"edge vertex out of range: edge {e} to {to[e]} of {V}";
                yield break;
            }
            if (opposite[e] < 0 || opposite[e] >= E || opposite[opposite[e]] != e || opposite[e] == e)
            {
                yield return $"opposite not a pairing: edge {e} opposite {opposite[e]}";
                yield break;
            }
            if (next[e] < 0 || next[e] >= E)
            {
                yield return $"next out of range: edge {e} next {next[e]}";
                yield break;
            }
            if (face[e] < -1 || face[e] >= F)
                yield return $"edge face out of range: edge {e} face {face[e]} of {F}";
        }
        var from = new int[E];
        for (var e = 0; e < E; e++)
            from[e] = to[opposite[e]];
        stats["boundaryEdges"] += face.Count(f => f < 0);

        // next a permutation
        var seen = new int[E];
        foreach (var n in next)
            seen[n]++;
        var notPerm = Enumerable.Range(0, E).Where(e => seen[e] != 1).ToList();
        if (notPerm.Count > 0)
            yield return $"next not a permutation: {notPerm.Count} edge(s) reached {string.Join("/", notPerm.Take(4).Select(e => $"{e}x{seen[e]}"))}";
        for (var e = 0; e < E; e++)
        {
            if (to[e] != from[next[e]])
                yield return $"next does not continue: edge {e} ends at {to[e]}, next {next[e]} starts at {from[next[e]]}";
            if (face[next[e]] != face[e])
                yield return $"next leaves the face: edge {e} face {face[e]}, next {next[e]} face {face[next[e]]}";
            if (to[e] == from[e])
                yield return $"edge from a vertex to itself: edge {e} at {to[e]}";
        }
        // directed edges unique
        var directed = new HashSet<(int, int)>();
        for (var e = 0; e < E; e++)
            if (!directed.Add((from[e], to[e])))
                yield return $"directed edge twice: {from[e]}->{to[e]} (edge {e})";
        // face loops
        var inLoop = new int[E];
        for (var f = 0; f < F; f++)
        {
            var start = faceFirst[f];
            if (start < 0 || start >= E || face[start] != f)
            {
                yield return $"face's first edge not on it: face {f} first {start}";
                continue;
            }
            var (x, n, corners) = (start, 0, new HashSet<int>());
            var repeated = false;
            do
            {
                inLoop[x]++;
                repeated |= !corners.Add(to[x]);
                x = next[x];
                n++;
            } while (x != start && n <= E);
            if (x != start)
                yield return $"face loop does not close: face {f}";
            else if (n < 3)
                yield return $"face with fewer than 3 corners: face {f} has {n}";
            if (repeated)
                yield return $"face visits a vertex twice (Valve-saved maps have these too: atixref, c2m2 environment prefab): face {f}";
        }
        for (var e = 0; e < E; e++)
            if (face[e] >= 0 && inLoop[e] != 1)
                yield return $"faced edge in {inLoop[e]} face loops: edge {e} face {face[e]}";
        // boundary loops
        var visited = new bool[E];
        for (var e = 0; e < E; e++)
        {
            if (face[e] >= 0 || visited[e])
                continue;
            var (x, n) = (e, 0);
            do
            {
                visited[x] = true;
                x = next[x];
                n++;
            } while (x != e && n <= E && face[x] < 0 && !visited[x]);
            if (x != e)
                yield return $"boundary loop does not close: from edge {e}";
        }
        // vertex fans: turning about v from its edge reaches every edge leaving v
        var leaving = new int[V];
        for (var e = 0; e < E; e++)
            leaving[from[e]]++;
        var prev = new int[E];
        for (var e = 0; e < E; e++)
            prev[next[e]] = e;
        for (var v = 0; v < V; v++)
        {
            var start = vertexEdge[v];
            if (start < 0 || start >= E)
            {
                if (leaving[v] > 0)
                    yield return $"vertex edge out of range: vertex {v} edge {start}";
                else
                    yield return $"vertex with no edge: vertex {v}";
                continue;
            }
            if (from[start] != v)
            {
                yield return $"vertex edge does not leave it: vertex {v} edge {start} leaves {from[start]}";
                continue;
            }
            // e leaves v; prev[e] arrives at v; its opposite leaves v: the next edge about v.
            var (x, n) = (start, 0);
            do
            {
                x = opposite[prev[x]];
                n++;
            } while (x != start && n <= E);
            if (x == start && n != leaving[v])
                yield return $"vertex on more than one fan: vertex {v}: {n} of {leaving[v]} leaving edges about its edge";
        }
        foreach (var (name, idx, count) in new[] { ("vertexDataIndices", vertexData, StreamSize(data, "vertexData")),
                                                   ("edgeVertexDataIndices", edgeVertexData, StreamSize(data, "faceVertexData")),
                                                   ("faceDataIndices", faceData, StreamSize(data, "faceData")) })
            if (count >= 0 && idx.Any(i => i < 0 || i >= count))
                yield return $"{name} out of range: max {idx.DefaultIfEmpty(0).Max()} of {count}";
    }

    private static int StreamSize(DmxBinary.Element data, string holder)
        => data.Get<DmxBinary.Element>(holder) is { } h ? h.GetValue<int>("size") ?? -1 : -1;
}
