using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// NODEENTRIES=capture_nodeentries.py file, NODEENTRIES_VMAP=the map, ENTRYVERTEX_MATERIAL=substring,
/// ENTRYVERTEX_STAGE=stage name (default the last): the captured entries with that material (stream layout
/// and first vertices) beside our baked prop entries with it (WorldNodeBuild.PropEntries). Prints only.
/// </summary>
public sealed class EntryVertexProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || Environment.GetEnvironmentVariable("ENTRYVERTEX_MATERIAL") is not { } want || CS2Fixtures.StockPak() is not { } pak)
            return;
        var stageWanted = Environment.GetEnvironmentVariable("ENTRYVERTEX_STAGE");
        using var r = new BinaryReader(File.OpenRead(path));
        var shown = 0;
        while (r.BaseStream.Position < r.BaseStream.Length && shown < 4)
        {
            var head = System.Text.Json.JsonDocument.Parse(r.ReadBytes(r.ReadInt32())).RootElement;
            var blob = r.ReadBytes(r.ReadInt32());
            if (head.GetProperty("ev").GetString() != "entry" || !head.TryGetProperty("material", out var mat) || !mat.GetString()!.Contains(want, StringComparison.OrdinalIgnoreCase))
                continue;
            var stage = head.TryGetProperty("stage", out var st) ? st.GetString() : "";
            if (stageWanted != null && stage != stageWanted)
                continue;
            int nv = head.GetProperty("nv").GetInt32(), stride = head.GetProperty("stride").GetInt32();
            var v = new float[nv * stride];
            Buffer.BlockCopy(blob, 0x238, v, 0, v.Length * 4);
            var streams = head.GetProperty("streams").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()}{x.GetProperty("index").GetInt32()}@{x.GetProperty("first").GetInt32()}x{x.GetProperty("count").GetInt32()}");
            output.WriteLine($"valve {stage} {mat.GetString()} {nv} vertices stride {stride} attr {BitConverter.ToUInt64(blob, 0x1b0):x}: {string.Join(" ", streams)}");
            for (var k = 0; k < Math.Min(3, nv); k++)
                output.WriteLine("   " + string.Join(" ", v.Skip(k * stride).Take(stride).Select(x => x.ToString("R"))));
            shown++;
        }
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        using var shaders = new ShaderLibrary(Path.Combine(game, "csgo", "shaders_pc_dir.vpk"), Path.Combine(game, "core", "shaders_pc_dir.vpk"));
        var doc = MapSource.Read(vmap);
        foreach (var n in WorldNodeBuild.PropEntries(doc, content, shaders).Where(n => n.Entry.Material.Contains(want, StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            var e = n.Entry;
            output.WriteLine($"ours {e.Material} {e.Vertices.Length / e.Stride} vertices stride {e.Stride} attr {n.Header.Attributes:x}: {string.Join(" ", e.Streams.Select(s => $"{s.Name}@{s.First}x{s.Count}"))}");
            for (var k = 0; k < 3; k++)
                output.WriteLine("   " + string.Join(" ", e.Vertices.Skip(k * e.Stride).Take(e.Stride).Select(x => x.ToString("R"))));
        }
    }
}
