using System.Numerics;
using Source2.Compiler.Io;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A compiled map package back to a Hammer <c>.vmap</c> (docs/DECOMPILE.md). This first
/// stage brings back the entities: every lump's entities, through
/// <see cref="EntityLumpAuthor.Unbuild"/>, placed in the compile's walk order.
/// Geometry, prefabs, instances, paths and world layers are not brought back yet; the
/// report says what each map is missing.
/// </summary>
public static class MapDecompiler
{
    /// <summary>What a decompile wrote and left out.</summary>
    /// <param name="Vmap">The .vmap file.</param>
    /// <param name="Entities">Entities written, the world included.</param>
    /// <param name="Placeholders">Walk positions with nothing in any lump (a hidden, editor-only
    /// or compile-only node), each filled with an editor-only info_target so the walk numbers
    /// the rest as the compile did.</param>
    /// <param name="Notes">What is not brought back, one line each.</param>
    /// <param name="Prefabs">The prefab maps the map's CMapPrefab nodes name, by content path
    /// (targetMapPath, e.g. maps/prefabs/&lt;map&gt;/prefab_108.vmap).</param>
    public sealed record Result(byte[] Vmap, IReadOnlyList<EntityLumpAuthor.SourceEntity> Entities,
                                int Placeholders, IReadOnlyList<string> Notes)
    {
        public IReadOnlyDictionary<string, byte[]> Prefabs { get; init; } = new Dictionary<string, byte[]>();

        /// <summary>A prefab's copies came out past their ids with the map's own nodes all walked.</summary>
        internal bool Overshoot { get; init; }

        /// <summary>The forward on the rebuild gives every copy, the map's own and its prefabs', its id.</summary>
        internal bool IdsMatched { get; init; }
    }

    private const string LayerLumpPrefix = "world_layer_";

    /// <summary>The class of a walk position nothing shipped from.</summary>
    public const string PlaceholderClass = "info_target";

    /// <summary>Decompile the map in <paramref name="files"/> (path to bytes: a package's
    /// entries) named <paramref name="mapName"/>.</summary>
    /// <param name="materials">Tool material hash to name (<see cref="BrushGeometry.MaterialTable"/>) over the
    /// game's materials; the package's own are added. Null: the package's only.</param>
    /// <param name="content">The game's models and materials: the prop_statics in the world's hulls
    /// (StaticProps), and world faces the render does not show, by their surface (WorldGeometry); null:
    /// neither.</param>
    public static Result Decompile(IReadOnlyDictionary<string, byte[]> files, string mapName, FgdSchema schema,
                                   IReadOnlyDictionary<uint, string>? materials = null,
                                   GameContent? content = null)
    {
        var first = Decompile(files, mapName, schema, materials, null, content);
        if (!first.Overshoot)
            return first;
        // A prefab's copies come out past their ids: the map's own nodes reach too high, so its
        // highest are copies of one collapse whose order showed nothing (they land in walk order),
        // and their ids past a prefab's block say a later round, an instance of a group that holds
        // the instance (MapInstances.Expand: one round per level). Each candidate is kept only when
        // the forward on the rebuild gives every copy its id.
        for (var count = 1; count <= MaxRetryCopies; count++)
            for (var wraps = 0; wraps <= MaxRetryWraps; wraps++)
                if (Decompile(files, mapName, schema, materials, new Retry(count, wraps), content) is { IdsMatched: true } retried)
                    return retried with
                    {
                        Notes = [.. retried.Notes, $"instances: the map's {count} highest nodes are one collapse through {wraps} wrapper group(s), the first reading (fewest copies, then wrappers) whose copy ids land"],
                    };
        return first;
    }

    private const int MaxRetryCopies = 16;
    private const int MaxRetryWraps = 2;

    /// <summary>How many group places a shared prefab map's copy frame search tries at most.</summary>
    private const long CopyFrameBudget = 20_000;

    /// <summary>A rebuild's forced reading of the map's own copies: its <paramref name="Copies"/>
    /// highest-id nodes as one collapse, instanced through <paramref name="Wraps"/> groups that each
    /// hold only the next instance.</summary>
    private sealed record Retry(int Copies, int Wraps);

    private static Result Decompile(IReadOnlyDictionary<string, byte[]> files, string mapName, FgdSchema schema,
                                    IReadOnlyDictionary<uint, string>? materials, Retry? retry,
                                    GameContent? content)
    {
        var notes = new List<string>();
        var lumps = files.Where(f => f.Key.EndsWith(".vents_c", StringComparison.OrdinalIgnoreCase)
                                     && f.Key.StartsWith($"maps/{mapName}/", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        if (lumps.Count == 0)
            throw new InvalidDataException($"no entity lump for maps/{mapName} in the package");

        // Every lump's raw entities first: the fixup prefix is a property of the map. A
        // template lump (<id path>#<key>) comes last: its entities are COPIES, placed
        // relative to the template and renamed (EntityLumpSet), and the original, where the
        // template kept it, is the one to write.
        var raw = new List<(string Lump, KVObject Entity)>();
        foreach (var (path, bytes) in lumps.OrderBy(l => IsTemplateLump(l.Key) ? 1 : 0))
            foreach (var e in LumpEntities(bytes, path))
                raw.Add((path, e));
        // A prefab loaded at runtime ships as a point_prefab, which takes no walk index: each comes back as
        // that CMapPrefab, standing before the next node its map ships after it (RuntimePrefab).
        var runtime = new List<(string Chain, RuntimePrefab Prefab)>();
        for (var i = 0; i < raw.Count; i++)
        {
            if (RuntimePrefabOf(raw[i].Entity, raw[i].Lump) is not { } r)
                continue;
            var chain = ChainOf(IdPath(raw[i].Entity));
            var before = Enumerable.Range(i + 1, raw.Count - i - 1)
                .Where(k => raw[k].Lump == raw[i].Lump && ChainOf(IdPath(raw[k].Entity)) == chain && RuntimePrefabOf(raw[k].Entity, raw[k].Lump) is null)
                .Select(k => SourceIdOf(raw[k].Entity)).FirstOrDefault(s => s >= 0, int.MaxValue);
            runtime.Add((chain, r with { Before = before }));
            if (Path.GetFileNameWithoutExtension(raw[i].Lump).StartsWith(LayerLumpPrefix, StringComparison.OrdinalIgnoreCase))
                notes.Add($"point_prefab {IdPath(raw[i].Entity)}: in {Path.GetFileName(raw[i].Lump)}; its prefab is placed in the world, not in that layer");
        }
        raw.RemoveAll(r => RuntimePrefabOf(r.Entity, r.Lump) is not null);
        var prefabs = PrefabFixups(raw.Select(r => (IdPath(r.Entity), Name(r.Entity))).ToList(), schema, out var mapFixup);
        string? FixupOf(string idPath)
        {
            var chain = ChainOf(idPath);
            return chain.Length > 0 && prefabs.TryGetValue(chain, out var p) && p.NamePrefix is { } own
                ? EntityLumpAuthor.NameFixupPrefix + own
                : mapFixup ? EntityLumpAuthor.NameFixupPrefix : null;
        }

        var entities = new List<EntityLumpAuthor.SourceEntity>();
        var layerOf = new Dictionary<EntityLumpAuthor.SourceEntity, string>();
        var fileOf = new Dictionary<EntityLumpAuthor.SourceEntity, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // A lump from an older compiler carries no compile_source_id (GROUND_TRUTH 265), so the walk
        // index of each node is not in the package. The nodes are written in lump order instead
        // (world 0, the rest from 1): a choice of the decompile, which states nothing about the
        // older compiler's walk, and leaves no positions for placeholders.
        var older = raw.Count > 0 && raw.All(r => SourceIdOf(r.Entity) < 0);
        var lumpOrder = 1;
        if (older)
            notes.Add("older lump format (no compile_source_id): nodes in lump order, the walk itself not known; read with the current layout, not verified against the compiler that built it");
        foreach (var (lump, e) in raw)
        {
            var source = EntityLumpAuthor.Unbuild(e, schema, FixupOf(IdPath(e)));
            if (older)
                source = source with { SourceId = source.Entity.IsWorld ? 0 : lumpOrder++ };
            if (IsTemplateLump(lump) && !seen.Contains(source.IdPath))
                source = FromTemplateCopy(source, lump, entities, notes);
            // A template lump (<id>#entitylumpname) holds a copy of every entity a
            // point_template names, each with the original's hammerUniqueId
            // (EntityLumpSet): one source node.
            if (source.IdPath.Length > 0 && !seen.Add(source.IdPath))
                continue;
            // A world layer ships as world_layer_<name> (EntityLumpSet).
            var lumpName = Path.GetFileNameWithoutExtension(lump);
            if (lumpName.StartsWith(LayerLumpPrefix, StringComparison.OrdinalIgnoreCase))
                layerOf[source] = lumpName[LayerLumpPrefix.Length..];
            entities.Add(source);
            fileOf[source] = lump;
        }

        var table = new Dictionary<uint, string>(materials ?? new Dictionary<uint, string>());
        foreach (var (hash, name) in BrushGeometry.MaterialTable(files.Keys.Where(k => k.EndsWith(".vmat_c", StringComparison.OrdinalIgnoreCase))))
            table.TryAdd(hash, name);

        var world = entities.FirstOrDefault(e => e.Entity.IsWorld)
                    ?? throw new InvalidDataException("no worldspawn in the lumps");

        // One map per scope: the map's own nodes, and each prefab's (its id path less the node's
        // own id: 108:3 is node 3 of prefab 108's map, 108:5:3 node 3 of prefab 5 inside it).
        var scopes = new Dictionary<string, Scope>(StringComparer.Ordinal) { [""] = new("", null) };
        Scope ScopeOf(string chain)
        {
            if (scopes.TryGetValue(chain, out var scope))
                return scope;
            var parent = ScopeOf(chain.Contains(':') ? chain[..chain.LastIndexOf(':')] : "");
            scope = scopes[chain] = new Scope(chain, parent);
            parent.Children.Add(scope);
            return scope;
        }
        foreach (var e in entities.Where(e => !e.Entity.IsWorld))
            ScopeOf(ChainOf(e.IdPath)).Entities.Add(e);
        foreach (var (chain, prefab) in runtime)
            ScopeOf(chain).Runtime.Add(prefab);

        // One prefab map placed several times is one source: every placement's nodes carry the
        // first placement's compile_source_ids (MapEntities.SourceIds), so placements under one
        // parent whose nodes have the same inner ids and indices are one map. The first in the
        // walk (the first in lump order) is written; the others are prefab nodes naming its file.
        foreach (var sc in scopes.Values)
            sc.Lump = sc.Entities.Select(e => entities.IndexOf(e)).DefaultIfEmpty(int.MaxValue).Min();
        int LumpIndex(Scope sc) => sc.Lump;
        var families = new List<List<Scope>>();
        string Signature(Scope sc) => string.Join(",", sc.Entities.Select(e => $"{e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..]}@{e.SourceId}").Order(StringComparer.Ordinal));
        // The placements need not share a parent: s2c_prefabprobe9's 115 places at the root the map
        // 112's 2156..2158 place inside 112's map, and all four carry the same indices (the walk meets
        // the loaded map once). Scopes are taken in lump order, a parent before its children, and one
        // under a placement placed again is that placement's own (written with the first) and joins none.
        var bySignature = new Dictionary<string, List<Scope>>(StringComparer.Ordinal);
        foreach (var sc in scopes.Values.Where(sc => sc.Parent is not null && sc.Entities.Count > 0).OrderBy(LumpIndex))
        {
            var replaced = false;
            for (var up = sc.Parent; up is not null; up = up.Parent)
                replaced |= up.SameAs is not null;
            if (replaced)
                continue;
            var key = Signature(sc);
            if (!bySignature.TryGetValue(key, out var family))
                bySignature[key] = family = [];
            else
                sc.SameAs = family[0];
            family.Add(sc);
        }
        families.AddRange(bySignature.Values.Where(f => f.Count > 1));

        // An instance's copy made inside a prefab's map ships with a plain id but carries its
        // template's walk index (MapInstances.Expand; EntityLumpSet.MainLump), which lies in the
        // prefab's block: the map's own nodes are all walked before its first prefab's world
        // (MapEntities.Walk), so a plain-id node numbered at or past that is such a copy. It
        // belongs to the last prefab whose block starts at or before its index.
        var blocks = scopes.Values.Where(sc => sc.Parent is not null).Select(sc => (Scope: sc, Start: sc.FirstSourceId - 1))
                           .OrderBy(b => b.Start).ToList();
        if (blocks.Count > 0)
            foreach (var e in scopes[""].Entities.Where(e => e.SourceId >= blocks[0].Start).ToList())
            {
                scopes[""].Entities.Remove(e);
                Canonical(blocks.Last(b => b.Start <= e.SourceId).Scope).Copies.Add(e);
            }

        // The scope a placement's nodes are written in: its family's first placement, under its
        // parent's (a prefab inside a prefab placed again is the first placement's same prefab).
        Scope Canonical(Scope sc)
        {
            if (sc.Parent is null)
                return sc;
            var parent = Canonical(sc.Parent);
            var s = parent == sc.Parent ? sc : parent.Children.FirstOrDefault(c => c.NodeId == sc.NodeId) ?? sc;
            return s.SameAs is { } first && first != s ? Canonical(first) : s;
        }


        foreach (var placements in families.OrderBy(f => f[0].Chain.Count(c => c == ':')))
        {
            // The map's placements inside prefabs placed again ship too and constrain its frame as
            // much as its own (s2c_prefabprobe9's 113:2157 is 112:2156's map through prefabs 113 and
            // 2157): solved with them, in lump order (its instances' collapses run in tree order).
            var through = scopes.Values.Where(sc => sc.Parent is not null && sc.Entities.Count > 0 && !placements.Contains(sc)
                                                    && Canonical(sc) == placements[0]);
            SolveFrame([.. placements.Concat(through).OrderBy(LumpIndex)], notes);
            // Solved over the lump's world placements, a nested placement's prefab transform is its
            // outer prefab's and its own composed (SettleWorld.PrefabPlacement moves the inner prefab
            // node by the outer's matrix): its prefab node stands in the outer map's frame.
            IntoOuterFrame(placements, notes);
        }
        // A later placement of a family whose map places prefabs moves those too: it is solved on the
        // map's own nodes, so it is checked on the nested ones as well (through both prefabs) and
        // moved to floats near it that land them all.
        foreach (var placements in families)
            foreach (var later in placements.Skip(1).Where(p => p.Children.Count > 0))
                LandNested(placements[0], later, Canonical, notes);
        // A later placement whose node ships another value of a key than the first's: one map cannot
        // hold both, so the key is bound to a variable of the map, the first's value its own and the
        // later placement's an override (MapVariables; s2c_varprobe's priority 5 and 0).
        foreach (var placements in families)
            PlanVariables(placements, notes);

        // A prefab map's own instances collapsed more than once (its copies' indices start again):
        // planned as the map's own are (InstancePlan), over the prefab map's nodes. A shared prefab
        // map's copies are one collapse per placement (SolveFrame), and one collapse is a template
        // group of its own (TemplatePlan). A map that places prefabs is left to TemplatePlan too (its
        // prefabs take blocks among its collapses', which InstancePlan does not model), and so is
        // every prefab map of a map with shared ones: s2c_prefabprobe9's planned groups landed 78 walk
        // indices late, the shared maps' blocks not being modelled either.
        foreach (var sc in scopes.Values.Where(sc => families.Count == 0 && sc.Parent is not null && sc.Copies.Count > 0
                                                     && sc.Children.Count == 0 && CollapseCount(sc.Copies) > 1))
        {
            sc.Plan = InstancePlan.Build([.. sc.Copies.Select(c => (c, entities.IndexOf(c), fileOf[c]))],
                                         [.. sc.Entities.Select(w => (w, entities.IndexOf(w), fileOf[w]))], notes);
            if (sc.Plan is not null)
                notes.Add($"prefab {sc.Chain}: {sc.Copies.Count} copies rebuilt as {sc.Plan.Collapses.Count} instances of {sc.Plan.Groups.Count} groups");
        }

        // The map's own instance copies: their template indices repeat (each template walks once, every
        // collapse carries its index), and their ids start past every node of the map
        // (MapInstances.Expand), so from the lowest id that carries a repeated index on, every node of
        // the map's own is a copy.
        InstancePlan? instancePlan = null;
        List<EntityLumpAuthor.SourceEntity> mainCopies = [];
        var wraps = retry?.Wraps ?? 0;
        {
            var own = scopes[""].Entities;
            var repeated = own.GroupBy(e => e.SourceId).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            // One collapse repeats no index, but its copies land where their instance's parent's
            // subtree ends, after nodes walked past their template: a lump file's walked nodes ship in
            // walk order, so a node shipped after one of a higher index is a copy. (A collapse that
            // lands in walk order compiles the same flattened.)
            if (repeated.Count == 0)
                repeated = [.. own.GroupBy(e => fileOf[e]).SelectMany(g =>
                {
                    var high = int.MinValue;
                    var late = new List<EntityLumpAuthor.SourceEntity>();
                    foreach (var e in g.OrderBy(entities.IndexOf))
                    {
                        if (e.SourceId < high) late.Add(e);
                        high = Math.Max(high, e.SourceId);
                    }
                    return late;
                })];
            if (repeated.Count == 0 && retry is not null)
                repeated = [.. own.OrderByDescending(e => e.Entity.NodeId).Take(retry.Copies)];
            if (repeated.Count > 0)
            {
                var start = repeated.Min(e => e.Entity.NodeId);
                var copyList = own.Where(e => e.Entity.NodeId >= start).ToList();
                var walkedList = own.Where(e => e.Entity.NodeId < start).ToList();
                instancePlan = InstancePlan.Build([.. copyList.Select(c => (c, entities.IndexOf(c), fileOf[c]))],
                                                  [.. walkedList.Select(w => (w, entities.IndexOf(w), fileOf[w]))], notes);
                if (instancePlan is not null)
                {
                    mainCopies = copyList;
                    foreach (var c in copyList)
                        own.Remove(c);
                    notes.Add($"instances: {copyList.Count} copies rebuilt as {instancePlan.Collapses.Count} instances of {instancePlan.Groups.Count} groups");
                }
            }
        }

        var main = scopes[""];
        main.Builder = new VmapBuilder(mapName);
        // The lump's mapUsageType is the world's, as the export's table names it (EntityLumpAuthor
        // .MapUsageTypeName, GROUND_TRUTH 266): a name of the table compiles to itself.
        var usage = world.Raw is { } rawWorld && rawWorld.TryGetValue("keyValues3Data", out var kv3) && kv3.TryGetValue("values", out var worldValues)
                    && worldValues.TryGetValue("mapUsageType", out var u) ? EntityLumpAuthor.Plain(u) : null;
        if (usage is not null && !EntityLumpAuthor.MapUsageTypes.Contains(usage))
        {
            notes.Add($"worldspawn: mapUsageType {usage} is not a name the export writes; standard");
            usage = null;
        }
        main.Builder.SetWorld(world.Entity.NodeId, world.Entity.Keys, world.Entity.Connections, mapFixup, usage ?? "standard");

        var placeholders = 0;
        var at = 1;
        var overshoot = false;
        var matched = false;
        // Every collapse's copies, before the template writer keeps only the first's (TemplatePlan).
        var allCopies = scopes.Values.ToDictionary(sc => sc, sc => sc.Copies.OrderBy(c => c.Entity.NodeId).ToList());
        // The prefab maps the compile read, as its .vmap_c records them (sorted, the map's own among
        // them): with one prefab map written and one recorded, it takes that file's name. More cannot
        // be paired with their placements from the package; their names are noted.
        var recorded = MapInputs(files, mapName).Select(i => i.File)
            .Where(f => f.EndsWith(".vmap", StringComparison.OrdinalIgnoreCase) && !f.Equals($"maps/{mapName}.vmap", StringComparison.OrdinalIgnoreCase)).ToList();
        // A shared prefab map is written once, by its family's first placement: a scope that is, or lies
        // under, a later placement writes none.
        bool Later(Scope? sc) => sc is not null && (families.Any(f => f.IndexOf(sc) > 0) || Later(sc.Parent));
        var written = scopes.Values.Where(sc => sc.Parent is not null && !Later(sc)).ToList();
        // A runtime prefab's map is read too (its point_prefab names it) and is not written here; it
        // pairs with no placement unless a merged prefab uses the same file.
        var runtimeMaps = runtime.Select(r => r.Prefab.MapPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (recorded.Count(f => !runtimeMaps.Contains(f)) is var merged && merged < recorded.Count && (merged > 0 || written.Count == 0))
            recorded = [.. recorded.Where(f => !runtimeMaps.Contains(f))];
        if (written.Count == 1 && recorded.Count == 1)
            written[0].FileName = Path.GetFileNameWithoutExtension(recorded[0]);
        else if (recorded.Count > 0)
            notes.Add($"prefab maps: the compile read {string.Join(", ", recorded)}; {written.Count} written under their chains (which placement used which file is not in the package)");
        Emit(main);
        // The map's nav settings from the .nav's generation parameters (NavSettings): a CMapNavData, unless
        // the nav is the fixed empty one a map with no CMapNavData and no walkable seed gets. A made-up id,
        // before the copy ids are matched.
        if (files.TryGetValue($"maps/{mapName}.nav", out var nav) && !nav.AsSpan().SequenceEqual(NavFile.EmptyDefault()))
        {
            var (navSettings, byte59, why) = NavSettings.FromNav(nav);
            if (navSettings is null)
                notes.Add($"nav: maps/{mapName}.nav not read ({why}); no CMapNavData");
            else
            {
                main.Builder!.AddNavData(main.NewId!(), navSettings);
                if (byte59 != 0)
                    notes.Add($"nav: the generation parameters' last byte is {byte59}, which the map's settings are not known to set; left 0");
            }
        }
        // The world's own meshes, from the physics soups and the render nodes (WorldGeometry), only as an
        // experiment (DECOMPILE_WORLD_MESHES=1): the soups' positions carry each source mesh's placement
        // rounding, which a mesh at the world's origin does not give back (docs/DECOMPILE.md "World
        // geometry"). Written after the map's nodes so they take made-up ids, before the copy ids are
        // matched, which pads past them.
        // The pieces reach the soups through PartOrder (a CRT qsort on shape type after every insert: with
        // one type a fixed permutation of the input), so they are written in its inverse, as a brush
        // entity's hulls are.
        var worldMeshes = Environment.GetEnvironmentVariable("DECOMPILE_WORLD_MESHES") == "1" ? WorldGeometry.Read(files, mapName, table, notes,
            content is null ? null : name => Physics.WorldCollision.ReadMaterial(name, content.Material, content.CollisionProperty).SurfaceKey) : [];
        // The prop_statics from the world's hulls (StaticProps), written after the world meshes. The part
        // sorts the shapes by type after every insert, the meshes (3) and the props' hulls (2) together,
        // so both are written in the inverse of that sort over this sequence: the meshes, then each prop's
        // hulls in its model's order, the props in the order that inverse asks for.
        var props = new List<StaticProps.Placed>();
        var shippedHulls = 0;
        if (Environment.GetEnvironmentVariable("DECOMPILE_WORLD_MESHES") == "1" && content is not null)
        {
            var (hulls, _) = StaticProps.WorldHulls(files, mapName);
            shippedHulls = hulls.Count;
            props = StaticProps.Find(hulls, StaticProps.Models(files, mapName), content.Physics, notes);
            if (Environment.GetEnvironmentVariable("DECOMPILE_WORLDDEBUG") == "1")
                notes.Add($"world debug: props' shipped hulls {string.Join(" ", props.OrderBy(p => p.Hulls.Min()).Select(p => $"{Path.GetFileNameWithoutExtension(p.Model)[^5..]}[{string.Join(",", p.Hulls)}]"))}");
        }
        var meshCount = worldMeshes.Count;
        var placedHulls = props.Sum(p => p.Hulls.Length);
        // Where the props stood among the meshes changes the sort's shuffle of its first few (CRT qsort):
        // the props are taken as one run after `before` meshes, the first count (most meshes first)
        // whose inverse gives each prop its hulls in a row.
        int[] ShippedAt(int before)
        {
            var at = Physics.WorldCollision.PartOrder(Enumerable.Range(0, meshCount + placedHulls), i => i < before || i >= before + placedHulls ? 3 : 2);
            var inverse = new int[at.Count];
            for (var k = 0; k < at.Count; k++)
                inverse[at[k]] = k;
            return inverse;
        }
        List<StaticProps.Placed>? PropOrder(int[] shippedAt, int before)
        {
            var byFirst = props.ToDictionary(p => p.Hulls[0]);
            var ordered = new List<StaticProps.Placed>();
            for (var slot = before; slot < before + placedHulls;)
            {
                if (!byFirst.TryGetValue(shippedAt[slot], out var prop)
                    || Enumerable.Range(0, prop.Hulls.Length).Any(j => shippedAt[slot + j] != prop.Hulls[j]))
                    return null;
                ordered.Add(prop);
                slot += prop.Hulls.Length;
            }
            return ordered;
        }
        var (meshesBefore, worldShippedAt, propOrder) = (meshCount, ShippedAt(meshCount), (List<StaticProps.Placed>?)null);
        if (props.Count > 0)
        {
            if (placedHulls == shippedHulls)
                for (var before = meshCount; before >= 0 && propOrder is null; before--)
                    if (PropOrder(ShippedAt(before), before) is { } order)
                        (meshesBefore, worldShippedAt, propOrder) = (before, ShippedAt(before), order);
            if (propOrder is null)
                notes.Add($"prop_static: no place among the meshes gives the shipped hull order ({placedHulls} hulls placed of {shippedHulls}); written as found");
            else
                notes.Add($"prop_static: {props.Count} rebuilt from {placedHulls} of the world's {shippedHulls} hulls, after {meshesBefore} of the {meshCount} world meshes");
        }
        void WriteMesh(int slot)
        {
            var mesh = worldMeshes[worldShippedAt[slot] - placedHulls];
            main.Builder!.AddMesh(main.Builder!.World, main.NewId!(), Vector3.Zero, Vector3.Zero,
                                  [.. mesh.Faces.Select(f => (f.Corners, f.Material))], mesh.Materials,
                                  cornerTexcoords: [.. mesh.Faces.Select(f => f.Texcoords)]);
        }
        for (var slot = 0; slot < meshesBefore; slot++)
            WriteMesh(slot);
        foreach (var prop in propOrder ?? props)
            main.Builder!.AddEntity(main.NewId!(), [new("classname", "prop_static"), new("model", prop.Model)],
                                    prop.Origin, prop.Angles, Vector3.One, []);
        for (var slot = meshesBefore + placedHulls; slot < meshCount + placedHulls; slot++)
            WriteMesh(slot);
        // A shared prefab map is written once, so a brush entity its first placement has no hulls for
        // has none in any placement: noted for each later one whose own model the package lacks too.
        foreach (var placements in families)
            foreach (var e in placements[0].Entities.Where(e => e.BrushModel is { } m && !files.ContainsKey(m + "_c")))
                foreach (var x in scopes.Values.Where(sc => sc != placements[0] && Canonical(sc) == placements[0]).SelectMany(p => p.Entities)
                                            .Where(x => x.IdPath[(x.IdPath.LastIndexOf(':') + 1)..] == e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..]
                                                        && x.BrushModel is { } xm && !files.ContainsKey(xm + "_c")))
                    notes.Add($"{x.Entity.ClassName} {x.IdPath}: placed from prefab {placements[0].Chain}'s map, and its model {x.BrushModel} is not in the package either; no geometry");
        if (placeholders > 0)
            notes.Add($"{placeholders} walk positions shipped nothing (hidden, editor-only or compile-only nodes); filled with editor-only {PlaceholderClass}");
        MatchCopyIds();
        var prefabFiles = scopes.Values.Where(sc => sc.Parent is not null && sc.Builder is not null && sc.SameAs is null)
                                .ToDictionary(sc => sc.Path(mapName), sc => sc.Builder!.Write(), StringComparer.OrdinalIgnoreCase);

        // The compile numbers nodes in walk order (compile_source_id), the world 0
        // (MapEntities.Walk): a map's own children in tree order, then each prefab's map where
        // its parent's children end, the prefab map's world one index ahead of its nodes. Writing
        // each scope's nodes in that order, its prefabs after them, walks them the same way. A
        // position no lump holds is a node that walked and shipped nothing: a placeholder keeps
        // the count. A layer's entities sit in a layer node; a run of them broken by others
        // opens a second node of the same name, so the walk still meets them in order.
        //
        // Node ids the decompile makes up (placeholders, meshes, groups, instances) come from
        // below the ids the compile shows, so none raises the map's highest id, which is where
        // the instance copies' ids start (MapInstances.Expand).
        void Emit(Scope scope)
        {
            var builder = scope.Builder!;
            var used = new HashSet<int>(scope.Entities.Select(e => e.Entity.NodeId).Concat(scope.Children.Select(c => c.NodeId))
                                                      .Concat(scope.Runtime.Select(r => r.NodeId)));
            if (scope.Parent is null)
                used.Add(world.Entity.NodeId);
            else
                used.Add(1);
            var pool = 2;
            int NewId()
            {
                while (used.Contains(pool))
                    pool++;
                used.Add(pool);
                return pool;
            }
            int Take(int id)
            {
                used.Add(id);
                return id;
            }
            var shownMax = used.Count > 0 ? used.Max() : 0;
            string? openLayer = null;
            DmxBinary.Element? layerNode = null;
            void FillTo(int sourceId, DmxBinary.Element? parent = null)
            {
                for (; at < sourceId; at++, placeholders++)
                {
                    builder.AddEntity(NewId(), [new("classname", PlaceholderClass)], Vector3.Zero, Vector3.Zero, Vector3.One, [],
                                      editorOnly: true, parent: parent);
                    openLayer = null;
                }
            }

            // The scope's own nodes, and its copies' template group where the walk meets it.
            var templates = scope.Plan is null ? TemplatePlan(scope, notes) : null;
            var items = scope.Entities.Select(e => (e.SourceId, Entity: (EntityLumpAuthor.SourceEntity?)e, Template: (List<TemplateSlot>?)null)).ToList();
            if (templates is not null)
                items.Add((templates.First(t => t.Copy is not null).Copy!.SourceId, null, templates));
            DmxBinary.Element? group = null;

            // The map's own instances (InstancePlan): each template group where the walk meets its
            // first template; the instances in block order (the bake collapses them in tree order),
            // those whose copies land before walk index i in a group placed at i, its subtree ending
            // there (MapInstances.EmitAt), and those whose copies land at the walk's end at the root,
            // where they stand between. A nested group's instance sits in its outer template.
            var plan = scope.Parent is null ? instancePlan : scope.Plan;
            var templateFixup = FixupOf(scope.Parent is null ? ":0" : scope.Chain + ":0");
            var planGroups = new Dictionary<InstancePlan.Group, DmxBinary.Element>();
            var planItems = new List<(int At, int Kind, InstancePlan.Group? Group)>();
            var pending = new Queue<InstancePlan.Collapse>();
            if (plan is not null)
            {
                planItems.AddRange(plan.Groups.Select(g => (g.SourceIds[0], 1, (InstancePlan.Group?)g)));
                planItems.AddRange(plan.Collapses.Where(c => c.Group.Outer is null && c.EmitAt != int.MaxValue).Select(c => c.EmitAt).Distinct()
                                       .Select(at => (at, 0, (InstancePlan.Group?)null)));
                foreach (var c in plan.Collapses.Where(c => c.Group.Outer is null))
                    pending.Enqueue(c);
            }
            // An instance's group may be written after it: the targets are set once all are.
            var targets = new List<(DmxBinary.Element Instance, InstancePlan.Group Group)>();
            DmxBinary.Element? wrapper = null;
            var wrapperAt = -1;
            void PlaceInstance(InstancePlan.Collapse c, DmxBinary.Element? parent)
            {
                // Each wrapper level: a group holding only the next instance, at the origin, instanced
                // unturned where the collapse stands, so its copy moves nothing and the copy's
                // instance collapses a round later where the copy lands (MapInstances.Expand).
                var holder = parent;
                var hops = new List<(DmxBinary.Element Group, DmxBinary.Element? Parent)>();
                for (var k = 0; k < (scope.Parent is null ? wraps : 0); k++)
                {
                    var wrap = builder.AddGroup(NewId());
                    hops.Add((wrap, holder));
                    holder = wrap;
                }
                for (var k = hops.Count - 1; k >= 0; k--)
                    builder.AddInstance(NewId(), hops[k].Group, Vector3.Zero, Vector3.Zero, hops[k].Parent);
                targets.Add((builder.AddInstance(NewId(), builder.World, c.Transform.Origin, c.Transform.Angles, holder), c.Group));
                if (c.HiddenAfter > 0)
                {
                    // A stand-in for the collapses that ship nothing here: a group of empty meshes (slot fillers),
                    // as wide as their blocks, instanced once just after.
                    var standIn = builder.AddGroup(NewId());
                    for (var k = 0; k < c.HiddenAfter - 1; k++)
                        builder.AddSlot(NewId(), standIn);
                    builder.AddInstance(NewId(), standIn, Vector3.Zero, Vector3.Zero, parent);
                }
            }
            void LandAt(int where)
            {
                while (pending.Any(c => c.EmitAt == where))
                {
                    var c = pending.Dequeue();
                    if (c.EmitAt == int.MaxValue)
                    {
                        PlaceInstance(c, null);
                        wrapper = null;
                        continue;
                    }
                    if (wrapper is null || wrapperAt != where)
                        (wrapper, wrapperAt) = (builder.AddGroup(NewId()), where);
                    PlaceInstance(c, wrapper);
                }
            }
            void Nested(InstancePlan.Group g, int before, DmxBinary.Element element, ref int fillers)
            {
                foreach (var inner in plan!.Groups.Where(i => i.Outer == g && i.OuterBefore == before))
                {
                    if (fillers-- <= 0)
                        notes.Add($"instances: group of {g.SourceIds[0]} has no free slot for the instance of {inner.SourceIds[0]}");
                    targets.Add((builder.AddInstance(NewId(), builder.World, inner.InstanceAt.Origin, inner.InstanceAt.Angles, element), inner));
                }
            }
            var plannedAt = new Queue<(int At, int Kind, InstancePlan.Group? Group)>(planItems.OrderBy(i => i.At).ThenBy(i => i.Kind));
            void PlanUpTo(int sourceId, bool inclusive)
            {
                while (plannedAt.Count > 0 && (plannedAt.Peek().At < sourceId || (inclusive && plannedAt.Peek().At == sourceId)))
                {
                    var (where, kind, g) = plannedAt.Dequeue();
                    if (kind == 0)
                    {
                        FillTo(where);
                        LandAt(where);
                        continue;
                    }
                    FillTo(g!.SourceIds[0]);
                    var element = builder.AddGroup(NewId(), at: g.GroupAt);
                    planGroups[g] = element;
                    for (var slot = 0; slot < g.Slots[0]; slot++)
                        builder.AddSlot(NewId(), element);
                    for (var i = 0; i < g.SourceIds.Count; i++)
                    {
                        if (i > 0)
                        {
                            var walkGap = g.SourceIds[i] - g.SourceIds[i - 1] - 1;
                            var slotGap = g.Slots[i] - g.Slots[i - 1] - 1;
                            var fillers = slotGap - walkGap;
                            Nested(g, i, element, ref fillers);
                            for (var k = 0; k < fillers; k++)
                                builder.AddSlot(NewId(), element);
                            if (slotGap < walkGap)
                                notes.Add($"instances: group of {g.SourceIds[0]} has fewer slots than walk indices between {g.SourceIds[i - 1]} and {g.SourceIds[i]}");
                        }
                        FillTo(g.SourceIds[i], element);
                        var copy = g.Collapses[0][i];
                        var t = EntityLumpAuthor.Unbuild(copy.Raw!, schema, templateFixup, instanced: true).Entity;
                        var node = builder.AddEntity(NewId(), t.Keys, g.Local[i].Origin, g.Local[i].Angles, t.Scales, t.Connections, parent: element);
                        if (copy.BrushModel is { } copyModel)
                            AddBrushGeometry(builder, node, copy with { Entity = copy.Entity with { Origin = g.Local[i].Origin, Angles = g.Local[i].Angles } },
                                             copyModel, files, table, notes, NewId, schema?.IsCableClass(copy.Entity.ClassName) == true);
                        at = g.SourceIds[i] + 1;
                    }
                    var trailing = g.Count - g.Slots[^1] - 1;
                    Nested(g, g.SourceIds.Count, element, ref trailing);
                    for (var k = 0; k < trailing; k++)
                        builder.AddSlot(NewId(), element);
                }
            }
            // The map's runtime prefabs, each where the walk meets the node it stands before.
            var runtimeLeft = new Queue<RuntimePrefab>(scope.Runtime);
            void RuntimeUpTo(int sourceId)
            {
                while (runtimeLeft.Count > 0 && runtimeLeft.Peek().Before <= sourceId)
                {
                    var r = runtimeLeft.Dequeue();
                    builder.AddPrefab(r.NodeId, r.MapPath, r.Fixup, "", r.Origin, r.Angles, loadAtRuntime: true, scales: r.Scales);
                }
            }
            foreach (var (sourceId, e, slots) in items.OrderBy(i => i.SourceId))
            {
                PlanUpTo(sourceId, inclusive: true);
                FillTo(sourceId);
                RuntimeUpTo(sourceId);
                if (slots is not null)
                {
                    group = builder.AddGroup(NewId(), at: (scope.CopyGroupAt, Vector3.Zero));
                    foreach (var slot in slots)
                    {
                        if (slot.Copy is { } copy)
                        {
                            FillTo(copy.SourceId, group);
                            // The placement is the copy's own, which a shared map's frame solve has made local
                            // (SolveFrame); the keys are read back through the instanced key table.
                            var t = EntityLumpAuthor.Unbuild(copy.Raw!, schema, FixupOf(scope.Chain + ":0"), instanced: true).Entity;
                            var node = builder.AddEntity(NewId(), t.Keys, copy.Entity.Origin, copy.Entity.Angles, t.Scales, t.Connections, parent: group);
                            if (copy.BrushModel is { } copyModel)
                                AddBrushGeometry(builder, node, copy, copyModel, files, table, notes, NewId, schema?.IsCableClass(copy.Entity.ClassName) == true);
                            at = copy.SourceId + 1;
                        }
                        else if (!slot.Walks)
                            builder.AddSlot(NewId(), group);
                    }
                    continue;
                }
                var layer = layerOf.GetValueOrDefault(e!);
                if (layer is not null && layer != openLayer)
                    layerNode = builder.AddLayer(NewId(), layer);
                openLayer = layer;
                var x = e!.Entity;
                if (x.PathNodes is { } pathNodes)
                {
                    // A path's nodes walk right after it and ship nothing (MapEntities.Walk), so they
                    // take the indices that follow; their ids the ones after the path's where free.
                    var nodeIds = Enumerable.Range(1, pathNodes.Count)
                        .Select(k => !used.Contains(x.NodeId + k) && x.NodeId + k < shownMax ? Take(x.NodeId + k) : NewId()).ToList();
                    if (schema?.IsCableClass(x.ClassName) == true)
                    {
                        // A cable_dynamic's settings from the model it shipped; a cable_static's tube is
                        // in the world geometry, which is not read yet.
                        var cable = e.BrushModel is { } cableModel && files.TryGetValue(cableModel + "_c", out var cableBytes)
                            ? CableSettings.Solve(x, cableBytes, schema, notes)
                            : VmapBuilder.Cable.Factory("");
                        if (e.BrushModel is null)
                            notes.Add($"{x.ClassName} {x.NodeId}: no model of its own (its tube is world geometry); factory settings, no material");
                        else if (!files.ContainsKey(e.BrushModel + "_c"))
                            notes.Add($"{x.ClassName} {x.NodeId}: its model {e.BrushModel} is not in the package (the compile failed it: a missing material); factory settings, no material");
                        builder.AddCable(x.NodeId, x, nodeIds, cable, parent: layer is null ? null : layerNode);
                    }
                    else
                        builder.AddPath(x.NodeId, x, nodeIds, parent: layer is null ? null : layerNode);
                    at = Math.Max(at, e.SourceId + 1 + pathNodes.Count);
                    continue;
                }
                if (schema?.IsCableClass(x.ClassName) == true)
                    notes.Add($"{x.ClassName} {x.NodeId}: not rebuilt as a cable ({string.Join("; ", e.Dropped.Where(d => d.StartsWith("pathNodes", StringComparison.Ordinal)))}); kept as an entity");
                var entityNode = builder.AddEntity(x.NodeId, x.Keys, x.Origin, x.Angles, x.Scales, x.Connections,
                                                   parent: layer is null ? null : layerNode);
                var inner = e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..];
                foreach (var v in scope.Variables.Where(v => v.Inner == inner))
                    VmapBuilder.Bind(entityNode, v.Key, v.Variable);
                if (e.BrushModel is { } model)
                    AddBrushGeometry(builder, entityNode, e, model, files, table, notes, NewId, schema?.IsCableClass(x.ClassName) == true);
                at = Math.Max(at, e.SourceId + 1);
            }
            RuntimeUpTo(int.MaxValue);
            PlanUpTo(int.MaxValue, inclusive: false);
            foreach (var child in scope.Children.OrderBy(c => c.FirstSourceId).ThenBy(LumpIndex))
            {
                // A root collapse whose copies ship ahead of this prefab's nodes: the bake collapses in
                // tree order, a prefab's map lands where its parent's children end and a copy after
                // the maps of the prefabs collapsed before it (MapInstances.EmitAt), so its instance
                // stands before the prefab.
                while (pending.Count > 0 && pending.Peek().EmitAt == int.MaxValue
                       && entities.IndexOf(pending.Peek().Group.Collapses[pending.Peek().Index][0]) < child.Lump)
                    PlaceInstance(pending.Dequeue(), null);
                // So too a prefab map's one template group (s2c_prefabprobe9's fixmid: its instance
                // ahead of its three prefabs collapses first in their round, its copies' ids before
                // their blocks).
                if (group is not null && scope.Instance is null && scope.Copies.Min(entities.IndexOf) < child.Lump)
                    scope.Instance = builder.AddInstance(NewId(), group, scope.CopyInstanceAt, Vector3.Zero);
                var fix = prefabs.GetValueOrDefault(child.Chain);
                if (child.SameAs is { } first)
                {
                    // Placed again: no new indices, its nodes are the first placement's.
                    builder.AddPrefab(child.NodeId, first.Path(mapName), fix?.Own is not null, fix?.TargetName ?? "",
                                      child.Placement.Origin, child.Placement.Angles, child.Overrides);
                    continue;
                }
                // The prefab map's world takes the index ahead of its first node.
                FillTo(child.FirstSourceId - 1);
                at++;
                builder.AddPrefab(child.NodeId, child.Path(mapName), fix?.Own is not null, fix?.TargetName ?? "",
                                  child.Placement.Origin, child.Placement.Angles);
                child.Builder = new VmapBuilder($"{mapName}/{child.Chain}");
                child.Builder.SetWorld(1, [new("classname", "worldspawn")], [], false, "standard");
                foreach (var v in child.Variables)
                    child.Builder.AddVariable(v.Variable, v.Value);
                Emit(child);
            }
            // The instance, at the map's root after its nodes: its copies land where the map's
            // walk ends (the parent's subtree end, MapInstances.EmitAt), and with its transform
            // its group's (both the origin, unturned) the collapse moves nothing
            // (SettleWorld.BakedPlacement), so each copy stands where its template does.
            if (group is not null && scope.Instance is null)
                scope.Instance = builder.AddInstance(NewId(), group, scope.CopyInstanceAt, Vector3.Zero);
            if (plan is not null)
            {
                // The instances left land at the walk's end, at the root. Then the map's highest
                // node id is the first block's start less one.
                while (pending.Count > 0)
                {
                    var c = pending.Dequeue();
                    if (c.EmitAt != int.MaxValue)
                        notes.Add($"instances: collapse at id {c.MinId} found no place to land at walk index {c.EmitAt}");
                    PlaceInstance(c, null);
                }
                foreach (var (instance, g) in targets)
                    instance.Set("target", DmxBinary.AttrType.Element, planGroups[g]);
                // Through wrappers the first block is a wrapper's, below the copies' ids: matched by
                // running the forward once the prefabs are written (MatchCopyIds).
                var highest = builder.NodeIds().Max();
                var wrapped = scope.Parent is null && wraps > 0;
                // The main map's highest id raised by a toolsskip box, which Hammer's save keeps (it drops an
                // empty group, Pad); a prefab map's by an empty group, which takes no slot of its block.
                if (!wrapped && highest < plan.Next0 - 1)
                    _ = scope.Parent is null ? builder.AddSlot(plan.Next0 - 1, builder.World) : builder.AddGroup(plan.Next0 - 1);
                else if (!wrapped && highest > plan.Next0 - 1)
                    notes.Add($"instances: the map's nodes reach id {highest}, past the first block's start {plan.Next0}");
            }
            scope.NewId = NewId;
        }

        // One node more in a map: in a prefab map it widens the prefab's block, so it must take a slot
        // (VmapBuilder.AddSlot; an empty group takes none, MapInstances.Hollow); in the main map it
        // moves the highest node id, which an empty group would too, but Hammer's save drops empty groups
        // (its re-save of the atixref decompile lost the one at 7266, every copy id 22 lower), and keeps
        // the toolsskip box, which walks and ships nothing either.
        DmxBinary.Element Pad(Scope sc) => sc.Builder!.AddSlot(sc.NewId!(), sc.Builder!.World);

        // The copies' ids: each collapse's block starts where the ones before it end, and a
        // prefab's block is its map's copy count plus one (MapInstances.Expand). The forward is
        // run on the rebuilt maps, and a collapse whose copies come out short of their ids has
        // the prefab map before it padded: one node moves the copies by the measured step (one
        // per block of that map before them), and the remainder comes from the map's highest
        // node id, which moves every block by one.
        void MatchCopyIds()
        {
            int Simulated(Scope scope) => SimulatedCopyIds(main, scopes.Values, mapName, schema)
                .Where(c => c.Scope == scope).Select(c => c.Id).DefaultIfEmpty(int.MaxValue).Min();
            // The map's own copies through wrappers: its highest node id moves every block by one.
            if (wraps > 0 && mainCopies.Count > 0 && Simulated(main) is var first && first < mainCopies.Min(c => c.Entity.NodeId))
                main.Builder!.AddSlot(main.Builder!.NodeIds().Max() + mainCopies.Min(c => c.Entity.NodeId) - first, main.Builder!.World);
            // With shared prefab maps every collapse is matched at once (MatchEveryCollapse).
            foreach (var scope in scopes.Values.Where(sc => families.Count == 0 && (sc.Instance is not null || (sc.Plan is not null && sc.Builder is not null))).OrderBy(sc => sc.Copies.Min(c => c.Entity.NodeId)))
            {
                var target = scope.Copies.Min(c => c.Entity.NodeId);
                var simulated = Simulated(scope);
                if (simulated == target)
                    continue;
                if (simulated == int.MaxValue || simulated > target)
                {
                    // Not with shared prefab maps: their frame solves would run again for every reading.
                    overshoot |= simulated != int.MaxValue && instancePlan is null && families.Count == 0;
                    notes.Add($"prefab {scope.Chain}: its instance's copies come out at {simulated}, not {target}; not matched");
                    continue;
                }
                // A node more in this map moves the copies by one for each block of it before them
                // (one per placement); the rest comes from the map's highest node id, which moves
                // every block by one.
                Pad(scope);
                var step = Simulated(scope) - simulated;
                var left = target - simulated - step;
                if (step > 0 && left >= 0)
                {
                    for (var i = 0; i < left / step; i++)
                        Pad(scope);
                    if (left % step > 0)
                        main.Builder!.AddSlot(main.Builder!.NodeIds().Max() + left % step, main.Builder!.World);
                }
                if (Simulated(scope) != target)
                    notes.Add($"prefab {scope.Chain}: its instance's copies come out at {Simulated(scope)}, not {target}; not matched");
            }
            if (families.Count > 0)
                MatchEveryCollapse();
            matched = !notes.Any(n => n.EndsWith("; not matched", StringComparison.Ordinal))
                      && (mainCopies.Count == 0 || SimulatedCopyIds(main, scopes.Values, mapName, schema).Where(c => c.Scope == main)
                                                       .Select(c => c.Id).ToHashSet().IsSupersetOf(mainCopies.Select(c => c.Entity.NodeId)));
        }

        // Every collapse's first copy, not only each map's first: a prefab map inside another widens
        // the outer one's block by its own size, so one map at a time cannot set the steps between
        // collapses (s2c_prefabprobe9: fixinner 3 nodes short puts fixmid's placements 9 too close).
        // Each prefab map that holds nodes and the main map's highest id is a variable; how one node
        // more in each moves every collapse's first copy is measured on the rebuild through the
        // forward (a node added and taken out again), the paddings solved from that (least squares,
        // rounded, never negative), and kept only when every collapse then lands on its id.
        void MatchEveryCollapse()
        {
            var withCopies = scopes.Values.Where(sc => sc.Builder is not null && allCopies[sc].Count > 0 && (sc.Instance is not null || sc.Plan is not null)).ToList();
            List<int> Starts()
            {
                var sim = SimulatedCopyIds(main, scopes.Values, mapName, schema);
                var starts = new List<int>();
                foreach (var sc in withCopies)
                {
                    var target = allCopies[sc];
                    var blocks = CollapseCount(target);
                    var ids = sim.Where(c => c.Scope == sc).Select(c => c.Id).Order().ToList();
                    if (ids.Count == 0 || blocks == 0 || ids.Count % blocks != 0)
                        return [];
                    for (var b = 0; b < blocks; b++)
                        starts.Add(ids[b * (ids.Count / blocks)]);
                }
                return starts;
            }
            var want = new List<int>();
            foreach (var sc in withCopies)
            {
                var sorted = allCopies[sc];
                for (var i = 0; i < sorted.Count; i++)
                    if (i == 0 || sorted[i].SourceId <= sorted[i - 1].SourceId)
                        want.Add(sorted[i].Entity.NodeId);
            }
            var now = Starts();
            if (now.Count != want.Count || now.SequenceEqual(want))
                return;
            var maps = scopes.Values.Where(sc => sc.Parent is not null && sc.Builder is not null && sc.NewId is not null).ToList();
            var columns = new List<int[]>();
            foreach (var sc in maps)
            {
                var probe = Pad(sc);
                columns.Add([.. Starts().Zip(now, (a, b) => a - b)]);
                sc.Builder!.RemoveFromWorld(probe);
            }
            {
                var probe = main.Builder!.AddSlot(main.Builder!.NodeIds().Max() + 1, main.Builder!.World);
                columns.Add([.. Starts().Zip(now, (a, b) => a - b)]);
                main.Builder!.RemoveFromWorld(probe);
            }
            // The fewest maps padded that solve it exactly: subsets of the variables by size.
            var gap = want.Zip(now, (a, b) => (double)(a - b)).ToArray();
            int[]? x = null;
            for (var size = 1; size <= columns.Count && x is null; size++)
                foreach (var subset in Subsets(columns.Count, size))
                {
                    if (LeastSquares([.. subset.Select(j => columns[j])], gap) is not { } part || part.Any(v => v < 0))
                        continue;
                    var full = new int[columns.Count];
                    for (var i = 0; i < subset.Length; i++)
                        full[subset[i]] = part[i];
                    if (want.Select((w, i) => w - now[i] - columns.Select((c, j) => c[i] * full[j]).Sum()).All(r => r == 0))
                    {
                        x = full;
                        break;
                    }
                }
            if (x is null)
            {
                notes.Add($"instances: no paddings land every collapse's copies ({want.Count} collapses); not matched");
                return;
            }
            for (var j = 0; j < maps.Count; j++)
                for (var k = 0; k < x[j]; k++)
                    Pad(maps[j]);
            if (x[^1] > 0)
                main.Builder!.AddSlot(main.Builder!.NodeIds().Max() + x[^1], main.Builder!.World);
            if (!Starts().SequenceEqual(want))
                notes.Add("instances: the paddings solved do not land every collapse's copies; not matched");
        }

        return new Result(main.Builder!.Write(), entities, placeholders, notes)
        {
            Prefabs = prefabFiles, Overshoot = overshoot, IdsMatched = matched,
        };
    }

    /// <summary>
    /// A brush entity's meshes from its model (<see cref="BrushGeometry"/>): one mesh per hull,
    /// placed where the entity is, its corners the hull's in the entity's space, so the
    /// compile's move into the entity's space (BrushHulls.Pieces) is the identity. Returns the
    /// next free node id.
    /// </summary>
    private static void AddBrushGeometry(VmapBuilder builder, DmxBinary.Element node, EntityLumpAuthor.SourceEntity e, string model,
                                         IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<uint, string> table,
                                         List<string> notes, Func<int> newId, bool cable = false)
    {
        var label = $"{e.Entity.ClassName} {e.IdPath}";
        if (!files.TryGetValue(model + "_c", out var bytes))
        {
            notes.Add($"{label}: its model {model} is not in the package (the compile failed it); no geometry");
            return;
        }
        var geometry = BrushGeometry.Read(bytes, table);
        // The mesh node stands at the entity's origin, unturned, as Hammer leaves a mesh drawn
        // under an entity (probe_classes). Each corner is the point the compile's two moves
        // (BrushHulls.PiecesWithCorners: the mesh's CTransform to the world, the entity's
        // inverse back) take to the hull's vertex, bit for bit where one exists.
        var meshNode = DmxBinary.NewElement("CMapMesh", "", Guid.Empty);
        meshNode.Attributes["origin"] = e.Entity.Origin;
        meshNode.Attributes["angles"] = Vector3.Zero;
        var entityNode = DmxBinary.NewElement("CMapEntity", "", Guid.Empty);
        entityNode.Attributes["origin"] = e.Entity.Origin;
        // The entity's angles as the .vmap's reader will hold them (MapNodeAngles).
        entityNode.Attributes["angles"] = SettleWorld.SetAngles(e.Entity.Angles);
        var toWorld = CTransform.FromNode(meshNode).Matrix();
        var toEntity = CTransform.FromNode(entityNode).Inverse().Matrix();
        var fromEntity = CTransform.FromNode(entityNode).Matrix();
        var fitted = new Dictionary<Vector3, Vector3>();
        Vector3 Fit(Vector3 h)
        {
            if (!fitted.TryGetValue(h, out var v))
                fitted[h] = v = CornerFor(h, e.Entity.Origin, toWorld, toEntity, fromEntity);
            return v;
        }
        // A mesh at the world's origin: its move is the identity, so every world point is a corner.
        var atZero = new Dictionary<Vector3, Vector3>();
        var identity = CTransform.FromNode(DmxBinary.NewElement("CMapMesh", "", Guid.Empty)).Matrix();
        Vector3 FitAtZero(Vector3 h)
        {
            if (!atZero.TryGetValue(h, out var v))
                atZero[h] = v = CornerFor(h, Vector3.Zero, identity, toEntity, fromEntity);
            return v;
        }
        // A mesh turned with its entity, as Hammer turns a brush entity's meshes with it: the two moves
        // nearly cancel, and the corner is searched on the mesh's own floats.
        var turnedNode = DmxBinary.NewElement("CMapMesh", "", Guid.Empty);
        turnedNode.Attributes["origin"] = e.Entity.Origin;
        turnedNode.Attributes["angles"] = entityNode.Attributes["angles"];
        var turnedToWorld = CTransform.FromNode(turnedNode).Matrix();
        var turnedFromWorld = CTransform.FromNode(turnedNode).Inverse().Matrix();
        var turned = new Dictionary<Vector3, Vector3?>();
        Vector3? FitTurned(Vector3 h)
        {
            if (!turned.TryGetValue(h, out var v))
                turned[h] = v = LocalCorner(h, MapMeshes.Transform(turnedFromWorld, MapMeshes.Transform(fromEntity, h)),
                                            c => MapMeshes.Transform(toEntity, MapMeshes.Transform(turnedToWorld, c)));
            return v;
        }
        // The model's part takes its shapes through PartOrder, a CRT qsort on shape type after
        // every insert: with one type that is a fixed permutation of the input, which the meshes
        // are written in the inverse of, so the hulls come out in the model's order.
        var count = geometry.Pieces.Count;
        var placedAt = Physics.WorldCollision.PartOrder(Enumerable.Range(0, count), _ => 0);
        var input = new BrushGeometry.Piece[count];
        for (var k = 0; k < count; k++)
            input[placedAt[k]] = geometry.Pieces[k];
        var (unordered, moved, off) = (0, 0, 0);
        // Inside a prefab or a copy the forward moves mesh and entity by the placement too, so the
        // landing check here holds only for the map's own entities.
        var own = !e.IdPath.Contains(':', StringComparison.Ordinal);
        // A model with render meshes: the hulls of each Hammer mesh with drawn faces come back as that
        // one mesh, its drawn faces and their streams from the render (BrushRender), where the forward
        // gives back its hulls exactly; the rest as one mesh per hull.
        var leading = new List<MergedMesh>();
        var merged = geometry.HasRender && own
            ? RenderMeshes(input, bytes, e, entityNode, Fit, toWorld, toEntity, fromEntity, label, notes, leading)
            : [];
        foreach (var m in leading)
            builder.AddMesh(node, newId(), e.Entity.Origin, Vector3.Zero, m.Faces, m.Materials,
                            cornerTexcoords: m.Texcoords, cornerNormals: m.Normals, cornerTints: m.Tints, physicsType: m.PhysicsType);
        for (var k = 0; k < input.Length; k++)
        {
            var piece = input[k];
            if (merged.TryGetValue(k, out var mesh))
            {
                if (mesh.Hulls[0] == k)
                    foreach (var m in mesh.Before.Append(mesh).Concat(mesh.After))
                        builder.AddMesh(node, newId(), e.Entity.Origin, Vector3.Zero, m.Faces, m.Materials,
                                        cornerTexcoords: m.Texcoords, cornerNormals: m.Normals, cornerTints: m.Tints, physicsType: m.PhysicsType);
                continue;
            }
            var (origin, angles) = (e.Entity.Origin, Vector3.Zero);
            Func<Vector3, Vector3> fit = Fit;
            // Judged by the forward's chain for one mesh (its hull's points, bit for bit): the mesh as
            // Hammer leaves it under an entity first, else turned with the entity, else at the world's
            // origin (its pivot is Hammer's to place; only its move reaches the compile). Inside a
            // prefab or a copy the forward also moves by the placement, which that chain does not, so
            // those keep the first.
            if (own && !SamePoints(piece, fit, origin, angles, e.Entity, entityNode)
                && Nudged(piece, Fit, h => MapMeshes.Transform(toEntity, MapMeshes.Transform(toWorld, h)),
                          h => CornerFor(h, e.Entity.Origin, toWorld, toEntity, fromEntity),
                          f => SamePoints(piece, f, origin, angles, e.Entity, entityNode)) is { } nudged)
                (fit, moved) = (nudged, moved + 1);
            else if (own && !SamePoints(piece, fit, origin, angles, e.Entity, entityNode))
            {
                var turnedAngles = (Vector3)turnedNode.Attributes["angles"]!;
                if (piece.Vertices.All(h => FitTurned(h) is not null)
                    && SamePoints(piece, h => FitTurned(h)!.Value, e.Entity.Origin, turnedAngles, e.Entity, entityNode))
                    (angles, fit, moved) = (turnedAngles, h => FitTurned(h)!.Value, moved + 1);
                else if (SamePoints(piece, FitAtZero, Vector3.Zero, Vector3.Zero, e.Entity, entityNode))
                    (origin, fit, moved) = (Vector3.Zero, FitAtZero, moved + 1);
                else
                    off++;
            }
            var faces = piece.Faces.Select(f => f.Select(fit).ToArray()).ToArray();
            var vertexOrder = piece.Vertices.Select(fit).ToArray();
            var order = HullOrder(faces, vertexOrder, piece, e.Entity, entityNode, origin, angles, HullOrderBudget);
            if (order is null)
                unordered++;
            builder.AddMesh(node, newId(), origin, angles, [.. (order ?? faces).Select(f => (f, 0))], [piece.Material], vertexOrder);
        }
        if (unordered > 0)
            notes.Add($"{label}: {unordered} hull(s) whose vertex order no face order searched gives (same points, other order)");
        if (moved > 0)
            notes.Add($"{label}: {moved} mesh(es) whose corners needed more than landing on the hull (a vertex the hull build moves, a mesh turned with the entity or at the world's origin)");
        if (off > 0)
            notes.Add($"{label}: {off} mesh(es) with a corner no searched point places exactly");
        if (geometry.HasRender && !own)
            notes.Add($"{label}: inside a prefab or a copy, render mesh not read (texture coordinates and normals not recovered)");
        // A model with no hulls but triangle-mesh shapes (a mesh the compile kept concave): without
        // geometry the forward drops a solid class entity outright (ReachesTheLump), so each shape
        // comes back as one mesh, a face per triangle in the shape's order and winding, its corners
        // landed as a hull's are. The source's polygons and the compile's own triangulation of them
        // are not recovered; it keeps the entity, its collision shape and its I/O. A cable's model is
        // its tube (CableSettings), not a mesh drawn under it: a cable not rebuilt as one keeps none.
        if (geometry.Pieces.Count == 0 && geometry.Soups.Count > 0 && !cable)
        {
            foreach (var soup in geometry.Soups)
            {
                var corners = soup.Vertices.Select(Fit).ToArray();
                var faces = soup.Triangles.Select(t => ((Vector3[])[corners[t.A], corners[t.B], corners[t.C]], 0)).ToList();
                builder.AddMesh(node, newId(), e.Entity.Origin, Vector3.Zero, faces, [soup.Material], corners);
            }
            notes.Add($"{label}: {geometry.Soups.Count} triangle-mesh shape(s) rebuilt a face per triangle (the source's polygons not recovered)");
        }
        else if (geometry.TriangleMeshes > 0)
            notes.Add($"{label}: {geometry.TriangleMeshes} triangle-mesh shape(s) not read");
        if (geometry.UnknownMaterials.Count > 0)
            notes.Add($"{label}: {geometry.UnknownMaterials.Count} hull(s) with a material hash no known material has");
        if (geometry.Pieces.Count == 0 && (geometry.Soups.Count == 0 || cable))
            notes.Add($"{label}: its model has no hulls; no geometry");
    }

    /// <summary>A Hammer mesh rebuilt with a brush entity model's render half: the hulls it gives (input
    /// indices, in order), its faces (corners in the mesh's space), materials and corner streams.</summary>
    private sealed record MergedMesh(int[] Hulls, List<(Vector3[] Corners, int Material)> Faces, List<string> Materials,
                                     List<Vector2[]?> Texcoords, List<Vector3[]?> Normals, List<Vector4[]?>? Tints, string PhysicsType)
    {
        /// <summary>Render-only meshes (physics type none) written just before and after this one.</summary>
        public List<MergedMesh> Before { get; } = [];
        public List<MergedMesh> After { get; } = [];
    }

    /// <summary>
    /// The Hammer meshes of a brush entity model with render meshes (<see cref="BrushRender"/>), keyed by
    /// each of their hulls' input index. The drawn hulls pair with the render pieces in order (the physics
    /// and the render walk the same meshes, each mesh's pieces in material order), a piece taking one hull
    /// per connected part or one for the whole, a flat part's hull pushed out by the minimum thickness; a
    /// mesh with render pieces and no hull is one whose physics type is none. A nodraw hull joins the mesh
    /// next to it in the run that holds its points or the ends of its open drawn edges, else it stays a
    /// mesh of its own. A mesh is kept only where the forward's chain for one mesh
    /// (EntityPhysicsModels.BrushMeshHulls) gives back its hulls bit for bit, in order, after a face order
    /// search where only the order differs; the others stay one mesh per hull, with a note. Render-only
    /// meshes with no rebuilt mesh to follow go into <paramref name="leading"/>.
    /// </summary>
    private static Dictionary<int, MergedMesh> RenderMeshes(BrushGeometry.Piece[] input, byte[] model, EntityLumpAuthor.SourceEntity e,
                                                            DmxBinary.Element entityNode, Func<Vector3, Vector3> fit, float[] toWorld, float[] toEntity,
                                                            float[] fromEntity, string label, List<string> notes, List<MergedMesh> leading)
    {
        var result = new Dictionary<int, MergedMesh>();
        var render = BrushRender.Read(model);
        if (BrushRender.MeshesOf([.. render.Select(r => r.Name)]) is not { } meshes)
        {
            notes.Add($"{label}: render mesh names not the compile's ({string.Join(", ", render.Select(r => r.Name))}); render not read");
            return result;
        }
        foreach (var r in render.Where(r => r.Unread.Count > 0))
            notes.Add($"{label}: {r.Name} holds {string.Join(", ", r.Unread)}, not read back");
        // A material draws when some render piece has it (DrawsNothing is the material's own).
        var drawn = new HashSet<string>(render.Select(r => r.Material), StringComparer.OrdinalIgnoreCase);
        bool Draws(string material) => drawn.Contains(material);
        var pieceMesh = new int[render.Count];
        for (var m = 0; m < meshes.Count; m++)
            foreach (var j in meshes[m])
                pieceMesh[j] = m;
        var positions = render.Select(r => r.Corners.Select(c => c.Position).ToHashSet()).ToList();
        var meshPositions = meshes.Select(m => m.SelectMany(j => positions[j]).ToHashSet()).ToList();

        // A drawn piece's hulls: one per connected part under convex_multi (BrushHulls.Inputs' groups;
        // atixref's glass panes, a front and a back face), or one of the whole piece. A hull's points are
        // its part's, except a flat or degenerate part's, which the map builder pushes out by its minimum
        // thickness and hulls again (RnHullBuilder, FUN_180384ab0): such a hull pairs with a part when the
        // forward's hull chain on the part's corners pushes it into those points (BrushRender.PushedFrom);
        // the mesh's own check below (BrushMeshHulls) is the exact one.
        var parts = render.Select((r, j) => Parts(r).Append(positions[j]).Distinct(HashSet<Vector3>.CreateSetComparer()).ToList()).ToList();
        int Pairs(int h, int j, IReadOnlyCollection<int> left) => !string.Equals(render[j].Material, input[h].Material, StringComparison.OrdinalIgnoreCase) ? -1
            : left.Where(p => input[h].Vertices.All(parts[j][p].Contains)).DefaultIfEmpty(-1).First() is var exact and >= 0 ? exact
            : left.Where(p => BrushRender.PushedFrom(input[h].Vertices, parts[j][p])).DefaultIfEmpty(-1).First();
        IReadOnlyCollection<int> AllParts(int j) => [.. Enumerable.Range(0, parts[j].Count)];
        // A mesh whose physics type is none has render pieces and no hulls: skipped whole where a later
        // mesh's first piece pairs with the hull (atixref's block_roof_waterm).
        var renderOnly = new HashSet<int>();
        var meshOf = Enumerable.Repeat(-1, input.Length).ToArray();
        var thickened = new bool[input.Length];
        var next = 0;
        var left = new HashSet<int>();
        bool Starts(int j) => meshes[pieceMesh[j]][0] == j;
        for (var h = 0; h < input.Length; h++)
        {
            if (!Draws(input[h].Material))
                continue;
            if (left.Count == 0 && next < render.Count)
                left = [.. AllParts(next)];
            if (next < render.Count && left.Count == parts[next].Count && Pairs(h, next, left) < 0 && Starts(next)
                && Enumerable.Range(next + 1, render.Count - next - 1).FirstOrDefault(j => Starts(j) && Pairs(h, j, AllParts(j)) >= 0, -1) is var later and > 0)
            {
                for (var m = pieceMesh[next]; m < pieceMesh[later]; m++)
                    renderOnly.Add(m);
                next = later;
                left = [.. AllParts(next)];
            }
            var part = next < render.Count ? Pairs(h, next, left) : -1;
            if (part < 0)
            {
                notes.Add($"{label}: drawn hull {h} does not pair with render piece {next} in order; render not read"
                          + (next < render.Count ? $" ({input[h].Material} vs {render[next].Material}, {input[h].Vertices.Count(positions[next].Contains)} of {input[h].Vertices.Length} hull points on it"
                                                   + $"{(Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DEBUG") == "1" ? $": hull {string.Join(" ", input[h].Vertices.Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}"))}; render {string.Join(" ", positions[next].Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}"))}" : "")})" : ""));
                return result;
            }
            thickened[h] = !input[h].Vertices.All(parts[next][part].Contains);
            meshOf[h] = pieceMesh[next];
            // The whole piece's points take every part with them.
            if (part == parts[next].Count - 1 && parts[next].Count > 1)
                left.Clear();
            else
                left.Remove(part);
            left.Remove(parts[next].Count - 1);
            if (left.Count == 0)
                next++;
        }
        if (next < render.Count && left.Count > 0 && left.Count < parts[next].Count)
        {
            notes.Add($"{label}: render piece {next} has parts with no hull; render not read");
            return result;
        }
        if (next < render.Count && !Starts(next))
        {
            notes.Add($"{label}: render piece {next} with no drawn hull; render not read");
            return result;
        }
        for (var m = next < render.Count ? pieceMesh[next] : meshes.Count; m < meshes.Count; m++)
            renderOnly.Add(m);
        // A nodraw hull joins the mesh before or after it in the run whose drawn corners hold its points
        // (ze_hold_em_p's breakables: three nodraw faces round seven drawn), else whose drawn faces' open
        // edges it holds the ends of (a drawn quad on a nodraw box, ze_hold_em_p's end_breakable_240).
        var openEnds = meshes.Select(m =>
        {
            var edges = new HashSet<(Vector3, Vector3)>();
            foreach (var j in m)
                for (var t = 0; t + 2 < render[j].Corners.Length; t += 3)
                    for (var i = 0; i < 3; i++)
                        edges.Add((render[j].Corners[t + i].Position, render[j].Corners[t + ((i + 1) % 3)].Position));
            return edges.Where(x => !edges.Contains((x.Item2, x.Item1))).SelectMany(x => new[] { x.Item1, x.Item2 }).ToHashSet();
        }).ToList();
        for (var h = 0; h < input.Length; h++)
        {
            if (meshOf[h] >= 0)
                continue;
            var before = h > 0 ? meshOf[h - 1] : -1;
            var after = Enumerable.Range(h + 1, input.Length - h - 1).Where(k => Draws(input[k].Material)).Select(k => meshOf[k]).DefaultIfEmpty(-1).First();
            var vertices = input[h].Vertices.ToHashSet();
            // Between two pieces of one mesh it is that mesh's (atixref's disco_wall_toggle: trim, nodraw, floor).
            if (before >= 0 && before == after)
                meshOf[h] = before;
            else if (before >= 0 && input[h].Vertices.All(meshPositions[before].Contains))
                meshOf[h] = before;
            else if (after >= 0 && input[h].Vertices.All(meshPositions[after].Contains))
                meshOf[h] = after;
            else if (before >= 0 && openEnds[before].Count > 0 && openEnds[before].All(vertices.Contains))
                meshOf[h] = before;
            else if (after >= 0 && openEnds[after].Count > 0 && openEnds[after].All(vertices.Contains))
                meshOf[h] = after;
        }

        static (int, int, int) Bits(Vector3 v) => (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
        Vector3 NormalToEntity(Vector3 n) => NodeMeshEntries.Normalise(NodeMeshEntries.Rotate(toEntity, NodeMeshEntries.Rotate(toWorld, n)));
        Vector3 NormalFromEntity(Vector3 n) => NodeMeshEntries.Rotate(fromEntity, n);
        var failed = new List<string>();
        var plans = new MergedMesh?[meshes.Count];
        for (var m = 0; m < meshes.Count; m++)
        {
            var hulls = Enumerable.Range(0, input.Length).Where(h => meshOf[h] == m).ToArray();
            if (hulls.Length > 0 && hulls[^1] - hulls[0] + 1 != hulls.Length)
            {
                failed.Add($"mesh {m}: its hulls are not one run");
                continue;
            }
            var materials = hulls.Select(h => input[h].Material).Concat(meshes[m].Select(j => render[j].Material))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int IndexOf(string material) => materials.FindIndex(x => string.Equals(x, material, StringComparison.OrdinalIgnoreCase));
            var pieces = meshes[m].Select(j => (render[j], IndexOf(render[j].Material))).ToList();
            // Rebuild and CornerStreams pair a face with its piece by material: two pieces of one
            // mesh in one material (ze_visualizer, workshop sweep 11-13) split by a rule not measured.
            if (pieces.GroupBy(p => p.Item2).FirstOrDefault(g => g.Count() > 1) is { } shared)
            {
                failed.Add($"mesh {m}: {shared.Count()} render pieces share material {materials[shared.Key]} " +
                           $"({string.Join(", ", shared.Select(p => p.Item1.Name))}; the split is not modelled)");
                continue;
            }
            var (faces, reason) = BrushRender.Rebuild(pieces, [.. hulls.Select(h => input[h])], [.. hulls.Select(h => IndexOf(input[h].Material))],
                                                      [.. hulls.Select(h => thickened[h])], x => !Draws(x), closed: !renderOnly.Contains(m));
            if (faces is null)
            {
                failed.Add($"mesh {m}: {reason}");
                continue;
            }
            var local = faces.Select(f => f.Corners.Select(fit).ToArray()).ToList();
            var (streams, why) = CornerStreams(faces, local, pieces, NormalToEntity, NormalFromEntity);
            if (streams is null)
            {
                failed.Add($"mesh {m}: {why}");
                continue;
            }
            var physicsType = renderOnly.Contains(m) ? "none" : "default";
            var (texcoords, normals, tints) = streams.Value;
            // The mesh with its faces in an order: the hulls the forward gives back, each exact or not.
            (DmxBinary.Element Mesh, bool[] Exact, bool SamePoints) Build(int[] order, bool hullsToo = true)
            {
                var scratch = new VmapBuilder("rendermesh");
                var trial = scratch.AddMesh(scratch.World, 1, e.Entity.Origin, Vector3.Zero, [.. order.Select(f => (local[f], faces[f].Material))], materials, null,
                                            [.. order.Select(f => texcoords[f])], [.. order.Select(f => normals[f])], tints is null ? null : [.. order.Select(f => tints[f])], physicsType);
                scratch.RemoveFromWorld(trial);
                if (!hullsToo)
                    return (trial, [], false);
                var back = Physics.EntityPhysicsModels.BrushMeshHulls(trial, entityNode, e.Entity.ClassName, VmapBuilder.FormatVersion);
                if (back.Count != hulls.Length)
                    return (trial, [.. hulls.Select(_ => false)], false);
                var exact = back.Zip(hulls).Select(p => p.First.Select(Bits).SequenceEqual(input[p.Second].Vertices.Select(Bits))).ToArray();
                return (trial, exact, back.Zip(hulls).All(p => p.First.Select(Bits).Order().SequenceEqual(input[p.Second].Vertices.Select(Bits).Order())));
            }
            var order = Enumerable.Range(0, faces.Count).ToArray();
            var built = Build(order);
            // A hull with the shipped points in another order: its piece's faces reordered (seeded tries)
            // until the hull builder meets them as the source's did, the drawn triangles' order through the
            // draw passes kept (RenderOrderHolds).
            var searched = new List<string>();
            if (!built.Exact.All(x => x) && built.SamePoints)
            {
                var random = new Random(0x5eed);
                foreach (var q in Enumerable.Range(0, hulls.Length).Where(q => !built.Exact[q]).ToList())
                {
                    var material = materials.FindIndex(x => string.Equals(x, input[hulls[q]].Material, StringComparison.OrdinalIgnoreCase));
                    var slots = Enumerable.Range(0, order.Length).Where(i => faces[order[i]].Material == material).ToArray();
                    var drawnPiece = pieces.FirstOrDefault(p => p.Item2 == material).Item1;
                    // A drawn piece: its triangles in their faces (joined across an edge whose corners are one
                    // render vertex each), the faces in every order and each face's triangles either way
                    // round, the draw checked first (atixref's valdina_button: six quads, its shipped order
                    // not the one the draw passes give back).
                    var (drawnTried, drawnHeld, placedHeld) = (0, 0, 0);
                    if (drawnPiece is not null)
                        foreach (var candidate in DrawnOrders(order, slots, faces).Take(60000))
                        {
                            // No order drawing even the shipped corner positions in the first few thousand:
                            // the filter misses something for this mesh, and the rest would not land either.
                            if (drawnTried >= 4096 && placedHeld == 0)
                                break;
                            drawnTried++;
                            var mesh = Build(candidate, hullsToo: false).Mesh;
                            var holds = RenderOrderHolds(mesh, material, drawnPiece, toWorld, toEntity, out var placed);
                            placedHeld += placed ? 1 : 0;
                            if (!holds)
                                continue;
                            drawnHeld++;
                            var tried = Build(candidate);
                            if (tried.Exact[q])
                            {
                                (order, built) = (candidate, tried);
                                break;
                            }
                        }
                    if (drawnPiece is not null && !built.Exact[q])
                        searched.Add($"{drawnTried} face orders tried, {placedHeld} with the shipped corner order, {drawnHeld} with its frames too");
                    for (var attempt = 0; attempt < 3000 && !built.Exact[q] && drawnPiece is null; attempt++)
                    {
                        var candidate = (int[])order.Clone();
                        var shuffled = slots.Select(i => order[i]).OrderBy(_ => random.Next()).ToArray();
                        for (var i = 0; i < slots.Length; i++)
                            candidate[slots[i]] = shuffled[i];
                        var tried = Build(candidate);
                        if (tried.Exact[q] && (drawnPiece is null || RenderOrderHolds(tried.Mesh, material, drawnPiece, toWorld, toEntity)))
                            (order, built) = (candidate, tried);
                    }
                }
            }
            if (!built.Exact.All(x => x))
            {
                failed.Add($"mesh {m}: its hulls through the forward differ ({(built.SamePoints ? "same points, no face order searched gives their order" : "other points")}"
                           + $"{(searched.Count > 0 ? "; " + string.Join("; ", searched) : "")})"
                           + (Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DEBUG") == "1"
                               ? $"; render order as read holds: {string.Join(",", pieces.Select(p => RenderOrderHolds(Build(Enumerable.Range(0, faces.Count).ToArray()).Mesh, p.Item2, p.Item1, toWorld, toEntity)))}" : ""));
                continue;
            }
            if (Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DEBUG") == "1")
                notes.Add($"{label}: render debug: mesh {m} kept, render filter {string.Join(",", pieces.Select(p => RenderOrderHolds(built.Mesh, p.Item2, p.Item1, toWorld, toEntity)))}");
            plans[m] = new MergedMesh(hulls, [.. order.Select(f => (local[f], faces[f].Material))], materials, [.. order.Select(f => texcoords[f])],
                                      [.. order.Select(f => normals[f])], tints is null ? null : [.. order.Select(f => tints[f])], physicsType);
        }
        // A render-only mesh has no hull to stand at: it is written after the mesh before it in model
        // order (before the first one when it leads).
        for (var m = 0; m < meshes.Count; m++)
        {
            if (plans[m] is not { } plan || plan.Hulls.Length == 0)
                continue;
            foreach (var h in plan.Hulls)
                result[h] = plan;
            for (var k = m + 1; k < meshes.Count && renderOnly.Contains(k); k++)
                if (plans[k] is { } after)
                    plan.After.Add(after);
            if (m == Enumerable.Range(0, meshes.Count).First(x => !renderOnly.Contains(x)))
                for (var k = 0; k < m; k++)
                    if (plans[k] is { } before)
                        plan.Before.Add(before);
        }
        // With no rebuilt mesh to stand by, the render-only ones lead the model's meshes (atixref's
        // block_roof_waterm: its drawn mesh has no physics, its only hull is a toolsclip mesh's).
        if (!plans.Any(p => p is { Hulls.Length: > 0 }))
            leading.AddRange(plans.OfType<MergedMesh>());
        if (failed.Count > 0)
            notes.Add($"{label}: {failed.Count} of {meshes.Count} mesh(es) not rebuilt with the render mesh: {string.Join("; ", failed)}");
        return result;
    }

    /// <summary>
    /// The corner streams of a rebuilt mesh (<see cref="BrushRender"/>): a drawn face's texcoords, normals
    /// and tints chosen so the forward packs the shipped bits; a face that draws nothing gets texcoords
    /// that keep the mesh's high precision flag as shipped (a texcoord past +-1.03125 after the shift
    /// anywhere in the mesh, EntityRenderModels.Pieces) and its own normal.
    /// </summary>
    private static ((List<Vector2[]?> Texcoords, List<Vector3[]?> Normals, List<Vector4[]?>? Tints)? Streams, string? Reason) CornerStreams(
        List<BrushRender.Face> faces, List<Vector3[]> local, List<(BrushRender.Piece Piece, int Material)> pieces,
        Func<Vector3, Vector3> normalToEntity, Func<Vector3, Vector3> normalFromEntity)
    {
        var formats = pieces.ToDictionary(p => p.Material, p => p.Piece.TexcoordFormat);
        var texcoords = new List<Vector2[]?>();
        var normals = new List<Vector3[]?>();
        var tints = pieces.Any(p => p.Piece.Corners.Any(c => c.Tint is not null)) ? new List<Vector4[]?>() : null;
        static Vector3 FaceNormal(Vector3[] loop)
        {
            var n = Vector3.Zero;
            for (var i = 0; i < loop.Length; i++)
            {
                var (a, b) = (loop[i], loop[(i + 1) % loop.Length]);
                n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            return n.LengthSquared() > 0f ? Vector3.Normalize(n) : n;
        }
        var faceNormals = local.Select(FaceNormal).ToList();
        // The distinct face normals round each corner, for its smoothing candidates.
        var around = new Dictionary<Vector3, List<Vector3>>();
        for (var f = 0; f < local.Count; f++)
            foreach (var c in local[f])
            {
                if (!around.TryGetValue(c, out var list))
                    around[c] = list = [];
                if (!list.Any(n => Vector3.Dot(n, faceNormals[f]) > 1f - 1e-6f))
                    list.Add(faceNormals[f]);
            }
        IEnumerable<Vector3> Candidates(Vector3 corner, Vector3 own)
        {
            yield return own;
            var others = around[corner].Where(n => Vector3.Dot(n, own) <= 1f - 1e-6f).ToList();
            // The normalised sum of the face's normal with each subset of the others, smallest first
            // (atixref's and ze's bevels: two faces at right angles give (0.70710677, 0.70710677, 0)).
            for (var size = 1; size <= Math.Min(others.Count, 5); size++)
                foreach (var subset in Subsets(others, size))
                    if (subset.Aggregate(own, (s, n) => s + n) is var sum && sum.LengthSquared() > 0f)
                        yield return Vector3.Normalize(sum);
        }
        var unpacked = 0;
        for (var f = 0; f < faces.Count; f++)
        {
            if (faces[f].Render is not { } corners)
            {
                texcoords.Add(null);
                normals.Add(null);
                tints?.Add(null);
                continue;
            }
            var uv = new Vector2[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                if (BrushRender.Texcoord(corners[i], formats[faces[f].Material]) is { } t)
                    uv[i] = t;
                else
                    unpacked++;
            }
            texcoords.Add(uv);
            normals.Add([.. corners.Select((c, i) => BrushRender.Normal(c.Frame, Candidates(local[f][i], faceNormals[f]), normalToEntity, normalFromEntity))]);
            tints?.Add([.. corners.Select(c => c.Tint is { } rgba ? BrushRender.Tint(rgba) : Vector4.One)]);
        }
        if (unpacked > 0)
            return (null, $"{unpacked} texcoord(s) no value packs to");
        // A packed piece's texcoords moved within their cells to the draw's UV density.
        foreach (var (piece, material) in pieces.Where(p => p.Piece.TexcoordFormat != "R32G32_FLOAT"))
        {
            var chosen = new Dictionary<BrushRender.Corner, Vector2>();
            var normalOf = new Dictionary<BrushRender.Corner, Vector3>();
            for (var f = 0; f < faces.Count; f++)
                if (faces[f].Material == material && faces[f].Render is { } corners)
                    for (var i = 0; i < corners.Length; i++)
                    {
                        chosen.TryAdd(corners[i], texcoords[f]![i]);
                        normalOf.TryAdd(corners[i], normalToEntity(normals[f]![i]));
                    }
            if (BrushRender.FitDensity(piece, chosen, c => normalOf[c]) is not { } fitted)
                return (null, $"{piece.Name}: no texcoords in their cells give the draw's UV density {piece.UvDensity:R}");
            for (var f = 0; f < faces.Count; f++)
                if (faces[f].Material == material && faces[f].Render is { } corners)
                    for (var i = 0; i < corners.Length; i++)
                        texcoords[f]![i] = fitted[corners[i]];
        }
        var precise = pieces.Any(p => p.Piece.TexcoordFormat == "R32G32_FLOAT");
        var drawnPast = texcoords.Where(t => t is not null).SelectMany(t => t!).Any(t => MathF.Abs(t.X) > 1.03125f || MathF.Abs(t.Y) > 1.03125f);
        for (var f = 0; f < faces.Count; f++)
            if (texcoords[f] is null)
                texcoords[f] = [.. local[f].Select((_, i) => precise && !drawnPast
                    ? new Vector2((i & 1) == 0 ? -1.75f : 1.75f, (i & 2) == 0 ? -1.75f : 1.75f)
                    : new Vector2(0.5f, 0.5f))];
        return ((texcoords, normals, tints), null);
    }

    /// <summary>A render piece's connected parts (its triangles joined by shared corners), each as its points.</summary>
    private static List<HashSet<Vector3>> Parts(BrushRender.Piece piece)
    {
        var parent = new Dictionary<Vector3, Vector3>();
        Vector3 Find(Vector3 x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        foreach (var c in piece.Corners)
            parent.TryAdd(c.Position, c.Position);
        for (var t = 0; t + 2 < piece.Corners.Length; t += 3)
        {
            var a = Find(piece.Corners[t].Position);
            parent[Find(piece.Corners[t + 1].Position)] = a;
            parent[Find(piece.Corners[t + 2].Position)] = Find(a);
        }
        return [.. parent.Keys.GroupBy(Find).Select(g => g.ToHashSet())];
    }

    /// <summary>
    /// Whether a rebuilt mesh's drawn piece comes out of the draw as the shipped triangles: the piece in
    /// the entity's space with its tangents recomputed (EntityRenderModels.Entry: MeshTangents over the
    /// triangles in order), through NodeDraw (the weld, meshopt's vertex cache and overdraw passes,
    /// first-use order), corner for corner by position and packed tangent frame. The weld runs over the
    /// position, texcoord, normal and tangent (and the tint where the layout has one), the streams a
    /// brush entity's draw keeps, so this filters a search; the forward compile of the decompile is the
    /// check.
    /// </summary>
    private static bool RenderOrderHolds(DmxBinary.Element mesh, int material, BrushRender.Piece shipped, float[] toWorld, float[] toEntity)
        => RenderOrderHolds(mesh, material, shipped, toWorld, toEntity, out _);

    /// <param name="positionsHold">Whether the corners' positions alone come out as shipped.</param>
    internal static bool RenderOrderHolds(DmxBinary.Element mesh, int material, BrushRender.Piece shipped, float[] toWorld, float[] toEntity, out bool positionsHold)
    {
        positionsHold = false;
        var piece = MapMeshCorners.Build(mesh, true, null, withTangent: false, allStreams: true).FirstOrDefault(p => p.Material == material);
        if (piece is null)
            return false;
        int? At(string name) => piece.Streams.Where(s => s.Name == name).Select(s => (int?)s.First).FirstOrDefault();
        var (uvAt, normalAt, tintAt) = (At("texcoord"), At("normal"), shipped.Corners.Any(c => c.Tint is not null) ? At("VertexPaintTintColor") : null);
        if (uvAt is null || normalAt is null)
            return false;
        var count = piece.Vertices.Length / piece.Stride;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        for (var c = 0; c < count; c++)
        {
            var o = c * piece.Stride;
            positions[c] = MapMeshes.Transform(toEntity, MapMeshes.Transform(toWorld, new Vector3(piece.Vertices[o], piece.Vertices[o + 1], piece.Vertices[o + 2])));
            var n = new Vector3(piece.Vertices[o + normalAt.Value], piece.Vertices[o + normalAt.Value + 1], piece.Vertices[o + normalAt.Value + 2]);
            normals[c] = NodeMeshEntries.Normalise(NodeMeshEntries.Rotate(toEntity, NodeMeshEntries.Rotate(toWorld, n)));
            uvs[c] = new Vector2(piece.Vertices[o + uvAt.Value], 1f - (1f - piece.Vertices[o + uvAt.Value + 1]));
        }
        var tangents = MeshTangents.Corners(positions, normals, uvs, piece.Indices);
        var cornerTangent = new Vector4[count];
        for (var k = 0; k < piece.Indices.Length; k++)
            cornerTangent[piece.Indices[k]] = tangents[k];
        var stride = tintAt is null ? 12 : 16;
        var vertices = new float[count * stride];
        for (var c = 0; c < count; c++)
        {
            var o = c * stride;
            (vertices[o], vertices[o + 1], vertices[o + 2]) = (positions[c].X, positions[c].Y, positions[c].Z);
            (vertices[o + 3], vertices[o + 4]) = (uvs[c].X, uvs[c].Y);
            (vertices[o + 5], vertices[o + 6], vertices[o + 7]) = (normals[c].X, normals[c].Y, normals[c].Z);
            (vertices[o + 8], vertices[o + 9], vertices[o + 10], vertices[o + 11]) = (cornerTangent[c].X, cornerTangent[c].Y, cornerTangent[c].Z, cornerTangent[c].W);
            if (tintAt is { } t)
                Array.Copy(piece.Vertices, (c * piece.Stride) + t, vertices, o + 12, 4);
        }
        List<Physics.MeshWeld.Stream> streams = [new("position", 0, 3, false, 42), new("texcoord", 3, 2, false, 41), new("normal", 5, 3, false, 42), new("tangent", 8, 4, false, 43)];
        if (tintAt is not null)
            streams.Add(new("VertexPaintTintColor", 12, 4, false, 43));
        var draw = NodeDraw.Build(vertices, stride, 0, piece.Indices, streams, false, 0f);
        if (draw.Indices.Length != shipped.Corners.Length)
            return false;
        Vector3 Placed(int i) => new(draw.Vertices[draw.Indices[i] * stride], draw.Vertices[(draw.Indices[i] * stride) + 1], draw.Vertices[(draw.Indices[i] * stride) + 2]);
        positionsHold = Enumerable.Range(0, draw.Indices.Length).All(i => Placed(i) == shipped.Corners[i].Position);
        if (!positionsHold)
            return false;
        for (var i = 0; i < draw.Indices.Length; i++)
        {
            var o = draw.Indices[i] * stride;
            var frame = TangentFrame.Compress(new Vector3(draw.Vertices[o + 5], draw.Vertices[o + 6], draw.Vertices[o + 7]),
                                              new Vector4(draw.Vertices[o + 8], draw.Vertices[o + 9], draw.Vertices[o + 10], draw.Vertices[o + 11]));
            if (frame != shipped.Corners[i].Frame)
            {
                if (Environment.GetEnvironmentVariable("DECOMPILE_RENDER_DEBUG") == "1")
                    Console.Error.WriteLine($"render filter: corner {i} of {draw.Indices.Length} at {Placed(i)}: frame {frame:x8}, shipped {shipped.Corners[i].Frame:x8}");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Face orders for a drawn piece's triangles (the faces at <paramref name="slots"/> of
    /// <paramref name="order"/>): the triangles joined into the faces they came from (an edge whose two
    /// corners are the same render vertices on both sides), then every order of those faces with each
    /// face's triangles as read or reversed, the read order first.
    /// </summary>
    private static IEnumerable<int[]> DrawnOrders(int[] order, int[] slots, List<BrushRender.Face> faces)
    {
        var triangles = slots.Select(i => order[i]).ToArray();
        var parent = Enumerable.Range(0, triangles.Length).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var edgeOf = new Dictionary<(BrushRender.Corner, BrushRender.Corner), int>();
        for (var t = 0; t < triangles.Length; t++)
        {
            var c = faces[triangles[t]].Render!;
            for (var i = 0; i < c.Length; i++)
            {
                var (a, b) = (c[i], c[(i + 1) % c.Length]);
                if (edgeOf.TryGetValue((b, a), out var other))
                    parent[Find(t)] = Find(other);
                edgeOf.TryAdd((a, b), t);
            }
        }
        var groups = Enumerable.Range(0, triangles.Length).GroupBy(Find).Select(g => g.Select(t => triangles[t]).ToArray()).ToList();
        var perm = Enumerable.Range(0, groups.Count).ToArray();
        do
        {
            for (var flips = 0; flips < 1 << Math.Min(groups.Count, 16); flips++)
            {
                var sequence = perm.SelectMany((g, i) => (flips >> i & 1) == 0 ? groups[g] : groups[g].Reverse()).ToArray();
                var candidate = (int[])order.Clone();
                for (var i = 0; i < slots.Length; i++)
                    candidate[slots[i]] = sequence[i];
                yield return candidate;
            }
        }
        while (NextPermutation(perm));
    }

    private static bool NextPermutation(int[] a)
    {
        var i = a.Length - 2;
        while (i >= 0 && a[i] >= a[i + 1])
            i--;
        if (i < 0)
            return false;
        var j = a.Length - 1;
        while (a[j] <= a[i])
            j--;
        (a[i], a[j]) = (a[j], a[i]);
        Array.Reverse(a, i + 1, a.Length - i - 1);
        return true;
    }

    private static IEnumerable<List<T>> Subsets<T>(List<T> items, int size, int from = 0)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }
        for (var i = from; i <= items.Count - size; i++)
            foreach (var rest in Subsets(items, size - 1, i + 1))
                yield return [items[i], .. rest];
    }

    /// <summary>
    /// A face order (and each face's first corner) whose mesh the entity model step hulls into exactly
    /// the shipped hull, vertex for vertex in order (EntityPhysicsModels.BrushMeshHulls). The hull
    /// builder lists its first tetrahedron and then each point it adds, so the order is mostly the
    /// geometry's, and the input order (the mesh's numbering: its triangles' first meeting of their
    /// corners, through the grouping's hash set, BrushHulls.GroupVertices) breaks the ties a box has
    /// plenty of. The faces as read first, then a seeded search over orders and rotations; null when
    /// none lands within the budget.
    /// </summary>
    /// <summary>Tries for <see cref="HullOrder"/>'s search: 3000, or DECOMPILE_HULL_BUDGET.</summary>
    private static readonly int HullOrderBudget =
        int.TryParse(Environment.GetEnvironmentVariable("DECOMPILE_HULL_BUDGET"), out var budget) && budget > 0 ? budget : 3000;

    private static Vector3[][]? HullOrder(Vector3[][] faces, Vector3[] vertexOrder, BrushGeometry.Piece piece,
                                          MapEntities.Entity entity, DmxBinary.Element entityNode, Vector3 meshOrigin, Vector3 meshAngles, int budget = 3000)
    {
        var scratch = new VmapBuilder("hullorder");
        bool Lands(Vector3[][] candidate)
        {
            var mesh = scratch.AddMesh(scratch.World, 1, meshOrigin, meshAngles, [.. candidate.Select(f => (f, 0))], [piece.Material], vertexOrder);
            scratch.RemoveFromWorld(mesh);
            var hulls = Physics.EntityPhysicsModels.BrushMeshHulls(mesh, entityNode, entity.ClassName, VmapBuilder.FormatVersion);
            return hulls.Count == 1 && hulls[0].Length == piece.Vertices.Length
                   && hulls[0].Zip(piece.Vertices).All(p => BitConverter.SingleToInt32Bits(p.First.X) == BitConverter.SingleToInt32Bits(p.Second.X)
                                                         && BitConverter.SingleToInt32Bits(p.First.Y) == BitConverter.SingleToInt32Bits(p.Second.Y)
                                                         && BitConverter.SingleToInt32Bits(p.First.Z) == BitConverter.SingleToInt32Bits(p.Second.Z));
        }
        if (Lands(faces))
            return faces;
        var random = new Random(0x5eed);
        for (var attempt = 0; attempt < budget; attempt++)
        {
            var candidate = faces.OrderBy(_ => random.Next()).Select(f =>
            {
                var r = random.Next(f.Length);
                return f.Skip(r).Concat(f.Take(r)).ToArray();
            }).ToArray();
            if (Lands(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Corners for a hull whose build moves a vertex off its corner's placed point (probe_classes'
    /// func_nav_blocker: two vertices a float below theirs in z): for each vertex no corner lands on,
    /// the corners that land on the points up to 2 floats a side of it, and the combination whose mesh
    /// the forward hulls into the shipped points (at most 4096 tried). Null when none does.
    /// </summary>
    private static Func<Vector3, Vector3>? Nudged(BrushGeometry.Piece piece, Func<Vector3, Vector3> fit, Func<Vector3, Vector3> place,
                                                  Func<Vector3, Vector3> cornerFor, Func<Func<Vector3, Vector3>, bool> lands)
    {
        static float Step(float f, int by)
        {
            for (; by > 0; by--) f = MathF.BitIncrement(f);
            for (; by < 0; by++) f = MathF.BitDecrement(f);
            return f;
        }
        var off = piece.Vertices.Distinct().Where(h => place(fit(h)) != h).ToList();
        if (off.Count == 0 || off.Count > 4)
            return null;
        var options = new List<List<Vector3>>();
        foreach (var h in off)
        {
            var list = new List<Vector3>();
            for (var dx = -2; dx <= 2; dx++)
                for (var dy = -2; dy <= 2; dy++)
                    for (var dz = -2; dz <= 2; dz++)
                    {
                        var near = new Vector3(Step(h.X, dx), Step(h.Y, dy), Step(h.Z, dz));
                        var corner = cornerFor(near);
                        if (place(corner) == near && !list.Contains(corner))
                            list.Add(corner);
                    }
            if (list.Count == 0)
                return null;
            options.Add(list);
        }
        var pick = new int[off.Count];
        for (var tried = 0; tried < 4096; tried++)
        {
            var chosen = new Dictionary<Vector3, Vector3>();
            for (var k = 0; k < off.Count; k++)
                chosen[off[k]] = options[k][pick[k]];
            Vector3 Fitted(Vector3 h) => chosen.TryGetValue(h, out var c) ? c : fit(h);
            if (lands(Fitted))
                return Fitted;
            var at = 0;
            while (at < pick.Length && ++pick[at] == options[at].Count)
                pick[at++] = 0;
            if (at == pick.Length)
                break;
        }
        return null;
    }

    /// <summary>Whether the piece's mesh, its corners fitted and placed so, hulls into exactly the shipped
    /// points (any order) through the forward's chain for one mesh.</summary>
    private static bool SamePoints(BrushGeometry.Piece piece, Func<Vector3, Vector3> fit, Vector3 origin, Vector3 angles,
                                   MapEntities.Entity entity, DmxBinary.Element entityNode)
    {
        static (int, int, int) Bits(Vector3 v) => (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
        var scratch = new VmapBuilder("samepoints");
        var mesh = scratch.AddMesh(scratch.World, 1, origin, angles, [.. piece.Faces.Select(f => (f.Select(fit).ToArray(), 0))], [piece.Material],
                                   [.. piece.Vertices.Select(fit)]);
        var hulls = Physics.EntityPhysicsModels.BrushMeshHulls(mesh, entityNode, entity.ClassName, VmapBuilder.FormatVersion);
        return hulls.Count == 1 && hulls[0].Length == piece.Vertices.Length
               && hulls[0].Select(Bits).Order().SequenceEqual(piece.Vertices.Select(Bits).Order());
    }

    /// <summary>A mesh-space corner <paramref name="place"/> takes to <paramref name="target"/> bit for bit: the
    /// estimate, then the floats round it in growing shells, up to 12 a side; null when none lands.</summary>
    private static Vector3? LocalCorner(Vector3 target, Vector3 estimate, Func<Vector3, Vector3> place)
    {
        static float Step(float f, int by)
        {
            for (; by > 0; by--) f = MathF.BitIncrement(f);
            for (; by < 0; by++) f = MathF.BitDecrement(f);
            return f;
        }
        static bool Same(Vector3 a, Vector3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
            && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
        for (var r = 0; r <= 12; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                    for (var dz = -r; dz <= r; dz++)
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) == r
                            && new Vector3(Step(estimate.X, dx), Step(estimate.Y, dy), Step(estimate.Z, dz)) is var c && Same(place(c), target))
                            return c;
        return null;
    }

    /// <summary>
    /// The corner of an unturned mesh at <paramref name="meshOrigin"/> that the compile's two moves
    /// (the mesh's CTransform to the world, the entity's inverse back; BrushHulls.PiecesWithCorners)
    /// place exactly on <paramref name="target"/>. A grid value is tried first; else the world point
    /// is refined from the inverse placement, then searched on the float grid round it, up to 4
    /// steps a side: far from the origin that grid is
    /// coarse, and the source's own corner went through it. The mesh's move is then a plain add
    /// (its matrix is the identity and the origin), which the corner minus the origin undoes
    /// exactly. With no exact point in reach, the nearest one found.
    /// </summary>
    internal static Vector3 CornerFor(Vector3 target, Vector3 meshOrigin, float[] toWorld, float[] toEntity, float[] fromEntity)
    {
        Vector3 Place(Vector3 v) => MapMeshes.Transform(toEntity, MapMeshes.Transform(toWorld, v));
        static long Key(float f) { var i = BitConverter.SingleToInt32Bits(f); return i < 0 ? int.MinValue - (long)i : i; }
        static long Ulps(Vector3 a, Vector3 b) => Math.Abs(Key(a.X) - Key(b.X)) + Math.Abs(Key(a.Y) - Key(b.Y)) + Math.Abs(Key(a.Z) - Key(b.Z));
        static float Step(float f, int by)
        {
            for (; by > 0; by--) f = MathF.BitIncrement(f);
            for (; by < 0; by++) f = MathF.BitDecrement(f);
            return f;
        }

        // A corner drawn on Hammer's grid: the inverse placement rounded to a power-of-two grid,
        // kept only where the compile places it exactly on the target, so nothing is assumed.
        var world = MapMeshes.Transform(fromEntity, target);
        for (var grid = 1f; grid >= 1f / 1024; grid /= 2)
        {
            var local = world - meshOrigin;
            var snapped = new Vector3(MathF.Round(local.X / grid) * grid, MathF.Round(local.Y / grid) * grid, MathF.Round(local.Z / grid) * grid);
            if (Place(snapped) == target)
                return snapped;
        }
        // Else the nearest float: the two matrices are not exact inverses, and far from the origin
        // the error grows past any small window, so a few Newton steps first, the residual taken
        // back through the entity's rotation in double.
        for (var i = 0; i < 8; i++)
        {
            var placed = Place(world - meshOrigin);
            if (placed == target)
                break;
            double rx = (double)target.X - placed.X, ry = (double)target.Y - placed.Y, rz = (double)target.Z - placed.Z;
            world = new Vector3(
                (float)(world.X + (fromEntity[0] * rx) + (fromEntity[1] * ry) + (fromEntity[2] * rz)),
                (float)(world.Y + (fromEntity[4] * rx) + (fromEntity[5] * ry) + (fromEntity[6] * rz)),
                (float)(world.Z + (fromEntity[8] * rx) + (fromEntity[9] * ry) + (fromEntity[10] * rz)));
        }
        var best = world - meshOrigin;
        var error = Ulps(Place(best), target);
        if (error == 0)
            return best;
        // The mesh's move is a plain add per axis, so the world points are searched on their own:
        // those the entity's inverse takes exactly to the target, nearest shells first (up to 16
        // floats a side), and for each a local float per axis that the add takes to it (the add
        // rounds, so not every world float is reachable from the corner side).
        static float? Addend(float want, float origin)
        {
            var guess = want - origin;
            for (var r = 0; r <= 8; r++)
                foreach (var s in r == 0 ? (int[])[0] : [r, -r])
                    if (Step(guess, s) is var x && x + origin == want)
                        return x;
            return null;
        }
        for (var r = 0; r <= 16; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                    for (var dz = -r; dz <= r; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r)
                            continue;
                        var w = new Vector3(Step(world.X, dx), Step(world.Y, dy), Step(world.Z, dz));
                        if (MapMeshes.Transform(toEntity, w) != target)
                        {
                            // The nearest reachable point so far, for the fallback.
                            var v0 = w - meshOrigin;
                            if (Ulps(Place(v0), target) is var e0 && e0 < error)
                                (best, error) = (v0, e0);
                            continue;
                        }
                        if (Addend(w.X, meshOrigin.X) is { } x && Addend(w.Y, meshOrigin.Y) is { } y && Addend(w.Z, meshOrigin.Z) is { } z
                            && Place(new Vector3(x, y, z)) == target)
                            return new Vector3(x, y, z);
                    }
        return best;
    }

    /// <summary>One map the decompile writes: the map itself (chain "") or a prefab's.</summary>
    /// <summary>
    /// A prefab loaded at runtime, from its point_prefab (MapEntities.Walk): its node id, map
    /// (maps/&lt;targetMapName&gt;.vmap, MapEntities.RuntimeMapName backwards), fixupNames and placement,
    /// and where it stands: before the node with walk index <paramref name="Before"/>, the next one
    /// the lump ships after it in its map (it takes no index itself), else at the map's end.
    /// </summary>
    private sealed record RuntimePrefab(int NodeId, string MapPath, bool Fixup, Vector3 Origin, Vector3 Angles, Vector3 Scales, int Before, string Lump);

    /// <summary>A lump entity's compile_source_id, or -1.</summary>
    private static int SourceIdOf(KVObject entity)
        => entity.TryGetValue("keyValues3Data", out var kv) && kv.TryGetValue("values", out var values) && values.TryGetValue("compile_source_id", out var id)
           && int.TryParse(EntityLumpAuthor.Plain(id), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;

    /// <summary>A lump entity that is a runtime prefab's point_prefab (prefab_load_dynamic, no compile_source_id), or null.</summary>
    private static RuntimePrefab? RuntimePrefabOf(KVObject entity, string lump)
    {
        if (!entity.TryGetValue("keyValues3Data", out var kv) || !kv.TryGetValue("values", out var values))
            return null;
        string? Text(string key) => values.TryGetValue(key, out var v) ? EntityLumpAuthor.Plain(v) : null;
        if (!string.Equals(Text("classname"), "point_prefab", StringComparison.OrdinalIgnoreCase) || Text("prefab_load_dynamic") is null
            || Text("compile_source_id") is not null)
            return null;
        Vector3 Vector(string key, Vector3 fallback)
            => values.TryGetValue(key, out var v) && v.Values?.Select(x => float.Parse(EntityLumpAuthor.Plain(x), System.Globalization.CultureInfo.InvariantCulture)).ToArray() is [var x, var y, var z]
                ? new Vector3(x, y, z) : fallback;
        var id = Text("hammerUniqueId") ?? "";
        return new RuntimePrefab(int.Parse(id[(id.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture),
                                 $"maps/{Text("targetMapName")}.vmap", Text("fixupNames") is "1", Vector("origin", Vector3.Zero), Vector("angles", Vector3.Zero),
                                 Vector("scales", Vector3.One), int.MaxValue, lump);
    }

    private sealed class Scope(string chain, Scope? parent)
    {
        public string Chain { get; } = chain;
        public Scope? Parent { get; } = parent;
        public List<EntityLumpAuthor.SourceEntity> Entities { get; } = [];
        public List<Scope> Children { get; } = [];
        public VmapBuilder? Builder { get; set; }

        /// <summary>Instance copies made inside this prefab's map (plain ids, template indices in its block).</summary>
        public List<EntityLumpAuthor.SourceEntity> Copies { get; } = [];

        /// <summary>The prefabs loaded at runtime that stand in this map, in lump order.</summary>
        public List<RuntimePrefab> Runtime { get; } = [];

        /// <summary>The scope's first place in lump order.</summary>
        public int Lump { get; set; }

        /// <summary>The first placement of the same prefab map, when this is a later one.</summary>
        public Scope? SameAs { get; set; }

        /// <summary>A later placement's prefab transform over the first's map.</summary>
        public (Vector3 Origin, Vector3 Angles) Placement { get; set; }

        /// <summary>A first placement's map variables: the node (inner id) and key each binds, and its value.</summary>
        public List<(string Inner, string Key, string Variable, string Value)> Variables { get; } = [];

        /// <summary>A later placement's overrides of its map's variables.</summary>
        public List<(string Variable, string Value)> Overrides { get; } = [];

        /// <summary>The map's instances rebuilt from copies of more than one collapse.</summary>
        public InstancePlan? Plan { get; set; }

        /// <summary>Where the copies' template group and its instance stand (a shared map's second frame).</summary>
        public Vector3 CopyGroupAt { get; set; }
        public Vector3 CopyInstanceAt { get; set; }

        /// <summary>The rebuilt instance, when the map has copies.</summary>
        public DmxBinary.Element? Instance { get; set; }

        /// <summary>The map's source of made-up node ids, once written.</summary>
        public Func<int>? NewId { get; set; }

        /// <summary>The prefab map's file name, when the package names it (<see cref="MapInputs"/>).</summary>
        public string? FileName { get; set; }

        /// <summary>The prefab map's content path: under the decompile's own folder, never the source's.</summary>
        public string Path(string mapName) => $"maps/prefabs/{mapName}/{FileName ?? "prefab_" + Chain.Replace(':', '_')}.vmap";

        /// <summary>The prefab node's own id: the last id of its chain.</summary>
        public int NodeId => int.Parse(Chain[(Chain.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The first walk index of the prefab map's nodes, its own or its prefabs'.</summary>
        public int FirstSourceId => Entities.Select(e => e.SourceId).Concat(Children.Select(c => c.FirstSourceId)).Min();
    }

    /// <summary>One slot of a template group, in preorder: a copy's template, a node that walks and ships
    /// nothing (an editor-only placeholder), or one that does not walk (an empty group).</summary>
    private sealed record TemplateSlot(EntityLumpAuthor.SourceEntity? Copy, bool Walks);

    /// <summary>How many collapses a scope's copies hold: in id order, a collapse's template indices
    /// rise, and the next collapse starts them again (as <see cref="TemplatePlan"/> splits them).</summary>
    private static int CollapseCount(List<EntityLumpAuthor.SourceEntity> copies)
    {
        var sorted = copies.OrderBy(c => c.Entity.NodeId).ToList();
        return 1 + sorted.Zip(sorted.Skip(1)).Count(p => p.Second.SourceId <= p.First.SourceId);
    }

    /// <summary>
    /// The template group a scope's copies come from, slot by slot. A copy's id is its collapse's
    /// block start plus its node's slot in the group, every node counted (MapInstances.Emit), and its
    /// compile_source_id its template's walk index; both run in preorder, so between two copies the
    /// walk-index gap is nodes that walk and ship nothing and the rest of the id gap nodes that do not
    /// walk. One collapse per scope (each template index once); more is noted and left out.
    /// </summary>
    private static List<TemplateSlot>? TemplatePlan(Scope scope, List<string> notes)
    {
        if (scope.Copies.Count == 0)
            return null;
        // Collapses of one template group give the same indices in the same id pattern, block after
        // block (one per placement of a shared prefab map): split by id where the index starts again.
        var sorted = scope.Copies.OrderBy(c => c.Entity.NodeId).ToList();
        var collapses = new List<List<EntityLumpAuthor.SourceEntity>> { new() };
        foreach (var c in sorted)
        {
            if (collapses[^1].Count > 0 && c.SourceId <= collapses[^1][^1].SourceId)
                collapses.Add([]);
            collapses[^1].Add(c);
        }
        var copies = collapses[0];
        var pattern = copies.Select(c => (c.SourceId, c.Entity.NodeId - copies[0].Entity.NodeId)).ToList();
        if (collapses.Any(k => !k.Select(c => (c.SourceId, c.Entity.NodeId - k[0].Entity.NodeId)).SequenceEqual(pattern)))
        {
            notes.Add($"prefab {scope.Chain}: {sorted.Count} instance copies from collapses of more than one template; not rebuilt");
            scope.Copies.Clear();
            return null;
        }
        scope.Copies.RemoveAll(c => !copies.Contains(c));
        var slots = new List<TemplateSlot> { new(copies[0], true) };
        for (var i = 1; i < copies.Count; i++)
        {
            var walkGap = copies[i].SourceId - copies[i - 1].SourceId - 1;
            var idGap = copies[i].Entity.NodeId - copies[i - 1].Entity.NodeId - 1;
            if (idGap < walkGap)
            {
                notes.Add($"prefab {scope.Chain}: copies {copies[i - 1].IdPath} and {copies[i].IdPath} leave fewer slots than walk indices; not rebuilt");
                scope.Copies.Clear();
                return null;
            }
            for (var k = 0; k < walkGap; k++)
                slots.Add(new TemplateSlot(null, true));
            for (var k = walkGap; k < idGap; k++)
                slots.Add(new TemplateSlot(null, false));
            slots.Add(new TemplateSlot(copies[i], true));
        }
        // The walking placeholders are written by the walk itself (FillTo), so only the copies
        // and the non-walking slots are listed for the writer; their order is kept.
        return [.. slots.Where(sl => sl.Copy is not null || !sl.Walks)];
    }

    /// <summary>
    /// A shared prefab map's frame (<see cref="FrameSolver"/>): every placement's nodes, its own and
    /// its instance copies (collapse k is placement k's: the collapses run in the prefabs' tree
    /// order), node for node; the solution's local placements are written into the map and the
    /// prefab nodes take its transforms. With no solution, the first placement stands where it is
    /// and the later ones take <see cref="SharedPlacement"/>, noted.
    /// </summary>
    /// <summary>
    /// The map variables a shared prefab map needs: a key a later placement's node ships with another
    /// value than the first's is bound to a variable "&lt;key&gt;_&lt;inner id&gt;" holding the first's
    /// value, which that placement overrides (the forward: MapVariables). Nodes are paired by inner id;
    /// a key one side lacks cannot be set this way (a binding sets a value, it removes none) and is noted.
    /// </summary>
    private static void PlanVariables(List<Scope> placements, List<string> notes)
    {
        var first = placements[0];
        static string Inner(EntityLumpAuthor.SourceEntity e) => e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..];
        var byInner = first.Entities.GroupBy(Inner).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var later in placements.Skip(1))
            foreach (var l in later.Entities)
            {
                if (!byInner.TryGetValue(Inner(l), out var f))
                    continue;
                var theirs = l.Entity.Keys.GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in f.Entity.Keys)
                {
                    if (!theirs.TryGetValue(key, out var other))
                    {
                        notes.Add($"prefab {later.Chain}: node {Inner(l)} has no {key} where prefab {first.Chain}'s has one; not rebuilt");
                        continue;
                    }
                    if (string.Equals(value, other, StringComparison.Ordinal))
                        continue;
                    var name = $"{key}_{Inner(f)}";
                    if (!first.Variables.Any(v => v.Variable == name))
                        first.Variables.Add((Inner(f), key, name, value));
                    later.Overrides.Add((name, other));
                }
            }
        if (first.Variables.Count > 0)
            notes.Add($"prefab {first.Chain}: {first.Variables.Count} map variables for keys its {placements.Count} placements ship differently");
    }

    private static void SolveFrame(List<Scope> placements, List<string> notes)
    {
        string Inner(EntityLumpAuthor.SourceEntity e) => e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..];
        var first = placements[0];
        var nodes = first.Entities.Where(e => placements.Skip(1).All(p => p.Entities.Any(x => Inner(x) == Inner(e)))).ToList();
        var copies = first.Copies.OrderBy(c => c.Entity.NodeId).ToList();
        var perCollapse = copies.Count / placements.Count;
        var placed = placements.Select((p, k) =>
            nodes.Select(e => p.Entities.First(x => Inner(x) == Inner(e)).Entity)
                 .Select(x => (x.Origin, x.Angles)).ToArray()).ToList();
        var solution = FrameSolver.Solve(placed, 32768, 2_000_000, out var why);
        if (solution is null)
        {
            notes.Add($"prefab {first.Chain}: no exact frame for its {placements.Count} placements ({why}); later placements solved from the first");
            foreach (var other in placements.Skip(1))
                other.Placement = SharedPlacement(first, other, notes);
            return;
        }
        notes.Add($"prefab {first.Chain}: frame solved for {placements.Count} placements, its prefab at {solution.FirstOrigin} ({solution.Tried:n0} frames tried)");
        first.Placement = (solution.FirstOrigin, Vector3.Zero);
        for (var k = 1; k < placements.Count; k++)
            placements[k].Placement = solution.Later[k];
        EntityLumpAuthor.SourceEntity Moved(EntityLumpAuthor.SourceEntity e, int j)
            => e with { Entity = e.Entity with { Origin = solution.Local[j].Origin, Angles = solution.Local[j].Angles } };
        for (var j = 0; j < nodes.Count; j++)
            first.Entities[first.Entities.IndexOf(nodes[j])] = Moved(nodes[j], j);
        // An instance's copies inside the map: the template rebuilt with its instance at its group's
        // transform, so each collapse moves nothing and the copy is the template as its prefab
        // places it; collapse k is placement k's. Each template's local placement is the one every
        // placement then lands on its copy.
        if (perCollapse > 0 && copies.Count == perCollapse * placements.Count)
        {
            var blockOf = CopyBlocks(solution, copies, perCollapse, placements.Count);
            var wanted = Enumerable.Range(0, perCollapse).Select(c => (IReadOnlyList<(Vector3 Origin, Vector3 Angles)>)
                [.. Enumerable.Range(0, placements.Count).Select(k => (copies[(blockOf[k] * perCollapse) + c].Entity.Origin, copies[(blockOf[k] * perCollapse) + c].Entity.Angles))]).ToList();
            var local = wanted.Select(w => FrameSolver.Locate(solution, w, copies: true)).ToArray();
            var missed = local.Count(l => l is null);
            if (missed > 0)
            {
                // Under a turned placement the copy's step rounds with the group's place (the moved
                // instance against the group): searched as a second frame.
                if (FrameSolver.LocateCopies(solution, wanted, 4096, CopyFrameBudget, out var places) is { } found)
                {
                    (first.CopyGroupAt, first.CopyInstanceAt) = (found.GroupAt, found.InstanceAt);
                    local = [.. found.Local.Select(l => ((Vector3, Vector3)?)l)];
                    notes.Add($"prefab {first.Chain}: its instance at {found.InstanceAt} over its group at {found.GroupAt} lands every copy ({places:n0} offsets tried)");
                }
                else
                    notes.Add($"prefab {first.Chain}: {missed} of {perCollapse} instance templates have no local placement that lands every copy ({places:n0} group offsets tried)");
            }
            for (var c = 0; c < perCollapse; c++)
                if (local[c] is { } at)
                    first.Copies[first.Copies.IndexOf(copies[c])] = copies[c] with { Entity = copies[c].Entity with { Origin = at.Origin, Angles = at.Angles } };
        }
    }

    /// <summary>Every subset of 0..n-1 of the given size, in index order.</summary>
    private static IEnumerable<int[]> Subsets(int n, int size)
    {
        if (size == 0) { yield return []; yield break; }
        for (var first = 0; first <= n - size; first++)
            foreach (var rest in Subsets(n - first - 1, size - 1))
                yield return [first, .. rest.Select(r => r + first + 1)];
    }

    /// <summary>The non-negative integers x nearest to solving columns·x = b in least squares (normal
    /// equations, Gaussian elimination, rounded); null when the system is singular.</summary>
    private static int[]? LeastSquares(List<int[]> columns, double[] b)
    {
        var n = columns.Count;
        var a = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
                a[i, j] = columns[i].Zip(columns[j], (p, q) => (double)p * q).Sum();
            a[i, n] = columns[i].Zip(b, (p, q) => p * q).Sum();
        }
        // A column that moves nothing is a padding that does nothing: left at 0.
        var live = Enumerable.Range(0, n).Where(i => a[i, i] != 0).ToList();
        var m = live.Count;
        var s = new double[m, m + 1];
        for (var i = 0; i < m; i++)
        {
            for (var j = 0; j < m; j++)
                s[i, j] = a[live[i], live[j]];
            s[i, m] = a[live[i], n];
        }
        for (var c = 0; c < m; c++)
        {
            var p = Enumerable.Range(c, m - c).MaxBy(r => Math.Abs(s[r, c]));
            if (Math.Abs(s[p, c]) < 1e-9)
                return null;
            for (var k = 0; k <= m; k++)
                (s[c, k], s[p, k]) = (s[p, k], s[c, k]);
            for (var r = 0; r < m; r++)
            {
                if (r == c) continue;
                var f = s[r, c] / s[c, c];
                for (var k = c; k <= m; k++)
                    s[r, k] -= f * s[c, k];
            }
        }
        var x = new int[n];
        for (var i = 0; i < m; i++)
            x[live[i]] = (int)Math.Round(s[i, m] / s[i, i]);
        return x;
    }

    /// <summary>
    /// Which block of copies (perCollapse a block, in id order) each placement's collapse wrote. The
    /// collapses run in the bake's rounds, not the lump's order (s2c_prefabprobe9: 115's collapse at
    /// the root comes a round before the nine inside 112, 113 and 114), so blocks are matched to
    /// placements by place: block 0's centroid taken back through a placement's solved transform and
    /// out through each other's lands nearest its own block; the assignment with the least total miss
    /// over every choice for block 0 is kept. Only a guess at the order: every copy is then located
    /// bit for bit through it (Locate), or not at all.
    /// </summary>
    private static int[] CopyBlocks(FrameSolver.Solution solution, List<EntityLumpAuthor.SourceEntity> copies, int perCollapse, int count)
    {
        var centroid = Enumerable.Range(0, count).Select(b =>
        {
            var s = Vector3.Zero;
            for (var c = 0; c < perCollapse; c++)
                s += copies[(b * perCollapse) + c].Entity.Origin;
            return s / perCollapse;
        }).ToArray();
        static Vector3 Out((Vector3 Origin, Vector3 Angles) t, Vector3 l)
        {
            var (sin, cos) = Math.SinCos(t.Angles.Y * Math.PI / 180);
            return new Vector3((float)((cos * l.X) - (sin * l.Y)), (float)((sin * l.X) + (cos * l.Y)), l.Z) + t.Origin;
        }
        static Vector3 Back((Vector3 Origin, Vector3 Angles) t, Vector3 w)
        {
            var d = w - t.Origin;
            var (sin, cos) = Math.SinCos(-t.Angles.Y * Math.PI / 180);
            return new Vector3((float)((cos * d.X) - (sin * d.Y)), (float)((sin * d.X) + (cos * d.Y)), d.Z);
        }
        var best = Enumerable.Range(0, count).ToArray();
        var bestMiss = double.MaxValue;
        for (var k0 = 0; k0 < count; k0++)
        {
            // Block 0 is placement k0's; each placement takes the free block nearest where block 0's
            // local centroid lands through it.
            var local = Back(solution.Later[k0], centroid[0]);
            var blockOf = new int[count];
            var used = new bool[count];
            var miss = 0.0;
            var ok = true;
            for (var k = 0; k < count && ok; k++)
            {
                var at = Out(solution.Later[k], local);
                var b = Enumerable.Range(0, count).Where(i => !used[i]).DefaultIfEmpty(-1).MinBy(i => i < 0 ? 0 : (centroid[i] - at).LengthSquared());
                if (b < 0) { ok = false; break; }
                used[b] = true;
                blockOf[k] = b;
                miss += (centroid[b] - at).Length();
            }
            if (ok && blockOf[k0] == 0 && miss < bestMiss)
                (best, bestMiss) = (blockOf, miss);
        }
        return best;
    }

    /// <summary>
    /// A nested family's prefab transforms moved into its outer prefab map's frame. The solve gives
    /// each placement's composed transform (where the outer prefab's matrix moves the inner prefab
    /// node, SettleWorld.PrefabPlacement); the inner node's own origin is the float the outer's
    /// first placement (unturned, a translation) moves onto it, and its angles the composed ones or
    /// floats near them, kept only when every node of the map lands through both prefabs as through
    /// the composed one, bit for bit.
    /// </summary>
    private static void IntoOuterFrame(List<Scope> placements, List<string> notes)
    {
        static DmxBinary.Element Prefab((Vector3 Origin, Vector3 Angles) t)
        {
            var e = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
            e.Attributes["origin"] = t.Origin;
            e.Attributes["angles"] = t.Angles;
            return e;
        }
        var nodes = placements[0].Entities.Select(e => (e.Entity.Origin, e.Entity.Angles)).ToList();
        foreach (var p in placements)
        {
            if (p.Parent is not { Parent: not null } outer || outer.Placement == default)
                continue;
            var outerNode = Prefab(outer.Placement);
            var composed = Prefab(p.Placement);
            var want = nodes.Select(n => SettleWorld.PrefabPlacement(n.Origin, n.Angles, [composed])).ToList();
            bool Lands((Vector3, Vector3) own)
            {
                var inner = Prefab(own);
                return nodes.Select((n, j) => SettleWorld.PrefabPlacement(n.Origin, n.Angles, [outerNode, inner]) == want[j]).All(x => x);
            }
            static float Preimage(float placed, float shift)
            {
                var x = placed - shift;
                for (var d = 0; d < 8 && x + shift != placed; d++)
                    x = x + shift < placed ? MathF.BitIncrement(x) : MathF.BitDecrement(x);
                return x;
            }
            var origin = new Vector3(Preimage(p.Placement.Origin.X, outer.Placement.Origin.X),
                                     Preimage(p.Placement.Origin.Y, outer.Placement.Origin.Y),
                                     Preimage(p.Placement.Origin.Z, outer.Placement.Origin.Z));
            var found = FrameSolver.AngleCandidates(p.Placement.Angles).Take(2000).Select(a => ((Vector3, Vector3)?)(origin, a))
                                   .FirstOrDefault(t => outer.Placement.Angles == Vector3.Zero && Lands(t!.Value));
            if (found is { } own)
                p.Placement = own;
            else
                notes.Add($"prefab {p.Chain}: no placement in prefab {outer.Chain}'s frame lands its nodes as the solve's does; left composed");
        }
    }

    /// <summary>
    /// A later placement of a shared map that places prefabs: every node it ships, its map's own and
    /// those of the prefabs inside (placed through it, then through the first placement's same
    /// prefab: SettleWorld.PrefabPlacement over both), as the lump holds them. Its transform from the
    /// solve is kept when that lands them all, else the floats near it (origin up to 3 either side
    /// on each axis, yaw up to 8) are tried, nearest first, and the first that lands every node taken.
    /// </summary>
    private static void LandNested(Scope first, Scope later, Func<Scope, Scope> canonical, List<string> notes)
    {
        static DmxBinary.Element Prefab((Vector3 Origin, Vector3 Angles) t)
        {
            var e = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
            e.Attributes["origin"] = t.Origin;
            e.Attributes["angles"] = t.Angles;
            return e;
        }
        static string Inner(string idPath) => idPath[(idPath.LastIndexOf(':') + 1)..];
        // Each node: its local placement in the map written, the prefab nodes between, and the lump's.
        var checks = new List<((Vector3 Origin, Vector3 Angles) Local, DmxBinary.Element[] Inside, (Vector3, Vector3) Want)>();
        foreach (var x in later.Entities)
            if (first.Entities.FirstOrDefault(e => Inner(e.IdPath) == Inner(x.IdPath)) is { } own)
                checks.Add(((own.Entity.Origin, own.Entity.Angles), [], (x.Entity.Origin, x.Entity.Angles)));
        foreach (var child in later.Children)
        {
            if (first.Children.FirstOrDefault(c => c.NodeId == child.NodeId) is not { } same)
                continue;
            var written = canonical(same);
            var through = Prefab(same.Placement);
            foreach (var x in child.Entities)
                if (written.Entities.FirstOrDefault(e => Inner(e.IdPath) == Inner(x.IdPath)) is { } own)
                    checks.Add(((own.Entity.Origin, own.Entity.Angles), [through], (x.Entity.Origin, x.Entity.Angles)));
        }
        bool Lands((Vector3 Origin, Vector3 Angles) t)
        {
            var node = Prefab(t);
            return checks.All(c => SettleWorld.PrefabPlacement(c.Local.Origin, c.Local.Angles, [node, .. c.Inside]) == c.Want);
        }
        if (Lands(later.Placement))
            return;
        var (o, a) = later.Placement;
        var near = from dx in Enumerable.Range(-3, 7) from dy in Enumerable.Range(-3, 7) from dz in Enumerable.Range(-3, 7)
                   from dyaw in Enumerable.Range(-8, 17)
                   orderby Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) + Math.Abs(dyaw)
                   select (new Vector3(FrameSolver.Step(o.X, dx), FrameSolver.Step(o.Y, dy), FrameSolver.Step(o.Z, dz)), a with { Y = FrameSolver.Step(a.Y, dyaw) });
        foreach (var t in near)
            if (Lands(t))
            {
                later.Placement = t;
                return;
            }
        notes.Add($"prefab {later.Chain}: no transform near the solved one lands the nodes of the prefabs inside it ({checks.Count} nodes)");
    }

    /// <summary>
    /// A later placement's prefab transform, with the first placement's map written where the first
    /// placement put its nodes (its prefab at the origin, unturned, so PrefabPlacement moves nothing):
    /// the rotation that turns a node of the first into the same node of this one, and the origin
    /// that then lands it (SettleWorld.PrefabPlacement: one matrix of the prefab's angles and origin).
    /// Taken from the first node both hold, then checked on every node through PrefabPlacement
    /// itself; a node it does not land bit for bit is noted.
    /// </summary>
    private static (Vector3 Origin, Vector3 Angles) SharedPlacement(Scope first, Scope other, List<string> notes)
    {
        var byId = first.Entities.ToDictionary(e => e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..]);
        var pairs = other.Entities.Select(e => (Mine: e, Theirs: byId.GetValueOrDefault(e.IdPath[(e.IdPath.LastIndexOf(':') + 1)..])))
                                  .Where(p => p.Theirs is not null).ToList();
        var (a, b) = (pairs[0].Theirs!.Entity, pairs[0].Mine.Entity);
        var ra = TemplateTransform.AngleMatrix(Vector3.Zero, a.Angles);
        var rb = TemplateTransform.AngleMatrix(Vector3.Zero, b.Angles);
        var turn = TemplateTransform.MatrixAngles(TemplateTransform.Concat(rb, TemplateTransform.Invert(ra)));
        // The origin that lands the node through the forward's own matrix of the angles
        // (SettleWorld.AngleMatrixDouble), worked in double so the rotation's error stays out of it.
        var r = SettleWorld.AngleMatrixDouble(turn, Vector3.Zero);
        double Row(int i, Vector3 v) => ((double)r[i * 4] * v.X) + ((double)r[(i * 4) + 1] * v.Y) + ((double)r[(i * 4) + 2] * v.Z);
        var origin = new Vector3((float)(b.Origin.X - Row(0, a.Origin)), (float)(b.Origin.Y - Row(1, a.Origin)), (float)(b.Origin.Z - Row(2, a.Origin)));
        var prefab = DmxBinary.NewElement("CMapPrefab", "", Guid.Empty);
        prefab.Attributes["origin"] = origin;
        prefab.Attributes["angles"] = turn;
        var misses = pairs.Where(p => SettleWorld.PrefabPlacement(p.Theirs!.Entity.Origin, p.Theirs.Entity.Angles, [prefab])
                                      != (p.Mine.Entity.Origin, p.Mine.Entity.Angles)).ToList();
        var missed = misses.Count;
        if (Environment.GetEnvironmentVariable("DECOMPILE_MISSES") == "1")
            foreach (var m in misses.Take(4))
                notes.Add($"  miss {m.Mine.IdPath} {m.Mine.Entity.ClassName}: from {m.Theirs!.Entity.Origin:R} {m.Theirs.Entity.Angles:R} lands {SettleWorld.PrefabPlacement(m.Theirs.Entity.Origin, m.Theirs.Entity.Angles, [prefab]):R}, wants {(m.Mine.Entity.Origin, m.Mine.Entity.Angles):R}");
        if (missed > 0)
            notes.Add($"prefab {other.Chain}: placed again as prefab {first.Chain}'s map at {origin:R} turned {turn:R}, {missed} of {pairs.Count} nodes not landed bit for bit");
        return (origin, turn);
    }

    /// <summary>The copies' ids the forward gives the rebuilt maps, and the scope each comes from:
    /// MapInstances.Expand on the decompiled map with its prefab maps attached.</summary>
    private static List<(Scope Scope, int Id)> SimulatedCopyIds(Scope main, IEnumerable<Scope> scopes, string mapName, FgdSchema schema)
    {
        var docs = scopes.Where(sc => sc.Parent is not null && sc.Builder is not null)
                         .ToDictionary(sc => sc.Path(mapName), sc => (Scope: sc, Doc: MapNodeAngles.Apply(DmxBinary.Read(sc.Builder!.Write()))),
                                       StringComparer.OrdinalIgnoreCase);
        var doc = MapNodeAngles.Apply(DmxBinary.Read(main.Builder!.Write()));
        MapPrefabs.Attach(doc, path => docs.TryGetValue(path.Replace('\\', '/'), out var d) ? d.Doc : null);
        var walked = MapEntities.From(doc);
        var (copies, _) = MapInstances.Expand(doc, walked, prefabs: true);
        var result = new List<(Scope, int)>();
        foreach (var copy in copies)
        {
            var owner = copy.Prefabs.Length == 0 ? main
                : docs.Values.FirstOrDefault(d => d.Doc.OfType("CMapWorld").FirstOrDefault() is { } w && copy.Prefabs[^1].Get<DmxBinary.Element>(MapPrefabs.WorldKey) == w).Scope;
            if (owner is not null)
                result.Add((owner, copy.NodeId));
        }
        return result;
    }

    /// <summary>A prefab's entity-name fixup as its contents show it: <paramref name="Own"/> the
    /// part it adds (its node id, or its targetName with useTargetNameAsPrefix), null with fixup off;
    /// <paramref name="NamePrefix"/> the whole prefix its nodes take after "[PR#]" (MapEntities.Walk).</summary>
    private sealed record PrefabFixup(string? Own, string? TargetName, string? NamePrefix);

    /// <summary>
    /// Each prefab's fixup, from its nodes' names. MapEntities.Walk: a prefab with
    /// fixupEntityNames appends "&lt;own&gt;_" to its parent's prefix, own being its targetName
    /// when useTargetNameAsPrefix is on and the name is not empty, else its node id; one without
    /// keeps its parent's. A node's names then take "[PR#]" and the prefix, or "[PR#]" alone when
    /// only the map fixes up (EntityLumpAuthor.BuildEntity). The map's own fixup shows on its own
    /// named nodes. Names the fixup leaves alone (empty, a class name, one starting with !*?@;
    /// EntityLumpAuthor.Fixup) say nothing.
    /// </summary>
    private static Dictionary<string, PrefabFixup> PrefabFixups(List<(string IdPath, string Name)> named, FgdSchema schema, out bool mapFixup)
    {
        bool Telling(string name) => name.Length > 0 && name[0] is not ('!' or '*' or '?' or '@') && !schema.HasClass(name);
        var byChain = named.Where(n => Telling(n.Name)).GroupBy(n => ChainOf(n.IdPath)).ToDictionary(g => g.Key, g => g.Select(n => n.Name).ToList());
        mapFixup = byChain.GetValueOrDefault("")?.Any(n => n.StartsWith(EntityLumpAuthor.NameFixupPrefix, StringComparison.Ordinal)) == true;
        var result = new Dictionary<string, PrefabFixup>(StringComparer.Ordinal);
        foreach (var chain in named.Select(n => ChainOf(n.IdPath)).Where(c => c.Length > 0).Distinct().OrderBy(c => c.Count(x => x == ':')))
            Resolve(chain);
        // A node of a prefab with no fixup of its own and no outer prefix takes "[PR#]" alone only
        // from the map's fixup (EntityLumpAuthor.BuildEntity), so its names show the map's too.
        mapFixup |= result.Where(r => r.Value.NamePrefix is null)
                          .Any(r => byChain.GetValueOrDefault(r.Key)?.Any(n => n.StartsWith(EntityLumpAuthor.NameFixupPrefix, StringComparison.Ordinal)) == true);
        return result;

        PrefabFixup Resolve(string chain)
        {
            if (result.TryGetValue(chain, out var known))
                return known;
            var outer = chain.Contains(':') ? Resolve(chain[..chain.LastIndexOf(':')]).NamePrefix : null;
            var id = chain[(chain.LastIndexOf(':') + 1)..];
            var names = byChain.GetValueOrDefault(chain) ?? [];
            var lead = EntityLumpAuthor.NameFixupPrefix + (outer ?? "");
            string? own = null, target = null;
            if (names.Count > 0 && names.All(n => n.StartsWith(lead, StringComparison.Ordinal) && n.IndexOf('_', lead.Length) > lead.Length))
            {
                var tokens = names.Select(n => n[lead.Length..n.IndexOf('_', lead.Length)]).Distinct().ToList();
                // A node id is the prefab's own unless every name shares another token: the targetName.
                if (tokens.Count == 1)
                    (own, target) = tokens[0] == id ? (id, null) : (tokens[0], tokens[0]);
            }
            return result[chain] = new PrefabFixup(own, target, own is null ? outer : (outer ?? "") + own + "_");
        }
    }

    /// <summary>A node's prefab chain: its id path less its own id ("" for the map's own nodes).</summary>
    private static string ChainOf(string idPath) => idPath.Contains(':') ? idPath[..idPath.LastIndexOf(':')] : "";

    private static string IdPath(KVObject entity)
    {
        if (!entity.TryGetValue("keyValues3Data", out var kv) || !kv.TryGetValue("values", out var values))
            return "";
        return values.TryGetValue("hammerUniqueId", out var id) ? EntityLumpAuthor.Plain(id) : "";
    }

    private static bool IsTemplateLump(string path) => Path.GetFileName(path).Contains('#');

    /// <summary>The suffix the template pass gives a member's name and every reference to it
    /// (EntityLumpSet.Rename).</summary>
    private const string TemplateRename = "&0000";

    /// <summary>
    /// A template member whose original the template removed: the copy placed back where the
    /// template's transform puts it, and the names the template pass suffixed restored. Only a
    /// point_template places its copies relative to itself; any other template class puts them
    /// at the origin, and where they stood is lost.
    /// </summary>
    private static EntityLumpAuthor.SourceEntity FromTemplateCopy(EntityLumpAuthor.SourceEntity copy, string lump,
                                                                  List<EntityLumpAuthor.SourceEntity> written, List<string> notes)
    {
        var owner = Path.GetFileName(lump).Split('#')[0];
        var template = written.FirstOrDefault(w => w.IdPath.Replace(':', '_') == owner);
        var e = copy.Entity;
        static string Unrename(string s) => s.EndsWith(TemplateRename, StringComparison.Ordinal) ? s[..^TemplateRename.Length] : s;
        var keys = e.Keys.Select(k => new KeyValuePair<string, string>(k.Key, Unrename(k.Value))).ToList();
        var connections = e.Connections.Select(c => c with { TargetName = Unrename(c.TargetName), OverrideParam = Unrename(c.OverrideParam) }).ToList();
        var (origin, angles) = (e.Origin, e.Angles);
        // _template_lump_ent_index is the copy's place in the lump, written by the pass.
        keys.RemoveAll(k => k.Key.EqualsAscii("_template_lump_ent_index"));
        if (template is not null && template.Entity.ClassName.EqualsAscii("point_template"))
        {
            if (TemplatePlacement.Solve(template.Entity.Origin, template.Entity.Angles, origin, angles,
                                        PrintedPlacement(copy.Raw, "origin"), PrintedPlacement(copy.Raw, "angles")) is { } placed)
                (origin, angles) = placed;
            else
            {
                var m = TemplateTransform.Concat(TemplateTransform.AngleMatrix(template.Entity.Origin, template.Entity.Angles),
                                                 TemplateTransform.AngleMatrix(origin, angles));
                (origin, angles) = (new Vector3(m[3], m[7], m[11]), TemplateTransform.MatrixAngles(m));
                notes.Add($"{e.ClassName} {copy.IdPath}: no placement lands on its template copy's; the inverse's kept");
            }
        }
        else
            notes.Add($"{e.ClassName} {copy.IdPath}: only a copy in {Path.GetFileName(lump)} survives, and that template places it at the origin; its placement is lost");
        return copy with { Entity = e with { Keys = keys, Connections = connections, Origin = origin, Angles = angles } };
    }

    /// <summary>A placement key the lump wrote as a "%f %f %f" string (an older compile), as its three
    /// words; null when it is today's float array or absent.</summary>
    private static string[]? PrintedPlacement(KVObject? raw, string key)
        => raw is not null && raw.TryGetValue("keyValues3Data", out var kv) && kv.TryGetValue("values", out var values)
           && values.TryGetValue(key, out var v) && !v.IsArray
           && EntityLumpAuthor.Plain(v).Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 3 } words ? words : null;

    /// <summary>What the map loads that is not built into it (memory of the FGD survey, 10-20): a
    /// skybox_reference's targetMapName and an info_spawngroup_load_unload's mapname are levels of
    /// their own (their own maps/&lt;name&gt;.vpk, decompiled on their own), a point_script's
    /// cs_script a compiled script in the addon's package (<see cref="ScriptSources"/>). The keys
    /// themselves round trip with the entities; this lists them so the rest of the addon can follow.</summary>
    public static List<(string ClassName, string Key, string Target)> Links(IEnumerable<EntityLumpAuthor.SourceEntity> entities)
    {
        var links = new List<(string, string, string)>();
        foreach (var e in entities)
        {
            var key = e.Entity.ClassName switch
            {
                "skybox_reference" => "targetMapName",
                "info_spawngroup_load_unload" => "mapname",
                "point_script" => "cs_script",
                _ => null,
            };
            if (key is null)
                continue;
            var value = e.Entity.Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(value))
                links.Add((e.Entity.ClassName, key, value));
        }
        return links;
    }

    /// <summary>The input dependencies the map's .vmap_c records (its RED2 block): content-relative file
    /// names with their CRC, the map's own source first, then every file the compile read (prefab maps
    /// among them).</summary>
    public static List<(string File, uint Crc)> MapInputs(IReadOnlyDictionary<string, byte[]> files, string mapName)
    {
        if (!files.TryGetValue($"maps/{mapName}.vmap_c", out var bytes))
            return [];
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.EditInfo is not ValveResourceFormat.Blocks.ResourceEditInfo info)
            return [];
        return [.. info.InputDependencies.Concat(info.AdditionalInputDependencies).Select(d => (d.ContentRelativeFilename, d.FileCRC))];
    }

    /// <summary>Every entry of a map package, by path.</summary>
    public static Dictionary<string, byte[]> ReadPackage(string vpk)
    {
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in package.Entries.Values)
            foreach (var entry in group)
                files[entry.GetFullPath()] = Io.VpkEntries.Read(package, entry);
        return files;
    }

    /// <summary>The map a package holds: its one maps/&lt;name&gt;.vmap_c.</summary>
    public static string MapNameOf(IReadOnlyDictionary<string, byte[]> files)
    {
        var maps = files.Keys.Where(k => k.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)
                                         && k.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase)
                                         && k.Count(c => c == '/') == 1).ToList();
        return maps.Count == 1
            ? Path.GetFileNameWithoutExtension(maps[0])
            : throw new InvalidDataException($"the package holds {maps.Count} maps/*.vmap_c, not one");
    }

    private static IEnumerable<KVObject> LumpEntities(byte[] bytes, string path)
    {
        using var resource = new Resource { FileName = Path.GetFileName(path) };
        resource.Read(new MemoryStream(bytes));
        var data = resource.DataBlock as EntityLump
                   ?? throw new InvalidDataException($"{path} has no entity lump DATA block");
        return data.Data.GetArray("m_entityKeyValues").ToList();
    }

    private static string Name(KVObject entity)
    {
        if (!entity.TryGetValue("keyValues3Data", out var kv) || !kv.TryGetValue("values", out var values))
            return "";
        return values.TryGetValue("targetname", out var name) ? EntityLumpAuthor.Plain(name) : "";
    }
}
