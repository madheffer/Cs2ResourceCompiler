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
/// <item>the first placement unturned at an origin searched by grid level (multiples of 4096 first,
/// down to 1, within a radius and a budget) over two lattices, the origin on the grid and the origin
/// that puts the first node on the grid of its own map; not the origin itself ( an unturned prefab there moves nothing, and a
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
                                  (Vector3 Origin, Vector3 Angles)[] Later, long Tried, long Work);

    /// <param name="placed">Each placement's nodes as the lump holds them, node for node; the first
    /// is the one written.</param>
    /// <param name="radius">How far the first placement's origin is searched, in units.</param>
    /// <param name="budget">How many frames are tried at most.</param>
    /// <param name="workBudget">How many landings are tested at most (a frame costs from one to thousands,
    /// by how many preimages and turns its nodes take): the search ends on whichever runs out first.</param>
    /// <param name="why">Why no solution was found, when none is.</param>
    /// <param name="quarter">The first placement's yaw in right angles (0 to 3): AngleMatrixDouble of
    /// 90, 180 or 270 is a signed swap of x and y (its cosine of 90 is 6e-17, which the float sums
    /// lose), so its preimages are the unturned ones swapped (<see cref="Plane"/>). Every landing is
    /// still checked through the forward. A prefab turned 90 over a map's instances turns every
    /// collapse (s2c_prefabprobe4).</param>
    public static Solution? Solve(IReadOnlyList<(Vector3 Origin, Vector3 Angles)[]> placed, int radius, long budget, out string why,
                                  int quarter = 0, long workBudget = long.MaxValue)
    {
        var first = placed[0];
        var turns = new Vector3[placed.Count];
        turns[0] = new Vector3(0f, quarter * 90f, 0f);
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
            // The turn between the two placements, after the first's own.
            if (quarter != 0)
            {
                var yaw = turns[k].Y + turns[0].Y;
                turns[k] = turns[k] with { Y = yaw > 180f ? yaw - 360f : yaw };
            }
        }
        for (var k = 0; k < placed.Count; k++)
            turns[k] = SettleWorld.SetAngles(turns[k]);
        var derived = (Vector3[])turns.Clone();
        var matrices = turns.Select(t => SettleWorld.AngleMatrixDouble(t, Vector3.Zero)).ToArray();

        // Positions: x and y through the yaw, z unchanged (rows of a yaw-only matrix).
        long tried = 0;
        long work = 0;
        var later = new Vector3[placed.Count];
        bool Lands(int k, Vector3 origin, Vector3 local, Vector3 want)
        {
            work++;
            var m = matrices[k];
            var x = (((m[0] * local.X) + (m[1] * local.Y)) + (m[2] * local.Z)) + origin.X;
            var y = (((m[4] * local.X) + (m[5] * local.Y)) + (m[6] * local.Z)) + origin.Y;
            var z = (((m[8] * local.X) + (m[9] * local.Y)) + (m[10] * local.Z)) + origin.Z;
            return x == want.X && y == want.Y && z == want.Z;
        }
        // z first, on its own: a yaw-only matrix leaves it to the translation (fl(local z + origin z)),
        // which no x or y reaches. The frame's z is searched over the values that put a node's local z
        // at 0, then over the grid; each node's local z among its preimages and each later placement's
        // origin z such that every placement lands every node.
        var zLocal = new float[first.Length];
        var zOrigin = new float[placed.Count];
        float? zFrame = null;
        IEnumerable<float> ZFrames()
        {
            foreach (var n in first)
                yield return n.Origin.Z;
            for (var grid = 4096; grid >= 1; grid /= 2)
                for (var i = -radius / grid; i <= radius / grid; i++)
                    if (grid == 4096 || i % 2 != 0)
                        yield return i * grid;
        }
        foreach (var zf in ZFrames())
        {
            var pre = first.Select(n => Preimages(n.Origin.Z, zf)).ToArray();
            if (pre.Any(p => p.Count == 0))
                continue;
            var allowed = pre.Select(p => new HashSet<float>(p)).ToArray();
            var ok = true;
            for (var k = 1; k < placed.Count && ok; k++)
            {
                var r0 = Enumerable.Range(0, first.Length).MinBy(j => pre[j].Count);
                float? oz = null;
                foreach (var lz in pre[r0])
                {
                    var guess = (float)((double)placed[k][r0].Origin.Z - lz);
                    foreach (var d in (int[])[0, 1, -1, 2, -2, 3, -3])
                    {
                        var o = Step(guess, d);
                        if (lz + o != placed[k][r0].Origin.Z)
                            continue;
                        // Every node keeps the local z values that land here too.
                        var keep = allowed.Select((a, j) => a.Where(l => l + o == placed[k][j].Origin.Z).ToHashSet()).ToArray();
                        if (keep.All(a => a.Count > 0))
                        {
                            oz = o;
                            allowed = keep;
                            break;
                        }
                    }
                    if (oz is not null)
                        break;
                }
                if (oz is not { } zFound)
                    ok = false;
                else
                    zOrigin[k] = zFound;
            }
            if (!ok)
                continue;
            zFrame = zf;
            for (var j = 0; j < first.Length; j++)
                zLocal[j] = pre[j].First(allowed[j].Contains);
            break;
        }
        if (zFrame is not { } frameZ)
        {
            why = "no z frame lands every node";
            return null;
        }

        // The frame tested: each node's local placement among its preimages, and each later
        // placement's origin, such that every placement lands every node; null when none does.
        string miss = "";
        var debug = Environment.GetEnvironmentVariable("DECOMPILE_FRAME_AT") is { Length: > 0 } || Environment.GetEnvironmentVariable("DECOMPILE_FRAME_FIRST") == "1";
        Vector3[]? Check(Vector3 shift, Vector3[] origins)
        {
            var sets = new (List<float> X, List<float> Y, List<float> Z)[first.Length];
            for (var j = 0; j < first.Length; j++)
            {
                var (px, py) = Plane(first[j].Origin, shift, quarter);
                sets[j] = (px, py, [zLocal[j]]);
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
                        var guess = new Vector3((float)(w0.X - Row(0)), (float)(w0.Y - Row(1)), zOrigin[k]);
                        foreach (var dx in (int[])[0, 1, -1, 2, -2])
                            foreach (var dy in (int[])[0, 1, -1, 2, -2])
                                foreach (var dz in (int[])[0])
                                {
                                    var o = new Vector3(Step(guess.X, dx), Step(guess.Y, dy), Step(guess.Z, dz));
                                    if (fit is null && Lands(k, o, l0, w0))
                                        fit = o;
                                }
                        if (fit is not null)
                            break;
                    }
                    if (debug && fit is null && turn == derived[k])
                    {
                        var l0 = new Vector3(sets[r].X[0], sets[r].Y[0], sets[r].Z[0]);
                        var m0 = matrices[k];
                        miss = $"trace placement {k}: l0 {l0:R}, m {string.Join(" ", m0.Select(v => v.ToString("R")))}";
                    }
                    if (fit is not { } o2)
                        continue;
                    var every = true;
                    for (var j = 0; j < first.Length && every; j++)
                    {
                        every = sets[j].X.Any(lx => sets[j].Y.Any(ly => sets[j].Z.Any(lz => Lands(k, o2, new Vector3(lx, ly, lz), placed[k][j].Origin))));
                        if (debug && !every && turn == derived[k])
                            miss = $"turn {turn:R} origin {o2:R}: node {j} {first[j].Origin:R} -> {placed[k][j].Origin:R} lands no local {sets[j].X[0]:R},{sets[j].Y[0]:R},{sets[j].Z[0]:R}";
                    }
                    // The turn has to land the reference node's angles too, from some local angles that
                    // the first placement also lands: a node where its prefab or group stands lands its
                    // position under any turn (atixref's lights at their group's origin).
                    if (every)
                    {
                        var l = new Vector3(sets[r].X[0], sets[r].Y[0], sets[r].Z[0]);
                        var here = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
                        here.Attributes["origin"] = o2;
                        here.Attributes["angles"] = turn;
                        var firstPrefab = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
                        firstPrefab.Attributes["origin"] = shift;
                        firstPrefab.Attributes["angles"] = turns[0];
                        every = AngleCandidates(Unturned(first[r].Angles, turns[0])).Take(2197).Any(a =>
                            ++work > 0 && SettleWorld.PrefabPlacement(l, a, [firstPrefab]).Angles == first[r].Angles
                            && SettleWorld.PrefabPlacement(l, a, [here]).Angles == placed[k][r].Angles);
                        if (debug && !every && turn == derived[k])
                            miss = $"turn {turn:R} origin {o2:R}: reference node angles {first[r].Angles:R} -> {placed[k][r].Angles:R} land from no local angles";
                    }
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
                        miss += $" | placement {k}: no turn near {derived[k]} lands every node (first {first[r].Origin:R} {first[r].Angles:R}, this {placed[k][r].Origin:R} {placed[k][r].Angles:R}, local candidates {sets[r].X.Count}x{sets[r].Y.Count}x{sets[r].Z.Count})";
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
                            var all = quarter == 0 || Lands(0, shift, l, first[j].Origin);
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
            why = Check(new Vector3(parts[0], parts[1], frameZ), origins) is null ? $"frame {at} does not land every node: {miss}" : $"frame {at} lands every node";
            return null;
        }

        // Frames by grid level, from two lattices: the first placement's origin on the grid (a prefab
        // placed on it), and the origin that puts the first node at a grid point of its own map (a
        // template at its group's origin, an instance off the grid: atixref's lights). Each level
        // covers the whole square of both; within the radius every integer frame and every frame of
        // an integer first node is tried, and the order only decides which comes first.
        Vector3? found = null;
        Vector3[] positions = [];
        var w0 = first[0].Origin;
        IEnumerable<Vector3> Frames()
        {
            yield return new Vector3(w0.X, w0.Y, frameZ);
            for (var grid = 4096; grid >= 1; grid /= 2)
            {
                var steps = radius / grid;
                foreach (var lattice in (int[])[0, 1])
                    for (var ix = -steps; ix <= steps; ix++)
                        for (var iy = -steps; iy <= steps; iy++)
                        {
                            var (x, y) = (ix * grid, iy * grid);
                            if (grid < 4096 && x % (grid * 2) == 0 && y % (grid * 2) == 0)
                                continue;
                            if (lattice == 0 && x == 0 && y == 0)
                                continue;
                            yield return lattice == 0 ? new Vector3(x, y, frameZ) : new Vector3(w0.X - x, w0.Y - y, frameZ);
                        }
            }
        }
        foreach (var shift in Frames())
        {
            if (shift == Vector3.Zero)
                continue;
            if (++tried > budget || work > workBudget)
            {
                why = tried > budget ? $"no frame found in {budget:n0} tried ({work:n0} landings)" : $"no frame found in {workBudget:n0} landings tested ({tried:n0} frames)";
                return null;
            }
            if (Check(shift, later) is { } locals)
            {
                found = shift;
                positions = locals;
                break;
            }
            // DECOMPILE_FRAME_FIRST=1: stop at the first candidate and say why it missed.
            if (Environment.GetEnvironmentVariable("DECOMPILE_FRAME_FIRST") == "1")
            {
                why = $"first frame {shift:R}: {miss}";
                return null;
            }
        }
        if (found is not { } frame)
        {
            why = $"no integer frame within {radius} units lands every node ({tried:n0} tried, {work:n0} landings)";
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
            foreach (var candidate in AngleCandidates(Unturned(first[j].Angles, turns[0])))
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
        return new Solution(frame, local, [.. Enumerable.Range(0, placed.Count).Select(k => (k == 0 ? frame : later[k], turns[k]))], tried, work);
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
        var w = wants[0].Origin;
        var f = solution.FirstOrigin;
        if (copies)
            // The first placement's step for a copy: the prefab's translation (its turn is 0) as the
            // instance moved by it, so the preimages are still under the frame's translation.
            f = SettleWorld.PrefabPlacement(Vector3.Zero, Vector3.Zero, [prefabs[0]]).Origin;
        var quarter = (int)MathF.Round(solution.Later[0].Angles.Y / 90f) & 3;
        var (xs, ys) = Plane(w, f, quarter);
        foreach (var x in xs)
            foreach (var y in ys)
                foreach (var z in Preimages(w.Z, f.Z))
                {
                    var origin = new Vector3(x, y, z);
                    foreach (var angles in AngleCandidates(Unturned(wants[0].Angles, solution.Later[0].Angles)).Take(4000))
                        if (Enumerable.Range(0, prefabs.Length).All(k => Place(origin, angles, k) == wants[k]))
                            return (origin, angles);
                }
        return null;
    }

    /// <summary>
    /// A node's local coordinate on one axis: every float that the first placement's translation
    /// rounds back to what the lump holds (fl(l + shift) == stored); usually one, more where the local
    /// value is finer than the placed one's grid (s2c_prefabprobe11's node at -447.81329 placed at
    /// -6591.8135).
    /// </summary>
    private static List<float> Preimages(float stored, float shift)
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

    /// <summary>
    /// A node's local x and y candidates under a first placement at <paramref name="shift"/> turned
    /// <paramref name="quarter"/> right angles: AngleMatrixDouble's rows are then (0, -1), (1, 0) at
    /// 90, (-1, 0), (0, -1) at 180 (sine folds to 0, exactly) and (0, 1), (-1, 0) at 270, up to the
    /// cosine of 90's 6e-17, so world x is fl(±l + shift) of one local axis and world y of the other.
    /// </summary>
    private static (List<float> X, List<float> Y) Plane(Vector3 world, Vector3 shift, int quarter)
    {
        static List<float> Neg(List<float> l) => [.. l.Select(v => -v)];
        return quarter switch
        {
            1 => (Preimages(world.Y, shift.Y), Neg(Preimages(world.X, shift.X))),
            2 => (Neg(Preimages(world.X, shift.X)), Neg(Preimages(world.Y, shift.Y))),
            3 => (Neg(Preimages(world.Y, shift.Y)), Preimages(world.X, shift.X)),
            _ => (Preimages(world.X, shift.X), Preimages(world.Y, shift.Y)),
        };
    }

    /// <summary>A later placement's yaw as derived, then up to 8 floats either side, nearest first, then
    /// the same about the nearest whole degree (a turn of 0 under a first placement turned 90 derives
    /// as 5.3e-5, whose floats either side never reach 0: s2c_prefabprobe4's group 745), then each of
    /// those 360 off, each as the setter holds it and once.</summary>
    private static IEnumerable<Vector3> TurnCandidates(Vector3 t)
    {
        var near = new List<Vector3>();
        foreach (var about in new[] { t.Y, MathF.Round(t.Y) }.Distinct())
        {
            near.Add(t with { Y = about });
            for (var d = 1; d <= 8; d++)
                foreach (var by in (int[])[d, -d])
                    near.Add(t with { Y = Step(about, by) });
        }
        // As the instance will hold them: the load sets every node's angles through the setter
        // (MapNodeAngles: a negative yaw gains 360, -0 and anything under 0.001 is 0), whose
        // AngleMatrixDouble differs in the last bits (s2c_prefabprobe4's lights 8379 and 8381 landed
        // a float off at a yaw of -89.99995 written).
        var seen = new HashSet<Vector3>();
        foreach (var c in near.Concat(near.SelectMany(c => (Vector3[])[c with { Y = c.Y + 360f }, c with { Y = c.Y - 360f }])))
            if (SettleWorld.SetAngles(c) is var held && seen.Add(held))
                yield return held;
    }

    /// <summary>A placed node's angles with the first placement's yaw taken off: where its local angles
    /// are searched (a template at yaw 0 is placed at 90 under a first placement turned 90).</summary>
    private static Vector3 Unturned(Vector3 placed, Vector3 turn) => turn.Y == 0f ? placed : placed with { Y = placed.Y - turn.Y };

    /// <summary>Every offset of up to 6 floats either side on each component, nearest first (a stable
    /// sort, so in the order the loops make them); built once, as each frame checked asks for it.</summary>
    private static readonly (int X, int Y, int Z)[] NearOffsets = BuildNearOffsets();

    private static (int X, int Y, int Z)[] BuildNearOffsets()
    {
        const int Reach = 6;
        var offsets = new List<(int X, int Y, int Z)>();
        for (var x = -Reach; x <= Reach; x++)
            for (var y = -Reach; y <= Reach; y++)
                for (var z = -Reach; z <= Reach; z++)
                    offsets.Add((x, y, z));
        return [.. offsets.OrderBy(o => Math.Abs(o.X) + Math.Abs(o.Y) + Math.Abs(o.Z))];
    }

    /// <summary>The stored angles first, then every combination of up to 6 floats either side on
    /// each component, nearest first, then each of those with a component 360 off.</summary>
    internal static IEnumerable<Vector3> AngleCandidates(Vector3 a)
    {
        Vector3 Near((int X, int Y, int Z) o) => new(Step(a.X, o.X), Step(a.Y, o.Y), Step(a.Z, o.Z));
        foreach (var o in NearOffsets)
            yield return Near(o);
        foreach (var c in NearOffsets.Select(Near))
            for (var axis = 0; axis < 3; axis++)
                foreach (var turn in (float[])[360f, -360f])
                {
                    var d = c;
                    d[axis] += turn;
                    yield return d;
                }
    }

    internal static float Step(float f, int by)
    {
        for (; by > 0; by--) f = MathF.BitIncrement(f);
        for (; by < 0; by++) f = MathF.BitDecrement(f);
        return f;
    }
}
