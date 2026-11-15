using Source2.Compiler.Meshes;
using Source2.Compiler.KeyValues;
using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// PROPMESHES=capture_propmeshes.py file, PROPMESH_MODEL=models/x.vmdl, PROPMESH_MATERIAL=substring: each captured
/// mesh whose material contains the substring beside our NodePropEntries.Load of the model's draw with that material,
/// float by float (position, normal, tangent, texcoord). Prints only.
/// </summary>
public sealed class PropMeshDirectProbe(ITestOutputHelper output)
{
    [Fact]
    public void Compare()
    {
        if (Environment.GetEnvironmentVariable("PROPMESHES") is not { } path || Environment.GetEnvironmentVariable("PROPMESH_MODEL") is not { } modelName
            || Environment.GetEnvironmentVariable("PROPMESH_MATERIAL") is not { } want || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        var model = content.LoadedModel(modelName + "_c")!;
        NodePropEntries.PropMesh? ours = null;
        foreach (var (mesh, _, _, _) in model.EmbeddedMeshesAndLod())
            foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                foreach (var call in so.GetArray("m_drawCalls"))
                {
                    if (!(call.GetStringProperty("m_material") ?? "").Contains(want, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var vb = mesh.Vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                    var ib = mesh.Vbib.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                    int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                    var start = call.GetInt32Property("m_nStartIndex");
                    var count = call.GetInt32Property("m_nIndexCount");
                    var bas = call.GetInt32Property("m_nBaseVertex");
                    var lo = Enumerable.Range(start, count).Min(Index) + bas;
                    var n = Enumerable.Range(start, count).Max(Index) + bas - lo + 1;
                    ours = NodePropEntries.Load(vb, lo, n, NodePropEntries.Attributes(call.GetStringProperty("m_material") ?? "", content.Read));
                    output.WriteLine($"ours: {n} vertices, stride {ours.Stride}, normal format {vb.InputLayoutFields.First(f => f.SemanticName == "NORMAL").Format}");
                }
        using var r = new BinaryReader(File.OpenRead(path));
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var head = System.Text.Json.JsonDocument.Parse(r.ReadBytes(r.ReadInt32())).RootElement;
            var blob = r.ReadBytes(r.ReadInt32());
            if (head.GetProperty("ev").GetString() != "mesh" || !head.GetProperty("material").GetString()!.Contains(want, StringComparison.OrdinalIgnoreCase))
                continue;
            int nv = head.GetProperty("nv").GetInt32(), stride = head.GetProperty("stride").GetInt32();
            var v = new float[nv * stride];
            Buffer.BlockCopy(blob, 0, v, 0, v.Length * 4);
            var streams = head.GetProperty("streams").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()}@{x.GetProperty("first").GetInt32()}x{x.GetProperty("count").GetInt32()}");
            output.WriteLine($"valve: {nv} vertices, stride {stride}: {string.Join(" ", streams)}");
            if (ours is null)
                return;
            var diffs = new Dictionary<int, int>();
            for (var k = 0; k < Math.Min(nv, ours.Vertices.Length / ours.Stride); k++)
                for (var f = 0; f < Math.Min(stride, ours.Stride); f++)
                    if (v[(k * stride) + f] != ours.Vertices[(k * ours.Stride) + f])
                        diffs[f] = diffs.GetValueOrDefault(f) + 1;
            output.WriteLine("  floats differing (index: vertices): " + string.Join(", ", diffs.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}")));
            for (var k = 0; k < 3; k++)
                output.WriteLine($"  v{k} valve {string.Join(" ", v.Skip(k * stride).Take(stride).Select(x => x.ToString("R")))}\n     ours  {string.Join(" ", ours.Vertices.Skip(k * ours.Stride).Take(ours.Stride).Select(x => x.ToString("R")))}");
            return;
        }
    }
}
