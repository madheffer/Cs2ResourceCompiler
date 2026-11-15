// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/VfxStaticComboData.cs: the resource (KV3) layout's constructor.
using Source2.Compiler.KeyValues;
using ValveKeyValue;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// One static combo of a program (<c>m_staticComboData[i]</c>): its variable write sequences, attributes,
/// per dynamic combo render states and the bytecode block they index.
/// </summary>
public sealed class VfxStaticComboData
{
    /// <summary>The program file.</summary>
    public VfxProgramData ParentProgramData { get; }

    /// <summary>The combo's id.</summary>
    public long StaticComboId { get; }

    /// <summary><c>m_allVars</c>: every variable the combo uses.</summary>
    public VfxVariableIndexArray AllVariables { get; }

    /// <summary>The attributes <c>m_attribIdx</c> picks from the file's <c>m_attributes</c>.</summary>
    public VfxShaderAttribute[] Attributes { get; }

    /// <summary><c>m_vsInputSignatureIndexArray</c>.</summary>
    public int[] VsInputSignatureIndices { get; }

    /// <summary>Each dynamic combo's write sequence (<c>m_dynamicComboVarsRef</c> over <c>m_dynamicComboVars</c>).</summary>
    public VfxVariableIndexArray[] DynamicComboVariables { get; }

    /// <summary>The low byte of each <c>m_constantBufferBindingArray</c> entry.</summary>
    public byte[] ConstantBufferBindingSlots { get; }

    /// <summary>The second byte of each <c>m_constantBufferBindingArray</c> entry.</summary>
    public byte[] ConstantBufferBindingFlags { get; }

    /// <summary><c>m_nConstantBufferSize</c>.</summary>
    public int ConstantBufferSize { get; }

    /// <summary><c>m_bStaticCB</c>.</summary>
    public bool StaticCB { get; }

    /// <summary><c>m_bGlobalsBDA</c>.</summary>
    public bool GlobalsBDA { get; }

    /// <summary>The bytecode entries, by <c>m_byteCodeIndex</c> (null where no dynamic combo names one).</summary>
    public VfxShaderFile[] ShaderFiles { get; } = [];

    /// <summary>Each dynamic combo's render state, in stored order.</summary>
    public VfxRenderStateInfo[] DynamicComboRenderStates { get; }

    // The resource layout stores only the dynamic combos that exist, by position; their ids are sparse.
    private readonly Dictionary<long, int> dynamicComboIdToIndex;

    /// <summary>The stored position of a dynamic combo id, or -1.</summary>
    public int GetDynamicComboIndex(long dynamicComboId) => dynamicComboIdToIndex.TryGetValue(dynamicComboId, out var index) ? index : -1;

    /// <summary>Decode a combo.</summary>
    public VfxStaticComboData(KVObject data, long staticComboId, VfxShaderAttribute[] attributes, IReadOnlyList<KVObject> byteCodeDataArray, VfxProgramData programData)
    {
        ParentProgramData = programData;
        StaticComboId = staticComboId;

        var dynamicComboIds = data.GetIntegerArray("m_dynamicComboIDs");
        var dynamicComboRenderState = data.GetArray("m_dynamicComboRenderState");
        var byteCodeIndex = data.GetArray<int>("m_byteCodeIndex")!;

        DynamicComboRenderStates = new VfxRenderStateInfo[dynamicComboRenderState.Count];
        dynamicComboIdToIndex = new Dictionary<long, int>(DynamicComboRenderStates.Length);
        for (var i = 0; i < DynamicComboRenderStates.Length; i++)
        {
            var id = dynamicComboIds.Length > 0 ? dynamicComboIds[i] : i;
            DynamicComboRenderStates[i] = programData.VcsProgramType switch
            {
                VcsProgramType.PixelShader or VcsProgramType.PixelShaderRenderState
                    => new VfxRenderStateInfoPixelShader(id, byteCodeIndex[i], -1, dynamicComboRenderState[i], programData.VcsVersion),
                _ => new VfxRenderStateInfo(id, byteCodeIndex[i], -1),
            };
            dynamicComboIdToIndex[id] = i;
        }

        var byteCodeDataIdx = data.GetInt32Property("m_nByteCodeDataIdx");
        if (byteCodeDataIdx >= 0)
        {
            var byteCodeData = byteCodeDataArray[byteCodeDataIdx];
            var blockOffset = byteCodeData.GetInt32Property("m_nOffs");
            var blockSize = byteCodeData.GetInt32Property("m_nSize");
            var block = VfxStaticComboVcsEntry.Uncompress(programData.Tail, blockOffset, out var consumed);
            if (consumed != blockSize)
                throw new InvalidDataException($"Static combo {staticComboId}: its bytecode block is {consumed} bytes, m_nSize {blockSize}.");

            var hashes = byteCodeData.GetArray("m_hash");
            var offsets = byteCodeData.GetArray<uint>("m_offs")!;
            if (offsets.Length != hashes.Count + 1)
                throw new InvalidDataException($"Static combo {staticComboId}: {offsets.Length} offsets for {hashes.Count} bytecode entries.");

            ShaderFiles = new VfxShaderFile[hashes.Count];
            foreach (var i in byteCodeIndex)
            {
                if (i == -1)
                    continue;
                var hash = new Guid(hashes[i].GetArray<byte>("m_nHashChar")!);
                var start = offsets[i];
                var end = offsets[i + 1];
                if (end < start || end > block.Length)
                    throw new InvalidDataException($"Static combo {staticComboId}: bytecode entry {i} spans {start}..{end} of {block.Length}.");
                var entry = block.AsSpan((int)start, (int)(end - start));
                ShaderFiles[i] = programData.VcsPlatformType switch
                {
                    VcsPlatformType.VULKAN => new VfxShaderFileVulkan(entry, i, hash, this),
                    VcsPlatformType.PC => new VfxShaderFileDXBC(entry, i, hash, this),
                    _ => throw new NotSupportedException($"Bytecode of platform {programData.VcsPlatformType} is not read."),
                };
            }
        }

        var dynamicComboVars = data.GetArray<uint>("m_dynamicComboVars");
        var dynamicComboVarsRef = data.GetArray("m_dynamicComboVarsRef");
        DynamicComboVariables = new VfxVariableIndexArray[dynamicComboVarsRef.Count];
        for (var i = 0; i < dynamicComboVarsRef.Count; i++)
        {
            var reference = dynamicComboVarsRef[i];
            var start = reference.GetInt32Property("m_indexAndRegisterOffsetStart");
            var count = reference.GetInt32Property("m_indexAndRegisterOffsetCount");
            if (start <= 0)
                start = 0; // psrs files store -1073741824
            DynamicComboVariables[i] = new VfxVariableIndexArray(dynamicComboVars.AsSpan(start, count),
                reference.GetInt32Property("m_nFirstRenderStateElement"), reference.GetInt32Property("m_nFirstConstantElement"), i);
        }

        var constantBufferBindingArray = data.GetArray<int>("m_constantBufferBindingArray")!;
        ConstantBufferBindingSlots = [.. constantBufferBindingArray.Select(i => (byte)(i >> 0))];
        ConstantBufferBindingFlags = [.. constantBufferBindingArray.Select(i => (byte)(i >> 8))];
        ConstantBufferSize = data.GetInt32Property("m_nConstantBufferSize");
        StaticCB = data.GetUInt32Property("m_bStaticCB") != 0u;
        GlobalsBDA = data.GetUInt32Property("m_bGlobalsBDA") != 0u;

        var allVars = data.GetSubCollection("m_allVars");
        AllVariables = new VfxVariableIndexArray(allVars.GetArray<uint>("m_indexAndRegisterOffsetArray"),
            allVars.GetInt32Property("m_nFirstRenderStateElement"), allVars.GetInt32Property("m_nFirstConstantElement"), -1);

        VsInputSignatureIndices = [.. data.GetIntegerArray("m_vsInputSignatureIndexArray").Select(i => (int)i)];
        Attributes = [.. data.GetIntegerArray("m_attribIdx").Select(i => attributes[i])];
    }
}
