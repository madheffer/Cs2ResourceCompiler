// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/Blocks/ShaderFile/: the resource (KV3) layout's bytecode entries.
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>One compiled shader of a static combo: its bytecode and MD5.</summary>
public abstract class VfxShaderFile
{
    /// <summary>The combo it belongs to.</summary>
    public VfxStaticComboData ParentCombo { get; }

    /// <summary>The bytecode's kind (<c>VULKAN</c>, <c>DXBC</c>).</summary>
    public abstract string SourceType { get; }

    /// <summary>The entry's index in <c>m_byteCodeData</c>'s list.</summary>
    public int ShaderFileId { get; }

    /// <summary>The entry's stored size.</summary>
    public int Size { get; protected set; }

    /// <summary>The bytecode (SPIR-V, or a DXBC container).</summary>
    public byte[] Bytecode { get; protected set; } = [];

    /// <summary>The entry's <c>m_hash</c>.</summary>
    public Guid HashMD5 { get; protected set; }

    /// <summary>An entry of a combo.</summary>
    protected VfxShaderFile(int shaderFileId, VfxStaticComboData parent)
    {
        ParentCombo = parent;
        ShaderFileId = shaderFileId;
    }

    /// <summary>Whether the entry stores nothing.</summary>
    public bool IsEmpty() => Size == 0;
}

/// <summary>A DXBC entry (the pc platform): the bytes as stored.</summary>
public sealed class VfxShaderFileDXBC : VfxShaderFile
{
    /// <inheritdoc/>
    public override string SourceType => "DXBC";

    internal VfxShaderFileDXBC(ReadOnlySpan<byte> entry, int shaderFileId, Guid hash, VfxStaticComboData parent) : base(shaderFileId, parent)
    {
        Size = entry.Length;
        Bytecode = entry.ToArray();
        HashMD5 = hash;
    }
}

/// <summary>
/// A Vulkan entry: <c>i32 version (2..6); i32 bytecodeSize; bytecode;</c> then a metadata block of which the
/// attribute map (<c>i32 size; bytes</c>: per input location, the D3D usage in the high nibble and the usage
/// index in the low) is read.
/// </summary>
public sealed class VfxShaderFileVulkan : VfxShaderFile
{
    /// <inheritdoc/>
    public override string SourceType => "VULKAN";

    /// <summary>The entry's version.</summary>
    public int Version { get; }

    /// <summary>The SPIR-V's size.</summary>
    public int BytecodeSize { get; }

    /// <summary>The vertex input attribute map, or null.</summary>
    public byte[]? AttribMap { get; }

    internal VfxShaderFileVulkan(ReadOnlySpan<byte> entry, int shaderFileId, Guid hash, VfxStaticComboData parent) : base(shaderFileId, parent)
    {
        HashMD5 = hash;
        Size = entry.Length;
        if (entry.Length < 8)
            throw new InvalidDataException($"A {entry.Length}-byte Vulkan shader entry.");
        Version = BinaryPrimitives.ReadInt32LittleEndian(entry);
        if (Version is < 2 or > 6)
            throw new InvalidDataException($"Vulkan shader entry version {Version}; 2 to 6 are read.");
        BytecodeSize = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
        if (BytecodeSize < 0 || BytecodeSize > entry.Length - 8)
            throw new InvalidDataException($"A Vulkan shader entry claims {BytecodeSize} bytes of SPIR-V in {entry.Length}.");
        Bytecode = entry.Slice(8, BytecodeSize).ToArray();
        var at = 8 + BytecodeSize;
        if (at + 4 <= entry.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(entry[at..]);
            if (size > 0 && size <= entry.Length - at - 4)
                AttribMap = entry.Slice(at + 4, size).ToArray();
        }
    }

    /// <summary>The D3D semantic and index of input <paramref name="location"/>, from the attribute map.</summary>
    public bool TryGetInputSemantic(uint location, out string semanticName, out int semanticIndex)
    {
        if (AttribMap is null || location >= (uint)AttribMap.Length)
        {
            semanticName = string.Empty;
            semanticIndex = 0;
            return false;
        }
        var slot = AttribMap[location];
        semanticName = ((D3DVertexUsage)(slot >> 4)).ToD3DSemanticName();
        semanticIndex = slot & 0xF;
        return semanticName.Length > 0;
    }
}

/// <summary>One write of a combo's variable sequence: a variable (and its layout set) and its register.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VfxVariableIndexData
{
    /// <summary>The low 16 bits: a register offset, or binding slot and control for a render state.</summary>
    public short RegisterOffset { get; init; }

    /// <summary>The high 16 bits: variable index and layout set.</summary>
    public short PackedIndex { get; init; }

    /// <summary>The index into <see cref="VfxProgramData.VariableDescriptions"/>.</summary>
    public int VariableIndex => PackedIndex & 0xFFF;

    /// <summary>The descriptor set (layout set) id.</summary>
    public int LayoutSet => PackedIndex >> 12;

    /// <summary>A render state's binding slot.</summary>
    public int BindingSlot => RegisterOffset & 0xFF;

    /// <summary>A render state's control byte.</summary>
    public int Control => (RegisterOffset >> 8) & 0xFF;
}

/// <summary>A combo's variable write sequence: evaluated variables, then render states, then constants.</summary>
public sealed class VfxVariableIndexArray
{
    /// <summary>The sequence's index (-1 for a combo's whole list).</summary>
    public int Index { get; }

    /// <summary>Where the render state writes start.</summary>
    public int FirstRenderStateElement { get; }

    /// <summary>Where the constant writes start.</summary>
    public int FirstConstantElement { get; }

    /// <summary>Every write.</summary>
    public VfxVariableIndexData[] Fields { get; }

    /// <summary>The evaluated variables.</summary>
    public IReadOnlyList<VfxVariableIndexData> Evaluated => Fields[..FirstRenderStateElement];

    /// <summary>The render state writes.</summary>
    public IReadOnlyList<VfxVariableIndexData> RenderState => Fields[FirstRenderStateElement..FirstConstantElement];

    /// <summary>The constant writes.</summary>
    public IReadOnlyList<VfxVariableIndexData> Constants => Fields[FirstConstantElement..];

    /// <summary>A sequence from its packed entries (<c>m_indexAndRegisterOffsetArray</c>).</summary>
    public VfxVariableIndexArray(ReadOnlySpan<uint> fields, int firstRenderStateElement, int firstConstantElement, int index)
    {
        Index = index;
        Fields = MemoryMarshal.Cast<uint, VfxVariableIndexData>(fields).ToArray();
        FirstRenderStateElement = firstRenderStateElement;
        FirstConstantElement = firstConstantElement;
    }
}
