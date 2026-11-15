// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
namespace Source2.Compiler.Shaders.Vfx
{
    /*
     * PCGL and MOBILE_GLES are working
     * PC, VULKAN, IOS_VULKAN, ANDROID_VULKAN parse without error, but lack decompiling source (source may be viewed as bytecode)
     * X360, MAC are not implemented
     *
     */
    /// <summary>
    /// Shader target platforms.
    /// </summary>
    public enum VcsPlatformType
    {
#pragma warning disable CS1591
        VULKAN,
        PC,
        PCGL,
        X360,
        MAC,
        MOBILE_GLES,
        IOS_VULKAN,
        ANDROID_VULKAN,
        Undetermined,
#pragma warning restore CS1591
    }
}
