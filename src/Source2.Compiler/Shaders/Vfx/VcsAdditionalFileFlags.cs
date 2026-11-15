// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Flags indicating additional shader files present.
/// </summary>
[Flags]
public enum VcsAdditionalFileFlags
{
#pragma warning disable CS1591
    None = 0,
    HasPixelShaderRenderState = 0x01,
    HasRaytracing = 0x02,
    HasMeshShader = 0x04,
#pragma warning restore CS1591
}
