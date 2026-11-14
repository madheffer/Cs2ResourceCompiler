using Source2.Compiler.Simulation;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// CrtMath.Exp2 against tier0's own V_exp2f (tier0_20261103.dll called through ctypes by
/// tools/re/exp2f_oracle.py): zero, Inf, NaN, |x| of 2^31 and more, the 229 limits, overflow
/// to Inf and the denormal tail with its ties-to-even rounding.
/// </summary>
public sealed class CrtMathExp2Tests
{
    [Theory]
    [InlineData(0x00000000U, 0x3f800000U)]
    [InlineData(0x80000000U, 0x3f800000U)]
    [InlineData(0x7f800000U, 0x7f800000U)]
    [InlineData(0xff800000U, 0x00000000U)]
    [InlineData(0x7fc00000U, 0x7fc00000U)]
    [InlineData(0xffc00000U, 0xffc00000U)]
    [InlineData(0x00000001U, 0x3f800000U)]
    [InlineData(0x80000001U, 0x3f800000U)]
    [InlineData(0x4f000000U, 0x7f800000U)]
    [InlineData(0xcf000000U, 0x00000000U)]
    [InlineData(0x501502f9U, 0x7f800000U)]
    [InlineData(0xd01502f9U, 0x00000000U)]
    [InlineData(0x7f7fc99eU, 0x7f800000U)]
    [InlineData(0xff7fc99eU, 0x00000000U)]
    [InlineData(0x42fe0000U, 0x7f000000U)]
    [InlineData(0x42fefae1U, 0x7f33c4c9U)]
    [InlineData(0x42ff0000U, 0x7f3504f3U)]
    [InlineData(0x42fffae1U, 0x7f7e3b37U)]
    [InlineData(0x43000000U, 0x7f800000U)]
    [InlineData(0x43008000U, 0x7f800000U)]
    [InlineData(0x43480000U, 0x7f800000U)]
    [InlineData(0x4364e666U, 0x7f800000U)]
    [InlineData(0x43650000U, 0x7f800000U)]
    [InlineData(0x43658000U, 0x7f800000U)]
    [InlineData(0x43960000U, 0x7f800000U)]
    [InlineData(0xc2fc0000U, 0x00800000U)]
    [InlineData(0xc2fc8000U, 0x006ba27eU)]
    [InlineData(0xc2fd0000U, 0x005a827aU)]
    [InlineData(0xc2fe0000U, 0x00400000U)]
    [InlineData(0xc2ff0000U, 0x002d413dU)]
    [InlineData(0xc3024ccdU, 0x00067f7dU)]
    [InlineData(0xc30cc51fU, 0x0000012cU)]
    [InlineData(0xc3140000U, 0x00000002U)]
    [InlineData(0xc3150000U, 0x00000001U)]
    [InlineData(0xc3156666U, 0x00000001U)]
    [InlineData(0xc3158000U, 0x00000001U)]
    [InlineData(0xc315999aU, 0x00000001U)]
    [InlineData(0xc3160000U, 0x00000000U)]
    [InlineData(0xc316199aU, 0x00000000U)]
    [InlineData(0xc3170000U, 0x00000000U)]
    [InlineData(0xc3480000U, 0x00000000U)]
    [InlineData(0xc364e666U, 0x00000000U)]
    [InlineData(0xc3658000U, 0x00000000U)]
    [InlineData(0xc3960000U, 0x00000000U)]
    [InlineData(0xbeca31e8U, 0x3f42b279U)]
    [InlineData(0xbed188f4U, 0x3f40c5a5U)]
    public void Exp2_IsTier0s(uint input, uint expected)
    {
        var got = BitConverter.SingleToUInt32Bits(CrtMath.Exp2(BitConverter.UInt32BitsToSingle(input)));
        Assert.Equal(expected.ToString("x8"), got.ToString("x8"));
    }
}
