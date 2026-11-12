using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A brush entity's geometry back from the model the compile built for it
/// (<c>maps/&lt;map&gt;/entities/&lt;name&gt;_&lt;id&gt;.vmdl_c</c>, Physics.EntityPhysicsModels).
///
/// <para>The compile welds each material piece of the entity's meshes in the entity's space
/// and hulls it; every hull keeps the piece's tool material as a hash
/// (<see cref="Io.ResourceNames.ToolMaterialHash"/>). A hull comes back here as a convex
/// polygon mesh, its faces the hull's own, in the entity's space, its material the name whose
/// hash it carries. That is the piece's convex hull, which is all the hull was built from: a
/// concave piece is not recovered as drawn. Render meshes and triangle-mesh shapes are not
/// read yet.</para>
/// </summary>
public static class BrushGeometry
{
    /// <summary>One convex piece: its corners (entity space) per face, in Hammer's winding
    /// (counter-clockwise seen from outside), and its material.</summary>
    /// <param name="Vertices">The hull's vertices in its own order.</param>
    public sealed record Piece(string Material, Vector3[][] Faces, int CollisionAttribute, Vector3[] Vertices);

    /// <summary>What a model gives back: its pieces, and what it holds that is not read.</summary>
    public sealed record Result(IReadOnlyList<Piece> Pieces, bool HasRender, int TriangleMeshes, IReadOnlyList<uint> UnknownMaterials)
    {
        /// <summary>The model's triangle-mesh shapes (RnMesh: m_Vertices float triples, m_Triangles int
        /// triples, entity space), each with its tool material's name ("" when no known material has it).</summary>
        public IReadOnlyList<Soup> Soups { get; init; } = [];
    }

    /// <summary>One triangle-mesh shape: its vertices and triangles as the shape holds them.</summary>
    public sealed record Soup(string Material, Vector3[] Vertices, (int A, int B, int C)[] Triangles);

    /// <summary>
    /// A table from tool material hash to material name over every name given (".vmat"
    /// or ".vmat_c" paths): the inverse of the hash for every material that can be named.
    /// </summary>
    public static Dictionary<uint, string> MaterialTable(IEnumerable<string> materials)
    {
        var table = new Dictionary<uint, string>();
        foreach (var path in materials)
        {
            var name = path.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? path[..^2] : path;
            var hash = Io.ResourceNames.ToolMaterialHash(name);
            if (hash != 0)
                table.TryAdd(hash, name);
        }
        return table;
    }

    /// <summary><see cref="MaterialTable"/> over the game's materials: every .vmat_c in csgo's and
    /// core's pak01 under <paramref name="game"/> (the install's game folder).</summary>
    public static Dictionary<uint, string> GameMaterials(string game) => MaterialTable(GameMaterialNames(game));

    /// <summary>Every .vmat_c path in csgo's and core's pak01 under <paramref name="game"/>.</summary>
    public static List<string> GameMaterialNames(string game)
    {
        var names = new List<string>();
        foreach (var pak in new[] { Path.Combine(game, "csgo", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk") })
        {
            if (!File.Exists(pak))
                continue;
            using var package = new ValvePak.Package();
            package.Read(pak);
            names.AddRange((package.Entries.GetValueOrDefault("vmat_c") ?? []).Select(e => e.GetFullPath()));
        }
        return names;
    }

    /// <summary>Read a compiled entity model.</summary>
    public static Result Read(byte[] model, IReadOnlyDictionary<uint, string> materials)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(model));
        var hasRender = resource.Blocks.Any(b => b.Type is BlockType.MDAT or BlockType.MVTX or BlockType.VBIB);
        var phys = resource.Blocks.FirstOrDefault(b => b.Type == BlockType.PHYS) switch
        {
            KeyValuesOrNTRO k => k.Data,
            BinaryKV3 k => k.Data.Root,
            _ => null,
        };
        var pieces = new List<Piece>();
        var unknown = new List<uint>();
        var triangleMeshes = 0;
        var soups = new List<Soup>();
        foreach (var part in Children(phys, "m_parts"))
        {
            var shape = Child(part, "m_rnShape");
            foreach (var mesh in Children(shape, "m_meshes"))
            {
                triangleMeshes++;
                var hash = (uint)Number(Child(mesh, "m_nToolMaterialHash"));
                if (!materials.TryGetValue(hash, out var name))
                {
                    unknown.Add(hash);
                    name = "";
                }
                var body = Child(mesh, "m_Mesh");
                var positions = Floats(Blob(Child(body, "m_Vertices")));
                var indices = Blob(Child(body, "m_Triangles"));
                var vertices = Enumerable.Range(0, positions.Length / 3).Select(i => new Vector3(positions[i * 3], positions[(i * 3) + 1], positions[(i * 3) + 2])).ToArray();
                var triangles = Enumerable.Range(0, indices.Length / 12)
                    .Select(k => (BitConverter.ToInt32(indices, k * 12), BitConverter.ToInt32(indices, (k * 12) + 4), BitConverter.ToInt32(indices, (k * 12) + 8))).ToArray();
                soups.Add(new Soup(name, vertices, triangles));
            }
            foreach (var hull in Children(shape, "m_hulls"))
            {
                var hash = (uint)Number(Child(hull, "m_nToolMaterialHash"));
                if (!materials.TryGetValue(hash, out var name))
                {
                    unknown.Add(hash);
                    name = "";
                }
                var body = Child(hull, "m_Hull")!;
                var positions = Positions(body);
                var vertices = Enumerable.Range(0, positions.Length / 3).Select(i => new Vector3(positions[i * 3], positions[(i * 3) + 1], positions[(i * 3) + 2])).ToArray();
                pieces.Add(new Piece(name, HullFaces(body), (int)Number(Child(hull, "m_nCollisionAttributeIndex")), vertices));
            }
        }
        return new Result(pieces, hasRender, triangleMeshes, unknown) { Soups = soups };
    }

    /// <summary>
    /// A hull's faces from its half-edges (RnHull: each edge next, twin, origin vertex, face;
    /// each face its first edge), each face the origins along its loop, turned to wind
    /// counter-clockwise about the face's outward plane normal as Hammer's faces do.
    /// </summary>
    private static Vector3[][] HullFaces(KVObject hull)
    {
        var positions = Positions(hull);
        var edges = Blob(Child(hull, "m_Edges"));
        var faces = Blob(Child(hull, "m_Faces"));
        var planes = Floats(Blob(Child(hull, "m_Planes")));
        var result = new Vector3[faces.Length][];
        for (var f = 0; f < faces.Length; f++)
        {
            var loop = new List<Vector3>();
            int e = faces[f], guard = 0;
            do
            {
                var origin = edges[(e * 4) + 2];
                loop.Add(new Vector3(positions[origin * 3], positions[(origin * 3) + 1], positions[(origin * 3) + 2]));
                e = edges[e * 4];
            }
            while (e != faces[f] && ++guard < edges.Length);
            var normal = new Vector3(planes[f * 4], planes[(f * 4) + 1], planes[(f * 4) + 2]);
            if (Vector3.Dot(Newell(loop), normal) < 0)
                loop.Reverse();
            result[f] = [.. loop];
        }
        return result;
    }

    /// <summary>
    /// A hull's vertex positions, three floats each. An older compile's hull (the older lump
    /// format, GROUND_TRUTH 265) has no m_VertexPositions and holds them as m_Vertices, 12 bytes a
    /// vertex (ze_por_island_skybox's unnamed_8: eight corners, 96 bytes).
    /// </summary>
    private static float[] Positions(KVObject hull)
        => Child(hull, "m_VertexPositions") is { } positions ? Floats(Blob(positions)) : Floats(Blob(Child(hull, "m_Vertices")));

    private static Vector3 Newell(List<Vector3> loop)
    {
        var n = Vector3.Zero;
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n;
    }

    private static float[] Floats(byte[] bytes)
    {
        var f = new float[bytes.Length / 4];
        for (var i = 0; i < f.Length; i++)
            f[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return f;
    }

    private static byte[] Blob(KVObject? value) => value?.AsBlob().ToArray() ?? [];

    private static KVObject? Child(KVObject? parent, string key)
        => parent is not null && parent.IsCollection && parent.TryGetValue(key, out var child) ? child : null;

    private static IEnumerable<KVObject> Children(KVObject? parent, string key) => Child(parent, key)?.Values ?? [];

    private static long Number(KVObject? value)
        => value is null ? 0 : value.ValueType == KVValueType.Boolean ? (value.ToBoolean(CultureInfo.InvariantCulture) ? 1 : 0)
                                                                     : value.ToInt64(CultureInfo.InvariantCulture);
}
