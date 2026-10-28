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
/// </list>
/// Nested instances and maps whose prefabs also take blocks are not planned yet.
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

    /// <param name="copies">The copies, each with its lump place.</param>
    /// <param name="walked">The map's own shipped nodes, each with its lump place.</param>
    public static InstancePlan? Build(IReadOnlyList<(EntityLumpAuthor.SourceEntity Copy, int Lump)> copies,
                                      IReadOnlyList<(EntityLumpAuthor.SourceEntity Node, int Lump)> walked, List<string> notes)
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
            static int Spacing(List<(EntityLumpAuthor.SourceEntity Copy, int Lump)> ids)
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

        // Each collapse's lump place: before the first shipped node of the map after its copies.
        var lumpOf = copies.ToDictionary(c => c.Copy, c => c.Lump);
        var walkedByLump = walked.OrderBy(w => w.Lump).ToList();
        foreach (var group in plan.Groups)
            for (var k = 0; k < group.Collapses.Count; k++)
            {
                var c = group.Collapses[k];
                var places = c.Select(x => lumpOf[x]).ToList();
                var after = walkedByLump.FirstOrDefault(w => w.Lump > places.Max());
                plan.Collapses.Add(new Collapse
                {
                    Group = group, Index = k, MinId = c.Min(x => x.Entity.NodeId),
                    EmitAt = after.Node is null ? int.MaxValue : after.Node.SourceId,
                });
            }
        plan.Collapses.Sort((a, b) => a.MinId.CompareTo(b.MinId));
        // The collapses run in tree order, and an instance is written where its copies land: the
        // landing places have to run with the blocks.
        for (var i = 1; i < plan.Collapses.Count; i++)
            if (plan.Collapses[i].EmitAt < plan.Collapses[i - 1].EmitAt)
                notes.Add($"instances: collapse at id {plan.Collapses[i].MinId} lands before walk index {plan.Collapses[i].EmitAt}, earlier than the one before it ({plan.Collapses[i - 1].MinId} at {plan.Collapses[i - 1].EmitAt})");

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
        }
        var head = plan.Collapses[0];
        plan.Next0 = head.MinId - 1 - head.Group.Slots[0];

        // Placements.
        foreach (var g in plan.Groups)
        {
            var mine = plan.Collapses.Where(c => c.Group == g).OrderBy(c => c.Index).ToList();
            if (g.Collapses.Count == 1)
            {
                g.Local = [.. g.Collapses[0].Select(x => (x.Entity.Origin, x.Entity.Angles))];
                mine[0].Transform = (Vector3.Zero, Vector3.Zero);
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
                var placed = order.Select(k => g.Collapses[k].Select(x => (x.Entity.Origin, x.Entity.Angles)).ToArray()).ToList();
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
        }
        return plan;
    }
}
