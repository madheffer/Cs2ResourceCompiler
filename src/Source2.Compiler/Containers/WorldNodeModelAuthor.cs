using Source2.Compiler.Kv3;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// A world node's model (<c>worldnodes/n*_*.vmdl_c</c>): one embedded render
/// mesh, its buffers meshopt-encoded (<see cref="Meshopt.MeshoptEncoder"/>).
///
/// <para>Blocks in the order Valve writes them: the buffers (MVTX, MIDX) in
/// block index order, then MDAT (the CRenderMesh), CTRL (the embedded
/// mesh and its buffer descriptions), RERL (the materials), RED2 and DATA.
/// Resource version 1; KV3 blocks under the generic format with binary_auto's
/// compression (<see cref="AuthoredKv3.ChooseCompression"/>).</para>
/// </summary>
public static class WorldNodeModelAuthor
{
    /// <summary>The container's resource version.</summary>
    public const ushort ResourceVersion = 1;

    /// <summary>
    /// The container around the encoded buffers and the four trees. The
    /// buffers come in block index order (each buffer's <c>m_nBlockIndex</c>
    /// in CTRL): a draw set's vertex buffers, then its index buffer.
    /// </summary>
    public static byte[] Container(IReadOnlyList<(string Type, byte[] Bytes)> buffers, KVObject mdat, KVObject ctrl,
                                   IEnumerable<string> references, KVObject red2, KVObject data)
    {
        var generic = Kv3Formats.Get("generic");
        var file = new ResourceFile { Version = ResourceVersion };
        foreach (var (type, bytes) in buffers)
            file.Blocks.Add(new ResourceBlock(type, bytes));
        file.Blocks.Add(new ResourceBlock("MDAT", Kv3Binary.Write(mdat, generic)));
        file.Blocks.Add(new ResourceBlock("CTRL", Kv3Binary.Write(ctrl, generic)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Sorted by V_stricmp_fast of the name, as RERL's one writer (rc 18016c0e0) sorts them.
        var refs = EditInfo.Red2Sorted(references.Where(path => !string.IsNullOrEmpty(path) && seen.Add(path)), path => path)
            .Select(path => (Source2ResourceId.ForPath(path), path)).ToList();
        if (refs.Count > 0)
            file.Blocks.Add(new ResourceBlock("RERL", ResourceRefs.Write(refs, 0)));
        file.Blocks.Add(new ResourceBlock("RED2", Kv3Binary.Write(red2, generic)));
        file.Blocks.Add(new ResourceBlock("DATA", Kv3Binary.Write(data, generic)));
        return file.Write();
    }
}
