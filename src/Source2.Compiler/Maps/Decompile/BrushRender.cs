using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// The render half of a brush entity's model read back (<see cref="EntityRenderModels"/> backwards).
///
/// <para>Each embedded mesh is one drawn material piece of one Hammer mesh under the entity, named
/// <c>hammer_mesh_&lt;piece&gt;</c> with the next free number appended when taken, so the names tell
/// which pieces came from one mesh (<see cref="MeshesOf"/>). Its triangles are the piece's corners in
/// the entity's space, after the draw's weld, vertex cache and overdraw passes (NodeDraw). The physics
/// hulls of the same model (<see cref="BrushGeometry"/>) carry every piece, drawn or not: a mesh comes
/// back whole as its drawn triangles, each its own face, plus the faces they leave open on the convex
/// hull of all its points, those taking the material of the nodraw hull holding them
/// (<see cref="Rebuild"/>). Every mesh of the test maps' render models is closed and convex
/// (EntityRenderProbe.RenderSurvey: 119 of 119). Fed back in the shipped order, the draw passes give
/// that order again on 117 of the 119 (MapDecompilerTests.BrushRender_CompilesAsTheSourceDoes).</para>
///
/// <para>The corner values are chosen so the forward's quantization gives the shipped bits back
/// (<see cref="Texcoord"/>, <see cref="Normal"/>, <see cref="Tint"/>): the shipped value where it
/// survives the forward, else the nearest that does; a packed draw's texcoords are then moved within
/// their cells to its UV density (<see cref="FitDensity"/>). Which of the equivalent values the source
/// held is not knowable from the package. Not recovered: Hammer's texture axes and scale (the compile
/// reads the corners' texcoords only; the axes stay the world alignment), and the faces' polygons
/// (the drawn faces stay triangles).</para>
/// </summary>
internal static class BrushRender
{
    /// <summary>A triangle corner as the vertex buffer holds it: the position (entity space), the first
    /// texcoord's components as stored (float bits, SNORM16 or half), the packed tangent frame and the
    /// tint (RGBA8, little endian) when the layout has one.</summary>
    public readonly record struct Corner(Vector3 Position, int U, int V, uint Frame, uint? Tint);

    /// <summary>One embedded mesh: its name, material, first texcoord's format, its corners (three a
    /// triangle, in index order), the fields it holds that are not read back (not all zero) and the
    /// draw's UV density (m_flUvDensity, computed on the float texcoords before they are packed).</summary>
    public sealed record Piece(string Name, string Material, string TexcoordFormat, Corner[] Corners, IReadOnlyList<string> Unread, float UvDensity);

    /// <summary>A rebuilt face: corners in the entity's space, the material (an index into the mesh's
    /// list) and, for a drawn face, the render corners behind its corners.</summary>
    public sealed record Face(Vector3[] Corners, int Material, Corner[]? Render);

    /// <summary>The embedded meshes of a compiled entity model, in model order.</summary>
    public static List<Piece> Read(byte[] bytes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var pieces = new List<Piece>();
        if (resource.DataBlock is not Model model)
            return pieces;
        foreach (var (mesh, _, name, _) in model.GetEmbeddedMeshesAndLoD())
        {
            var call = mesh.Data.GetArray("m_sceneObjects").SelectMany(so => so.GetArray("m_drawCalls")).FirstOrDefault();
            if (call is null)
                continue;
            var vb = mesh.VBIB.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
            var ib = mesh.VBIB.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
            var fields = vb.InputLayoutFields;
            int? Offset(string semantic, int index) => fields.Where(f => f.SemanticName.Equals(semantic, StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == index)
                                                             .Select(f => (int?)f.Offset).FirstOrDefault();
            var position = Offset("POSITION", 0) ?? 0;
            var texcoord = Offset("TEXCOORD", 0);
            var frame = Offset("NORMAL", 0);
            var tint = Offset("COLOR", 0);
            var format = fields.FirstOrDefault(f => f.SemanticName.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && f.SemanticIndex == 0).Format.ToString();
            var stride = (int)vb.ElementSizeInBytes;
            var data = vb.Data;
            var read = new HashSet<(string, int)> { ("POSITION", 0), ("TEXCOORD", 0), ("NORMAL", 0), ("COLOR", 0) };
            // Every other field: unread unless zero throughout (the second texcoord and the blend
            // params a blend material's mesh gets zero filled, NodeMeshEntries.WithPaintStreams).
            var unread = new List<string>();
            foreach (var f in fields.Where(f => !read.Contains((f.SemanticName.ToUpperInvariant(), f.SemanticIndex))))
            {
                var size = (fields.Where(g => g.Offset > f.Offset).Select(g => (int)g.Offset).DefaultIfEmpty(stride).Min()) - (int)f.Offset;
                if (Enumerable.Range(0, (int)vb.ElementCount).Any(v => data.AsSpan((v * stride) + (int)f.Offset, size).IndexOfAnyExcept((byte)0) >= 0))
                    unread.Add($"{f.SemanticName}{f.SemanticIndex}");
            }
            Corner At(int v)
            {
                var o = v * stride;
                var p = new Vector3(BitConverter.ToSingle(data, o + position), BitConverter.ToSingle(data, o + position + 4), BitConverter.ToSingle(data, o + position + 8));
                var (u, w) = texcoord is not { } t ? (0, 0)
                    : format == "R32G32_FLOAT" ? (BitConverter.ToInt32(data, o + t), BitConverter.ToInt32(data, o + t + 4))
                    : (BitConverter.ToInt16(data, o + t) & (format == "R16G16_SNORM" ? -1 : 0xffff), BitConverter.ToInt16(data, o + t + 2) & (format == "R16G16_SNORM" ? -1 : 0xffff));
                return new Corner(p, u, w, frame is { } n ? BitConverter.ToUInt32(data, o + n) : 0, tint is { } c ? BitConverter.ToUInt32(data, o + c) : null);
            }
            int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
            var start = call.GetInt32Property("m_nStartIndex");
            var count = call.GetInt32Property("m_nIndexCount");
            var baseVertex = call.GetInt32Property("m_nBaseVertex");
            var corners = Enumerable.Range(start, count).Select(i => At(Index(i) + baseVertex)).ToArray();
            pieces.Add(new Piece(name, call.GetStringProperty("m_material") ?? "", format, corners, unread,
                                 call.ContainsKey("m_flUvDensity") ? call.GetFloatProperty("m_flUvDensity") : 0f));
        }
        return pieces;
    }

    /// <summary>
    /// The Hammer meshes the embedded meshes came from, each a run of piece indices: the names the
    /// forward gives (<see cref="EntityRenderModels.Pieces"/>: <c>hammer_mesh_&lt;k&gt;</c>, k counting a
    /// mesh's drawn pieces from 0, the next free number appended to a taken name) replayed over the
    /// list. A continuing piece's name starts with k + 1 &gt; 0 and a new mesh's with 0, so the two never
    /// meet. Null when the names are not the forward's.
    /// </summary>
    public static List<List<int>>? MeshesOf(IReadOnlyList<string> names)
    {
        var meshes = new List<List<int>>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        string Next(int k)
        {
            var name = $"hammer_mesh_{k}";
            for (var n = 1; taken.Contains(name); n++)
                name = $"hammer_mesh_{k}{n}";
            return name;
        }
        for (var i = 0; i < names.Count; i++)
        {
            if (meshes.Count > 0 && names[i] == Next(meshes[^1].Count))
                meshes[^1].Add(i);
            else if (names[i] == Next(0))
                meshes.Add([i]);
            else
                return null;
            taken.Add(names[i]);
        }
        return meshes;
    }

    /// <summary>
    /// One Hammer mesh back from its drawn pieces (render triangles, each its own face, corners as the
    /// index buffer lists them) and the hulls the compile made of its pieces (<paramref name="hulls"/>,
    /// in the mesh's material order; <paramref name="materialOf"/> gives each hull's index into it):
    /// the faces the drawn ones leave open on the convex hull of all their points, each a polygon of
    /// every point on its plane (collinear ones kept, as a closed mesh's faces hold them) with the
    /// material of the nodraw hull holding most of its points. A flat piece's hull (<paramref name="thickened"/>) is
    /// its points pushed out by the minimum thickness: those pushed points are not the mesh's. Null, with
    /// the reason, when <paramref name="closed"/> and the drawn and rebuilt faces do not close (a mesh
    /// with no physics keeps only its drawn faces).
    /// </summary>
    public static (List<Face>? Faces, string? Reason) Rebuild(IReadOnlyList<(Piece Piece, int Material)> drawn, IReadOnlyList<BrushGeometry.Piece> hulls,
                                                               IReadOnlyList<int> materialOf, IReadOnlyList<bool> thickened, Func<string, bool> drawsNothing,
                                                               bool closed = true)
    {
        var faces = new List<Face>();
        foreach (var (piece, material) in drawn)
            for (var t = 0; t + 2 < piece.Corners.Length; t += 3)
                faces.Add(new Face([piece.Corners[t].Position, piece.Corners[t + 1].Position, piece.Corners[t + 2].Position], material,
                                   [piece.Corners[t], piece.Corners[t + 1], piece.Corners[t + 2]]));
        if (!closed)
            return (faces, null);
        var drawnPoints = new HashSet<Vector3>();
        foreach (var f in faces)
            drawnPoints.UnionWith(f.Corners);
        // Each nodraw hull's own points: a pushed one's are the drawn corners it was pushed from (each a
        // vertex, or one the thickness below one along an axis), when the forward pushes those into it
        // (atixref's disco_wall_toggle: one nodraw quad among five drawn faces).
        var real = new HashSet<Vector3>?[hulls.Count];
        for (var h = 0; h < hulls.Count; h++)
        {
            if (!drawsNothing(hulls[h].Material))
                continue;
            var vertices = hulls[h].Vertices;
            bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-8f;
            var from = drawnPoints.Where(p => vertices.Any(v => Near(v, p) || Near(v, p + Vector3.UnitX) || Near(v, p + Vector3.UnitY) || Near(v, p + Vector3.UnitZ))).ToHashSet();
            real[h] = !vertices.All(drawnPoints.Contains) && PushedFrom(vertices, from) ? from : [.. vertices];
        }
        var points = new HashSet<Vector3>(drawnPoints);
        for (var h = 0; h < hulls.Count; h++)
            if (real[h] is { } r)
                points.UnionWith(r);
            else if (!thickened[h])
                points.UnionWith(hulls[h].Vertices);
        var all = points.ToList();
        var scale = all.Aggregate(1f, (m, p) => MathF.Max(m, MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z)))));
        var eps = scale * 1e-5f;

        // The planes the drawn triangles cover; every other plane the mesh's points all lie behind is
        // a face that draws nothing, taking the material of the nodraw hull holding most of its points.
        var covered = faces.Select(f => Plane(f.Corners)).Where(p => p.Normal != Vector3.Zero).ToList();
        bool On((Vector3 Normal, float D) a, (Vector3 Normal, float D) b) => Vector3.Dot(a.Normal, b.Normal) > 1f - 1e-5f && MathF.Abs(a.D - b.D) <= eps;
        var nodraw = Enumerable.Range(0, hulls.Count).Where(h => real[h] is not null).ToList();
        if (nodraw.Count > 0)
            foreach (var plane in SupportingPlanes(all, eps, nodraw.SelectMany(h => hulls[h].Faces)))
            {
                if (covered.Any(c => On(c, plane)))
                    continue;
                var onPlane = all.Where(p => MathF.Abs(Vector3.Dot(plane.Normal, p) - plane.D) <= eps).ToList();
                var loop = Boundary(onPlane, plane.Normal, eps);
                if (loop.Length < 3)
                    continue;
                var owner = nodraw.OrderByDescending(h => loop.Count(real[h]!.Contains)).First();
                faces.Add(new Face(loop, materialOf[owner], null));
            }

        // Closed: every directed edge has its opposite.
        var edges = new Dictionary<(Vector3, Vector3), int>();
        foreach (var f in faces)
            for (var i = 0; i < f.Corners.Length; i++)
            {
                var key = (f.Corners[i], f.Corners[(i + 1) % f.Corners.Length]);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        var open = edges.Keys.Count(k => !edges.ContainsKey((k.Item2, k.Item1)));
        var doubled = edges.Values.Count(v => v > 1);
        if (open > 0 || doubled > 0)
            return (null, $"the drawn triangles and the open hull faces do not close ({open} open edge(s), {doubled} used twice)"
                          + (Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DEBUG") == "1"
                              ? ": " + string.Join(" | ", faces.Select(f => $"{(f.Render is null ? "nd" : "d")}{f.Material} " + string.Join(" ", f.Corners.Select(c => $"{c.X:R},{c.Y:R},{c.Z:R}"))))
                                + " || hulls " + string.Join(" | ", hulls.Select((h, i) => $"{h.Material}{(thickened[i] ? "(thick)" : "")} " + string.Join(" ", h.Vertices.Select(c => $"{c.X:R},{c.Y:R},{c.Z:R}"))))
                              : ""));
        return (faces, null);
    }

    /// <summary>
    /// The planes every point lies on or behind and at least three lie on: over every triple of points
    /// when there are few, else over the given hull faces' planes.
    /// </summary>
    private static List<(Vector3 Normal, float D)> SupportingPlanes(List<Vector3> points, float eps, IEnumerable<Vector3[]> hullFaces)
    {
        var planes = new List<(Vector3 Normal, float D)>();
        void Try(Vector3 n, float d)
        {
            if (points.Any(p => Vector3.Dot(n, p) - d > eps) || points.Count(p => MathF.Abs(Vector3.Dot(n, p) - d) <= eps) < 3)
                return;
            if (!planes.Any(q => Vector3.Dot(q.Normal, n) > 1f - 1e-5f && MathF.Abs(q.D - d) <= eps))
                planes.Add((n, d));
        }
        if (points.Count <= 48)
        {
            for (var i = 0; i < points.Count; i++)
                for (var j = i + 1; j < points.Count; j++)
                    for (var k = j + 1; k < points.Count; k++)
                    {
                        var n = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
                        if (n.LengthSquared() < eps * eps)
                            continue;
                        n = Vector3.Normalize(n);
                        var d = Vector3.Dot(n, points[i]);
                        Try(n, d);
                        Try(-n, -d);
                    }
        }
        else
            foreach (var face in hullFaces)
                if (Plane(face) is { Normal: var n, D: var d } && n != Vector3.Zero)
                    Try(n, d);
        return planes;
    }

    /// <summary>Whether a hull is the forward's push of a flat or degenerate point set: the map builder's
    /// hull of <paramref name="from"/> pushes it out (RnHullBuilder, FUN_180384ab0), and every vertex is a
    /// point of it or one moved by the minimum thickness (1) along +x, +y or +z. Which pushed points the
    /// compile's hull keeps depends on the order it meets them, so this is the structure, not the bits.</summary>
    internal static bool PushedFrom(IReadOnlyCollection<Vector3> vertices, HashSet<Vector3> from)
    {
        if (from.Count < 3 || ForwardHullPoints([.. from]) is not { } hull || hull.IsSubsetOf(from))
            return false;
        bool Near(Vector3 v) => from.Any(p => Vector3.DistanceSquared(v, p) < 1e-8f || Vector3.DistanceSquared(v, p + Vector3.UnitX) < 1e-8f
                                              || Vector3.DistanceSquared(v, p + Vector3.UnitY) < 1e-8f || Vector3.DistanceSquared(v, p + Vector3.UnitZ) < 1e-8f);
        return vertices.All(Near);
    }

    /// <summary>The points of the hull the entity model step makes of a point set (the loop of
    /// EntityPhysicsModels.Build: the map builder's hull, ShapePoints, the compile's hull), or null.</summary>
    internal static HashSet<Vector3>? ForwardHullPoints(Vector3[] points)
    {
        var qh = Physics.RnHullBuilder.BuildHull(points, Physics.RnHullBuilder.Options.MapBuilder, out _);
        var shape = qh == null ? null : Physics.BrushHulls.ShapePoints([.. qh.HullVertices.Select(v => new Vector3(v.X, v.Y, v.Z))]);
        if (shape == null || Physics.RnHullBuilder.Create(shape, Physics.RnHullBuilder.Options.Compile, out _) is not { } hull)
            return null;
        Physics.RnHullBuilder.Transform(hull, Physics.RnHullBuilder.Identity);
        return [.. hull.VertexPositions];
    }

    /// <summary>A polygon's plane: its normalised Newell normal and offset (zero normal when degenerate).</summary>
    private static (Vector3 Normal, float D) Plane(IReadOnlyList<Vector3> loop)
    {
        var n = Vector3.Zero;
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        if (n.LengthSquared() == 0f)
            return (Vector3.Zero, 0f);
        n = Vector3.Normalize(n);
        var c = loop.Aggregate(Vector3.Zero, (s, p) => s + p) / loop.Count;
        return (n, Vector3.Dot(n, c));
    }

    /// <summary>The convex boundary of coplanar points, counter-clockwise about <paramref name="normal"/>,
    /// points on its edges kept, points inside dropped.</summary>
    private static Vector3[] Boundary(List<Vector3> points, Vector3 normal, float eps)
    {
        if (points.Count < 3)
            return [];
        var u = Vector3.Normalize(MathF.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY));
        var v = Vector3.Cross(normal, u);
        var flat = points.Select(p => (P: p, X: (double)Vector3.Dot(p, u), Y: (double)Vector3.Dot(p, v))).OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        // Monotone chain keeping collinear points: drop a point only on a strict right turn.
        double Cross((Vector3 P, double X, double Y) o, (Vector3 P, double X, double Y) a, (Vector3 P, double X, double Y) b)
            => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        var tol = (double)eps * eps * 4;
        List<(Vector3 P, double X, double Y)> Chain(IEnumerable<(Vector3 P, double X, double Y)> ordered)
        {
            var chain = new List<(Vector3 P, double X, double Y)>();
            foreach (var p in ordered)
            {
                while (chain.Count >= 2 && Cross(chain[^2], chain[^1], p) < -tol)
                    chain.RemoveAt(chain.Count - 1);
                chain.Add(p);
            }
            return chain;
        }
        var lower = Chain(flat);
        var upper = Chain(Enumerable.Reverse(flat));
        var hull = lower.Take(lower.Count - 1).Concat(upper.Take(upper.Count - 1)).Select(p => p.P).Distinct().ToArray();
        return hull;
    }

    /// <summary>
    /// A texcoord the forward stores as the shipped bits: through the DMX round trip on v (1 - (1 - v),
    /// EntityRenderModels) and the format's packing (WorldNodeModels: float, SNORM16 truncated, half).
    /// The shipped value where it survives; else, for SNORM, the coarsest binary fraction that packs to
    /// it, then the floats round the shipped value. Null when none in reach does.
    /// </summary>
    /// <summary>Whether the forward stores <paramref name="x"/> as <paramref name="raw"/>: through the DMX
    /// round trip when <paramref name="flipped"/> (the V component), then the format's packing.</summary>
    private static bool Packs(float x, int raw, string format, bool flipped)
    {
        var through = flipped ? 1f - (1f - x) : x;
        return format switch
        {
            "R32G32_FLOAT" => BitConverter.SingleToInt32Bits(through) == raw,
            "R16G16_SNORM" => WorldNodeModels.Snorm16(through) == (short)raw,
            _ => WorldNodeModels.ToHalf(through) == (ushort)raw,
        };
    }

    public static Vector2? Texcoord(Corner c, string format)
    {
        float? Solve(int raw, bool flipped)
        {
            bool Packs(float x) => BrushRender.Packs(x, raw, format, flipped);
            var shipped = format switch
            {
                "R32G32_FLOAT" => BitConverter.Int32BitsToSingle(raw),
                "R16G16_SNORM" => MathF.Max((short)raw / 32767f, -1f),
                _ => (float)BitConverter.UInt16BitsToHalf((ushort)raw),
            };
            if (format == "R16G16_SNORM")
                for (var k = 0; k <= 24; k++)
                {
                    var grid = MathF.ScaleB(1f, -k);
                    var x = MathF.Round(shipped / grid) * grid;
                    if (Packs(x))
                        return x;
                }
            for (var r = 0; r <= 64; r++)
            {
                var (up, down) = (shipped, shipped);
                for (var s = 0; s < r; s++)
                    (up, down) = (MathF.BitIncrement(up), MathF.BitDecrement(down));
                if (Packs(up))
                    return up;
                if (Packs(down))
                    return down;
            }
            return null;
        }
        return Solve(c.U, false) is { } u && Solve(c.V, true) is { } v ? new Vector2(u, v) : null;
    }

    /// <summary>
    /// Texcoords for a packed piece's distinct corners whose draw UV density (UvDensity over its
    /// triangles, on the texcoords after the DMX round trip, the forward's 20th percentile) is the
    /// shipped one. Packing loses the source's floats and the density keeps a trace of them (atixref's
    /// block_roof_waterm: u 0 and 1, v 0.3735212 and 0.5051669, two rows of one packed value each).
    /// Corners sharing a packed component share its float, as a projected mapping gives them; those
    /// values start at <paramref name="chosen"/>'s and move through the floats that still pack to their
    /// bits: one at a time across the cell, then float by float, then two at a time, each move kept when
    /// it brings the density nearer, until it is the shipped one. Null when no move in reach gets there.
    /// </summary>
    public static Dictionary<Corner, Vector2>? FitDensity(Piece piece, Dictionary<Corner, Vector2> chosen, Func<Corner, Vector3> entityNormal)
    {
        var distinct = chosen.Keys.ToList();
        var index = distinct.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var positions = distinct.Select(c => c.Position).ToArray();
        var indices = piece.Corners.Select(c => index[c]).ToArray();
        // One value per (axis, packed bits).
        var keys = distinct.SelectMany(c => new[] { (0, c.U), (1, c.V) }).Distinct().ToList();
        var slot = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        var values = keys.Select(k => distinct.First(c => (k.Item1 == 0 ? c.U : c.V) == k.Item2) is var c0 ? (k.Item1 == 0 ? chosen[c0].X : chosen[c0].Y) : 0f).ToArray();
        var uSlot = distinct.Select(c => slot[(0, c.U)]).ToArray();
        var vSlot = distinct.Select(c => slot[(1, c.V)]).ToArray();
        float Density()
            => UvDensity.Compute(positions, [.. distinct.Select((_, i) => new Vector2(values[uSlot[i]], 1f - (1f - values[vSlot[i]])))], indices);
        Dictionary<Corner, Vector2> Result() => distinct.Select((c, i) => (c, new Vector2(values[uSlot[i]], values[vSlot[i]]))).ToDictionary(p => p.c, p => p.Item2);
        var target = piece.UvDensity;
        // The tangents follow the texcoords too (EntityRenderModels.Entry: MeshTangents over the corners,
        // one each, then packed with the normal): a fit holds only where every frame stays the shipped one.
        var cornerPositions = piece.Corners.Select(c => c.Position).ToArray();
        var cornerNormals = piece.Corners.Select(entityNormal).ToArray();
        var sequential = Enumerable.Range(0, piece.Corners.Length).ToArray();
        bool Frames()
        {
            var uvs = piece.Corners.Select(c => new Vector2(values[slot[(0, c.U)]], 1f - (1f - values[slot[(1, c.V)]]))).ToArray();
            var tangents = MeshTangents.Corners(cornerPositions, cornerNormals, uvs, sequential);
            for (var k = 0; k < piece.Corners.Length; k++)
                if (TangentFrame.Compress(cornerNormals[k], tangents[k]) != piece.Corners[k].Frame)
                    return false;
            return true;
        }
        // Where the shipped order is not the one the source's faces give (the tangents sum per corner in
        // triangle order), the frames cannot hold here: the face order search checks them afterwards.
        var framesHold = Frames();
        bool Hit(float d) => BitConverter.SingleToInt32Bits(d) == BitConverter.SingleToInt32Bits(target) && (!framesHold || Frames());
        // A SNORM draw stays in [-1, 1]: past it the forward packs the draw as halves (NodeDraw.TexcoordFormat).
        bool Fits(int s, float x) => Packs(x, keys[s].Item2, piece.TexcoordFormat, keys[s].Item1 == 1)
                                     && (piece.TexcoordFormat != "R16G16_SNORM" || MathF.Abs(keys[s].Item1 == 1 ? 1f - (1f - x) : x) <= 1f);
        var now = Density();
        if (Hit(now))
            return Result();
        // The floats that pack to a value's bits, sampled across the cell.
        List<float> Cell(int s)
        {
            float Edge(float from, bool up)
            {
                var step = MathF.Abs(from) * 1e-7f + 1e-12f;
                var at = from;
                while (Fits(s, up ? at + step : at - step) && step < 1f)
                {
                    at = up ? at + step : at - step;
                    step *= 2f;
                }
                for (; step > 0f && step >= MathF.Abs(at) * 1e-9f; step *= 0.5f)
                    if (Fits(s, up ? at + step : at - step))
                        at = up ? at + step : at - step;
                return at;
            }
            var (lo, hi) = (Edge(values[s], false), Edge(values[s], true));
            return [.. Enumerable.Range(0, 257).Select(k => lo + ((hi - lo) * k / 256f)).Where(x => Fits(s, x))];
        }
        // The floats either side of a value that still pack to its bits, nearest first.
        IEnumerable<float> Steps(int s, int reach)
        {
            float up = values[s], down = values[s];
            for (var k = 0; k < reach; k++)
            {
                up = MathF.BitIncrement(up);
                down = MathF.BitDecrement(down);
                if (Fits(s, up))
                    yield return up;
                if (Fits(s, down))
                    yield return down;
            }
        }
        // Tries each value of a slot, keeping the nearest; true when the density lands.
        bool Scan(int s, IEnumerable<float> xs)
        {
            var (keep, best) = (values[s], MathF.Abs(now - target));
            foreach (var x in xs.ToList())
            {
                values[s] = x;
                var d = Density();
                if (Hit(d))
                    return true;
                if (MathF.Abs(d - target) < best)
                    (keep, best) = (x, MathF.Abs(d - target));
            }
            values[s] = keep;
            now = Density();
            return false;
        }
        // A value bisected across its cell for the density's crossing of the shipped one (monotone while
        // one triangle stays the percentile's), the nearest kept; true when it lands.
        bool Bisect(int s)
        {
            var cell = Cell(s);
            if (cell.Count == 0)
                return false;
            float F(float x)
            {
                values[s] = x;
                return Density() - target;
            }
            var (lo, hi) = (cell[0], cell[^1]);
            var (flo, fhi) = (F(lo), F(hi));
            var best = MathF.Abs(flo) < MathF.Abs(fhi) ? lo : hi;
            if (flo == 0f || fhi == 0f)
            {
                values[s] = flo == 0f ? lo : hi;
                return Hit(Density());
            }
            if (MathF.Sign(flo) != MathF.Sign(fhi))
                for (var guard = 0; guard < 200 && MathF.BitIncrement(lo) < hi; guard++)
                {
                    var mid = lo + ((hi - lo) * 0.5f);
                    if (mid <= lo || mid >= hi)
                        break;
                    var fm = F(mid);
                    if (fm == 0f && Hit(Density()))
                        return true;
                    if (MathF.Abs(fm) < MathF.Abs(F(best)))
                        best = mid;
                    if (MathF.Sign(fm) == MathF.Sign(flo))
                        (lo, flo) = (mid, fm);
                    else
                        hi = mid;
                }
            foreach (var x in new[] { lo, hi, best })
                if (Fits(s, x) && F(x) == 0f && Hit(Density()))
                    return true;
            values[s] = best;
            now = Density();
            return false;
        }
        for (var pass = 0; pass < 3; pass++)
            for (var s = 0; s < keys.Count; s++)
                if (Scan(s, Cell(s)) || Bisect(s))
                    return Result();
        // Two at a time: one value nudged, another bisected (the density's steps on one value can
        // skip the shipped float).
        for (var a = 0; a < keys.Count; a++)
        {
            var keepA = values[a];
            foreach (var x in Steps(a, 16).Concat(Cell(a).Take(64)).ToList())
            {
                values[a] = x;
                for (var b = 0; b < keys.Count; b++)
                {
                    if (b == a)
                        continue;
                    var keepB = values[b];
                    if (Bisect(b))
                        return Result();
                    values[b] = keepB;
                }
            }
            values[a] = keepA;
            now = Density();
        }
        if (Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DEBUG") == "1")
            Console.Error.WriteLine($"density fit: {piece.Name} target {target:R} reached {now:R}; " + string.Join(" ", keys.Select((k, s) => $"{(k.Item1 == 0 ? "u" : "v")}{k.Item2}={values[s]:R}")));
        return null;
    }

    /// <summary>
    /// A corner normal (mesh space) whose packed octahedral normal the forward writes as the shipped
    /// one's (bits 12-31 of the frame, TangentFrame.Compress, after <paramref name="toEntity"/>, the
    /// forward's turn into the entity's space and renormalisation): the first of
    /// <paramref name="candidates"/> that does, else the normal those bits decode to, turned back by
    /// <paramref name="fromEntity"/>. The tangent bits follow from it in the forward.
    /// </summary>
    public static Vector3 Normal(uint frame, IEnumerable<Vector3> candidates, Func<Vector3, Vector3> toEntity, Func<Vector3, Vector3> fromEntity)
    {
        const uint NormalBits = 0xfffff000u;
        foreach (var c in candidates)
            if ((TangentFrame.Compress(toEntity(c), Vector4.Zero) & NormalBits) == (frame & NormalBits))
                return c;
        var decoded = Decode(frame);
        foreach (var c in new[] { fromEntity(decoded), decoded })
            if ((TangentFrame.Compress(toEntity(c), Vector4.Zero) & NormalBits) == (frame & NormalBits))
                return c;
        return fromEntity(decoded);
    }

    /// <summary>The unit normal a packed frame's octahedral bits decode to, as TangentFrame.Compress rebuilds it.</summary>
    internal static Vector3 Decode(uint frame)
    {
        var qu = (frame >> 12) & 0x3ff;
        var qv = (frame >> 22) & 0x3ff;
        var fx = ((float)qu / 1023f * 2f) - 1f;
        var fy = ((float)qv / 1023f * 2f) - 1f;
        var fz = 1f - (MathF.Abs(fx) + MathF.Abs(fy));
        if (fz < 0f)
            (fx, fy) = ((1f - MathF.Abs(fy)) * (fx < 0f ? -1f : 1f), (1f - MathF.Abs(fx)) * (fy < 0f ? -1f : 1f));
        var n = new Vector3(fx, fy, fz);
        return n.LengthSquared() > 0f ? Vector3.Normalize(n) : n;
    }

    /// <summary>A tint the forward packs to the shipped RGBA8 (WorldNodeModels.Unorm8, truncating): per
    /// channel byte / 255, stepped up where that truncates a step low.</summary>
    public static Vector4 Tint(uint rgba)
    {
        float Channel(int shift)
        {
            var b = (byte)(rgba >> shift);
            var x = b / 255f;
            for (var s = 0; s < 64 && WorldNodeModels.Unorm8(x) != b; s++)
                x = MathF.BitIncrement(x);
            return x;
        }
        return new Vector4(Channel(0), Channel(8), Channel(16), Channel(24));
    }
}
