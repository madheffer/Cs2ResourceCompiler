using System.Numerics;
using Source2.Compiler;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <c>DECOMPILE_FRAME=&lt;addon&gt;|&lt;map&gt;</c>: for each prefab of the map, its map's nodes placed through
/// SettleWorld.PrefabPlacement in the map's own frame and in frames shifted by a vector c (the
/// node at L - c, the prefab at o + R c, the same placement in exact arithmetic): how many nodes each
/// frame lands bit for bit on the true one's placement. Measures how much the frame decides.
/// </summary>
public class MapDecompilerFrameProbe(ITestOutputHelper output)
{
    [Fact]
    public void Frames()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_FRAME") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var doc = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(doc, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        foreach (var prefab in doc.OfType("CMapPrefab"))
        {
            var world = prefab.Get<DmxBinary.Element>(Maps.MapPrefabs.WorldKey);
            if (world is null)
                continue;
            var nodes = Walk(world).Where(MapEntities.CarriesGameKeys).ToList();
            var o = prefab.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var a = prefab.GetValue<Vector3>("angles") ?? Vector3.Zero;
            var truth = nodes.Select(n => Place(n, Vector3.Zero, o, a)).ToList();
            var r = Maps.SettleWorld.AngleMatrixDouble(a, Vector3.Zero);
            Vector3 Turn(Vector3 v) => Maps.MapMeshes.Transform(r, v);
            var shifts = new List<Vector3> { Vector3.Zero, new(1, 0, 0), new(0, 1, 0), new(16, 16, 0), new(0.5f, 0, 0), new(-1024, 512, 0), -o };
            foreach (var c in shifts)
            {
                // The same placement in exact arithmetic: nodes at L - c, the prefab at o + R c.
                var o2 = o + Turn(c);
                var landed = nodes.Select((n, i) => Place(n, c, o2, a) == truth[i]).Count(x => x);
                output.WriteLine($"prefab {prefab.GetValue<int>("nodeID")} at {o} turned {a}: frame shifted {c} lands {landed} of {nodes.Count}");
            }
        }
    }

    private static (Vector3, Vector3) Place(DmxBinary.Element node, Vector3 shift, Vector3 origin, Vector3 angles)
    {
        var prefab = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
        prefab.Attributes["origin"] = origin;
        prefab.Attributes["angles"] = angles;
        var local = (node.GetValue<Vector3>("origin") ?? Vector3.Zero) - shift;
        return Maps.SettleWorld.PrefabPlacement(local, node.GetValue<Vector3>("angles") ?? Vector3.Zero, [prefab]);
    }

    private static IEnumerable<DmxBinary.Element> Walk(DmxBinary.Element e)
    {
        foreach (var c in e.GetElements("children"))
        {
            yield return c;
            foreach (var d in Walk(c))
                yield return d;
        }
    }
}
