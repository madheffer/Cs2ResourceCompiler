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
/// templates are solved with the outer group's own and every copy checked through both steps.</item>
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
        // collapse's first copy to the next one's is its group's width plus whatever collapses ship no
        // copy (a group of prop_statics and meshes, whose copies bake into the world): each group's
        // width is its least gap (at least its slots), and the rest of a wider gap is a stand-in
        // collapse of that width, a group of empty nodes instanced once. Slots count from the group's
        // first shipping template (no lead).
        var gaps = new Dictionary<Group, List<int>>();
        for (var i = 0; i + 1 < plan.Collapses.Count; i++)
        {
            var (c, n) = (plan.Collapses[i], plan.Collapses[i + 1]);
            if (!gaps.TryGetValue(c.Group, out var list))
                gaps[c.Group] = list = [];
            list.Add(n.MinId - c.MinId - 1);
        }
        foreach (var g in plan.Groups)
        {
            var span = g.Slots[^1] + 1;
            g.Count = gaps.TryGetValue(g, out var list) ? list.Min() : span;
            if (g.Count < span)
            {
                notes.Add($"instances: group of {g.SourceIds[0]}: its least gap {g.Count} is under its slots {span}; not rebuilt");
                return null;
            }
        }
        for (var i = 0; i + 1 < plan.Collapses.Count; i++)
        {
            var (c, n) = (plan.Collapses[i], plan.Collapses[i + 1]);
            c.HiddenAfter = n.MinId - c.MinId - 1 - c.Group.Count;
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
            FrameSolver.Solution? solution = null;
            var rep = -1;
            var why = "";
            // The first placement has to be an unturned instance: collapses are tried in order of how
            // near their first copy's yaw is to the most common one among them (the template's own, in
            // a group mostly placed unturned); the solver accepts only an exact landing.
            float Yaw(int k) => ((g.Collapses[k][0].Entity.Angles.Y % 360f) + 360f) % 360f;
            var common = Enumerable.Range(0, g.Collapses.Count).GroupBy(k => MathF.Round(Yaw(k))).MaxBy(x => x.Count())!.Key;
            var reps = Enumerable.Range(0, g.Collapses.Count).OrderBy(k => Math.Abs(Yaw(k) - common)).Take(4).ToList();
            foreach (var r in reps)
            {
                if (solution is not null)
                    break;
                var order = new[] { r }.Concat(Enumerable.Range(0, g.Collapses.Count).Where(k => k != r)).ToList();
                var placed = order.Select(k => Members(k).Select(x => (x.Entity.Origin, x.Entity.Angles)).ToArray()).ToList();
                solution = FrameSolver.Solve(placed, 32768, 2_000_000, out why);
                if (solution is not null)
                {
                    rep = r;
                    for (var i = 0; i < order.Count; i++)
                        mine.First(c => c.Index == order[i]).Transform = solution.Later[i];
                    g.Local = solution.Local;
                }
            }
            if (solution is null)
            {
                notes.Add($"instances: group of templates {string.Join(",", g.SourceIds)} ({g.Collapses.Count} collapses): no exact frame ({why}); not rebuilt");
                return null;
            }
            Split();
            if (!NestedExact(g, innerGroups, mine, notes))
                return null;
        }
        return plan;
    }

    /// <summary>
    /// Every nested copy through both steps as the forward makes it (SettleWorld.BakedPlacement over
    /// the outer collapse, then the inner instance at the outer template's origin, both groups at the
    /// origin): the one-step solve stands only where the second step moves exactly as the first.
    /// </summary>
    private static bool NestedExact(Group outer, List<Group> inners, List<Collapse> collapses, List<string> notes)
    {
        static DmxBinary.Element Node(string type, Vector3 origin, Vector3 angles, DmxBinary.Element? target = null)
        {
            var e = DmxBinary.NewElement(type, "", Guid.Empty)
                .Set("origin", DmxBinary.AttrType.Vector3, origin).Set("angles", DmxBinary.AttrType.QAngle, angles);
            if (target is not null)
                e.Set("target", DmxBinary.AttrType.Element, target);
            return e;
        }
        var outerGroup = Node("CMapGroup", Vector3.Zero, Vector3.Zero);
        foreach (var inner in inners)
        {
            var innerInstance = Node("CMapInstance", Vector3.Zero, Vector3.Zero, Node("CMapGroup", Vector3.Zero, Vector3.Zero));
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
