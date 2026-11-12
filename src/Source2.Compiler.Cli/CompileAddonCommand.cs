using Source2.Compiler.Compiling;
using Source2.Compiler.RcTexture;

namespace Source2.Compiler.Cli;

/// <summary><c>s2c compile-addon</c>: a thin shell over <see cref="AddonCompile.Run"/>.</summary>
internal static class CompileAddonCommand
{
    public static int Run(string[] a)
    {
        string? addon = null, cs2 = Environment.GetEnvironmentVariable("CS2_DIR");
        var only = new List<string>();
        var vendor = Bc7Vendor.Amd;
        var dryRun = false;
        for (var i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--cs2" when i + 1 < a.Length: cs2 = a[++i]; break;
                case "--only" when i + 1 < a.Length: only.Add(a[++i]); break;
                case "--bc7" when i + 1 < a.Length:
                    vendor = a[++i].ToLowerInvariant() switch
                    {
                        "amd" => Bc7Vendor.Amd,
                        "intel" => Bc7Vendor.Intel,
                        var v => throw new ArgumentException($"--bc7 takes amd or intel, not '{v}'."),
                    };
                    break;
                case "-n" or "--dry-run": dryRun = true; break;
                default:
                    if (a[i].StartsWith('-') || addon is not null)
                        throw new ArgumentException($"compile-addon: unexpected argument '{a[i]}'.");
                    addon = a[i];
                    break;
            }
        }
        if (addon is null)
            throw new ArgumentException("compile-addon needs the addon's name.");
        if (string.IsNullOrEmpty(cs2))
            throw new ArgumentException("compile-addon needs --cs2 <CS2 install dir> (or CS2_DIR).");

        var results = AddonCompile.Run(cs2, addon, only.Count > 0 ? only : null, vendor, dryRun);
        var failed = 0;
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Source}: {(r.Succeeded ? "ok" : "FAILED")}, {r.Written.Count} written, {r.Unchanged.Count} unchanged");
            foreach (var w in r.Written)
                Console.WriteLine($"    {(dryRun ? "would write" : "wrote")} {w}");
            foreach (var u in r.Unchanged)
                Console.WriteLine($"    same {u}");
            foreach (var d in r.Diagnostics)
                Console.WriteLine($"    {d.Severity.ToString().ToLowerInvariant()}: {d.Message}");
            if (!r.Succeeded)
                failed++;
        }
        Console.WriteLine($"{results.Count} sources, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
