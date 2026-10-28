using Source2.Compiler;
using Source2.Compiler.Maps.Decompile;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <c>DECOMPILE_HULL=&lt;addon&gt;|&lt;map&gt;|&lt;entity node id&gt;</c>: one brush entity's pieces, the
/// points each stage of the hull build holds (BrushHulls.Build: the map builder's hull, the
/// shape's points, RnHullCreate's vertices), on the true source and on the decompile.
/// </summary>
public class MapDecompilerHullProbe(ITestOutputHelper output)
{
    [Fact]
    public void Stages()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_HULL") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var package = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..", "game", "csgo_addons", p[0], "maps", p[1] + ".vpk"));
        var result = MapDecompiler.Decompile(MapDecompiler.ReadPackage(package), p[1], MapFixtures.GameSchema());
        foreach (var (side, doc) in new[] { ("truth", DmxBinary.ReadFile(source)), ("decompile", Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap))) })
        {
            var entity = doc.Elements.First(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == int.Parse(p[2]));
            output.WriteLine($"== {side}: angles {entity.GetValue<System.Numerics.Vector3>("angles")}");
            foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
                foreach (var (material, positions, faces, local) in Physics.BrushHulls.Pieces(mesh, entity))
                {
                    output.WriteLine($"  piece material {material}: {positions.Length} points, {faces.Length} faces");
                    output.WriteLine("    points " + string.Join(" ", positions.Select(V)));
                    foreach (var input in Physics.BrushHulls.Inputs(positions, faces, Physics.BrushHulls.Resolve(Physics.BrushHulls.PhysicsType.Default, true, false, false, false), local))
                    {
                        output.WriteLine("    input  " + string.Join(" ", input.Select(V)));
                        var qh = Physics.RnHullBuilder.BuildHull(input, Physics.RnHullBuilder.Options.MapBuilder, out _);
                        var built = qh!.HullVertices.Select(v => new System.Numerics.Vector3(v.X, v.Y, v.Z)).ToArray();
                        output.WriteLine("    built  " + string.Join(" ", built.Select(V)));
                        var shape = Physics.BrushHulls.ShapePoints(built)!;
                        output.WriteLine("    shape  " + string.Join(" ", shape.Select(V)));
                        var hull = Physics.RnHullBuilder.Create(shape, Physics.RnHullBuilder.Options.Compile, out _)!;
                        output.WriteLine("    hull   " + string.Join(" ", hull.VertexPositions.Select(V)));
                    }
                }
        }
    }

    /// <summary><c>DECOMPILE_FIT=x,y,z|ax,ay,az|ox,oy,oz</c>: the corner fit for one target, an entity at
    /// the origin given (default 0) turned by the angles, the mesh unturned there: where the fit lands.</summary>
    [Fact]
    public void Fit()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_FIT") is not { Length: > 0 } spec)
            return;
        static System.Numerics.Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); return new(f[0], f[1], f[2]); }
        var parts = spec.Split('|');
        var (target, angles) = (P(parts[0]), P(parts[1]));
        var origin = parts.Length > 2 ? P(parts[2]) : System.Numerics.Vector3.Zero;
        var mesh = DmxBinary.NewElement("CMapMesh", "", Guid.Empty);
        mesh.Attributes["origin"] = origin;
        var entity = DmxBinary.NewElement("CMapEntity", "", Guid.Empty);
        entity.Attributes["angles"] = angles;
        entity.Attributes["origin"] = origin;
        var toWorld = Maps.CTransform.FromNode(mesh).Matrix();
        var toEntity = Maps.CTransform.FromNode(entity).Inverse().Matrix();
        var guess = Maps.MapMeshes.Transform(Maps.CTransform.FromNode(entity).Matrix(), target);
        var v = MapDecompiler.CornerFor(target, origin, toWorld, toEntity, Maps.CTransform.FromNode(entity).Matrix());
        // The source's own corner, for this probe's box: world = corner + origin on integers.
        foreach (var dx in new[] { -128f, 128f }) foreach (var dy in new[] { -128f, 128f }) foreach (var dz in new[] { -32f, 32f })
        {
            var c = new System.Numerics.Vector3(dx, dy, dz);
            var at = Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, c));
            if (System.Numerics.Vector3.Distance(at, target) < 0.01f)
                output.WriteLine($"source corner {V(c)} places at {V(at)}; world {V(Maps.MapMeshes.Transform(toWorld, c))} vs inverse {V(Maps.MapMeshes.Transform(Maps.CTransform.FromNode(entity).Matrix(), target))}");
        }
        var placed = Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, v));
        output.WriteLine($"guess {V(guess)} -> fit {V(v)} places at {V(placed)}, target {V(target)}");
    }

    private static string V(System.Numerics.Vector3 v) => $"({v.X:R},{v.Y:R},{v.Z:R})";
}
