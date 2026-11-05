using Source2.Compiler.Compiling;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="AddonCompile"/> (8.b1, GROUND_TRUTH 255): an addon's materials compiled into its game folder are
/// resourcecompiler's files, children and default textures included. The sources and RC's outputs are main's
/// RcReference/materials probes (compiled under csgo_addons/vpkstuff_rc_probe); on 2026-11-05 the same two
/// materials compiled by RC 1103 in s2c_rc_probe gave the same eight files byte for byte. Needs CS2's shaders and
/// content (CS2Fixtures).
/// </summary>
public class AddonCompileTests
{
    private static string Probes()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null && !Directory.Exists(Path.Combine(dir, "RcReference")); i++)
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "RcReference", "materials");
    }

    [Fact]
    public void Materials_WriteResourceCompilersFiles()
    {
        if (CS2Fixtures.StockPak() is not { } pak) { CS2Fixtures.Skip("shaders_pc_dir.vpk"); return; }
        var csgo = Path.GetDirectoryName(pak)!;
        var temp = Directory.CreateTempSubdirectory("s2c_addon_");
        try
        {
            var content = Path.Combine(temp.FullName, "content");
            var game = Path.Combine(temp.FullName, "game");
            Directory.CreateDirectory(Path.Combine(content, "materials", "s2cmat"));
            foreach (var name in new[] { "cx_full.vmat", "cx_color.vmat", "color.png", "normal.png", "rough.png", "metal.png", "ao.png" })
                File.Copy(Path.Combine(Probes(), name), Path.Combine(content, "materials", "s2cmat", name));
            var layout = AddonCompile.Layout.FromInstall(Path.GetDirectoryName(Path.GetDirectoryName(csgo)!)!, "unused") with
            {
                ContentRoot = content, GameRoot = game, SearchPath = "csgo_addons/vpkstuff_rc_probe",
            };

            var results = AddonCompile.Run(layout);

            Assert.Equal(["materials/s2cmat/cx_color.vmat", "materials/s2cmat/cx_full.vmat"], results.Select(r => r.Source));
            Assert.All(results, r => Assert.True(r.Succeeded, string.Join("; ", r.Diagnostics.Select(d => d.Message))));
            var written = results.SelectMany(r => r.Written).Distinct().Order(StringComparer.Ordinal).ToList();
            Assert.Equal([
                "materials/default/default_ao_tga_559f1ac6.vtex_c", "materials/default/default_normal_tga_7be61377.vtex_c",
                "materials/s2cmat/ao_png_e71012b2.vtex_c", "materials/s2cmat/color_png_1c8a88c2.vtex_c",
                "materials/s2cmat/color_png_b65d74e9.vtex_c", "materials/s2cmat/cx_color.vmat_c",
                "materials/s2cmat/cx_full.vmat_c", "materials/s2cmat/normal_png_e21b0a5a.vtex_c"], written);
            foreach (var path in written)
                Assert.True(File.ReadAllBytes(Path.Combine(game, path)).AsSpan().SequenceEqual(
                                File.ReadAllBytes(Path.Combine(Probes(), "compiled", Path.GetFileName(path)))), $"{path} differs from RC's");

            // A second run finds every file already right and writes nothing.
            var again = AddonCompile.Run(layout);
            Assert.All(again, r => Assert.Empty(r.Written));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
