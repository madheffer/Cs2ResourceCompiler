using System.Numerics;
using Source2.Compiler.Dmx;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Cli;

/// <summary>
/// `s2c vmap-world-compare <source.vmap> <decompiled.vmap>`: the decompile's world meshes against the source's,
/// face by face in world space. Each decompiled world face goes to the source face its centroid lies on
/// (coplanar within 1/32, inside the polygon); then: faces equal (same corner positions within 1/64), source
/// faces split over several of ours, our faces over several source faces (by their corners' source faces),
/// and source meshes back as one of ours, split over several, or sharing one of ours with another source mesh.
/// </summary>
internal static class VmapWorldCompare
{
    public static int Run(string[] a)
    {
        var paths = a.Where(x => !x.StartsWith('-')).ToArray();
        if (paths.Length != 2)
            throw new ArgumentException("usage: vmap-world-compare <source.vmap> <decompiled.vmap>");
        var verbose = a.Contains("--verbose");
        if (a.Contains("--parents"))
            foreach (var g in MapMeshes.Read(DmxBinary.ReadFile(paths[0])).GroupBy(m => (m.ParentType, m.ParentClass, m.Hidden)))
                Console.WriteLine($"  parent {g.Key.ParentType} / {g.Key.ParentClass ?? "-"} hidden {g.Key.Hidden}: {g.Count()} meshes");
        var source = World(paths[0]);
        var ours = World(paths[1]);
        Console.WriteLine($"source: {source.Count} world meshes, {source.Sum(m => m.Faces.Length)} faces; ours: {ours.Count} meshes, {ours.Sum(m => m.Faces.Length)} faces");

        // Source faces by cell of their bounds (1/1 unit cells are too many; 64-unit cells).
        var sourceFaces = source.SelectMany((m, mi) => m.Faces.Select(f => (Mesh: mi, Face: f))).ToList();
        var grid = new Dictionary<(int, int, int), List<int>>();
        static (int, int, int) C(Vector3 p) => ((int)MathF.Floor(p.X / 64), (int)MathF.Floor(p.Y / 64), (int)MathF.Floor(p.Z / 64));
        for (var i = 0; i < sourceFaces.Count; i++)
        {
            var corners = sourceFaces[i].Face.Corners;
            var lo = C(corners.Aggregate(Vector3.Min));
            var hi = C(corners.Aggregate(Vector3.Max));
            for (var x = lo.Item1; x <= hi.Item1; x++)
                for (var y = lo.Item2; y <= hi.Item2; y++)
                    for (var z = lo.Item3; z <= hi.Item3; z++)
                        (grid.TryGetValue((x, y, z), out var l) ? l : grid[(x, y, z)] = []).Add(i);
        }
        int SourceFaceAt(Vector3 p, Vector3 normal)
        {
            foreach (var i in grid.GetValueOrDefault(C(p)) ?? [])
                if (OnFace(sourceFaces[i].Face.Corners, p, normal))
                    return i;
            return -1;
        }
        var key = new Func<Vector3[], string>(cs => string.Join(";", cs.Select(c => $"{MathF.Round(c.X * 64)},{MathF.Round(c.Y * 64)},{MathF.Round(c.Z * 64)}").Order()));
        var sourceKeys = sourceFaces.Select(f => key(f.Face.Corners)).ToHashSet();

        var ourFaceSource = new List<(int OurMesh, int SourceFace)>();
        var (equal, unmatched) = (0, 0);
        for (var mi = 0; mi < ours.Count; mi++)
            foreach (var f in ours[mi].Faces)
            {
                if (sourceKeys.Contains(key(f.Corners)))
                    equal++;
                var n = Newell(f.Corners);
                var centroid = f.Corners.Aggregate(Vector3.Add) / f.Corners.Length;
                var s = SourceFaceAt(centroid, n);
                if (s < 0)
                    unmatched++;
                ourFaceSource.Add((mi, s));
            }
        var matched = ourFaceSource.Where(x => x.SourceFace >= 0).ToList();
        if (verbose)
        {
            var unmatchedFaces = new List<MapMeshes.Face>();
            var idx = 0;
            for (var mi = 0; mi < ours.Count; mi++)
                foreach (var f in ours[mi].Faces)
                    if (ourFaceSource[idx++].SourceFace < 0)
                        unmatchedFaces.Add(f);
            Console.WriteLine("  our faces on no source face, by material: " + string.Join(" ", unmatchedFaces.GroupBy(f => Path.GetFileNameWithoutExtension(f.Material))
                .OrderByDescending(g => g.Count()).Take(15).Select(g => $"{g.Key}:{g.Count()}")));
            Console.WriteLine("  e.g. " + string.Join(" | ", unmatchedFaces.Where((_, i) => i % Math.Max(1, unmatchedFaces.Count / 6) == 0).Take(6)
                .Select(f => $"{Path.GetFileNameWithoutExtension(f.Material)} at {f.Corners.Aggregate(Vector3.Add) / f.Corners.Length}")));
        }
        var piecesPerSourceFace = matched.GroupBy(x => x.SourceFace).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"faces: {equal} of ours equal a source face; {unmatched} of ours on no source face; " +
                          $"{piecesPerSourceFace.Count} of {sourceFaces.Count} source faces reached, {piecesPerSourceFace.Count(kv => kv.Value == 1)} by one face of ours, " +
                          $"{piecesPerSourceFace.Count(kv => kv.Value > 1)} split (into {piecesPerSourceFace.Where(kv => kv.Value > 1).Sum(kv => kv.Value)} of ours)");
        var ourMeshesPerSource = matched.GroupBy(x => sourceFaces[x.SourceFace].Mesh).ToDictionary(g => g.Key, g => g.Select(x => x.OurMesh).Distinct().ToList());
        var sourcePerOurMesh = matched.GroupBy(x => x.OurMesh).ToDictionary(g => g.Key, g => g.Select(x => sourceFaces[x.SourceFace].Mesh).Distinct().ToList());
        var whole = ourMeshesPerSource.Count(kv => kv.Value.Count == 1 && sourcePerOurMesh[kv.Value[0]].Count == 1);
        Console.WriteLine($"meshes: {ourMeshesPerSource.Count} of {source.Count} source meshes reached; {whole} back as exactly one of ours; " +
                          $"{ourMeshesPerSource.Count(kv => kv.Value.Count > 1)} split over several of ours; " +
                          $"{sourcePerOurMesh.Count(kv => kv.Value.Count > 1)} of our meshes hold faces of several source meshes");
        if (verbose)
            foreach (var (s, list) in ourMeshesPerSource.Where(kv => kv.Value.Count > 1).Take(20))
                Console.WriteLine($"  source mesh {source[s].NodeId} ({source[s].Faces.Length} faces, {string.Join(",", source[s].Faces.Select(f => Path.GetFileNameWithoutExtension(f.Material)).Distinct())}) -> {list.Count} of ours");
        return 0;
    }

    /// <summary>
    /// `s2c world-charts <package.vpk> <source.vmap>`: whether the render's lightmap charts keep to one source mesh.
    /// Draw triangles join a chart where they share a corner at one position with one lightmap texcoord (TEXCOORD3);
    /// each triangle goes to the source world mesh holding its centroid. Reports charts spanning several source
    /// meshes and source meshes spanning several charts.
    /// </summary>
    public static int Charts(string[] a)
    {
        var paths = a.Where(x => !x.StartsWith('-')).ToArray();
        var files = Maps.Decompile.MapDecompiler.ReadPackage(paths[0]);
        var map = Maps.Decompile.MapDecompiler.MapNameOf(files);
        var drawn = Maps.Decompile.WorldGeometry.RenderTriangles(files, map).Where(t => t.Lightmap is not null).ToList();
        var source = World(paths[1]);
        var faces = source.SelectMany((m, mi) => m.Faces.Select(f => (Mesh: mi, Face: f))).ToList();
        var grid = new Dictionary<(int, int, int), List<int>>();
        static (int, int, int) C(Vector3 p) => ((int)MathF.Floor(p.X / 64), (int)MathF.Floor(p.Y / 64), (int)MathF.Floor(p.Z / 64));
        for (var i = 0; i < faces.Count; i++)
        {
            var (lo, hi) = (C(faces[i].Face.Corners.Aggregate(Vector3.Min)), C(faces[i].Face.Corners.Aggregate(Vector3.Max)));
            for (var x = lo.Item1; x <= hi.Item1; x++)
                for (var y = lo.Item2; y <= hi.Item2; y++)
                    for (var z = lo.Item3; z <= hi.Item3; z++)
                        (grid.TryGetValue((x, y, z), out var l) ? l : grid[(x, y, z)] = []).Add(i);
        }
        var meshOf = drawn.Select(t =>
        {
            var c = (t.A + t.B + t.C) / 3;
            var n = Vector3.Cross(t.B - t.A, t.C - t.A);
            foreach (var i in grid.GetValueOrDefault(C(c)) ?? [])
                if (OnFace(faces[i].Face.Corners, c, n))
                    return faces[i].Mesh;
            return -1;
        }).ToArray();
        var parent = Enumerable.Range(0, drawn.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        var first = new Dictionary<(Vector3, ushort, ushort, string), int>();
        for (var i = 0; i < drawn.Count; i++)
        {
            Vector3[] p = [drawn[i].A, drawn[i].B, drawn[i].C];
            for (var k = 0; k < 3; k++)
            {
                var key = (p[k], drawn[i].Lightmap![k].U, drawn[i].Lightmap![k].V, drawn[i].Model);
                if (first.TryGetValue(key, out var j))
                    parent[Find(i)] = Find(j);
                else
                    first[key] = i;
            }
        }
        var charts = Enumerable.Range(0, drawn.Count).Where(i => meshOf[i] >= 0).GroupBy(Find).ToList();
        var spanning = charts.Where(g => g.Select(i => meshOf[i]).Distinct().Count() > 1).ToList();
        var perMesh = charts.SelectMany(g => g.Select(i => meshOf[i]).Distinct().Select(m => (Mesh: m, Chart: g.Key))).GroupBy(x => x.Mesh).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"{drawn.Count} draw triangles with lightmap texcoords, {drawn.Count(_ => true) - meshOf.Count(m => m >= 0)} on no source world face; " +
                          $"{charts.Count} charts over source faces, {spanning.Count} span several source meshes ({spanning.Sum(g => g.Count())} triangles); " +
                          $"{perMesh.Count} source meshes, {perMesh.Count(kv => kv.Value == 1)} in one chart, {perMesh.Count(kv => kv.Value > 1)} over several (at most {perMesh.Values.DefaultIfEmpty(0).Max()})");
        foreach (var g in spanning.Take(8))
            Console.WriteLine($"  chart of {g.Count()} triangles over source meshes {string.Join(",", g.Select(i => source[meshOf[i]].NodeId).Distinct())}, materials {string.Join(",", g.Select(i => Path.GetFileNameWithoutExtension(drawn[i].Material)).Distinct())}");
        return 0;
    }

    internal static List<MapMeshes.Mesh> World(string path)
        => [.. MapMeshes.Read(DmxBinary.ReadFile(path)).Where(m => (m.ParentClass is null || m.ParentClass == "worldspawn" || m.ParentType == "CMapWorld") && !m.Hidden && m.Faces.Length > 0)];

    internal static Vector3 Newell(Vector3[] c)
    {
        var n = Vector3.Zero;
        for (var i = 0; i < c.Length; i++)
        {
            var (p, q) = (c[i], c[(i + 1) % c.Length]);
            n += new Vector3((p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y));
        }
        return n;
    }

    // p on the face: in its plane within 1/32, facing the same way, inside its polygon (projected on its main axis).
    internal static bool OnFace(Vector3[] corners, Vector3 p, Vector3 normal)
    {
        var n = Newell(corners);
        if (n.LengthSquared() == 0 || Vector3.Dot(Vector3.Normalize(n), Vector3.Normalize(normal)) < 0.999f)
            return false;
        var u = Vector3.Normalize(n);
        if (MathF.Abs(Vector3.Dot(u, p - corners[0])) > 1f / 32)
            return false;
        var axis = MathF.Abs(u.X) > MathF.Abs(u.Y) ? (MathF.Abs(u.X) > MathF.Abs(u.Z) ? 0 : 2) : (MathF.Abs(u.Y) > MathF.Abs(u.Z) ? 1 : 2);
        Vector2 P(Vector3 v) => axis == 0 ? new(v.Y, v.Z) : axis == 1 ? new(v.Z, v.X) : new(v.X, v.Y);
        var q = P(p);
        var inside = false;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var (a, b) = (P(corners[i]), P(corners[j]));
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < ((b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y)) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
