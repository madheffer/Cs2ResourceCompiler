// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
using ValveKeyValue;
using Source2.Compiler.KeyValues;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Contains render state information for a shader variant.
/// </summary>
public class VfxRenderStateInfo
{
    /// <summary>Gets the dynamic combo ID.</summary>
    public long DynamicComboId { get; }

    /// <summary>Gets the shader file ID.</summary>
    public int ShaderFileId { get; }

    /// <summary>
    /// Gets the byte offset of the shader file within the uncompressed static combo data,
    /// relative to the shader file count field. It is -1 in version 71 and resource files.
    /// </summary>
    public int ShaderFileOffset { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VfxRenderStateInfo"/> class.
    /// </summary>
    public VfxRenderStateInfo(long comboId, int shaderFileId, int shaderFileOffset)
    {
        DynamicComboId = comboId;
        ShaderFileId = shaderFileId;
        ShaderFileOffset = shaderFileOffset;
    }

}

/// <summary>
/// Render state information specific to pixel shaders, including rasterizer, depth/stencil, and blend states.
/// </summary>
public class VfxRenderStateInfoPixelShader : VfxRenderStateInfo
{
    /// <summary>Gets the rasterizer state descriptor.</summary>
    public RsRasterizerStateDesc? RasterizerStateDesc { get; }

    /// <summary>Gets the depth/stencil state descriptor.</summary>
    public RsDepthStencilStateDesc? DepthStencilStateDesc { get; }

    /// <summary>Gets the blend state descriptor.</summary>
    public RsBlendStateDesc? BlendStateDesc { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VfxRenderStateInfoPixelShader"/> class.
    /// </summary>
    public VfxRenderStateInfoPixelShader(long comboId, int shaderFileId, int shaderFileOffset, KVObject renderState, int vcsVersion)
        : base(comboId, shaderFileId, shaderFileOffset)
    {
        if (renderState is null)
        {
            return;
        }

        if (renderState.ContainsKey("rasterizerStateDesc"))
        {
            RasterizerStateDesc = new RsRasterizerStateDesc(renderState.GetArray<int>("rasterizerStateDesc"));
        }

        if (renderState.ContainsKey("depthStencilStateDesc"))
        {
            DepthStencilStateDesc = new RsDepthStencilStateDesc(renderState.GetUnsignedIntegerProperty("depthStencilStateDesc"), vcsVersion);
        }

        // PSRS only has blendStateDesc
        if (renderState.ContainsKey("blendStateDesc"))
        {
            BlendStateDesc = new RsBlendStateDesc(renderState.GetArray<int>("blendStateDesc"));
        }
    }

}
