using System.Collections.Concurrent;
using System.Diagnostics;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// Inclusive wall time and call counts per named stage of a decompile, kept only when the
/// environment sets DECOMPILE_PROFILE=1 (a nested stage's time is in its caller's too).
/// </summary>
internal static class DecompileProfile
{
    public static readonly bool On = Environment.GetEnvironmentVariable("DECOMPILE_PROFILE") == "1";
    private static readonly ConcurrentDictionary<string, (long Ticks, long Calls)> Stages = new(StringComparer.Ordinal);

    public readonly struct Scope(string name, long start) : IDisposable
    {
        public void Dispose()
        {
            if (name is null)
                return;
            var elapsed = Stopwatch.GetTimestamp() - start;
            Stages.AddOrUpdate(name, (elapsed, 1), (_, v) => (v.Ticks + elapsed, v.Calls + 1));
        }
    }

    public static Scope Time(string name) => On ? new Scope(name, Stopwatch.GetTimestamp()) : default;

    /// <summary>The stages, slowest first, as lines; and clears them.</summary>
    public static List<string> Take()
    {
        var lines = Stages.OrderByDescending(kv => kv.Value.Ticks)
            .Select(kv => $"profile {kv.Key}: {kv.Value.Ticks * 1000.0 / Stopwatch.Frequency:F0} ms over {kv.Value.Calls} call(s)").ToList();
        Stages.Clear();
        return lines;
    }
}
