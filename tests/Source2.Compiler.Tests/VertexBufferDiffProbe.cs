using Xunit;
using Source2.Compiler.Meshes;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// VBDIFF=valve.vmdl_c|ours.vmdl_c: every vertex buffer of the two models decoded, the
/// first differing elements printed as hex words with their element index. Prints only.
/// </summary>
public sealed class VertexBufferDiffProbe(ITestOutputHelper output)
{
    [Fact]
    public void Diff()
    {
        if (Environment.GetEnvironmentVariable("VBSCAN") is { } dir)
        {
            var seen = new Dictionary<string, int>();
            foreach (var file in Directory.GetFiles(dir, "*.vmdl_c"))
                foreach (var buffer in CompiledModel.Read(File.ReadAllBytes(file)).Meshes.SelectMany(m => m.VertexBuffers).Where(b => b.ElementSize == 12))
                {
                    var data = buffer.Read();
                    for (var v = 0; v < buffer.ElementCount; v++)
                    {
                        var key = Convert.ToHexString(data.AsSpan((v * 12) + 4, 8));
                        seen[key] = seen.GetValueOrDefault(key) + 1;
                    }
                }
            foreach (var (k, n) in seen.OrderByDescending(x => x.Value).Take(15))
                output.WriteLine($"  {k} x{n}");
            return;
        }
        if (Environment.GetEnvironmentVariable("VBDUMP") is { } one)
        {
            foreach (var (buffer, i) in CompiledModel.Read(File.ReadAllBytes(one)).Meshes.SelectMany(m => m.VertexBuffers).Select((x, i) => (x, i)))
            {
                var data = buffer.Read();
                output.WriteLine($"buffer {i}: {buffer.ElementCount}x{buffer.ElementSize}");
                for (var v = 0; v < Math.Min(8, buffer.ElementCount); v++)
                    output.WriteLine($"  v{v}: {Convert.ToHexString(data.AsSpan(v * buffer.ElementSize, buffer.ElementSize))}");
            }
            return;
        }
        if (Environment.GetEnvironmentVariable("VBDIFF") is not { } pair)
            return;
        var paths = pair.Split('|');
        var a = CompiledModel.Read(File.ReadAllBytes(paths[0]));
        var b = CompiledModel.Read(File.ReadAllBytes(paths[1]));
        var ba = a.Meshes.SelectMany(m => m.VertexBuffers).ToList();
        var bb = b.Meshes.SelectMany(m => m.VertexBuffers).ToList();
        for (var i = 0; i < Math.Min(ba.Count, bb.Count); i++)
        {
            var (x, y) = (ba[i].Read(), bb[i].Read());
            output.WriteLine($"buffer {i}: {ba[i].ElementCount}x{ba[i].ElementSize} vs {bb[i].ElementCount}x{bb[i].ElementSize}");
            var shown = 0;
            for (var v = 0; v < Math.Min(ba[i].ElementCount, bb[i].ElementCount) && shown < 12; v++)
            {
                var ex = x.AsSpan(v * ba[i].ElementSize, ba[i].ElementSize).ToArray();
                var ey = y.AsSpan(v * bb[i].ElementSize, bb[i].ElementSize).ToArray();
                if (ex.AsSpan().SequenceEqual(ey))
                    continue;
                shown++;
                output.WriteLine($"  v{v}: valve {Convert.ToHexString(ex)}");
                output.WriteLine($"  v{v}: ours  {Convert.ToHexString(ey)}");
            }
        }
    }
}
