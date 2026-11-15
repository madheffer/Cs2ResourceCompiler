using System.Buffers.Binary;
using Silk.NET.Vulkan;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The GPU's BC1, BC3 and BC4 decode, measured exhaustively, then checked on random blocks.
/// <list type="bullet">
/// <item>BC1 colour, per channel (R and B 5 bits, G 6 bits), every endpoint pair and selector, in the
/// four-colour mode (colour0 &gt; colour1 as 16-bit values) and the three-colour mode; BC3's colour block
/// (four colours whatever the order) likewise. A channel's pair is reached with the other channels set to
/// fix the mode; that a channel does not depend on the others is what the random check then tests.</item>
/// <item>BC4 (BC3's alpha, BC5's two channels), every endpoint pair and index.</item>
/// </list>
/// Writes the tables as little-endian float32 to <c>&lt;out&gt;/&lt;vendor&gt;_*.f32</c> with a manifest, and reports
/// how many texels of random BC1, BC3, BC4 and BC5 blocks the tables predict exactly.
/// <c>BCVENDOR=&lt;out dir&gt;[|random blocks]</c>.
/// </summary>
public sealed unsafe class BcVendorDecodeProbe(ITestOutputHelper output)
{
    private const int Side = 1024;              // texels; 256 x 256 blocks per image
    private const int BlocksPerImage = Side / 4 * (Side / 4);

    [Fact]
    public void MeasureAndCheck()
    {
        if (Environment.GetEnvironmentVariable("BCVENDOR") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var outDir = parts[0];
        var randomBlocks = parts.Length > 1 ? int.Parse(parts[1]) : BlocksPerImage;
        Directory.CreateDirectory(outDir);
        using var gpu = new GpuDevice();
        var vendor = gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "nvidia"
            : gpu.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpu.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "amd" : "other";
        output.WriteLine($"GPU: {gpu.Name} -> {vendor}");

        // Colour tables: [mode][channel][a][b][selector], channel 0 R (32), 1 G (64), 2 B (32).
        var bc1Four = ColourTable(gpu, Format.BC1RgbaUnormBlock, fourColour: true, bc3: false);
        var bc1Three = ColourTable(gpu, Format.BC1RgbaUnormBlock, fourColour: false, bc3: false);
        var bc3Colour = ColourTable(gpu, Format.BC3UnormBlock, fourColour: true, bc3: true);
        var bc4 = AlphaTable(gpu, Format.BC4UnormBlock);
        // BC3's alpha block has BC4's layout but is not decoded as BC4 is (NVIDIA rounds it to 8 bits): its own table.
        var bc3Alpha = AlphaTable(gpu, Format.BC3UnormBlock);
        foreach (var (name, data) in new[] { ("bc1_four", bc1Four), ("bc1_three", bc1Three), ("bc3_colour", bc3Colour), ("bc3_alpha", bc3Alpha), ("bc4", bc4) })
            File.WriteAllBytes(Path.Combine(outDir, $"{vendor}_{name}.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()).ToArray());
        File.WriteAllText(Path.Combine(outDir, $"{vendor}_manifest.txt"),
            $"gpu {gpu.Name}\nlayout colour: [channel R32,G64,B32 concatenated][a][b][selector 4] float32; bc3_alpha and bc4: [a 256][b 256][index 8] float32\n");

        var rng = new Random(0x5eed);
        Check(gpu, Format.BC1RgbaUnormBlock, 8, randomBlocks, rng, (b, t) => PredictBc1(b, t, bc1Four, bc1Three), "BC1");
        Check(gpu, Format.BC3UnormBlock, 16, randomBlocks, rng, (b, t) => PredictBc3(b, t, bc3Colour, bc3Alpha), "BC3");
        Check(gpu, Format.BC4UnormBlock, 8, randomBlocks, rng, (b, t) => [Bc4(b, 0, t, bc4), 0, 0, 1], "BC4");
        Check(gpu, Format.BC5UnormBlock, 16, randomBlocks, rng, (b, t) => [Bc4(b, 0, t, bc4), Bc4(b, 8, t, bc4), 0, 1], "BC5");
    }

    private static readonly int[] Bits = [5, 6, 5];
    private static int ChannelBase(int c) => c switch { 0 => 0, 1 => 32 * 32 * 4, _ => 32 * 32 * 4 + 64 * 64 * 4 };
    private static int ColourIndex(int c, int a, int b, int s) => ChannelBase(c) + ((a << Bits[c]) + b) * 4 + s;

    private static ushort Pack(int r, int g, int b) => (ushort)((r << 11) | (g << 5) | b);

    // One block per (channel, a, b): selectors 0..3 repeated over the 16 texels.
    private float[] ColourTable(GpuDevice gpu, Format format, bool fourColour, bool bc3)
    {
        var table = new float[ChannelBase(2) + 32 * 32 * 4];
        var blocks = new List<(int Channel, int A, int B, byte[] Block)>();
        for (var c = 0; c < 3; c++)
        {
            var n = 1 << Bits[c];
            for (var a = 0; a < n; a++)
                for (var b = 0; b < n; b++)
                {
                    // The other channels fix the mode: R decides first (top bits), then G, then B.
                    int[] lo = [0, 0, 0], hi = [0, 0, 0];
                    lo[c] = a; hi[c] = b;   // colour0 gets a, colour1 gets b
                    var c0 = Pack(lo[0], lo[1], lo[2]);
                    var c1 = Pack(hi[0], hi[1], hi[2]);
                    if (!bc3)
                    {
                        var want = fourColour ? c0 > c1 : c0 <= c1;
                        if (!want)
                        {
                            // Order by a higher channel (or a lower one when c is R), leaving channel c as it is.
                            var other = c == 0 ? 1 : 0;
                            int[] l2 = [.. lo], h2 = [.. hi];
                            if (fourColour) { l2[other] = 1; h2[other] = 0; } else { l2[other] = 0; h2[other] = 1; }
                            c0 = Pack(l2[0], l2[1], l2[2]);
                            c1 = Pack(h2[0], h2[1], h2[2]);
                            if ((fourColour ? c0 > c1 : c0 <= c1) == false)
                                continue;   // not reachable in this mode (R with a < b in four-colour mode)
                        }
                    }
                    var block = new byte[bc3 ? 16 : 8];
                    var at = bc3 ? 8 : 0;
                    if (bc3) { block[0] = 255; block[1] = 255; }   // opaque alpha
                    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at), c0);
                    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at + 2), c1);
                    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 4), 0xE4E4E4E4u);   // selectors 0,1,2,3 per row
                    blocks.Add((c, a, b, block));
                }
        }
        Array.Fill(table, float.NaN);
        var texels = Decode(gpu, format, blocks.Select(x => x.Block).ToList());
        for (var i = 0; i < blocks.Count; i++)
        {
            var (c, a, b, _) = blocks[i];
            for (var s = 0; s < 4; s++)
                table[ColourIndex(c, a, b, s)] = texels[i][s][c];   // texel s of the first row has selector s
        }
        return table;
    }

    private float[] AlphaTable(GpuDevice gpu, Format format)
    {
        var bc3 = format == Format.BC3UnormBlock;
        var table = new float[256 * 256 * 8];
        var blocks = new List<byte[]>();
        for (var a = 0; a < 256; a++)
            for (var b = 0; b < 256; b++)
            {
                var block = new byte[bc3 ? 16 : 8];
                block[0] = (byte)a;
                block[1] = (byte)b;
                // 3-bit indices 0..7 for texels 0..7, again for 8..15: 0x688 per 8 texels = 0b111_110_101_100_011_010_001_000
                ulong bits = 0;
                for (var t = 0; t < 16; t++)
                    bits |= (ulong)(t % 8) << (3 * t);
                for (var k = 0; k < 6; k++)
                    block[2 + k] = (byte)(bits >> (8 * k));
                blocks.Add(block);
            }
        var texels = Decode(gpu, format, blocks);
        for (var i = 0; i < blocks.Count; i++)
            for (var t = 0; t < 8; t++)
                table[i * 8 + t] = texels[i][t][bc3 ? 3 : 0];
        return table;
    }

    // Decodes the blocks (all one format) on the GPU: per block, its 16 texels' RGBA in row order.
    private List<float[][]> Decode(GpuDevice gpu, Format format, IReadOnlyList<byte[]> blocks)
    {
        var result = new List<float[][]>(blocks.Count);
        var size = blocks[0].Length;
        for (var start = 0; start < blocks.Count; start += BlocksPerImage)
        {
            var count = Math.Min(BlocksPerImage, blocks.Count - start);
            var data = new byte[BlocksPerImage * size];
            for (var i = 0; i < count; i++)
                blocks[start + i].CopyTo(data, i * size);
            var rgba = TextureDecodeGpuProbe.GpuDecode(gpu, format, data, Side, Side);
            for (var i = 0; i < count; i++)
            {
                int bx = i % (Side / 4) * 4, by = i / (Side / 4) * 4;
                var texels = new float[16][];
                for (var t = 0; t < 16; t++)
                {
                    var p = ((by + t / 4) * Side + bx + t % 4) * 4;
                    texels[t] = [rgba[p], rgba[p + 1], rgba[p + 2], rgba[p + 3]];
                }
                result.Add(texels);
            }
        }
        return result;
    }

    private static float[] PredictBc1(byte[] block, int texel, float[] four, float[] three)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2));
        var sel = (int)(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4)) >> (2 * texel)) & 3;
        var table = c0 > c1 ? four : three;
        int[] a = [c0 >> 11, (c0 >> 5) & 63, c0 & 31], b = [c1 >> 11, (c1 >> 5) & 63, c1 & 31];
        var alpha = c0 <= c1 && sel == 3 ? 0f : 1f;
        return [table[ColourIndex(0, a[0], b[0], sel)], table[ColourIndex(1, a[1], b[1], sel)], table[ColourIndex(2, a[2], b[2], sel)], alpha];
    }

    private static float[] PredictBc3(byte[] block, int texel, float[] colour, float[] bc4)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(8));
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10));
        var sel = (int)(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12)) >> (2 * texel)) & 3;
        int[] a = [c0 >> 11, (c0 >> 5) & 63, c0 & 31], b = [c1 >> 11, (c1 >> 5) & 63, c1 & 31];
        return [colour[ColourIndex(0, a[0], b[0], sel)], colour[ColourIndex(1, a[1], b[1], sel)], colour[ColourIndex(2, a[2], b[2], sel)], Bc4(block, 0, texel, bc4)];
    }

    private static float Bc4(byte[] block, int at, int texel, float[] table)
    {
        ulong bits = 0;
        for (var k = 0; k < 6; k++)
            bits |= (ulong)block[at + 2 + k] << (8 * k);
        var index = (int)(bits >> (3 * texel)) & 7;
        return table[(block[at] * 256 + block[at + 1]) * 8 + index];
    }

    private void Check(GpuDevice gpu, Format format, int size, int count, Random rng, Func<byte[], int, float[]> predict, string name)
    {
        var blocks = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var b = new byte[size];
            rng.NextBytes(b);
            blocks.Add(b);
        }
        var texels = Decode(gpu, format, blocks);
        long total = 0, exact = 0, nan = 0;
        var shown = 0;
        for (var i = 0; i < count; i++)
            for (var t = 0; t < 16; t++)
            {
                var p = predict(blocks[i], t);
                var g = texels[i][t];
                total++;
                if (p.Any(float.IsNaN)) { nan++; continue; }
                var channels = name switch { "BC4" => 1, "BC5" => 2, _ => 4 };
                var same = true;
                for (var c = 0; c < channels; c++)
                    same &= BitConverter.SingleToInt32Bits(p[c]) == BitConverter.SingleToInt32Bits(g[c]);
                if (same) exact++;
                else if (shown++ < 6)
                    output.WriteLine($"  {name} block {Convert.ToHexString(blocks[i])} texel {t}: table {string.Join(",", p.Take(channels))} gpu {string.Join(",", g.Take(channels))}");
            }
        output.WriteLine($"{name}: {exact} of {total} random texels predicted bit for bit ({nan} hit an unmeasured entry)");
    }
}
