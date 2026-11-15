using ValvePak;
using Source2.Compiler.Kv3;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The two KV3 v5 writers against CS2 itself: every v5 block of the given compiled types in the installed
/// pak01 is read with our reader and written again by <see cref="Kv3BlockWriter"/> (the resource library's)
/// and by the map compiler's <c>Kv3V5Writer</c>, each with the compression tier0 chooses. A writer is right
/// for a block when its bytes equal Valve's. Tallies per type and stored method; lists the first misses.
/// <c>KV3AB=&lt;ext,ext,...&gt;[|limit per ext]</c>, with <c>CS2_DIR</c> or the default install; <c>KV3AB_VPK</c> reads another VPK instead (a map Valve compiled).
/// </summary>
public sealed class Kv3WriterAbProbe(ITestOutputHelper output)
{
    [Fact]
    public void BothWriters_AgainstValve()
    {
        if (Environment.GetEnvironmentVariable("KV3AB") is not { Length: > 0 } spec)
            return;
        var pakPath = Environment.GetEnvironmentVariable("KV3AB_VPK") is { Length: > 0 } vpk ? vpk : CS2Fixtures.StockPak() ?? throw new InvalidOperationException("no pak01_dir.vpk (set CS2_DIR)");
        var parts = spec.Split('|');
        var exts = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('.')).ToArray();
        var limit = parts.Length > 1 ? int.Parse(parts[1]) : int.MaxValue;

        using var pak = new Package();
        pak.Read(pakPath);
        var byExt = Io.VpkEntries.ByExtension(pak);
        var tally = new SortedDictionary<string, (int Blocks, int Ours, int Legacy, int Agree)>();
        var misses = new List<string>();
        int unreadable = 0;
        foreach (var ext in exts)
        {
            if (!byExt.TryGetValue(ext, out var list))
                continue;
            foreach (var e in list.Take(limit))
            {
                pak.ReadEntry(e, out var bytes);
                ResourceFile file;
                try { file = ResourceFile.Read(bytes); }
                catch (InvalidDataException) { unreadable++; continue; }
                foreach (var block in file.Blocks)
                {
                    if (Kv3Binary.Version(block.Data) != 5)
                        continue;
                    var doc = Kv3Binary.Read(block.Data);
                    var method = BitConverter.ToUInt32(block.Data, 20) switch { 0 => "raw", 1 => "lz4", 2 => "zstd", var m => $"m{m}" };
                    var key = $"{ext} {method}";
                    var guid = doc.Header!.Format.Id;
                    byte[] ours, legacy;
                    try { ours = Kv3BlockWriter.Write(doc.Root, guid); }
                    catch (Exception ex) { ours = []; misses.Add($"ours threw {e.GetFullPath()} {block.Type}: {ex.Message}"); }
                    try { legacy = Kv3BlockWriter.Write(doc.Root, guid); }
                    catch (Exception ex) { legacy = []; misses.Add($"legacy threw {e.GetFullPath()} {block.Type}: {ex.Message}"); }
                    bool o = ours.AsSpan().SequenceEqual(block.Data), l = legacy.AsSpan().SequenceEqual(block.Data);
                    var (n, so, sl, sa) = tally.GetValueOrDefault(key);
                    tally[key] = (n + 1, so + (o ? 1 : 0), sl + (l ? 1 : 0), sa + (ours.AsSpan().SequenceEqual(legacy) ? 1 : 0));
                    if ((o != l) && misses.Count < 40)
                        misses.Add($"{(o ? "only ours" : "only legacy")} {e.GetFullPath()} {block.Type} ({block.Data.Length} bytes, first diff ours@{ours.AsSpan().CommonPrefixLength(block.Data)} legacy@{legacy.AsSpan().CommonPrefixLength(block.Data)})");
                }
            }
        }
        output.WriteLine($"{"type method",-22} {"blocks",7} {"ours=valve",11} {"legacy=valve",13} {"ours=legacy",12}");
        foreach (var (key, (n, o, l, a)) in tally)
            output.WriteLine($"{key,-22} {n,7} {o,11} {l,13} {a,12}");
        output.WriteLine($"unreadable containers: {unreadable}");
        foreach (var m in misses)
            output.WriteLine(m);
    }
}
