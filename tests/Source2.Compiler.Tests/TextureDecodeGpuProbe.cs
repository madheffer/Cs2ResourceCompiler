using Silk.NET.Vulkan;
using SkiaSharp;
using Source2.Compiler.Gpu;
using Source2.Compiler.Textures;
using ValvePak;
using Xunit;
using Xunit.Abstractions;
using CT = Source2.Compiler.Textures.CompiledTexture;
using TF = Source2.Compiler.Textures.VTexFormat;

namespace Source2.Compiler.Tests;

/// <summary>
/// Our CPU texture decode (<see cref="CT.GenerateBitmap"/> with no codec) against the GPU's: CS2 and vrad3
/// sample these blocks on the GPU, so its hardware decode is what the game shows. For each stored format,
/// pak01 textures (2D, one slice) are decoded at one mip by us and by a nearest blit of the stored blocks into
/// an R32G32B32A32 float image on the GPU, read back and compared texel by texel. LDR channels compare as
/// bytes (the GPU's value times 255 must be our byte); HDR channels as floats.
/// <c>TEXGPU=&lt;per format&gt;[|max side]</c>, with <c>CS2_DIR</c> or the default install.
/// </summary>
public sealed unsafe class TextureDecodeGpuProbe(ITestOutputHelper output)
{
    private static readonly (TF Format, Format Vk)[] Formats =
    [
        (TF.DXT1, Format.BC1RgbaUnormBlock), (TF.DXT5, Format.BC3UnormBlock), (TF.ATI1N, Format.BC4UnormBlock),
        (TF.ATI2N, Format.BC5UnormBlock), (TF.BC6H, Format.BC6HUfloatBlock), (TF.BC7, Format.BC7UnormBlock),
        (TF.RGBA8888, Format.R8G8B8A8Unorm), (TF.BGRA8888, Format.B8G8R8A8Unorm), (TF.I8, Format.R8Unorm),
        (TF.IA88, Format.R8G8Unorm), (TF.R16, Format.R16Unorm), (TF.RG1616, Format.R16G16Unorm),
        (TF.RGBA16161616, Format.R16G16B16A16Unorm), (TF.R16F, Format.R16Sfloat), (TF.RG1616F, Format.R16G16Sfloat),
        (TF.RGBA16161616F, Format.R16G16B16A16Sfloat), (TF.R32F, Format.R32Sfloat), (TF.RG3232F, Format.R32G32Sfloat),
        (TF.RGBA32323232F, Format.R32G32B32A32Sfloat),
    ];

    [Fact]
    public void CpuDecode_EqualsTheGpus()
    {
        if (Environment.GetEnvironmentVariable("TEXGPU") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var perFormat = int.Parse(parts[0]);
        var maxSide = parts.Length > 1 ? int.Parse(parts[1]) : 256;

        using var pak = new Package();
        pak.Read(CS2Fixtures.StockPak() ?? throw new InvalidOperationException("no pak01_dir.vpk (set CS2_DIR)"));
        using var gpu = new GpuDevice();
        var byFormat = new Dictionary<TF, List<(string Path, CT Tex)>>();
        foreach (var e in Io.VpkEntries.ByExtension(pak)["vtex_c"])
        {
            pak.ReadEntry(e, out var bytes);
            CT tex;
            try { tex = CT.Read(bytes); }
            catch (InvalidDataException) { continue; }
            if ((tex.Flags & (VTexFlags.CUBE_TEXTURE | VTexFlags.VOLUME_TEXTURE)) != 0 || tex.Depth > 1)
                continue;
            if (!Formats.Any(f => f.Format == tex.Format))
                continue;
            var list = byFormat.TryGetValue(tex.Format, out var l) ? l : byFormat[tex.Format] = [];
            if (list.Count < perFormat)
                list.Add((e.GetFullPath(), tex));
        }

        output.WriteLine($"GPU: {gpu.Name}");
        output.WriteLine($"{"format",-16} {"textures",8} {"texels",10} {"exact",10} {"off by 1",9} {"worse",7} {"max diff",10} {"floats exact",13}");
        foreach (var (format, vkFormat) in Formats)
        {
            if (!byFormat.TryGetValue(format, out var list))
                continue;
            gpu.Vk.GetPhysicalDeviceFormatProperties(gpu.Physical, vkFormat, out var props);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.BlitSrcBit) == 0)
            {
                output.WriteLine($"{format,-16} the GPU cannot blit from {vkFormat}");
                continue;
            }
            long texels = 0, exact = 0, offByOne = 0, worse = 0, bitExact = 0;
            double maxDiff = 0;
            var examples = new List<string>();
            foreach (var (path, tex) in list)
            {
                var mip = 0u;
                while (mip + 1 < tex.NumMipLevels && Math.Max(tex.Width >> (int)mip, tex.Height >> (int)mip) > maxSide)
                    mip++;
                using var cpu = tex.GenerateBitmap(0, 0, mip, TextureCodec.None);
                var gpuTexels = GpuDecode(gpu, vkFormat, tex.ReadMip(mip), Math.Max(1, tex.Width >> (int)mip), Math.Max(1, tex.Height >> (int)mip));
                // The exact floats (CompiledTexture.DecodeTexels): all four channels bit for bit with the GPU's.
                var ours = tex.DecodeTexels(0, 0, mip);
                var hdr = cpu.ColorType == SKColorType.RgbaF32;
                var w = cpu.Width;
                for (var y = 0; y < cpu.Height; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var g = gpuTexels.AsSpan(((y * Math.Max(1, tex.Width >> (int)mip)) + x) * 4, 4);
                        float[] c;
                        if (hdr)
                        {
                            var px = cpu.GetPixelSpan();
                            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(px).Slice((y * w + x) * 4, 4);
                            c = [f[0], f[1], f[2], f[3]];
                        }
                        else
                        {
                            var p = cpu.GetPixel(x, y);
                            c = [p.Red, p.Green, p.Blue, p.Alpha];
                        }
                        var channels = Channels(format);
                        double worst = 0;
                        for (var k = 0; k < 4; k++)
                        {
                            if (!channels[k])
                                continue;
                            var diff = hdr ? Math.Abs(c[k] - g[k]) : Math.Abs(c[k] - g[k] * 255.0);
                            worst = Math.Max(worst, diff);
                        }
                        texels++;
                        var o = ours.AsSpan((y * w + x) * 4, 4);
                        var all = true;
                        for (var k = 0; k < 4; k++)
                            all &= !channels[k] || BitConverter.SingleToInt32Bits(o[k]) == BitConverter.SingleToInt32Bits(g[k]);
                        if (all) bitExact++;
                        maxDiff = Math.Max(maxDiff, worst);
                        if (worst == 0 || (!hdr && worst < 1e-3)) exact++;
                        else if (!hdr && worst <= 1.0 + 1e-3) offByOne++;
                        else
                        {
                            worse++;
                            if (examples.Count < 3)
                                examples.Add($"    {path} mip {mip} ({x},{y}): ours {string.Join(",", c.Select(v => v.ToString("0.#####")))} gpu {string.Join(",", g.ToArray().Select(v => (hdr ? v : v * 255).ToString("0.#####")))}");
                        }
                    }
            }
            output.WriteLine($"{format,-16} {list.Count,8} {texels,10} {exact,10} {offByOne,9} {worse,7} {maxDiff,10:0.######} {bitExact,13}");
            foreach (var ex in examples)
                output.WriteLine(ex);
        }
    }

    // Which RGBA channels the format stores; the rest are the decoder's constants (BC4 G/B, BC5 B, ...).
    private static bool[] Channels(TF f) => f switch
    {
        TF.ATI1N or TF.I8 or TF.R16 or TF.R16F or TF.R32F => [true, false, false, false],
        TF.ATI2N or TF.RG1616 or TF.RG1616F or TF.RG3232F => [true, true, false, false],
        TF.IA88 => [true, false, false, true],
        TF.BC6H => [true, true, true, false],
        _ => [true, true, true, true],
    };

    internal static float[] GpuDecode(GpuDevice gpu, Format format, byte[] blocks, int width, int height)
    {
        var src = gpu.CreateImage((uint)width, (uint)height, 1, format, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit);
        var dst = gpu.CreateImage((uint)width, (uint)height, 1, Format.R32G32B32A32Sfloat, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit);
        var staging = gpu.Upload(blocks, BufferUsageFlags.TransferSrcBit);
        var readback = gpu.CreateBuffer((ulong)(width * height * 16), BufferUsageFlags.TransferDstBit);
        gpu.Submit(cmd =>
        {
            gpu.Transition(cmd, src, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                new Offset3D(0, 0, 0), new Extent3D((uint)width, (uint)height, 1));
            gpu.Vk.CmdCopyBufferToImage(cmd, staging.Buffer, src.Image, ImageLayout.TransferDstOptimal, 1, &region);
            gpu.Transition(cmd, src, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal);
            gpu.Transition(cmd, dst, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(width, height, 1);
            blit.DstOffsets[1] = new Offset3D(width, height, 1);
            gpu.Vk.CmdBlitImage(cmd, src.Image, ImageLayout.TransferSrcOptimal, dst.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
            gpu.Transition(cmd, dst, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal);
            var back = new BufferImageCopy(0, 0, 0, new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                new Offset3D(0, 0, 0), new Extent3D((uint)width, (uint)height, 1));
            gpu.Vk.CmdCopyImageToBuffer(cmd, dst.Image, ImageLayout.TransferSrcOptimal, readback.Buffer, 1, &back);
        });
        var result = new float[width * height * 4];
        new ReadOnlySpan<float>((void*)readback.Mapped, result.Length).CopyTo(result);
        gpu.Destroy(staging);
        gpu.Destroy(readback);
        foreach (var image in new[] { src, dst })
        {
            gpu.Vk.DestroyImageView(gpu.Device, image.View, null);
            gpu.Vk.DestroyImage(gpu.Device, image.Image, null);
            gpu.Vk.FreeMemory(gpu.Device, image.Memory, null);
        }
        return result;
    }
}
