using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>Textures for the CPU side of the bake (BakeScene's materials and the sky).</summary>
public static class BakeTextures
{
    /// <summary>A texture's mip 0 (face <paramref name="face"/> of a cubemap) as linear floats; 8-bit textures are taken as sRGB.</summary>
    public static BakeTexture? Load(byte[]? bytes, int face)
    {
        if (bytes is null)
            return null;
        var tex = Source2.Compiler.Textures.CompiledTexture.Read(bytes);
        using var bmp = tex.GenerateBitmap(0, (Source2.Compiler.Textures.CubemapFace)face, 0, Source2.Compiler.Textures.TextureCodec.None);
        var w = bmp.Width;
        var h = bmp.Height;
        var texels = new Vector4[w * h];
        if (bmp.ColorType is SkiaSharp.SKColorType.RgbaF32)
        {
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bmp.GetPixelSpan());
            for (var i = 0; i < texels.Length; i++)
                texels[i] = new Vector4(span[i * 4], span[i * 4 + 1], span[i * 4 + 2], span[i * 4 + 3]);
        }
        else if (bmp.ColorType is SkiaSharp.SKColorType.RgbaF16)
        {
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bmp.GetPixelSpan());
            for (var i = 0; i < texels.Length; i++)
                texels[i] = new Vector4((float)span[i * 4], (float)span[i * 4 + 1], (float)span[i * 4 + 2], (float)span[i * 4 + 3]);
        }
        else
        {
            // 8-bit: the bitmap's own byte order (CopyTo between colour types can fail and leave zeros).
            using var rgba = bmp.ColorType is SkiaSharp.SKColorType.Bgra8888 or SkiaSharp.SKColorType.Rgba8888 ? null : bmp.Copy(SkiaSharp.SKColorType.Rgba8888);
            var src = rgba ?? bmp;
            var bytes8 = src.GetPixelSpan().ToArray();
            if (src.ColorType == SkiaSharp.SKColorType.Bgra8888)
                for (var i = 0; i < bytes8.Length; i += 4)
                    (bytes8[i], bytes8[i + 2]) = (bytes8[i + 2], bytes8[i]);
            return BakeTexture.FromRgba8(w, h, bytes8, true);
        }
        return new BakeTexture(w, h, texels);
    }
}
