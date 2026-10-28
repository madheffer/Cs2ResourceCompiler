using System.Numerics;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// The local frame of a prefab map placed several times, solved exactly (docs/DECOMPILE.md,
/// "Shared prefab maps"). The compile places each node of the map through its prefab's matrix
/// (SettleWorld.PrefabPlacement: AngleMatrixDouble of the prefab's angles and origin, then Moved),
/// so the placements a lump holds are fixed by the map's own coordinates, its nodes' own angles and
/// each prefab's transform. In exact arithmetic any frame shifted by a vector, with the prefab
/// origins moved to match, gives the same placements; in floats only some do, and which the map
/// had is not stored. The solver looks for one that does, and accepts nothing it has not run
/// through the forward bit for bit:
/// <list type="bullet">
/// <item>the first placement unturned at an origin searched over integer points within a radius,
/// by grid level (multiples of 4096 first, down to 1; within a budget), not the origin itself ( an unturned prefab there moves nothing, and a
/// translated one rebuilds its nodes' angles through Moved, which the lump shows);</item>
/// <item>each later placement's turn the rotation between its nodes' angles and the first's, or up
/// to 8 floats either side of its yaw, or 360 off; its origin the one that lands the node with the
/// fewest local candidates, two floats either side tried;</item>
/// <item>each node's own angles the stored ones, or up to 6 floats either side on each component,
/// or one of those 360 off on a component, whichever every placement then lands on.</item>
/// </list>
/// Turns about the vertical only (pitch and roll 0); others are not searched.
/// </summary>
public static class FrameSolver
{
    /// <summary>A solution: the first placement's origin, each node's local placement, each
    /// later placement's transform, and the candidates tried.</summary>
    public sealed record Solution(Vector3 FirstOrigin, (Vector3 Origin, Vector3 Angles)[] Local,
                                  (Vector3 Origin, Vector3 Angles)[] Later, long Tried);

    /// <param name="placed">Each placement's nodes as the lump holds them, node for node; the first
    /// is the one written.</param>
    /// <param name="radius">How far the first placement's origin is searched, in units.</param>
    /// <param name="budget">How many frames are tried at most.</param>
    public static Solution? Solve(IReadOnlyList<(Vector3 Origin, Vector3 Angles)[]> placed, int radius, long budget, out string why)
    {
        var first = placed[0];
        var turns = new Vector3[placed.Count];
        for (var k = 1; k < placed.Count; k++)
        {
            var ra = TemplateTransform.AngleMatrix(Vector3.Zero, first[0].Angles);
            var rb = TemplateTransform.AngleMatrix(Vector3.Zero, placed[k][0].Angles);
            turns[k] = TemplateTransform.MatrixAngles(TemplateTransform.Concat(rb, TemplateTransform.Invert(ra)));
            if (turns[k].X != 0f || turns[k].Z != 0f)
            {
                why = $"placement {k} turns about more than the vertical ({turns[k]})";
                return null;
            }
        }
        var derived = (Vector3[])turns.Clone();
        var matrices = turns.Select(t => SettleWorld.AngleMatrixDouble(t, Vector3.Zero)).ToArray();

        // Positions: x and y through the yaw, z unchanged (rows of a yaw-only matrix).
        long tried = 0;
        var later = new Vector3[placed.Count];
        bool Lands(int k, Vector3 origin, Vector3 local, Vector3 want)
        {
            var m = matrices[k];
            var x = (((m[0] * local.X) + (m[1] * local.Y)) + (m[2] * local.Z)) + origin.X;
            var y = (((m[4] * local.X) + (m[5] * local.Y)) + (m[6] * local.Z)) + origin.Y;
            var z = (((m[8] * local.X) + (m[9] * local.Y)) + (m[10] * local.Z)) + origin.Z;
            return x == want.X && y == want.Y && z == want.Z;
        }
        // A node's local coordinate on one axis: every float that the first placement's
        // translation rounds back to what the lump holds (fl(l + shift) == stored); usually one,
        // more where the local value is finer than the placed one's grid (s2c_prefabprobe11's
        // node at -447.81329 placed at -6591.8135).
        static List<float> Preimages(float stored, float shift)
        {
            var list = new List<float>();
            var l0 = stored - shift;
            if (l0 + shift != stored)
                return list;
            list.Add(l0);
            for (var l = MathF.BitIncrement(l0); l + shift == stored && list.Count < 256; l = MathF.BitIncrement(l))
                list.Add(l);
            for (var l = MathF.BitDecrement(l0); l + shift == stored && list.Count < 512; l = MathF.BitDecrement(l))
                list.Add(l);
            return list;
        }

        // The frame tested: each node's local placement among its preimages, and each later
        // placement's origin, such that every placement lands every node; null when none does.
        string miss = "";
        var debug = Environment.GetEnvironmentVariable("DECOMPILE_FRAME_AT") is { Length: > 0 };
        Vector3[]? Check(Vector3 shift, Vector3[] origins)
        {
            var sets = new (List<float> X, List<float> Y, List<float> Z)[first.Length];
            for (var j = 0; j < first.Length; j++)
            {
                sets[j] = (Preimages(first[j].Origin.X, shift.X), Preimages(first[j].Origin.Y, shift.Y), Preimages(first[j].Origin.Z, shift.Z));
                if (sets[j].X.Count == 0 || sets[j].Y.Count == 0 || sets[j].Z.Count == 0)
                {
                    if (debug)
                        miss = $"node {j} {first[j].Origin:R} has no preimage under {shift}";
                    return null;
                }
            }
            // The later placements' origins from the node with the fewest preimages.
            var r = Enumerable.Range(0, first.Length).MinBy(j => (long)sets[j].X.Count * sets[j].Y.Count * sets[j].Z.Count);
            var locals = new Vector3[first.Length];
            for (var k = 1; k < placed.Count; k++)
            {
                // The turn: the one the nodes' angles give, or a float or a few either side of it, or
                // 360 off (AngleMatrixDouble folds the degrees, its rounding does not): the first under
                // which an origin lands node r and every node has a local placement that lands.
                var w0 = placed[k][r].Origin;
                Vector3? fit = null;
                foreach (var turn in TurnCandidates(derived[k]))
                {
                    matrices[k] = SettleWorld.AngleMatrixDouble(turn, Vector3.Zero);
                    var m = matrices[k];
                    fit = null;
                    foreach (var lx in sets[r].X) foreach (var ly in sets[r].Y) foreach (var lz in sets[r].Z)
                    {
                        var l0 = new Vector3(lx, ly, lz);
                        double Row(int i) => ((double)m[i * 4] * l0.X) + ((double)m[(i * 4) + 1] * l0.Y) + ((double)m[(i * 4) + 2] * l0.Z);
                        var guess = new Vector3((float)(w0.X - Row(0)), (float)(w0.Y - Row(1)), (float)(w0.Z - Row(2)));
                        foreach (var dx in (int[])[0, 1, -1, 2, -2])
                            foreach (var dy in (int[])[0, 1, -1, 2, -2])
                                foreach (var dz in (int[])[0, 1, -1])
                                {
                                    var o = new Vector3(Step(guess.X, dx), Step(guess.Y, dy), Step(guess.Z, dz));
                                    if (fit is null && Lands(k, o, l0, w0))
                                        fit = o;
                                }
                        if (fit is not null)
                            break;
                    }
                    if (fit is not { } o2)
                        continue;
                    var every = true;
                    for (var j = 0; j < first.Length && every; j++)
                        every = sets[j].X.Any(lx => sets[j].Y.Any(ly => sets[j].Z.Any(lz => Lands(k, o2, new Vector3(lx, ly, lz), placed[k][j].Origin))));
                    if (every)
                    {
                        turns[k] = turn;
                        break;
                    }
                    fit = null;
                }
                if (fit is not { } found)
                {
                    if (debug)
                        miss = $"placement {k}: no turn near {derived[k]} lands every node";
                    return null;
                }
                origins[k] = found;
            }
            for (var j = 0; j < first.Length; j++)
            {
                Vector3? hit = null;
                foreach (var lx in sets[j].X)
                {
                    foreach (var ly in sets[j].Y)
                    {
                        foreach (var lz in sets[j].Z)
                        {
                            var l = new Vector3(lx, ly, lz);
                            var all = true;
                            for (var k = 1; k < placed.Count && all; k++)
                                all = Lands(k, origins[k], l, placed[k][j].Origin);
                            if (all) { hit = l; break; }
                        }
                        if (hit is not null) break;
                    }
                    if (hit is not null) break;
                }
                if (hit is not { } found)
                {
                    if (debug)
                        miss = $"node {j} {first[j].Origin:R} ({sets[j].X.Count}x{sets[j].Y.Count}x{sets[j].Z.Count} preimages) lands no placement set";
                    return null;
                }
                locals[j] = found;
            }
            return locals;
        }

        // DECOMPILE_FRAME_AT=x,y: that one frame only.
        if (Environment.GetEnvironmentVariable("DECOMPILE_FRAME_AT") is { Length: > 0 } at)
        {
            var parts = at.Split(',').Select(float.Parse).ToArray();
            var origins = new Vector3[placed.Count];
            why = Check(new Vector3(parts[0], parts[1], 0), origins) is null ? $"frame {at} does not land every node: {miss}" : $"frame {at} lands every node";
            return null;
        }

        // Frames by grid level: every multiple of 4096 within the radius, then of 2048 (that is not
        // one of 4096), down to every integer; each level covers the whole square, so within the
        // radius every integer frame is tried, and the order only decides which comes first.
        Vector3? found = null;
        Vector3[] positions = [];
        for (var grid = 4096; grid >= 1 && found is null; grid /= 2)
        {
            var steps = radius / grid;
            for (var ix = -steps; ix <= steps && found is null; ix++)
                for (var iy = -steps; iy <= steps && found is null; iy++)
                {
                    var (x, y) = (ix * grid, iy * grid);
                    if ((x == 0 && y == 0) || (grid < 4096 && x % (grid * 2) == 0 && y % (grid * 2) == 0))
                        continue;
                    if (++tried > budget)
                    {
                        why = $"no frame found in {budget:n0} tried (grid {grid} of {radius})";
                        return null;
                    }
                    var shift = new Vector3(x, y, 0);
                    if (Check(shift, later) is { } locals)
                    {
                        found = shift;
                        positions = locals;
                    }
                }
        }
        if (found is not { } frame)
        {
            why = $"no integer frame within {radius} units lands every node ({tried:n0} tried)";
            return null;
        }

        // Angles: the first placement's prefab is the frame's translation, so the forward rebuilds
        // its nodes' angles; each node's own is the one every placement turns into what it shows.
        var local = new (Vector3, Vector3)[first.Length];
        var prefabs = new DmxBinary.Element[placed.Count];
        for (var k = 0; k < placed.Count; k++)
        {
            prefabs[k] = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
            prefabs[k].Attributes["origin"] = k == 0 ? frame : later[k];
            prefabs[k].Attributes["angles"] = turns[k];
        }
        for (var j = 0; j < first.Length; j++)
        {
            var origin = positions[j];
            Vector3? angles = null;
            foreach (var candidate in AngleCandidates(first[j].Angles))
                if (Enumerable.Range(0, placed.Count).All(k => SettleWorld.PrefabPlacement(origin, candidate, [prefabs[k]]) == placed[k][j]))
                {
                    angles = candidate;
                    break;
                }
            if (angles is null)
            {
                why = $"node {j}: no angles near {first[j].Angles} land every placement";
                return null;
            }
            local[j] = (origin, angles.Value);
        }
        why = "";
        return new Solution(frame, local, [.. Enumerable.Range(0, placed.Count).Select(k => (k == 0 ? frame : later[k], turns[k]))], tried);
    }

    /// <summary>
    /// One more node in a solved frame: the local placement that every placement, through its
    /// solved prefab transform, lands on what the lump holds for it (<paramref name="wants"/>, one per
    /// placement); its position among the preimages of the first, its angles as for the map's nodes.
    /// Null when none lands.
    /// </summary>
    /// <param name="copies">The node is an instance's template, the instance and its group both at the
    /// map's origin unturned: its copy is placed through SettleWorld.NestedPlacement (the prefab moves
    /// the instance, then the collapse moves the template by the moved instance over its group).</param>
    public static (Vector3 Origin, Vector3 Angles)? Locate(Solution solution, IReadOnlyList<(Vector3 Origin, Vector3 Angles)> wants,
                                                         bool copies = false)
    {
        var prefabs = solution.Later.Select(t =>
        {
            var e = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
            e.Attributes["origin"] = t.Origin;
            e.Attributes["angles"] = t.Angles;
            return e;
        }).ToArray();
        var group = DmxBinary.NewElement("CMapGroup", "", Guid.Empty);
        group.Attributes["origin"] = Vector3.Zero;
        group.Attributes["angles"] = Vector3.Zero;
        var instance = DmxBinary.NewElement("CMapInstance", "", Guid.Empty);
        instance.Attributes["origin"] = Vector3.Zero;
        instance.Attributes["angles"] = Vector3.Zero;
        instance.Attributes["target"] = group;
        (Vector3, Vector3) Place(Vector3 origin, Vector3 angles, int k)
            => copies ? SettleWorld.NestedPlacement(origin, angles, [instance], [prefabs[k]])
                      : SettleWorld.PrefabPlacement(origin, angles, [prefabs[k]]);
        static List<float> Pre(float stored, float shift)
        {
            var list = new List<float>();
            var l0 = stored - shift;
            if (l0 + shift != stored)
                return list;
            list.Add(l0);
            for (var l = MathF.BitIncrement(l0); l + shift == stored && list.Count < 256; l = MathF.BitIncrement(l))
                list.Add(l);
            for (var l = MathF.BitDecrement(l0); l + shift == stored && list.Count < 512; l = MathF.BitDecrement(l))
                list.Add(l);
            return list;
        }
        var w = wants[0].Origin;
        var f = solution.FirstOrigin;
        if (copies)
            // The first placement's step for a copy: the prefab's translation (its turn is 0) as the
            // instance moved by it, so the preimages are still under the frame's translation.
            f = SettleWorld.PrefabPlacement(Vector3.Zero, Vector3.Zero, [prefabs[0]]).Origin;
        foreach (var x in Pre(w.X, f.X))
            foreach (var y in Pre(w.Y, f.Y))
                foreach (var z in Pre(w.Z, f.Z))
                {
                    var origin = new Vector3(x, y, z);
                    foreach (var angles in AngleCandidates(wants[0].Angles).Take(4000))
                        if (Enumerable.Range(0, prefabs.Length).All(k => Place(origin, angles, k) == wants[k]))
                            return (origin, angles);
                }
        return null;
    }

    /// <summary>A later placement's yaw as derived, then up to 8 floats either side, nearest first, then
    /// each of those 360 off.</summary>
    private static IEnumerable<Vector3> TurnCandidates(Vector3 t)
    {
        var near = new List<Vector3> { t };
        for (var d = 1; d <= 8; d++)
            foreach (var by in (int[])[d, -d])
                near.Add(t with { Y = Step(t.Y, by) });
        foreach (var c in near)
            yield return c;
        foreach (var c in near)
            foreach (var turn in (float[])[360f, -360f])
                yield return c with { Y = c.Y + turn };
    }

    /// <summary>The stored angles first, then every combination of up to 6 floats either side on
    /// each component, nearest first, then each of those with a component 360 off.</summary>
    private static IEnumerable<Vector3> AngleCandidates(Vector3 a)
    {
        const int Reach = 6;
        var offsets = new List<(int X, int Y, int Z)>();
        for (var x = -Reach; x <= Reach; x++)
            for (var y = -Reach; y <= Reach; y++)
                for (var z = -Reach; z <= Reach; z++)
                    offsets.Add((x, y, z));
        var near = offsets.OrderBy(o => Math.Abs(o.X) + Math.Abs(o.Y) + Math.Abs(o.Z))
                          .Select(o => new Vector3(Step(a.X, o.X), Step(a.Y, o.Y), Step(a.Z, o.Z))).ToList();
        foreach (var c in near)
            yield return c;
        foreach (var c in near)
            for (var axis = 0; axis < 3; axis++)
                foreach (var turn in (float[])[360f, -360f])
                {
                    var d = c;
                    d[axis] += turn;
                    yield return d;
                }
    }

    private static float Step(float f, int by)
    {
        for (; by > 0; by--) f = MathF.BitIncrement(f);
        for (; by < 0; by++) f = MathF.BitDecrement(f);
        return f;
    }
}
