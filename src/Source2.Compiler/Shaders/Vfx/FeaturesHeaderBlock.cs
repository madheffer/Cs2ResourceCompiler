// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// CompiledShader/: the resource (KV3) layout only.
using System.Diagnostics;
using System.Text;
using ValveKeyValue;
using Source2.Compiler.KeyValues;

namespace Source2.Compiler.Shaders.Vfx;

/// <summary>
/// Header block for shader features files.
/// </summary>
public class FeaturesHeaderBlock
{
    /// <summary>
    /// Gets the version of the shader source this file was compiled from. It increments whenever the source changes,
    /// and is identical across all platforms and shader models the shader was compiled for.
    /// </summary>
    public int Version { get; }
    /// <summary>Gets the file description.</summary>
    public string FileDescription { get; }
    /// <summary>Gets whether this is a development shader.</summary>
    public bool DevShader { get; }
    /// <summary>Gets which program types are present, indexed by program type.</summary>
    public bool[] AvailablePrograms { get; }
    /// <summary>Gets the list of shader modes.</summary>
    public List<(string Name, string ShaderFallback, string StaticComboName, int StaticComboValue)> Modes { get; } = [];

    /// <summary>
    /// Initializes a new instance from <see cref="KVObject"/> data.
    /// </summary>
    public FeaturesHeaderBlock(KVObject data)
    {
        Version = data.GetInt32Property("m_nVersion");
        FileDescription = data.GetStringProperty("m_description");
        DevShader = data.GetBooleanProperty("m_bDevShader");
        AvailablePrograms = data.GetArray<bool>("m_bHasShaderProgram")!;

        var modeArray = data.GetArray("m_modeArray");
        Modes.EnsureCapacity(modeArray.Count);

        foreach (var modeObj in modeArray)
        {
            var name = modeObj.GetStringProperty("m_szName");
            var shaderFallback = modeObj.GetStringProperty("m_szShaderFallback");

            var mode = (name, shaderFallback, StaticComboName: string.Empty, StaticComboValue: -1);

            var settings = modeObj.GetArray("m_staticComboSettings")!;
            if (settings.Count > 0)
            {
                Debug.Assert(settings.Count <= 1, "CVfxModeSettings with more than one combo.");

                var setting = settings[0];
                mode.StaticComboName = setting.GetStringProperty("m_szStaticCombo");
                mode.StaticComboValue = setting.GetInt32Property("m_nValue");
            }

            Modes.Add(mode);
        }
    }

}
