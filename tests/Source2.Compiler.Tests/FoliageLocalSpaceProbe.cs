using Source2.Compiler.Maps;
using ValveResourceFormat.Serialization.KeyValues;
using ValvePak;
using ValveResourceFormat;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>FOLIAGEPROBE=1</c>): the stock materials that answer
/// NeedsLocalSpaceVertices (0x3b7ef4e7) through their shader's static combo
/// (<see cref="MaterialAttributes"/>), and the stock models that draw with
/// one, for a specimen of AddStaticProps' prop flag 8 (OPEN 1.b1).
/// </summary>
public class FoliageLocalSpaceProbe(ITestOutputHelper output)
{
    /// <summary><c>AGGDUMP=&lt;vpk&gt;|&lt;node file&gt;|&lt;index&gt;</c>: one m_aggregateSceneObjects entry as KV3 text.</summary>
    [Fact]
    public void DumpAggregate()
    {
        if (Environment.GetEnvironmentVariable("AGGDUMP") is not { Length: > 0 } spec || spec.Split('|') is not [var vpkPath, var file, var index])
            return;
        using var package = new Package();
        package.Read(vpkPath);
        package.ReadEntry(package.FindEntry(file)!, out var bytes);
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        ValveKeyValue.KVObject data = ((ValveResourceFormat.ResourceTypes.KeyValuesOrNTRO)resource.DataBlock!).Data;
        var agg = data.GetArray("m_aggregateSceneObjects")[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)];
        output.WriteLine($"allFlags {agg["m_allFlags"]} ({agg["m_allFlags"]?.ValueType}) anyFlags {agg["m_anyFlags"]} model {agg["m_renderableModel"]}");
        foreach (var mesh in agg.GetArray("m_aggregateMeshes"))
            output.WriteLine($"  objectFlags {mesh["m_objectFlags"]} ({mesh["m_objectFlags"]?.ValueType}) draw {mesh["m_nDrawCallIndex"]}");
    }

    [Fact]
    public void Explore()
    {
        if (Environment.GetEnvironmentVariable("FOLIAGEPROBE") != "1" || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak, Path.GetDirectoryName(pak)!);
        // FOLIAGEMODEL=<model path>: its material groups and each LOD's draw materials.
        if (Environment.GetEnvironmentVariable("FOLIAGEMODEL") is { Length: > 0 } modelPath)
        {
            var model = content.LoadedModel(modelPath + "_c")!;
            foreach (var g in model.Data.GetArray("m_materialGroups") ?? [])
                output.WriteLine($"group {g.GetStringProperty("m_name")}: {string.Join(", ", g.GetArray<string>("m_materials"))}");
            output.WriteLine($"m_nDefaultMeshGroupMask / skin keys: {string.Join(", ", model.Data.Keys.Where(k => k.Contains("Group", StringComparison.OrdinalIgnoreCase) || k.Contains("skin", StringComparison.OrdinalIgnoreCase)))}");
            foreach (var (mesh, _, name, lod) in model.GetEmbeddedMeshesAndLoD())
                output.WriteLine($"mesh {name} lod 0x{lod:x}: {string.Join(", ", mesh.Data.GetArray("m_sceneObjects").SelectMany(s => s.GetArray("m_drawCalls")).Select(c => c.GetStringProperty("m_material")))}");
            return;
        }
        using var package = new Package();
        package.Read(pak);
        var key = MeshEntryFlags.Key("NeedsLocalSpaceVertices");
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaders = new SortedDictionary<string, (int Yes, int No)>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmat_c") ?? [])
        {
            var path = entry.GetFullPath()[..^2];
            if (content.Material(path) is not { } info)
                continue;
            var shader = Path.GetFileNameWithoutExtension(info.Shader ?? "");
            if (!shader.Contains("foliage", StringComparison.OrdinalIgnoreCase) && !shader.Contains("liquid", StringComparison.OrdinalIgnoreCase))
                continue;
            var yes = MaterialAttributes.Of(info, content.Shaders, content.TextureSize).Bool(key) == true;
            var (y, n) = shaders.GetValueOrDefault(shader);
            shaders[shader] = yes ? (y + 1, n) : (y, n + 1);
            if (yes)
                local.Add(path);
        }
        foreach (var (shader, (y, n)) in shaders)
            output.WriteLine($"{shader}: {y} answer true, {n} false");
        var shown = 0;
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var refs = resource.ExternalReferences?.ResourceRefInfoList.Select(r => r.Name).ToList() ?? [];
            var used = refs.Where(local.Contains).ToList();
            if (used.Count == 0)
                continue;
            var others = refs.Count(r => r.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase)) - used.Count;
            if (shown++ < 60)
                output.WriteLine($"{entry.GetFullPath()}: {string.Join(", ", used)} (+{others} other materials)");
        }
        output.WriteLine($"{local.Count} local-space materials, {shown} models");
    }
}
