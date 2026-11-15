// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// IO/ShaderDataProvider.cs (GetStaticConfiguration_ForFeatureState, TryReduceStaticConfiguration) and
// CompiledShader/ShaderUtilHelpers.cs (ParseDynamicExpression).
using System.Diagnostics;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>Static combo selection over a program file, and the dynamic expression printer.</summary>
public static class VfxCombos
{
    /// <summary>
    /// The static configuration a material's feature values select in <paramref name="program"/>, and its id: a combo
    /// set by a feature takes the feature's value (clamped to the feature's range), one set by feature equality 1
    /// when the feature equals (or for _NE_ differs from) the combo's compare value, a combo of no feature the value
    /// <paramref name="staticParams"/> gives it, else 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="features"/> is not a features file, or
    /// <paramref name="program"/> is.</exception>
    public static (int[] StaticConfig, long StaticComboId) GetStaticConfiguration_ForFeatureState(
        VfxProgramData features, VfxProgramData program, IDictionary<string, byte> featureParams, IDictionary<string, byte>? staticParams = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(program);
        if (features.VcsProgramType != VcsProgramType.Features)
            throw new ArgumentOutOfRangeException(nameof(features), $"Needs a {VcsProgramType.Features} file.");
        if (program.VcsProgramType == VcsProgramType.Features)
            throw new ArgumentOutOfRangeException(nameof(program), "A features file has no static configuration.");

        var staticConfiguration = new int[program.StaticComboArray.Length];
        var configMapping = new ComboConfigMapping(program);
        foreach (var condition in program.StaticComboArray)
        {
            if (condition.FeatureIndex == -1)
            {
                if (staticParams != null && staticParams.TryGetValue(condition.Name, out var value))
                    staticConfiguration[condition.Index] = value;
                continue;
            }
            var feature = features.StaticComboArray[condition.FeatureIndex];
            featureParams.TryGetValue(feature.Name, out var featureValue);
            var sourceType = (VfxStaticComboSourceType)condition.ComboSourceType;
            if (sourceType == VfxStaticComboSourceType.__SET_BY_FEATURE__)
                staticConfiguration[condition.Index] = featureValue > feature.RangeMax ? feature.RangeMax
                    : featureValue < feature.RangeMin ? feature.RangeMin : featureValue;
            else if (sourceType == VfxStaticComboSourceType.__SET_BY_FEATURE_EQ__)
            {
                Debug.Assert(condition.RangeMin == 0 && condition.RangeMax == 1);
                staticConfiguration[condition.Index] = featureValue == condition.FeatureComparisonValue ? 1 : 0;
            }
            else if (sourceType == VfxStaticComboSourceType.__SET_BY_FEATURE_NE__)
            {
                Debug.Assert(condition.RangeMin == 0 && condition.RangeMax == 1);
                staticConfiguration[condition.Index] = featureValue != condition.FeatureComparisonValue ? 1 : 0;
            }
            else
                throw new InvalidOperationException($"Combo source type '{sourceType}' for the feature-linked static combo '{condition.Name}'.");
        }
        return (staticConfiguration, configMapping.CalcComboIdFromValues(staticConfiguration));
    }

    /// <summary>
    /// <paramref name="staticConfiguration"/> with the switches the file's static combo rules exclude turned off,
    /// repeated until stable (disabling one combo may break another rule): a ChildOf or Requires rule whose first
    /// argument is on with fewer than ExtraRuleData[0] of the others on turns the first off; an AllowNum rule turns
    /// off the arguments of the rule's type past the first ExtraRuleData[0] that are on.
    /// </summary>
    /// <returns>True when the configuration changed.</returns>
    public static bool TryReduceStaticConfiguration(VfxProgramData program, int[] staticConfiguration, out int[] reducedConfiguration)
    {
        reducedConfiguration = [.. staticConfiguration];
        for (var loop = 0; loop < 100; loop++)
        {
            var changed = false;
            foreach (var constraint in program.StaticComboRules)
            {
                var indexCount = constraint.ExtraRuleData[1];
                if (constraint.RuleMethod is VfxRuleMethod.ChildOf or VfxRuleMethod.Requires)
                {
                    if (!ArgMatches(constraint, 0, reducedConfiguration))
                        continue;
                    var numEnabled = 0;
                    for (var i = 1; i < indexCount; i++)
                        if (ArgMatches(constraint, i, reducedConfiguration))
                            numEnabled++;
                    if (numEnabled < constraint.ExtraRuleData[0])
                    {
                        reducedConfiguration[constraint.ArgIndices[0]] = 0;
                        changed = true;
                    }
                }
                else if (constraint.RuleMethod == VfxRuleMethod.AllowNum)
                {
                    var allowedCount = constraint.ExtraRuleData[0];
                    var numEnabled = 0;
                    for (var i = 0; i < indexCount; i++)
                    {
                        if (!ArgMatches(constraint, i, reducedConfiguration))
                            continue;
                        if (numEnabled >= allowedCount)
                        {
                            if (constraint.ArgTypes[i] == constraint.RuleType)
                            {
                                reducedConfiguration[constraint.ArgIndices[i]] = 0;
                                changed = true;
                            }
                        }
                        else
                            numEnabled++;
                    }
                }
            }
            if (!changed)
                break;
        }
        return !reducedConfiguration.AsSpan().SequenceEqual(staticConfiguration);

        static bool ArgMatches(VfxRule constraint, int argIndex, int[] values)
        {
            if (constraint.ArgTypes[argIndex] != VfxRuleType.Static)
                return false;
            var value = values[constraint.ArgIndices[argIndex]];
            var expected = constraint.ArgValues[argIndex];
            return expected == -1 ? value != 0 : value == expected;
        }
    }

    /// <summary>A static configuration's id in <paramref name="program"/>.</summary>
    public static long ComboId(VfxProgramData program, int[] configuration) => new ComboConfigMapping(program).CalcComboIdFromValues(configuration);

    /// <summary>A compiled dynamic expression as text, without its return statement ("[error in dyn-exp]" when it does not decode).</summary>
    public static string ParseDynamicExpression(byte[] bytecode)
    {
        try
        {
            return new VfxEval(bytecode, omitReturnStatement: true).DynamicExpressionResult.Replace("UNKNOWN", "VAR", StringComparison.InvariantCulture);
        }
        catch (Exception)
        {
            return "[error in dyn-exp]";
        }
    }
}
