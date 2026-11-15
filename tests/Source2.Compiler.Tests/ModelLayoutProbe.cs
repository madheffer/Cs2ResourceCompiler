using Source2.Compiler.Meshes;
using Source2.Compiler.KeyValues;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>MODELLAYOUT=models/a.vmdl;models/b.vmdl: each draw call's material and vertex layout fields (stock pak). Prints only.</summary>
public sealed class ModelLayoutProbe(ITestOutputHelper output)
{
    [Fact]
    public void Layouts()
    {
        if (Environment.GetEnvironmentVariable("MODELLAYOUT") is not { } list || CS2Fixtures.StockPak() is not { } pak)
            return;
        using var content = new GameContent(pak);
        foreach (var name in list.Split(';'))
        {
            if (content.LoadedModel(name + "_c") is not { } model)
            {
                output.WriteLine($"{name}: not found");
                continue;
            }
            foreach (var (mesh, _, _, lod) in model.EmbeddedMeshesAndLod())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var call in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.Vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        output.WriteLine($"{name} lod {lod} {call.GetStringProperty("m_material")}: " +
                                         string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}")));
                    }
        }
    }
}
