using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="TJunctionFix"/> against a node entries capture
/// (<c>tools/vis/capture_nodeentries.py</c>, <c>NODEENTRIES=&lt;capture&gt;</c>):
/// the entries FixTJunctionEdgeCracks received, through the port, against
/// what it left, every vertex float and index.
/// </summary>
public class TJunctionFixReplay(ITestOutputHelper output)
{
    [Fact]
    public void MatchesValve()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var before = all.Where(c => c.Stage == "FixTJunctions:in").ToList();
        var after = all.Where(c => c.Stage == "FixTJunctions:out").ToList();
        var meshes = before.Select(e => new TJunctionFix.Mesh
        {
            Vertices = [.. e.Vertices],
            Stride = e.Stride,
            Indices = [.. e.Indices],
            Group = BitConverter.ToInt32(e.Raw, 0x40),
            Excluded = (BitConverter.ToUInt64(e.Raw, 0x1b0) & 1) != 0 || e.Raw[0x1a4] != 0,
            SkipOwnNode = e.Raw[0x1a0] != 0,
        }).ToList();
        TJunctionFix.Run(meshes);
        int exact = 0, changed = 0, shown = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            var (o, e) = (meshes[i], after[i]);
            if (!before[i].Indices.SequenceEqual(e.Indices) || before[i].Vertices.Length != e.Vertices.Length)
                changed++;
            var same = o.Indices.SequenceEqual(e.Indices) && o.Vertices.Count == e.Vertices.Length
                       && o.Vertices.Select(BitConverter.SingleToInt32Bits).SequenceEqual(e.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 8)
            {
                var firstIndex = Enumerable.Range(0, Math.Min(o.Indices.Count, e.Indices.Length)).FirstOrDefault(k => o.Indices[k] != e.Indices[k], -1);
                var firstFloat = Enumerable.Range(0, Math.Min(o.Vertices.Count, e.Vertices.Length)).FirstOrDefault(k => BitConverter.SingleToInt32Bits(o.Vertices[k]) != BitConverter.SingleToInt32Bits(e.Vertices[k]), -1);
                output.WriteLine($"entry {i} {Path.GetFileName(e.Material)}: ours {o.Vertices.Count / o.Stride}v {o.Indices.Count / 3}t, valve {e.Vertices.Length / e.Stride}v {e.Indices.Length / 3}t (before {before[i].Vertices.Length / e.Stride}v {before[i].Indices.Length / 3}t); first index diff {firstIndex}, first float diff {firstFloat}"
                                 + (firstFloat >= 0 ? $" ({o.Vertices[firstFloat]:R} vs {e.Vertices[firstFloat]:R})" : ""));
            }
        }
        if (Environment.GetEnvironmentVariable("TJMESHPTR") == "1")
            output.WriteLine("mesh pointers: " + string.Join(" ", before.Select((x, i) => $"{i}:{BitConverter.ToUInt64(x.Raw, 0):x}")));
        // TJDEBUG=<entry>: that entry's vertices where Valve and ours differ, with the snap of the input.
        if (int.TryParse(Environment.GetEnvironmentVariable("TJDEBUG"), out var debug))
        {
            var (o, e, b) = (meshes[debug], after[debug], before[debug]);
            static float Snap(float x) => MathF.Floor((x / 0.03125f) + 0.5f) * 0.03125f;
            int valveOnly = 0, oursOnly = 0, other = 0;
            for (var v = 0; v < e.Vertices.Length / e.Stride; v++)
            {
                var at = v * e.Stride;
                var (vo, ve, vb) = (new[] { o.Vertices[at], o.Vertices[at + 1], o.Vertices[at + 2] }, e.Vertices[at..(at + 3)], b.Vertices[at..(at + 3)]);
                if (vo.SequenceEqual(ve))
                    continue;
                var valveSnapped = ve.SequenceEqual(vb.Select(Snap));
                var oursSnapped = vo.SequenceEqual(vb.Select(Snap));
                if (valveSnapped && vo.SequenceEqual(vb))
                    valveOnly++;
                else if (oursSnapped && ve.SequenceEqual(vb))
                    oursOnly++;
                else
                    other++;
                var tris = Enumerable.Range(0, b.Indices.Length / 3).Where(t => b.Indices[t * 3] == v || b.Indices[(t * 3) + 1] == v || b.Indices[(t * 3) + 2] == v).ToList();
                if (valveOnly + oursOnly + other <= 12)
                    output.WriteLine($"  v{v} before ({string.Join(", ", vb)}) valve ({string.Join(", ", ve)}) ours ({string.Join(", ", vo)}) tris {string.Join(" ", tris.Take(6))}");
            }
            output.WriteLine($"entry {debug}: Valve snapped, ours not {valveOnly}; ours snapped, Valve not {oursOnly}; other {other}");
            // Position-shared vertices: for each vertex only one side snapped, whether another vertex at the same
            // input position was snapped by the other side.
            var byPos = Enumerable.Range(0, b.Vertices.Length / b.Stride).GroupBy(v => (b.Vertices[v * b.Stride], b.Vertices[(v * b.Stride) + 1], b.Vertices[(v * b.Stride) + 2])).ToList();
            bool Moved(IReadOnlyList<float> m, int v) => m[v * b.Stride] != b.Vertices[v * b.Stride] || m[(v * b.Stride) + 1] != b.Vertices[(v * b.Stride) + 1] || m[(v * b.Stride) + 2] != b.Vertices[(v * b.Stride) + 2];
            int mixedValve = 0, mixedOurs = 0, groups = 0;
            foreach (var g in byPos.Where(g => g.Count() > 1))
            {
                groups++;
                if (g.Any(v => Moved(e.Vertices, v)) && !g.All(v => Moved(e.Vertices, v)))
                    mixedValve++;
                if (g.Any(v => Moved(o.Vertices, v)) && !g.All(v => Moved(o.Vertices, v)))
                    mixedOurs++;
            }
            output.WriteLine($"entry {debug}: {groups} positions shared by several vertices; moved only partly: Valve {mixedValve}, ours {mixedOurs}");
            // TJVERTEX=<v>: that vertex's triangles (open edges by Twins) and every entry's open-edge vertex whose
            // snap equals its snap, with the entry's group.
            if (int.TryParse(Environment.GetEnvironmentVariable("TJVERTEX"), out var tv))
            {
                var fresh = before.Select(x => new TJunctionFix.Mesh { Vertices = [.. x.Vertices], Stride = x.Stride, Indices = [.. x.Indices], Group = BitConverter.ToInt32(x.Raw, 0x40) }).ToList();
                var target = new System.Numerics.Vector3(b.Vertices[tv * b.Stride], b.Vertices[(tv * b.Stride) + 1], b.Vertices[(tv * b.Stride) + 2]);
                var st = new System.Numerics.Vector3(Snap(target.X), Snap(target.Y), Snap(target.Z));
                var twinSelf = TJunctionFix.Twins(fresh[debug]);
                output.WriteLine($"  groups: {string.Join(" ", fresh.Select((x, i) => $"{i}:{x.Group}/{before[i].Raw[0x1a0]}"))}");
                for (var t = 0; t < b.Indices.Length; t += 3)
                    if (b.Indices[t] == tv || b.Indices[t + 1] == tv || b.Indices[t + 2] == tv)
                        output.WriteLine($"  tri {t / 3} ({b.Indices[t]} {b.Indices[t + 1]} {b.Indices[t + 2]}) twins {twinSelf[t]} {twinSelf[t + 1]} {twinSelf[t + 2]}");
                for (var m = 0; m < fresh.Count; m++)
                {
                    var tw = TJunctionFix.Twins(fresh[m]);
                    var x = before[m];
                    for (var c = 0; c < x.Indices.Length; c++)
                    {
                        if (tw[c] != -1)
                            continue;
                        foreach (var vv in new[] { x.Indices[c], x.Indices[c - (c % 3) + ((c % 3) + 1) % 3] })
                        {
                            var p = new System.Numerics.Vector3(x.Vertices[vv * x.Stride], x.Vertices[(vv * x.Stride) + 1], x.Vertices[(vv * x.Stride) + 2]);
                            if (System.Numerics.Vector3.Distance(p, target) < 0.1f)
                                output.WriteLine($"  record: entry {m} group {fresh[m].Group} vertex {vv} corner {c} ({p.X}, {p.Y}, {p.Z})");
                        }
                    }
                }
            }
        }
        output.WriteLine($"{meshes.Count} entries ({changed} changed by Valve), {exact} exact");
        Assert.Equal(meshes.Count, exact);
    }
}

/// <summary>
/// BuildNode's closing weld (CMesh_Weld at 1/32) against a node entries
/// capture: the entries Step25d500 left, welded by <c>MeshWeld</c>, against
/// BuildNode's output. <c>NODEWELD_MODE</c> picks the renumbering.
/// </summary>
public class NodeWeldReplay(ITestOutputHelper output)
{
    [Fact]
    public void MatchesValve()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var before = all.Where(c => c.Stage == "Step25d500:out").ToList();
        var after = all.Where(c => c.Stage == "BuildNode:out").ToList();
        var renumber = Environment.GetEnvironmentVariable("NODEWELD_MODE") == "renumber";
        int exact = 0, shown = 0;
        for (var i = 0; i < before.Count; i++)
        {
            var (b, e) = (before[i], after[i]);
            var welds = !(b.Raw[0x1a0] != 0 && b.Raw[0x98] != 0 && (b.Raw[0xc0] & 0x10) != 0);
            var streams = b.Layout.Select(x => new Physics.MeshWeld.Stream(x.Name, x.First, x.Count, false, x.Type)).ToList();
            var (v, idx) = welds ? Physics.MeshWeld.Weld(b.Vertices, b.Stride, b.Indices, streams, 1f / 32f, renumber) : (b.Vertices, b.Indices);
            var same = idx.SequenceEqual(e.Indices) && v.Select(BitConverter.SingleToInt32Bits).SequenceEqual(e.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 6)
                output.WriteLine($"entry {i} {Path.GetFileName(b.Material)}: ours {v.Length / b.Stride}v {idx.Length / 3}t, valve {e.Vertices.Length / e.Stride}v {e.Indices.Length / 3}t (welds {welds})");
        }
        output.WriteLine($"{before.Count} entries, {exact} exact");
        Assert.Equal(before.Count, exact);
    }
}

/// <summary>
/// BuildNode end to end for a map without static props or later-added
/// entries: our entries from the .vmap (<see cref="NodeMeshEntries"/>), the
/// drops (+0x1a5 or attribute bit 34; then Step257b50's bits 10 and 36), the
/// T-junction fix and the closing weld, against BuildNode's captured output.
/// The entry flags and the baked PerVertexLighting come from the capture's
/// BuildNode input (the material-side flags are not ported yet).
/// <c>NODEENTRIES</c> and <c>NODEENTRIES_VMAP</c> as for NodeEntriesFromVmap.
/// </summary>
public class BuildNodeEndToEnd(ITestOutputHelper output)
{
    [Fact]
    public void MatchesValve()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap
            || CS2Fixtures.StockPak() is not { } pak)
            return;
        var all = NodeEntriesFromVmap.Read(path);
        var valveIn = all.Where(c => c.Stage == "BuildNode:in").ToList();
        var valveOut = all.Where(c => c.Stage == "BuildNode:out").ToList();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        IReadOnlyCollection<string> Signature(string material) => content.Read(material + "_c") is { } b
            ? [.. PackageMaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var ours = NodeMeshEntries.FromWorld(DmxBinary.ReadFile(vmap), signature: Signature, rendersAsWorld: NodeEntriesFromVmap.RendersAsWorld(game));
        Assert.Equal(valveIn.Count, ours.Count);

        var meshes = new List<(TJunctionFix.Mesh Mesh, NodeEntriesFromVmap.Captured Valve, NodeMeshEntries.Entry Ours)>();
        for (var i = 0; i < ours.Count; i++)
        {
            var (o, e) = (ours[i], valveIn[i]);
            var v = (float[])o.Vertices.Clone();
            foreach (var st in o.Streams.Where(x => x.Name == "PerVertexLighting"))
                for (var c = 0; c < v.Length / o.Stride; c++)
                    Array.Copy(e.Vertices, c * e.Stride + st.First, v, c * o.Stride + st.First, st.Count);
            var attr = BitConverter.ToUInt64(e.Raw, 0x1b0);
            if (e.Raw[0x1a5] != 0 || (attr & 0x400000000) != 0 || (attr & 0x400) != 0 || (attr & 0x1000000000) != 0)
                continue;
            meshes.Add((new TJunctionFix.Mesh
            {
                Vertices = [.. v], Stride = o.Stride, Indices = [.. o.Indices],
                Group = o.NodeId,
                Excluded = (attr & 1) != 0 || e.Raw[0x1a4] != 0,
                SkipOwnNode = e.Raw[0x1a0] != 0,
            }, e, o));
        }
        TJunctionFix.Run([.. meshes.Select(m => m.Mesh)]);
        Assert.Equal(valveOut.Count, meshes.Count);
        int exact = 0, shown = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            var (m, e, o) = meshes[i];
            var welds = !(e.Raw[0x1a0] != 0 && e.Raw[0x98] != 0 && (e.Raw[0xc0] & 0x10) != 0);
            var (v, idx) = welds ? Physics.MeshWeld.Weld([.. m.Vertices], m.Stride, [.. m.Indices], o.Streams, 1f / 32f, true) : ([.. m.Vertices], [.. m.Indices]);
            var want = valveOut[i];
            var same = idx.SequenceEqual(want.Indices) && v.Select(BitConverter.SingleToInt32Bits).SequenceEqual(want.Vertices.Select(BitConverter.SingleToInt32Bits));
            if (same)
                exact++;
            else if (shown++ < 6)
                output.WriteLine($"entry {i} {Path.GetFileName(want.Material)}: ours {v.Length / m.Stride}v {idx.Length / 3}t, valve {want.Vertices.Length / want.Stride}v {want.Indices.Length / 3}t");
        }
        output.WriteLine($"{meshes.Count} entries out of BuildNode, {exact} exact");
        Assert.Equal(meshes.Count, exact);
    }
}
