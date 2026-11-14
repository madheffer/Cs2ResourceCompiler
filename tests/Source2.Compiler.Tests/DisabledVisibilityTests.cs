using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// A map with visibility disabled (worldspawn pvstype 0): visbuilder's VisEntry writes FUN_180047cf0's fixed vis
/// (WorldNodeFiles.DisabledVisibility). Valve's reference is a -world -vis -fshallow compile of
/// ze_obj_christmas_factory (%TEMP%/fresh_compile/xmas_vis.vpk, compile_fresh.py; ledger 293); the test
/// returns early without it.
/// </summary>
public class DisabledVisibilityTests
{
    [Fact]
    public void DisabledVisIsValves()
    {
        var path = Path.Combine(Path.GetTempPath(), "fresh_compile", "xmas_vis.vpk");
        if (!File.Exists(path))
            return;
        using var pak = new ValvePak.Package();
        pak.Read(path);
        var entry = pak.FindEntry("maps/ze_obj_christmas_factory/world_visibility.vvis_c");
        Assert.NotNull(entry);
        pak.ReadEntry(entry, out var bytes);
        var valve = VoxelVisibilityReader.Read(bytes);
        var ours = WorldNodeFiles.DisabledVisibility();
        Assert.Equal(valve.WriteVxvs(), ours.WriteVxvs());
        Assert.Equal(valve.BaseClusterCount, ours.BaseClusterCount);
        Assert.Equal(valve.PVSBytesPerCluster, ours.PVSBytesPerCluster);
        Assert.Equal(valve.MinBounds, ours.MinBounds);
        Assert.Equal(valve.MaxBounds, ours.MaxBounds);
        Assert.Equal(valve.GridSize, ours.GridSize);
        Assert.Equal(valve.SkyVisibilityCluster, ours.SkyVisibilityCluster);
        Assert.Equal(valve.SunVisibilityCluster, ours.SunVisibilityCluster);
    }
}
