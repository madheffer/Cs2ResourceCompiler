using Source2.Compiler.Textures;

namespace Source2.Compiler.Tests;

/// <summary>Mip helpers over <see cref="CompiledTexture"/> in the form the older texture tests call them.</summary>
internal static class TestTextureExtensions
{
    /// <summary>A mip's uncompressed size, every face and slice included (<see cref="CompiledTexture.MipBufferSize"/>).</summary>
    public static int CalculateBufferSizeForMipLevel(this CompiledTexture texture, uint mipLevel) => texture.MipBufferSize(mipLevel);

    /// <summary>A mip's bytes, decompressed, into <paramref name="output"/>.</summary>
    public static void ReadTextureMipLevel(this CompiledTexture texture, Span<byte> output, uint mipLevel) => texture.ReadMip(mipLevel).CopyTo(output);

    /// <summary>Every mip, smallest first: level, size, depth (a cube's six faces counted) and uncompressed bytes.</summary>
    public static IEnumerable<(uint Level, int Width, int Height, int Depth, int BufferSize)> GetEveryMipLevelMetrics(this CompiledTexture texture)
    {
        for (var i = texture.NumMipLevels - 1; i >= 0; i--)
        {
            var (width, height, depth) = texture.MipSize((uint)i);
            yield return ((uint)i, width, height, depth, texture.MipBufferSize((uint)i));
        }
    }

    /// <summary>The codec the texture's edit info names (<see cref="CompiledTexture.CodecFromEditInfo"/>).</summary>
    public static TextureCodec RetrieveCodecFromResourceEditInfo(this CompiledTexture texture) => texture.CodecFromEditInfo();
}
