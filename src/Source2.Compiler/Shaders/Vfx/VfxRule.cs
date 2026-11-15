// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
using System.Diagnostics;
using ValveKeyValue;
using Source2.Compiler.KeyValues;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Represents a constraint rule for shader combos.
/// </summary>
public class VfxRule
{
    /// <summary>Gets the index in the owning array.</summary>
    public int Index { get; }
    /// <summary>Gets the rule method.</summary>
    public VfxRuleMethod RuleMethod { get; }
    /// <summary>Gets the rule type.</summary>
    public VfxRuleType RuleType { get; }
    /// <summary>Gets the type of each rule argument.</summary>
    public VfxRuleType[] ArgTypes { get; }
    /// <summary>Gets the combo or feature index of each rule argument.</summary>
    public int[] ArgIndices { get; }
    /// <summary>Gets the value of each rule argument.</summary>
    public int[] ArgValues { get; }
    /// <summary>Gets extra rule data.</summary>
    public int[] ExtraRuleData { get; }
    /// <summary>Gets the error message shown when the rule is violated.</summary>
    public string ErrorString { get; }

    private const int MaxArgs = 16;

    /// <summary>
    /// Initializes a new instance from <see cref="KVObject"/> data.
    /// </summary>
    public VfxRule(KVObject data, int index)
    {
        Index = index;
        RuleMethod = data.GetEnumValue<VfxRuleMethod>("m_rule", normalize: true, stripExtension: "Method");
        RuleType = data.GetEnumValue<VfxRuleType>("m_ruleType", normalize: true);

        ArgTypes = new VfxRuleType[MaxArgs];
        ArgIndices = new int[MaxArgs];
        ArgValues = new int[MaxArgs];
        ExtraRuleData = new int[MaxArgs];

        var argTypesArray = data.GetArray<string>("m_argTypeArray")!;
        var argIndexArray = data.GetArray<int>("m_argIndexArray")!;
        var argValueArray = data.GetArray<int>("m_argValueArray")!;
        var extraRuleData = data.GetArray<int>("m_nExtraRuleData")!;

        Debug.Assert(argTypesArray.Length == MaxArgs);
        Debug.Assert(argIndexArray.Length == MaxArgs);
        Debug.Assert(argValueArray.Length == MaxArgs);
        Debug.Assert(extraRuleData.Length == MaxArgs);

        for (var i = 0; i < MaxArgs; i++)
        {
            ArgTypes[i] = Enum.Parse<VfxRuleType>(KvExtensions.NormalizeEnumName<VfxRuleType>(argTypesArray[i]));
            ArgIndices[i] = argIndexArray[i];
            ArgValues[i] = argValueArray[i];
            ExtraRuleData[i] = extraRuleData[i];
        }

        ErrorString = data.GetStringProperty("m_szErrorString");
    }

}
