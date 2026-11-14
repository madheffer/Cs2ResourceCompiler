using System.Numerics;
using Source2.Compiler.Physics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// prop_statics back from the world's physics hulls. A prop_static leaves the entity lump and, baked to
/// the world, the render too; its hulls stay in world_physics.vmdl_c, each one its model's hull cooked
/// in the model's space and then moved vertex by vertex, in order, by the node's transform
/// (<see cref="StaticPropHulls.Shape"/>). So a shipped hull and a model hull with as many vertices pair
/// vertex for vertex: a least-squares fit of the one onto the other gives the placement nearly, and a
/// search around it (whole degrees and float steps) finds the origin and angles whose transform, as the
/// forward runs it, gives every vertex bit for bit. The models tried are the ones the .vmap_c refers to.
/// A prop is all of its model's hulls under one placement.
/// <para>Not covered yet: scaled props (their hulls are moved before the cook), a model's spheres,
/// capsules and meshes, deformed props, props with no physics (their placement is in the render only).</para>
/// </summary>
internal static class StaticProps
{
    /// <summary>A prop found: its model and placement, and the shipped hulls it gives, in its own order.</summary>
    internal sealed record Placed(string Model, Vector3 Origin, Vector3 Angles, int[] Hulls);

    /// <summary>A shipped hull: its vertices and planes.</summary>
    internal sealed record Shipped(Vector3[] Vertices, (Vector3 Normal, float Offset)[] Planes);

    /// <summary>The world part's hulls, in the shipped order, and how many mesh shapes the part has.</summary>
    internal static (List<Shipped> Hulls, int Meshes) WorldHulls(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        var hulls = new List<Shipped>();
        if (!files.TryGetValue($"maps/{mapName}/world_physics.vmdl_c", out var bytes))
            return (hulls, 0);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.GetBlockByType(BlockType.PHYS) is not PhysAggregateData phys)
            return (hulls, 0);
        var meshes = 0;
        foreach (var part in phys.Parts)
        {
            foreach (var hull in part.Shape.Hulls)
                hulls.Add(new Shipped(hull.Shape.GetVertexPositions().ToArray(), [.. hull.Shape.GetPlanes().ToArray().Select(q => (q.Normal, q.Offset))]));
            meshes += part.Shape.Meshes.Length;
        }
        return (hulls, meshes);
    }

    /// <summary>The models the map's .vmap_c refers to.</summary>
    internal static List<string> Models(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        if (!files.TryGetValue($"maps/{mapName}.vmap_c", out var bytes))
            return [];
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        return [.. (resource.ExternalReferences?.ResourceRefInfoList ?? []).Select(r => r.Name ?? "")
            .Where(n => n.StartsWith("models/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The world part's triangle-mesh shapes: each soup's vertices and triangles.</summary>
    internal static List<(Vector3[] Vertices, (int A, int B, int C)[] Triangles)> WorldSoups(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        var soups = new List<(Vector3[], (int, int, int)[])>();
        if (!files.TryGetValue($"maps/{mapName}/world_physics.vmdl_c", out var bytes))
            return soups;
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.GetBlockByType(BlockType.PHYS) is not PhysAggregateData phys)
            return soups;
        foreach (var part in phys.Parts)
            foreach (var m in part.Shape.Meshes)
                soups.Add(([.. m.Shape.GetVertices().ToArray()], [.. m.Shape.GetTriangles().ToArray().Select(t => (t.X, t.Y, t.Z))]));
        return soups;
    }

    /// <summary>A soup triangle by its corners' bits, the same whichever corner it starts from.</summary>
    internal static ((int, int, int), (int, int, int), (int, int, int)) TriangleKey(Vector3 a, Vector3 b, Vector3 c)
    {
        var k = new[] { Bits(a), Bits(b), Bits(c) };
        Array.Sort(k);
        return (k[0], k[1], k[2]);
    }

    /// <summary>
    /// prop_statics whose physics is triangle meshes, back from the world soups. The forward moves each mesh
    /// point by the prop matrix (<see cref="StaticPropHulls.Meshes"/>) and joins the triangles into the world's
    /// soups with the model's surfaces, while the prop's render is baked to the world apart from them: such
    /// triangles land on no render corner and carry surfaces no world material has (ze_minecraft_sakura: 13
    /// models, 281,550 triangles). Placements are found by voting: offsets from sample model points to every soup
    /// point, at each right-angle yaw; each peak is checked through the forward and its origin solved per axis
    /// over float steps until every point lands bit for bit. Returns the props and the soup triangles they give.
    /// </summary>
    internal static (List<Placed> Props, HashSet<((int, int, int), (int, int, int), (int, int, int))> Triangles) FindMeshProps(
        IReadOnlyList<(Vector3[] Vertices, (int A, int B, int C)[] Triangles)> soups, IReadOnlyList<string> models,
        Func<string, PhysAggregateData?> physics, List<string> notes)
    {
        var props = new List<Placed>();
        var taken = new HashSet<((int, int, int), (int, int, int), (int, int, int))>();
        var soupPoints = soups.SelectMany(s => s.Vertices).ToHashSet();
        var soupList = soupPoints.ToArray();
        var soupKeys = soups.SelectMany(s => s.Triangles.Select(t => TriangleKey(s.Vertices[t.A], s.Vertices[t.B], s.Vertices[t.C]))).ToHashSet();
        var near = soupList.Select(Cell).ToHashSet();
        var byCell = soupList.GroupBy(Cell).ToDictionary(g => g.Key, g => g.ToList());
        // A prop point as the soup holds it: itself, or the soup point the world's build welded it to (RnMeshBuilder
        // welds within 1/32, its default tolerance: seams where two chunk models meet), else null.
        Vector3? Snap(Vector3 p)
        {
            if (soupPoints.Contains(p))
                return p;
            var (cx, cy, cz) = Cell(p);
            Vector3? best = null;
            var bestDistance = 1f / 1024f;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        foreach (var q in byCell.GetValueOrDefault((cx + dx, cy + dy, cz + dz)) ?? [])
                            if (Vector3.DistanceSquared(p, q) is var d && d <= bestDistance)
                                (best, bestDistance) = (q, d);
            return best;
        }
        var (approximate, missing) = (0, 0);
        foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (physics(model) is not { } phys || !phys.Parts.Any(p => p.Shape.Meshes.Length > 0))
                continue;
            var local = StaticPropHulls.Meshes(new StaticPropHulls.Prop(-1, model, Vector3.Zero, Vector3.Zero, Vector3.One), phys)
                .SelectMany(n => n.Points).Distinct().ToArray();
            if (local.Length == 0)
                continue;
            const int samples = 8;
            var sample = Enumerable.Range(0, samples).Select(i => local[(int)((long)i * local.Length / samples)]).ToArray();
            var placedHere = 0;
            for (var quarter = 0; quarter < 4 && placedHere == 0; quarter++)
            {
                Vector3 Turn(Vector3 p) => quarter switch { 1 => new(-p.Y, p.X, p.Z), 2 => new(-p.X, -p.Y, p.Z), 3 => new(p.Y, -p.X, p.Z), _ => p };
                var votes = new Dictionary<(long, long, long), (int Count, Vector3 Offset)>();
                foreach (var p in sample.Select(Turn))
                {
                    var seen = new HashSet<(long, long, long)>();
                    foreach (var s in soupList)
                    {
                        var d = s - p;
                        var key = ((long)MathF.Round(d.X * 8), (long)MathF.Round(d.Y * 8), (long)MathF.Round(d.Z * 8));
                        if (!seen.Add(key))
                            continue;
                        votes[key] = votes.TryGetValue(key, out var v) ? (v.Count + 1, v.Offset) : (1, d);
                    }
                }
                if (DecompileProfile.On && votes.Count > 0)
                {
                    var best = votes.Values.MaxBy(v => v.Count);
                    var landing = local.Select(Turn).Count(p => near.Contains(Cell(p + best.Offset)));
                    notes.Add($"prop_static debug: {model} quarter {quarter}: best {best.Count}/{samples} votes at {best.Offset:R}, " +
                              $"{landing} of {local.Length} points within 1/16 of a soup point; peaks >= {samples - 2}: {votes.Values.Count(v => v.Count >= samples - 2)}");
                }
                foreach (var (_, (count, offset)) in votes.Where(kv => kv.Value.Count >= samples - 2).OrderByDescending(kv => kv.Value.Count))
                {
                    var angles = new Vector3(0f, quarter * 90f, 0f);
                    if (ExactOrigin(model, phys, angles, offset, soupPoints, near, Snap) is not { } origin)
                    {
                        approximate++;
                        if (DecompileProfile.On && count == samples)
                        {
                            var best = (Exact: -1, Miss: 0, Total: 0, Z: 0f);
                            foreach (var z in Steps(offset.Z, 64))
                            {
                                var (exact, miss, total) = (0, 0, 0);
                                foreach (var p in StaticPropHulls.Meshes(new StaticPropHulls.Prop(-1, model, new Vector3(MathF.Round(offset.X), MathF.Round(offset.Y), z), angles, Vector3.One), phys).SelectMany(n => n.Points))
                                {
                                    total++;
                                    if (soupPoints.Contains(p)) exact++;
                                    else if (near.Contains(Cell(p))) miss++;
                                }
                                if (exact > best.Exact)
                                    best = (exact, miss, total, z);
                            }
                            var sampleMiss = StaticPropHulls.Meshes(new StaticPropHulls.Prop(-1, model, new Vector3(MathF.Round(offset.X), MathF.Round(offset.Y), best.Z), angles, Vector3.One), phys)
                                .SelectMany(n => n.Points).Where(p => !soupPoints.Contains(p) && near.Contains(Cell(p))).Take(2)
                                .Select(p => $"{p:R} vs {soupList.MinBy(q => Vector3.DistanceSquared(q, p)):R}");
                            var distances = StaticPropHulls.Meshes(new StaticPropHulls.Prop(-1, model, new Vector3(MathF.Round(offset.X), MathF.Round(offset.Y), best.Z), angles, Vector3.One), phys)
                                .SelectMany(n => n.Points).Where(p => !soupPoints.Contains(p) && near.Contains(Cell(p)))
                                .Select(p => MathF.Sqrt(soupList.Min(q => Vector3.DistanceSquared(q, p)))).OrderBy(d => d).ToList();
                            notes.Add($"prop_static debug: {model} at <{MathF.Round(offset.X)}, {MathF.Round(offset.Y)}, {best.Z:R}>: {best.Exact} exact, {best.Miss} near misses of {best.Total}; " +
                                      $"miss distances median {distances.ElementAtOrDefault(distances.Count / 2):R} max {distances.LastOrDefault():R}, over 1/256: {distances.Count(d => d > 1f / 256)}: {string.Join(" ", distances.Where(d => d > 1f / 256).Take(6).Select(d => d.ToString("R")))}; e.g. {string.Join("; ", sampleMiss)}");
                        }
                        continue;
                    }
                    if (props.Any(x => x.Model == model && Bits(x.Origin) == Bits(origin) && Bits(x.Angles) == Bits(angles)))
                        continue;
                    var nodes = StaticPropHulls.Meshes(new StaticPropHulls.Prop(-1, model, origin, angles, Vector3.One), phys);
                    // The triangles the soup keeps of the prop's (the world's build drops a few: degenerate or welded).
                    foreach (var n in nodes)
                        for (var t = 0; t < n.Indices.Length / 3; t++)
                            if (Snap(n.Points[n.Indices[t * 3]]) is { } a && Snap(n.Points[n.Indices[(t * 3) + 1]]) is { } b && Snap(n.Points[n.Indices[(t * 3) + 2]]) is { } c
                                && TriangleKey(a, b, c) is var key && soupKeys.Contains(key))
                                taken.Add(key);
                    props.Add(new Placed(model, origin, angles, []));
                    placedHere++;
                }
            }
            if (placedHere == 0)
                missing++;
        }
        if (props.Count > 0 || missing > 0)
            notes.Add($"prop_static: {props.Count} with mesh physics placed from the world soups ({taken.Count} soup triangles); " +
                      $"{approximate} vote peaks not landing bit for bit, {missing} mesh model(s) placed nowhere (an entity's model, or no right-angle yaw): " +
                      string.Join("; ", props.Select(x => $"{x.Model} at {x.Origin:R} yaw {x.Angles.Y}")));
        return (props, taken);
    }

    // A point's 1/16 cell: "within reach" of a soup point.
    private static (long, long, long) Cell(Vector3 p) => ((long)MathF.Round(p.X * 16), (long)MathF.Round(p.Y * 16), (long)MathF.Round(p.Z * 16));

    // The origin under which the model's mesh points, turned by angles, land on soup points: every point within
    // reach of one (its 1/16 cell holds a soup point) is one bit for bit or welded to one within 1/32 (RnMeshBuilder's
    // weld: seams between chunk models), and at least 99% land bit for bit (the world's build drops a few). Typed values
    // first (whole, 1/1000, 1/32), then each axis over float steps about the estimate, the one most points' own
    // coordinate takes.
    private static Vector3? ExactOrigin(string model, PhysAggregateData phys, Vector3 angles, Vector3 estimate, HashSet<Vector3> soup,
                                        HashSet<(long, long, long)> near, Func<Vector3, Vector3?> snap)
    {
        Vector3[] Placed(Vector3 origin) => [.. StaticPropHulls.Meshes(new StaticPropHulls.Prop(-1, model, origin, angles, Vector3.One), phys).SelectMany(n => n.Points)];
        bool Lands(Vector3 origin)
        {
            var points = Placed(origin);
            var exact = 0;
            foreach (var p in points)
            {
                if (soup.Contains(p))
                    exact++;
                else if (near.Contains(Cell(p)) && snap(p) is null)
                    return false;
            }
            return exact >= points.Length * 0.99;
        }
        foreach (var round in new Func<float, float>[] { MathF.Round, f => MathF.Round(f * 1000f) / 1000f, f => MathF.Round(f * 32f) / 32f })
        {
            var o = new Vector3(round(estimate.X), round(estimate.Y), round(estimate.Z));
            if (Lands(o))
                return o;
        }
        var origin = new Vector3(MathF.Round(estimate.X * 1000f) / 1000f, MathF.Round(estimate.Y * 1000f) / 1000f, MathF.Round(estimate.Z * 1000f) / 1000f);
        // Each axis in turn, twice: the value whose placement lands the most points bit for bit (of the first 2048).
        for (var pass = 0; pass < 2; pass++)
            for (var axis = 0; axis < 3; axis++)
            {
                float Of(Vector3 v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
                var (best, bestCount) = (Of(origin), -1);
                foreach (var c in Steps(Of(estimate), 64).Concat(Steps(MathF.Round(Of(estimate) * 1000f) / 1000f, 64)).Concat(Steps(MathF.Round(Of(estimate)), 4)))
                {
                    var o = axis == 0 ? origin with { X = c } : axis == 1 ? origin with { Y = c } : origin with { Z = c };
                    var count = Placed(o).Take(2048).Count(soup.Contains);
                    if (count > bestCount)
                        (best, bestCount) = (c, count);
                }
                origin = axis == 0 ? origin with { X = best } : axis == 1 ? origin with { Y = best } : origin with { Z = best };
            }
        return Lands(origin) ? origin : null;
    }

    // One model hull: its body and place in the model's hull list, and its cooked vertices.
    private sealed record ModelHull(string Model, int Part, int Index, Vector3[] Points, Vector3[] Cooked);

    /// <summary>The props whose hulls the shipped ones are; the hulls nothing explains are noted.</summary>
    internal static List<Placed> Find(List<Shipped> shipped, IReadOnlyList<string> models, Func<string, PhysAggregateData?> physics, List<string> notes)
    {
        var modelHulls = new List<ModelHull>();
        var hullCount = new Dictionary<string, int>();
        foreach (var model in models)
        {
            if (physics(model) is not { } phys)
                continue;
            var nodes = StaticPropHulls.Nodes(new StaticPropHulls.Prop(-1, model, Vector3.Zero, Vector3.Zero, Vector3.One), phys);
            var index = 0;
            foreach (var node in nodes)
                if (StaticPropHulls.Cook(node) is { } cooked)
                    modelHulls.Add(new ModelHull(model, node.Part, index++, node.Points, cooked.VertexPositions));
            hullCount[model] = index;
        }
        // Each shipped hull: the model hull and placement it is, bit for bit.
        var found = new (ModelHull Hull, Vector3 Origin, Vector3 Angles)?[shipped.Count];
        var known = new List<(string Model, Vector3 Origin, Vector3 Angles)>();
        for (var i = 0; i < shipped.Count; i++)
        {
            var w = shipped[i];
            foreach (var mh in modelHulls.Where(h => h.Cooked.Length == w.Vertices.Length))
            {
                var phys = physics(mh.Model)!;
                // A placement already found for this model first (a prop's other hulls).
                foreach (var (_, o, a) in known.Where(k => k.Model == mh.Model))
                    if (Gives(mh, phys, o, a, w))
                    {
                        found[i] = (mh, o, a);
                        break;
                    }
                if (found[i] is null && Solve(mh, phys, w) is var (origin, angles))
                {
                    found[i] = (mh, origin, angles);
                    known.Add((mh.Model, origin, angles));
                }
                if (found[i] is not null)
                    break;
            }
        }
        // Props: all of a model's hulls under one placement, each once.
        var props = new List<Placed>();
        foreach (var g in Enumerable.Range(0, shipped.Count).Where(i => found[i] is not null)
                     .GroupBy(i => (found[i]!.Value.Hull.Model, Bits(found[i]!.Value.Origin), Bits(found[i]!.Value.Angles))))
        {
            var (hull, origin, angles) = found[g.First()]!.Value;
            var byIndex = g.ToDictionary(i => found[i]!.Value.Hull.Index, i => i);
            if (byIndex.Count != g.Count() || byIndex.Count != hullCount[hull.Model])
            {
                notes.Add($"prop_static: {hull.Model} at {origin:R} {angles:R}: {g.Count()} hulls of its {hullCount[hull.Model]} found");
                continue;
            }
            props.Add(new Placed(hull.Model, origin, angles, [.. Enumerable.Range(0, byIndex.Count).Select(k => byIndex[k])]));
        }
        var unexplained = Enumerable.Range(0, shipped.Count).Count(i => found[i] is null);
        if (unexplained > 0)
            notes.Add($"prop_static: {unexplained} of {shipped.Count} world hulls match no hull of the map's models ({models.Count}) under an unscaled placement");
        return props;
    }

    private static (int, int, int) Bits(Vector3 v) => (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));

    // The hull node the forward makes for the model hull of a prop at (origin, angles).
    private static StaticPropHulls.HullNode Node(ModelHull mh, PhysAggregateData phys, Vector3 origin, Vector3 angles)
    {
        var local = MapMeshes.Concat(StaticPropHulls.PropMatrix(new StaticPropHulls.Prop(-1, mh.Model, origin, angles, Vector3.One)),
                                     StaticPropHulls.BindPose(phys, mh.Part));
        return new StaticPropHulls.HullNode(mh.Part, mh.Points, new Vector3(local[3], local[7], local[11]), StaticPropHulls.MatrixAngles(local));
    }

    // Its transform's matrix, which moves the cooked vertices.
    private static float[] NodeMatrix(ModelHull mh, PhysAggregateData phys, Vector3 origin, Vector3 angles)
        => StaticPropHulls.NodeTransform(Node(mh, phys, origin, angles)).Matrix();

    // The vertices bit for bit, then the whole hull as the forward builds it: its planes too (angles a
    // float step apart can round every vertex alike and still turn the planes and the inertia).
    private static bool Gives(ModelHull mh, PhysAggregateData phys, Vector3 origin, Vector3 angles, Shipped want)
    {
        var m = NodeMatrix(mh, phys, origin, angles);
        for (var v = 0; v < want.Vertices.Length; v++)
            if (Bits(RnHullBuilder.Point(m, mh.Cooked[v])) != Bits(want.Vertices[v]))
                return false;
        if (StaticPropHulls.Shape(Node(mh, phys, origin, angles)) is not { } hull || hull.Planes.Length != want.Planes.Length)
            return false;
        for (var k = 0; k < hull.Planes.Length; k++)
            if (Bits(hull.Planes[k].Normal) != Bits(want.Planes[k].Normal)
                || BitConverter.SingleToInt32Bits(hull.Planes[k].Offset) != BitConverter.SingleToInt32Bits(want.Planes[k].Offset))
                return false;
        return true;
    }

    /// <summary>The placement that moves the model hull onto <paramref name="want"/> exactly, or null.</summary>
    private static (Vector3 Origin, Vector3 Angles)? Solve(ModelHull mh, PhysAggregateData phys, Shipped want)
    {
        if (Fit(mh.Cooked, want.Vertices) is not { } fit)
            return null;
        var estimate = StaticPropHulls.MatrixAngles(fit);
        var (px, py, pz) = (Steps(estimate.X), Steps(estimate.Y), Steps(estimate.Z));
        if ((long)px.Count * py.Count * pz.Count > 200_000)
            return null;
        var t = new Vector3(fit[3], fit[7], fit[11]);
        foreach (var ax in px)
            foreach (var ay in py)
                foreach (var az in pz)
                {
                    var angles = new Vector3(ax, ay, az);
                    // The rotation first, with the estimated origin: each axis then on its own (its
                    // coordinate takes only the origin's own component).
                    var m = NodeMatrix(mh, phys, t, angles);
                    if (!Close(m, mh.Cooked, want.Vertices))
                        continue;
                    if (Origin(mh, phys, angles, t, want) is { } origin)
                        return (origin, angles);
                }
        return null;
    }

    // The rotation holds when every vertex lands within a few units in the last place.
    private static bool Close(float[] m, Vector3[] cooked, Vector3[] want)
    {
        for (var v = 0; v < want.Length; v++)
        {
            var p = RnHullBuilder.Point(m, cooked[v]);
            if (!Near(p.X, want[v].X) || !Near(p.Y, want[v].Y) || !Near(p.Z, want[v].Z))
                return false;
        }
        return true;
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= MathF.Max(1e-3f, 64 * (MathF.BitIncrement(MathF.Abs(b)) - MathF.Abs(b)));

    // Each origin component from float steps around the estimate, checked on its own axis.
    private static Vector3? Origin(ModelHull mh, PhysAggregateData phys, Vector3 angles, Vector3 estimate, Shipped shipped)
    {
        var origin = estimate;
        for (var axis = 0; axis < 3; axis++)
        {
            var want = shipped.Vertices;
            var hit = false;
            // The component the first vertex asks for: x = (m1 y + m3) + (m0 x + m2 z), row by row.
            var r = NodeMatrix(mh, phys, origin, angles);
            var (row, p0) = (axis * 4, mh.Cooked[0]);
            var asked = (double)(axis == 0 ? want[0].X : axis == 1 ? want[0].Y : want[0].Z)
                        - ((double)r[row + 1] * p0.Y) - (((double)r[row] * p0.X) + ((double)r[row + 2] * p0.Z));
            var tried = Steps((float)asked, 16).Concat(Steps(axis == 0 ? estimate.X : axis == 1 ? estimate.Y : estimate.Z, 16));
            foreach (var c in tried)
            {
                var o = axis == 0 ? origin with { X = c } : axis == 1 ? origin with { Y = c } : origin with { Z = c };
                var m = NodeMatrix(mh, phys, o, angles);
                var ok = true;
                for (var v = 0; v < want.Length && ok; v++)
                {
                    var p = RnHullBuilder.Point(m, mh.Cooked[v]);
                    var (got, w) = axis == 0 ? (p.X, want[v].X) : axis == 1 ? (p.Y, want[v].Y) : (p.Z, want[v].Z);
                    ok = BitConverter.SingleToInt32Bits(got) == BitConverter.SingleToInt32Bits(w);
                }
                if (ok)
                {
                    (origin, hit) = (o, true);
                    break;
                }
            }
            if (!hit)
                return null;
        }
        return Gives(mh, phys, origin, angles, shipped) ? origin : null;
    }

    // Candidate floats near x: x, its whole number, its 1/1000 (each also a turn away for an angle past
    // 90), and up to n float steps either side of each; zero as 0 and -0.
    private static List<float> Steps(float x, int n = 4)
    {
        var bases = new HashSet<float> { x, MathF.Round(x), MathF.Round(x * 1000f) / 1000f };
        if (MathF.Abs(x) > 90f)
            foreach (var b in bases.ToList())
            {
                bases.Add(b + 360f);
                bases.Add(b - 360f);
            }
        var result = new List<float>();
        var seen = new HashSet<int>();
        void Add(float f)
        {
            if (seen.Add(BitConverter.SingleToInt32Bits(f)))
                result.Add(f);
        }
        foreach (var b in bases)
        {
            if (b == 0f)
            {
                Add(0f);
                Add(-0f);
                continue;
            }
            Add(b);
            var (up, down) = (b, b);
            for (var k = 0; k < n; k++)
            {
                Add(up = MathF.BitIncrement(up));
                Add(down = MathF.BitDecrement(down));
            }
        }
        return result;
    }

    // A least-squares 3x4 matrix taking the model hull's vertices to the shipped ones (row-major), when
    // its 3x3 part is a rotation (columns of length 1 within 1e-3) and every vertex lands within 1/100.
    private static float[]? Fit(Vector3[] from, Vector3[] to)
    {
        var n = from.Length;
        if (n < 4)
            return null;
        Vector3d cf = default, ct = default;
        for (var i = 0; i < n; i++)
        {
            cf += new Vector3d(from[i]);
            ct += new Vector3d(to[i]);
        }
        cf /= n;
        ct /= n;
        var ff = new double[3, 3];
        var tf = new double[3, 3];
        for (var i = 0; i < n; i++)
        {
            var f = new Vector3d(from[i]) - cf;
            var t = new Vector3d(to[i]) - ct;
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                {
                    ff[r, c] += f[r] * f[c];
                    tf[r, c] += t[r] * f[c];
                }
        }
        if (Inverse(ff) is not { } inv)
            return null;
        var a = new double[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                for (var k = 0; k < 3; k++)
                    a[r, c] += tf[r, k] * inv[k, c];
        for (var c = 0; c < 3; c++)
            if (Math.Abs(Math.Sqrt((a[0, c] * a[0, c]) + (a[1, c] * a[1, c]) + (a[2, c] * a[2, c])) - 1) > 1e-3)
                return null;
        var tr = new double[3];
        for (var r = 0; r < 3; r++)
            tr[r] = ct[r] - ((a[r, 0] * cf[0]) + (a[r, 1] * cf[1]) + (a[r, 2] * cf[2]));
        for (var i = 0; i < n; i++)
            for (var r = 0; r < 3; r++)
                if (Math.Abs((a[r, 0] * from[i].X) + (a[r, 1] * from[i].Y) + (a[r, 2] * from[i].Z) + tr[r] - to[i][r]) > 0.01)
                    return null;
        return [(float)a[0, 0], (float)a[0, 1], (float)a[0, 2], (float)tr[0],
                (float)a[1, 0], (float)a[1, 1], (float)a[1, 2], (float)tr[1],
                (float)a[2, 0], (float)a[2, 1], (float)a[2, 2], (float)tr[2]];
    }

    private static double[,]? Inverse(double[,] m)
    {
        var det = (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1]))) - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0])))
                  + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));
        if (Math.Abs(det) < 1e-12)
            return null;
        var r = new double[3, 3];
        r[0, 0] = ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1])) / det;
        r[0, 1] = ((m[0, 2] * m[2, 1]) - (m[0, 1] * m[2, 2])) / det;
        r[0, 2] = ((m[0, 1] * m[1, 2]) - (m[0, 2] * m[1, 1])) / det;
        r[1, 0] = ((m[1, 2] * m[2, 0]) - (m[1, 0] * m[2, 2])) / det;
        r[1, 1] = ((m[0, 0] * m[2, 2]) - (m[0, 2] * m[2, 0])) / det;
        r[1, 2] = ((m[0, 2] * m[1, 0]) - (m[0, 0] * m[1, 2])) / det;
        r[2, 0] = ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])) / det;
        r[2, 1] = ((m[0, 1] * m[2, 0]) - (m[0, 0] * m[2, 1])) / det;
        r[2, 2] = ((m[0, 0] * m[1, 1]) - (m[0, 1] * m[1, 0])) / det;
        return r;
    }

    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public Vector3d(Vector3 v) : this(v.X, v.Y, v.Z) { }
        public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3d operator /(Vector3d a, double d) => new(a.X / d, a.Y / d, a.Z / d);
    }
}
