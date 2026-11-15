using Source2.Compiler.Shaders.Vfx;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>SHADERDUMP=&lt;shader vpk&gt;|&lt;name substring&gt;|&lt;out dir&gt;</c>): every
/// .vcs in the package whose path contains the substring, written as its
/// variable names (variables.txt) and, per static combo, each distinct shader
/// file decompiled through SPIRV-Cross (s&lt;combo&gt;_f&lt;file&gt;.hlsl). Used to read
/// vrad3's ray tracing shaders (rtx_vrad3_*).
/// </summary>
public class ShaderDumpProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("SHADERDUMP") is not { Length: > 0 } spec || spec.Split('|') is not [var pak, var part, var outDir])
            return;
        using var package = new ValvePak.Package();
        package.Read(pak);
        foreach (var entry in package.Entries!.Values.SelectMany(e => e).Where(e => e.GetFullPath().Contains(part, StringComparison.OrdinalIgnoreCase)
                                                                                     && e.GetFullPath().EndsWith(".vcs", StringComparison.Ordinal)))
        {
            var name = Path.GetFileNameWithoutExtension(entry.GetFullPath());
            var dir = Path.Combine(outDir, name);
            Directory.CreateDirectory(dir);
            package.ReadEntry(entry, out var bytes);
            var program = VfxProgramData.Read(entry.GetFullPath(), bytes);
            File.WriteAllLines(Path.Combine(dir, "variables.txt"),
                program.VariableDescriptions.Select((v, i) => $"{i}\t{v.Name}\t{v.VfxType}\t{v.SourceIndex}"));
            var files = 0;
            foreach (var (id, combo) in program.StaticComboEntries)
            {
                var data = combo.Unserialize();
                for (var f = 0; f < data.ShaderFiles.Length; f++)
                {
                    var code = data.ShaderFiles[f] is { } file ? Decompile(file) : "";
                    File.WriteAllText(Path.Combine(dir, $"s{id}_f{f}.hlsl"), code);
                    files++;
                }
            }
            output.WriteLine($"{name}: {program.VariableDescriptions.Length} variables, {program.StaticComboEntries.Count} static combos, {files} shader files");
        }
    }

    // A Vulkan file as HLSL, else GLSL (as VRF's GetDecompiledFile tried them); other bytecode is not decompiled.
    private static string Decompile(VfxShaderFile file) => file is VfxShaderFileVulkan vk
        ? ShaderSpirvReflection.ReflectSpirv(vk, Vortice.SpirvCross.Backend.HLSL, out var hlsl) ? hlsl
          : ShaderSpirvReflection.ReflectSpirv(vk, Vortice.SpirvCross.Backend.GLSL, out var glsl) ? glsl : "// SPIR-V reflection failed"
        : $"{file.SourceType} decompilation is not supported.";
}
