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
    }

    private const string LayerLumpPrefix = "world_layer_";

    /// <summary>The class of a walk position nothing shipped from.</summary>
    public const string PlaceholderClass = "info_target";

    /// <summary>Decompile the map in <paramref name="files"/> (path to bytes: a package's
    /// entries) named <paramref name="mapName"/>.</summary>
    /// <param name="materials">Tool material hash to name (<see cref="BrushGeometry.MaterialTable"/>) over the
    /// game's materials; the package's own are added. Null: the package's only.</param>
    public static Result Decompile(IReadOnlyDictionary<string, byte[]> files, string mapName, FgdSchema schema,
                                   IReadOnlyDictionary<uint, string>? materials = null)
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (lump, e) in raw)
        {
            var source = EntityLumpAuthor.Unbuild(e, schema, FixupOf(IdPath(e)));
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
                blocks.Last(b => b.Start <= e.SourceId).Scope.Copies.Add(e);
            }

        var main = scopes[""];
        main.Builder = new VmapBuilder(mapName);
        // The lump's mapUsageType is the literal the compile writes for every map (BuildEntity),
        // so it says nothing of the source's.
        main.Builder.SetWorld(world.Entity.NodeId, world.Entity.Keys, world.Entity.Connections, mapFixup, "standard");

        var placeholders = 0;
        var at = 1;
        Emit(main);
        if (placeholders > 0)
            notes.Add($"{placeholders} walk positions shipped nothing (hidden, editor-only or compile-only nodes); filled with editor-only {PlaceholderClass}");
        MatchCopyIds();
        var prefabFiles = scopes.Values.Where(sc => sc.Parent is not null && sc.Builder is not null)
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
            var used = new HashSet<int>(scope.Entities.Select(e => e.Entity.NodeId).Concat(scope.Children.Select(c => c.NodeId)));
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
            var templates = TemplatePlan(scope, notes);
            var items = scope.Entities.Select(e => (e.SourceId, Entity: (EntityLumpAuthor.SourceEntity?)e, Template: (List<TemplateSlot>?)null)).ToList();
            if (templates is not null)
                items.Add((templates.First(t => t.Copy is not null).Copy!.SourceId, null, templates));
            DmxBinary.Element? group = null;
            foreach (var (sourceId, e, slots) in items.OrderBy(i => i.SourceId))
            {
                FillTo(sourceId);
                if (slots is not null)
                {
                    group = builder.AddGroup(NewId());
                    foreach (var slot in slots)
                    {
                        if (slot.Copy is { } copy)
                        {
                            FillTo(copy.SourceId, group);
                            var t = EntityLumpAuthor.Unbuild(copy.Raw!, schema, FixupOf(scope.Chain + ":0"), instanced: true).Entity;
                            var node = builder.AddEntity(NewId(), t.Keys, t.Origin, t.Angles, t.Scales, t.Connections, parent: group);
                            if (copy.BrushModel is { } copyModel)
                                AddBrushGeometry(builder, node, copy, copyModel, files, table, notes, NewId);
                            at = copy.SourceId + 1;
                        }
                        else if (!slot.Walks)
                            builder.AddGroup(NewId(), group);
                    }
                    continue;
                }
                var layer = layerOf.GetValueOrDefault(e!);
                if (layer is not null && layer != openLayer)
                    layerNode = builder.AddLayer(NewId(), layer);
                openLayer = layer;
                var x = e!.Entity;
                var entityNode = builder.AddEntity(x.NodeId, x.Keys, x.Origin, x.Angles, x.Scales, x.Connections,
                                                   parent: layer is null ? null : layerNode);
                if (e.BrushModel is { } model)
                    AddBrushGeometry(builder, entityNode, e, model, files, table, notes, NewId);
                at = Math.Max(at, e.SourceId + 1);
            }
            foreach (var child in scope.Children.OrderBy(c => c.FirstSourceId))
            {
                // The prefab map's world takes the index ahead of its first node.
                FillTo(child.FirstSourceId - 1);
                at++;
                var fix = prefabs.GetValueOrDefault(child.Chain);
                builder.AddPrefab(child.NodeId, child.Path(mapName), fix?.Own is not null, fix?.TargetName ?? "");
                child.Builder = new VmapBuilder($"{mapName}/{child.Chain}");
                child.Builder.SetWorld(1, [new("classname", "worldspawn")], [], false, "standard");
                Emit(child);
            }
            // The instance, at the map's root after its nodes: its copies land where the map's
            // walk ends (the parent's subtree end, MapInstances.EmitAt), and with its transform
            // its group's (both the origin, unturned) the collapse moves nothing
            // (SettleWorld.BakedPlacement), so each copy stands where its template does.
            if (group is not null)
                scope.Instance = builder.AddInstance(NewId(), group, Vector3.Zero, Vector3.Zero);
            scope.NewId = NewId;
        }

        // The copies' ids: each collapse's block starts where the ones before it end, and a
        // prefab's block is its map's copy count plus one (MapInstances.Expand). The forward is
        // run on the rebuilt maps, and a collapse whose copies come out short of their ids has
        // the prefab map before it padded by the difference: each node a map gains moves every
        // later block by one, so one pass per collapse settles it.
        void MatchCopyIds()
        {
            foreach (var scope in scopes.Values.Where(sc => sc.Instance is not null).OrderBy(sc => sc.Copies.Min(c => c.Entity.NodeId)))
            {
                var target = scope.Copies.Min(c => c.Entity.NodeId);
                var simulated = SimulatedCopyIds(main, scopes.Values, mapName, schema).Where(c => c.Scope == scope).Select(c => c.Id).DefaultIfEmpty(int.MaxValue).Min();
                var short_ = target - simulated;
                if (short_ < 0 || simulated == int.MaxValue)
                {
                    notes.Add($"prefab {scope.Chain}: its instance's copies come out at {simulated}, not {target}; not matched");
                    continue;
                }
                for (var i = 0; i < short_; i++)
                    scope.Builder!.AddGroup(scope.NewId!());
            }
        }
        return new Result(main.Builder!.Write(), entities, placeholders, notes) { Prefabs = prefabFiles };
    }

    /// <summary>
    /// A brush entity's meshes from its model (<see cref="BrushGeometry"/>): one mesh per hull,
    /// placed where the entity is, its corners the hull's in the entity's space, so the
    /// compile's move into the entity's space (BrushHulls.Pieces) is the identity. Returns the
    /// next free node id.
    /// </summary>
    private static void AddBrushGeometry(VmapBuilder builder, DmxBinary.Element node, EntityLumpAuthor.SourceEntity e, string model,
                                         IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<uint, string> table,
                                         List<string> notes, Func<int> newId)
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
        // The model's part takes its shapes through PartOrder, a CRT qsort on shape type after
        // every insert: with one type that is a fixed permutation of the input, which the meshes
        // are written in the inverse of, so the hulls come out in the model's order.
        var count = geometry.Pieces.Count;
        var placedAt = Physics.WorldCollision.PartOrder(Enumerable.Range(0, count), _ => 0);
        var input = new BrushGeometry.Piece[count];
        for (var k = 0; k < count; k++)
            input[placedAt[k]] = geometry.Pieces[k];
        foreach (var piece in input)
            builder.AddMesh(node, newId(), e.Entity.Origin, Vector3.Zero,
                            [.. piece.Faces.Select(f => (f.Select(Fit).ToArray(), 0))], [piece.Material],
                            [.. piece.Vertices.Select(Fit)]);
        if (geometry.HasRender)
            notes.Add($"{label}: render mesh not read (texture alignment and render-only faces are not recovered)");
        if (geometry.TriangleMeshes > 0)
            notes.Add($"{label}: {geometry.TriangleMeshes} triangle-mesh shape(s) not read");
        if (geometry.UnknownMaterials.Count > 0)
            notes.Add($"{label}: {geometry.UnknownMaterials.Count} hull(s) with a material hash no known material has");
        if (geometry.Pieces.Count == 0)
            notes.Add($"{label}: its model has no hulls; no geometry");
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
        for (var r = 0; r <= 4 && error > 0; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                    for (var dz = -r; dz <= r; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r)
                            continue;
                        var w = new Vector3(Step(world.X, dx), Step(world.Y, dy), Step(world.Z, dz));
                        var v = w - meshOrigin;
                        var e = Ulps(Place(v), target);
                        if (e < error)
                            (best, error) = (v, e);
                        if (error == 0)
                            return best;
                    }
        return best;
    }

    /// <summary>One map the decompile writes: the map itself (chain "") or a prefab's.</summary>
    private sealed class Scope(string chain, Scope? parent)
    {
        public string Chain { get; } = chain;
        public Scope? Parent { get; } = parent;
        public List<EntityLumpAuthor.SourceEntity> Entities { get; } = [];
        public List<Scope> Children { get; } = [];
        public VmapBuilder? Builder { get; set; }

        /// <summary>Instance copies made inside this prefab's map (plain ids, template indices in its block).</summary>
        public List<EntityLumpAuthor.SourceEntity> Copies { get; } = [];

        /// <summary>The rebuilt instance, when the map has copies.</summary>
        public DmxBinary.Element? Instance { get; set; }

        /// <summary>The map's source of made-up node ids, once written.</summary>
        public Func<int>? NewId { get; set; }

        /// <summary>The prefab map's content path.</summary>
        public string Path(string mapName) => $"maps/prefabs/{mapName}/prefab_{Chain.Replace(':', '_')}.vmap";

        /// <summary>The prefab node's own id: the last id of its chain.</summary>
        public int NodeId => int.Parse(Chain[(Chain.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The first walk index of the prefab map's nodes, its own or its prefabs'.</summary>
        public int FirstSourceId => Entities.Select(e => e.SourceId).Concat(Children.Select(c => c.FirstSourceId)).Min();
    }

    /// <summary>One slot of a template group, in preorder: a copy's template, a node that walks and ships
    /// nothing (an editor-only placeholder), or one that does not walk (an empty group).</summary>
    private sealed record TemplateSlot(EntityLumpAuthor.SourceEntity? Copy, bool Walks);

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
        var copies = scope.Copies.OrderBy(c => c.Entity.NodeId).ToList();
        if (copies.Select(c => c.SourceId).Distinct().Count() != copies.Count
            || !copies.Zip(copies.Skip(1)).All(p => p.First.SourceId < p.Second.SourceId))
        {
            notes.Add($"prefab {scope.Chain}: {copies.Count} instance copies from more than one collapse; not rebuilt");
            scope.Copies.Clear();
            return null;
        }
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
            var owner = copy.Prefabs.Length == 0 ? null
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
            var m = TemplateTransform.Concat(TemplateTransform.AngleMatrix(template.Entity.Origin, template.Entity.Angles),
                                             TemplateTransform.AngleMatrix(origin, angles));
            (origin, angles) = (new Vector3(m[3], m[7], m[11]), TemplateTransform.MatrixAngles(m));
        }
        else
            notes.Add($"{e.ClassName} {copy.IdPath}: only a copy in {Path.GetFileName(lump)} survives, and that template places it at the origin; its placement is lost");
        return copy with { Entity = e with { Keys = keys, Connections = connections, Origin = origin, Angles = angles } };
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
