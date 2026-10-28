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

    private DmxBinary.Element New(string type) => DmxBinary.NewElement(type, "", NextGuid());

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
