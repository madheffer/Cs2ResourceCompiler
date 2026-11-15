// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/VfxStaticComboVcsEntry.cs and ZstdDictionary.cs: the resource (KV3) layout's path.
using System.Buffers.Binary;
using ValveKeyValue;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>One static combo of a program file, decoded on demand.</summary>
public sealed class VfxStaticComboVcsEntry
{
    /// <summary>The program file the combo belongs to.</summary>
    public required VfxProgramData ParentProgramData { get; init; }

    /// <summary>The combo's id.</summary>
    public long StaticComboId { get; init; }

    /// <summary>The combo's <c>m_staticComboData</c> entry, every attribute of the file, and <c>m_byteCodeData</c>.</summary>
    public sealed record ResourceEntry(KVObject ComboData, VfxShaderAttribute[] AllAttributes, IReadOnlyList<KVObject> ByteCodeDescArray);

    /// <summary>What the combo is decoded from.</summary>
    public required ResourceEntry ResourceData { get; init; }

    /// <summary>The combo: its variables, attributes, render states and bytecode.</summary>
    public VfxStaticComboData Unserialize() => new(ResourceData.ComboData, StaticComboId, ResourceData.AllAttributes,
        ResourceData.ByteCodeDescArray, ParentProgramData);

    /// <summary>
    /// A bytecode block of the file's tail at <paramref name="offset"/>, decompressed: <c>i32 -compressionType;
    /// i32 uncompressedSize; i32 compressedSize;</c> then the zstd frame. Type 3 uses dictionary 0x2bc2fa87 and
    /// type 5 dictionary 0x255df362 (materialsystem2.dll's two, <see cref="ZstdDictionary"/>); CS2's files use no
    /// other type.
    /// </summary>
    /// <param name="tail">The file's bytes after its declared size.</param>
    /// <param name="offset">The block's <c>m_nOffs</c>.</param>
    /// <param name="consumed">The block's stored size (12-byte header plus the frame).</param>
    /// <exception cref="InvalidDataException">An unknown compression type, or a frame that does not decode to its size.</exception>
    internal static byte[] Uncompress(byte[] tail, int offset, out int consumed)
    {
        if (offset < 0 || offset > tail.Length - 12)
            throw new InvalidDataException($"A bytecode block at {offset} lies outside the {tail.Length}-byte tail.");
        var span = tail.AsSpan(offset);
        var compressionType = -BinaryPrimitives.ReadInt32LittleEndian(span);
        var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        if (compressedSize < 0 || compressedSize > span.Length - 12 || uncompressedSize < 0)
            throw new InvalidDataException($"A bytecode block at {offset} claims {compressedSize} bytes to {uncompressedSize}.");
        consumed = 12 + compressedSize;
        var dictionary = compressionType switch
        {
            3 => ZstdDictionary.Get(0x2bc2fa87),
            5 => ZstdDictionary.Get(0x255df362),
            _ => throw new InvalidDataException($"Bytecode compression type {compressionType} is not one CS2's shader files use."),
        };
        using var decompressor = new ZstdSharp.Decompressor();
        decompressor.LoadDictionary(dictionary);
        var output = new byte[uncompressedSize];
        if (!decompressor.TryUnwrap(span.Slice(12, compressedSize), output, out var written) || written != uncompressedSize)
            throw new InvalidDataException($"A bytecode block at {offset} did not decompress to its {uncompressedSize} bytes.");
        return output;
    }
}

/// <summary>
/// The two zstd dictionaries CS2's shader bytecode is compressed with, as materialsystem2.dll holds them (the
/// 64 KB buffers it hands V_ZSTD_createDDict_byReference: magic 0xec30a437, then the id). Taken from CS2 1.41.9.7
/// with tools/re/vfx_zstd_dicts.py, which also checks them against an installed build.
/// </summary>
public static class ZstdDictionary
{
    private static readonly Dictionary<uint, byte[]> Loaded = [];

    /// <summary>The dictionary with id <paramref name="id"/>.</summary>
    /// <exception cref="InvalidDataException">No such dictionary is embedded.</exception>
    public static byte[] Get(uint id)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(id, out var cached))
                return cached;
            using var stream = typeof(ZstdDictionary).Assembly.GetManifestResourceStream($"vfx_zstd_{id:x8}.bin")
                ?? throw new InvalidDataException($"No zstd dictionary {id:x8} is embedded.");
            var bytes = new byte[0x10000];
            stream.ReadExactly(bytes);
            return Loaded[id] = bytes;
        }
    }
}
