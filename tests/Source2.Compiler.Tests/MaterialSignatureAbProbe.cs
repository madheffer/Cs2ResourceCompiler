using ValvePak;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The input and depth signatures of every pak01 material as the resource library reads them
/// (<see cref="PackageMaterialAuthor"/>, our container and KV3 reader) and as the map compiler's VRF-based
/// <c>MaterialAuthor</c> reads them. Lists the materials where they differ, with both readings.
/// <c>MATSIGAB=1[|limit]</c>, with <c>CS2_DIR</c> or the default install.
/// </summary>
public sealed class MaterialSignatureAbProbe(ITestOutputHelper output)
{
    [Fact]
    public void BothReaders_SameSignatures()
    {
        if (Environment.GetEnvironmentVariable("MATSIGAB") is not { Length: > 0 } spec)
            return;
        var limit = spec.Split('|') is [_, var l] ? int.Parse(l) : int.MaxValue;
        using var pak = new Package();
        pak.Read(CS2Fixtures.StockPak() ?? throw new InvalidOperationException("no pak01_dir.vpk (set CS2_DIR)"));
        int n = 0, same = 0, shown = 0;
        static string Text(Func<IReadOnlyList<Source2ContainerAuthor.MaterialAuthoring.InputSignatureElement>> read)
        {
            try { return string.Join(" ", read().Select(e => $"{e.Name}/{e.Semantic}/{e.D3DSemanticName}{e.D3DSemanticIndex}")); }
            catch (Exception ex) { return $"<throws {ex.GetType().Name}>"; }
        }
        foreach (var e in Io.VpkEntries.ByExtension(pak)["vmat_c"].Take(limit))
        {
            pak.ReadEntry(e, out var bytes);
            n++;
            var oi = Text(() => PackageMaterialAuthor.ExtractInputSignature(bytes));
            var li = Text(() => MaterialAuthor.ExtractInputSignature(bytes));
            var od = Text(() => PackageMaterialAuthor.ExtractDepthSignature(bytes));
            var ld = Text(() => MaterialAuthor.ExtractDepthSignature(bytes));
            if (oi == li && od == ld) { same++; continue; }
            if (shown++ < 25)
                output.WriteLine($"{e.GetFullPath()}\n  input ours  {oi}\n  input vrf   {li}\n  depth ours  {od}\n  depth vrf   {ld}");
        }
        output.WriteLine($"{same} of {n} materials read the same");
    }
}
