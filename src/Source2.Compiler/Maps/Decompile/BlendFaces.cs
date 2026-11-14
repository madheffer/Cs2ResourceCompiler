using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A world face's vertex paint (the mesh's VertexPaintBlendParams corner stream) back from the render. A world
/// node draw whose material reads the paint carries it per vertex as an RGBA8 field the layout names
/// <c>VertexPaintBlendParams</c> (ze_hold_em_p's slabs, concrete and hr_conc aggregates: TEXCOORD4). The paint
/// decides which layer surface a blend material's physics triangle takes (WorldCollision.SplitLayers), so a
/// decompiled painted mesh needs it for its soup's per-triangle surfaces, its name and the surface order.
/// What the render keeps is the paint after the compile's tessellation (a subdivided face's patch values) at 8
/// bits, at the render's 1/32 weld positions: each corner takes the nearest such vertex within 1/32 on a draw
/// triangle of its material facing its way.
/// </summary>
internal static class BlendFaces
{
    private readonly record struct PaintCorner(Vector3 Position, Vector3 Normal, Vector4 Paint);

    /// <summary>The render's painted corners, per material, by 1/32 cell.</summary>
    internal sealed class RenderPaint
    {
        private readonly Dictionary<string, Dictionary<(int, int, int), List<PaintCorner>>> byMaterial = new(StringComparer.Ordinal);

        public bool Has(string material) => byMaterial.ContainsKey(material);

        public int Corners => byMaterial.Values.Sum(m => m.Values.Sum(l => l.Count));

        internal void Add(string material, Vector3 position, Vector3 normal, Vector4 paint)
        {
            if (!byMaterial.TryGetValue(material, out var cells))
                byMaterial[material] = cells = [];
            var key = Cell(position);
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = [];
            list.Add(new PaintCorner(position, normal, paint));
        }

        /// <summary>The paint of the nearest corner of <paramref name="material"/> within 1/32 of <paramref name="at"/>
        /// on a triangle facing as <paramref name="normal"/> (unit normals within 1e-4), or null.</summary>
        public Vector4? At(string material, Vector3 at, Vector3 normal)
        {
            if (normal.LengthSquared() == 0f || !byMaterial.TryGetValue(material, out var cells))
                return null;
            var n = Vector3.Normalize(normal);
            var (cx, cy, cz) = Cell(at);
            PaintCorner? best = null;
            var bestDistance = float.MaxValue;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        foreach (var c in cells.GetValueOrDefault((cx + dx, cy + dy, cz + dz)) ?? [])
                            if (Vector3.DistanceSquared(c.Position, at) is var d && d <= Weld * Weld && d < bestDistance
                                && c.Normal.LengthSquared() > 0f && Vector3.Dot(Vector3.Normalize(c.Normal), n) > 0.9999f)
                                (best, bestDistance) = (c, d);
            return best?.Paint;
        }

        /// <summary>Each corner's paint for a face (corners in order), or null when the render has no paint for its
        /// material or misses a corner.</summary>
        public Vector4[]? Face(string material, Vector3[] corners)
        {
            if (!Has(material) || corners.Length < 3)
                return null;
            var normal = Newell(corners);
            var paint = new Vector4[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                if (At(material, corners[i], normal) is not { } p)
                    return null;
                paint[i] = p;
            }
            return paint;
        }
    }

    /// <summary>The painted corners of every world node draw that carries a VertexPaintBlendParams field.</summary>
    internal static RenderPaint Read(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        var result = new RenderPaint();
        foreach (var (_, bytes) in files.Where(f => f.Key.StartsWith($"maps/{mapName}/worldnodes/", StringComparison.Ordinal) && f.Key.EndsWith(".vmdl_c", StringComparison.Ordinal)))
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
                        // A draw may split its streams over two buffers (one vertex numbering): each field from the
                        // buffer that has it.
                        var buffers = call.GetArray("m_vertexBuffers").Select(b => (Buffer: mesh.VBIB.VertexBuffers[b.GetInt32Property("m_hBuffer")],
                                                                                        Offset: b.ContainsKey("m_nBindOffsetBytes") ? b.GetInt32Property("m_nBindOffsetBytes") : 0)).ToList();
                        var withPosition = buffers.FirstOrDefault(b => b.Buffer.InputLayoutFields?.Any(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase)) == true);
                        var withPaint = buffers.FirstOrDefault(b => b.Buffer.InputLayoutFields?.Any(IsPaint) == true);
                        if (withPosition.Buffer.InputLayoutFields is null || withPaint.Buffer.InputLayoutFields is null)
                            continue;
                        var (pb, cb) = (withPosition.Buffer, withPaint.Buffer);
                        var position = pb.InputLayoutFields.First(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase));
                        if (position.Format.ToString() != "R32G32B32_FLOAT")
                            continue;
                        var paintField = cb.InputLayoutFields.First(IsPaint);
                        Vector3 P(int v)
                        {
                            var at = (int)withPosition.Offset + v * (int)pb.ElementSizeInBytes + (int)position.Offset;
                            return new(BitConverter.ToSingle(pb.Data, at), BitConverter.ToSingle(pb.Data, at + 4), BitConverter.ToSingle(pb.Data, at + 8));
                        }
                        Vector4 C(int v)
                        {
                            var at = (int)withPaint.Offset + v * (int)cb.ElementSizeInBytes + (int)paintField.Offset;
                            return new Vector4(Unpack(cb.Data[at]), Unpack(cb.Data[at + 1]), Unpack(cb.Data[at + 2]), Unpack(cb.Data[at + 3]));
                        }
                        var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var (start, count, bas) = (call.GetInt32Property("m_nStartIndex"), call.GetInt32Property("m_nIndexCount"), call.GetInt32Property("m_nBaseVertex"));
                        for (var i = start; i + 2 < start + count; i += 3)
                        {
                            int a = I(i) + bas, b = I(i + 1) + bas, c = I(i + 2) + bas;
                            var normal = Vector3.Cross(P(b) - P(a), P(c) - P(a));
                            foreach (var v in (int[])[a, b, c])
                                result.Add(material, P(v), normal, C(v));
                        }
                    }
        }
        return result;
    }

    /// <summary>
    /// The lightmapScaleBias of each of a mesh's pieces so that the forward cuts them in the given order. A world
    /// mesh's physics is one piece per (bias, material index) face set, in that sorted order
    /// (WorldCollision.TessellatedPieces, BrushHulls.PiecesWithCorners), a blend's piece then split per layer. So
    /// pieces that follow in the order share a bias while the material index rises, and the bias steps up where
    /// it does not (a second set of a material, or a lower index after a higher one). The physics fixes only the
    /// order: the values are the least such (the first piece at 0), which is what ze_hold_em_p's node 409 holds
    /// (concrete_9, hr_conc, slabs at 0, then hr_conc and slabs at 1). A face rendered flat shows its absolute
    /// bias in the lightmap's density (ledger 188); a subdivided one does not.
    /// </summary>
    /// <param name="materials">Each piece's material index, in the forward's (unshuffled) piece order.</param>
    internal static int[] AssignBiases(IReadOnlyList<int> materials)
    {
        var bias = new int[materials.Count];
        for (var i = 1; i < materials.Count; i++)
            bias[i] = materials[i] > materials[i - 1] ? bias[i - 1] : bias[i - 1] + 1;
        return bias;
    }

    /// <summary>RED2's physics_shape_mesh_count of the map's world physics: how many mesh pieces the compile
    /// cut (empty layer pieces of painted blends included), or null.</summary>
    internal static int? ShippedMeshPieces(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        if (!files.TryGetValue($"maps/{mapName}/world_physics.vmdl_c", out var bytes))
            return null;
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        return resource.EditInfo?.SearchableUserData.TryGetValue("physics_shape_mesh_count", out var value) == true && value is not null
            ? (int)Convert.ToInt64(value.ToString(), System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The world physics' surface table entries the mesh pieces registered, in table order: the part registers
    /// spheres, capsules and hulls first, then each mesh piece in part order, empty ones too (WorldPhysics.Build), so
    /// the entries no other shape uses are the mesh pieces' first surfaces in their part order. Null without the file.
    /// </summary>
    internal static List<uint>? MeshSurfaceOrder(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        if (!files.TryGetValue($"maps/{mapName}/world_physics.vmdl_c", out var bytes))
            return null;
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var phys = resource.Blocks.FirstOrDefault(b => b.Type == BlockType.PHYS) switch
        {
            KeyValuesOrNTRO k => k.Data,
            BinaryKV3 k => k.Data.Root,
            _ => null,
        };
        static ValveKeyValue.KVObject? Child(ValveKeyValue.KVObject? parent, string key)
            => parent is not null && parent.IsCollection && parent.TryGetValue(key, out var child) ? child : null;
        var table = Child(phys, "m_surfacePropertyHashes")?.Values.Select(v => (uint)v.ToInt64(null)).ToList();
        if (table is null)
            return null;
        var others = new HashSet<int>();
        foreach (var part in Child(phys, "m_parts")?.Values ?? [])
            foreach (var kind in new[] { "m_spheres", "m_capsules", "m_hulls" })
                foreach (var shape in Child(Child(part, "m_rnShape"), kind)?.Values ?? [])
                    others.Add((int)(Child(shape, "m_nSurfacePropertyIndex")?.ToInt64(null) ?? -1));
        return [.. table.Where((_, i) => !others.Contains(i))];
    }

    /// <summary>Where the world's meshes go in the .vmap: their walk order and where the props' hull runs stand.</summary>
    /// <param name="Walk">Indices into the (merged) mesh list, in walk order.</param>
    /// <param name="Runs">Each run of props' hulls in the walk: how many meshes come before it and how many hulls it holds.</param>
    /// <param name="ShippedOfWalk">Each walk item's place in the part (pieces and hulls), for the props' check.</param>
    /// <param name="HullWalk">The hulls' walk items, in walk order.</param>
    internal sealed record Layout(List<int> Walk, List<(int MeshesBefore, int Hulls)> Runs, int[] ShippedOfWalk, int[] HullWalk);

    /// <summary>
    /// The world meshes' walk order that gives the shipped part order of their physics pieces. Each mesh gives
    /// the pieces the forward cuts from it (<paramref name="piecesOf"/>: triangles per piece in the forward's
    /// order; a splitting blend gives one per layer, empty ones kept, WorldCollision.SplitLayers), one after
    /// another in the walk, and the part sorts all of them with the props' hulls after every append
    /// (WorldCollision.PartOrder). The meshes come in shipped order (each one non-empty piece, in the soup's
    /// vertex order), so only the empty pieces' places in the part are unknown: searched so that every mesh's
    /// pieces stand together in the walk. Before that, adjacent runs of one material are joined until the
    /// pieces number the shipped count (<paramref name="target"/>, RED2): the soup is the pieces joined in part
    /// order, so which adjacent runs join changes no physics byte, only the count does (node 409's hr_conc
    /// 30 | 8 is one such choice; the leftmost runs are joined first). Null when a mesh has more than one
    /// non-empty piece or no order holds.
    /// </summary>
    /// <param name="propsHold">Whether the props' hulls land in the shipped hull order for this walk-to-part map and
    /// these hull walk items (in walk order).</param>
    /// <remarks>The props' hulls are one run between meshes, or two (ze_hold_em_p's source: node 12 and 13, the 43
    /// fixtures' 86 hulls, node 409's 7 pieces, the 43 bulbs' hulls; WPBUILD_WALK). One run is tried first.</remarks>
    /// <param name="surfaceOrder">The surfaces the mesh pieces registered, in the shipped table's order
    /// (<see cref="MeshSurfaceOrder"/>): a layout must register them so, empty pieces included.</param>
    internal static (List<WorldGeometry.Mesh> Meshes, Layout? Layout) PieceLayout(List<WorldGeometry.Mesh> meshes,
        Func<WorldGeometry.Mesh, (int Triangles, uint Surface)[]> piecesOf, int? target, int hulls, Func<int[], int[], bool> propsHold,
        IReadOnlyList<uint>? surfaceOrder, List<string> notes)
    {
        meshes = [.. meshes];
        var pieces = meshes.Select(piecesOf).ToList();
        if (pieces.Any(p => p.Count(n => n.Triangles > 0) != 1))
        {
            notes.Add("world: a mesh gives other than one non-empty physics piece; meshes written in the one-piece order");
            return (meshes, null);
        }
        // Join adjacent one-piece runs of one material until the count is the shipped one.
        var joined = 0;
        while (target is { } want && pieces.Sum(p => p.Length) > want)
        {
            var m = Enumerable.Range(0, meshes.Count - 1).FirstOrDefault(i => pieces[i].Length == 1 && pieces[i + 1].Length == 1
                && meshes[i].Materials.Count == 1 && meshes[i + 1].Materials.Count == 1
                && string.Equals(meshes[i].Materials[0], meshes[i + 1].Materials[0], StringComparison.Ordinal), -1);
            if (m < 0)
                break;
            var (a, b) = (meshes[m], meshes[m + 1]);
            List<Vector4[]?>? paint = a.Paint is null && b.Paint is null ? null
                : [.. a.Paint ?? Enumerable.Repeat<Vector4[]?>(null, a.Faces.Count), .. b.Paint ?? Enumerable.Repeat<Vector4[]?>(null, b.Faces.Count)];
            List<int>? bias = a.Bias is null && b.Bias is null ? null
                : [.. a.Bias ?? Enumerable.Repeat(0, a.Faces.Count), .. b.Bias ?? Enumerable.Repeat(0, b.Faces.Count)];
            meshes[m] = new WorldGeometry.Mesh(a.Materials, [.. a.Faces, .. b.Faces], a.Name, paint, bias);
            meshes.RemoveAt(m + 1);
            pieces.RemoveAt(m + 1);
            pieces[m] = piecesOf(meshes[m]);
            joined++;
        }
        var total = pieces.Sum(p => p.Length);
        if (joined > 0)
            notes.Add($"world: {joined} adjacent run(s) of one material joined to the shipped {target} physics pieces (which ones join changes no physics byte)");
        if (target is { } t && total != t)
            notes.Add($"world: the meshes give {total} physics pieces, the package {t}");
        var filled = pieces.Select(p => Array.FindIndex(p, n => n.Triangles > 0)).ToArray();
        // Surfaces some other shape registered first: in the table, not among the mesh pieces' own.
        var registeredBefore = surfaceOrder is null ? null
            : pieces.SelectMany(p => p.Select(x => x.Surface)).Where(s => !surfaceOrder.Contains(s)).ToHashSet();
        // The walk's type layout: a mesh pieces, h1 hulls, (b - a) mesh pieces, the other hulls, the rest. One run
        // (h1 = all hulls, the props after `a` pieces) first, most pieces before it first, as the one-piece search did.
        IEnumerable<(int A, int H1, int B)> Layouts()
        {
            for (var a = total; a >= 0; a--)
                yield return (a, hulls, a);
            // Each layout costs a full part sort; two runs are tried only where that stays small (ze: 9 pieces, 129 hulls).
            if (hulls < 2 || (long)total * total * hulls > 40000)
                yield break;
            for (var a = total; a >= 0; a--)
                for (var b = total; b > a; b--)
                    for (var h1 = hulls - 1; h1 >= 1; h1--)
                        yield return (a, h1, b);
        }
        foreach (var (a, h1, b) in Layouts())
        {
            var isHull = new bool[total + hulls];
            for (var w = a; w < a + h1; w++)
                isHull[w] = true;
            for (var w = b + h1; w < b + hulls; w++)
                isHull[w] = true;
            var at = Physics.WorldCollision.PartOrder(Enumerable.Range(0, total + hulls), i => isHull[i] ? 2 : 3);
            var shippedOfWalk = new int[at.Count];
            for (var k = 0; k < at.Count; k++)
                shippedOfWalk[at[k]] = k;
            var hullWalk = Enumerable.Range(0, at.Count).Where(w => isHull[w]).ToArray();
            // The props' hulls first: cheaper than placing the pieces, and it rules out most layouts.
            if (hulls > 0 && !propsHold(shippedOfWalk, hullWalk))
                continue;
            // Each part slot of a mesh piece (after the hulls) back to its walk item.
            var walkOfSlot = new int[total];
            for (var w = 0; w < at.Count; w++)
                if (!isHull[w])
                    walkOfSlot[shippedOfWalk[w] - hulls] = w;
            int SlotOf(int w) => w < 0 || w >= at.Count || isHull[w] ? -1 : shippedOfWalk[w] - hulls;
            var claimed = new bool[total];
            var start = new int[meshes.Count];
            var budget = 200000;
            // Meshes in shipped order take the free slots in order; a slot skipped is an empty piece of a later mesh.
            bool Place(int m, int from, int pending)
            {
                if (--budget < 0)
                    return false;
                if (m == meshes.Count)
                    return pending == 0 && claimed.All(c => c) && SurfacesHold();
                for (var s = from; s < total; s++)
                {
                    if (claimed[s])
                        continue;
                    var first = walkOfSlot[s] - filled[m];
                    var slots = Enumerable.Range(0, pieces[m].Length).Select(j => SlotOf(first + j)).ToArray();
                    if (slots.All(x => x >= 0 && (x == s || !claimed[x])) && slots.Distinct().Count() == slots.Length
                        && slots.Where(x => x != s).All(x => x > s || IsPending(x, s)))
                    {
                        var freed = slots.Count(x => x != s && x < s);
                        foreach (var x in slots)
                            claimed[x] = true;
                        start[m] = first;
                        if (Place(m + 1, s + 1, pending - freed))
                            return true;
                        foreach (var x in slots)
                            claimed[x] = false;
                    }
                    // Else this slot is a later mesh's empty piece.
                    pending++;
                    if (pending > total - meshes.Count)
                        return false;
                }
                return false;
            }
            // A skipped slot below s is one left unclaimed earlier.
            bool IsPending(int x, int s) => x < s && !claimed[x];
            // The pieces' surfaces, first seen in part order, are the shipped table's mesh entries in order.
            bool SurfacesHold()
            {
                if (surfaceOrder is null)
                    return true;
                var bySlot = new uint[total];
                for (var m = 0; m < meshes.Count; m++)
                    for (var j = 0; j < pieces[m].Length; j++)
                        bySlot[SlotOf(start[m] + j)] = pieces[m][j].Surface;
                var seen = bySlot.Where(s => !registeredBefore!.Contains(s)).Distinct().ToList();
                return seen.SequenceEqual(surfaceOrder);
            }
            if (!Place(0, 0, 0))
                continue;
            var walk = Enumerable.Range(0, meshes.Count).OrderBy(m => start[m]).ToList();
            List<(int, int)> runs = h1 == hulls ? [(walk.Count(m => start[m] < a), hulls)]
                : [(walk.Count(m => start[m] < a), h1), (walk.Count(m => start[m] < b + h1), hulls - h1)];
            return (meshes, new Layout(walk, runs, shippedOfWalk, hullWalk));
        }
        notes.Add("world: no walk order gives the shipped part order with every mesh's physics pieces together; meshes written in the one-piece order");
        return (meshes, null);
    }

    /// <summary>
    /// A stored channel back to a paint value. The compile packs it as ConvertFloatToChannel (181363b90,
    /// WorldNodeModels.Unorm8): clamped, times 255, truncated; so byte b stood for a value in [b/255, (b+1)/255)
    /// (node 409: every render corner is its bake's paint truncated, BlendFacesTests). The middle of that range
    /// packs back to b and is within 1/510 of the source's value; 255 stands for 1, the clamp's top.
    /// </summary>
    internal static float Unpack(byte b) => b == 255 ? 1f : (b + 0.5f) / 255f;

    // The layout names the field by the material input it feeds (WorldNodeModels' slots, MeshGeometry.Classify).
    private static bool IsPaint(ValveResourceFormat.Blocks.VBIB.RenderInputLayoutField f)
        => f.Format.ToString() == "R8G8B8A8_UNORM" && string.Equals(f.ShaderSemantic, "VertexPaintBlendParams", StringComparison.OrdinalIgnoreCase);

    private static Vector3 Newell(Vector3[] corners)
    {
        var n = Vector3.Zero;
        for (var i = 0; i < corners.Length; i++)
        {
            var (a, b) = (corners[i], corners[(i + 1) % corners.Length]);
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n;
    }

    private const float Weld = 1f / 32f;

    private static (int, int, int) Cell(Vector3 v) => ((int)MathF.Floor(v.X / Weld), (int)MathF.Floor(v.Y / Weld), (int)MathF.Floor(v.Z / Weld));
}
