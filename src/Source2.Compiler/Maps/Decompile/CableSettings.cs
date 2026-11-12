using System.Globalization;
using System.Numerics;
using Source2.Compiler.Physics;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A cable_dynamic's CMapCable settings from the model its compile shipped (EntityPhysicsModels.CableModel).
/// <list type="bullet">
/// <item>The render mesh is the render tube (CableCollision.RenderTube): samples x (sides + 1) vertices and
/// (samples - 1) x sides x 6 indices, which give the side count and the sample count exactly. Its
/// positions are stored to about 2.5e-4 near the origin (s2c_cablerender: every vertex within that of the tube, in another
/// order), so they place the samples and the radius only that closely.</item>
/// <item>The tessellation spacing shows only through each node pair's sample count, max(4, ceil(length /
/// spacing)): every spacing with the same counts compiles the same, and the first a mapper would type is
/// taken (16, the factory's, then whole units, then halves).</item>
/// <item>A collision cable ships the tube again as its physics mesh, exact: the settings are kept only when
/// the forward's mesh (EntityPhysicsModels.CableShape) has the same vertices bit for bit, the radius
/// searched in float steps about the render estimate and the error over the values mappers use.</item>
/// <item>The material is the draw call's. The texture settings, the tint's alpha and particleSnapshotSpacing
/// are not recovered (the render streams are not compared).</item>
/// </list>
/// </summary>
internal static class CableSettings
{
    /// <summary>How far the render mesh may lie from a candidate tube.</summary>
    private const float Fit = 1e-2f;

    public static VmapBuilder.Cable Solve(MapEntities.Entity cable, byte[] model, FgdSchema? schema, List<string> notes)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(model));
        var data = (Model)resource.DataBlock!;
        // A cable with no nodes (pathNodes "[  ]") ships a model with no mesh (ze_ratatouille's
        // cable_dynamic 4721, workshop sweep 11-13): nothing to read the settings from.
        var embedded = data.GetEmbeddedMeshesAndLoD().ToList();
        if (embedded.Count == 0)
        {
            notes.Add($"cable {cable.NodeId}: its model has no render mesh; factory settings, no material");
            return VmapBuilder.Cable.Factory("") with { Collision = PhysicsVertices(resource) is not null };
        }
        var (mesh, _, _, _) = embedded[0];
        var call = mesh.Data.GetArray("m_sceneObjects")[0].GetArray("m_drawCalls")[0];
        var material = call.GetStringProperty("m_material") ?? "";
        var vb = mesh.VBIB.VertexBuffers[0];
        var field = vb.InputLayoutFields.First(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase));
        var vertices = Enumerable.Range(0, (int)vb.ElementCount).Select(v =>
        {
            var at = v * (int)vb.ElementSizeInBytes + (int)field.Offset;
            return new Vector3(BitConverter.ToSingle(vb.Data, at), BitConverter.ToSingle(vb.Data, at + 4), BitConverter.ToSingle(vb.Data, at + 8));
        }).ToArray();
        var indices = Enumerable.Range(0, (int)mesh.VBIB.IndexBuffers[0].ElementCount).Select(i => Index(mesh.VBIB.IndexBuffers[0], i)).ToArray();
        var physics = PhysicsVertices(resource);
        var label = $"cable {cable.NodeId}";
        var fallback = VmapBuilder.Cable.Factory(material) with { Collision = physics is not null };

        // Sides and samples from the counts: V = s (k + 1), I = 6 (s - 1) k.
        int sides = 0, samples = 0;
        for (var k = 3; k <= 1000 && sides == 0; k++)
            if (vertices.Length % (k + 1) == 0 && vertices.Length / (k + 1) is var s && indices.Length == 6 * (s - 1) * k)
                (sides, samples) = (k, s);
        if (sides == 0)
        {
            notes.Add($"{label}: its render mesh ({vertices.Length} vertices, {indices.Length} indices) is no tube; factory settings");
            return fallback;
        }

        var (controls, scales) = EntityLumpAuthor.PathControls(cable, schema);
        IEnumerable<float> Spacings()
        {
            yield return 16f;
            for (var i = 1; i <= 512; i++)
                yield return i;
            for (var i = 1; i <= 1024; i++)
                yield return i * 0.5f;
            for (var i = 1; i <= 64; i++)
                yield return i * 0.25f;
        }
        var bestFit = float.MaxValue;
        foreach (var spacing in Spacings())
        {
            if (CableCollision.Samples(controls, spacing).Samples.Length != samples)
                continue;
            var settings = new CableCollision.Settings(sides, spacing, 1f, false, cable.ClosedLoop, 0f);
            var (centres, _) = CableCollision.RenderTube(controls, scales, settings with { Radius = 0f });
            var (unit, unitIndices) = CableCollision.RenderTube(controls, scales, settings);
            if (centres.Length != vertices.Length)
                continue;
            var offsets = unit.Zip(centres, (u, c) => u - c).ToArray();
            // The radius: each vertex's distance from its nearest sample over its ring offset's length.
            var radius = Median(vertices.Select(q =>
            {
                var j = Enumerable.Range(0, centres.Length).MinBy(i => Vector3.DistanceSquared(q, centres[i]));
                return offsets[j].Length() is var l and > 0f ? (q - centres[j]).Length() / l : 0f;
            }));
            var nearest = vertices.Select(q => Enumerable.Range(0, centres.Length).MinBy(i => Vector3.DistanceSquared(q, centres[i] + radius * offsets[i]))).ToArray();
            var fit = vertices.Select((q, i) => (q - (centres[nearest[i]] + radius * offsets[nearest[i]])).Length()).Max();
            bestFit = Math.Min(bestFit, fit);
            // Only a filter of sample layouts (a wrong spacing misses by about the sample spacing); the stored
            // positions are coarser far from the model's origin (s2c_prefabinst's cable: 2.1e-3).
            if (fit > Fit)
                continue;
            radius = Shortest(radius, r => vertices.Select((q, i) => (q - (centres[nearest[i]] + r * offsets[nearest[i]])).Length()).Max() <= fit + 1e-4f);
            // The first triangle's winding against the tube's.
            var (a, b, c) = (nearest[indices[0]], nearest[indices[1]], nearest[indices[2]]);
            var flip = !Enumerable.Range(0, unitIndices.Length / 3).Any(t => Cyclic(unitIndices, t, a, b, c));
            var found = new VmapBuilder.Cable(material, sides, spacing, radius, flip, physics is not null, 2f);
            if (physics is null)
            {
                notes.Add($"{label}: {sides} sides, spacing {spacing.ToString("R", CultureInfo.InvariantCulture)} (its sample counts), radius {radius.ToString("R", CultureInfo.InvariantCulture)} to the render mesh's precision (not exact); texture settings not recovered");
                return found;
            }
            if (Exact(cable, found, physics, schema) is { } exact)
            {
                notes.Add($"{label}: {sides} sides, spacing {spacing.ToString("R", CultureInfo.InvariantCulture)}, radius {exact.Radius.ToString("R", CultureInfo.InvariantCulture)}, error {exact.Error.ToString("R", CultureInfo.InvariantCulture)}: its physics mesh exact; texture settings not recovered");
                return exact;
            }
            notes.Add($"{label}: no settings give its physics mesh; {sides} sides, radius {radius.ToString("R", CultureInfo.InvariantCulture)} from the render mesh (not exact)");
            return found;
        }
        notes.Add($"{label}: no spacing gives its {samples} samples (spacing 16 gives {CableCollision.Samples(controls, 16f).Samples.Length}, " +
                  $"12 gives {CableCollision.Samples(controls, 12f).Samples.Length}, {controls.Length / 9} nodes; best fit {bestFit.ToString("R", CultureInfo.InvariantCulture)}); factory settings but {sides} sides");
        return fallback with { Sides = sides };
    }

    /// <summary>The settings whose physics mesh has the shipped vertices: the radius in float steps about the
    /// estimate (nearest first), the flip both ways, the error over the values mappers use.</summary>
    private static VmapBuilder.Cable? Exact(MapEntities.Entity cable, VmapBuilder.Cable guess, HashSet<Vector3> physics, FgdSchema? schema)
    {
        float[] errors = [2f, 0f, 0.25f, 0.5f, 1f, 1.5f, 3f, 4f, 5f, 0.1f];
        foreach (var flip in (bool[])[guess.Flip, !guess.Flip])
            foreach (var error in errors)
                for (var step = 0; step <= 64; step++)
                    foreach (var sign in step == 0 ? (int[])[1] : [1, -1])
                    {
                        var radius = guess.Radius;
                        for (var k = 0; k < step; k++)
                            radius = sign > 0 ? MathF.BitIncrement(radius) : MathF.BitDecrement(radius);
                        var settings = new CableCollision.Settings(guess.Sides, guess.Spacing, radius, flip, cable.ClosedLoop, error);
                        // Through the physics mesh build as the model's is (RnMeshCreate's 1/32 weld, which joins
                        // a closed loop's last ring to its first).
                        if (EntityPhysicsModels.CableShape(cable, settings, schema) is { } shape
                            && RnMeshBuilder.Create(shape.Indices, shape.Points, null) is { } built
                            && physics.SetEquals(built.Vertices))
                            return guess with { Radius = radius, Flip = flip, Error = error };
                    }
        return null;
    }

    /// <summary>The physics mesh's vertices, or null for a model with no physics mesh.</summary>
    internal static HashSet<Vector3>? PhysicsVertices(Resource resource)
    {
        var phys = resource.Blocks.FirstOrDefault(b => b.Type == BlockType.PHYS) switch
        {
            KeyValuesOrNTRO k => k.Data,
            BinaryKV3 k => k.Data.Root,
            _ => null,
        };
        static KVObject? Child(KVObject? parent, string key)
            => parent is not null && parent.IsCollection && parent.TryGetValue(key, out var child) ? child : null;
        // Every mesh of every part: a long tube may ship in more than one.
        var meshes = (Child(phys, "m_parts")?.Values ?? []).SelectMany(p => Child(Child(p, "m_rnShape"), "m_meshes")?.Values ?? []).ToList();
        if (meshes.Count == 0)
            return null;
        var set = new HashSet<Vector3>();
        foreach (var mesh in meshes)
        {
            var blob = Child(Child(mesh, "m_Mesh"), "m_Vertices")?.AsBlob().ToArray() ?? [];
            for (var i = 0; i + 12 <= blob.Length; i += 12)
                set.Add(new Vector3(BitConverter.ToSingle(blob, i), BitConverter.ToSingle(blob, i + 4), BitConverter.ToSingle(blob, i + 8)));
        }
        return set;
    }

    private static int Index(VBIB.OnDiskBufferData ib, int i)
        => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);

    private static bool Cyclic(int[] indices, int t, int a, int b, int c)
    {
        var (x, y, z) = (indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2]);
        return (x, y, z) == (a, b, c) || (y, z, x) == (a, b, c) || (z, x, y) == (a, b, c);
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0f : sorted[sorted.Length / 2];
    }

    /// <summary>The value with the fewest decimals that still fits, the estimate itself when none does.</summary>
    private static float Shortest(float estimate, Func<float, bool> fits)
    {
        for (var decimals = 0; decimals <= 6; decimals++)
        {
            var r = (float)Math.Round(estimate, decimals);
            if (fits(r))
                return r;
        }
        return estimate;
    }
}
