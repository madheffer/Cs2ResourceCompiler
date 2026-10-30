using System.Diagnostics;
using Google.OrTools.Sat;
using Source2.Compiler.Maps.Decompile;

namespace Source2.Compiler.Solvers;

/// <summary>
/// The world soup's input order as one CP-SAT model: every order it accepts gives the shipped triangles
/// through RnMeshCreate (probe01: solved with no source data, world PHYS exact; docs/DECOMPILE.md "World
/// geometry"). Prototype and checks: tools/map-survey/world_order_cpsat.py.
/// <para>Per BVH node, p[x] = x's place in the node's entry (all different). A leaf: the shipped order
/// (ascending soup ids). A split by count: the left set's places below a, the children's places the
/// node's (less a on the right). A SAH node's swap partition in closed form: with l[k] the place of the
/// k-th left one (increasing, through the left child's inverse), a right one at place d ends in the right
/// child at (follow d to l[d] while d &lt; a) - a: Lomuto's left one at i swaps a[i] with the right
/// block's front, so a cell d &gt;= a is final once written, and a left one at place q writes the cell of
/// its own left rank. Encoded as label[d] = label[l[d]] for d &lt; a, label[d] = d - a for d &gt;= a,
/// the right place = label[p[x]].</para>
/// <para>The input: the root's places are slots; each triangle alone or in one quad (its second in the
/// next slot, the pair's corner starts one of the problem's); corner times 3 slot + the corner's index
/// under its start; vertex ids are the order of first use, first(v) &lt; first(v + 1). Bounds: each
/// leaf's order is a precedence chain on its left-chain top's places; each connected piece takes one
/// block of slots in the order of its vertex ranges (the soup is built mesh after mesh); the lag window
/// per face (lone, or a quad by the pair's highest corner): when it goes in, vertex top + lag + 1 is not
/// yet met.</para>
/// </summary>
internal static class WorldOrderCpSat
{
    /// <summary>Sets <see cref="WorldGeometry.OrderSolver"/> to this model.</summary>
    internal static void Install() => WorldGeometry.OrderSolver = Solve;

    internal static (int[]? Order, string Report) Solve(WorldGeometry.OrderProblem problem)
    {
        var soup = problem.Soup;
        var (count, vertexCount) = (soup.Length, problem.Vertices);
        var m = new CpModel();

        // The tree from the pre-order trace.
        var nodes = problem.Trace;
        var children = new (int Left, int Right)?[nodes.Length];
        var parent = new (int Node, bool Left)?[nodes.Length];
        var at = 0;
        int Read()
        {
            var i = at++;
            if (nodes[i].Left is null)
                return i;
            var l = Read();
            var r = Read();
            children[i] = (l, r);
            (parent[l], parent[r]) = ((i, true), (i, false));
            return i;
        }
        Read();

        // Places: a leaf's are constants.
        var p = new Dictionary<int, LinearExpr>[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var set = nodes[i].Set;
            if (children[i] is null)
            {
                p[i] = set.Order().Select((t, k) => (t, k)).ToDictionary(x => x.t, x => (LinearExpr)LinearExpr.Constant(x.k));
                continue;
            }
            var vars = set.ToDictionary(t => t, t => m.NewIntVar(0, set.Length - 1, ""));
            m.AddAllDifferent(vars.Values);
            p[i] = vars.ToDictionary(x => x.Key, x => (LinearExpr)x.Value);
        }
        IntVar AsVar(LinearExpr e, long lo, long hi)
        {
            if (e is IntVar v)
                return v;
            var x = m.NewIntVar(lo, hi, "");
            m.Add(x == e);
            return x;
        }

        for (var i = 0; i < nodes.Length; i++)
        {
            if (children[i] is not { } c)
                continue;
            var left = nodes[i].Left!.ToHashSet();
            var set = nodes[i].Set;
            var (n, a) = (set.Length, left.Count);
            var lefts = set.Where(left.Contains).ToArray();
            var rights = set.Where(t => !left.Contains(t)).ToArray();
            if (nodes[i].ByCount)
            {
                foreach (var t in lefts)
                    m.Add(p[i][t] == p[c.Left][t]);
                foreach (var t in rights)
                    m.Add(p[i][t] == p[c.Right][t] + a);
                continue;
            }
            // l[k] = place of the k-th left one, through the left child's inverse.
            var inverse = Enumerable.Range(0, a).Select(_ => m.NewIntVar(0, a - 1, "")).ToArray();
            m.AddInverse(lefts.Select(t => AsVar(p[c.Left][t], 0, a - 1)), inverse);
            var l = Enumerable.Range(0, a).Select(k => m.NewIntVar(k, k + (n - a), "")).ToArray();
            var leftPlaces = lefts.Select(t => p[i][t]).ToArray();
            for (var k = 0; k < a; k++)
            {
                m.AddElement(inverse[k], leftPlaces, l[k]);
                if (k > 0)
                    m.Add(l[k] > l[k - 1]);
            }
            var label = new LinearExpr[n];
            for (var d = 0; d < n; d++)
                label[d] = d < a ? m.NewIntVar(0, n - a - 1, "") : LinearExpr.Constant(d - a);
            for (var d = 0; d < a; d++)
                m.AddElement(l[d], label, label[d]);
            foreach (var t in rights)
                m.AddElement(p[i][t], label, p[c.Right][t]);
        }

        // Each leaf's order as precedences on its left-chain top's places.
        for (var i = 0; i < nodes.Length; i++)
        {
            if (children[i] is not null)
                continue;
            var top = i;
            while (parent[top] is { Left: true } up)
                top = up.Node;
            if (top == i)
                continue;
            var order = nodes[i].Set.Order().ToArray();
            for (var k = 0; k + 1 < order.Length; k++)
                m.Add(p[top][order[k]] < p[top][order[k + 1]]);
        }

        // Pieces: each connected piece (by shared vertices) one block of slots, in vertex-range order.
        var slot = p[0];
        var root = Enumerable.Range(0, vertexCount).ToArray();
        int Find(int x)
        {
            while (root[x] != x)
                x = root[x] = root[root[x]];
            return x;
        }
        foreach (var t in soup)
        {
            root[Find(t[0])] = Find(t[1]);
            root[Find(t[1])] = Find(t[2]);
        }
        var start = 0;
        foreach (var piece in Enumerable.Range(0, count).GroupBy(t => Find(soup[t][0])).OrderBy(g => g.Min(t => soup[t].Min())))
        {
            var size = piece.Count();
            foreach (var t in piece)
            {
                m.Add(slot[t] >= start);
                m.Add(slot[t] < start + size);
            }
            start += size;
        }

        // Faces: each triangle alone or in exactly one quad; corner starts.
        var lone = Enumerable.Range(0, count).Select(_ => m.NewBoolVar("")).ToArray();
        var turn = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, 3).Select(_ => m.NewBoolVar("")).ToArray()).ToArray();
        var roles = Enumerable.Range(0, count).Select(t => new List<ILiteral> { lone[t] }).ToArray();
        var quadOf = new List<(int First, int Second, BoolVar Taken)>();
        foreach (var t in Enumerable.Range(0, count))
            m.AddExactlyOne(turn[t]);
        foreach (var (first, second, starts) in problem.Quads)
        {
            var z = m.NewBoolVar("");
            quadOf.Add((first, second, z));
            roles[first].Add(z);
            roles[second].Add(z);
            m.Add(slot[second] == slot[first] + 1).OnlyEnforceIf(z);
            var ways = new List<ILiteral> { z.Not() };
            foreach (var (rf, rs) in starts)
            {
                var y = m.NewBoolVar("");
                m.AddImplication(y, turn[first][rf]);
                m.AddImplication(y, turn[second][rs]);
                m.AddImplication(y, z);
                ways.Add(y);
            }
            m.AddBoolOr(ways);
        }
        foreach (var t in Enumerable.Range(0, count))
            m.AddExactlyOne(roles[t]);

        // The vertex rule: ids in order of first use.
        var uses = Enumerable.Range(0, vertexCount).Select(_ => new List<LinearExpr>()).ToArray();
        for (var t = 0; t < count; t++)
            for (var j = 0; j < 3; j++)
                uses[soup[t][j]].Add(3 * slot[t] + LinearExpr.Sum(Enumerable.Range(0, 3).Select(k => (LinearExpr)turn[t][k] * ((j - k + 3) % 3))));
        var met = Enumerable.Range(0, vertexCount).Select(_ => m.NewIntVar(0, 3 * count - 1, "")).ToArray();
        for (var v = 0; v < vertexCount; v++)
        {
            if (uses[v].Count == 0)
                return (null, $"order solver: vertex {v} is on no triangle");
            m.AddMinEquality(met[v], uses[v]);
            if (v > 0)
                m.Add(met[v] > met[v - 1]);
        }

        // The lag window per face.
        for (var t = 0; t < count; t++)
            if (soup[t].Max() + problem.Lag + 1 is var v && v < vertexCount)
                m.Add(met[v] >= 3 * slot[t]).OnlyEnforceIf(lone[t]);
        foreach (var (f, s, z) in quadOf)
            if (Math.Max(soup[f].Max(), soup[s].Max()) + problem.Lag + 1 is var v && v < vertexCount)
                m.Add(met[v] >= 3 * slot[f]).OnlyEnforceIf(z);

        var solver = new CpSolver
        {
            StringParameters = $"num_workers:{Math.Min(4, Environment.ProcessorCount)} max_time_in_seconds:{problem.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        };
        var clock = Stopwatch.StartNew();
        var status = solver.Solve(m);
        var report = $"order solver (CP-SAT, {nodes.Length} nodes): {status} in {clock.Elapsed.TotalSeconds:F1} s";
        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
            return (null, report);
        return ([.. Enumerable.Range(0, count).OrderBy(t => solver.Value(slot[t]))], report);
    }
}
