using Source2.Compiler.KeyValues;

namespace Source2.Compiler;

/// <summary>A compiled resource's KV3 blocks as KV3 text (the s2c decompile and inspect commands).</summary>
public static class ResourceText
{
    /// <summary>The DATA block as KV3 text, or null with the reason.</summary>
    public static string? DataBlockToKv3(CompiledResource resource, out string reason) => BlockToKv3(resource, "DATA", out reason);

    /// <summary>The first <paramref name="blockType"/> block as KV3 text under its own format, or null with the reason.</summary>
    public static string? BlockToKv3(CompiledResource resource, string blockType, out string reason)
    {
        reason = string.Empty;
        if (resource.Block(blockType) is null)
        {
            reason = $"No {blockType} block in resource.";
            return null;
        }
        if (resource.Kv3(blockType) is not { } root)
        {
            reason = $"The {blockType} block is not KV3.";
            return null;
        }
        return root.ToKV3String(resource.Kv3Format(blockType));
    }
}
