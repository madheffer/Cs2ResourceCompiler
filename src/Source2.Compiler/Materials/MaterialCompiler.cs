using System.IO.Hashing;
using System.Numerics;
using SkiaSharp;
using Source2.Compiler.Compiling;
using Source2.Compiler.RcTexture;
using Source2.Compiler.Shaders;
using ValvePak;
using ValveKeyValue;
using Source2.Compiler.KeyValues;

namespace Source2.Compiler.Materials;

/// <summary>A content tree under a search path name (<c>csgo</c>, <c>core</c>), searched after the request's own.</summary>
public sealed record ContentMount(string SearchPath, IContentSource Content);

/// <summary>Settings for <see cref="MaterialCompiler"/>.</summary>
public sealed record MaterialCompileSettings
{
    /// <summary>The game's <c>shaders_pc_dir.vpk</c>.</summary>
    public required string ShaderPackage { get; init; }

    /// <summary>
    /// The shader package path RC records in RED2 (<c>vpk:&lt;this&gt;/csgo_complex.vfx</c>): the
    /// absolute path on the machine RC ran on, with backslashes. Null derives it from
    /// <see cref="ShaderPackage"/> (<c>_dir</c> dropped, separators as backslashes).
    /// </summary>
    public string? RecordedShaderPackage { get; init; }

    /// <summary>Content searched after the request's own, in order: RC's is the addon, then
    /// <c>csgo</c>, then <c>core</c>.</summary>
    public IReadOnlyList<ContentMount> Mounts { get; init; } = [];

    /// <summary>
    /// Compile the child textures too (the default). A child this compiler cannot write exactly
    /// fails the compile, rather than leave a material without its texture; false writes the
    /// material alone.
    /// </summary>
    public bool CompileChildren { get; init; } = true;

    /// <summary>Whose BC7 the children reproduce.</summary>
    public RcTexture.Bc7Vendor Bc7Vendor { get; init; } = RcTexture.Bc7Vendor.Amd;
}

/// <summary>
/// <c>.vmat</c> to <c>.vmat_c</c> and its child textures, as resourcecompiler writes them
/// (docs/MATERIALS.md). Anything not measured against RC is refused.
/// </summary>
public sealed class MaterialCompiler : IResourceCompiler
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> SourceExtensions { get; } = [".vmat"];

    /// <inheritdoc/>
    public string OutputPathFor(string sourcePath) => sourcePath + "_c";

    // Attribute value types that land in m_SearchableUserData.
    private const int AttrFloat = 1, AttrInt = 5, AttrBool = 9, AttrTexture = 14, AttrString = 41;

    /// <inheritdoc/>
    public CompileOutput Compile(CompileRequest request, ICollection<CompileDiagnostic> diagnostics)
    {
        var settings = request.Settings as MaterialCompileSettings ?? throw new InvalidOperationException(
            "A .vmat compile needs MaterialCompileSettings: the shader package and the content mounts.");
        var content = request.Content ?? throw new InvalidOperationException($"{request.SourcePath} needs a content source for its images.");
        var mounts = new List<ContentMount> { new(request.SearchPath, content) };
        mounts.AddRange(settings.Mounts);

        var vmat = VmatSource.Parse(request.Source, request.SourcePath);
        var values = vmat.ByName;
        var shaders = new ShaderSet(settings.ShaderPackage, vmat.Shader);
        int Feature(int index) => MaterialTextures.FeatureValue(shaders.Features, index, values);

        var textures = MaterialTextures.Resolve(shaders.Features, shaders.Pixel, values,
            path => mounts.Any(m => m.Content.Read(path) is not null));

        // Every image the children read, found in the first mount that has it.
        var images = new SortedDictionary<string, (string SearchPath, byte[] Bytes)>(StringComparer.Ordinal);
        foreach (var input in textures.Where(t => t.Generated is not null).SelectMany(t => t.Generated!.Inputs))
            if (!images.ContainsKey(input.FileName))
                images[input.FileName] = Find(mounts, input.FileName)
                    ?? throw new InvalidOperationException($"{input.FileName} (for {request.SourcePath}) was not found in any content mount.");

        var searchable = Searchable(shaders, textures, images, Feature, values, vmat.Attributes);
        // 18010c660 adds RepresentativeTextureWidth/Height to DATA when it meets the attribute
        // named RepresentativeTexture (csgo_weapon has none: cx_weapon's m_intAttributes is empty).
        var data = BuildData(request.SourcePath, vmat, shaders, textures, images, searchable.ContainsKey("RepresentativeTexture"));
        var red2 = BuildRed2(request, settings, shaders, textures, images, mounts, searchable, vmat.Attributes);

        var container = new CompiledResource(new ResourceFile { Version = Source2ContainerAuthor.MaterialResourceVersion });
        container.SetKv3("RED2", red2, Kv3Formats.Get("generic"));
        container.SetKv3("DATA", data, Kv3Formats.Get("generic"));
        container.SetKv3("INSG", BuildInsg(shaders, values), Kv3Formats.Get("generic"));
        container.SetReferences(textures.Select(t => t.ResourcePath).Distinct().Order(StringComparer.Ordinal));

        var children = new List<CompiledFile>();
        if (settings.CompileChildren)
            foreach (var g in textures.Where(t => t.Generated is not null).Select(t => t.Generated!).DistinctBy(g => g.ChildPath))
                children.Add(new(g.ChildPath + "_c", ChildTextureCompiler.Compile(g, path => Find(mounts, path), request.SearchPath, settings.Bc7Vendor)));
        return new(container.Write(), children);
    }

    internal static (string SearchPath, byte[] Bytes)? Find(IEnumerable<ContentMount> mounts, string path)
    {
        foreach (var m in mounts)
            if (m.Content.Read(path) is { } bytes)
                return (m.SearchPath, bytes);
        return null;
    }

    private static KVObject BuildData(string materialPath, VmatSource vmat, ShaderSet shaders,
        IReadOnlyList<MaterialTexture> textures, IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images,
        bool representative)
    {
        // Params as the .vmat sets them, typed as RC types them (VmatSource: tier0 gives each KV1
        // value a KV3 type from its text, 18010f480 files ints, doubles and '['-strings), so RC
        // keeps names the shader does not declare (cx_order's F_SPECULAR, g_flFadeExponent).
        // Every list is sorted by name with V_stricmp_fast (1801156e0 -> 180115f60).
        var ints = new List<(string Name, int Value)>();
        var floats = new List<(string Name, float Value)>();
        var vectors = new List<(string Name, float[] Value)>();
        foreach (var (key, value) in vmat.Values)
        {
            var typed = VmatSource.TypeOf(value, materialPath, key);
            switch (typed.Kind)
            {
                case VmatSource.Kv1Kind.Int:
                    ints.Add((key, IntThroughFloat(typed.Int, materialPath, key)));
                    break;
                case VmatSource.Kv1Kind.Double:
                    floats.Add((key, (float)typed.Double));
                    break;
                case VmatSource.Kv1Kind.String when value.Length == 0 || value[0] == '[':
                    vectors.Add((key, VmatSource.ParseVector(value, materialPath, key)));
                    break;
            }
        }

        // The attribute tables (VmatSource.Attributes); the representative size joins the ints
        // (18010c660 adds it to the same list before the sort).
        var intAttributes = new List<(string Name, int Value)>();
        var floatAttributes = new List<(string Name, float Value)>();
        var vectorAttributes = new List<(string Name, float[] Value)>();
        var stringAttributes = new List<(string Name, string Value)>();
        foreach (var a in vmat.Attributes)
        {
            switch (a.Kind)
            {
                case VmatAttributeKind.Int:
                    intAttributes.Add((a.Name, IntThroughFloat(VmatSource.TypeOf(a.Text, materialPath, a.Name).Int, materialPath, a.Name)));
                    break;
                case VmatAttributeKind.Float:
                    floatAttributes.Add((a.Name, AttributeFloat(a.Text, materialPath, a.Name)));
                    break;
                case VmatAttributeKind.Vector:
                    vectorAttributes.Add((a.Name, VmatSource.ParseVector(a.Text, materialPath, a.Name)));
                    break;
                case VmatAttributeKind.String:
                    stringAttributes.Add((a.Name, a.Text));
                    break;
            }
        }
        if (representative)
        {
            var (w, h) = RepresentativeSize(textures, images);
            intAttributes.Add(("RepresentativeTextureHeight", h));
            intAttributes.Add(("RepresentativeTextureWidth", w));
        }

        var root = KVObject.Collection();
        root.Add("m_materialName", new KVObject(materialPath));
        root.Add("m_shaderName", new KVObject(vmat.Shader));
        root.Add("m_intParams", VmatParams.NamedArray(ByName(ints), "m_nValue", v => new KVObject(v)));
        root.Add("m_floatParams", VmatParams.NamedArray(ByName(floats), "m_flValue", v => new KVObject((double)v)));
        root.Add("m_vectorParams", VmatParams.NamedArray(ByName(vectors), "m_value", Vector));
        root.Add("m_textureParams", VmatParams.NamedArray(ByName(textures.Select(t => (t.Name, t.ResourcePath))), "m_pValue",
            v => new KVObject(v) { Flag = KVFlag.Resource }));
        root.Add("m_dynamicParams", KVObject.Array());
        root.Add("m_dynamicTextureParams", KVObject.Array());
        root.Add("m_intAttributes", VmatParams.NamedArray(ByName(intAttributes), "m_nValue", v => new KVObject(v)));
        root.Add("m_floatAttributes", VmatParams.NamedArray(ByName(floatAttributes), "m_flValue", v => new KVObject((double)v)));
        root.Add("m_vectorAttributes", VmatParams.NamedArray(ByName(vectorAttributes), "m_value", Vector));
        root.Add("m_textureAttributes", KVObject.Array());
        root.Add("m_stringAttributes", VmatParams.NamedArray(ByName(stringAttributes), "m_value", v => new KVObject(v)));
        root.Add("m_renderAttributesUsed", KVObject.Array());
        return PackageContainerAuthor.NarrowIntegers(root);
    }

    // The colour texture's first input image size; a .vtex named directly reads as 1x1 (cx_vtex).
    private static (int W, int H) RepresentativeSize(IReadOnlyList<MaterialTexture> textures,
        IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images)
    {
        var color = textures.FirstOrDefault(t => t.Name == "g_tColor")
            ?? throw new InvalidOperationException("The material has no g_tColor; its representative texture is not measured.");
        if (color.Generated is null)
            return (1, 1);
        // Read the way the children read their images, TGA included (a shader's default colour).
        var file = color.Generated.Inputs[0].FileName;
        var (_, w, h) = VtexCompiler.DecodeRgba(images[file].Bytes, file);
        return (w, h);
    }

    private static List<(string Name, T Value)> ByName<T>(IEnumerable<(string Name, T Value)> items) =>
        EditInfo.Red2Sorted(items, i => i.Name);

    private static KVObject Vector(float[] v)
    {
        var a = KVObject.Array();
        foreach (var f in v) a.Add(new KVObject((double)f));
        return a;
    }

    // 18010e280 keeps an int as (float)(int)value and DATA reads it back as an int; past 2^24 that
    // loses bits, and the read back is not ported, so such values are refused.
    private static int IntThroughFloat(long value, string path, string key) =>
        Math.Abs(value) <= 1 << 24 ? (int)value
            : throw new InvalidOperationException($"{path}: '{key}' = {value} is past 2^24; RC keeps ints as floats and that read back is not ported.");

    // A float attribute: a double as stored ((float) of the KV3 double), or an int of an attribute
    // table other than SystemAttributes (1800c7e20 of the KV3 int).
    private static float AttributeFloat(string text, string path, string key)
    {
        var typed = VmatSource.TypeOf(text, path, key);
        return typed.Kind == VmatSource.Kv1Kind.Int ? IntThroughFloat(typed.Int, path, key) : (float)typed.Double;
    }

    // The material's value for a shader expression's parameter: a vector variable reads a vector
    // value, any other an int or double one.
    private static float[] ParamValue(string value, bool vector, string path, string key)
    {
        if (vector)
            return VmatSource.ParseVector(value, path, key);
        var typed = VmatSource.TypeOf(value, path, key);
        return typed.Kind switch
        {
            VmatSource.Kv1Kind.Int => [IntThroughFloat(typed.Int, path, key), 0, 0, 0],
            VmatSource.Kv1Kind.Double => [(float)typed.Double, 0, 0, 0],
            _ => throw new InvalidOperationException($"{path}: '{key}' = \"{value}\" is not a number the shader can read."),
        };
    }

    private static KVObject BuildRed2(CompileRequest request, MaterialCompileSettings settings, ShaderSet shaders,
        IReadOnlyList<MaterialTexture> textures, IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images,
        IReadOnlyList<ContentMount> mounts, KVObject searchable, IReadOnlyList<VmatAttribute> attributes)
    {
        var root = KVObject.Collection();

        // Input dependencies: the .vmat, each image and each image's optional .txt (recorded in
        // the first mount when absent), by path.
        var inputs = new List<EditInfo.Input> { EditInfo.Input.Read(request.SourcePath, request.SearchPath, request.Source) };
        foreach (var (path, (searchPath, bytes)) in images)
        {
            inputs.Add(EditInfo.Input.Read(path, searchPath, bytes));
            inputs.Add(SettingsInput(mounts, path, request.SearchPath));
        }
        root.Add("m_InputDependencies", EditInfo.Inputs(EditInfo.ByPath(inputs)));
        root.Add("m_AdditionalInputDependencies", KVObject.Array());

        var args = new List<EditInfo.Argument>();
        if (images.Count > 0 || textures.Any(t => t.Generated is null))
            args.AddRange(EditInfo.TextureArguments(images.Keys));
        // A .vtex the material names directly is an image argument only (cx_vtex).
        foreach (var path in textures.Where(t => t.Generated is null).Select(t => t.ResourcePath).Distinct())
            args.Add(new(path, "ImageArg"));
        root.Add("m_ArgumentDependencies", EditInfo.Arguments(args));

        var special = new Dictionary<string, Source2ContainerAuthor.SpecialDep>(StringComparer.Ordinal);
        void Special(Source2ContainerAuthor.SpecialDep dep) => special[dep.Name] = dep;
        Special(Source2ContainerAuthor.MaterialBaseDeps[0]);
        var generated = textures.Where(t => t.Generated is not null).Select(t => t.Generated!).ToList();
        if (generated.Count > 0)
            foreach (var dep in Source2ContainerAuthor.TextureBaseDeps)
                Special(dep);
        foreach (var g in generated)
            foreach (var dep in RcTextureChannels.Deps(g))
                Special(dep);
        var recorded = settings.RecordedShaderPackage
            ?? settings.ShaderPackage.Replace("_dir.vpk", ".vpk", StringComparison.OrdinalIgnoreCase).Replace('/', '\\');
        Special(new($"vpk:{recorded}/{shaders.Name}", "CompileMaterial", shaders.Features.Version));
        foreach (var mode in shaders.Features.Modes)
            if (mode.Name == "Depth" && mode.Fallback.Length > 0)
                Special(new($"vpk:{recorded}/{mode.Fallback}", "CompileMaterial", shaders.Fallback(mode.Fallback).Features.Version));
        root.Add("m_SpecialDependencies", EditInfo.SpecialDependencies(special.Values));

        var children = KVObject.Array();
        foreach (var path in generated.Select(g => g.ChildPath).Distinct().Order(StringComparer.Ordinal))
            children.Add(new KVObject(path));
        EditInfo.AddTail(root, searchable, children, SurfacePropReferences(attributes));
        return PackageContainerAuthor.NarrowIntegers(root);
    }

    // 18010f480: each string attribute named PhysicsSurfaceProperties (V_stricmp_fast) is a
    // surface_prop subasset reference (context vf3 = AddSubassetReference 180175420, count + 1;
    // an empty name adds nothing). Null when there is none, as RC leaves the table.
    private static KVObject? SurfacePropReferences(IReadOnlyList<VmatAttribute> attributes)
    {
        var names = new List<string>();
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attributes)
        {
            if (a.Kind != VmatAttributeKind.String || a.Text.Length == 0
                || Io.Tier0Strings.StricmpFast(a.Name, "PhysicsSurfaceProperties") != 0)
                continue;
            if (counts.TryGetValue(a.Text, out var n))
                counts[a.Text] = n + 1;
            else
            {
                counts[a.Text] = 1;
                names.Add(a.Text);
            }
        }
        if (names.Count == 0)
            return null;
        var table = KVObject.Collection();
        foreach (var name in names)
            table.Add(name, new KVObject(counts[name]));
        var root = KVObject.Collection();
        root.Add("surface_prop", table);
        return root;
    }

    /// <summary>An image's <c>.txt</c> settings file as RC records it: read from the first mount that
    /// has it, else absent in <paramref name="absentSearchPath"/>.</summary>
    internal static EditInfo.Input SettingsInput(IEnumerable<ContentMount> mounts, string image, string absentSearchPath)
    {
        var txt = EditInfo.SettingsFile(image);
        return Find(mounts, txt) is { } found
            ? new EditInfo.Input(txt, found.SearchPath, System.IO.Hashing.Crc32.HashToUInt32(found.Bytes), true, true)
            : EditInfo.Input.Absent(txt, absentSearchPath);
    }

    // The shader's attributes for the material's combo in every program, evaluated, with the F_
    // values the material sets and RC's own keys; sorted ignoring case.
    private static KVObject Searchable(ShaderSet shaders, IReadOnlyList<MaterialTexture> textures,
        IReadOnlyDictionary<string, (string SearchPath, byte[] Bytes)> images, Func<int, int> feature,
        IReadOnlyDictionary<string, string> values, IReadOnlyList<VmatAttribute> attributes)
    {
        var entries = new Dictionary<string, KVObject>(StringComparer.Ordinal);
        var byHash = shaders.Features.Variables.GroupBy(v => ShaderExpression.ParamHash(v.Name)).ToDictionary(g => g.Key, g => g.First());
        Vector4 Param(uint hash)
        {
            if (!byHash.TryGetValue(hash, out var v))
                throw new InvalidOperationException($"A shader expression reads parameter {hash:X8}, which the shader does not declare.");
            if (values.TryGetValue(v.Name, out var s))
            {
                var f = ParamValue(s, v.Type == 3 || v.Type == 4, shaders.Name, v.Name);
                return new Vector4(f[0], f[1], f[2], f[3]);
            }
            return v.Type == ShaderVariable.Int
                ? new Vector4(v.IntDefault[0], v.IntDefault[1], v.IntDefault[2], v.IntDefault[3])
                : new Vector4(v.FloatDefault[0], v.FloatDefault[1], v.FloatDefault[2], v.FloatDefault[3]);
        }

        // The combo of every mode the shader runs itself (no fallback): the default one, and the
        // ones a mode fixes (unlit's Depth mode, S_MODE_DEPTH 1, adds DepthPassBatchID). Which
        // mode wins when two disagree on a value is not measured; the first one seen does here.
        // RC gathers the combo's attributes in 181fa2f30, walking every static combo the feature
        // combo leaves free; 181faa3b0 first pins the combos of each mode whose name contains
        // "Tool" (strstr, 18274ef4c), so ToolsVis' S_MODE_TOOLS_VIS 1 attributes
        // (lightmappedgeneric's environmentmapped) never count. That walk over the other free
        // combos and its rule checks are not ported: this takes each remaining mode's settings.
        var modeSettings = shaders.Features.Modes
            .Where(m => m.Fallback.Length == 0 && !m.Name.Contains("Tool", StringComparison.Ordinal))
            .Select(m => m.StaticComboSettings)
            .Prepend(new Dictionary<string, int>()).ToList();
        foreach (var program in shaders.Programs)
        foreach (var modeCombo in modeSettings)
        {
            long id = program.StaticComboId(feature, modeCombo);
            foreach (int index in program.AttributesOf(id) ?? [])
            {
                var a = program.Attributes[index];
                KVObject? value = a.Type switch
                {
                    AttrTexture => a.VariableBinding >= 0
                        ? textures.FirstOrDefault(t => t.Name == program.Variables[a.VariableBinding].Name) is { } t ? new KVObject(t.ResourcePath) : null
                        : null,
                    AttrBool or AttrInt or AttrFloat => Evaluate(a, feature, Param),
                    // A string attribute (type 41: csgo_weapon's MaterialLayerType "csgo_weapon"),
                    // its value as it is (cx_weapon, RC 11-03).
                    AttrString when a.Expression is null && a.Value is { ValueType: KVValueType.String } s => new KVObject((string)s),
                    _ => null,
                };
                if (value is not null)
                    entries.TryAdd(a.Name, value);
            }
        }
        // 18010f480 decodes the feature combo (181fac660) and hands every feature whose value is
        // not 0 to the searchable data (context vf18), set by the material or not.
        for (int i = 0; i < shaders.Features.StaticCombos.Count; i++)
        {
            var combo = shaders.Features.StaticCombos[i];
            int v = Math.Clamp(feature(i), combo.Min, combo.Max);
            if (v != 0)
                entries[combo.Name] = new KVObject(v);
        }
        entries["IsChildResource"] = new KVObject(0);
        entries["shader"] = new KVObject(shaders.Name);
        if (entries.ContainsKey("RepresentativeTexture"))
        {
            var (w, h) = RepresentativeSize(textures, images);
            entries["RepresentativeTextureHeight"] = new KVObject(h);
            entries["RepresentativeTextureWidth"] = new KVObject(w);
        }
        // 18010f480, after the shader's attributes, the feature values and "shader": every int
        // attribute (context vf18), then every float one (vf17), then every string one (vf19),
        // each setting its name; vector attributes go nowhere. A later write replaces an earlier
        // one: an int of an attributes table is a float attribute too and lands as the float
        // (s1i_attrs: S2cIntAttr = 3.0, RC 11-10).
        foreach (var kind in new[] { VmatAttributeKind.Int, VmatAttributeKind.Float, VmatAttributeKind.String })
            foreach (var a in attributes.Where(a => a.Kind == kind))
                entries[a.Name] = kind switch
                {
                    VmatAttributeKind.Int => new KVObject(IntThroughFloat(VmatSource.TypeOf(a.Text, shaders.Name, a.Name).Int, shaders.Name, a.Name)),
                    VmatAttributeKind.Float => new KVObject((double)AttributeFloat(a.Text, shaders.Name, a.Name)),
                    _ => new KVObject(a.Text),
                };

        var root = KVObject.Collection();
        // RC sorts the table by V_stricmp_fast at the RED2 finalize (EditInfo.Red2Sorted):
        // cx_alphatest's _dummyReference... attributes come before AllowBackfaceCulling.
        foreach (var (key, value) in EditInfo.Red2Sorted(entries, e => e.Key))
            root.Add(key, value);
        return root;
    }

    private static KVObject Evaluate(ShaderAttribute a, Func<int, int> feature, Func<uint, Vector4> param)
    {
        float x;
        if (a.Expression is { } code)
            x = ShaderExpression.Evaluate(code, feature, param).X;
        else if (a.Value is { } v)
            x = v.ValueType == KVValueType.Boolean ? ((bool)v ? 1 : 0) : (float)v;
        else
            throw new InvalidDataException($"Shader attribute {a.Name} has neither a value nor an expression.");
        return a.Type switch
        {
            AttrBool => new KVObject(x != 0 ? 1 : 0),
            AttrInt => new KVObject((int)x),
            _ => new KVObject((double)x),
        };
    }

    // m_elems: the union of the input signatures of the VS static combo, sorted by D3D semantic
    // name, index and element name; m_depth_elems the same from the Depth mode's fallback shader.
    private static KVObject BuildInsg(ShaderSet shaders, IReadOnlyDictionary<string, string> values)
    {
        var root = KVObject.Collection();
        root.Add("m_elems", Signature(shaders.Features, shaders.Vertex, values, null));
        if (shaders.Features.Modes.FirstOrDefault(m => m.Name == "Depth") is { } depth)
        {
            if (depth.Fallback.Length > 0)
            {
                var fallback = shaders.Fallback(depth.Fallback);
                root.Add("m_depth_elems", Signature(fallback.Features, fallback.Vertex, values, null));
            }
            else
                root.Add("m_depth_elems", Signature(shaders.Features, shaders.Vertex, values, depth.StaticComboSettings));
        }
        return PackageContainerAuthor.NarrowIntegers(root);
    }

    private static KVObject Signature(ShaderProgram features, ShaderProgram vertex, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, int>? modeSettings)
    {
        long id = vertex.StaticComboId(i => MaterialTextures.FeatureValue(features, i, values), modeSettings);
        var indices = vertex.InputSignaturesOf(id) ?? throw new InvalidOperationException($"The vertex shader has no static combo {id}.");
        return PackageMaterialAuthor.WriteSignature(indices.Distinct().SelectMany(i => vertex.InputSignatures[i]).Distinct()
            .OrderBy(e => e.D3DSemanticName, StringComparer.Ordinal).ThenBy(e => e.D3DSemanticIndex).ThenBy(e => e.Name, StringComparer.Ordinal));
    }

    /// <summary>A shader's programs from the shader package, read once per compile.</summary>
    private sealed class ShaderSet
    {
        private readonly string _package;
        private readonly Dictionary<string, ShaderSet> _fallbacks = [];

        public string Name { get; }
        public ShaderProgram Features { get; }
        public ShaderProgram Vertex { get; }
        public ShaderProgram Pixel { get; }
        public IReadOnlyList<ShaderProgram> Programs { get; }

        public ShaderSet(string package, string shader)
        {
            _package = package;
            Name = shader;
            using var pak = new Package();
            pak.Read(package);
            var stem = Path.GetFileNameWithoutExtension(shader);
            ShaderProgram? Load(string kind)
            {
                var entry = pak.FindEntry($"shaders/vfx/{stem}_pc_50_{kind}.vcs");
                if (entry is null)
                    return null;
                pak.ReadEntry(entry, out var bytes);
                return ShaderProgram.Read(bytes);
            }
            Features = Load("features") ?? throw new InvalidOperationException($"{shader} is not in {package}.");
            Vertex = Load("vs") ?? throw new InvalidOperationException($"{shader} has no vertex program.");
            Pixel = Load("ps") ?? throw new InvalidOperationException($"{shader} has no pixel program.");
            Programs = new[] { Vertex, Pixel, Load("psrs") }.OfType<ShaderProgram>().ToList();
        }

        public ShaderSet Fallback(string shader)
        {
            if (!_fallbacks.TryGetValue(shader, out var set))
                _fallbacks[shader] = set = new ShaderSet(_package, shader);
            return set;
        }
    }
}
