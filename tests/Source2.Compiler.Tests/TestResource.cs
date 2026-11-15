using Source2.Compiler.KeyValues;
using Source2.Compiler.Materials;
using Source2.Compiler.Meshes;
using Source2.Compiler.Textures;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler.Tests;

/// <summary>The kind of a compiled resource, from its file name or, without one, its RED2 compiler.</summary>
internal enum TestResourceType
{
    Unknown,
    Material,
    Texture,
    Sound,
    PanoramaVectorGraphic,
    Model,
    Mesh,
    VData,
    Particle,
    EntityLump,
    World,
    WorldNode,
    WorldVisibility,
    ResourceManifest,
    Map,
    SmartProp,
}

/// <summary>A block of a <see cref="TestResource"/>: its type, where it sat in the file, its bytes.</summary>
internal class TestBlock(string type, long offset, byte[] bytes)
{
    public string Type { get; } = type;
    public long Offset { get; } = offset;
    public uint Size => (uint)Bytes.Length;
    public byte[] Bytes { get; } = bytes;

    /// <summary>The block's KV3 tree.</summary>
    /// <exception cref="InvalidOperationException">The block is not KV3.</exception>
    public virtual KVObject AsKeyValueCollection() => throw new InvalidOperationException($"The {Type} block is not KV3.");
}

/// <summary>A KV3 block: its document (format and tree).</summary>
internal class TestKv3Block(string type, long offset, byte[] bytes, KVDocument data) : TestBlock(type, offset, bytes)
{
    public KVDocument Data { get; } = data;

    public override KVObject AsKeyValueCollection() => Data.Root;

    public override string ToString() => Data.Root.ToKV3String(Data.Header?.Format);
}

/// <summary>A KV3 DATA block as a typed resource's tree (an entity lump, a world, a world node...).</summary>
internal sealed class TestKv3Typed(string type, long offset, byte[] bytes, KVDocument data) : TestKv3Block(type, offset, bytes, data)
{
    public new KVObject Data => base.Data.Root;

    /// <summary>The block's format (schema).</summary>
    public KV3ID? Format => base.Data.Header?.Format;
}

/// <summary>One RERL entry.</summary>
internal sealed record TestRefInfo(ulong Id, string Name);

/// <summary>The RERL block.</summary>
internal sealed class TestRefList(long offset, byte[] bytes) : TestBlock("RERL", offset, bytes)
{
    public List<TestRefInfo> ResourceRefInfoList { get; } = [.. ResourceRefs.Read(bytes).Select(r => new TestRefInfo(r.Id, r.Name))];
}

/// <summary>The RED2 block, its lists read from the KV3 tree.</summary>
internal sealed class TestEditInfo(long offset, byte[] bytes, KVDocument data) : TestKv3Block("RED2", offset, bytes, data)
{
    public sealed record InputDependency(string ContentRelativeFilename, string ContentSearchPath, uint FileCRC, bool Optional, bool FileExists, bool IsGameFile);
    public sealed record ArgumentDependency(string ParameterName, string ParameterType, uint Fingerprint, uint FingerprintDefault);
    public sealed record SpecialDependency(string String, string CompilerIdentifier, uint Fingerprint, uint UserData);

    public List<InputDependency> InputDependencies => Inputs("m_InputDependencies");
    public List<InputDependency> AdditionalInputDependencies => Inputs("m_AdditionalInputDependencies");

    public List<ArgumentDependency> ArgumentDependencies =>
        [.. List("m_ArgumentDependencies").Select(a => new ArgumentDependency(a.GetStringProperty("m_ParameterName") ?? "",
            a.GetStringProperty("m_ParameterType") ?? "", a.GetUInt32Property("m_nFingerprint"), a.GetUInt32Property("m_nFingerprintDefault")))];

    public List<SpecialDependency> SpecialDependencies =>
        [.. List("m_SpecialDependencies").Select(d => new SpecialDependency(d.GetStringProperty("m_String") ?? "",
            d.GetStringProperty("m_CompilerIdentifier") ?? "", d.GetUInt32Property("m_nFingerprint"), d.GetUInt32Property("m_nUserData")))];

    public List<string> ChildResourceList => [.. Data.Root.GetArray<string>("m_ChildResourceList") ?? []];

    public List<string> WeakReferenceList => [.. Data.Root.GetArray<string>("m_WeakReferenceList") ?? []];

    public Dictionary<string, KVObject> SearchableUserData =>
        Data.Root.GetSubCollection("m_SearchableUserData") is { } s ? s.Children.ToDictionary(c => c.Key, c => c.Value) : [];

    /// <summary><c>m_SubassetReferences</c>: per type, each name's count; null when absent.</summary>
    public Dictionary<string, Dictionary<string, int>>? SubassetReferences =>
        Data.Root.GetSubCollection("m_SubassetReferences") is { ValueType: KVValueType.Collection } refs
            ? refs.Children.Where(p => p.Value.ValueType == KVValueType.Collection)
                .ToDictionary(p => p.Key, p => p.Value.Children.ToDictionary(c => c.Key, c => Convert.ToInt32(c.Value, System.Globalization.CultureInfo.InvariantCulture)))
            : null;

    /// <summary><c>m_SubassetDefinitions</c>: per type, its names; null when absent.</summary>
    public Dictionary<string, string[]>? SubassetDefinitions =>
        Data.Root.GetSubCollection("m_SubassetDefinitions") is { ValueType: KVValueType.Collection } defs
            ? defs.Children.Where(p => p.Value.IsArray).ToDictionary(p => p.Key, p => p.Value.Values.Select(v => (string)v).ToArray())
            : null;

    private List<InputDependency> Inputs(string key) =>
        [.. List(key).Select(d => new InputDependency(d.GetStringProperty("m_RelativeFilename") ?? "", d.GetStringProperty("m_SearchPath") ?? "",
            d.GetUInt32Property("m_nFileCRC"), d.GetBooleanProperty("m_bOptional"), d.GetBooleanProperty("m_bExists"), d.GetBooleanProperty("m_bIsGameFile")))];

    private IReadOnlyList<KVObject> List(string key) => Data.Root.GetArray(key) ?? [];
}

/// <summary>
/// A compiled resource read with this library's own container and readers, in the shape the older tests
/// ask for it: its blocks with their file offsets, its DATA as the typed reader for its kind
/// (<see cref="CompiledModel"/>, <see cref="CompiledTexture"/>, <see cref="CompiledMaterial"/>, or the KV3
/// tree), RED2 and RERL decoded. New tests use <see cref="CompiledResource"/> and the typed readers directly.
/// </summary>
internal sealed class TestResource : IDisposable
{
    private byte[] _bytes = [];

    public string? FileName { get; set; }

    public CompiledResource Compiled { get; private set; } = null!;

    public ushort Version => Compiled.Version;

    public List<TestBlock> Blocks { get; private set; } = [];

    public void Read(string path)
    {
        FileName ??= Path.GetFileName(path);
        Read(File.ReadAllBytes(path));
    }

    public void Read(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Read(copy.ToArray());
    }

    public void Read(byte[] bytes)
    {
        _bytes = bytes;
        Compiled = CompiledResource.Read(bytes);
        Blocks = [.. Compiled.File.Blocks.Select(Block)];
        _dataBlock = null;
    }

    private static TestBlock Block(ResourceBlock b)
    {
        var at = b.SourceOffset ?? 0;
        if (b.Type == "RERL")
            return new TestRefList(at, b.Data);
        if (!Kv3Binary.IsKv3(b.Data))
            return new TestBlock(b.Type, at, b.Data);
        var document = Kv3Binary.Read(b.Data);
        return b.Type switch
        {
            "RED2" => new TestEditInfo(at, b.Data, document),
            "DATA" => new TestKv3Typed(b.Type, at, b.Data, document),
            _ => new TestKv3Block(b.Type, at, b.Data, document),
        };
    }

    /// <summary>The container's header version (12).</summary>
    public ushort HeaderVersion => ResourceFile.HeaderVersion;

    /// <summary>The container's declared size (where the blocks end).</summary>
    public uint FileSize => BitConverter.ToUInt32(_bytes, 0);

    public TestBlock? GetBlockByType(string type) => Blocks.Find(b => b.Type == type);

    public TestBlock GetBlockByIndex(int index) => Blocks[index];

    public bool ContainsBlockType(string type) => Blocks.Exists(b => b.Type == type);

    public TestEditInfo? EditInfo => GetBlockByType("RED2") as TestEditInfo;

    public TestRefList? ExternalReferences => GetBlockByType("RERL") as TestRefList;

    public TestResourceType ResourceType
    {
        get
        {
            var name = FileName is { } n && n.EndsWith("_c", StringComparison.Ordinal) ? n[..^2] : FileName;
            var ext = name is null ? null : Path.GetExtension(name).TrimStart('.');
            var byName = ext switch
            {
                "vmat" => TestResourceType.Material,
                "vtex" => TestResourceType.Texture,
                "vsnd" => TestResourceType.Sound,
                "vsvg" => TestResourceType.PanoramaVectorGraphic,
                "vmdl" => TestResourceType.Model,
                "vmesh" => TestResourceType.Mesh,
                "vdata" => TestResourceType.VData,
                "vpcf" => TestResourceType.Particle,
                "vents" => TestResourceType.EntityLump,
                "vwrld" => TestResourceType.World,
                "vwnod" => TestResourceType.WorldNode,
                "vvis" => TestResourceType.WorldVisibility,
                "vrman" => TestResourceType.ResourceManifest,
                "vmap" => TestResourceType.Map,
                "vsmart" => TestResourceType.SmartProp,
                _ => TestResourceType.Unknown,
            };
            if (byName != TestResourceType.Unknown)
                return byName;
            var compilers = EditInfo?.SpecialDependencies.Select(d => d.CompilerIdentifier).ToHashSet() ?? [];
            if (compilers.Contains("CompileMaterial")) return TestResourceType.Material;
            if (compilers.Contains("CompileTexture")) return TestResourceType.Texture;
            if (compilers.Contains("CompileSound")) return TestResourceType.Sound;
            if (compilers.Contains("CompileVectorGraphic") || compilers.Contains("CompilePanorama")) return TestResourceType.PanoramaVectorGraphic;
            if (compilers.Contains("CompileModel") || compilers.Contains("CompileModelDoc") || ContainsBlockType("MDAT")) return TestResourceType.Model;
            if (compilers.Contains("CompileVData")) return TestResourceType.VData;
            if (compilers.Contains("CompileParticle")) return TestResourceType.Particle;
            if (compilers.Contains("CompileEntityLump")) return TestResourceType.EntityLump;
            if (compilers.Contains("CompileWorld")) return TestResourceType.World;
            if (compilers.Contains("CompileWorldNode")) return TestResourceType.WorldNode;
            return TestResourceType.Unknown;
        }
    }

    private object? _dataBlock;

    /// <summary>The DATA as its kind's reader: a model, texture or material reader, else the DATA block.</summary>
    public object? DataBlock => _dataBlock ??= ResourceType switch
    {
        TestResourceType.Model => CompiledModel.Read(_bytes),
        TestResourceType.Texture => CompiledTexture.Read(_bytes),
        TestResourceType.Material => CompiledMaterial.Read(_bytes),
        _ => GetBlockByType("DATA"),
    };

    public void Dispose()
    {
    }
}
