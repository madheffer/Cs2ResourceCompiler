using Source2.Compiler.KeyValues;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// A map's <c>world_physics.vmdl_c</c>: the world's collision as a model with
/// no meshes and one embedded physics aggregate.
///
/// <para>Measured on every Valve-compiled map at hand (32 local compiles,
/// resourcecompiler 0923): resource version 1, blocks in the order
/// <c>PHYS CTRL RED2 DATA</c>, every block KV3 v5 under the generic format.
/// Every block's compression is binary_auto's (<see cref="AuthoredKv3.ChooseCompression"/>):
/// CTRL (183 bytes) is raw, and PHYS turns Zstd past 0x80000 bytes.</para>
///
/// <para>Compressed bytes are encoder defined, so parity is the decoded tree
/// of each block plus the container facts (docs/RESOURCES.md).</para>
/// </summary>
public static class WorldPhysicsAuthor
{
    /// <summary>The container's resource version.</summary>
    public const ushort ResourceVersion = 1;

    /// <summary>The CTRL tree every world_physics carries: the model's physics is embedded block 0.</summary>
    public static KVObject Ctrl()
    {
        var embedded = KVObject.Collection();
        embedded.Add("phys_data_block", new KVObject(0L));
        var root = KVObject.Collection();
        root.Add("embedded_physics", embedded);
        return root;
    }

    /// <summary>
    /// The container around the three trees, typed as they are to be written.
    /// Without physics it is RED2 and DATA alone (a brush entity model with no
    /// shapes: atixref's func_water).
    /// </summary>
    public static byte[] Container(KVObject? phys, KVObject red2, KVObject data)
    {
        ArgumentNullException.ThrowIfNull(red2);
        ArgumentNullException.ThrowIfNull(data);
        var generic = Kv3Formats.Get("generic");
        var file = new ResourceFile { Version = ResourceVersion };
        if (phys != null)
        {
            file.Blocks.Add(new ResourceBlock("PHYS", Kv3Binary.Write(phys, generic)));
            file.Blocks.Add(new ResourceBlock("CTRL", Kv3Binary.Write(Ctrl(), generic)));
        }
        file.Blocks.Add(new ResourceBlock("RED2", Kv3Binary.Write(red2, generic)));
        file.Blocks.Add(new ResourceBlock("DATA", Kv3Binary.Write(data, generic)));
        // Zero padding between blocks, as Valve's files have it.
        return file.Write();
    }
}
