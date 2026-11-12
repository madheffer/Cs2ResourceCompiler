using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.Decompile;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// A point_template member's placement back from its copy when the lump printed the copy's
/// placement as "%f" strings (an older compile): the solved placement must print, through the
/// forward's TemplateTransform.Relative, to the lump's text.
/// </summary>
public sealed class TemplatePlacementTests
{
    // ze_sisyphus' template 858 (-15808 -12800 15727, yaw 180) and its point_teleport 857, whose
    // copy ships origin "0.000000 0.000000 128.000000" and angles "-0.000000 0.000000 0.000000".
    [Fact]
    public void PrintedCopy_UnderYaw180_Lands()
    {
        var templateOrigin = new Vector3(-15808, -12800, 15727);
        var templateAngles = new Vector3(0, 180, 0);
        string[] origin = ["0.000000", "0.000000", "128.000000"];
        string[] angles = ["-0.000000", "0.000000", "0.000000"];
        var solved = TemplatePlacement.Solve(templateOrigin, templateAngles, new Vector3(0, 0, 128), Vector3.Zero, origin, angles);
        Assert.NotNull(solved);
        var (o, a) = TemplateTransform.Relative(templateOrigin, templateAngles, solved.Value.Origin, solved.Value.Angles);
        Assert.Equal(origin, new[] { SettleWorld.Fixed6(o.X), SettleWorld.Fixed6(o.Y), SettleWorld.Fixed6(o.Z) });
        Assert.Equal(angles, new[] { SettleWorld.Fixed6(a.X), SettleWorld.Fixed6(a.Y), SettleWorld.Fixed6(a.Z) });
    }
}
