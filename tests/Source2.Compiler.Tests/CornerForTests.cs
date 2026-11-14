using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Maps.Decompile;
using Xunit;

namespace Source2.Compiler.Tests;

/// <summary>
/// MapDecompiler.CornerFor's per-axis search (both moves unturned) against the full walk it replaces:
/// the same corner, bit for bit, on seeded targets around entities near and far from the origin,
/// grid corners and off-grid ones, zeros included.
/// </summary>
public sealed class CornerForTests
{
    [Fact]
    public void PerAxisSearch_IsTheFullWalk()
    {
        static (float[] To, float[] Inverse) Moves(Vector3 at)
        {
            var node = DmxBinary.NewElement("CMapMesh", "", Guid.Empty);
            node.Attributes["origin"] = at;
            node.Attributes["angles"] = Vector3.Zero;
            var t = CTransform.FromNode(node);
            return (t.Matrix(), t.Inverse().Matrix());
        }
        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        var random = new Random(1114);
        var checkedCount = 0;
        for (var i = 0; i < 600; i++)
        {
            var scale = i % 3 == 0 ? 16f : i % 3 == 1 ? 4096f : 16384f;
            var entity = new Vector3(MathF.Round((float)(random.NextDouble() * 2 - 1) * scale * 2) / 2,
                                     MathF.Round((float)(random.NextDouble() * 2 - 1) * scale * 2) / 2,
                                     MathF.Round((float)(random.NextDouble() * 2 - 1) * scale * 4) / 4);
            var mesh = i % 4 == 0 ? entity : entity + new Vector3(0.00098f, -0.00195f, 0.5f);
            var (toWorld, _) = Moves(mesh);
            var (fromEntity, toEntity) = Moves(entity);
            var target = new Vector3((float)(random.NextDouble() * 512 - 256), (float)(random.NextDouble() * 512 - 256),
                                     i % 5 == 0 ? 0f : (float)(random.NextDouble() * 512 - 256));
            var fast = MapDecompiler.CornerFor(target, mesh, toWorld, toEntity, fromEntity);
            var full = MapDecompiler.CornerFor(target, mesh, toWorld, toEntity, fromEntity, fastCorners: false);
            Assert.True(Bits(fast.X) == Bits(full.X) && Bits(fast.Y) == Bits(full.Y) && Bits(fast.Z) == Bits(full.Z),
                        $"case {i}: target {target:R} mesh {mesh:R}: fast {fast:R}, full {full:R}");
            checkedCount++;
        }
        Assert.Equal(600, checkedCount);
    }
}
