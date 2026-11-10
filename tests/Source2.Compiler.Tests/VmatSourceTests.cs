using System.Text;
using Source2.Compiler.Materials;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="VmatSource"/>: KV1 values typed the way tier0 types them (1800cf320) and the
/// attribute tables as rc 18010f480 files them (GROUND_TRUTH 264). The RC byte checks of the same
/// rules are the s1i_* probes in <see cref="MaterialCompilerTests"/>.
/// </summary>
public class VmatSourceTests
{
    [Theory]
    [InlineData("0", VmatSource.Kv1Kind.Int)]
    [InlineData("-3", VmatSource.Kv1Kind.Int)]
    [InlineData("+7", VmatSource.Kv1Kind.Int)]
    [InlineData("1.000", VmatSource.Kv1Kind.Double)]
    [InlineData("5.", VmatSource.Kv1Kind.Double)]
    [InlineData(".5", VmatSource.Kv1Kind.Double)]
    [InlineData("1e3", VmatSource.Kv1Kind.Double)]
    [InlineData("", VmatSource.Kv1Kind.String)]
    [InlineData("[1 2 3]", VmatSource.Kv1Kind.String)]
    [InlineData("materials/x.tga", VmatSource.Kv1Kind.String)]
    [InlineData("5 ", VmatSource.Kv1Kind.String)]
    [InlineData("3000000000", VmatSource.Kv1Kind.String)]
    [InlineData("nanosuit", VmatSource.Kv1Kind.String)]
    [InlineData("informal", VmatSource.Kv1Kind.String)]
    public void Values_AreTypedByTheirText(string text, VmatSource.Kv1Kind kind) =>
        Assert.Equal(kind, VmatSource.TypeOf(text, "t.vmat", "k").Kind);

    [Theory]
    [InlineData("inf")]
    [InlineData("-NaN")]
    [InlineData("0x1A")]
    [InlineData("0x0000000000000001")]
    [InlineData("1e999")]
    public void Values_StrtodReadsSpecially_AreRefused(string text) =>
        Assert.Throws<InvalidOperationException>(() => VmatSource.TypeOf(text, "t.vmat", "k"));

    [Fact]
    public void Double_IsStoredThroughFloat() =>
        Assert.Equal((double)(float)0.1, VmatSource.TypeOf("0.1", "t.vmat", "k").Double);

    [Theory]
    [InlineData("", new[] { 0f, 0, 0, 0 })]
    [InlineData("[0.5 0.25]", new[] { 0.5f, 0.25f, 0, 0 })]
    [InlineData("[1 2 3 4 5]", new[] { 1f, 2, 3, 4 })]
    [InlineData("[1 x 3]", new[] { 1f, 0, 0, 0 })]
    public void Vectors_ScanAsSscanfDoes(string text, float[] lanes) =>
        Assert.Equal(lanes, VmatSource.ParseVector(text, "t.vmat", "k"));

    private static VmatSource Parse(string text) => VmatSource.Parse(Encoding.UTF8.GetBytes(text), "t.vmat");

    [Fact]
    public void Source1ImportForm_KeepsTextAndSkipsLegacyImport()
    {
        var vmat = Parse("\"Layer0\"\r\n{\r\n\t\"Shader\"\t\t\"csgo_lightmappedgeneric.vfx\"\r\n\t\"g_flCubeMapScalar\"\t\t\"1.000000\"\r\n"
            + "\t\"SystemAttributes\"\r\n\t{\r\n\t\t\"PhysicsSurfaceProperties\"\t\t\"brick\"\r\n\t}\r\n"
            + "\t\"legacy_import\"\r\n\t{\r\n\t\t\"LightmappedGeneric\"\r\n\t\t{\r\n\t\t\t\"$detail\"\t\t\"detail\\noise\"\r\n\t\t}\r\n\t}\r\n}\r\n");
        Assert.Equal("csgo_lightmappedgeneric.vfx", vmat.Shader);
        Assert.Equal([new("g_flCubeMapScalar", "1.000000")], vmat.Values);
        Assert.Equal([new VmatAttribute("PhysicsSurfaceProperties", VmatAttributeKind.String, "brick")], vmat.Attributes);
    }

    [Fact]
    public void AttributeTables_FileByType_IntsOutsideSystemAttributesAreFloatsToo()
    {
        var vmat = Parse("Layer0 { shader \"x.vfx\" attributes { A \"3\" V \"[1 2]\" S \"s\" T { x 1 } } SystemAttributes { B \"7\" F \"0.5\" } }");
        Assert.Equal(
        [
            new("A", VmatAttributeKind.Int, "3"), new("A", VmatAttributeKind.Float, "3"),
            new("V", VmatAttributeKind.Vector, "[1 2]"), new("S", VmatAttributeKind.String, "s"),
            new("B", VmatAttributeKind.Int, "7"), new("F", VmatAttributeKind.Float, "0.5"),
        ], vmat.Attributes);
    }

    [Theory]
    [InlineData("Layer0 { shader \"x.vfx\" a \"1\" a \"2\" }")]
    [InlineData("Layer0 { shader \"x.vfx\" a = \"1\" }")]
    [InlineData("#base \"x.vmat\"\nLayer0 { shader \"x.vfx\" }")]
    [InlineData("Layer0 { shader \"x.vfx\" a \"x\\\"y\" }")]
    [InlineData("Layer0 { shader \"x.vfx\" a \"m\\\\x.tga\" }")]
    [InlineData("Layer0 { shader \"x.vfx\" dynamicparams { } }")]
    [InlineData("Layer0 { shader \"x.vfx\" } extra")]
    public void UnportedSyntax_IsRefused(string text) =>
        Assert.ThrowsAny<InvalidOperationException>(() => Parse(text));
}
