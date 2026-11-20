using Source2.Compiler.Kv3;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>The read limit scopes: <see cref="Kv3ReadLimit.Use(long, long)"/> only lowers, <see cref="Kv3ReadLimit.UseTrusted"/> raises the value limit.</summary>
public class Kv3ReadLimitTests
{
    [Fact]
    public void UseOnlyLowers()
    {
        using (Kv3ReadLimit.Use(1 << 20, 1000))
        {
            Assert.Equal(1000, Kv3ReadLimit.MaxValues);
            using (Kv3ReadLimit.Use(1 << 20, Kv3ReadLimit.DefaultMaxValues))
                Assert.Equal(1000, Kv3ReadLimit.MaxValues);
        }
        Assert.Equal(Kv3ReadLimit.DefaultMaxValues, Kv3ReadLimit.MaxValues);
    }

    [Fact]
    public void TrustedRaisesTheValueLimitAndKeepsTheBytes()
    {
        using (Kv3ReadLimit.Use(1 << 20, 1000))
        {
            using (Kv3ReadLimit.UseTrusted())
            {
                Assert.Equal(Kv3ReadLimit.TrustedMaxValues, Kv3ReadLimit.MaxValues);
                Assert.Equal(1 << 20, Kv3ReadLimit.MaxUncompressedBytes);
                using (Kv3ReadLimit.Use(1 << 20, 5))
                    Assert.Equal(5, Kv3ReadLimit.MaxValues);
                Assert.Equal(Kv3ReadLimit.TrustedMaxValues, Kv3ReadLimit.MaxValues);
            }
            Assert.Equal(1000, Kv3ReadLimit.MaxValues);
        }
        Assert.Equal(Kv3ReadLimit.DefaultMaxValues, Kv3ReadLimit.MaxValues);
        Assert.Equal(Kv3ReadLimit.DefaultMaxUncompressedBytes, Kv3ReadLimit.MaxUncompressedBytes);
    }
}
