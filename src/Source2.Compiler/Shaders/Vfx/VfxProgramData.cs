// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/VfxProgramData.cs and ShaderUtilHelpers.cs: the resource (KV3) layout only, which is the
// one CS2 ships (VCS 72); the older vcs2 binary layout is not read.
using System.Diagnostics;
using Source2.Compiler.KeyValues;
using ValveKeyValue;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// A compiled shader program file (<c>&lt;shader&gt;_&lt;platform&gt;_&lt;model&gt;_&lt;stage&gt;.vcs</c>), as CS2 ships
/// it: a Source 2 resource (version 72) whose DATA block holds the program in KV3 and whose tail holds each
/// static combo's bytecode, zstd-compressed with a dictionary from materialsystem2.dll
/// (<see cref="ZstdDictionary"/>).
/// </summary>
public sealed class VfxProgramData
{
    /// <summary>The file name it was read under.</summary>
    public string FilenamePath { get; private set; } = "";

    /// <summary>The shader's name (the file name before platform, model and stage).</summary>
    public string ShaderName { get; private set; } = "";

    /// <summary>The container.</summary>
    public CompiledResource Resource { get; private set; } = null!;

    /// <summary>The stage, from the file name.</summary>
    public VcsProgramType VcsProgramType { get; private set; } = VcsProgramType.Undetermined;

    /// <summary>The platform, from the file name.</summary>
    public VcsPlatformType VcsPlatformType { get; private set; } = VcsPlatformType.Undetermined;

    /// <summary>The shader model, from the file name.</summary>
    public VcsShaderModelType VcsShaderModelType { get; private set; } = VcsShaderModelType.Undetermined;

    /// <summary>A features file's header (modes, feature list); null for a stage file.</summary>
    public FeaturesHeaderBlock? FeaturesHeader { get; private set; }

    /// <summary>The resource version, 72 for every file CS2 ships.</summary>
    public int VcsVersion { get; private set; }

    /// <summary><c>m_variableDescriptionVersionHash</c>.</summary>
    public Guid VariableDescriptionVersionHash { get; private set; }

    /// <summary><c>m_nVariableSourceMax</c>.</summary>
    public int VariableSourceMax { get; private set; }

    /// <summary><c>m_programHashes</c>.</summary>
    public List<Guid> ProgramHashes { get; } = [];

    /// <summary><c>m_staticComboArray</c> (a features file's feature list).</summary>
    public VfxCombo[] StaticComboArray { get; private set; } = [];

    /// <summary><c>m_staticComboRuleArray</c>.</summary>
    public VfxRule[] StaticComboRules { get; private set; } = [];

    /// <summary><c>m_dynamicComboArray</c>.</summary>
    public VfxCombo[] DynamicComboArray { get; private set; } = [];

    /// <summary><c>m_dynamicComboRuleArray</c>.</summary>
    public VfxRule[] DynamicComboRules { get; private set; } = [];

    /// <summary><c>m_variableDescriptionArray</c>.</summary>
    public VfxVariableDescription[] VariableDescriptions { get; private set; } = [];

    /// <summary><c>m_vsInputSignatureArray</c>.</summary>
    public VsInputSignature[] VsInputSignatures { get; private set; } = [];

    /// <summary>Each static combo the file carries, by id.</summary>
    public SortedDictionary<long, VfxStaticComboVcsEntry> StaticComboEntries { get; } = [];

    private ComboConfigMapping? dynamicComboMapping;

    private VfxProgramData()
    {
    }

    /// <summary>
    /// Read a program file. <paramref name="fileName"/> names its stage, platform and model
    /// (<c>csgo_complex_vulkan_60_rtx.vcs</c>), as the file itself does not.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a VCS 72 resource, or malformed.</exception>
    public static VfxProgramData Read(string fileName, byte[] bytes)
    {
        var program = new VfxProgramData { FilenamePath = fileName };
        program.Resource = CompiledResource.Read(bytes);
        program.VcsVersion = program.Resource.Version;
        program.SetFileNameDerivedProperties(fileName);
        if (program.VcsVersion != 72)
            throw new InvalidDataException($"{fileName}: VCS version {program.VcsVersion}; only 72, the version CS2 ships, is read.");
        var data = program.Resource.Kv3("DATA") ?? throw new InvalidDataException($"{fileName}: no KV3 DATA block.");
        if (program.VcsProgramType is VcsProgramType.Features)
        {
            program.FeaturesHeader = new FeaturesHeaderBlock(data);
            program.UnserializeKV3ProgramData(data.GetSubCollection("m_programData")
                ?? throw new InvalidDataException($"{fileName}: a features file without m_programData."));
        }
        else
            program.UnserializeKV3ProgramData(data);
        return program;
    }

    /// <summary>The static combo bytecode blocks' bytes: what follows the container's declared size.</summary>
    internal byte[] Tail => Resource.File.Tail;

    private void UnserializeKV3ProgramData(KVObject data)
    {
        foreach (var hashObject in data.GetArray("m_programHashes"))
        {
            var hashBytes = hashObject.GetArray<byte>("m_nHashChar")!;
            Debug.Assert(hashBytes.Length == 16);
            ProgramHashes.Add(new Guid(hashBytes));
        }

        VariableDescriptionVersionHash = new Guid(data.GetSubCollection("m_variableDescriptionVersionHash")!.GetArray<byte>("m_nHashChar")!);
        VariableSourceMax = data.GetInt32Property("m_nVariableSourceMax");

        var staticCombos = data.GetArray("m_staticComboArray");
        StaticComboArray = new VfxCombo[staticCombos.Count];
        for (var i = 0; i < staticCombos.Count; i++)
            StaticComboArray[i] = new VfxCombo(staticCombos[i], i, VcsVersion);

        var staticComboRules = data.GetArray("m_staticComboRuleArray");
        StaticComboRules = new VfxRule[staticComboRules.Count];
        for (var i = 0; i < staticComboRules.Count; i++)
            StaticComboRules[i] = new VfxRule(staticComboRules[i], i);

        var dynamicCombos = data.GetArray("m_dynamicComboArray");
        DynamicComboArray = new VfxCombo[dynamicCombos.Count];
        for (var i = 0; i < dynamicCombos.Count; i++)
            DynamicComboArray[i] = new VfxCombo(dynamicCombos[i], i, VcsVersion);

        var dynamicComboRules = data.GetArray("m_dynamicComboRuleArray");
        DynamicComboRules = new VfxRule[dynamicComboRules.Count];
        for (var i = 0; i < dynamicComboRules.Count; i++)
            DynamicComboRules[i] = new VfxRule(dynamicComboRules[i], i);

        // The static combos' render states map their dynamic combos through this.
        dynamicComboMapping = new ComboConfigMapping(this, isDynamic: true);

        var variableDescriptions = data.GetArray("m_variableDescriptionArray");
        VariableDescriptions = new VfxVariableDescription[variableDescriptions.Count];
        for (var i = 0; i < variableDescriptions.Count; i++)
            VariableDescriptions[i] = new VfxVariableDescription(variableDescriptions[i], i);

        var vsInputSignatureArray = data.GetArray("m_vsInputSignatureArray");
        VsInputSignatures = new VsInputSignature[vsInputSignatureArray.Count];
        for (var i = 0; i < vsInputSignatureArray.Count; i++)
            VsInputSignatures[i] = new VsInputSignature(vsInputSignatureArray[i], i);

        var staticComboData = data.GetArray("m_staticComboData");
        var staticComboIDs = data.GetIntegerArray("m_staticComboIDs");
        var byteCodeData = data.GetArray("m_byteCodeData");
        var attributes = data.GetArray("m_attributes").Select(a => new VfxShaderAttribute(a)).ToArray();
        for (var i = 0; i < staticComboData.Count; i++)
        {
            var staticComboId = staticComboIDs[i];
            StaticComboEntries.Add(staticComboId, new VfxStaticComboVcsEntry
            {
                ParentProgramData = this,
                StaticComboId = staticComboId,
                ResourceData = new(staticComboData[i], attributes, byteCodeData),
            });
        }
    }

    private void SetFileNameDerivedProperties(string fileName)
    {
        var tokens = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (tokens.Length < 4)
            throw new InvalidDataException($"{fileName}: not a <shader>_<platform>_<model>_<stage>.vcs name.");
        VcsProgramType = ComputeVcsProgramType(tokens[^1].ToLowerInvariant());
        VcsPlatformType = tokens[^3].ToLowerInvariant() switch
        {
            "pc" => VcsPlatformType.PC,
            "pcgl" => VcsPlatformType.PCGL,
            "gles" => VcsPlatformType.MOBILE_GLES,
            "vulkan" => VcsPlatformType.VULKAN,
            _ => VcsPlatformType.Undetermined,
        };
        var cutoff = tokens.Length - 3;
        if (VcsPlatformType == VcsPlatformType.VULKAN)
            VcsPlatformType = tokens[^4].ToLowerInvariant() switch
            {
                "android" => VcsPlatformType.ANDROID_VULKAN,
                "ios" => VcsPlatformType.IOS_VULKAN,
                _ => VcsPlatformType.VULKAN,
            };
        if (VcsPlatformType is VcsPlatformType.MOBILE_GLES or VcsPlatformType.ANDROID_VULKAN or VcsPlatformType.IOS_VULKAN)
            cutoff--;
        VcsShaderModelType = tokens[^2].ToLowerInvariant() switch
        {
            "20" => VcsShaderModelType._20,
            "2b" => VcsShaderModelType._2b,
            "30" => VcsShaderModelType._30,
            "31" => VcsShaderModelType._31,
            "40" => VcsShaderModelType._40,
            "41" => VcsShaderModelType._41,
            "50" => VcsShaderModelType._50,
            "60" => VcsShaderModelType._60,
            _ => VcsShaderModelType.Undetermined,
        };
        ShaderName = string.Join('_', tokens[..cutoff]);
    }

    /// <summary>The stage a file name's last token names.</summary>
    public static VcsProgramType ComputeVcsProgramType(string abbrev) => abbrev switch
    {
        "features" => VcsProgramType.Features,
        "vs" => VcsProgramType.VertexShader,
        "ps" => VcsProgramType.PixelShader,
        "psrs" => VcsProgramType.PixelShaderRenderState,
        "gs" => VcsProgramType.GeometryShader,
        "cs" => VcsProgramType.ComputeShader,
        "hs" => VcsProgramType.HullShader,
        "ds" => VcsProgramType.DomainShader,
        "rtx" => VcsProgramType.RaytracingShader,
        "ms" => VcsProgramType.MeshShader,
        _ => VcsProgramType.Undetermined,
    };

    /// <summary>A static combo, decoded on every call (its bytecode decompressed).</summary>
    public VfxStaticComboData GetStaticCombo(long id) => StaticComboEntries[id].Unserialize();

    /// <summary>The combo values of a dynamic combo id.</summary>
    public int[] GetDynamicComboConfig(long dynamicComboId) =>
        (dynamicComboMapping ?? throw new InvalidOperationException("The dynamic combo mapping is not built.")).GetConfigState(dynamicComboId);
}
