// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/Blocks/VsInputSignature.cs, onto the INSG element reader materials use.
using ValveKeyValue;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>One vertex input signature (<c>m_vsInputSignatureArray[i]</c>): its INSG elements.</summary>
public sealed class VsInputSignature
{
    /// <summary>The signature's index.</summary>
    public int Index { get; }

    /// <summary>The elements.</summary>
    public IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement> Elements { get; }

    /// <summary>Read a signature.</summary>
    public VsInputSignature(KVObject data, int index)
    {
        Index = index;
        Elements = PackageMaterialAuthor.ReadSignature(data);
    }
}
