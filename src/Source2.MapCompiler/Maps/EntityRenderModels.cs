using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The render half of a brush entity's model: one embedded mesh per drawn
/// material piece of each mesh under the entity (depth first), named
/// <c>hammer_mesh_&lt;piece&gt;</c>, a name already taken getting the next free
/// number appended (<c>hammer_mesh_01</c>, <c>hammer_mesh_02</c>; atixref's
/// 58 render models). A piece whose material draws nothing is left out, and a
/// mesh left with none has no embedded mesh.
/// <para>Corners are in the entity's space (the mesh's matrix, then the
/// inverse of the entity's), normals turned the same way; the model compiler
/// recomputes tangents (preserve_tangents 0: CMesh_ComputeTangents,
/// <see cref="MeshTangents"/>), then each piece is one draw built as a world
/// node's plain draw (<see cref="WorldNodeModels"/>).</para>
/// </summary>
internal static class EntityRenderModels
{
    /// <summary>One embedded mesh: its name and the entry it draws.</summary>
    /// <summary>One embedded mesh: its name, the entry it draws and the signature its layout follows.</summary>
    public sealed record Piece(string Name, WorldNodeEntry Entry, IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement> Layout);

    /// <summary>
    /// A brush entity's whole model with its render meshes (<see cref="EntityModelAuthor"/>):
    /// each piece built as a plain draw of a non-map mesh, beside the physics.
    /// </summary>
    public static byte[] Author(Physics.EntityPhysicsModels.Model model, GameContent content)
    {
        var facts = new Dictionary<string, WorldNodeModels.MaterialFacts>(StringComparer.OrdinalIgnoreCase);
        WorldNodeModels.MaterialFacts Material(string path)
        {
            if (!facts.TryGetValue(path, out var f))
                facts[path] = f = content.Read(path + "_c") is { } bytes
                    ? new WorldNodeModels.MaterialFacts(Source2.Compiler.MaterialAuthor.ExtractInputSignature(bytes), Source2.Compiler.MaterialAuthor.ExtractDepthSignature(bytes))
                    : new WorldNodeModels.MaterialFacts([], []);
            return f;
        }
        var pieces = Pieces(model.Entity!, model.Meshes, m => Physics.EntityPhysicsModels.DrawsNothing(content.Material(m)), m => Material(m).Signature,
                            model.TransformOf, model.Applied);
        var meshes = pieces.Select(p =>
        {
            var b = WorldNodeModels.Build(model.Path, [p.Entry], false, Material, mapMesh: false, layout: new WorldNodeModels.MaterialFacts(p.Layout, []));
            return new EntityModelAuthor.Mesh(p.Name, b.Model with { EntityClass = model.ClassName }, b.Buffers, b.References);
        }).ToList();
        return EntityModelAuthor.Container(meshes, Physics.WorldPhysicsTrees.Phys(model.Physics),
                                           Physics.WorldPhysicsTrees.Red2(model.Physics, content.SurfaceName, model.ClassName));
    }

    public static List<Piece> Pieces(DmxBinary.Element entity, IReadOnlyList<DmxBinary.Element> meshes, Func<string, bool> drawsNothing,
                                     Func<string, IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement>> signature,
                                     Func<DmxBinary.Element, CTransform>? transformOf = null, string? applied = null, bool shift = true)
    {
        transformOf ??= CTransform.FromNode;
        var toEntity = transformOf(entity).Inverse().Matrix();
        var result = new List<Piece>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in meshes)
        {
            var names = mesh.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials")?.Select(x => x as string ?? "").ToArray() ?? [];
            int? whole = applied == null ? null : Array.IndexOf(names, applied) is var at and >= 0 ? at : names.Length;
            var toWorld = transformOf(mesh).Matrix();
            var all = MapMeshCorners.Build(mesh, shift, null, withTangent: false, allStreams: true, whole);
            // Hammer's export gives the mesh the paint streams any of its materials reads, as the world's.
            var needs = (applied != null ? [applied] : names).SelectMany(m => signature(m).Select(e => e.Semantic)).ToHashSet();
            all = [.. all.Select(p => NodeMeshEntries.WithPaintStreams(p, needs))];
            // Hammer's high precision flag: a texcoord out of +-1.03125 anywhere in the
            // mesh, faces that draw nothing included.
            var precise = MapMeshCorners.TexcoordsOutOfRange(mesh, shift);
            var pieces = all.Select(p => (Piece: p, Material: applied ?? (p.Material < names.Length ? names[p.Material] : "")))
                .Where(p => !drawsNothing(p.Material)).ToList();
            for (var k = 0; k < pieces.Count; k++)
            {
                var (piece, material) = pieces[k];
                var name = UniqueName($"hammer_mesh_{k}", taken);
                taken.Add(name);
                result.Add(new Piece(name, Entry(piece, material, toWorld, toEntity, precise, signature(material)), signature(material)));
            }
        }
        return result;
    }

    /// <summary>
    /// ModelDoc's unique node name (rc 0923 181f1a7e0, from 181410e00 when ModelDoc_CreateNode adds a named node):
    /// a name no existing one equals (V_stricmp_fast) is kept. Otherwise its trailing digits, leading zeros
    /// skipped, give N: the base is the name up to those digits and the first try N + 1; with no trailing
    /// number, or only zeros, the base is the whole name and the first try 1. Tries go up while a name is
    /// taken, at most as far as the first try plus the count of existing names, then "". So hammer_mesh_0
    /// becomes hammer_mesh_01, 02 ..., hammer_mesh_1 becomes hammer_mesh_2, 3 ... (ze_doom_p2's downers).
    /// </summary>
    internal static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
            return name;
        var digits = name.Length;
        while (digits > 0 && char.IsAsciiDigit(name[digits - 1]))
            digits--;
        var first = digits;
        while (first < name.Length && name[first] == '0')
            first++;
        string stem;
        long n;
        if (first < name.Length)
        {
            stem = name[..first];
            n = (long)CNumbers.Atoi(name[first..]) + 1;
            if ((uint)n == 0xffffffff)
                n = 1;
        }
        else
        {
            stem = name;
            n = 1;
        }
        var last = n + taken.Count;
        for (; n <= last; n++)
        {
            var candidate = stem + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
                return candidate;
        }
        return "";
    }

    // The piece in the entity's space with tangents recomputed, reduced to the streams its material reads.
    private static WorldNodeEntry Entry(MapMeshCorners.Piece piece, string material, float[] toWorld, float[] toEntity, bool precise,
                                                IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement> signature)
    {
        var count = piece.Vertices.Length / piece.Stride;
        // The first stream of each name.
        var at = new Dictionary<string, int>();
        foreach (var s in piece.Streams)
            at.TryAdd(s.Name, s.First);
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        var v = (float[])piece.Vertices.Clone();
        for (var c = 0; c < count; c++)
        {
            var o = c * piece.Stride;
            positions[c] = MapMeshes.Transform(toEntity, MapMeshes.Transform(toWorld, new Vector3(v[o], v[o + 1], v[o + 2])));
            (v[o], v[o + 1], v[o + 2]) = (positions[c].X, positions[c].Y, positions[c].Z);
            if (at.TryGetValue("normal", out var n))
            {
                normals[c] = NodeMeshEntries.Normalise(NodeMeshEntries.Rotate(toEntity, NodeMeshEntries.Rotate(toWorld, new Vector3(v[o + n], v[o + n + 1], v[o + n + 2]))));
                (v[o + n], v[o + n + 1], v[o + n + 2]) = (normals[c].X, normals[c].Y, normals[c].Z);
            }
            // The model compile takes the mesh through DMX, which flips V there and back:
            // 1 - (1 - v), which rounds (ze_hold_em_p's end_breakable texcoords, -0.5222222 to BF05B05C).
            foreach (var s in piece.Streams)
                if (s.Name == "texcoord" && s.Count >= 2)
                    v[o + s.First + 1] = 1f - (1f - v[o + s.First + 1]);
            if (at.TryGetValue("texcoord", out var t))
                uvs[c] = new Vector2(v[o + t], v[o + t + 1]);
        }
        var tangents = MeshTangents.Corners(positions, normals, uvs, piece.Indices);
        // The streams the material reads, the recomputed tangent after the normal.
        // A brush entity's mesh carries no baked per-vertex lighting (end_breakable_240 in ze_hold_em_p).
        var kept = MaterialStreams.Kept(piece.Streams, MaterialStreams.Inputs(signature.Select(x => x.Semantic)), false)
            .Where(i => !Io.Tier0Strings.EqualsIgnoreCase(piece.Streams[i].Name, "PerVertexLighting")).ToList();
        var layout = new List<(string Name, int First, int Count, bool Tangent)>();
        foreach (var i in kept)
        {
            var s = piece.Streams[i];
            layout.Add((s.Name, s.First, s.Count, false));
            if (s.Name == "normal")
                layout.Add(("tangent", 0, 4, true));
        }
        var stride = layout.Sum(l => l.Count);
        // A corner's tangent: the indices name corners one to one.
        var cornerTangent = new Vector4[count];
        for (var k = 0; k < piece.Indices.Length; k++)
            cornerTangent[piece.Indices[k]] = tangents[k];
        var vertices = new float[count * stride];
        for (var c = 0; c < count; c++)
        {
            var to = c * stride;
            foreach (var (name, first, n, tangent) in layout)
            {
                if (tangent)
                {
                    var tg = cornerTangent[c];
                    (vertices[to], vertices[to + 1], vertices[to + 2], vertices[to + 3]) = (tg.X, tg.Y, tg.Z, tg.W);
                }
                else
                    Array.Copy(v, (c * piece.Stride) + first, vertices, to, n);
                to += n;
            }
        }
        var streams = new List<WrbMeshEntry.Stream>();
        foreach (var (name, _, n, _) in layout)
            streams.Add(new WrbMeshEntry.Stream(name, streams.Count(s => s.Name == name), n, (byte)(precise && name == "texcoord" ? 1 : 0), 39 + n));
        var facts = new WrbMeshEntry { Material = material, Field28 = Vector4.One, Stride = stride, Streams = streams };
        return new WorldNodeEntry(material, stride, vertices, piece.Indices, facts);
    }
}
