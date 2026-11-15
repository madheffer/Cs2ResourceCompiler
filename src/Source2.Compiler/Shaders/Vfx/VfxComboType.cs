// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Shader combo types.
/// </summary>
public enum VfxComboType
{
#pragma warning disable CS1591
    Unknown,
    Static,
    Dynamic,
    Alias,
#pragma warning restore CS1591
}
