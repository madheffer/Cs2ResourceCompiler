using System.IO.Hashing;
using ValveResourceFormat;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A map's cs_script sources (.js) back from their compiled .vjs_c.
///
/// <para>A PanoramaScript resource of version 4 or later stores its source as plain text: the DATA
/// block is the file as the compile read it (VRF's Panorama.IsPlaintext). Measured 10-30 on 227
/// workshop scripts (77 addons): the CRC32 of every DATA block equals the CRC the resource's RED2
/// records for its .js input, so the text is the source byte for byte, not a rewrite. Scripts sit in
/// the addon's package (scripts/...), never in a map's own .vpk: 0 of those 77 addons' maps hold one.</para>
/// </summary>
public static class ScriptSources
{
    /// <summary>The source a compiled script was made from, at the content path its RED2 names; null
    /// when the resource keeps no plain text (older versions) or the text is not the source the
    /// compile recorded (its CRC differs).</summary>
    public static (string Path, byte[] Source)? Recover(byte[] compiled)
    {
        // VRF types a script by its extension only (no special dependency names it).
        using var resource = new Resource { FileName = "script.vjs_c" };
        resource.Read(new MemoryStream(compiled));
        if (resource.ResourceType is not (ResourceType.PanoramaScript or ResourceType.PanoramaTypescript)
            || resource.DataBlock is not ValveResourceFormat.ResourceTypes.Panorama panorama
            || resource.EditInfo is not ValveResourceFormat.Blocks.ResourceEditInfo info)
            return null;
        // The first input is the source file; a second, scripts/<name>.vjs with CRC 0, is optional.
        var source = info.InputDependencies.FirstOrDefault(d => d.FileCRC != 0
                                                                && (d.ContentRelativeFilename.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                                                                    || d.ContentRelativeFilename.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)));
        if (source is null || Crc32.HashToUInt32(panorama.Data) != source.FileCRC)
            return null;
        return (source.ContentRelativeFilename, panorama.Data);
    }
}
