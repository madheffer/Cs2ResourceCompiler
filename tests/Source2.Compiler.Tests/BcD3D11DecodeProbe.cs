using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Source2.Compiler.Gpu;
using Xunit;
using Xunit.Abstractions;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Source2.Compiler.Tests;

/// <summary>
/// CS2's default renderer is D3D11, the BC tables were measured through Vulkan. This decodes random blocks of
/// every BC format CS2 stores, UNORM and sRGB, on the same GPU through D3D11 (a compute shader's Texture2D.Load,
/// no filtering) and through Vulkan (the nearest blit the tables came from), and compares them bit for bit.
/// <c>D3D11BC=[random blocks per format]</c>.
/// </summary>
public sealed unsafe class BcD3D11DecodeProbe(ITestOutputHelper output)
{
    private const int Side = 512;

    private const string Hlsl = """
        Texture2D<float4> t : register(t0);
        RWStructuredBuffer<float4> o : register(u0);
        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID) { o[id.y * 512 + id.x] = t.Load(int3(id.xy, 0)); }
        """;

    [Fact]
    public void D3D11_EqualsVulkan()
    {
        if (Environment.GetEnvironmentVariable("D3D11BC") is not { } spec)
            return;
        var count = spec.Length > 0 ? int.Parse(spec) : Side / 4 * (Side / 4) * 4;
        var d3d = D3D11.GetApi(null);
        var compiler = D3DCompiler.GetApi();
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        D3DFeatureLevel level = D3DFeatureLevel.Level110;
        SilkMarshal.ThrowHResult(d3d.CreateDevice(null, D3DDriverType.Hardware, 0, 0, &level, 1, D3D11.SdkVersion, &device, null, &context));

        ID3D10Blob* code = null, errors = null;
        var source = System.Text.Encoding.ASCII.GetBytes(Hlsl);
        var entry = "main\0"u8.ToArray();
        var target = "cs_5_0\0"u8.ToArray();
        fixed (byte* s = source, e = entry, tg = target)
        {
            var hr = compiler.Compile(s, (nuint)source.Length, (byte*)null, null, null, e, tg, 0, 0, &code, &errors);
            if (hr < 0)
                throw new InvalidOperationException("HLSL: " + (errors != null ? SilkMarshal.PtrToString((nint)errors->GetBufferPointer()) : hr.ToString()));
        }
        ID3D11ComputeShader* shader = null;
        SilkMarshal.ThrowHResult(device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), null, &shader));

        using var gpu = new GpuDevice();
        output.WriteLine($"Vulkan: {gpu.Name}; D3D11 feature level {level}");
        var cases = new (string Name, Format Dx, VkFormat Vk, int BlockBytes)[]
        {
            ("BC1", Format.FormatBC1Unorm, VkFormat.BC1RgbaUnormBlock, 8), ("BC1 sRGB", Format.FormatBC1UnormSrgb, VkFormat.BC1RgbaSrgbBlock, 8),
            ("BC3", Format.FormatBC3Unorm, VkFormat.BC3UnormBlock, 16), ("BC3 sRGB", Format.FormatBC3UnormSrgb, VkFormat.BC3SrgbBlock, 16),
            ("BC4", Format.FormatBC4Unorm, VkFormat.BC4UnormBlock, 8), ("BC5", Format.FormatBC5Unorm, VkFormat.BC5UnormBlock, 16),
            ("BC6H", Format.FormatBC6HUF16, VkFormat.BC6HUfloatBlock, 16),
            ("BC7", Format.FormatBC7Unorm, VkFormat.BC7UnormBlock, 16), ("BC7 sRGB", Format.FormatBC7UnormSrgb, VkFormat.BC7SrgbBlock, 16),
            ("RGBA8 sRGB", Format.FormatR8G8B8A8UnormSrgb, VkFormat.R8G8B8A8Srgb, 64),
        };
        var rng = new Random(0xd311);
        var perImage = Side / 4 * (Side / 4);
        foreach (var (name, dx, vk, size) in cases)
        {
            long texels = 0, same = 0;
            var shown = 0;
            for (var done = 0; done < count; done += perImage)
            {
                var data = new byte[perImage * size];
                rng.NextBytes(data);
                var a = DecodeD3D11(device, context, shader, dx, data, size == 64 ? Side * 4 : Side / 4 * size);
                var b = TextureDecodeGpuProbe.GpuDecode(gpu, vk, data, Side, Side);
                for (var i = 0; i < a.Length; i += 4)
                {
                    texels++;
                    var eq = true;
                    for (var c = 0; c < 4; c++)
                        eq &= BitConverter.SingleToInt32Bits(a[i + c]) == BitConverter.SingleToInt32Bits(b[i + c]);
                    if (eq) same++;
                    else if (shown++ < 3)
                        output.WriteLine($"  {name} texel {i / 4}: D3D11 {a[i]:R},{a[i + 1]:R},{a[i + 2]:R},{a[i + 3]:R} Vulkan {b[i]:R},{b[i + 1]:R},{b[i + 2]:R},{b[i + 3]:R}");
                }
            }
            output.WriteLine($"{name,-11} {same} of {texels} texels equal bit for bit");
        }
        shader->Release();
        code->Release();
        context->Release();
        device->Release();
    }

    private static float[] DecodeD3D11(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11ComputeShader* shader, Format format, byte[] data, int rowPitch)
    {
        var desc = new Texture2DDesc
        {
            Width = Side, Height = Side, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDesc = new SampleDesc(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource,
        };
        ID3D11Texture2D* texture = null;
        fixed (byte* p = data)
        {
            var init = new SubresourceData(p, (uint)rowPitch, 0);
            SilkMarshal.ThrowHResult(device->CreateTexture2D(&desc, &init, &texture));
        }
        ID3D11ShaderResourceView* srv = null;
        SilkMarshal.ThrowHResult(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv));
        var bytes = (uint)(Side * Side * 16);
        var bufferDesc = new BufferDesc
        {
            ByteWidth = bytes, Usage = Usage.Default, BindFlags = (uint)BindFlag.UnorderedAccess,
            MiscFlags = (uint)ResourceMiscFlag.BufferStructured, StructureByteStride = 16,
        };
        ID3D11Buffer* buffer = null;
        SilkMarshal.ThrowHResult(device->CreateBuffer(&bufferDesc, null, &buffer));
        ID3D11UnorderedAccessView* uav = null;
        SilkMarshal.ThrowHResult(device->CreateUnorderedAccessView((ID3D11Resource*)buffer, null, &uav));
        var stagingDesc = new BufferDesc { ByteWidth = bytes, Usage = Usage.Staging, CPUAccessFlags = (uint)CpuAccessFlag.Read, StructureByteStride = 16, MiscFlags = (uint)ResourceMiscFlag.BufferStructured };
        ID3D11Buffer* staging = null;
        SilkMarshal.ThrowHResult(device->CreateBuffer(&stagingDesc, null, &staging));

        context->CSSetShader(shader, null, 0);
        context->CSSetShaderResources(0, 1, &srv);
        context->CSSetUnorderedAccessViews(0, 1, &uav, null);
        context->Dispatch(Side / 8, Side / 8, 1);
        context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)buffer);
        MappedSubresource mapped = default;
        SilkMarshal.ThrowHResult(context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped));
        var result = new float[Side * Side * 4];
        new ReadOnlySpan<float>(mapped.PData, result.Length).CopyTo(result);
        context->Unmap((ID3D11Resource*)staging, 0);
        ID3D11ShaderResourceView* noSrv = null;
        ID3D11UnorderedAccessView* noUav = null;
        context->CSSetShaderResources(0, 1, &noSrv);
        context->CSSetUnorderedAccessViews(0, 1, &noUav, null);
        staging->Release();
        uav->Release();
        buffer->Release();
        srv->Release();
        texture->Release();
        return result;
    }
}
