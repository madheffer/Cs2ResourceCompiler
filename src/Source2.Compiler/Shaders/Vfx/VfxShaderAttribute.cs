// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
using System.Text;
using ValveKeyValue;
using Source2.Compiler.KeyValues;
namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Shader attribute with name, type, and value or expression.
/// </summary>
public class VfxShaderAttribute
{
    /// <summary>Gets the attribute name.</summary>
    public string Name { get; }
    /// <summary>Gets the 32-bit MurmurHash2 of the name.</summary>
    public uint Murmur32 { get; }
    /// <summary>Gets the variable type.</summary>
    public VfxVariableType VfxType { get; }
    /// <summary>Gets the index of the variable this attribute is bound to, or -1 when it is not bound to one.</summary>
    public short VariableBinding { get; }
    /// <summary>Gets the dynamic expression bytecode.</summary>
    public byte[]? DynExpression { get; }
    /// <summary>Gets the constant value.</summary>
    public object? ConstValue { get; }

    /// <summary>
    /// Initializes a new instance from <see cref="KVObject"/> data.
    /// </summary>
    public VfxShaderAttribute(KVObject data)
    {
        Name = data.GetStringProperty("m_Name");
        VfxType = (VfxVariableType)data.GetInt32Property("m_type");
        VariableBinding = (short)data.GetInt32Property("m_nVariableBinding");

        if (data.TryGetValue("m_value", out var value) && value.ValueType != KVValueType.Null)
        {
            ConstValue = VfxType switch
            {
                VfxVariableType.Int => (int)value,
                VfxVariableType.Bool => (bool)value,
                VfxVariableType.String => (string)value,
                VfxVariableType.Float when value.IsArray => (float)value[0],
                VfxVariableType.Float => (float)value,
                VfxVariableType.Float2 when value.IsArray => value.ToVector2(),
                VfxVariableType.Float3 when value.IsArray => value.ToVector3(),
                VfxVariableType.Float4 when value.IsArray => value.ToVector4(),
                _ => (object)value,
            };
        }

        if (data.GetArray<byte>("m_expr") is byte[] expression)
        {
            DynExpression = expression;
        }

        Murmur32 = StringToken.Store(Name);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a formatted string with the attribute name, hash, type, variable binding, and either the dynamic expression or constant value.
    /// </remarks>
    public override string ToString()
    {
        if (DynExpression != null)
        {
            return $"{Name,-40} 0x{Murmur32:x08}  {VfxType,-15} {VariableBinding,-3}  {VfxCombos.ParseDynamicExpression(DynExpression)}";
        }
        else
        {
            return $"{Name,-40} 0x{Murmur32:x08}  {VfxType,-15} {VariableBinding,-3}  {ConstValue}";
        }
    }
}
