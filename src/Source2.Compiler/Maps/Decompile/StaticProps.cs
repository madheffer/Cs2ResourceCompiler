using System.Numerics;
using Source2.Compiler.Physics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

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
    internal sealed record Placed(string Model, Vector3 Origin, Vector3 Angles, int[] Hulls)
    {
        /// <summary>The prop's scales (modelscale), one unless the placement says otherwise.</summary>
        public Vector3 Scales { get; init; } = Vector3.One;
    }

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

    // A 3x4 matrix as a KV3 array, flat (12 numbers: a fragment transform) or as rows (3 of 4: m_vTransform), row-major.
    private static float[] Matrix(IEnumerable<ValveKeyValue.KVObject>? kv)
    {
        var values = new List<float>();
        void Walk(ValveKeyValue.KVObject x)
        {
            var children = x.Values?.ToList();
            if (children is { Count: > 0 })
                foreach (var child in children)
                    Walk(child);
            else
                values.Add((float)x.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var x in kv ?? [])
            Walk(x);
        return [.. values];
    }

    private static float[] Matrix(ValveKeyValue.KVObject kv) => Matrix([kv]);

    /// <summary>The world nodes' aggregate draws that carry a fragment transform: (node model file, draw index).</summary>
    internal static HashSet<(string Model, int Draw)> TransformedDraws(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        var result = new HashSet<(string, int)>();
        foreach (var (_, model, draw, _) in Fragments(files, mapName))
            result.Add((model, draw));
        return result;
    }

    // Every aggregate fragment with a transform: its node, aggregate model file (with _c), draw index and 3x4 matrix.
    private static IEnumerable<(string Node, string Model, int Draw, float[] Matrix)> Fragments(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        foreach (var (path, bytes) in files.Where(f => f.Key.StartsWith($"maps/{mapName}/worldnodes/", StringComparison.Ordinal) && f.Key.EndsWith(".vwnod_c", StringComparison.Ordinal)))
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not WorldNode node)
                continue;
            foreach (var ao in node.Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var model = (ao.GetStringProperty("m_renderableModel") ?? "") + "_c";
                var transforms = ao.GetArray("m_fragmentTransforms") ?? [];
                var k = 0;
                foreach (var mo in ao.GetArray("m_aggregateMeshes") ?? [])
                    if (mo.GetBooleanProperty("m_bHasTransform") && k < transforms.Count)
                        if (Matrix(transforms[k++]) is { Length: 12 } transform)
                            yield return (path, model, (int)mo.GetIntegerProperty("m_nDrawCallIndex"), transform);
            }
        }
    }

    /// <summary>
    /// prop_statics the world nodes list with their transforms: each scene object whose model is not one of the
    /// map's own node models (its 3x4 m_vTransform), and each group of aggregate fragments under one transform,
    /// their model found by their draws' vertices (stored in the model's space) among the .vmap_c's models'
    /// LOD 0 render meshes of the same material. The transform is the forward's prop matrix (PropMatrix: the
    /// angle matrix, origin as translation, each column times its scale): origin and scales read from it, the
    /// angles searched in float steps about MatrixAngles until the matrix comes back bit for bit (else the
    /// estimate, counted). Returns the props found and the models no fragment group matched.
    /// </summary>
    internal static (List<Placed> Props, int Inexact, int Unmatched) SceneProps(IReadOnlyDictionary<string, byte[]> files, string mapName,
        Func<string, IReadOnlyList<List<(Vector3 A, Vector3 B, Vector3 C, string Material)>>> renderMeshes, List<string> notes)
    {
        using var profiled = DecompileProfile.Time("SceneProps");
        var props = new List<Placed>();
        var (inexact, unmatched) = (0, 0);
        void Add(string model, float[] m)
        {
            var origin = new Vector3(m[3], m[7], m[11]);
            var scales = new Vector3(new Vector3(m[0], m[4], m[8]).Length(), new Vector3(m[1], m[5], m[9]).Length(), new Vector3(m[2], m[6], m[10]).Length());
            Vector3 Clean(Vector3 v) => new(MathF.Abs(v.X - MathF.Round(v.X * 1000) / 1000) < 1e-5f ? MathF.Round(v.X * 1000) / 1000 : v.X,
                                            MathF.Abs(v.Y - MathF.Round(v.Y * 1000) / 1000) < 1e-5f ? MathF.Round(v.Y * 1000) / 1000 : v.Y,
                                            MathF.Abs(v.Z - MathF.Round(v.Z * 1000) / 1000) < 1e-5f ? MathF.Round(v.Z * 1000) / 1000 : v.Z);
            scales = Clean(scales);
            var unscaled = (float[])m.Clone();
            for (var r = 0; r < 3; r++)
                (unscaled[r * 4], unscaled[(r * 4) + 1], unscaled[(r * 4) + 2]) = (m[r * 4] / scales.X, m[(r * 4) + 1] / scales.Y, m[(r * 4) + 2] / scales.Z);
            var estimate = StaticPropHulls.MatrixAngles(unscaled);
            bool Same(Vector3 angles)
            {
                var q = StaticPropHulls.PropMatrix(new StaticPropHulls.Prop(-1, model, origin, angles, scales));
                for (var i = 0; i < 12; i++)
                    if (BitConverter.SingleToInt32Bits(q[i]) != BitConverter.SingleToInt32Bits(m[i]))
                        return false;
                return true;
            }
            Vector3? found = Same(estimate) ? estimate : null;
            if (found is null)
            {
                var (px, py, pz) = (Steps(estimate.X), Steps(estimate.Y), Steps(estimate.Z));
                foreach (var ax in px)
                {
                    foreach (var ay in py)
                    {
                        foreach (var az in pz)
                            if (Same(new Vector3(ax, ay, az)))
                            {
                                found = new Vector3(ax, ay, az);
                                break;
                            }
                        if (found is not null) break;
                    }
                    if (found is not null) break;
                }
            }
            if (found is null)
                inexact++;
            props.Add(new Placed(model, origin, found ?? estimate, []) { Scales = scales });
        }
        // Scene objects.
        foreach (var (path, bytes) in files.Where(f => f.Key.StartsWith($"maps/{mapName}/worldnodes/", StringComparison.Ordinal) && f.Key.EndsWith(".vwnod_c", StringComparison.Ordinal)))
        {
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            if (resource.DataBlock is not WorldNode node)
                continue;
            foreach (var so in node.Data.GetArray("m_sceneObjects") ?? [])
            {
                var model = so.GetStringProperty("m_renderableModel") ?? "";
                if (model.Length == 0 || model.StartsWith($"maps/{mapName}/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Matrix(so.GetArray("m_vTransform")) is { Length: 12 } transform)
                    Add(model, transform);
            }
        }
        // Aggregate fragments: the draws by (model file, draw), grouped by transform.
        var fragmentList = Fragments(files, mapName).ToList();
        if (fragmentList.Count > 0)
        {
            var draws = WorldGeometry.RenderTriangles(files, mapName, fragments: true).GroupBy(t => (t.Model, t.Draw)).ToDictionary(g => g.Key, g => g.ToList());
            // Each candidate model's LOD 0 render vertices by material.
            var byVertex = new Dictionary<(string Material, Vector3 Position), HashSet<string>>();
            foreach (var model in Models(files, mapName).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var mesh in renderMeshes(model))
                    foreach (var (a, b, c, material) in mesh)
                        foreach (var p in (Vector3[])[a, b, c])
                            (byVertex.TryGetValue((material, p), out var set) ? set : byVertex[(material, p)] = new(StringComparer.OrdinalIgnoreCase)).Add(model);
            foreach (var group in fragmentList.GroupBy(f => string.Join(",", f.Matrix.Select(BitConverter.SingleToInt32Bits))))
            {
                var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var total = 0;
                foreach (var f in group)
                    foreach (var t in draws.GetValueOrDefault((f.Model, f.Draw)) ?? [])
                        foreach (var p in (Vector3[])[t.A, t.B, t.C])
                        {
                            total++;
                            foreach (var model in byVertex.GetValueOrDefault((t.Material, p)) ?? [])
                                votes[model] = votes.GetValueOrDefault(model) + 1;
                        }
                if (votes.Count == 0 || votes.Max(v => v.Value) < total * 0.99)
                {
                    unmatched++;
                    continue;
                }
                Add(votes.MaxBy(v => v.Value).Key, group.First().Matrix);
            }
        }
        if (props.Count > 0 || unmatched > 0)
            notes.Add($"prop_static: {props.Count} from the world nodes' transforms (scene objects and aggregate fragments), " +
                      $"{props.Count - inexact} with angles that give the transform bit for bit, {inexact} the nearest; " +
                      $"{unmatched} fragment group(s) whose draws match no model of the .vmap_c");
        return (props, inexact, unmatched);
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
        using var profiled = DecompileProfile.Time("FindMeshProps");
        var props = new List<Placed>();
        var taken = new HashSet<((int, int, int), (int, int, int), (int, int, int))>();
        var soupPoints = soups.SelectMany(s => s.Vertices).ToHashSet();
        var soupList = soupPoints.ToArray();
        var soupKeys = soups.SelectMany(s => s.Triangles.Select(t => TriangleKey(s.Vertices[t.A], s.Vertices[t.B], s.Vertices[t.C]))).ToHashSet();
        var near = soupList.Select(Cell).ToHashSet();
        var reach = Dilated(near);
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
                var spread = Enumerable.Range(0, Math.Min(256, local.Length)).Select(i => Turn(local[(int)((long)i * local.Length / Math.Min(256, local.Length))])).ToArray();
                foreach (var offset in Offsets([.. sample.Select(Turn)], soupList, p => reach.Contains(Cell(p)), samples - 2))
                {
                    var angles = new Vector3(0f, quarter * 90f, 0f);
                    // At least 99% of 256 spread points within reach before the exact solve.
                    if (spread.Count(p => reach.Contains(Cell(p + offset))) < spread.Length * 0.99)
                        continue;
                    if (props.Any(x => x.Model == model && Vector3.Distance(x.Origin, offset) < 1f / 8))
                        continue;
                    if (ExactOrigin(model, phys, angles, offset, soupPoints, near, Snap) is not { } origin)
                    {
                        approximate++;
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

    /// <summary>
    /// prop_statics with no collision in the world (no physics, or none the soups hold), back from the world
    /// render: the prop's render is baked into the world nodes' draws, its model's LOD 0 render meshes
    /// (<see cref="GameContent.RenderMeshes"/>) moved by the prop matrix, then welded at 1/32 into the
    /// aggregates. Placements are voted as <see cref="FindMeshProps"/> does, over the draw corners given, and
    /// kept where at least 99% of the model's render corners land within 1/32 of one. The draws keep only the
    /// weld's representatives, so the origin is the mean offset of the corners that land, not a bit-exact
    /// solve (noted). Returns the props and, per draw triangle given, whether a prop's render covers it.
    /// </summary>
    internal static (List<Placed> Props, bool[] Covered) FindRenderProps(IReadOnlyList<WorldGeometry.RenderTriangle> drawn, IEnumerable<string> models,
                                                                     Func<string, IReadOnlyList<List<(Vector3 A, Vector3 B, Vector3 C, string Material)>>> renderMeshes,
                                                                     List<string> notes)
    {
        using var profiled = DecompileProfile.Time("FindRenderProps");
        var props = new List<Placed>();
        var covered = new bool[drawn.Count];
        static (int, int, int) Cell32(Vector3 p) => ((int)MathF.Floor(p.X * 32), (int)MathF.Floor(p.Y * 32), (int)MathF.Floor(p.Z * 32));
        var points = drawn.SelectMany(t => (Vector3[])[t.A, t.B, t.C]).Distinct().ToArray();
        var byCell = points.GroupBy(Cell32).ToDictionary(g => g.Key, g => g.ToList());
        var reach32 = Dilated(byCell.Keys);
        Vector3? Nearest(Vector3 p)
        {
            var (x, y, z) = Cell32(p);
            Vector3? best = null;
            var bestDistance = 1f / 1024f;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        foreach (var q in byCell.GetValueOrDefault((x + dx, y + dy, z + dz)) ?? [])
                            if (Vector3.DistanceSquared(p, q) is var d && d <= bestDistance)
                                (best, bestDistance) = (q, d);
            return best;
        }
        var placedCorners = new HashSet<(int, int, int)>();
        var skipped = new List<string>();
        // Draw triangles by their sorted edge lengths (half units), for the any-rotation pass.
        static (int, int, int) EdgeKey(Vector3 a, Vector3 b, Vector3 c)
        {
            float[] l = [(a - b).Length(), (b - c).Length(), (c - a).Length()];
            Array.Sort(l);
            return ((int)MathF.Round(l[0] * 2), (int)MathF.Round(l[1] * 2), (int)MathF.Round(l[2] * 2));
        }
        var drawByKey = new Dictionary<(int, int, int), List<int>>();
        for (var i = 0; i < drawn.Count; i++)
            (drawByKey.TryGetValue(EdgeKey(drawn[i].A, drawn[i].B, drawn[i].C), out var list) ? list : drawByKey[EdgeKey(drawn[i].A, drawn[i].B, drawn[i].C)] = []).Add(i);
        IEnumerable<int> Matching((int, int, int) k)
        {
            for (var d0 = -1; d0 <= 1; d0++)
                for (var d1 = -1; d1 <= 1; d1++)
                    for (var d2 = -1; d2 <= 1; d2++)
                        foreach (var i in drawByKey.GetValueOrDefault((k.Item1 + d0, k.Item2 + d1, k.Item3 + d2)) ?? [])
                            yield return i;
        }
        static (Vector3 E1, Vector3 E2, Vector3 N) Frame(Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = Vector3.Normalize(b - a);
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            return (e1, Vector3.Cross(n, e1), n);
        }
        foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var local = renderMeshes(model).SelectMany(m => m.SelectMany(t => (Vector3[])[t.A, t.B, t.C])).Distinct().ToArray();
            if (local.Length < 4 || points.Length == 0)
                continue;
            const int samples = 8;
            var sample = Enumerable.Range(0, samples).Select(i => local[(int)((long)i * local.Length / samples)]).ToArray();
            var placedHere = 0;
            for (var quarter = 0; quarter < 4; quarter++)
            {
                Vector3 Turn(Vector3 p) => quarter switch { 1 => new(-p.Y, p.X, p.Z), 2 => new(-p.X, -p.Y, p.Z), 3 => new(p.Y, -p.X, p.Z), _ => p };
                var spread = Enumerable.Range(0, Math.Min(256, local.Length)).Select(i => Turn(local[(int)((long)i * local.Length / Math.Min(256, local.Length))])).ToArray();
                foreach (var offset in Offsets([.. sample.Select(Turn)], points, p => reach32.Contains(Cell32(p)), samples - 1))
                {
                    var angles = new Vector3(0f, quarter * 90f, 0f);
                    // At least 99% of 256 spread corners within reach, and no prop of this model there yet, before the full check.
                    if (spread.Count(p => reach32.Contains(Cell32(p + offset))) < spread.Length * 0.99
                        || props.Any(x => x.Model == model && x.Angles == angles && Vector3.Distance(x.Origin, offset) < 1f / 8))
                        continue;
                    var matrix = StaticPropHulls.PropMatrix(new StaticPropHulls.Prop(-1, model, offset, angles, Vector3.One));
                    var moved = local.Select(p => MapMeshes.Transform(matrix, p)).ToArray();
                    var landing = moved.Select(Nearest).ToArray();
                    if (landing.Count(l => l is not null) < moved.Length * 0.99)
                        continue;
                    // The origin: the estimate moved by the mean miss of the corners that land.
                    var shift = Vector3.Zero;
                    var n = 0;
                    for (var i = 0; i < moved.Length; i++)
                        if (landing[i] is { } l)
                            (shift, n) = (shift + (l - moved[i]), n + 1);
                    var origin = offset + (shift / n);
                    if (props.Any(x => x.Model == model && Vector3.Distance(x.Origin, origin) < 1f / 16 && x.Angles == angles))
                        continue;
                    var placed = StaticPropHulls.PropMatrix(new StaticPropHulls.Prop(-1, model, origin, angles, Vector3.One));
                    foreach (var p in local)
                        placedCorners.Add(Cell32(MapMeshes.Transform(placed, p)));
                    props.Add(new Placed(model, origin, angles, []));
                    placedHere++;
                }
            }
            // Any rotation: the model's render triangles of the rarest shapes among the draws (edges of 2 units and
            // more), each matched by its edge lengths to draw triangles; a match's frames give the rotation, checked
            // on 256 spread corners, then fitted by least squares on the corners that land (within 1/8: the weld's
            // 1/32 on both sides) and checked again through the prop matrix of the fitted angles and origin.
            var meshTriangles = renderMeshes(model).SelectMany(m => m).Where(t =>
                MathF.Min((t.A - t.B).Length(), MathF.Min((t.B - t.C).Length(), (t.C - t.A).Length())) >= 2f).ToList();
            var references = meshTriangles.Select(t => (T: t, Count: Matching(EdgeKey(t.A, t.B, t.C)).Count()))
                .Where(x => x.Count > 0).OrderBy(x => x.Count).Take(2).ToList();
            var spreadAny = Enumerable.Range(0, Math.Min(256, local.Length)).Select(i => local[(int)((long)i * local.Length / Math.Min(256, local.Length))]).ToArray();
            var subset = Enumerable.Range(0, Math.Min(2048, local.Length)).Select(i => local[(int)((long)i * local.Length / Math.Min(2048, local.Length))]).ToArray();
            foreach (var (r, _) in references)
                foreach (var di in Matching(EdgeKey(r.A, r.B, r.C)))
                {
                    var d = drawn[di];
                    foreach (var (d0, d1, d2) in ((Vector3, Vector3, Vector3)[])[(d.A, d.B, d.C), (d.B, d.C, d.A), (d.C, d.A, d.B)])
                    {
                        if (MathF.Abs((d1 - d0).Length() - (r.B - r.A).Length()) > 0.125f || MathF.Abs((d2 - d1).Length() - (r.C - r.B).Length()) > 0.125f
                            || MathF.Abs((d0 - d2).Length() - (r.A - r.C).Length()) > 0.125f)
                            continue;
                        var (m1, m2, mn) = Frame(r.A, r.B, r.C);
                        var (w1, w2, wn) = Frame(d0, d1, d2);
                        Vector3 Move(Vector3 p)
                        {
                            var v = p - r.A;
                            return d0 + (w1 * Vector3.Dot(m1, v)) + (w2 * Vector3.Dot(m2, v)) + (wn * Vector3.Dot(mn, v));
                        }
                        if (spreadAny.Count(p => reach32.Contains(Cell32(Move(p)))) < spreadAny.Length * 0.99)
                            continue;
                        var at = Move(Vector3.Zero);
                        if (props.Any(x => x.Model == model && Vector3.Distance(x.Origin, at) < 0.5f))
                            continue;
                        var pairs = subset.Select(p => (From: p, To: Nearest(Move(p)))).Where(x => x.To is not null).ToList();
                        if (pairs.Count < subset.Length * 0.99)
                            continue;
                        if (Fit([.. pairs.Select(x => x.From)], [.. pairs.Select(x => x.To!.Value)], 0.125, 5e-3) is not { } fit)
                            continue;
                        var angles = StaticPropHulls.MatrixAngles(fit);
                        var origin = new Vector3(fit[3], fit[7], fit[11]);
                        var placed = StaticPropHulls.PropMatrix(new StaticPropHulls.Prop(-1, model, origin, angles, Vector3.One));
                        if (local.Count(p => Nearest(MapMeshes.Transform(placed, p)) is not null) < local.Length * 0.99)
                            continue;
                        foreach (var p in local)
                            placedCorners.Add(Cell32(MapMeshes.Transform(placed, p)));
                        props.Add(new Placed(model, origin, angles, []));
                        placedHere++;
                        break;
                    }
                }
            if (placedHere == 0)
                skipped.Add(Path.GetFileNameWithoutExtension(model));
        }
        bool OnProp(Vector3 p)
        {
            var (x, y, z) = Cell32(p);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (placedCorners.Contains((x + dx, y + dy, z + dz)))
                            return true;
            return false;
        }
        for (var i = 0; i < drawn.Count; i++)
            covered[i] = OnProp(drawn[i].A) && OnProp(drawn[i].B) && OnProp(drawn[i].C);
        if (props.Count > 0 || skipped.Count > 0)
            notes.Add($"prop_static: {props.Count} with no world collision placed from the render (origin within the render's 1/32 weld, " +
                      $"not bit for bit), {covered.Count(c => c)} draw triangles theirs; not found: {string.Join(" ", skipped)}; " +
                      string.Join("; ", props.Take(40).Select(x => $"{x.Model} at {x.Origin} angles {x.Angles}")));
        return (props, covered);
    }

    // A cell set grown by one cell each way: membership is "within one cell" in a single lookup.
    private static HashSet<(int, int, int)> Dilated(IEnumerable<(int, int, int)> cells)
    {
        var grown = new HashSet<(int, int, int)>();
        foreach (var (x, y, z) in cells)
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        grown.Add((x + dx, y + dy, z + dz));
        return grown;
    }

    private static HashSet<(long, long, long)> Dilated(HashSet<(long, long, long)> cells)
    {
        var grown = new HashSet<(long, long, long)>();
        foreach (var (x, y, z) in cells)
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        grown.Add((x + dx, y + dy, z + dz));
        return grown;
    }

    // The offsets that move the lead sample (turned) onto a target point and put at least `need` of all the
    // samples within reach, one per 1/8 cell, in target order; the next sample leads when the first gives none
    // (a lead point the target set lacks).
    private static List<Vector3> Offsets(Vector3[] turned, Vector3[] targets, Func<Vector3, bool> reach, int need)
    {
        for (var lead = 0; lead < Math.Min(2, turned.Length); lead++)
        {
            var found = new Dictionary<(long, long, long), Vector3>();
            foreach (var q in targets)
            {
                var d = q - turned[lead];
                var misses = 0;
                for (var i = 0; i < turned.Length && misses <= turned.Length - need; i++)
                    if (i != lead && !reach(turned[i] + d))
                        misses++;
                if (misses <= turned.Length - need)
                    found.TryAdd(((long)MathF.Round(d.X * 8), (long)MathF.Round(d.Y * 8), (long)MathF.Round(d.Z * 8)), d);
            }
            if (found.Count > 0)
                return [.. found.Values];
        }
        return [];
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
        // The first 2048 mesh points as StaticPropHulls.Meshes moves them (prop matrix times each body's bind
        // pose), without building the nodes: what a candidate is scored on.
        var subset = new List<(int Part, Vector3 Point)>();
        for (var part = 0; part < phys.Parts.Length && subset.Count < 2048; part++)
            foreach (var desc in phys.Parts[part].Shape.Meshes)
                foreach (var v in desc.Shape.GetVertices().ToArray())
                    if (subset.Count < 2048)
                        subset.Add((part, v));
        int Score(Vector3 origin)
        {
            var m = StaticPropHulls.PropMatrix(new StaticPropHulls.Prop(-1, model, origin, angles, Vector3.One));
            var locals = new float[phys.Parts.Length][];
            var count = 0;
            foreach (var (part, v) in subset)
                if (soup.Contains(MapMeshes.Transform(locals[part] ??= MapMeshes.Concat(m, StaticPropHulls.BindPose(phys, part)), v)))
                    count++;
            return count;
        }
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
                    var count = Score(o);
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
    private static float[]? Fit(Vector3[] from, Vector3[] to, double tolerance = 0.01, double rotationTolerance = 1e-3)
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
            if (Math.Abs(Math.Sqrt((a[0, c] * a[0, c]) + (a[1, c] * a[1, c]) + (a[2, c] * a[2, c])) - 1) > rotationTolerance)
                return null;
        var tr = new double[3];
        for (var r = 0; r < 3; r++)
            tr[r] = ct[r] - ((a[r, 0] * cf[0]) + (a[r, 1] * cf[1]) + (a[r, 2] * cf[2]));
        for (var i = 0; i < n; i++)
            for (var r = 0; r < 3; r++)
                if (Math.Abs((a[r, 0] * from[i].X) + (a[r, 1] * from[i].Y) + (a[r, 2] * from[i].Z) + tr[r] - to[i][r]) > tolerance)
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
