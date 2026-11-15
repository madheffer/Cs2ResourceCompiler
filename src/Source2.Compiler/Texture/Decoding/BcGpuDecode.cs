using System.Buffers.Binary;
using System.IO.Compression;

namespace Source2.Compiler.Textures.Decoding;

/// <summary>A GPU whose BC1-BC5 decode is measured (Texture/Decoding/Gpu).</summary>
public enum GpuVendor
{
    /// <summary>NVIDIA, measured on an RTX 4070 (driver 610.88, Vulkan).</summary>
    Nvidia,
}

/// <summary>
/// BC1, BC3, BC4 and BC5 as a GPU decodes them. CS2 and vrad3 sample these blocks on the GPU, and vendors do
/// not interpolate the endpoints alike, so the decode is the GPU's measured one rather than a formula: every
/// BC1 and BC3 colour endpoint pair and selector per channel (BC1 in its four- and three-colour modes), and
/// every BC4 and BC3-alpha endpoint pair and index, decoded on the GPU (tests' BcVendorDecodeProbe, packed by
/// tools/re/bc_gpu_tables.py). The tables then predicted 4,194,304 random-block texels of each format bit for
/// bit, which is what shows a channel depends only on its own endpoints. Values are exact: BC1/BC3 colour and
/// BC3 alpha are k/255, BC4 (and so BC5) k/65535; the GPU does not round BC4 to 8 bits, and does round BC3's
/// alpha. A texel is RGBA as the GPU's UNORM format returns it: BC1's three-colour selector 3 is transparent
/// black (D3D11's BC1_UNORM, CS2's default renderer, has no alpha-less BC1); BC4 is (r, 0, 0, 1), BC5 (r, g, 0, 1).
/// sRGB formats decode the colour as UNORM, then convert each colour channel through a measured 256-entry table
/// (<see cref="SrgbToLinear"/>; not the sRGB curve, 2 of 256 entries equal it in float), alpha unchanged: BC1, BC3,
/// BC7 and RGBA8, 4,194,304 random texels each (BcSrgbDecodeProbe). D3D11, CS2's default renderer, decodes every
/// one of these formats, UNORM and sRGB, bit for bit as the Vulkan the tables came from (BcD3D11DecodeProbe,
/// 1,048,576 random texels a format). Not measured: AMD (docs/INDEPENDENCE.md).
/// </summary>
public static class BcGpuDecode
{
    private sealed class Tables(byte[] bc1Four, byte[] bc1Three, byte[] bc3Colour, byte[] bc3Alpha, ushort[] bc4, float[] srgb)
    {
        public readonly byte[] Bc1Four = bc1Four, Bc1Three = bc1Three, Bc3Colour = bc3Colour, Bc3Alpha = bc3Alpha;
        public readonly ushort[] Bc4 = bc4;
        public readonly float[] Srgb = srgb;
    }

    private static Tables Of(GpuVendor vendor) => vendor switch { GpuVendor.Nvidia => Nvidia.Value, _ => throw new NotSupportedException($"No measured decode for {vendor}.") };

    /// <summary>An sRGB format's colour channel as the GPU returns it, from the channel's UNORM byte (0-255).</summary>
    public static float SrgbToLinear(int unorm, GpuVendor vendor) => Of(vendor).Srgb[unorm];

    private static readonly Lazy<Tables> Nvidia = new(() => Load("nvidia"));

    /// <summary>The vendor whose decode <see cref="CompiledTexture"/> uses unless told otherwise.</summary>
    public static GpuVendor Default { get; set; } = GpuVendor.Nvidia;

    /// <summary>Whether the format is one this decodes.</summary>
    public static bool Handles(VTexFormat format) => format is VTexFormat.DXT1 or VTexFormat.DXT5 or VTexFormat.ATI1N or VTexFormat.ATI2N;

    /// <summary>
    /// Decode a face stored <paramref name="storedWidth"/> x <paramref name="storedHeight"/> into RGBA floats of
    /// <paramref name="outWidth"/> x <paramref name="outHeight"/> (a display rect may be smaller), rows in order.
    /// </summary>
    /// <exception cref="InvalidDataException">The face holds fewer bytes than its blocks need.</exception>
    public static void Decode(VTexFormat format, ReadOnlySpan<byte> input, int storedWidth, int storedHeight, Span<float> rgba,
                              int outWidth, int outHeight, GpuVendor vendor)
    {
        if (!Handles(format))
            throw new ArgumentException($"{format} is not a BC1-BC5 format.", nameof(format));
        var t = Of(vendor);
        var blockBytes = format is VTexFormat.DXT1 or VTexFormat.ATI1N ? 8 : 16;
        var blocksWide = (storedWidth + 3) / 4;
        var blocksHigh = (storedHeight + 3) / 4;
        if (input.Length < (long)blocksWide * blocksHigh * blockBytes)
            throw new InvalidDataException($"A {storedWidth}x{storedHeight} {format} face needs {(long)blocksWide * blocksHigh * blockBytes} bytes, {input.Length} are stored.");
        if (rgba.Length < outWidth * outHeight * 4)
            throw new ArgumentException("The destination is smaller than the output.", nameof(rgba));
        Span<float> texels = stackalloc float[64];
        for (var by = 0; by * 4 < outHeight; by++)
            for (var bx = 0; bx * 4 < outWidth; bx++)
            {
                var block = input.Slice((by * blocksWide + bx) * blockBytes, blockBytes);
                switch (format)
                {
                    case VTexFormat.DXT1:
                        Colour(block, texels, t.Bc1Four, t.Bc1Three, bc3: false);
                        break;
                    case VTexFormat.DXT5:
                        Colour(block[8..], texels, t.Bc3Colour, t.Bc3Colour, bc3: true);
                        Alpha(block, texels, 3, a => K255(t.Bc3Alpha[a]));
                        break;
                    case VTexFormat.ATI1N:
                        Alpha(block, texels, 0, a => K65535(t.Bc4[a]));
                        for (var i = 0; i < 16; i++)
                            (texels[i * 4 + 1], texels[i * 4 + 2], texels[i * 4 + 3]) = (0f, 0f, 1f);
                        break;
                    default:
                        Alpha(block, texels, 0, a => K65535(t.Bc4[a]));
                        Alpha(block[8..], texels, 1, a => K65535(t.Bc4[a]));
                        for (var i = 0; i < 16; i++)
                            (texels[i * 4 + 2], texels[i * 4 + 3]) = (0f, 1f);
                        break;
                }
                for (var y = 0; y < 4 && by * 4 + y < outHeight; y++)
                    for (var x = 0; x < 4 && bx * 4 + x < outWidth; x++)
                        texels.Slice((y * 4 + x) * 4, 4).CopyTo(rgba[((((by * 4 + y) * outWidth) + bx * 4 + x) * 4)..]);
            }
    }

    // The measured value: k / 255 or k / 65535 in double, then float, which is how the tables were proven equal to
    // the GPU's float32 results (tools/re/bc_gpu_tables.py refuses any value it would change).
    private static float K255(int k) => (float)(k / 255.0);
    private static float K65535(int k) => (float)(k / 65535.0);

    // Table index: channel R (32 x 32 pairs), G (64 x 64), B (32 x 32), each pair x 4 selectors.
    private static int ColourIndex(int channel, int a, int b, int selector)
    {
        var start = channel switch { 0 => 0, 1 => 32 * 32 * 4, _ => 32 * 32 * 4 + 64 * 64 * 4 };
        var bits = channel == 1 ? 6 : 5;
        return start + ((a << bits) + b) * 4 + selector;
    }

    private static void Colour(ReadOnlySpan<byte> block, Span<float> texels, byte[] four, byte[] three, bool bc3)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        var selectors = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        // BC3's colour block always has four colours; BC1's has three plus transparent black when c0 <= c1.
        var fourColour = bc3 || c0 > c1;
        var table = fourColour ? four : three;
        int r0 = c0 >> 11, g0 = (c0 >> 5) & 63, b0 = c0 & 31, r1 = c1 >> 11, g1 = (c1 >> 5) & 63, b1 = c1 & 31;
        for (var i = 0; i < 16; i++)
        {
            var s = (int)(selectors >> (2 * i)) & 3;
            texels[i * 4] = K255(table[ColourIndex(0, r0, r1, s)]);
            texels[i * 4 + 1] = K255(table[ColourIndex(1, g0, g1, s)]);
            texels[i * 4 + 2] = K255(table[ColourIndex(2, b0, b1, s)]);
            texels[i * 4 + 3] = !fourColour && s == 3 ? 0f : 1f;
        }
    }

    // A BC4-layout block (two endpoints, 16 three-bit indices) into channel `channel` through `value(entry)`.
    private static void Alpha(ReadOnlySpan<byte> block, Span<float> texels, int channel, Func<int, float> value)
    {
        ulong indices = 0;
        for (var k = 0; k < 6; k++)
            indices |= (ulong)block[2 + k] << (8 * k);
        var pair = (block[0] * 256 + block[1]) * 8;
        for (var i = 0; i < 16; i++)
            texels[i * 4 + channel] = value(pair + (int)((indices >> (3 * i)) & 7));
    }

    private static Tables Load(string vendor)
    {
        static byte[] Read(string name)
        {
            var asm = typeof(BcGpuDecode).Assembly;
            using var s = asm.GetManifestResourceStream($"Source2.Compiler.BcGpu.{name}.bin.gz")
                ?? throw new InvalidOperationException($"The {name} table is not embedded.");
            using var z = new GZipStream(s, CompressionMode.Decompress);
            using var m = new MemoryStream();
            z.CopyTo(m);
            return m.ToArray();
        }
        var bc4Bytes = Read($"{vendor}_bc4");
        var bc4 = new ushort[bc4Bytes.Length / 2];
        for (var i = 0; i < bc4.Length; i++)
            bc4[i] = BinaryPrimitives.ReadUInt16LittleEndian(bc4Bytes.AsSpan(2 * i));
        var srgbBytes = Read($"{vendor}_srgb");
        var srgb = new float[256];
        for (var i = 0; i < 256; i++)
            srgb[i] = BinaryPrimitives.ReadSingleLittleEndian(srgbBytes.AsSpan(4 * i));
        return new Tables(Read($"{vendor}_bc1_four"), Read($"{vendor}_bc1_three"), Read($"{vendor}_bc3_colour"), Read($"{vendor}_bc3_alpha"), bc4, srgb);
    }
}
