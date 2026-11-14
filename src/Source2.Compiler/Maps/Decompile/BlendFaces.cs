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
