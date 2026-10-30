using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Source2.Compiler;
using Source2.Compiler.Maps.Decompile;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The decompile's entities against the Hammer source they were compiled from, through
/// the forward compile: Valve's package is decompiled, and our lump author run on the
/// decompiled .vmap must give what it gives on the true source, lump for lump, entity
/// for entity, value, type, key order and connection. Our author is the yardstick
/// rather than Valve's lump because it is the same code on both sides: what it does not
/// yet do (EntityLumpAgainstValveTests' gaps) cancels out, and only the decompile shows.
///
/// <para>A difference must be one of the gaps written down in <see cref="Gaps"/>; the
/// rest fail the test, so a gap closing shows as a list that can shrink.</para>
/// </summary>
public partial class MapDecompilerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "ze_doom_p2_c_gameplay")]
    [InlineData("s2c_rc_probe", "probe01")]
    // Prefabs and instances (docs/DECOMPILE.md stage 3): a prefab (s2c_prefabprobe), one instance
    // (s2c_pinst), an instance placed by a prefab (5), a prefab's prefab and instances (7),
    // nested instances with paths and deformers (s2c_instnest), and atixref's 144 instances.
    [InlineData("s2c_rc_probe", "s2c_prefabprobe")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe2")]
    [InlineData("s2c_rc_probe", "s2c_pinst")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe5")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe7")]
    [InlineData("s2c_rc_probe", "s2c_instnest")]
    [InlineData("s2c_rc_probe", "atixref")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe3")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe4")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe6")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe8")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe9")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe11")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe12")]
    [InlineData("s2c_rc_probe", "s2c_pinst7")]
    [InlineData("s2c_rc_probe", "s2c_prefabinst")]
    // Cables rebuilt as CMapCable: 17 collision cables over every cable setting (s2c_cablecoll) and
    // render-only ones over materials, tints and shapes (s2c_cablerender), both -world builds.
    [InlineData("s2c_rc_probe", "s2c_cablecoll")]
    [InlineData("s2c_rc_probe", "s2c_cablerender")]
    // Map variables: the map's own and a prefab map's, overridden and at their defaults.
    [InlineData("s2c_rc_probe", "s2c_varprobe")]
    // Prefabs loaded at runtime: point_prefab back to CMapPrefab (runtime_prefab_probe_map.py).
    [InlineData("s2c_rc_probe", "s2c_rtprefab")]
    [InlineData("s2c_rc_probe", "s2c_rtprefab4")]
    [InlineData("s2c_rc_probe", "s2c_rtprefab6")]
    public void Entities_CompileAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();

        var files = MapDecompiler.ReadPackage(package);
        var result = MapDecompiler.Decompile(files, map, schema, StockMaterials());
        // DECOMPILE_SAVE=<dir>: the decompile this check judges, as <dir>/<map>.vmap.
        if (Environment.GetEnvironmentVariable("DECOMPILE_SAVE") is { Length: > 0 } saveTo)
            File.WriteAllBytes(Path.Combine(saveTo, map + ".vmap"), result.Vmap);
        var decompiled = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
        var truth = DmxBinary.ReadFile(source);

        // Both sides settle as the compile does. A prop Valve's settle put to sleep ships with "Start
        // asleep", which the settle skips (SettleWorld.IsEligible), so the decompile's settled pose
        // stands; where the settle refuses the source, neither side settles.
        var (expected, settled) = Lumps(truth, map, schema, source, settle: true);
        var (ours, _) = Lumps(decompiled, map, schema, source, settled, result);
        output.WriteLine($"{map}: {result.Entities.Count} entities, {result.Placeholders} placeholders, {result.Notes.Count} notes, " +
                         $"{result.Vmap.Length:n0} bytes{(settled ? "" : ", not settled")}");
        foreach (var note in result.Notes.GroupBy(n => Regex.Replace(n, @"^\S+ \S+: ", "")).Take(15))
            output.WriteLine($"  note x{note.Count()}: {note.Key}");
        // DECOMPILE_TREE=1: both maps' node trees (type, nodeID, classname, an instance's target).
        if (Environment.GetEnvironmentVariable("DECOMPILE_TREE") is { Length: > 0 })
            foreach (var (side, doc) in new[] { ("truth", truth), ("ours", decompiled) })
            {
                output.WriteLine($"  {side} tree:");
                // DECOMPILE_TREE=2: with each node's origin and angles, and the true prefab maps' trees.
                var placements = Environment.GetEnvironmentVariable("DECOMPILE_TREE") == "2";
                void Print(DmxBinary.Element e, int depth)
                {
                    var cls = e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname");
                    var target = e.Get<DmxBinary.Element>("target") is { } t ? $" -> {t.GetValue<int>("nodeID")}" : "";
                    var at = placements && e.GetValue<Vector3>("origin") is { } o ? $" @ {o:R} {e.GetValue<Vector3>("angles") ?? Vector3.Zero:R}" : "";
                    var map = e.Get<string>("targetMapPath") is { } m ? $" [{m}]" : "";
                    output.WriteLine($"    {new string(' ', depth * 2)}{e.Type} {e.GetValue<int>("nodeID")}{(cls is null ? "" : " " + cls)}{target}{map}{at}");
                    foreach (var c in e.GetElements("children")) Print(c, depth + 1);
                }
                if (doc.Root?.Get<DmxBinary.Element>("world") is { } world) Print(world, 0);
                if (placements && side == "truth")
                    foreach (var m in doc.Elements.Select(e => e.Get<string>("targetMapPath")).OfType<string>().Distinct())
                        if (Path.Combine(Path.GetDirectoryName(source)!, "..", m) is var file && File.Exists(file)
                            && DmxBinary.ReadFile(file).Root?.Get<DmxBinary.Element>("world") is { } inner)
                        {
                            output.WriteLine($"  truth {m}:");
                            Print(inner, 0);
                        }
            }

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), ours.Keys.Order(StringComparer.OrdinalIgnoreCase));
        // A brush entity with no geometry does not ship (EntityLumpAuthor.ReachesTheLump), and the
        // decompile does not rebuild geometry yet: those are left out of the truth side, counted.
        // A brush entity whose model the package lacks (the compile failed it) or holds no hull
        // comes back with no geometry, does not ship (EntityLumpAuthor.ReachesTheLump), and is
        // left out of the truth side, counted.
        var brush = result.Notes.Where(n => n.EndsWith("no geometry", StringComparison.Ordinal))
                                .Select(n => n.Split(' ')[1].TrimEnd(':')).ToHashSet(StringComparer.Ordinal);
        var unexplained = new List<string>();
        var explained = new Dictionary<string, int>();
        foreach (var (path, bytes) in expected)
        {
            var truthSide = EntityLumpComparison.Read(bytes, path);
            var withoutBrush = truthSide.Where(e => !brush.Contains(e.HammerId)).ToList();
            if (withoutBrush.Count != truthSide.Count)
                explained["brush entity with no model or no hull: no geometry, so it does not ship"] =
                    explained.GetValueOrDefault("brush entity with no model or no hull: no geometry, so it does not ship") + truthSide.Count - withoutBrush.Count;
            var oursSide = EntityLumpComparison.Read(ours[path], path).Where(e => !brush.Contains(e.HammerId)).ToList();
            // Valve's own lump of the package, for keys a lit compile rewrites.
            var shipped = files.FirstOrDefault(f => f.Key.EndsWith(path, StringComparison.OrdinalIgnoreCase)).Value is { } lump
                ? EntityLumpComparison.Read(lump, path).GroupBy(e => e.HammerId).ToDictionary(g => g.Key, g => g.First())
                : [];
            // DECOMPILE_LUMP=<lump file name>: both sides' entities of that lump, in order.
            if (Environment.GetEnvironmentVariable("DECOMPILE_LUMP") is { Length: > 0 } listed && Path.GetFileName(path) == listed)
                foreach (var (side, list) in new[] { ("truth", truthSide), ("ours", EntityLumpComparison.Read(ours[path], path).ToList()) })
                    output.WriteLine($"  {side}: " + string.Join(", ", list.Select(e => $"{e.ClassName}#{e.HammerId}")));
            foreach (var line in EntityLumpComparison.Diff(withoutBrush, oursSide))
            {
                var gap = Gaps.FirstOrDefault(g => g.Pattern.IsMatch(line));
                // A template's copies are numbered by their place in its lump, so a brush member
                // the decompile cannot ship yet moves every later member's index.
                if (gap.Reason is null && withoutBrush.Count != truthSide.Count && line.Contains("_template_lump_ent_index", StringComparison.Ordinal))
                    gap = (null!, "template member index moved by a brush member that does not ship");
                // Keys a lit compile's light step writes in place: the precomputed keys the source holds
                // from an earlier placement, traced again (LightPrecompute), and directlight, 3 made 2 on
                // a stationary light with no shadow (BakedShadowAssignment). The package holds the step's
                // values, the truth side here (no light step) the source's. Explained only when ours is
                // what Valve shipped.
                if (gap.Reason is null && Regex.Match(line, @"^\[(\w+)#([\d:]+)\] (precomputed\w+|directlight|bakedshadowindex|desiredstationary|light_(?:path|map)_uniqueid): value valve .*?, ours (\w+):(.*)$") is { Success: true } pre
                    && shipped.TryGetValue(pre.Groups[2].Value, out var valveEntity)
                    && valveEntity.Values.TryGetValue(pre.Groups[3].Value, out var valveValue)
                    && valveValue.Value == pre.Groups[4].Value + ":" + pre.Groups[5].Value)
                    gap = (null!, "light step keys: ours are the package's, the source's were rewritten by the lit compile");
                if (gap.Reason is null)
                    unexplained.Add($"{Path.GetFileName(path)}: {line}");
                else
                    explained[gap.Reason] = explained.GetValueOrDefault(gap.Reason) + 1;
            }
        }
        foreach (var (reason, count) in explained)
            output.WriteLine($"  gap x{count}: {reason}");
        // DECOMPILE_ALL=1: every unexplained line, not the first 40.
        foreach (var line in unexplained.Take(Environment.GetEnvironmentVariable("DECOMPILE_ALL") is { Length: > 0 } ? int.MaxValue : 40))
            output.WriteLine("  UNEXPLAINED " + line);
        Assert.True(unexplained.Count == 0, $"{unexplained.Count} unexplained difference(s); first: {unexplained.FirstOrDefault()}");
    }

    /// <summary>
    /// <c>DECOMPILE_CABLES=&lt;addon&gt;|&lt;map&gt;|&lt;vpk&gt;</c>: each cable_dynamic of the true source, our
    /// render tube (CableCollision.RenderTube, the source's settings) against the render mesh of the
    /// model Valve's package ships for it: vertex counts, and how many positions are bit for bit ours
    /// in order (a decompile can solve a cable's settings only if they are).
    /// </summary>
    [Fact]
    public void CableRenderMeshes_AgainstTheTube()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_CABLES") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var schema = MapFixtures.GameSchema();
        var document = DmxBinary.ReadFile(source);
        var files = MapDecompiler.ReadPackage(p[2]);
        var lump = EntityLumpComparison.Read(files.First(f => f.Key.EndsWith("/entities/default_ents.vents_c", StringComparison.OrdinalIgnoreCase)).Value);
        foreach (var element in document.OfType("CMapCable"))
        {
            var cable = MapEntities.ReadNode(element)!;
            var shippedEntity = lump.FirstOrDefault(e => e.HammerId == cable.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var modelPath = shippedEntity?.Values.GetValueOrDefault("model").Value is { } m ? m[(m.IndexOf(':') + 1)..] + "_c" : null;
            if (modelPath is null || !files.TryGetValue(modelPath, out var bytes))
            {
                output.WriteLine($"{cable.NodeId} {cable.ClassName}: no model ({modelPath})");
                continue;
            }
            var (controls, scales) = EntityLumpAuthor.PathControls(cable, schema);
            var (tube, tubeIndices) = Physics.CableCollision.RenderTube(controls, scales, Physics.CableCollision.SettingsOf(element));
            // The forward's physics mesh on the source's own settings against the PHYS mesh shipped.
            using (var phys = new ValveResourceFormat.Resource())
            {
                phys.Read(new MemoryStream(bytes));
                var shipped = CableSettings.PhysicsVertices(phys);
                var shape = Physics.EntityPhysicsModels.CableShape(cable, Physics.CableCollision.SettingsOf(element), schema);
                output.WriteLine($"{cable.NodeId}: closed {cable.ClosedLoop} interp {cable.InterpolationType} angles {cable.Angles} error {element.GetValue<float>("physicsSimplificationError")}; " +
                                 $"shipped physics {shipped?.Count.ToString() ?? "none"}, forward {shape?.Points.Length}, same {(shipped is not null && shape is { } sh && Physics.RnMeshBuilder.Create(sh.Indices, sh.Points, null) is { } built && shipped.SetEquals(built.Vertices))}");
            }
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(bytes));
            var model = (ValveResourceFormat.ResourceTypes.Model)resource.DataBlock!;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
            {
                var vb = mesh.VBIB.VertexBuffers[0];
                var position = vb.InputLayoutFields.First(f => f.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase));
                var count = (int)vb.ElementCount;
                var points = Enumerable.Range(0, count).Select(v =>
                {
                    var at = v * (int)vb.ElementSizeInBytes + (int)position.Offset;
                    return new Vector3(BitConverter.ToSingle(vb.Data, at), BitConverter.ToSingle(vb.Data, at + 4), BitConverter.ToSingle(vb.Data, at + 8));
                }).ToArray();
                var inOrder = Enumerable.Range(0, Math.Min(count, tube.Length)).Count(i => points[i] == tube[i]);
                var tubeSet = tube.ToHashSet();
                var inSet = points.Count(tubeSet.Contains);
                // The model's positions lie on a 1/2048 grid: which snap of ours gives them.
                static Vector3 Snap(Vector3 v, Func<double, double> f) => new((float)(f(v.X * 2048.0) / 2048.0), (float)(f(v.Y * 2048.0) / 2048.0), (float)(f(v.Z * 2048.0) / 2048.0));
                var snaps = new (string Name, Func<double, double> F)[] { ("round", x => Math.Round(x, MidpointRounding.ToEven)), ("floor", Math.Floor), ("trunc", Math.Truncate) };
                var snapCounts = string.Join(", ", snaps.Select(s =>
                {
                    var set = tube.Select(t => Snap(t, s.F)).ToHashSet();
                    return $"{s.Name} {points.Count(set.Contains)}";
                }));
                var onGrid = points.Count(q => Snap(q, x => Math.Round(x)) == q);
                output.WriteLine($"   on 1/2048 grid {onGrid}/{count}; snapped tube in the model's set: {snapCounts}");
                static (Vector3, Vector3) Box(IEnumerable<Vector3> v) => (v.Aggregate(Vector3.Min), v.Aggregate(Vector3.Max));
                var nearest = points.Select(q => tube.MinBy(t => Vector3.DistanceSquared(t, q))).ToArray();
                var offsets = points.Zip(nearest, (q, t) => q - t).ToArray();
                output.WriteLine($"   model box {Box(points)}, tube box {Box(tube)}; nearest offsets mean {offsets.Aggregate(Vector3.Add) / count:R}, " +
                                 $"max |d| {offsets.Max(o => o.Length()):R}; distinct nearest {nearest.Distinct().Count()}");
                var ib = mesh.VBIB.IndexBuffers[0];
                output.WriteLine($"{cable.NodeId} {cable.ClassName} sides {element.GetValue<int>("numSides")} r {element.GetValue<float>("radius")} spacing {element.GetValue<float>("tessellationSpacing")} " +
                                 $"flip {element.GetValue<bool>("flipFaces")} mat {element.Get<string>("materialName")}: model {count} vertices ({position.Format}), {ib.ElementCount} indices; tube {tube.Length} / {tubeIndices.Length}; " +
                                 $"{inOrder} in order, {inSet} in the tube's set; first {points[0]:R} vs {tube.FirstOrDefault():R}");
            }
        }
    }

    /// <summary>
    /// <c>DECOMPILE_WORLD=&lt;vpk&gt;</c>: what a package keeps of the world's meshes: each world node's DATA
    /// tree, each node model's draw calls (material, vertex and index counts, the vertex layout), and the
    /// world physics' parts and shapes (hulls, meshes and their materials and collision attributes).
    /// </summary>
    [Fact]
    public void WorldGeometrySurvey()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_WORLD") is not { Length: > 0 } vpk)
            return;
        var files = MapDecompiler.ReadPackage(vpk);
        foreach (var (path, bytes) in files.Where(f => f.Key.Contains("/worldnodes/", StringComparison.Ordinal)).OrderBy(f => f.Key))
        {
            using var res = ResourceTrees.Read(bytes, path);
            if (path.EndsWith(".vwnod_c", StringComparison.Ordinal))
            {
                output.WriteLine($"== {path}");
                foreach (var line in ResourceTrees.Render(res).Split('\n').Take(400))
                    output.WriteLine("  " + line.TrimEnd());
            }
            else if (path.EndsWith(".vmdl_c", StringComparison.Ordinal) && res.DataBlock is ValveResourceFormat.ResourceTypes.Model model)
            {
                output.WriteLine($"== {path}");
                foreach (var (mesh, name, _, _) in model.GetEmbeddedMeshesAndLoD())
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                        foreach (var call in so.GetArray("m_drawCalls"))
                        {
                            var vbIndex = call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer");
                            var vb = mesh.VBIB.VertexBuffers[vbIndex];
                            output.WriteLine($"  mesh {name} call {call.GetStringProperty("m_material")}: {call.GetInt32Property("m_nVertexCount")} vertices, " +
                                             $"{call.GetInt32Property("m_nIndexCount")} indices, base {call.GetInt32Property("m_nBaseVertex")}; layout " +
                                             string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}")));
                        }
            }
        }
        if (files.TryGetValue(files.Keys.First(k => k.EndsWith("/world_physics.vmdl_c", StringComparison.Ordinal)), out var phys))
        {
            var tree = WorldPhysicsAuthorTests.Trees(phys)["PHYS"];
            output.WriteLine("== world_physics PHYS");
            var parts = tree["m_parts"]!;
            for (var i = 0; i < parts.Count; i++)
            {
                var shape = parts[i]!["m_rnShape"]!;
                foreach (var kind in new[] { "m_spheres", "m_capsules", "m_hulls", "m_meshes" })
                {
                    var list = shape[kind];
                    output.WriteLine($"  part {i} {kind}: {list?.Count ?? 0}");
                    for (var k = 0; k < Math.Min(list?.Count ?? 0, 12); k++)
                    {
                        var s = list![k]!;
                        var body = s["m_Mesh"] ?? s["m_Hull"];
                        var counts = body is null ? "" : $" vertices {body["m_Vertices"]?.AsBlob().Length / 12 ?? body["m_VertexPositions"]?.AsBlob().Length / 12}, " +
                                                          $"triangles {body["m_Triangles"]?.AsBlob().Length / 12}";
                        output.WriteLine($"    {kind}[{k}]: attr {s["m_nCollisionAttributeIndex"]}, surface {s["m_nSurfacePropertyIndex"]}, tool {s["m_nToolMaterialHash"]}, user {s["m_UserFriendlyName"]}{counts}");
                    }
                }
            }
            output.WriteLine($"  collision attributes {tree["m_collisionAttributes"]?.Count}, surfaces {tree["m_surfacePropertyHashes"]?.Count}");
        }
    }

    /// <summary><c>DECOMPILE_INPUTS=&lt;vpk&gt;</c>: the input dependencies its .vmap_c records (MapDecompiler.MapInputs).</summary>
    [Fact]
    public void MapInputsOfAPackage()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_INPUTS") is not { Length: > 0 } vpk)
            return;
        foreach (var (file, crc) in MapDecompiler.MapInputs(MapDecompiler.ReadPackage(vpk), Path.GetFileNameWithoutExtension(vpk)))
            output.WriteLine($"    {crc:x8} {file}");
    }

    /// <summary>
    /// <c>DECOMPILE_PKGDIFF=&lt;a.vpk&gt;|&lt;b.vpk&gt;</c>: two builds of one probe under different map names (the
    /// map names taken out of paths): files on one side only, files that differ, each .vmap_c's inputs and
    /// the lumps' entity differences (a runtime prefab's probe against its base, DECOMPILE.md item 11).
    /// </summary>
    [Fact]
    public void PackagesOfTwoBuilds()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_PKGDIFF") is not { Length: > 0 } spec || spec.Split('|') is not [var pa, var pb])
            return;
        var (na, nb) = (Path.GetFileNameWithoutExtension(pa), Path.GetFileNameWithoutExtension(pb));
        var (fa, fb) = (MapDecompiler.ReadPackage(pa), MapDecompiler.ReadPackage(pb));
        string Key(string path, string name) => path.Replace(name, "<map>", StringComparison.OrdinalIgnoreCase);
        var (ka, kb) = (fa.ToDictionary(f => Key(f.Key, na), f => f.Value), fb.ToDictionary(f => Key(f.Key, nb), f => f.Value));
        foreach (var k in ka.Keys.Except(kb.Keys).Order())
            output.WriteLine($"  only in a: {k} ({ka[k].Length} bytes)");
        foreach (var k in kb.Keys.Except(ka.Keys).Order())
            output.WriteLine($"  only in b: {k} ({kb[k].Length} bytes)");
        foreach (var k in ka.Keys.Intersect(kb.Keys).Order().Where(k => !ka[k].AsSpan().SequenceEqual(kb[k])))
            output.WriteLine($"  differs: {k} ({ka[k].Length} vs {kb[k].Length} bytes)");
        foreach (var (side, files, name) in new[] { ("a", fa, na), ("b", fb, nb) })
        {
            foreach (var (file, crc) in MapDecompiler.MapInputs(files, name))
                output.WriteLine($"  input {side}: {crc:x8} {file}");
            // The .vmap_c's external references and edit info, as trees.
            using var r = new ValveResourceFormat.Resource();
            r.Read(new MemoryStream(files[$"maps/{name}.vmap_c"]));
            foreach (var x in r.ExternalReferences?.ResourceRefInfoList ?? [])
                output.WriteLine($"  rerl {side}: {x.Name}");
            if (r.EditInfo is ValveResourceFormat.Blocks.ResourceEditInfo2 e2 && e2.Data?.Root is { } red2)
                foreach (var (k, v) in red2.Children)
                    output.WriteLine($"  red2 {side}: {k} = {Regex.Replace(v.ToString() ?? "", @"\s+", " ")[..Math.Min(300, Regex.Replace(v.ToString() ?? "", @"\s+", " ").Length)]}");
            else if (r.EditInfo is ValveResourceFormat.Blocks.ResourceEditInfo e1)
                foreach (var c in e1.ChildResourceList)
                    output.WriteLine($"  child {side}: {c}");
        }
        static byte[]? Lump(Dictionary<string, byte[]> files) => files.FirstOrDefault(f => f.Key.EndsWith("/entities/default_ents.vents_c", StringComparison.OrdinalIgnoreCase)).Value;
        if (Lump(fa) is { } la && Lump(fb) is { } lb)
        {
            IReadOnlyList<EntityLumpComparison.Entity> Plain(byte[] lump, string name) => [.. EntityLumpComparison.Read(lump).Select(e => e with
            {
                Values = e.Values.ToDictionary(kv => kv.Key, kv => (kv.Value.Type, kv.Value.Value.Replace(name, "<map>", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase),
            })];
            var (ea, eb) = (Plain(la, na), Plain(lb, nb));
            output.WriteLine($"  lump: a {ea.Count} entities, b {eb.Count}");
            foreach (var line in EntityLumpComparison.Diff(ea, eb).Take(60))
                output.WriteLine("    " + line[..Math.Min(240, line.Length)]);
            foreach (var e in eb.Where(e => e.ClassName.Contains("prefab", StringComparison.OrdinalIgnoreCase) || e.ClassName.Contains("spawngroup", StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"    b {e.ClassName} #{e.HammerId}: " + string.Join(" ", e.Values.Select(kv => $"{kv.Key}={kv.Value.Value}")));
        }
    }

    /// <summary>
    /// <c>DECOMPILE_CORNERS=&lt;addon&gt;|&lt;map&gt;|&lt;entity node id&gt;</c>: each corner of the true source's
    /// meshes under that entity, placed by the compile's two moves, against what CornerFor finds for
    /// the placed point: whether it lands, and how many floats the source's world point lies from the
    /// inverse estimate.
    /// </summary>
    [Fact]
    public void CornerSearch_AgainstTheSource()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_CORNERS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var document = DmxBinary.ReadFile(MapFixtures.VmapSource(p[0], p[1])!);
        var entity = document.Elements.First(e => e.Type == "CMapEntity" && e.GetValue<int>("nodeID") == int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
        var toEntity = Maps.CTransform.FromNode(entity).Inverse().Matrix();
        var fromEntity = Maps.CTransform.FromNode(entity).Matrix();
        static long Key(float f) { var i = BitConverter.SingleToInt32Bits(f); return i < 0 ? int.MinValue - (long)i : i; }
        foreach (var mesh in entity.GetElements("children").Where(c => c.Type == "CMapMesh"))
        {
            var toWorld = Maps.CTransform.FromNode(mesh).Matrix();
            var origin = mesh.GetValue<Vector3>("origin") ?? Vector3.Zero;
            var positions = mesh.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("vertexData")!.GetElements("streams")
                .First(s => s.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data")!.Cast<Vector3>().ToArray();
            int lands = 0, misses = 0;
            foreach (var c in positions)
            {
                var world = Maps.MapMeshes.Transform(toWorld, c);
                var target = Maps.MapMeshes.Transform(toEntity, world);
                var found = MapDecompiler.CornerFor(target, origin, toWorld, toEntity, fromEntity);
                if (Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, found)) == target)
                {
                    lands++;
                    continue;
                }
                misses++;
                var estimate = Maps.MapMeshes.Transform(fromEntity, target);
                output.WriteLine($"  miss: corner {c:R} world {world:R} target {target:R}; estimate {estimate:R} is " +
                                 $"({Key(world.X) - Key(estimate.X)}, {Key(world.Y) - Key(estimate.Y)}, {Key(world.Z) - Key(estimate.Z)}) floats from the world point");
            }
            output.WriteLine($"mesh {mesh.GetValue<int>("nodeID")}: {lands} corners found, {misses} missed");
            // The shipped hull's vertices against the placed corners (DECOMPILE_CORNERS's 4th field: the package).
            if (p.Length > 3)
            {
                var files = MapDecompiler.ReadPackage(p[3]);
                var model = files.First(f => f.Key.Contains("/entities/", StringComparison.Ordinal) && f.Key.EndsWith("_" + p[2] + ".vmdl_c", StringComparison.Ordinal));
                var hull = BrushGeometry.Read(model.Value, new Dictionary<uint, string>()).Pieces.SelectMany(x => x.Vertices).ToArray();
                var placed = positions.Select(c => Maps.MapMeshes.Transform(toEntity, Maps.MapMeshes.Transform(toWorld, c))).ToArray();
                foreach (var h in hull)
                {
                    var near = placed.MinBy(q => Vector3.DistanceSquared(q, h));
                    output.WriteLine($"  hull {h:R}: {(placed.Contains(h) ? "a placed corner" : $"nearest placed {near:R}, off {(h - near):R}")}");
                }
            }
        }
    }

    /// <summary>
    /// <c>DECOMPILE_IDS=&lt;vmap&gt;|&lt;map&gt;[|settle]</c>: our lump author on a .vmap under that map name,
    /// as this file's checks run it: every default_ents entity's id and class, lump order.
    /// </summary>
    [Fact]
    public void LumpIdsOfAFile()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_IDS") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var (lumps, settled) = Lumps(DmxBinary.ReadFile(p[0]), p[1], MapFixtures.GameSchema(), p[0], settle: p.Length > 2);
        output.WriteLine($"settled {settled}");
        var ents = lumps.First(l => l.Key.EndsWith("default_ents.vents_c", StringComparison.OrdinalIgnoreCase));
        foreach (var e in EntityLumpComparison.Read(ents.Value, ents.Key))
            output.WriteLine($"    {e.HammerId,-12} {e.ClassName} @ {e.Values.GetValueOrDefault("origin").Value}");
    }

    /// <summary>
    /// <c>DECOMPILE_RC=&lt;valve.vpk&gt;|&lt;rc.vpk&gt;</c>: Valve's default_ents from a map's own
    /// package (or resourcecompiler's compile of a renamed copy of its source, same options)
    /// against resourcecompiler's compile of our decompile of it, every difference counted by key.
    /// Reports, does not assert: the two builds may differ in options.
    /// </summary>
    [Fact]
    public void ResourceCompilerOfTheDecompile_AgainstValve()
    {
        if (Environment.GetEnvironmentVariable("DECOMPILE_RC") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        static byte[] DefaultEnts(string vpk) => MapDecompiler.ReadPackage(vpk)
            .First(f => f.Key.EndsWith("/entities/default_ents.vents_c", StringComparison.OrdinalIgnoreCase)).Value;
        // One package: its entities' ids, classes and targetnames, lump order (a probe's raw answer).
        if (p.Length == 1)
        {
            foreach (var e in EntityLumpComparison.Read(DefaultEnts(p[0])))
                output.WriteLine($"    {e.HammerId,-12} {e.ClassName} {e.Values.GetValueOrDefault("targetname").Value} walk {e.Values.GetValueOrDefault("compile_source_id").Value} @ {e.Values.GetValueOrDefault("origin").Value}");
            return;
        }
        // Both builds may be renamed copies of one map (decompile_rc_batch.ps1: s2c_src_<map> and
        // s2c_dec_<map>): the prefix is taken out of every value (model paths, worldname), and a
        // handshake is its map path's hash (Lighting: seed 0x3501a674) plus an index, so the
        // index is compared.
        static IReadOnlyList<EntityLumpComparison.Entity> Unprefixed(IReadOnlyList<EntityLumpComparison.Entity> lump, string vpk)
        {
            var bytes = Encoding.UTF8.GetBytes(@"maps\" + Path.GetFileNameWithoutExtension(vpk));
            var seed = (long)(Io.ResourceNames.Hash(bytes, bytes.Length, 0x3501a674) & 0x7fffffff);
            if (seed + 0xff > 0x7fffffff)
                seed -= 0xff;
            // Values render as "<type>:<value>" (EntityLumpComparison.Render).
            string Value(string key, string value) =>
                key.Equals("handshake", StringComparison.OrdinalIgnoreCase) && long.TryParse(value[(value.IndexOf(':') + 1)..], out var h) && h != 0
                    ? $"{value[..value.IndexOf(':')]}:map hash + {h - seed}"
                    // Hammer's re-save of a decompile is s2c_rs_<decompile>_saved (hammer_autosave.py).
                    : Regex.Replace(Regex.Replace(value, "s2c_(src|dec)_", ""), @"s2c_rs_(\w+?)_saved", "$1");
            return lump.Select(e => e with
            {
                Values = e.Values.ToDictionary(kv => kv.Key, kv => (kv.Value.Type, Value(kv.Key, kv.Value.Value)),
                    StringComparer.OrdinalIgnoreCase),
            }).ToList();
        }
        var valve = Unprefixed(EntityLumpComparison.Read(DefaultEnts(p[0])), p[0]);
        var rc = Unprefixed(EntityLumpComparison.Read(DefaultEnts(p[1])), p[1]);
        var report = EntityLumpComparison.Diff(valve, rc).ToList();
        // A third package, the map's own build: keys a lit compile's light step rewrites (as in
        // Entities_CompileAsTheSourceDoes) are explained where the decompile's build holds what that
        // package shipped, since the source keeps the values of an earlier placement.
        if (p.Length > 2)
        {
            var shipped = EntityLumpComparison.Read(DefaultEnts(p[2])).GroupBy(e => e.HammerId).ToDictionary(g => g.Key, g => g.First());
            var light = report.RemoveAll(line =>
                Regex.Match(line, @"^\[(\w+)#([\d:]+)\] (precomputed\w+|directlight|bakedshadowindex|desiredstationary|light_(?:path|map)_uniqueid): value valve .*?, ours (\w+):(.*)$") is { Success: true } m
                && shipped.TryGetValue(m.Groups[2].Value, out var e) && e.Values.TryGetValue(m.Groups[3].Value, out var v)
                && v.Value == m.Groups[4].Value + ":" + m.Groups[5].Value);
            output.WriteLine($"{light} light step keys as the map's own build shipped them");
        }
        output.WriteLine($"valve {valve.Count} entities, rc of the decompile {rc.Count}; {report.Count} difference(s)");
        foreach (var g in report.GroupBy(l => Regex.Replace(l, @"^\[[^\]]+\] ", "").Split(':')[0]).OrderByDescending(g => g.Count()).Take(40))
            output.WriteLine($"  {g.Count(),5} x {g.Key}   e.g. {g.First()[..Math.Min(160, g.First().Length)]}");
        if (Environment.GetEnvironmentVariable("DECOMPILE_ALL") == "1")
            foreach (var line in report)
                output.WriteLine("    " + line);
        // DECOMPILE_RC_IDS=1: entities the two sides ship under different ids, paired by class,
        // targetname and origin, Valve's id order (a copy block of another width shows as a step).
        if (Environment.GetEnvironmentVariable("DECOMPILE_RC_IDS") == "1")
        {
            static string Where(EntityLumpComparison.Entity e) => string.Join("|", e.ClassName,
                e.Values.GetValueOrDefault("targetname").Value, e.Values.GetValueOrDefault("origin").Value);
            var ours = rc.GroupBy(Where).ToDictionary(g => g.Key, g => new Queue<EntityLumpComparison.Entity>(g));
            foreach (var e in valve.OrderBy(e => int.TryParse(e.HammerId, out var n) ? n : int.MaxValue))
                if (ours.TryGetValue(Where(e), out var q) && q.Count > 0 && q.Dequeue() is { } o && o.HammerId != e.HammerId)
                    output.WriteLine($"    id {e.HammerId} -> {o.HammerId}  {e.ClassName} {e.Values.GetValueOrDefault("targetname").Value}");
        }

        // The brush entities' models, by file name (the two maps' folders differ): PHYS against PHYS.
        static Dictionary<string, byte[]> Models(string vpk) => MapDecompiler.ReadPackage(vpk)
            .Where(f => f.Key.Contains("/entities/", StringComparison.Ordinal) && f.Key.EndsWith(".vmdl_c", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetFileName(f.Key), f => f.Value);
        var (va, vb) = (Models(p[0]), Models(p[1]));
        int exact = 0, missing = 0;
        var differ = new List<string>();
        foreach (var (name, bytes) in va)
        {
            if (!vb.TryGetValue(name, out var other)) { missing++; continue; }
            var (ta, tb) = (WorldPhysicsAuthorTests.Trees(bytes).GetValueOrDefault("PHYS"), WorldPhysicsAuthorTests.Trees(other).GetValueOrDefault("PHYS"));
            var d = ta is null || tb is null ? (ta is null && tb is null ? [] : ["PHYS on one side only"]) : KvTreeDiff.Diff(ta, tb);
            if (d.Count == 0) exact++; else differ.Add($"{name}: {d.Count} differences, first {d[0]}");
        }
        output.WriteLine($"entity models: valve {va.Count}, rc of the decompile {vb.Count}; {exact} PHYS equal, {differ.Count} differ, {missing} missing");
        foreach (var line in differ.Take(15))
            output.WriteLine("  " + line[..Math.Min(220, line.Length)]);

        // Their render halves: every vertex and index buffer block byte for byte and the MDAT trees
        // (BrushRender: the decompile's texcoords, normals, tints and faces through Valve's compile).
        static List<(string Type, byte[] Bytes, ValveKeyValue.KVObject? Tree)> RenderBlocks(byte[] file)
        {
            using var r = new ValveResourceFormat.Resource();
            r.Read(new MemoryStream(file));
            return [.. r.Blocks.Where(b => b.Type is ValveResourceFormat.BlockType.MVTX or ValveResourceFormat.BlockType.MIDX or ValveResourceFormat.BlockType.VBIB
                                                or ValveResourceFormat.BlockType.MDAT)
                .Select(b =>
                {
                    r.Reader.BaseStream.Position = b.Offset;
                    var raw = new byte[b.Size];
                    r.Reader.BaseStream.ReadExactly(raw);
                    return (b.Type.ToString(), raw, b switch
                    {
                        ValveResourceFormat.ResourceTypes.BinaryKV3 k => k.Data.Root,
                        ValveResourceFormat.ResourceTypes.KeyValuesOrNTRO k => k.Data,
                        _ => (ValveKeyValue.KVObject?)null,
                    });
                })];
        }
        int renderModels = 0, renderEqual = 0;
        var renderDiffer = new List<string>();
        foreach (var (name, bytes) in va)
        {
            if (!vb.TryGetValue(name, out var other) || RenderBlocks(bytes) is not { Count: > 0 } ra)
                continue;
            renderModels++;
            var rb = RenderBlocks(other);
            var at = ra.Count != rb.Count ? 0 : Enumerable.Range(0, ra.Count).FirstOrDefault(i => ra[i].Type != rb[i].Type || !ra[i].Bytes.AsSpan().SequenceEqual(rb[i].Bytes), -1);
            if (at < 0) { renderEqual++; continue; }
            var tree = ra.Count == rb.Count && ra[at].Tree is { } x && rb[at].Tree is { } y ? KvTreeDiff.Diff(x, y).FirstOrDefault() : null;
            renderDiffer.Add($"{name}: {(ra.Count != rb.Count ? $"{ra.Count} vs {rb.Count} render blocks" : $"{ra[at].Type} {at} differs{(tree is null ? "" : ", " + tree)}")}");
        }
        output.WriteLine($"render models: {renderModels}; {renderEqual} render blocks equal, {renderDiffer.Count} differ");
        foreach (var line in renderDiffer.Take(15))
            output.WriteLine("  " + line[..Math.Min(220, line.Length)]);
    }

    /// <summary>
    /// Every compiled cs_script of the subscribed workshop addons gives back its source: the
    /// .vjs_c's plain text, at the path and with the CRC its RED2 records (ScriptSources).
    /// 227 of 227 on 10-30.
    /// </summary>
    [Fact]
    public void Scripts_KeepTheirSource()
    {
        if (MapFixtures.WorkshopDir() is not { } root) { MapFixtures.Skip("workshop addons"); return; }
        int seen = 0, lost = 0;
        foreach (var dir in Directory.EnumerateFiles(root, "*_dir.vpk", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var package = new ValvePak.Package();
            package.Read(dir);
            foreach (var entry in package.Entries.TryGetValue("vjs_c", out var scripts) ? scripts : [])
            {
                seen++;
                if (ScriptSources.Recover(Source2.Compiler.Io.VpkEntries.Read(package, entry)) is null)
                {
                    lost++;
                    output.WriteLine($"{dir}: {entry.GetFullPath()} keeps no source matching its RED2 CRC");
                }
            }
        }
        output.WriteLine($"{seen - lost} of {seen} scripts give back their source");
        Assert.Equal(0, lost);
    }

    /// <summary>
    /// The brush entities' collision, through the forward compile: our entity model builder
    /// (Physics.EntityPhysicsModels) run on the decompiled meshes and on the true source, PHYS
    /// block against PHYS block for every model. A model either matches whole, or its hulls are
    /// the same point sets (geometry equal, the bytes not: the builder's output order follows its
    /// input's), or it differs; each counted, the differing ones listed.
    /// </summary>
    [Theory]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "ze_doom_p2_c_gameplay")]
    [InlineData("s2c_rc_probe", "probe01")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe2")]
    public void BrushPhysics_CompilesAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var cs2 = CS2Fixtures.StockPak();
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || cs2 is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, ".."));
        using var content = new Maps.GameContent(cs2, Path.Combine(game, "csgo_addons", addon));

        var result = MapDecompiler.Decompile(MapDecompiler.ReadPackage(package), map, schema, StockMaterials());
        var decompiled = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
        Maps.MapPrefabs.Attach(decompiled, PrefabLoader(result));
        Maps.MapDeformers.Apply(decompiled, schema);
        var truth = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(truth, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        Maps.MapDeformers.Apply(truth, schema);

        Dictionary<string, Physics.EntityPhysicsModels.Model> Models(DmxBinary.Document d)
            => Physics.EntityPhysicsModels.Build(d, map, content.Material, content.CollisionProperty, schema, [], content.SmartProp)
                      .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var expected = Models(truth);
        var ours = Models(decompiled);

        int exact = 0, sameGeometry = 0;
        var differing = new List<string>();
        foreach (var (path, model) in expected.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!ours.TryGetValue(path, out var mine))
            {
                differing.Add($"{path} ({model.ClassName}): no model from the decompile");
                continue;
            }
            var a = WorldPhysicsAuthorTests.Trees(Physics.EntityPhysicsModels.Author(model, content.SurfaceName))["PHYS"];
            var b = WorldPhysicsAuthorTests.Trees(Physics.EntityPhysicsModels.Author(mine, content.SurfaceName))["PHYS"];
            var diff = KvTreeDiff.Diff(a, b);
            if (diff.Count == 0)
            {
                exact++;
                continue;
            }
            var (ha, hb) = (HullPointSets(model), HullPointSets(mine));
            if (ha.Count == hb.Count && ha.Zip(hb).All(p => p.First.SetEquals(p.Second))
                && model.Physics.AllShapes.Select(x => (x.Attribute, x.ToolMaterialHash)).SequenceEqual(mine.Physics.AllShapes.Select(x => (x.Attribute, x.ToolMaterialHash))))
            {
                sameGeometry++;
                if (sameGeometry <= 3)
                    output.WriteLine($"  same point sets: {path} ({model.ClassName}): " + string.Join(" | ", diff.Take(12)));
            }
            else
                differing.Add($"{path} ({model.ClassName}): hulls {ha.Count} vs {hb.Count}; {diff.Count} differences, first {diff[0]}");
            // DECOMPILE_ORDER=<model path part>: both sides' shapes in order, material hash and centroid.
            if (Environment.GetEnvironmentVariable("DECOMPILE_ORDER") is { Length: > 0 } part && path.Contains(part, StringComparison.Ordinal))
                foreach (var (side, m) in new[] { ("truth", model), ("ours", mine) })
                    output.WriteLine($"  {side}: " + string.Join(" ", m.Physics.AllShapes.Select(x => $"{x.ToolMaterialHash}@{x.Hull?.Centroid}")));
        }
        foreach (var path in ours.Keys.Where(k => !expected.ContainsKey(k)))
            differing.Add($"{path}: only from the decompile");
        output.WriteLine($"{map}: {expected.Count} models; {exact} PHYS exact, {sameGeometry} same hull point sets, {differing.Count} differ");
        foreach (var group in result.Notes.Where(n => !n.Contains("walk positions")).GroupBy(n => Regex.Replace(n, @"^\S+ \S+: |\([^)]*\)|maps/\S+", "").Trim()))
            output.WriteLine($"  note x{group.Count()}: {group.Key}");
        foreach (var line in differing.Take(25))
            output.WriteLine("  DIFFERS " + line);
        // Same point sets with other bytes: the hull's vertex and face order, which follows the
        // order the compile collects a piece's points in (BrushHulls.GroupVertices' hash slots over
        // the triangulated faces' first-meet numbering), is not inverted yet. Those count as a gap;
        // a model that differs otherwise must be one of these.
        var unexplained = differing.Where(d => !KnownHullMisses.Any(k => d.StartsWith(k, StringComparison.Ordinal))).ToList();
        Assert.True(unexplained.Count == 0, $"{unexplained.Count} model(s) differ: {unexplained.FirstOrDefault()}");
    }

    /// <summary>
    /// The brush entities' render meshes, through the forward compile: our entity model builder with
    /// render meshes (EntityRenderModels.Author: buffers, MDAT, PHYS, CTRL, RERL, RED2, DATA) run on the
    /// decompile and on the true source, file against file for every model that draws. Our forward on
    /// the source equals Valve's file on all four maps (EntityRenderProbe.EveryEntityModel: 119 of 119),
    /// so a match here is Valve's bytes. A model either matches whole or is listed with the first block
    /// that differs.
    /// </summary>
    [Theory]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "atixref")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe2")]
    public void BrushRender_CompilesAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var cs2 = CS2Fixtures.StockPak();
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || cs2 is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, ".."));
        using var content = new Maps.GameContent(cs2, Path.Combine(game, "csgo_addons", addon));

        var result = MapDecompiler.Decompile(MapDecompiler.ReadPackage(package), map, schema, StockMaterials());
        var decompiled = Maps.MapNodeAngles.Apply(DmxBinary.Read(result.Vmap));
        Maps.MapPrefabs.Attach(decompiled, PrefabLoader(result));
        Maps.MapDeformers.Apply(decompiled, schema);
        var truth = DmxBinary.ReadFile(source);
        Maps.MapPrefabs.Attach(truth, Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        Maps.MapDeformers.Apply(truth, schema);

        Dictionary<string, Physics.EntityPhysicsModels.Model> Models(DmxBinary.Document d)
            => Physics.EntityPhysicsModels.Build(d, map, content.Material, content.CollisionProperty, schema, [], content.SmartProp)
                      .Where(m => !m.PhysicsOnly)
                      .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var expected = Models(truth);
        var ours = Models(decompiled);
        static List<(string Type, byte[] Bytes)> Blocks(byte[] file)
        {
            using var r = new ValveResourceFormat.Resource();
            r.Read(new MemoryStream(file));
            return [.. r.Blocks.Select(b =>
            {
                r.Reader.BaseStream.Position = b.Offset;
                var bytes = new byte[b.Size];
                r.Reader.BaseStream.ReadExactly(bytes);
                return (b.Type.ToString(), bytes);
            })];
        }
        int exact = 0;
        var differing = new List<string>();
        var firstBlock = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, model) in expected.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!ours.TryGetValue(path, out var mine))
            {
                differing.Add($"{path} ({model.ClassName}): no render model from the decompile");
                firstBlock["missing"] = firstBlock.GetValueOrDefault("missing") + 1;
                continue;
            }
            var a = Maps.EntityRenderModels.Author(model, content);
            var b = Maps.EntityRenderModels.Author(mine, content);
            if (a.AsSpan().SequenceEqual(b))
            {
                exact++;
                continue;
            }
            var (ba, bb) = (Blocks(a), Blocks(b));
            var at = Enumerable.Range(0, Math.Min(ba.Count, bb.Count)).FirstOrDefault(i => ba[i].Type != bb[i].Type || !ba[i].Bytes.AsSpan().SequenceEqual(bb[i].Bytes), -1);
            var what = ba.Count != bb.Count ? $"blocks {ba.Count} vs {bb.Count}" : at < 0 ? "same blocks, other layout" : $"block {at} {ba[at].Type} ({ba[at].Bytes.Length} vs {bb[at].Bytes.Length} bytes)";
            var kind = ba.Count != bb.Count ? "block count" : at < 0 ? "layout" : ba[at].Type;
            firstBlock[kind] = firstBlock.GetValueOrDefault(kind) + 1;
            // A KV3 block's first differences as trees.
            static ValveKeyValue.KVObject? Tree(byte[] file, int index)
            {
                using var r = new ValveResourceFormat.Resource();
                r.Read(new MemoryStream(file));
                return r.Blocks[index] switch
                {
                    ValveResourceFormat.ResourceTypes.BinaryKV3 k => k.Data.Root,
                    ValveResourceFormat.ResourceTypes.KeyValuesOrNTRO k => k.Data,
                    ValveResourceFormat.Blocks.ResourceEditInfo2 e => e.Data?.Root,
                    _ => null,
                };
            }
            if (at >= 0 && Tree(a, at) is { } ta && Tree(b, at) is { } tb)
                what += ": " + string.Join(" | ", KvTreeDiff.Diff(ta, tb).Take(3));
            // A vertex buffer's differences by field, vertex by vertex in order.
            if (kind is "MVTX" or "VBIB")
            {
                static List<ValveResourceFormat.Blocks.VBIB.OnDiskBufferData> Buffers(byte[] file)
                {
                    using var r = new ValveResourceFormat.Resource();
                    r.Read(new MemoryStream(file));
                    return [.. ((ValveResourceFormat.ResourceTypes.Model)r.DataBlock!).GetEmbeddedMeshesAndLoD().Select(m => m.Mesh.VBIB.VertexBuffers[0])];
                }
                var (va, vb) = (Buffers(a), Buffers(b));
                for (var i = 0; i < Math.Min(va.Count, vb.Count); i++)
                {
                    var (da, db, st) = (va[i].Data, vb[i].Data, (int)va[i].ElementSizeInBytes);
                    if (da.AsSpan().SequenceEqual(db))
                        continue;
                    var fields = va[i].InputLayoutFields;
                    var byField = Enumerable.Range(0, Math.Min(da.Length, db.Length)).Where(x => da[x] != db[x])
                        .GroupBy(x => { var f = fields.Last(f => f.Offset <= x % st); return f.SemanticName + f.SemanticIndex; })
                        .Select(g => $"{g.Key} x{g.Select(x => x / st).Distinct().Count()}");
                    var first = Enumerable.Range(0, Math.Min(da.Length, db.Length)).FirstOrDefault(x => da[x] != db[x], -1);
                    what += $"; mesh {i}: {va[i].ElementCount} vs {vb[i].ElementCount} vertices, {string.Join(" ", byField)}"
                            + (first >= 0 ? $", vertex {first / st}: {Convert.ToHexString(da, first / st * st, st)} vs {Convert.ToHexString(db, first / st * st, st)}" : "");
                    break;
                }
            }
            differing.Add($"{path} ({model.ClassName}): {what}");
        }
        output.WriteLine($"{map}: {expected.Count} render models; {exact} exact, {differing.Count} differ");
        foreach (var (k, v) in firstBlock)
            output.WriteLine($"  first differing block {k}: {v}");
        foreach (var group in result.Notes.Where(n => n.Contains("render", StringComparison.Ordinal)).GroupBy(n => Regex.Replace(n, @"^\S+ \S+: |\([^)]*\)|maps/\S+|\d+", "#").Trim()))
            output.WriteLine($"  note x{group.Count()}: {group.First()}");
        foreach (var line in differing.Take(25))
            output.WriteLine("  DIFFERS " + line);
        var unexplained = differing.Where(d => !KnownRenderMisses.Any(k => d.StartsWith(k, StringComparison.Ordinal))).ToList();
        Assert.True(unexplained.Count == 0, $"{unexplained.Count} render model(s) differ: {unexplained.FirstOrDefault()}");
    }

    /// <summary>Render models the decompile does not give back yet, each with its reason (docs/DECOMPILE.md,
    /// "Brush entity render meshes").</summary>
    private static readonly string[] KnownRenderMisses =
    [
        // Its box hull comes back with the shipped points in another order, and no order of its faces
        // searched draws as shipped: the decompiler's draw filter fails on the true source's mesh too
        // (EntityRenderProbe.RenderFilterOnTheSource, ENTFILTER=s2c_rc_probe|atixref|123), so the
        // filter misses a step of the forward for this mesh.
        "maps/atixref/entities/valdina_button_1_123.vmdl",
        // Inside a prefab: the forward moves the mesh and the entity by the prefab's placement, which
        // the decompile's one-mesh chain does not (its physics is a known miss too).
        "maps/s2c_prefabprobe2/entities/combo_moveparent_108_115.vmdl",
    ];

    /// <summary>
    /// The nav settings (Decompile.NavSettings): the generation parameters our forward hands navsystem
    /// (NavGenPipeline.Settings: gameinfo's NavSystem, the hull vdata, the map's CMapNavData) from the
    /// decompile, as the block NavWriter writes, against the block in the shipped .nav and against our
    /// forward on the true source. A map whose nav is the fixed empty one gets no CMapNavData.
    /// </summary>
    [Theory]
    [InlineData("s2c_rc_probe", "probe01")]
    [InlineData("s2c_rc_probe", "probe_classes")]
    [InlineData("s2c_rc_probe", "atixref")]
    [InlineData("s2c_rc_probe", "ze_hold_em_p")]
    [InlineData("s2c_rc_probe", "s2c_prefabprobe9")]
    [InlineData("s2c_rc_probe", "s2c_varprobe")]
    // probe01 with its own settings and hull (s2c_navprobe) and with a hull preset named (s2c_navprobe2):
    // tools/map-survey/nav_settings_probe_map.py, full builds.
    [InlineData("s2c_rc_probe", "s2c_navprobe")]
    [InlineData("s2c_rc_probe", "s2c_navprobe2")]
    public void NavSettings_CompileAsTheSourceDoes(string addon, string map)
    {
        var source = MapFixtures.VmapSource(addon, map);
        var cs2 = CS2Fixtures.StockPak();
        var package = source is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "..",
                                                                            "game", "csgo_addons", addon, "maps", map + ".vpk"));
        if (source is null || cs2 is null || !File.Exists(package)) { MapFixtures.Skip($"the {addon}/{map} source and package"); return; }
        var schema = MapFixtures.GameSchema();
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cs2)!, ".."));
        using var content = new Maps.GameContent(cs2, Path.Combine(game, "csgo_addons", addon));
        var files = MapDecompiler.ReadPackage(package);
        if (!files.TryGetValue($"maps/{map}.nav", out var nav)) { output.WriteLine($"{map}: no .nav in the package"); return; }
        var result = MapDecompiler.Decompile(files, map, schema, StockMaterials());
        var decompiled = DmxBinary.Read(result.Vmap);
        var truth = DmxBinary.ReadFile(source);
        var vdata = Maps.NavGen.NavGenPipeline.HullVData(content.Read);
        var navSystem = Maps.NavGen.NavGenPipeline.NavSystemBlock(Path.Combine(game, "csgo", "gameinfo.gi"));
        var hasNode = decompiled.OfType("CMapNavData").Any();
        output.WriteLine($"{map}: nav {nav.Length} bytes{(nav.AsSpan().SequenceEqual(Maps.NavFile.EmptyDefault()) ? " (the fixed empty one)" : "")}; "
                         + $"CMapNavData in the source {truth.OfType("CMapNavData").Any()}, in the decompile {hasNode}");
        foreach (var n in result.Notes.Where(n => n.StartsWith("nav:", StringComparison.Ordinal)))
            output.WriteLine("  note " + n);
        if (Environment.GetEnvironmentVariable("DECOMPILE_NAV_HEX") == "1")
            for (var at = 0; at < Math.Min(nav.Length, 256); at += 32)
                output.WriteLine($"  {at,4}: {Convert.ToHexString(nav, at, Math.Min(32, nav.Length - at))}");
        if (!hasNode)
        {
            Assert.True(nav.AsSpan().SequenceEqual(Maps.NavFile.EmptyDefault()) || result.Notes.Any(n => n.StartsWith("nav:", StringComparison.Ordinal)),
                        "a generated nav with no CMapNavData rebuilt and no note");
            return;
        }
        var (shipped, _, why) = NavSettings.FromNav(nav);
        Assert.True(shipped is not null, why);
        var block = NavSettings.Block(shipped!);
        var ours = NavSettings.Block(Maps.NavGen.NavGenPipeline.Settings(decompiled, vdata, navSystem));
        var forward = NavSettings.Block(Maps.NavGen.NavGenPipeline.Settings(truth, vdata, navSystem));
        output.WriteLine($"  block {block.Length} bytes; decompile's = shipped {ours.AsSpan().SequenceEqual(block)}, source's = shipped {forward.AsSpan().SequenceEqual(block)}");
        if (!forward.AsSpan().SequenceEqual(block))
            output.WriteLine($"  shipped {Convert.ToHexString(block)}\n  source  {Convert.ToHexString(forward)}");
        Assert.Equal(Convert.ToHexString(block), Convert.ToHexString(ours));
    }

    /// <summary>Models with a corner the fit cannot place exactly (MapDecompiler.CornerFor: no grid value
    /// and no point within the search lands on the hull's vertex), each a few float steps off.</summary>
    private static readonly string[] KnownHullMisses =
    [
        "maps/ze_doom_p2_c_gameplay/entities/teleportrandom2_458.vmdl",
        "maps/probe_classes/entities/probe_func_nav_blocker_2333.vmdl",
        "maps/probe_classes/entities/probe_trigger_wind_2863.vmdl",
    ];

    /// <summary>A decompile's prefab maps, as MapPrefabs.Attach loads them (by targetMapPath).</summary>
    private static Func<string, DmxBinary.Document?> PrefabLoader(MapDecompiler.Result result)
        => path => result.Prefabs.TryGetValue(path.Replace('\\', '/'), out var bytes)
            ? Maps.MapNodeAngles.Apply(DmxBinary.Read(bytes)) : null;

    /// <summary>Each hull's vertices as a set, in shape order.</summary>
    private static List<HashSet<System.Numerics.Vector3>> HullPointSets(Physics.EntityPhysicsModels.Model model)
        => [.. model.Physics.AllShapes.Where(s => s.Hull is not null).Select(s => s.Hull!.VertexPositions.ToHashSet())];

    private static IReadOnlyDictionary<uint, string>? _stockMaterials;

    /// <summary>The game's material names by tool material hash (csgo and core pak01).</summary>
    private static IReadOnlyDictionary<uint, string> StockMaterials()
        => _stockMaterials ??= CS2Fixtures.StockPak() is { } csgo
            ? BrushGeometry.GameMaterials(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csgo)!, "..")))
            : new Dictionary<uint, string>();

    /// <summary>What the decompile does not bring back yet, each with its reason.</summary>
    private static readonly (Regex Pattern, string Reason)[] Gaps =
    [
        // EntityLumpSet.WithParticleSnapshot writes the path only when the node snapshot_mesh
        // names is in the map; probe_classes' node 3 is a world mesh.
        (new(@"\] snapshot_file: value valve String/ResourceName:maps/[^ ]+/particle_snapshots/node_\d+\.vsnap, ours String/ResourceName:$"),
         "particle snapshot from a mesh node: world geometry not rebuilt"),
    ];

    /// <summary>The lumps our author makes of a map, as EntityLumpAgainstValveTests builds them:
    /// prefabs attached from the addon's content and deformers applied, the document passed so
    /// instances are collapsed, and settled when <paramref name="settle"/> and the settle takes the
    /// map (whether it did is returned).</summary>
    private static (Dictionary<string, byte[]> Lumps, bool Settled) Lumps(DmxBinary.Document document, string map, FgdSchema schema,
                                                                       string source, bool settle, MapDecompiler.Result? decompiled = null)
    {
        Maps.MapPrefabs.Attach(document, decompiled is null
            ? Maps.MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!)
            : PrefabLoader(decompiled));
        Maps.MapDeformers.Apply(document, schema);
        Dictionary<string, Maps.SettleWorld.Settlement>? settled = null;
        if (settle)
            try { settled = SettleLumpTests.Settle(document, source); }
            catch (NotSupportedException) { }
        // With the smart props' locators, which take node ids before any instance copy
        // (EntityLumpAgainstValveTests passes them too): atixref's radiator moves every copy id by one.
        return (EntityLumpSet.Author(MapEntities.From(document), schema, map, MapEntities.FixupEntityNames(document), document,
                                     MapFixtures.SmartPropLocators, settled)
                             .ToDictionary(l => l.Path, l => l.Bytes, StringComparer.OrdinalIgnoreCase), settled is not null);
    }
}
