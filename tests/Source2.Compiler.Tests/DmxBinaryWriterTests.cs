using Source2.Compiler;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The binary DMX writer against Hammer's own saves: every map is read, written, and
/// read again, and the second graph must equal the first element for element,
/// attribute for attribute, type code included. Bytes are not compared, since the
/// string table order is ours and not Hammer's (DmxBinaryWriter).
/// </summary>
public class DmxBinaryWriterTests
{
    [Theory]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "s2c_instnest")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "atixref")]
    public void HammerSave_RoundTripsAsAGraph(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        if (source is null) { MapFixtures.Skip($"the {addon}/{map} source"); return; }
        var first = DmxBinary.Read(File.ReadAllBytes(source));
        var bytes = DmxBinaryWriter.Write(first.Format, first.FormatVersion, first.Elements, first.Prefix);
        var second = DmxBinary.Read(bytes);

        Assert.Equal(0, second.TrailingBytes);
        Assert.Equal((first.Format, first.FormatVersion), (second.Format, second.FormatVersion));
        AssertSameGraph(first, second);
    }

    [Fact]
    public void Write_RefusesAnAttributeWithNoType()
    {
        var root = DmxBinary.NewElement("CMapRootElement", "", Guid.Empty);
        root.Attributes["isprefab"] = false;
        Assert.Throws<InvalidOperationException>(() => DmxBinaryWriter.Write("vmap", 40, root, []));
    }

    [Fact]
    public void Write_FromARoot_ReachesEveryElementOnce_RootFirst()
    {
        var root = DmxBinary.NewElement("CMapRootElement", "", Guid.NewGuid());
        var world = DmxBinary.NewElement("CMapWorld", "", Guid.NewGuid());
        var a = DmxBinary.NewElement("CMapEntity", "", Guid.NewGuid()).Set("nodeID", DmxBinary.AttrType.Int, 2);
        world.Set("children", DmxBinary.AttrType.Array(DmxBinary.AttrType.Element), new object?[] { a, a, null });
        root.Set("world", DmxBinary.AttrType.Element, world)
            .Set("origin", DmxBinary.AttrType.QAngle, new System.Numerics.Vector3(1, 2, 3));

        var doc = DmxBinary.Read(DmxBinaryWriter.Write("vmap", 40, root, []));

        Assert.Equal(["CMapRootElement", "CMapWorld", "CMapEntity"], doc.Elements.Select(e => e.Type));
        Assert.Equal(DmxBinary.AttrType.QAngle, doc.Elements[0].Types["origin"]);
        var children = doc.Elements[1].Get<object?[]>("children")!;
        Assert.Same(children[0], children[1]);
        Assert.Null(children[2]);
        Assert.Equal(2, ((DmxBinary.Element)children[0]!).GetValue<int>("nodeID"));
    }

    private static void AssertSameGraph(DmxBinary.Document a, DmxBinary.Document b)
    {
        Assert.Equal(a.Elements.Count, b.Elements.Count);
        Assert.Equal(a.Prefix.Count, b.Prefix.Count);
        var ia = Index(a.Elements);
        var ib = Index(b.Elements);
        for (var i = 0; i < a.Prefix.Count; i++)
            AssertSameElement(a.Prefix[i], b.Prefix[i], ia, ib, $"prefix {i}");
        for (var i = 0; i < a.Elements.Count; i++)
            AssertSameElement(a.Elements[i], b.Elements[i], ia, ib, $"element {i}");
    }

    private static Dictionary<DmxBinary.Element, int> Index(IReadOnlyList<DmxBinary.Element> elements)
    {
        var index = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < elements.Count; i++)
            index.TryAdd(elements[i], i);
        return index;
    }

    private static void AssertSameElement(DmxBinary.Element x, DmxBinary.Element y,
                                          Dictionary<DmxBinary.Element, int> ix, Dictionary<DmxBinary.Element, int> iy, string where)
    {
        Assert.True(x.Type == y.Type && x.Name == y.Name && x.Id == y.Id, $"{where}: {x} vs {y}");
        Assert.Equal(x.Attributes.Keys, y.Attributes.Keys);
        foreach (var (name, value) in x.Attributes)
        {
            Assert.True(x.Types[name] == y.Types[name], $"{where}.{name}: type {x.Types[name]} vs {y.Types[name]}");
            Assert.True(Same(value, y.Attributes[name], ix, iy), $"{where}.{name}: value differs");
        }
    }

    private static bool Same(object? x, object? y, Dictionary<DmxBinary.Element, int> ix, Dictionary<DmxBinary.Element, int> iy)
        => (x, y) switch
        {
            (null, null) => true,
            (DmxBinary.Element ex, DmxBinary.Element ey) => ix[ex] == iy[ey],
            (object?[] ax, object?[] ay) => ax.Length == ay.Length && ax.Zip(ay).All(p => Same(p.First, p.Second, ix, iy)),
            (byte[] bx, byte[] by) => bx.AsSpan().SequenceEqual(by),
            // Bit for bit, so a NaN or a -0 survives too.
            (float fx, float fy) => BitConverter.SingleToInt32Bits(fx) == BitConverter.SingleToInt32Bits(fy),
            _ => Equals(x, y),
        };
}
