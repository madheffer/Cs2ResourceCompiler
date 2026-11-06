using Source2.Compiler.Compiling;
using Source2.Compiler.Maps;
using Source2.Compiler.KeyValues;
using ValveKeyValue;

namespace Source2.Compiler.Models;

/// <summary>Settings for <see cref="ModelDocCompiler"/>.</summary>
/// <param name="CompiledMaterial">A material's compiled bytes (<c>.vmat_c</c>) by its path
/// (<c>materials/x.vmat</c>), from which the draws take the vertex input signature; null or a null
/// answer leaves the layout unfiltered, as RC does for a material it cannot load.</param>
public sealed record ModelDocSettings(Func<string, byte[]?>? CompiledMaterial = null);

/// <summary>
/// A ModelDoc source (<c>.vmdl</c>) to <c>.vmdl_c</c>, as resourcecompiler's CompileModel writes it
/// (docs/MODELS.md). Ported so far: a static model of RenderMeshFile nodes over model DMX files
/// (<see cref="DmxModelMeshes"/>). Any other node, and any option off its default, is refused
/// rather than approximated.
/// </summary>
public sealed class ModelDocCompiler : IResourceCompiler
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".vmdl"];

    /// <inheritdoc/>
    public string OutputPathFor(string sourcePath) => sourcePath + "_c";

    /// <summary>The node classes this compiler reads.</summary>
    private static readonly HashSet<string> Ported = new(["RootNode", "RenderMeshList", "RenderMeshFile"], StringComparer.Ordinal);

    /// <inheritdoc/>
    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
    {
        var content = request.Content ?? throw new InvalidOperationException($"{request.SourcePath} needs a content source for its mesh files.");
        var settings = request.Settings as ModelDocSettings ?? new ModelDocSettings();
        var doc = Kv3Text.Parse(request.Source);
        var root = doc.Root.GetSubCollection("rootNode") ?? throw new InvalidDataException($"{request.SourcePath} has no rootNode.");
        Check(root);

        var inputs = new List<EditInfo.Input> { EditInfo.Input.Read(request.SourcePath, request.SearchPath, request.Source) };
        var meshes = new List<(string Name, IReadOnlyList<DmxModelMeshes.Mesh> Meshes)>();
        foreach (var node in Nodes(root).Where(n => Class(n) == "RenderMeshFile"))
        {
            var file = (Str(node, "filename") ?? "").Replace('\\', '/').TrimStart('/');
            var scale = node.GetFloatProperty("import_scale", 1f);
            var bytes = content.Read(file) ?? throw new FileNotFoundException($"{file} (for {request.SourcePath}) was not found.");
            inputs.Add(EditInfo.Input.Read(file, request.SearchPath, bytes));
            meshes.Add((Str(node, "name") ?? Path.GetFileNameWithoutExtension(file), DmxModelMeshes.Load(DmxBinary.Read(bytes), scale)));
        }
        if (meshes.Count != 1)
            throw new NotSupportedException($"{request.SourcePath}: {meshes.Count} render meshes; only one is ported so far.");

        var facts = new Dictionary<string, WorldNodeModels.MaterialFacts>(StringComparer.OrdinalIgnoreCase);
        WorldNodeModels.MaterialFacts Material(string path)
        {
            if (!facts.TryGetValue(path, out var f))
                facts[path] = f = settings.CompiledMaterial?.Invoke(path) is { } m
                    ? new WorldNodeModels.MaterialFacts(MaterialAuthor.ExtractInputSignature(m), MaterialAuthor.ExtractDepthSignature(m))
                    : new WorldNodeModels.MaterialFacts([], []);
            return f;
        }

        var modelName = request.SourcePath.Replace('\\', '/');
        var (meshName, list) = meshes[0];
        RefuseUnported(list, Material);
        var built = WorldNodeModels.Build(modelName, [.. list.Select(m => m.Entry)], false, Material, mapMesh: false, preserveTangents: false);
        var model = built.Model with { EntityClass = "" };
        var red2 = Red2(model, inputs, root);
        var ctrl = KVObject.Collection();
        var embedded = KVObject.Array();
        embedded.Add(WorldNodeModelTrees.CtrlMesh(model, meshName, 0, 0));
        ctrl.Add("embedded_meshes", embedded);
        var bytesOut = WorldNodeModelAuthor.Container(built.Buffers, WorldNodeModelTrees.Mdat(model), ctrl, built.References, red2, WorldNodeModelTrees.Data(model));
        return new CompileOutput(bytesOut);
    }

    // What the shared mesh compile does not yet do for a .vmdl, refused rather than written differently:
    // - ModelCompile_MeshInputLayouts (1802f5680) gives a non-map mesh whose material has a signature a fourth
    //   layout of the standard streams the signature leaves unread (position, texcoord, color, normal,
    //   blendweight(s), blendindices; three more conditions there are not read yet), which Valve writes as an
    //   extra vertex buffer no draw binds (dkr_remix skybox_bottom). Tangent is left out of the test: it is
    //   packed into the normal and ladder1, whose signature names no tangent, has no fourth buffer.
    // - The draw-set match (1802f69f0) compares all four layouts, the depth ones (built whenever the material
    //   has a depth list) included; the port matches on the main layout, so draws whose materials' depth lists
    //   differ are refused. How texcoord precision and the SNORM compression enter the match is open too
    //   (docs/MODELS.md, open questions): draws whose texcoords differ in kind are refused.
    private static void RefuseUnported(IReadOnlyList<DmxModelMeshes.Mesh> meshes, Func<string, WorldNodeModels.MaterialFacts> material)
    {
        string[] standard = ["position", "texcoord", "color", "normal", "blendweight", "blendweights", "blendindices"];
        foreach (var m in meshes)
        {
            var signature = material(m.Material).Signature;
            if (signature.Count == 0)
                continue;
            var read = MaterialStreams.Inputs(signature.Select(x => x.Semantic));
            foreach (var s in m.Entry.Facts.Streams)
                if (standard.Any(n => Io.Tier0Strings.EqualsIgnoreCase(n, s.Name))
                    && !read.Any(r => Io.Tier0Strings.EqualsIgnoreCase(r.Name, s.Name) && r.Index == s.Index))
                    throw new NotSupportedException($"{m.Material} reads no {s.Name} {s.Index}; the fourth layout (1802f5680) is not ported yet.");
        }
        if (meshes.Select(m => m.Material).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            return;
        var depths = meshes.Select(m => string.Join(',', material(m.Material).Depth.Select(x => x.Semantic))).Distinct(StringComparer.Ordinal).Count();
        if (depths > 1)
            throw new NotSupportedException("Draws whose materials' depth lists differ; the draw-set match on the depth layouts (1802f69f0) is not ported yet.");
        // coaster_railing_01: parts past 16 and parts within 16 but past 1 share one set in Valve's output, while
        // atixref's blocklight shadows keep past-16 and within-1 parts apart; the port splits the first pair.
        var kinds = meshes.Select(m => TexcoordKind(m.Entry)).ToHashSet();
        if (kinds.Contains(0) && kinds.Contains(2))
            throw new NotSupportedException("Draws with texcoords past 16 beside draws within 16; how the draw-set match treats texcoord precision is open.");
    }

    // 0: some texcoord past +-16 (high precision); 1: every texcoord within [-1, 1]; 2: otherwise.
    private static int TexcoordKind(WorldNodeCompile.Entry e)
    {
        var offset = 0;
        var kind = 1;
        foreach (var s in e.Facts.Streams)
        {
            if (Io.Tier0Strings.EqualsIgnoreCase(s.Name, "texcoord"))
            {
                if (s.Precise != 0)
                    return 0;
                for (var v = 0; v + e.Stride <= e.Vertices.Length; v += e.Stride)
                    for (var k = 0; k < s.Count; k++)
                        if (MathF.Abs(e.Vertices[v + offset + k]) > 1f)
                            kind = 2;
            }
            offset += s.Count;
        }
        return kind;
    }

    // The arguments ModelCompile_ReadMeshArgs (1802f9cc0) and the compile read, none given on a
    // .vmdl compile: each recorded as a FloatArg whose fingerprint is its default's float bits
    // (GROUND_TRUTH, RED2 arguments), so meshlets_max_*'s 0xffffffff default is -1.0f. The map
    // builder hands them as IntArgs instead (WorldNodeModel.ArgumentList).
    private static readonly EditInfo.Argument[] Arguments =
    [
        EditInfo.OverrideInputData(),
        .. new[] { "create_pooled_vb", "embedded_aggregate_mesh", "embedded_map_mesh", "force_up_to_date_materials",
                   "generate_draw_bounds", "generate_meshlets", "keep_vertices", "preserve_tangents" }.Select(n => new EditInfo.Argument(n, "FloatArg")),
        new("mapbuilder_entity_classname", "StringArg"),
        new("meshlets_max_tri_count", "FloatArg", BitConverter.SingleToUInt32Bits(-1f), BitConverter.SingleToUInt32Bits(-1f)),
        new("meshlets_max_vertex_count", "FloatArg", BitConverter.SingleToUInt32Bits(-1f), BitConverter.SingleToUInt32Bits(-1f)),
    ];

    // RED2: the vmdl and every mesh file read (sorted by path), CompileModel's arguments at their
    // defaults, the node model's searchable counts with this compile's IsChildResource 0 and the
    // root node's archetype and associated entity.
    private static KVObject Red2(WorldNodeModel model, List<EditInfo.Input> inputs, KVObject root)
    {
        var tree = WorldNodeModelTrees.Red2(model);
        var result = KVObject.Collection();
        foreach (var (key, value) in tree.Children)
        {
            if (key == "m_InputDependencies")
                result.Add(key, EditInfo.Inputs(EditInfo.ByPath(inputs)));
            else if (key == "m_ArgumentDependencies")
                result.Add(key, EditInfo.Arguments(Arguments));
            else if (key == "m_SearchableUserData")
            {
                var user = KVObject.Collection();
                foreach (var (k, v) in value.Children)
                    user.Add(k, k switch
                    {
                        "IsChildResource" => new KVObject(0),
                        "model_archetype_id" => new KVObject(Str(root, "model_archetype") ?? ""),
                        "model_primary_associated_entity" => new KVObject(Str(root, "primary_associated_entity") ?? ""),
                        _ => v,
                    });
                result.Add(key, user);
            }
            else
                result.Add(key, value);
        }
        return result;
    }

    private static void Check(KVObject root)
    {
        foreach (var n in Nodes(root))
            if (!Ported.Contains(Class(n)))
                throw new NotSupportedException($"ModelDoc node {Class(n)} is not ported yet.");
        foreach (var n in Nodes(root).Where(n => Class(n) == "RenderMeshFile"))
        {
            var filter = n.GetSubCollection("import_filter");
            if (filter != null && (filter.GetBooleanProperty("exclude_by_default") || filter.GetArray("exception_list") is { Count: > 0 }))
                throw new NotSupportedException("RenderMeshFile import_filter is not ported yet.");
            foreach (var (k, _) in n.Children)
                if (k is not ("_class" or "name" or "filename" or "import_scale" or "import_filter" or "children"))
                    throw new NotSupportedException($"RenderMeshFile option {k} is not ported yet.");
        }
    }

    private static IEnumerable<KVObject> Nodes(KVObject node)
    {
        yield return node;
        if (node.GetArray("children") is { } children)
            foreach (var c in children)
                foreach (var n in Nodes(c))
                    yield return n;
    }

    private static string Class(KVObject node) => Str(node, "_class") ?? "";

    private static string? Str(KVObject node, string key) => node.GetStringProperty(key);
}
