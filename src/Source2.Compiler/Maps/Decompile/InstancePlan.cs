using System.Numerics;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// The map's own instances rebuilt from their copies (docs/DECOMPILE.md, stage 3), by the forward's
/// rules (MapInstances.Expand, Emit, SettleWorld.BakedPlacement):
/// <list type="bullet">
/// <item>a copy carries its template's walk index as compile_source_id, and its id is its collapse's
/// block start plus its template node's slot in the group, every node counted; so two template
/// indices are one group's when they have as many copies and the k-th of each lie a constant id
/// apart, and a group's k-th copies are its k-th collapse;</item>
/// <item>blocks are handed out back to back in collapse order, one as wide as its group plus one, so
/// the gaps between collapses give each group's width, and a gap wider than that holds collapses that
/// ship no copy, rebuilt as a stand-in of that width;</item>
/// <item>a collapse's copies land where its instance's parent's subtree ends;</item>
/// <item>with the group unturned, a copy's matrix is its instance's own (the group's inverse only
/// translates), so the group is written at the origin and the instances placed by
/// <see cref="FrameSolver"/>, collapses standing for placements; a group copied once stands its
/// template where the copy is and its instance at the group's transform, which moves nothing.</item>
/// <item>an instance inside a template group is collapsed through that group's copies, in a later
/// round (MapInstances.Expand): its blocks follow every first-round block, one per outer copy, and its
/// copies land inside their outer copy (the EmitAt they inherit, in the copy's preorder). Its group is
/// written at the origin and its instance there in the outer template, so with an unturned outer
/// collapse the second step moves by the first one's matrix (SettleWorld.BakedPlacement); the inner
/// templates are solved with the outer group's own and every copy checked through both steps. Under
/// turned outer collapses that one step does not land them, and the inner instance and group are
/// placed (<see cref="NestedSolve"/>).</item>
/// </list>
/// Maps whose prefabs also take blocks are not planned yet.
/// </summary>
internal sealed class InstancePlan
{
    public sealed class Group
    {
        /// <summary>Template walk indices, in slot order, and their slots counted from the group's start.</summary>
        public List<int> SourceIds { get; } = [];
        public List<int> Slots { get; } = [];
        public int Count { get; set; }
        public List<List<EntityLumpAuthor.SourceEntity>> Collapses { get; } = [];
        /// <summary>Each template's local placement, in slot order.</summary>
        public (Vector3 Origin, Vector3 Angles)[] Local { get; set; } = [];

        /// <summary>For a group instanced from inside another's template: that group, and how many of
        /// its templates come before the instance node.</summary>
        public Group? Outer { get; set; }
        public int OuterBefore { get; set; }

        /// <summary>For a nested group: its instance's placement in the outer template, and its own
        /// placement (both the origin unturned unless <see cref="NestedSolve"/> placed them).</summary>
        public (Vector3 Origin, Vector3 Angles) InstanceAt { get; set; }
        public (Vector3 Origin, Vector3 Angles) GroupAt { get; set; }
    }

    public sealed class Collapse
    {
        public required Group Group { get; init; }
        public required int Index { get; init; }
        public required int MinId { get; init; }
        /// <summary>The walk index the copies land before; int.MaxValue for the walk's end.</summary>
        public required int EmitAt { get; init; }
        public (Vector3 Origin, Vector3 Angles) Transform { get; set; }

        /// <summary>The width of the blocks that ship nothing between this collapse and the next.</summary>
        public int HiddenAfter { get; set; }
    }

    public List<Group> Groups { get; } = [];
    public List<Collapse> Collapses { get; } = [];

    /// <summary>The first block's start: the map's highest node id plus one (MapInstances.Expand).</summary>
    public int Next0 { get; private set; }

    /// <param name="copies">The copies, each with its place among the lumps' entities and its lump file.</param>
    /// <param name="walked">The map's own shipped nodes, each with its place and lump file.</param>
    public static InstancePlan? Build(IReadOnlyList<(EntityLumpAuthor.SourceEntity Copy, int Lump, string File)> copies,
                                      IReadOnlyList<(EntityLumpAuthor.SourceEntity Node, int Lump, string File)> walked, List<string> notes)
    {
        var plan = new InstancePlan();
        var bySource = copies.GroupBy(c => c.Copy.SourceId)
                             .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Copy.Entity.NodeId).ToList());

        // Groups: indices with as many copies, their k-th copies a constant id apart.
        var sources = bySource.Keys.Order().ToList();
        var groupOf = new Dictionary<int, int>();
        foreach (var s in sources)
        {
            if (groupOf.ContainsKey(s))
                continue;
            groupOf[s] = s;
            // One block holds one copy of each of its group's templates, and the next collapse of the
            // group starts a block further on, so two templates of a group lie closer than any two
            // collapses of either: a constant offset at or past that is two groups placed alike.
            static int Spacing(List<(EntityLumpAuthor.SourceEntity Copy, int Lump, string File)> ids)
                => ids.Count < 2 ? 64 : ids.Zip(ids.Skip(1)).Min(p => p.Second.Copy.Entity.NodeId - p.First.Copy.Entity.NodeId);
            foreach (var t in sources.Where(t => !groupOf.ContainsKey(t) && bySource[t].Count == bySource[s].Count))
            {
                var d = bySource[t][0].Copy.Entity.NodeId - bySource[s][0].Copy.Entity.NodeId;
                if (Math.Abs(d) < Math.Min(Spacing(bySource[s]), Spacing(bySource[t]))
                    && bySource[t].Zip(bySource[s]).All(p => p.First.Copy.Entity.NodeId - p.Second.Copy.Entity.NodeId == d))
                    groupOf[t] = s;
            }
        }
        foreach (var members in sources.GroupBy(s => groupOf[s]))
        {
            var group = new Group();
            var order = members.OrderBy(s => bySource[s][0].Copy.Entity.NodeId).ToList();
            var first = bySource[order[0]][0].Copy.Entity.NodeId;
            foreach (var s in order)
            {
                group.SourceIds.Add(s);
                group.Slots.Add(bySource[s][0].Copy.Entity.NodeId - first);
            }
            if (!group.SourceIds.Zip(group.SourceIds.Skip(1)).All(p => p.First < p.Second))
            {
                notes.Add($"instances: a group's template indices do not run with its slots ({string.Join(",", group.SourceIds)}); not rebuilt");
                return null;
            }
            for (var k = 0; k < bySource[order[0]].Count; k++)
                group.Collapses.Add([.. order.Select(s => bySource[s][k].Copy)]);
            plan.Groups.Add(group);
        }

        // Each collapse's lump place: before the first shipped node of the map after its copies in
        // their lump (a world layer's lump is walked apart).
        var lumpOf = copies.ToDictionary(c => c.Copy, c => c.Lump);
        var fileOf = copies.ToDictionary(c => c.Copy, c => c.File);
        var walkedByLump = walked.OrderBy(w => w.Lump).ToList();
        foreach (var group in plan.Groups)
            for (var k = 0; k < group.Collapses.Count; k++)
            {
                var c = group.Collapses[k];
                var places = c.Select(x => lumpOf[x]).ToList();
                var after = walkedByLump.FirstOrDefault(w => w.File == fileOf[c[^1]] && w.Lump > places.Max());
                plan.Collapses.Add(new Collapse
                {
                    Group = group, Index = k, MinId = c.Min(x => x.Entity.NodeId),
                    EmitAt = after.Node is null ? int.MaxValue : after.Node.SourceId,
                });
            }
        plan.Collapses.Sort((a, b) => a.MinId.CompareTo(b.MinId));

        // Nested instances: a group whose every collapse lands inside a distinct collapse of one other
        // group (its copies just after some of that collapse's and before the rest) and whose blocks
        // all follow that group's is copied from inside it, a round later.
        var atLump = copies.ToDictionary(c => c.Lump, c => c.Copy);
        var collapseOf = new Dictionary<EntityLumpAuthor.SourceEntity, (Group Group, int Index)>();
        foreach (var g in plan.Groups)
            for (var k = 0; k < g.Collapses.Count; k++)
                foreach (var x in g.Collapses[k])
                    collapseOf[x] = (g, k);
        foreach (var inner in plan.Groups)
        {
            var hosts = new List<(Group Group, int Index, int Before)>();
            for (var k = 0; k < inner.Collapses.Count; k++)
            {
                var places = inner.Collapses[k].Select(x => lumpOf[x]).Order().ToList();
                if (!atLump.TryGetValue(places[0] - 1, out var before) || collapseOf[before].Group == inner)
                    break;
                var (host, index) = collapseOf[before];
                var hostPlaces = host.Collapses[index].Select(x => lumpOf[x]).Order().ToList();
                var count = hostPlaces.Count(l => l < places[0]);
                // The host's copies run unbroken around the inner ones.
                if (places[^1] - places[0] + 1 != places.Count
                    || hostPlaces[count - 1] != places[0] - 1
                    || hostPlaces.Take(count).Zip(hostPlaces.Take(count).Skip(1)).Any(p => p.Second != p.First + 1)
                    || hostPlaces.Skip(count).Where((l, i) => l != places[^1] + 1 + i).Any())
                    break;
                hosts.Add((host, index, count));
            }
            if (hosts.Count != inner.Collapses.Count || hosts.Select(h => h.Group).Distinct().Count() != 1)
                continue;
            var outer = hosts[0].Group;
            if (hosts.Select(h => h.Index).Distinct().Count() != outer.Collapses.Count || inner.Collapses.Count != outer.Collapses.Count
                || hosts.Select(h => h.Before).Distinct().Count() != 1 || outer.Outer is not null)
                continue;
            var innerMin = plan.Collapses.Where(c => c.Group == inner).Min(c => c.MinId);
            if (plan.Collapses.Any(c => c.Group == outer && c.MinId > innerMin))
                continue;
            // The second round collapses the inner instances as the outer copies stand in the tree,
            // which for outer collapses of one parent is block order (each appends its copy).
            var hostOrder = plan.Collapses.Where(c => c.Group == outer).Select(c => c.Index).ToList();
            var innerOrder = plan.Collapses.Where(c => c.Group == inner).Select(c => hosts[c.Index].Index).ToList();
            if (!hostOrder.SequenceEqual(innerOrder)
                || plan.Collapses.Where(c => c.Group == outer).Select(c => c.EmitAt).Distinct().Count() != 1)
            {
                notes.Add($"instances: the nested group of {inner.SourceIds[0]} collapses in another order than its outer copies stand; not rebuilt");
                return null;
            }
            inner.Outer = outer;
            inner.OuterBefore = hosts[0].Before;
            // Its k-th collapse is the k-th outer copy's.
            var reordered = Enumerable.Range(0, inner.Collapses.Count).OrderBy(k => hosts[k].Index).Select(k => inner.Collapses[k]).ToList();
            inner.Collapses.Clear();
            inner.Collapses.AddRange(reordered);
            for (var i = 0; i < plan.Collapses.Count; i++)
                if (plan.Collapses[i].Group == inner)
                    plan.Collapses[i] = new Collapse
                    {
                        Group = inner, Index = hosts[plan.Collapses[i].Index].Index, MinId = plan.Collapses[i].MinId, EmitAt = plan.Collapses[i].EmitAt,
                    };
            notes.Add($"instances: group of {inner.SourceIds[0]} is instanced inside the group of {outer.SourceIds[0]} ({inner.Collapses.Count} second-round collapses)");
        }

        // The collapses run in tree order, and an instance is written where its copies land: the
        // landing places of those whose copies do not land at the walk's end have to run with the
        // blocks (an instance at the root lands at the end wherever it stands).
        var landing = plan.Collapses.Where(c => c.Group.Outer is null && c.EmitAt != int.MaxValue).ToList();
        for (var i = 1; i < landing.Count; i++)
            if (landing[i].EmitAt < landing[i - 1].EmitAt)
            {
                notes.Add($"instances: collapse at id {landing[i].MinId} lands before walk index {landing[i].EmitAt}, earlier than the one before it ({landing[i - 1].MinId} at {landing[i - 1].EmitAt}); not rebuilt");
                return null;
            }

        // Widths from the block chain. Blocks run back to back in collapse order, so the gap from a
        // collapse's first copy to the next one's first copy is the group's width past its lead (the
        // slots before its first shipping template, a prop_static or mesh that bakes into the world),
        // plus the next group's lead, plus whatever collapses ship no copy between them: gap = W(c) +
        // L(n) + H. A collapse takes 1 + its counted nodes and a hollow target none (MapInstances.Hollow),
        // so H is 0 or a stand-in of at least 2 (a group of empty meshes instanced once). Each group's
        // W is its least gap less the next lead (at least its slots); a leftover of 1 is the next
        // group's lead, which moves every collapse of that group one later, so the widths are solved
        // again with it (atixref's group of 663 leads with a prop_static, s2c_slotprobe3).
        var span = plan.Groups.ToDictionary(g => g, g => g.Slots[^1] + 1);
        var lead = plan.Groups.ToDictionary(g => g, _ => 0);
        var width = new Dictionary<Group, int>();
        for (var round = 0; ; round++)
        {
            width.Clear();
            for (var i = 0; i + 1 < plan.Collapses.Count; i++)
            {
                var (c, n) = (plan.Collapses[i], plan.Collapses[i + 1]);
                var w = n.MinId - c.MinId - 1 - lead[n.Group];
                width[c.Group] = width.TryGetValue(c.Group, out var least) ? Math.Min(least, w) : w;
            }
            foreach (var g in plan.Groups)
                width.TryAdd(g, span[g]);
            Collapse? odd = null;
            for (var i = 0; i + 1 < plan.Collapses.Count && odd is null; i++)
            {
                var (c, n) = (plan.Collapses[i], plan.Collapses[i + 1]);
                if (n.MinId - c.MinId - 1 - width[c.Group] - lead[n.Group] == 1)
                    odd = n;
            }
            if (odd is null)
                break;
            if (odd.Group.Outer is not null || round > 4 * plan.Groups.Count)
            {
                notes.Add($"instances: one id before the collapse at {odd.MinId} is neither a lead nor a stand-in; not rebuilt");
                return null;
            }
            lead[odd.Group]++;
        }
        foreach (var g in plan.Groups)
        {
            if (width[g] < span[g])
            {
                notes.Add($"instances: group of {g.SourceIds[0]}: its least gap {width[g]} is under its slots {span[g]}; not rebuilt");
                return null;
            }
            for (var k = 0; k < g.Slots.Count; k++)
                g.Slots[k] += lead[g];
            g.Count = width[g] + lead[g];
        }
        for (var i = 0; i + 1 < plan.Collapses.Count; i++)
        {
            var (c, n) = (plan.Collapses[i], plan.Collapses[i + 1]);
            c.HiddenAfter = n.MinId - c.MinId - 1 - width[c.Group] - lead[n.Group];
            // A stand-in is a first-round instance: blocks that ship nothing after a second-round
            // one would be a nested group that ships nothing, which is not rebuilt.
            if (c.HiddenAfter > 0 && c.Group.Outer is not null)
            {
                notes.Add($"instances: {c.HiddenAfter} ids past the nested collapse at {c.MinId} ship nothing; not rebuilt");
                return null;
            }
        }
        var head = plan.Collapses[0];
        plan.Next0 = head.MinId - 1 - head.Group.Slots[0];

        // Placements. A nested group's templates are solved as the outer group's last members.
        // The yaw most collapses' first copies show, across every group: a prefab turned over the map
        // turns them all (s2c_prefabprobe4's atixref at 90), which a group whose own template is
        // turned against it does not show (its copies at 0, group 745).
        static float Yaw360(float y) => ((y % 360f) + 360f) % 360f;
        var scopeYaw = plan.Groups.SelectMany(x => x.Collapses).GroupBy(c => MathF.Round(Yaw360(c[0].Entity.Angles.Y))).MaxBy(x => x.Count())!.Key;
        foreach (var g in plan.Groups.Where(g => g.Outer is null))
        {
            var mine = plan.Collapses.Where(c => c.Group == g).OrderBy(c => c.Index).ToList();
            var innerGroups = plan.Groups.Where(i => i.Outer == g).ToList();
            List<EntityLumpAuthor.SourceEntity> Members(int k) => [.. g.Collapses[k], .. innerGroups.SelectMany(i => i.Collapses[k])];
            void Split()
            {
                var at = g.SourceIds.Count;
                foreach (var i in innerGroups)
                {
                    i.Local = g.Local[at..(at + i.SourceIds.Count)];
                    at += i.SourceIds.Count;
                }
                g.Local = g.Local[..g.SourceIds.Count];
            }
            if (g.Collapses.Count == 1)
            {
                g.Local = [.. Members(0).Select(x => (x.Entity.Origin, x.Entity.Angles))];
                mine[0].Transform = (Vector3.Zero, Vector3.Zero);
                Split();
                if (!NestedExact(g, innerGroups, mine, notes))
                    return null;
                continue;
            }
            var why = "";
            // The first placement has to be an instance unturned or turned a right angle (a prefab
            // turned 90 over the map turns every collapse: s2c_prefabprobe4): collapses are tried in
            // order of how near their first copy's yaw is to the most common one among them (the
            // template's own, in a group mostly placed unturned), each first unturned and then turned by
            // the right angle nearest that yaw, then the one nearest the scope's (each try is a whole
            // frame search, up to minutes, so unturned goes first and the other right angles are not
            // tried); the solver accepts only an exact landing.
            float Yaw(int k) => ((g.Collapses[k][0].Entity.Angles.Y % 360f) + 360f) % 360f;
            var common = Enumerable.Range(0, g.Collapses.Count).GroupBy(k => MathF.Round(Yaw(k))).MaxBy(x => x.Count())!.Key;
            var reps = Enumerable.Range(0, g.Collapses.Count).OrderBy(k => Math.Abs(Yaw(k) - common)).Take(4).ToList();
            static float Apart(float a, float b) => Math.Min(Math.Abs(a - b), 360f - Math.Abs(a - b));
            int Quarter(float yaw) => new[] { 0, 1, 2, 3 }.MinBy(q => Apart(q * 90f, yaw));
            var quarters = new[] { 0, Quarter(common), Quarter(scopeYaw) }.Distinct().ToList();
            // Each frame found is kept only if accepted: a frame that fits the outer group's own
            // members may still not be the one its nested copies need (a one-template group fits
            // unturned and turned alike), so the search goes on to the next one.
            var tries = new List<string>();
            bool Search(Func<int, List<EntityLumpAuthor.SourceEntity>> members, int nested, Func<FrameSolver.Solution, bool> accept)
            {
                foreach (var (r, quarter) in quarters.SelectMany(q => reps.Select(r => (r, q))))
                {
                    var order = new[] { r }.Concat(Enumerable.Range(0, g.Collapses.Count).Where(k => k != r)).ToList();
                    var placed = order.Select(k => members(k).Select(x => (x.Entity.Origin, x.Entity.Angles)).ToArray()).ToList();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    if (Environment.GetEnvironmentVariable("DECOMPILE_FRAME_LOG") == "1")
                        Console.Error.WriteLine($"frame: group {g.SourceIds[0]} rep {r} quarter {quarter}: trying, first node at {placed[0][0].Origin:R} {placed[0][0].Angles:R}");
                    // 50M landings: 50 times the most a solved frame has taken (atixref group 674, 1.08M).
                    var solution = FrameSolver.Solve(placed, 32768, 2_000_000, out why, quarter, 50_000_000);
                    var work = solution?.Work;
                    var at = solution?.FirstOrigin;
                    if (solution is not null)
                    {
                        for (var i = 0; i < order.Count; i++)
                            mine.First(c => c.Index == order[i]).Transform = solution.Later[i];
                        tries.Clear();
                        if (!accept(solution))
                        {
                            why = tries.LastOrDefault() ?? "not accepted";
                            solution = null;
                        }
                    }
                    if (Environment.GetEnvironmentVariable("DECOMPILE_FRAME_LOG") == "1")
                        Console.Error.WriteLine($"frame: group {g.SourceIds[0]} ({g.SourceIds.Count} own, {nested} nested) rep {r} quarter {quarter}: {(solution is null ? why : $"solved at {at} ({work:n0} landings)")} in {watch.Elapsed.TotalSeconds:F1}s");
                    if (solution is not null)
                        return true;
                }
                return false;
            }
            // The nested groups' templates first as the outer group's last members, one step from
            // the outer collapse (the inner instance and group at the origin unturned).
            if (Search(Members, innerGroups.Sum(i => i.SourceIds.Count), s =>
                {
                    g.Local = s.Local;
                    Split();
                    return NestedExact(g, innerGroups, mine, tries);
                }))
                continue;
            // Else the outer group on its own members, and each nested group placed through both
            // steps (NestedSolve): under a turned outer collapse a nested copy rounds by where its
            // own group stands, which one step does not reach (s2c_prefabprobe4's group 6203).
            if (innerGroups.Count > 0 && Search(k => g.Collapses[k], 0, s =>
                {
                    g.Local = s.Local;
                    return innerGroups.All(i => NestedSolve(g, i, mine, tries)) && NestedExact(g, innerGroups, mine, tries);
                }))
                continue;
            notes.Add($"instances: group of templates {string.Join(",", g.SourceIds)} ({g.Collapses.Count} collapses): no exact frame ({why}); not rebuilt");
            return null;
        }
        return plan;
    }

    private static DmxBinary.Element Node(string type, Vector3 origin, Vector3 angles, DmxBinary.Element? target = null)
    {
        var e = DmxBinary.NewElement(type, "", Guid.Empty)
            .Set("origin", DmxBinary.AttrType.Vector3, origin).Set("angles", DmxBinary.AttrType.QAngle, angles);
        if (target is not null)
            e.Set("target", DmxBinary.AttrType.Element, target);
        return e;
    }

    /// <summary>
    /// A nested group placed through both steps as the forward makes them (SettleWorld.BakedPlacement
    /// over the outer collapse, then the inner instance, moved by it, against its group), in the shape
    /// atixref's source has (instance 6207, group 6203, template 6201): the instance, the group and the
    /// first template share their angles, and the first template stands at the group's origin. Under
    /// collapses turned right angles each world axis of a copy comes from one axis of the instance and
    /// group origins, so those are searched axis by axis: the instance origin within floats of the one
    /// step back from the first copy, the group origin over integers then finer grids, the pair that
    /// lands that axis of every copy. Later templates are searched the same way around the one step
    /// back. The caller checks every copy whole (NestedExact).
    /// </summary>
    private static bool NestedSolve(Group outer, Group inner, List<Collapse> collapses, List<string> notes)
    {
        var outers = collapses.Select(c => Node("CMapInstance", c.Transform.Origin, c.Transform.Angles, Node("CMapGroup", Vector3.Zero, Vector3.Zero))).ToList();
        var target = Node("CMapGroup", Vector3.Zero, Vector3.Zero);
        var instance = Node("CMapInstance", Vector3.Zero, Vector3.Zero, target);
        (Vector3 Origin, Vector3 Angles) Through(int k, Vector3 io, Vector3 go, Vector3 a, Vector3 lo, Vector3 la)
        {
            instance.Attributes["origin"] = io;
            instance.Attributes["angles"] = a;
            target.Attributes["origin"] = go;
            target.Attributes["angles"] = a;
            return Maps.SettleWorld.BakedPlacement(lo, la, [outers[k], instance]);
        }
        var copies = collapses.Select(c => inner.Collapses[c.Index].Select(x => x.Entity).ToList()).ToList();
        bool Fail(string why)
        {
            notes.Add($"instances: nested group of {inner.SourceIds[0]} in {outer.SourceIds[0]}: {why}; not rebuilt");
            return false;
        }

        // Each world axis d from one local axis e, the same under every collapse.
        var turns = collapses.Select(c => Maps.SettleWorld.AngleMatrixDouble(c.Transform.Angles, c.Transform.Origin)).ToList();
        var axisOf = new int[3];
        for (var d = 0; d < 3; d++)
        {
            var row = d * 4;
            var e = Enumerable.Range(0, 3).MaxBy(e => Math.Abs(turns[0][row + e]));
            if (turns.Any(m => Math.Abs(m[row + e]) < 0.999f))
                return Fail("its collapses are not turned right angles alike");
            axisOf[d] = e;
        }
        // The one step back from collapse 0, in double.
        Vector3 Back(Vector3 world)
        {
            var m = turns[0];
            var v = new double[3];
            for (var e = 0; e < 3; e++)
                for (var d = 0; d < 3; d++)
                    v[e] += m[(d * 4) + e] * ((double)world[d] - m[(d * 4) + 3]);
            return new Vector3((float)v[0], (float)v[1], (float)v[2]);
        }

        // The shared angles: the first copy's with the first collapse's yaw taken off, whole degrees
        // first.
        var head = copies[0][0];
        var unturned = head.Angles with { Y = head.Angles.Y - collapses[0].Transform.Angles.Y };
        var whole = new Vector3(MathF.Round(unturned.X), MathF.Round(unturned.Y), MathF.Round(unturned.Z));
        Vector3? shared = null;
        // Each as the instance and group will hold it (MapNodeAngles).
        foreach (var a in new[] { whole }.Concat(FrameSolver.AngleCandidates(unturned).Take(4000)).Select(Maps.SettleWorld.SetAngles).Distinct())
            if (Enumerable.Range(0, copies.Count).All(k => Through(k, Vector3.Zero, Vector3.Zero, a, Vector3.Zero, a).Angles == copies[k][0].Angles))
            {
                shared = a;
                break;
            }
        if (shared is not { } angles)
            return Fail($"no shared angles near {unturned} land every copy's");

        // Group origins: integers nearest 0 first, then each finer grid down to 2^-16 sampled across
        // the same span (golden-ratio steps, 4096 a grid): a copy rounds by the group origin's last
        // bits (atixref's group 6203 at x -456.4531 turns 55.66162 into 55.66165, one 2^-15 step).
        static IEnumerable<float> Origins()
        {
            yield return 0f;
            for (var i = 1; i <= 4096; i++)
            {
                yield return i;
                yield return -i;
            }
            for (var p = 1; p <= 16; p++)
                for (var t = 1; t <= 4096; t++)
                {
                    var u = (t * 0.6180339887498949) % 1.0;
                    yield return (float)(Math.Round(((u * 8192.0) - 4096.0) * (1 << p)) / (1 << p));
                }
        }
        static float Near(float f, int step) => FrameSolver.Step(f, (step & 1) == 0 ? step / 2 : -((step + 1) / 2));
        var io = Back(head.Origin);
        var go = Vector3.Zero;
        for (var d = 0; d < 3; d++)
        {
            var e = axisOf[d];
            var guess = io[e];
            var hit = false;
            foreach (var g in Origins())
            {
                for (var step = 0; step <= 16 && !hit; step++)
                {
                    var (tryIo, tryGo) = (io, go);
                    tryIo[e] = Near(guess, step);
                    tryGo[e] = g;
                    if (Enumerable.Range(0, copies.Count).All(k => Through(k, tryIo, tryGo, angles, tryGo, angles).Origin[d] == copies[k][0].Origin[d]))
                        (io, go, hit) = (tryIo, tryGo, true);
                }
                if (hit)
                    break;
            }
            if (!hit)
                return Fail($"no instance and group {"xyz"[e]} land every copy's {"xyz"[d]}");
        }
        var local = new (Vector3 Origin, Vector3 Angles)[inner.SourceIds.Count];
        local[0] = (go, angles);
        for (var j = 1; j < local.Length; j++)
        {
            var copy = copies[0][j];
            Vector3? turned = null;
            foreach (var a in FrameSolver.AngleCandidates(copy.Angles with { Y = copy.Angles.Y - collapses[0].Transform.Angles.Y }).Take(4000))
                if (Enumerable.Range(0, copies.Count).All(k => Through(k, io, go, angles, Vector3.Zero, a).Angles == copies[k][j].Angles))
                {
                    turned = a;
                    break;
                }
            if (turned is not { } la)
                return Fail($"template {inner.SourceIds[j]}: no angles land every copy's");
            var lo = go + (Back(copy.Origin) - Back(head.Origin));
            for (var d = 0; d < 3; d++)
            {
                var e = axisOf[d];
                var guess = lo[e];
                var hit = false;
                for (var step = 0; step <= 64 && !hit; step++)
                {
                    var tryLo = lo;
                    tryLo[e] = Near(guess, step);
                    if (Enumerable.Range(0, copies.Count).All(k => Through(k, io, go, angles, tryLo, la).Origin[d] == copies[k][j].Origin[d]))
                        (lo, hit) = (tryLo, true);
                }
                if (!hit)
                    return Fail($"template {inner.SourceIds[j]}: no local {"xyz"[e]} lands every copy's {"xyz"[d]}");
            }
            local[j] = (lo, la);
        }
        inner.InstanceAt = (io, angles);
        inner.GroupAt = (go, angles);
        inner.Local = local;
        return true;
    }

    /// <summary>
    /// Every nested copy through both steps as the forward makes it (SettleWorld.BakedPlacement over
    /// the outer collapse, then the inner instance in the outer template against its group): the
    /// one-step solve stands only where the second step moves exactly as the first.
    /// </summary>
    private static bool NestedExact(Group outer, List<Group> inners, List<Collapse> collapses, List<string> notes)
    {
        var outerGroup = Node("CMapGroup", Vector3.Zero, Vector3.Zero);
        foreach (var inner in inners)
        {
            var innerInstance = Node("CMapInstance", inner.InstanceAt.Origin, inner.InstanceAt.Angles,
                                     Node("CMapGroup", inner.GroupAt.Origin, inner.GroupAt.Angles));
            foreach (var c in collapses)
            {
                var outerInstance = Node("CMapInstance", c.Transform.Origin, c.Transform.Angles, outerGroup);
                for (var j = 0; j < inner.SourceIds.Count; j++)
                {
                    var copy = inner.Collapses[c.Index][j].Entity;
                    var got = Maps.SettleWorld.BakedPlacement(inner.Local[j].Origin, inner.Local[j].Angles, [outerInstance, innerInstance]);
                    if (got != (copy.Origin, copy.Angles))
                    {
                        notes.Add($"instances: nested group of {inner.SourceIds[0]} in {outer.SourceIds[0]}: copy {copy.NodeId} comes out at {got} through both steps, not {(copy.Origin, copy.Angles)}; not rebuilt");
                        return false;
                    }
                }
            }
        }
        return true;
    }
}
