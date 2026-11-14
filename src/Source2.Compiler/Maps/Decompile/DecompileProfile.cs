using System.Collections.Concurrent;
using System.Diagnostics;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// Inclusive wall time and call counts per named stage of a decompile, kept only when the
/// environment sets DECOMPILE_PROFILE=1 (a nested stage's time is in its caller's too).
/// </summary>
public static class DecompileProfile
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

    /// <summary>Counts one event under <paramref name="name"/> (no time).</summary>
    public static void Count(string name)
    {
        if (On)
            Stages.AddOrUpdate(name, (0, 1), (_, v) => (v.Ticks, v.Calls + 1));
    }

    /// <summary>The power-of-ten bucket a count falls in, for <see cref="Count"/> names ("<1e4").</summary>
    public static string Decade(long n) => n < 10 ? "<1e1" : $"<1e{(int)Math.Floor(Math.Log10(n)) + 1}";

    /// <summary>The stages, slowest first, as lines; and clears them.</summary>
    public static List<string> Take()
    {
        var lines = Stages.OrderByDescending(kv => kv.Value.Ticks)
            .Select(kv => $"profile {kv.Key}: {kv.Value.Ticks * 1000.0 / Stopwatch.Frequency:F0} ms over {kv.Value.Calls} call(s)").ToList();
        Stages.Clear();
        return lines;
    }
}
