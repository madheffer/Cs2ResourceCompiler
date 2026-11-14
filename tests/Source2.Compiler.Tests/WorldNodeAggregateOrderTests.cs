using Source2.Compiler.Maps;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// The node's aggregate list order (ledger 276): Step_BuildingVertexOverrideStreams (180277ab0) std::sorts
/// the list by the object's "%s_lr%i_%s" name with 1802a3410's comparison: an empty name first, then strcmp
/// on the unsigned bytes.
/// </summary>
public class WorldNodeAggregateOrderTests
{
    [Fact]
    public void NameIsTakenFromTheModelPath()
        => Assert.Equal("n0_lr0_agg_prop_brick_0", WorldNodeFiles.AggregateName("maps/m/worldnodes/n0_lr0_agg_prop_brick_0.vmdl"));

    [Fact]
    public void ANameSortsBeforeItsOwnExtension()
    {
        // strcmp: the shorter name's NUL is below '-'; the model paths would compare ".vmdl" against "-x".
        Assert.True(WorldNodeFiles.AggregateNameOrder("n0_lr0_agg_prop_m_1", "n0_lr0_agg_prop_m_1-x_0") < 0);
        Assert.True(string.CompareOrdinal("n0_lr0_agg_prop_m_1.vmdl", "n0_lr0_agg_prop_m_1-x_0.vmdl") > 0);
    }

    [Fact]
    public void JoinedPropsLeaveTheirListBySwappingInTheLast()
    {
        // 180272ce0: each joined prop, in list order, is replaced by the list's last prop; FinishLists then
        // walks what is left front to back. List a b c d e with b and c joined:
        // remove b -> a e c d; remove c -> a e d.
        var props = new List<(string, bool)> { ("a", false), ("b", true), ("c", true), ("d", false), ("e", false) };
        Assert.Equal(["a", "e", "d"], NodePropAggregates.LeftInLists([("m", props)], aggregated: true));
        // No aggregate built: nothing is removed, walk order.
        Assert.Equal(["a", "b", "c", "d", "e"], NodePropAggregates.LeftInLists([("m", props)], aggregated: false));
    }

    [Fact]
    public void BytesCompareUnsignedAndEmptyComesFirst()
    {
        Assert.True(WorldNodeFiles.AggregateNameOrder("", "a") < 0);
        Assert.True(WorldNodeFiles.AggregateNameOrder("a", "") > 0);
        Assert.Equal(0, WorldNodeFiles.AggregateNameOrder("", ""));
        Assert.True(WorldNodeFiles.AggregateNameOrder("B", "a") < 0);
        Assert.True(WorldNodeFiles.AggregateNameOrder("agg_z", "agg_é") < 0);
    }
}
