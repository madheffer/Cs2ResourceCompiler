using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using T = Source2.Compiler.DmxBinary.AttrType;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// Builds a <c>.vmap</c> in the shape Hammer saves one: vmap format 40, each element with
/// the attributes, order and DMX types of Hammer's own save (editorbuild 9806,
/// editorversion 400; s2c_rc_probe's probe_classes, s2c_instnest and ze_hold_em_p, read
/// with tools/map-survey/vmap_schema.py). Values a decompile cannot know take what those
/// saves hold for a node nobody touched.
///
/// <para>GUIDs and reference ids are derived from the node ids and a running count, so the
/// same input writes the same file.</para>
/// </summary>
public sealed class VmapBuilder
{
    /// <summary>Hammer's editor build and version in the saves the shapes come from.</summary>
    public const int EditorBuild = 9806, EditorVersion = 400, FormatVersion = 40;

    private readonly string _seed;
    private int _guids;

    /// <summary>The map root.</summary>
    public DmxBinary.Element Root { get; }

    /// <summary>The world node (worldspawn).</summary>
    public DmxBinary.Element World { get; }

    /// <summary>The asset references of the file's prefix.</summary>
    public List<string> AssetReferences { get; } = [];

    public VmapBuilder(string seed)
    {
        _seed = seed;
        Root = New("CMapRootElement");
        World = New("CMapWorld");
        var camera = New("CStoredCamera")
            .Set("position", T.Vector3, Vector3.Zero)
            .Set("lookat", T.Vector3, new Vector3(1, 0, 0));
        var cameras = New("CStoredCameras")
            .Set("activecamera", T.Int, -1)
            .Set("cameras", T.Array(T.Element), Array.Empty<object?>());
        var visibility = New("CVisibilityMgr");
        Node(visibility, 0, 0);
        visibility.Set("nodes", T.Array(T.Element), Array.Empty<object?>())
                  .Set("hiddenFlags", T.Array(T.Int), Array.Empty<object?>());
        Placement(visibility, Vector3.Zero, Vector3.Zero, Vector3.One);
        Flags(visibility);
        var variables = New("CMapVariableSet")
            .Set("variableNames", T.Array(T.String), Array.Empty<object?>())
            .Set("variableValues", T.Array(T.String), Array.Empty<object?>())
            .Set("variableTypeNames", T.Array(T.String), Array.Empty<object?>())
            .Set("variableTypeParameters", T.Array(T.String), Array.Empty<object?>())
            .Set("m_ChoiceGroups", T.Array(T.Element), Array.Empty<object?>());
        var selection = New("CMapSelectionSet")
            .Set("children", T.Array(T.Element), Array.Empty<object?>())
            .Set("selectionSetName", T.String, "")
            .Set("selectionSetData", T.Element, null);

        Root.Set("isprefab", T.Bool, false)
            .Set("editorbuild", T.Int, EditorBuild)
            .Set("editorversion", T.Int, EditorVersion)
            .Set("itemFile", T.String, "")
            .Set("defaultcamera", T.Element, camera)
            .Set("3dcameras", T.Element, cameras)
            .Set("world", T.Element, World)
            .Set("visbility", T.Element, visibility)
            .Set("mapVariables", T.Element, variables)
            .Set("rootSelectionSet", T.Element, selection)
            .Set("m_ReferencedMeshSnapshots", T.Array(T.Element), Array.Empty<object?>())
            .Set("m_bIsCordoning", T.Bool, false)
            .Set("m_bCordonsVisible", T.Bool, false)
            .Set("nodeInstanceData", T.Array(T.Element), Array.Empty<object?>());
    }

    /// <summary>The world's keys, outputs and the CMapWorld fields of its own.</summary>
    public void SetWorld(int nodeId, IEnumerable<KeyValuePair<string, string>> keys,
                         IEnumerable<MapEntities.Connection> connections, bool fixupEntityNames, string mapUsageType)
    {
        Node(World, nodeId, 0);
        World.Set("relayPlugData", T.Element, PlugList())
             .Set("connectionsData", T.Array(T.Element), Connections(connections))
             .Set("entity_properties", T.Element, Props(keys))
             .Set("nextDecalID", T.Int, 0)
             .Set("fixupEntityNames", T.Bool, fixupEntityNames)
             .Set("mapUsageType", T.String, mapUsageType);
        Placement(World, Vector3.Zero, Vector3.Zero, Vector3.One);
        Flags(World);
    }

    /// <summary>A CMapEntity under <paramref name="parent"/> (the world when null).</summary>
    public DmxBinary.Element AddEntity(int nodeId, IEnumerable<KeyValuePair<string, string>> keys, Vector3 origin,
                                       Vector3 angles, Vector3 scales, IEnumerable<MapEntities.Connection> connections,
                                       bool editorOnly = false, DmxBinary.Element? parent = null)
    {
        var e = New("CMapEntity");
        Node(e, nodeId, ReferenceId(nodeId));
        e.Set("relayPlugData", T.Element, PlugList())
         .Set("connectionsData", T.Array(T.Element), Connections(connections))
         .Set("entity_properties", T.Element, Props(keys))
         .Set("hitNormal", T.Vector3, new Vector3(0, 0, 1))
         .Set("isProceduralEntity", T.Bool, false);
        Placement(e, origin, angles, scales);
        Flags(e, editorOnly);
        AddChild(parent ?? World, e);
        return e;
    }

    /// <summary>A CMapWorldLayer in the world, at the origin: its entities keep their world
    /// placement. Shape: Mako's layers (s2c_big) without the transform pin and the
    /// group fields, as a CMapGroup is saved bare (s2c_instnest).</summary>
    public DmxBinary.Element AddLayer(int nodeId, string worldLayerName)
    {
        var layer = New("CMapWorldLayer");
        Node(layer, nodeId, ReferenceId(nodeId));
        layer.Set("worldLayerName", T.String, worldLayerName);
        Placement(layer, Vector3.Zero, Vector3.Zero, Vector3.One);
        Flags(layer);
        AddChild(World, layer);
        return layer;
    }

    /// <summary>
    /// A CMapMesh under <paramref name="parent"/>: the polygons (corners in the node's space,
    /// counter-clockwise from outside), each with an index into <paramref name="materials"/>.
    /// Corners at the same position are one vertex. The node's fields are a Hammer save's for
    /// a mesh nobody changed (probe_classes' brush meshes); the texture axes are Hammer's
    /// world alignment, picked by the face normal's largest component, at scale 0.25 as those
    /// meshes hold. Texture coordinates follow the axes at an assumed 512 texels: they are
    /// not recovered.
    /// </summary>
    /// <param name="vertexOrder">The vertices' order, where it matters (a hull's, which the
    /// compile's hull builder reads them in); the rest follow by first use.</param>
    public DmxBinary.Element AddMesh(DmxBinary.Element parent, int nodeId, Vector3 origin, Vector3 angles,
                                     IReadOnlyList<(Vector3[] Corners, int Material)> faces, IReadOnlyList<string> materials,
                                     IReadOnlyList<Vector3>? vertexOrder = null)
    {
        // Vertices welded by exact position, in the order given, then first-use order.
        var vertexOf = new Dictionary<Vector3, int>();
        var positions = new List<Vector3>();
        foreach (var v in vertexOrder ?? [])
            if (vertexOf.TryAdd(v, positions.Count))
                positions.Add(v);
        var loops = faces.Select(f => f.Corners.Select(c =>
        {
            if (!vertexOf.TryGetValue(c, out var v))
            {
                vertexOf[c] = v = positions.Count;
                positions.Add(c);
            }
            return v;
        }).ToArray()).ToList();

        // Half-edges in opposite pairs (2k, 2k + 1). An edge points TO a vertex, and a face's
        // corners are its edges' targets along next (MapMeshes.LocalFaces).
        var to = new List<int>();
        var face = new List<int>();
        var next = new List<int>();
        var pairOf = new Dictionary<(int From, int To), int>();
        var faceFirst = new int[loops.Count];
        for (var f = 0; f < loops.Count; f++)
        {
            var loop = loops[f];
            var ids = new int[loop.Length];
            for (var i = 0; i < loop.Length; i++)
            {
                var (a, b) = (loop[(i + loop.Length - 1) % loop.Length], loop[i]);
                if (!pairOf.TryGetValue((a, b), out var e))
                {
                    e = to.Count;
                    to.Add(b); face.Add(-1); next.Add(-1);
                    to.Add(a); face.Add(-1); next.Add(-1);
                    pairOf[(a, b)] = e;
                    pairOf[(b, a)] = e + 1;
                }
                face[e] = f;
                ids[i] = e;
            }
            for (var i = 0; i < loop.Length; i++)
                next[ids[i]] = ids[(i + 1) % loop.Length];
            faceFirst[f] = ids[0];
        }
        // An edge with no face (an open mesh) runs round its boundary: its next is the
        // faceless edge leaving its target.
        for (var e = 0; e < to.Count; e++)
            if (face[e] < 0)
                next[e] = Enumerable.Range(0, to.Count).FirstOrDefault(x => face[x] < 0 && to[x ^ 1] == to[e], e);
        var vertexEdge = new int[positions.Count];
        for (var e = to.Count - 1; e >= 0; e--)
            vertexEdge[to[e ^ 1]] = e;

        var normals = faces.Select(f => Vector3.Normalize(Newell(f.Corners))).ToArray();
        var axes = normals.Select(WorldAxes).ToArray();
        var texcoords = new object?[to.Count];
        var cornerNormals = new object?[to.Count];
        var tangents = new object?[to.Count];
        for (var e = 0; e < to.Count; e++)
        {
            var f = Math.Max(face[e], 0);
            var (u, v) = axes[f];
            var p = positions[to[e]];
            texcoords[e] = new Vector2(Vector3.Dot(p, u) / (TextureScale * 512f), Vector3.Dot(p, v) / (TextureScale * 512f));
            cornerNormals[e] = normals[f];
            var sign = Vector3.Dot(Vector3.Cross(normals[f], u), v) < 0 ? -1f : 1f;
            tangents[e] = new Vector4(u, sign);
        }

        static object?[] Ints(IEnumerable<int> xs) => xs.Select(x => (object?)x).ToArray();
        var data = New("CDmePolygonMesh", "meshData")
            .Set("vertexEdgeIndices", T.Array(T.Int), Ints(vertexEdge))
            .Set("vertexDataIndices", T.Array(T.Int), Ints(Enumerable.Range(0, positions.Count)))
            .Set("edgeVertexIndices", T.Array(T.Int), Ints(to))
            .Set("edgeOppositeIndices", T.Array(T.Int), Ints(Enumerable.Range(0, to.Count).Select(e => e ^ 1)))
            .Set("edgeNextIndices", T.Array(T.Int), Ints(next))
            .Set("edgeFaceIndices", T.Array(T.Int), Ints(face))
            .Set("edgeDataIndices", T.Array(T.Int), Ints(Enumerable.Range(0, to.Count).Select(e => e / 2)))
            .Set("edgeVertexDataIndices", T.Array(T.Int), Ints(Enumerable.Range(0, to.Count)))
            .Set("faceEdgeIndices", T.Array(T.Int), Ints(faceFirst))
            .Set("faceDataIndices", T.Array(T.Int), Ints(Enumerable.Range(0, loops.Count)))
            .Set("materials", T.Array(T.String), materials.Cast<object?>().ToArray())
            .Set("vertexData", T.Element, DataArray(positions.Count,
                Stream("position", 3, T.Vector3, positions.Cast<object?>().ToArray())))
            .Set("faceVertexData", T.Element, DataArray(to.Count,
                Stream("texcoord", 1, T.Vector2, texcoords),
                Stream("normal", 1, T.Vector3, cornerNormals),
                Stream("tangent", 1, T.Vector4, tangents)))
            .Set("edgeData", T.Element, DataArray(to.Count / 2,
                Stream("flags", 3, T.Int, Ints(new int[to.Count / 2]))))
            .Set("faceData", T.Element, DataArray(loops.Count,
                Stream("textureScale", 0, T.Vector2, loops.Select(_ => (object?)new Vector2(TextureScale, TextureScale)).ToArray()),
                Stream("textureAxisU", 0, T.Vector4, axes.Select(a => (object?)new Vector4(a.U, 0)).ToArray()),
                Stream("textureAxisV", 0, T.Vector4, axes.Select(a => (object?)new Vector4(a.V, 0)).ToArray()),
                Stream("materialindex", 8, T.Int, Ints(faces.Select(f => f.Material))),
                Stream("flags", 3, T.Int, Ints(new int[loops.Count])),
                Stream("lightmapScaleBias", 1, T.Int, Ints(new int[loops.Count]))))
            .Set("subdivisionData", T.Element, New("CDmePolygonMeshSubdivisionData")
                .Set("subdivisionLevels", T.Array(T.Int), Ints(new int[to.Count]))
                .Set("streams", T.Array(T.Element), Array.Empty<object?>()));

        var mesh = New("CMapMesh");
        Node(mesh, nodeId, ReferenceId(nodeId));
        mesh.Set("cubeMapName", T.String, "")
            .Set("lightGroup", T.String, "")
            .Set("visexclude", T.Bool, false)
            .Set("disablemerging", T.Bool, false)
            .Set("renderwithdynamic", T.Bool, false)
            .Set("disableHeightDisplacement", T.Bool, false)
            .Set("fademindist", T.Float, -1f)
            .Set("fademaxdist", T.Float, 0f)
            .Set("bakelighting", T.Bool, true)
            .Set("precomputelightprobes", T.Bool, true)
            .Set("renderToCubemaps", T.Bool, true)
            .Set("emissiveLightingEnabled", T.Bool, true)
            .Set("emissiveLightingBoost", T.Float, 1f)
            .Set("disableShadows", T.Int, 0)
            .Set("lightingDummy", T.Bool, false)
            .Set("smoothingAngle", T.Float, 40f)
            .Set("tintColor", T.Color, new byte[] { 255, 255, 255, 255 })
            .Set("renderAmt", T.Int, 255)
            .Set("physicsType", T.String, "default")
            .Set("physicsGroup", T.String, "")
            .Set("physicsInteractsAs", T.String, "")
            .Set("physicsInteractsWith", T.String, "")
            .Set("physicsInteractsExclude", T.String, "")
            .Set("meshData", T.Element, data)
            .Set("physicsSimplificationOverride", T.Bool, false)
            .Set("physicsSimplificationError", T.Float, 0f);
        Placement(mesh, origin, angles, Vector3.One);
        Flags(mesh);
        AddChild(parent, mesh);
        return mesh;
    }

    private const float TextureScale = 0.25f;

    /// <summary>Hammer's world alignment: a face mostly facing z maps x and -y, one facing y
    /// maps x and -z, one facing x maps y and -z (probe_classes' box brushes).</summary>
    private static (Vector3 U, Vector3 V) WorldAxes(Vector3 n)
    {
        var a = Vector3.Abs(n);
        return a.Z >= a.X && a.Z >= a.Y ? (Vector3.UnitX, -Vector3.UnitY)
             : a.Y >= a.X ? (Vector3.UnitX, -Vector3.UnitZ)
             : (Vector3.UnitY, -Vector3.UnitZ);
    }

    private static Vector3 Newell(Vector3[] loop)
    {
        var n = Vector3.Zero;
        for (var i = 0; i < loop.Length; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Length]);
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n;
    }

    private DmxBinary.Element DataArray(int size, params DmxBinary.Element[] streams)
        => New("CDmePolygonMeshDataArray").Set("size", T.Int, size).Set("streams", T.Array(T.Element), streams.Cast<object?>().ToArray());

    /// <summary>A data stream as Hammer writes a standard one: the element named
    /// "&lt;name&gt;:&lt;semantic index&gt;", which is how readers find it (MapMeshes.Stream); name,
    /// semantic and state flags as probe_classes' meshes hold them.</summary>
    private DmxBinary.Element Stream(string name, int stateFlags, byte scalar, object?[] data)
        => New("CDmePolygonMeshDataStream", name + ":0")
            .Set("standardAttributeName", T.String, name)
            .Set("semanticName", T.String, name)
            .Set("semanticIndex", T.Int, 0)
            .Set("vertexBufferLocation", T.Int, 0)
            .Set("dataStateFlags", T.Int, stateFlags)
            .Set("subdivisionBinding", T.Element, null)
            .Set("data", T.Array(scalar), data);

    /// <summary>
    /// A CMapPrefab in the world, at the origin unturned (its map's nodes keep their compiled
    /// placement), naming <paramref name="targetMapPath"/>. Shape: s2c_prefabprobe9's prefabs; loaded
    /// with the map (loadIfNested on, not at runtime), as a prefab that ships merged into its parent.
    /// </summary>
    public DmxBinary.Element AddPrefab(int nodeId, string targetMapPath, bool fixupEntityNames, string targetName)
    {
        var prefab = New("CMapPrefab");
        Node(prefab, nodeId, ReferenceId(nodeId));
        prefab.Set("relayPlugData", T.Element, PlugList())
              .Set("connectionsData", T.Array(T.Element), Array.Empty<object?>())
              .Set("target", T.Element, null)
              .Set("variableOverrideNames", T.Array(T.String), Array.Empty<object?>())
              .Set("variableOverrideValues", T.Array(T.String), Array.Empty<object?>());
        Placement(prefab, Vector3.Zero, Vector3.Zero, Vector3.One);
        Flags(prefab);
        prefab.Set("customVisGroup", T.String, "")
              .Set("randomSeed", T.Int, 0)
              .Set("tintColor", T.Color, new byte[] { 255, 255, 255, 255 })
              .Set("visexclude", T.Bool, false)
              .Set("targetMapPath", T.String, targetMapPath)
              .Set("targetName", T.String, targetName)
              .Set("fixupEntityNames", T.Bool, fixupEntityNames)
              .Set("useTargetNameAsPrefix", T.Bool, targetName.Length > 0)
              .Set("loadIfNested", T.Bool, true)
              .Set("prefabRuntimeEntity", T.Bool, false)
              .Set("loadAtRuntime", T.Bool, false);
        AddChild(World, prefab);
        return prefab;
    }

    /// <summary>The file: vmap 40, binary 9.</summary>
    public byte[] Write()
    {
        var prefix = DmxBinary.NewElement("$prefix_element$", "", Guid.Empty)
            .Set("map_asset_references", T.Array(T.String), AssetReferences.Cast<object?>().ToArray());
        return DmxBinaryWriter.Write("vmap", FormatVersion, Root, [prefix]);
    }

    private static void AddChild(DmxBinary.Element parent, DmxBinary.Element child)
    {
        var children = parent.Get<object?[]>("children") ?? [];
        parent.Attributes["children"] = children.Append(child).ToArray();
    }

    private static void Node(DmxBinary.Element e, int nodeId, ulong referenceId)
        => e.Set("nodeID", T.Int, nodeId)
            .Set("referenceID", T.UInt64, referenceId)
            .Set("children", T.Array(T.Element), e.Get<object?[]>("children") ?? Array.Empty<object?>())
            .Set("variableTargetKeys", T.Array(T.String), Array.Empty<object?>())
            .Set("variableNames", T.Array(T.String), Array.Empty<object?>());

    private static void Placement(DmxBinary.Element e, Vector3 origin, Vector3 angles, Vector3 scales)
        => e.Set("origin", T.Vector3, origin).Set("angles", T.QAngle, angles).Set("scales", T.Vector3, scales);

    private static void Flags(DmxBinary.Element e, bool editorOnly = false)
        => e.Set("transformLocked", T.Bool, false).Set("force_hidden", T.Bool, false).Set("editorOnly", T.Bool, editorOnly);

    private DmxBinary.Element PlugList()
        => New("DmePlugList")
            .Set("names", T.Array(T.String), Array.Empty<object?>())
            .Set("dataTypes", T.Array(T.Int), Array.Empty<object?>())
            .Set("plugTypes", T.Array(T.Int), Array.Empty<object?>())
            .Set("descriptions", T.Array(T.String), Array.Empty<object?>());

    /// <summary>Every key a string, as Hammer keeps them (MapEntities).</summary>
    private DmxBinary.Element Props(IEnumerable<KeyValuePair<string, string>> keys)
    {
        var props = New("EditGameClassProps");
        foreach (var (key, value) in keys)
            props.Set(key, T.String, value);
        return props;
    }

    private object?[] Connections(IEnumerable<MapEntities.Connection> connections)
        => connections.Select(c => (object?)New("DmeConnectionData")
            .Set("outputName", T.String, c.OutputName)
            // ENTITY_CONNECTION_TARGET_NAME, which every Hammer connection in the corpus holds.
            .Set("targetType", T.Int, 7)
            .Set("targetName", T.String, c.TargetName)
            .Set("inputName", T.String, c.InputName)
            .Set("overrideParam", T.String, c.OverrideParam)
            .Set("delay", T.Float, c.Delay)
            .Set("timesToFire", T.Int, c.TimesToFire)).ToArray();

    private DmxBinary.Element New(string type, string name = "") => DmxBinary.NewElement(type, name, NextGuid());

    private Guid NextGuid()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{_seed}/guid/{_guids++}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>A node's referenceID: Hammer's is random 64-bit, nonzero; this one is the node
    /// id hashed with the seed, so it is stable and as unlikely to collide.</summary>
    private ulong ReferenceId(int nodeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{_seed}/ref/{nodeId}"));
        var id = BitConverter.ToUInt64(hash, 0);
        return id == 0 ? 1 : id;
    }
}
