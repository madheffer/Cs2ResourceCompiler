using ValvePak;
using Source2.Compiler.Kv3;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Feeds tools/re/kv3_tier0_oracle.py, which judges our KV3 reader with CS2's own tier0: for every binary
/// KV3 block of the given types (any version, the legacy VKV3 container too) it tallies the version, and for
/// the first N of each version and type writes Valve's block and our re-encoding of what our reader read
/// (written as v5 by <see cref="Kv3BlockWriter"/>) to <c>&lt;out&gt;/&lt;n&gt;.valve.bin</c> and
/// <c>&lt;n&gt;.ours.bin</c>, with an index line per pair. tier0 then loads both and saves them as text; equal
/// text means our reader kept everything tier0 reads.
/// <c>KV3T0=&lt;out dir&gt;|&lt;ext,ext,...&gt;|&lt;per version and type&gt;</c>; <c>KV3AB_VPK</c> reads another VPK.
/// </summary>
public sealed class Kv3TierZeroDump(ITestOutputHelper output)
{
    [Fact]
    public void DumpPairs()
    {
        if (Environment.GetEnvironmentVariable("KV3T0") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split('|');
        var outDir = parts[0];
        var exts = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('.')).ToArray();
        var per = int.Parse(parts[2]);
        Directory.CreateDirectory(outDir);
        var vpk = Environment.GetEnvironmentVariable("KV3AB_VPK") is { Length: > 0 } v ? v
            : CS2Fixtures.StockPak() ?? throw new InvalidOperationException("no pak01_dir.vpk (set CS2_DIR)");
        using var pak = new Package();
        pak.Read(vpk);
        var byExt = Io.VpkEntries.ByExtension(pak);
        var tally = new SortedDictionary<string, int>();
        var dumped = new Dictionary<string, int>();
        using var index = new StreamWriter(Path.Combine(outDir, "index.tsv"), append: true);
        var n = Directory.EnumerateFiles(outDir, "*.valve.bin").Count();
        foreach (var ext in exts)
        {
            if (!byExt.TryGetValue(ext, out var list))
                continue;
            foreach (var e in list)
            {
                pak.ReadEntry(e, out var bytes);
                ResourceFile file;
                try { file = ResourceFile.Read(bytes); }
                catch (InvalidDataException) { continue; }
                foreach (var block in file.Blocks)
                {
                    if (!Kv3Binary.IsKv3(block.Data))
                        continue;
                    var version = Kv3Binary.Version(block.Data);
                    var key = $"{ext} v{version}";
                    tally[key] = tally.GetValueOrDefault(key) + 1;
                    if (dumped.GetValueOrDefault(key) >= per)
                        continue;
                    byte[] ours;
                    try
                    {
                        var doc = Kv3Binary.Read(block.Data);
                        ours = Kv3BlockWriter.Write(doc.Root, doc.Header!.Format.Id);
                    }
                    catch (Exception ex)
                    {
                        output.WriteLine($"ours failed {e.GetFullPath()} {block.Type} v{version}: {ex.Message}");
                        continue;
                    }
                    dumped[key] = dumped.GetValueOrDefault(key) + 1;
                    File.WriteAllBytes(Path.Combine(outDir, $"{n}.valve.bin"), block.Data);
                    File.WriteAllBytes(Path.Combine(outDir, $"{n}.ours.bin"), ours);
                    index.WriteLine($"{n}\t{key}\t{e.GetFullPath()}\t{block.Type}");
                    n++;
                }
            }
        }
        foreach (var (key, count) in tally)
            output.WriteLine($"{key,-20} {count,8} blocks, {dumped.GetValueOrDefault(key)} dumped");
    }
}
