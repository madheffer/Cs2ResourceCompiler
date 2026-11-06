using Source2.Compiler.Compiling;
using Source2.Compiler.Models;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// .vmdl compiles against Valve's: s2c_model_probe's models, compiled by the installed
/// resourcecompiler with tools/pipeline/compile_model_ref.py (references in
/// %TEMP%/model_refs/s2c_model_probe/run1). Skips without the addon or the references.
/// </summary>
public sealed class ModelDocCompilerTests(ITestOutputHelper output)
{
    private static readonly string Cs2 = Environment.GetEnvironmentVariable("CS2_DIR") is { Length: > 0 } d ? d
        : @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    // The addon's materials as Valve compiled them beside the model, else the game's (pak01), as RC finds them.
    private static byte[]? Material(string path)
    {
        var local = Path.Combine(Refs, path + "_c");
        if (File.Exists(local))
            return File.ReadAllBytes(local);
        try { return CS2Fixtures.Template(path + "_c"); }
        catch (InvalidOperationException) { return null; }
    }

    private static readonly string Refs = Path.Combine(Path.GetTempPath(), "model_refs", "s2c_model_probe", "run1");

    [Theory]
    [InlineData("models/props/cs_office/ladder1.vmdl")]
    public void StaticModel_MatchesResourceCompiler(string model)
    {
        var content = Path.Combine(Cs2, "content", "csgo_addons", "s2c_model_probe");
        var reference = Path.Combine(Refs, model + "_c");
        if (!File.Exists(Path.Combine(content, model)) || !File.Exists(reference))
        {
            output.WriteLine($"skipped: {model} or its reference is missing");
            return;
        }
        var request = new CompileRequest(model, File.ReadAllBytes(Path.Combine(content, model)))
        {
            SearchPath = "csgo_addons/s2c_model_probe",
            Content = new DirectoryContentSource(content),
            Settings = new ModelDocSettings(Material),
        };
        var ours = new ModelDocCompiler().Compile(request, new List<CompileDiagnostic>()).Resource;
        var valve = File.ReadAllBytes(reference);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "model_refs", Path.GetFileName(model) + "_c.ours"), ours);
        var a = ResourceFile.Read(ours);
        var b = ResourceFile.Read(valve);
        output.WriteLine($"ours {ours.Length} bytes, Valve's {valve.Length}");
        output.WriteLine("ours:  " + string.Join(' ', a.Blocks.Select(x => $"{x.Type}:{x.Data.Length}")));
        output.WriteLine("valve: " + string.Join(' ', b.Blocks.Select(x => $"{x.Type}:{x.Data.Length}")));
        foreach (var block in b.Blocks)
            output.WriteLine($"{block.Type}: {(a[block.Type]?.Data.AsSpan().SequenceEqual(block.Data) == true ? "same" : "DIFFERS")}");
        Assert.Equal(valve, ours);
    }

    /// <summary>
    /// Every model of a corpus list (tools/pipeline/model_probe_corpus.py --list): how many compile to
    /// Valve's bytes, which blocks differ for the rest, and what is refused as not ported.
    /// </summary>
    [Theory]
    [InlineData("model_corpus_render.txt")]
    public void Corpus_MatchesResourceCompiler(string list)
    {
        var content = Path.Combine(Cs2, "content", "csgo_addons", "s2c_model_probe");
        // The repository's tools/pipeline, found upward from the working directory (the test project).
        var listPath = "";
        for (var dir = new DirectoryInfo(SourceDir()); dir != null && listPath.Length == 0; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "pipeline", list)))
                listPath = Path.Combine(dir.FullName, "tools", "pipeline", list);
        if (!File.Exists(listPath) || !Directory.Exists(content))
        {
            output.WriteLine($"skipped: {list} or the probe addon is missing");
            return;
        }
        var compiler = new ModelDocCompiler();
        var settings = new ModelDocSettings(Material);
        var (exact, differs, refused, missing) = (0, 0, 0, 0);
        var blocks = new Dictionary<string, int>();
        var differing = new List<string>();
        var reasons = new Dictionary<string, int>();
        foreach (var model in File.ReadAllLines(listPath).Where(l => l.Length > 0))
        {
            var reference = Path.Combine(Refs, model + "_c");
            byte[] ours;
            try
            {
                ours = compiler.Compile(new CompileRequest(model, File.ReadAllBytes(Path.Combine(content, model)))
                {
                    SearchPath = "csgo_addons/s2c_model_probe",
                    Content = new DirectoryContentSource(content),
                    Settings = settings,
                }, new List<CompileDiagnostic>()).Resource;
            }
            catch (NotSupportedException e)
            {
                refused++;
                var reason = System.Text.RegularExpressions.Regex.Replace(e.Message, "'[^']*'|^[^:]*\\.vmdl: |\\d+", "#");
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                continue;
            }
            if (!File.Exists(reference))
            {
                missing++;
                continue;
            }
            var valve = File.ReadAllBytes(reference);
            if (valve.AsSpan().SequenceEqual(ours))
            {
                exact++;
                continue;
            }
            differs++;
            var keep = Path.Combine(Path.GetTempPath(), "model_refs", "ours", model + "_c");
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.WriteAllBytes(keep, ours);
            var a = ResourceFile.Read(ours);
            var b = ResourceFile.Read(valve);
            var which = string.Join(',', b.Blocks.Where(x => a[x.Type]?.Data.AsSpan().SequenceEqual(x.Data) != true).Select(x => x.Type).Distinct());
            blocks[which] = blocks.GetValueOrDefault(which) + 1;
            output.WriteLine($"differs [{which}]: {model}");
            differing.Add(model);
        }
        output.WriteLine($"exact {exact}, differs {differs}, refused {refused}, no reference {missing}");
        foreach (var (k, n) in blocks.OrderByDescending(x => x.Value))
            output.WriteLine($"  {n} differ in {k}");
        foreach (var (k, n) in reasons.OrderByDescending(x => x.Value))
            output.WriteLine($"  {n} refused: {k}");
        // The open differences (docs/MODELS.md): one packed tangent frame word each (OPEN 1.c7, the shared
        // tangent recompute a step off Valve's at packing boundaries), and skybox_bottom's fourth layout buffer.
        string[] open =
        [
            "models/deadbodies/cemetary/pose_d.vmdl", "models/props_doors/checkpoint_frame_reinforcement_01.vmdl",
            "models/props_fairgrounds/ride_rotato.vmdl", "models/props_urban/chimney005_02.vmdl",
            "models/props_urban/porch_light001.vmdl", "models/props_urban/telephone_streetlight001.vmdl",
            "models/props_skybox/skybox_bottom.vmdl",
        ];
        Assert.Empty(differing.Except(open));
    }
}

/// <summary>Exploration: where a differing model's first vertex buffer departs from Valve's (field by field).</summary>
public sealed class ModelDocVertexProbe(ITestOutputHelper output)
{
    [Theory]
    [InlineData("models/deadbodies/cemetary/pose_d.vmdl", 20)]
    [InlineData("models/props_urban/porch_light001.vmdl", 20)]
    public void FirstVertexBuffer(string model, int stride)
    {
        var ours = Path.Combine(Path.GetTempPath(), "model_refs", "ours", model + "_c");
        var valve = Path.Combine(Path.GetTempPath(), "model_refs", "s2c_model_probe", "run1", model + "_c");
        if (!File.Exists(ours) || !File.Exists(valve))
            return;
        var a = ResourceFile.Read(File.ReadAllBytes(ours))["MVTX"]!.Data;
        var v = ResourceFile.Read(File.ReadAllBytes(valve))["MVTX"]!.Data;
        output.WriteLine($"MVTX ours {a.Length} valve {v.Length}");
        // The element count is in CTRL; read it from Valve's resourceinfo-free route: try counts until both decode.
        int Count(byte[] b) { for (var n = 1; n < 200000; n++) { try { Meshes.MeshoptVertexCodec.Decode(b, n, stride); return n; } catch { } } return -1; }
        var n = Count(v);
        var da = Meshes.MeshoptVertexCodec.Decode(a, n, stride);
        var dv = Meshes.MeshoptVertexCodec.Decode(v, n, stride);
        var shown = 0;
        var byField = new int[stride / 4];
        for (var i = 0; i < n; i++)
            for (var f = 0; f < stride / 4; f++)
                if (BitConverter.ToUInt32(da, i * stride + f * 4) != BitConverter.ToUInt32(dv, i * stride + f * 4))
                {
                    byField[f]++;
                    if (shown++ < 12)
                        output.WriteLine($"v{i} word{f}: ours {BitConverter.ToUInt32(da, i * stride + f * 4):x8} valve {BitConverter.ToUInt32(dv, i * stride + f * 4):x8}");
                }
        output.WriteLine($"{n} vertices; differing words by field: {string.Join(' ', byField)}");
    }
}
