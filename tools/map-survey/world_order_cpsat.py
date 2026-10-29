"""World soup input order as one CP-SAT model (prototype).

problem json (WorldGeometry.ForwardSearch, DECOMPILE_SEARCH_DUMP): soup corners, the faces each triangle can
begin, the BVH trace in pre-order, the source's order when known.

Model, per node of the BVH, p[x] = x's place in the node's entry (a permutation):
  leaf: p fixed to the shipped order (ascending soup ids).
  split by count: the left set's places < a; children's places are the node's (minus a on the right).
  SAH (swap partition): l[i] = place of the i-th left (increasing; the left child's order is the node's
  restricted). The right child's place of a right one at place d: follow d -> l[d] while d < a, then d - a.
  (Lomuto: a left at i swaps a[i] with the front of the right block, so the cell a[d] for d >= a is final
  once written, and a left at place p copies the cell of its own left rank.) Encoded as label[d] =
  label[l[d]] for d < a, label[d] = d - a for d >= a, right place = label[p[x]].
Input: root p = slot of each triangle; a quad's second triangle in the slot after its first; each triangle
alone or in exactly one quad; corner times 3*slot + corner index under the face's start; vertex ids are the
order of first use: first(v) < first(v+1).

usage: world_order_cpsat.py problem.json [--truth] [--test-partition] [--time S] [--out order.txt --vertices verts.txt]
"""
import json, sys, random, time
from ortools.sat.python import cp_model

args = sys.argv[1:]
P = json.load(open(args[0]))
soup = P["soup"]
T = len(soup)
V = P["vertices"]
trace = P["trace"]


def queue_partition(entry, left):
    """BvhRun's SAH node: returns (left entry, right entry)."""
    q, h, out_l = [], 0, []
    for t in entry:
        if t in left:
            if len(q) > h:
                q.append(q[h]); h += 1
            out_l.append(t)
        else:
            q.append(t)
    return out_l, q[h:]


def label_partition(entry, left):
    n = len(entry)
    a = sum(1 for t in entry if t in left)
    l = [d for d, t in enumerate(entry) if t in left]
    label = {}

    def lab(d):
        while d < a:
            d = l[d]
        return d - a
    right = [None] * (n - a)
    for d, t in enumerate(entry):
        if t not in left:
            right[lab(d)] = t
    return [t for t in entry if t in left], right


if "--test-partition" in args:
    for _ in range(20000):
        n = random.randint(1, 12)
        entry = list(range(n)); random.shuffle(entry)
        left = set(random.sample(entry, random.randint(0, n)))
        assert queue_partition(entry, left) == label_partition(entry, left), (entry, left)
    print("partition rule = BvhRun on 20000 random nodes")

# The tree from the pre-order trace.
nodes = []


def read(at):
    i = at[0]; at[0] += 1
    n = trace[i]
    node = {"set": n["set"], "left": n["left"], "byCount": n["byCount"], "children": None}
    nodes.append(node)
    assert len(nodes) - 1 == i
    if n["left"] is not None:
        lc = read(at); rc = read(at)
        node["children"] = (lc, rc)
    return i


read([0])
assert sorted(nodes[0]["set"]) == list(range(T))

m = cp_model.CpModel()
p = [None] * len(nodes)  # per node: dict soup id -> IntVar
for i, n in enumerate(nodes):
    N = len(n["set"])
    if n["children"] is None:
        p[i] = {t: k for k, t in enumerate(sorted(n["set"]))}  # constants
    else:
        p[i] = {t: m.NewIntVar(0, N - 1, f"p{i}_{t}") for t in n["set"]}
        m.AddAllDifferent(list(p[i].values()))

for i, n in enumerate(nodes):
    if n["children"] is None:
        continue
    lc, rc = n["children"]
    left = set(n["left"])
    S = n["set"]
    N, a = len(S), len(left)
    A = [t for t in S if t in left]
    B = [t for t in S if t not in left]
    assert set(nodes[lc]["set"]) == left and set(nodes[rc]["set"]) == set(B)
    if n["byCount"]:
        for t in A:
            m.Add(p[i][t] == p[lc][t])
        for t in B:
            m.Add(p[i][t] == p[rc][t] + a)
        continue
    # l[k] = place of the k-th left: the left child's inverse, through the node's places.
    inv = [m.NewIntVar(0, a - 1, f"inv{i}_{k}") for k in range(a)]
    pl = [p[lc][t] for t in A]
    if all(isinstance(x, int) for x in pl):
        inv_c = [None] * a
        for j, x in enumerate(pl):
            inv_c[x] = j
        for k in range(a):
            m.Add(inv[k] == inv_c[k])
    else:
        pl = [x if not isinstance(x, int) else m.NewConstant(x) for x in pl]
        m.AddInverse(pl, inv)
    # the k-th left has k lefts and at most all rights before it
    l = [m.NewIntVar(k, k + (N - a), f"l{i}_{k}") for k in range(a)]
    pa = [p[i][t] for t in A]
    for k in range(a):
        m.AddElement(inv[k], pa, l[k])
        if k:
            m.Add(l[k] > l[k - 1])
    label = [m.NewIntVar(0, N - a - 1, f"lab{i}_{d}") for d in range(a)] + [d - a for d in range(a, N)]
    for d in range(a):
        m.AddElement(l[d], label, label[d])
    for t in B:
        m.AddElement(p[i][t], label, p[rc][t])

# Redundant: a node's entry is its left-chain top's (the root or a right child) restricted to it, so each
# node's known order (a leaf's) is a precedence chain on its chain top's places.
parent = {}
for i, n in enumerate(nodes):
    if n["children"]:
        parent[n["children"][0]] = (i, True)
        parent[n["children"][1]] = (i, False)


def chain_top(i):
    while i in parent and parent[i][1]:
        i = parent[i][0]
    return i


if "--no-prec" not in args:
    for i, n in enumerate(nodes):
        if n["children"] is None:
            x = chain_top(i)
            if x != i:
                o = sorted(n["set"])
                for a_, b_ in zip(o, o[1:]):
                    m.Add(p[x][a_] < p[x][b_])

# Pieces: the soup is built mesh after mesh, so each connected piece (by shared vertices) takes one block of
# slots, the blocks in the order of their vertex ranges.
if "--no-pieces" not in args:
    par = list(range(V))

    def find(x):
        while par[x] != x:
            par[x] = par[par[x]]; x = par[x]
        return x
    for a_, b_, c_ in soup:
        par[find(a_)] = find(b_); par[find(b_)] = find(c_)
    pieces = {}
    for t in range(T):
        pieces.setdefault(find(soup[t][0]), []).append(t)
    start = 0
    for ts in sorted(pieces.values(), key=lambda ts: min(min(soup[t]) for t in ts)):
        for t in ts:
            m.Add(p[0][t] >= start); m.Add(p[0][t] < start + len(ts))
        start += len(ts)

# Input: slots, faces, the vertex rule.
slot = p[0]
role = {t: [] for t in range(T)}
lone = {t: m.NewBoolVar(f"lone{t}") for t in range(T)}
rot = {t: [m.NewBoolVar(f"rot{t}_{k}") for k in range(3)] for t in range(T)}
for t in range(T):
    m.AddExactlyOne(rot[t])
    role[t].append(lone[t])
quads = {}
for t in range(T):
    for b in P["begins"][t]:
        u = b["partner"]
        if u < 0:
            continue
        c = b["corners"]
        rt = soup[t].index(c[0]); ru = soup[u].index(c[3])
        assert [soup[t][(rt + k) % 3] for k in range(3)] == c[:3] and [soup[u][(ru + k) % 3] for k in range(3)] == c[3:]
        quads.setdefault((t, u), set()).add((rt, ru))
qvar = {}
for (t, u), allowed in quads.items():
    z = qvar[(t, u)] = m.NewBoolVar(f"q{t}_{u}")
    role[t].append(z); role[u].append(z)
    m.Add(slot[u] == slot[t] + 1).OnlyEnforceIf(z)
    # the pair's starts: one of the allowed (rt, ru)
    alts = []
    for rt, ru in sorted(allowed):
        y = m.NewBoolVar("")
        m.AddImplication(y, rot[t][rt]); m.AddImplication(y, rot[u][ru]); m.AddImplication(y, z)
        alts.append(y)
    m.AddBoolOr([z.Not()] + alts)
for t in range(T):
    m.AddExactlyOne(role[t])

firsts = [[] for _ in range(V)]
for t in range(T):
    for j in range(3):
        e = 3 * slot[t] + sum(((j - k) % 3) * rot[t][k] for k in range(3))
        firsts[soup[t][j]].append(e)
fu = [m.NewIntVar(0, 3 * T - 1, f"fu{v}") for v in range(V)]
for v in range(V):
    assert firsts[v], v
    m.AddMinEquality(fu[v], firsts[v])
    if v:
        m.Add(fu[v] > fu[v - 1])

# The lag window (a search bound, as in ForwardSearch; the source needs 7): when a triangle goes in, no more
# than lag vertices past its highest corner have been brought in.
if "--lag" in args:
    lag = int(args[args.index("--lag") + 1])
    # per face: a lone triangle, or a quad by its first triangle and the pair's highest corner
    for t in range(T):
        v = max(soup[t]) + lag + 1
        if v < V:
            m.Add(fu[v] >= 3 * slot[t]).OnlyEnforceIf(lone[t])
    for (t, u), z in qvar.items():
        v = max(soup[t] + soup[u]) + lag + 1
        if v < V:
            m.Add(fu[v] >= 3 * slot[t]).OnlyEnforceIf(z)

truth = P.get("truth")
if "--truth" in args:
    assert truth and sorted(truth) == list(range(T))
    for k, t in enumerate(truth):
        m.Add(slot[t] == k)
if "--fix-range" in args:
    lo, hi = map(int, args[args.index("--fix-range") + 1].split(":"))
    for k in range(lo, hi):
        m.Add(slot[truth[k]] == k)
if "--prefix" in args:
    n = int(args[args.index("--prefix") + 1])
    for k, t in enumerate(truth[:n]):
        m.Add(slot[t] == k)

# Forward decisions (what ForwardSearch decides): the triangle in each slot, in slot order.
at = [m.NewIntVar(0, T - 1, f"at{k}") for k in range(T)]
m.AddInverse([slot[t] for t in range(T)], at)
if "--fixed" in args:
    m.AddDecisionStrategy(at, cp_model.CHOOSE_FIRST, cp_model.SELECT_MIN_VALUE)
if "--hint" in args:
    order = [int(x) for x in open(args[args.index("--hint") + 1]).read().split()]
    for k, t in enumerate(order):
        m.AddHint(slot[t], k)

solver = cp_model.CpSolver()
if "--fixed" in args and "--portfolio" not in args:
    solver.parameters.search_branching = cp_model.FIXED_SEARCH
solver.parameters.num_workers = 4
solver.parameters.max_time_in_seconds = float(args[args.index("--time") + 1]) if "--time" in args else 600
solver.parameters.log_search_progress = "--log" in args
t0 = time.time()
st = solver.Solve(m)
print(f"{solver.StatusName(st)} in {time.time() - t0:.1f} s ({len(nodes)} nodes)")
if st in (cp_model.OPTIMAL, cp_model.FEASIBLE):
    order = sorted(range(T), key=lambda t: solver.Value(slot[t]))
    if truth:
        same = sum(1 for a_, b_ in zip(order, truth) if a_ == b_)
        print(f"slots equal to the source's: {same} of {T}")
    if "--out" in args:
        open(args[args.index("--out") + 1], "w").write(" ".join(map(str, order)) + "\n")
