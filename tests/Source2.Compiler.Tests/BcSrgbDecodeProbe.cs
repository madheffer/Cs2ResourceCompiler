using Silk.NET.Vulkan;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The GPU's sRGB block decode against its UNORM one: random BC1, BC3 and BC7 blocks and RGBA8 texels decoded
/// in both formats. If every sRGB colour channel is one function of the UNORM value (the colour decoded in 8 bits,
/// then converted), that function, 256 entries, is the whole sRGB rule; alpha must be the UNORM alpha. Writes the
/// measured table (float32, by UNORM byte) to <c>&lt;out&gt;/&lt;vendor&gt;_srgb.f32</c> and reports how many texels it
/// predicts, and how it compares with the sRGB curve in double.
/// <c>BCSRGB=&lt;out dir&gt;[|random blocks]</c>.
/// </summary>
public sealed class BcSrgbDecodeProbe(ITestOutputHelper output)
{
    [Fact]
    public void SrgbIsUnormThenATable()
    {
        if (Environment.GetEnvironmentVariable("BCSRGB") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var outDir = parts[0];
        var count = parts.Length > 1 ? int.Parse(parts[1]) : 65536;
        Directory.CreateDirectory(outDir);
        using var gpu = new GpuDevice();
        var vendor = gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "nvidia" : "other";
        output.WriteLine($"GPU: {gpu.Name}");
        var table = new float[256];
        Array.Fill(table, float.NaN);
        var rng = new Random(0x5e6b);
        var cases = new (string Name, Format Unorm, Format Srgb, int BlockBytes)[]
        {
            ("BC1", Format.BC1RgbaUnormBlock, Format.BC1RgbaSrgbBlock, 8),
            ("BC3", Format.BC3UnormBlock, Format.BC3SrgbBlock, 16),
            ("BC7", Format.BC7UnormBlock, Format.BC7SrgbBlock, 16),
            ("RGBA8", Format.R8G8B8A8Unorm, Format.R8G8B8A8Srgb, 64),   // 16 texels x 4 bytes, uncompressed
        };
        const int side = 1024;
        var perImage = side / 4 * (side / 4);
        foreach (var (name, unorm, srgb, size) in cases)
        {
            long texels = 0, consistent = 0, conflicts = 0, alphaSame = 0;
            for (var done = 0; done < count; done += perImage)
            {
                var data = new byte[perImage * size];
                rng.NextBytes(data);
                if (name == "RGBA8")
                    data = Retile(data, side);   // the bytes as a linear image, same texels as block order below
                var u = TextureDecodeGpuProbe.GpuDecode(gpu, unorm, data, side, side);
                var s = TextureDecodeGpuProbe.GpuDecode(gpu, srgb, data, side, side);
                for (var i = 0; i < u.Length; i += 4)
                {
                    texels++;
                    var ok = true;
                    for (var c = 0; c < 3; c++)
                    {
                        var k = u[i + c] * 255.0;
                        var b = (int)Math.Round(k);
                        if (Math.Abs(k - b) > 1e-4)
                        {
                            ok = false;   // a UNORM value off the 8-bit grid: not "8 bits, then convert"
                            continue;
                        }
                        if (float.IsNaN(table[b]))
                            table[b] = s[i + c];
                        else if (BitConverter.SingleToInt32Bits(table[b]) != BitConverter.SingleToInt32Bits(s[i + c]))
                        {
                            ok = false;
                            if (conflicts++ < 4)
                                output.WriteLine($"  {name}: UNORM {b} gives sRGB {s[i + c]:R} here and {table[b]:R} before");
                        }
                    }
                    if (ok) consistent++;
                    if (BitConverter.SingleToInt32Bits(u[i + 3]) == BitConverter.SingleToInt32Bits(s[i + 3])) alphaSame++;
                }
            }
            output.WriteLine($"{name}: {consistent} of {texels} texels' colour is table[UNORM byte]; alpha unchanged on {alphaSame}");
        }
        var measured = table.Count(v => !float.IsNaN(v));
        var curveSame = Enumerable.Range(0, 256).Count(b => !float.IsNaN(table[b]) && table[b] == (float)Curve(b / 255.0));
        output.WriteLine($"table: {measured} of 256 entries measured; {curveSame} equal float(sRGB curve in double)");
        for (var b = 0; b < 256 && curveSame < measured; b++)
            if (!float.IsNaN(table[b]) && table[b] != (float)Curve(b / 255.0) && b % 1 == 0)
            {
                output.WriteLine($"  {b}: gpu {table[b]:R} curve {(float)Curve(b / 255.0):R}");
                if (--measured < 0) break;
            }
        File.WriteAllBytes(Path.Combine(outDir, $"{vendor}_srgb.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(table.AsSpan()).ToArray());
    }

    private static double Curve(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    // Random bytes stay random as a linear RGBA8 image; nothing to reorder, but keep the size a full image.
    private static byte[] Retile(byte[] data, int side) => data.AsSpan(0, side * side * 4).ToArray();
}
