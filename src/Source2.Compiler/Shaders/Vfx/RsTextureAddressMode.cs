// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Specifies texture addressing modes.
/// </summary>
public enum RsTextureAddressMode : byte
{
#pragma warning disable CS1591
    Wrap = 0,
    Mirror = 1,
    Clamp = 2,
    Border = 3,
    MirrorOnce = 4,
#pragma warning restore CS1591
}
