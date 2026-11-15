// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Shader register types.
/// </summary>
public enum VfxRegisterType
{
#pragma warning disable CS1591
    Bool = 1,
    Int4,
    Float4,
    Texture,
    RenderState,
    SamplerState,
    InputTexture,
    ConstantBuffer,
    Uav,
    DescriptorSet,
    PushConstantBuffer,
    TextureIndex,
    SamplerIndex,
    SpecConstant,
#pragma warning restore CS1591
}
