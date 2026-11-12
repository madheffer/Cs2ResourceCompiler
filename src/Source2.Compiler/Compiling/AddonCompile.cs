using Source2.Compiler.Materials;
using Source2.Compiler.RcTexture;

namespace Source2.Compiler.Compiling;

/// <summary>
/// Compiles an addon's own sources into its game folder, as resourcecompiler does when it is run
/// on each asset (GROUND_TRUTH 255: a map compile never compiles the materials it references, so
/// they are compiled before it). Each <c>.vmat</c> and <c>.vtex</c> under
/// <c>content/csgo_addons/&lt;addon&gt;</c> goes through <see cref="ResourceCompiler"/> with RC's
/// search order (the addon, then <c>csgo</c>, then <c>core</c>) and its compiled file and child
/// textures are written to <c>game/csgo_addons/&lt;addon&gt;</c> at their content paths: for
/// cx_full.vmat that is the material, its children beside their images, and the default textures
/// under <c>materials/default/</c> (resourcecompiler 1103, %TEMP%/addon_ref/s2cmat_1105).
/// </summary>
/// <remarks>
/// Every source is compiled; RC's own up-to-date test (rc 18016f170, GROUND_TRUTH 255) skips a
/// file whose existing output's RED2 still matches. That changes which files RC rewrites, not
/// their bytes, so here a file whose bytes are unchanged is left as it is instead.
/// </remarks>
public static class AddonCompile
{
    /// <summary>The source types compiled: materials and textures (what a map references).</summary>
    public static readonly IReadOnlyList<string> SourceExtensions = [".vmat", ".vtex"];

    /// <summary>What one source gave.</summary>
    /// <param name="Source">Its content path.</param>
    /// <param name="Written">Files written (changed or new), content paths.</param>
    /// <param name="Unchanged">Files already there with the same bytes.</param>
    /// <param name="Diagnostics">The compile's diagnostics.</param>
    public sealed record SourceResult(string Source, IReadOnlyList<string> Written, IReadOnlyList<string> Unchanged,
                                      IReadOnlyList<CompileDiagnostic> Diagnostics)
    {
        /// <summary>True when no error diagnostic was raised.</summary>
        public bool Succeeded => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Where an addon compile reads and writes.</summary>
    /// <param name="ContentRoot">The addon's content folder (its sources).</param>
    /// <param name="GameRoot">The addon's game folder (the outputs).</param>
    /// <param name="SearchPath">The search path RED2 records for the addon: <c>csgo_addons/&lt;addon&gt;</c>.</param>
    /// <param name="CsgoContent">content/csgo, searched after the addon.</param>
    /// <param name="CoreContent">content/core, searched last.</param>
    /// <param name="ShaderPackage">game/csgo/shaders_pc_dir.vpk.</param>
    public sealed record Layout(string ContentRoot, string GameRoot, string SearchPath, string CsgoContent, string CoreContent,
                                string ShaderPackage)
    {
        /// <summary>An addon of the CS2 install at <paramref name="cs2"/> (the folder holding game and content).</summary>
        public static Layout FromInstall(string cs2, string addon) => new(
            Path.Combine(cs2, "content", "csgo_addons", addon), Path.Combine(cs2, "game", "csgo_addons", addon),
            "csgo_addons/" + addon, Path.Combine(cs2, "content", "csgo"), Path.Combine(cs2, "content", "core"),
            Path.Combine(cs2, "game", "csgo", "shaders_pc_dir.vpk"));
    }

    /// <summary>
    /// Compile <paramref name="addon"/>'s sources (all of them, or the content paths in
    /// <paramref name="only"/>) and write the outputs, unless <paramref name="dryRun"/>.
    /// </summary>
    /// <param name="cs2">The CS2 install folder (the one holding <c>game</c> and <c>content</c>).</param>
    /// <param name="addon">The addon's folder name.</param>
    /// <param name="only">Content paths to compile, or null for every source of <see cref="SourceExtensions"/>.</param>
    /// <param name="vendor">Whose BC7 to reproduce: what RC writes on that CPU vendor.</param>
    /// <param name="dryRun">Compile but write nothing.</param>
    public static IReadOnlyList<SourceResult> Run(string cs2, string addon, IEnumerable<string>? only = null,
                                                  Bc7Vendor vendor = Bc7Vendor.Amd, bool dryRun = false)
        => Run(Layout.FromInstall(cs2, addon), only, vendor, dryRun);

    /// <summary>Compile the sources of <paramref name="layout"/>'s addon; see the other overload.</summary>
    public static IReadOnlyList<SourceResult> Run(Layout layout, IEnumerable<string>? only = null,
                                                  Bc7Vendor vendor = Bc7Vendor.Amd, bool dryRun = false)
    {
        var contentRoot = layout.ContentRoot;
        var gameRoot = layout.GameRoot;
        if (!Directory.Exists(contentRoot))
            throw new DirectoryNotFoundException($"No addon content at {contentRoot}.");
        var settings = new MaterialCompileSettings
        {
            ShaderPackage = layout.ShaderPackage,
            Mounts = [new("csgo", new DirectoryContentSource(layout.CsgoContent)),
                      new("core", new DirectoryContentSource(layout.CoreContent))],
            Bc7Vendor = vendor,
        };
        var content = new DirectoryContentSource(contentRoot);
        var compiler = ResourceCompiler.CreateDefault();
        var sources = only?.Select(p => p.Replace('\\', '/').TrimStart('/')).ToList() ?? Sources(contentRoot);
        var results = new List<SourceResult>();
        foreach (var source in sources)
        {
            var bytes = content.Read(source);
            if (bytes is null)
            {
                results.Add(new(source, [], [], [new(DiagnosticSeverity.Error, "No such source in the addon's content.", source)]));
                continue;
            }
            var result = compiler.Compile(new CompileRequest(source, bytes)
            {
                SearchPath = layout.SearchPath,
                Content = content,
                Settings = settings,
            });
            var written = new List<string>();
            var unchanged = new List<string>();
            if (result.Succeeded)
                foreach (var (path, output) in result.Children.Select(c => (c.Path, c.Bytes)).Append((result.OutputPath, result.Output!)))
                {
                    if (!Io.SafePath.TryJoinUnderRoot(gameRoot, path, out var full))
                        throw new InvalidDataException($"{source}: output path {path} leaves the addon's game folder.");
                    if (File.Exists(full) && File.ReadAllBytes(full).AsSpan().SequenceEqual(output))
                    {
                        unchanged.Add(path);
                        continue;
                    }
                    written.Add(path);
                    if (dryRun)
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    var temp = full + ".s2c-tmp";
                    File.WriteAllBytes(temp, output);
                    File.Move(temp, full, overwrite: true);
                }
            results.Add(new(source, written, unchanged, result.Diagnostics));
        }
        return results;
    }

    /// <summary>Every source of <see cref="SourceExtensions"/> under <paramref name="contentRoot"/>, as content
    /// paths in ordinal order; maps are left to compile-map.</summary>
    public static List<string> Sources(string contentRoot)
        => Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(contentRoot, f).Replace('\\', '/'))
            .Where(p => SourceExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)
                        && !p.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
}
