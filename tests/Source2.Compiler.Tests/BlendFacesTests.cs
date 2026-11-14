using System.Numerics;
using Source2.Compiler.Dmx;
using Source2.Compiler.Maps.Decompile;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// A painted, subdivided world mesh back from its package (BlendFaces), checked on the true Hammer source
/// ze_hold_em_p's node 409 (36 slab faces of a two-layer blend, 35 of them subdivided to 128 triangles each,
/// with hr_conc_i_tintdark and concrete_9 faces at lightmapScaleBias 0 and 1). WorldPhysicsAuthorTests with
/// WPBUILD_FLATTEN=409|bytes measured that this mesh written flat (its baked triangles as faces, per-face bias,
/// per-vertex paint at 8 bits) builds Valve's world physics byte for byte; these tests pin the two inputs a
/// decompile has to recover for that.
/// </summary>
public sealed class BlendFacesTests(ITestOutputHelper output)
{
    private static (DmxBinary.Element Node, string Package)? Node409()
    {
        var source = MapFixtures.VmapSource("s2c_rc_probe", "ze_hold_em_p");
        if (source is null)
            return null;
        var package = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..", "game", "csgo_addons", "s2c_rc_probe", "maps", "ze_hold_em_p.vpk"));
        if (!File.Exists(package))
            return null;
        var doc = DmxBinary.ReadFile(source);
        return (doc.Elements.First(e => e.GetValue<int>("nodeID") == 409), package);
    }

    private static int[] FaceStream(DmxBinary.Element meshData, string name)
        => (meshData.Get<DmxBinary.Element>("faceData")?.GetElements("streams").FirstOrDefault(x => x.Name.Split(':')[0] == name)?.Get<object?[]>("data") ?? [])
            .Select(x => x is int i ? i : 0).ToArray();

    /// <summary>The mesh's (bias, material) face sets in the forward's sorted order give back its biases.</summary>
    [Fact]
    public void BiasesFromThePieceOrder()
    {
        if (Node409() is not { } fixture)
            return;
        var md = fixture.Node.Get<DmxBinary.Element>("meshData")!;
        var faceData = (md.Get<object?[]>("faceDataIndices") ?? []).Select(x => x is int i ? i : 0).ToArray();
        var (materials, biases) = (FaceStream(md, "materialindex"), FaceStream(md, "lightmapScaleBias"));
        var sets = faceData.Select(d => (Bias: biases[d], Material: materials[d])).Distinct().Order().ToList();
        output.WriteLine("sets: " + string.Join(" ", sets.Select(s => $"({s.Bias},{s.Material})")));
        Assert.Equal(sets.Select(s => s.Bias), BlendFaces.AssignBiases([.. sets.Select(s => s.Material)]));
    }

    /// <summary>
    /// The render's VertexPaintBlendParams at each baked corner of the mesh is the bake's own paint at 8 bits: the
    /// paint the decompile writes is the paint the physics split read.
    /// </summary>
    [Fact]
    public void RenderPaintIsTheBakesAtEightBits()
    {
        if (Node409() is not { } fixture)
            return;
        var md = fixture.Node.Get<DmxBinary.Element>("meshData")!;
        var names = (md.Get<object?[]>("materials") ?? []).Select(x => x as string ?? "").ToArray();
        var faceData = (md.Get<object?[]>("faceDataIndices") ?? []).Select(x => x is int i ? i : 0).ToArray();
        var materials = FaceStream(md, "materialindex");
        var toWorld = Maps.MapMeshes.Local(fixture.Node);
        var cut = Maps.SubdivisionBake.Bake(md, p => Maps.MapMeshes.Transform(toWorld, p));
        Assert.NotNull(cut.Paint);
        var render = BlendFaces.Read(MapDecompiler.ReadPackage(fixture.Package), "ze_hold_em_p");
        var (same, differ, missing) = (0, 0, 0);
        var maxOff = 0f;
        for (var t = 0; t < cut.Faces.Count; t++)
        {
            var material = names[materials[faceData[cut.Faces[t]]]];
            if (!render.Has(material))
                continue;
            // The bake's positions stay in the mesh's space; the physics moves them after (TessellatedPieces).
            Vector3[] p = [.. Enumerable.Range(0, 3).Select(k => Maps.MapMeshes.Transform(toWorld, cut.Positions[cut.Indices[t * 3 + k]]))];
            var normal = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
            for (var k = 0; k < 3; k++)
            {
                var bake = cut.Paint![t * 3 + k];
                // As the compile packs it (ConvertFloatToChannel, truncated) against what the decompile read back.
                static byte[] Packed(Vector4 v) => [Maps.WorldNodeModels.Unorm8(v.X), Maps.WorldNodeModels.Unorm8(v.Y), Maps.WorldNodeModels.Unorm8(v.Z), Maps.WorldNodeModels.Unorm8(v.W)];
                if (render.At(material, p[k], normal) is not { } got)
                    missing++;
                else if (Packed(got).SequenceEqual(Packed(bake)) && (got - bake).Length() <= 1f / 255f)
                    same++;
                else
                {
                    differ++;
                    maxOff = MathF.Max(maxOff, (got - bake).Length());
                    if (differ <= 5)
                        output.WriteLine($"triangle {t} corner {k} {Path.GetFileNameWithoutExtension(material)} at {p[k]}: render {got * 255f} bake {bake * 255f}");
                }
            }
        }
        output.WriteLine($"render paint at the baked corners: {same} the bake's at 8 bits, {differ} other (max off {maxOff}), {missing} without a render corner");
        Assert.True(same > 0);
        Assert.Equal(0, differ + missing);
    }
}
