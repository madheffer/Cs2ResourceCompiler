using Source2.Compiler.KeyValues;
using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;

namespace Source2.Compiler;

/// <summary>
/// A brush entity's model with render meshes
/// (<c>maps/&lt;map&gt;/entities/&lt;name&gt;_&lt;node&gt;.vmdl_c</c>): per embedded
/// mesh its buffers and MDAT, then PHYS, CTRL (the meshes and
/// <c>embedded_physics</c>), RERL, RED2 and DATA (atixref's 58 such models).
/// RED2 is a node model's with the entity's arguments
/// (<see cref="WorldNodeModel.EntityClass"/>), the bounds and counts over
/// every mesh, and the physics model's shape counts and surface properties.
/// </summary>
public static class EntityModelAuthor
{
    /// <summary>One embedded mesh: its CTRL name, description and encoded buffers in block order.</summary>
    public sealed record Mesh(string Name, WorldNodeModel Model, IReadOnlyList<(string Type, byte[] Bytes)> Buffers, IReadOnlyList<string> Materials);

    public static KVObject Ctrl(IReadOnlyList<Mesh> meshes, int? physBlock)
    {
        var list = KVObject.Array();
        var block = 0;
        for (var i = 0; i < meshes.Count; i++)
        {
            list.Add(WorldNodeModelTrees.CtrlMesh(meshes[i].Model, meshes[i].Name, i, block));
            block += meshes[i].Buffers.Count + 1;
        }
        var root = KVObject.Collection();
        root.Add("embedded_meshes", list);
        if (physBlock is { } at)
        {
            var physics = KVObject.Collection();
            physics.Add("phys_data_block", new KVObject(at));
            root.Add("embedded_physics", physics);
        }
        return root;
    }

    /// <summary>
    /// The edit info: the first mesh's arguments and the render counts over
    /// all meshes, then the physics RED2's shape counts (and its default
    /// surface flag) and its surface properties.
    /// </summary>
    public static KVObject Red2(IReadOnlyList<Mesh> meshes, KVObject physicsRed2)
    {
        var red2 = WorldNodeModelTrees.Red2(meshes[0].Model);
        var user = red2["m_SearchableUserData"]!;
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        long triangles = 0, vertices = 0;
        foreach (var m in meshes)
        {
            foreach (var so in m.Model.SceneObjects)
                (min, max) = (Vector3.Min(min, so.Min), Vector3.Max(max, so.Max));
            triangles += m.Model.SceneObjects.Sum(so => so.Draws.Sum(d => d.IndexCount / 3));
            vertices += m.Model.VertexBuffers.Count > 0 ? m.Model.VertexBuffers[0].Count : 0;
        }
        var size = max - min;
        var physicsUser = physicsRed2["m_SearchableUserData"]!;
        var merged = KVObject.Collection();
        foreach (var (key, value) in user.Children)
        {
            KVObject Count(long n) => n is 0 or 1 ? new KVObject(n) : new KVObject((int)n);
            merged.Add(key, key switch
            {
                "bounds_longest" => new KVObject((double)MathF.Max(size.X, MathF.Max(size.Y, size.Z))),
                "bounds_x" => new KVObject((double)size.X),
                "bounds_y" => new KVObject((double)size.Y),
                "bounds_z" => new KVObject((double)size.Z),
                "model_lod0_triangle_count" or "model_total_triangle_count" => Count(triangles),
                "model_lod0_vertex_count" or "model_total_vertex_count" => Count(vertices),
                _ => value,
            });
            if (key == "generate_meshlets" && physicsUser.Children.FirstOrDefault(c => c.Key == "has_default_surface_property") is { Value: not null } flag)
                merged.Add(flag.Key, flag.Value);
        }
        foreach (var (key, value) in physicsUser.Children.Where(c => c.Key.StartsWith("physics_shape_", StringComparison.Ordinal)))
            merged.Add(key, value);
        var root = KVObject.Collection();
        foreach (var (key, value) in red2.Children)
            root.Add(key, key switch
            {
                "m_SearchableUserData" => merged,
                "m_SubassetReferences" => physicsRed2["m_SubassetReferences"]!,
                _ => value,
            });
        return root;
    }

    /// <summary>
    /// The model data: a node model's, with one mesh group mask, LOD mask and
    /// remapping table start (0) per mesh (ze_hold_em_p's end_breakable models).
    /// </summary>
    public static KVObject Data(WorldNodeModel first, int meshCount)
    {
        var data = WorldNodeModelTrees.Data(first);
        var root = KVObject.Collection();
        foreach (var (key, value) in data.Children)
        {
            if (key is "m_refMeshGroupMasks" or "m_refLODGroupMasks" or "m_remappingTableStarts")
            {
                var a = KVObject.Array();
                for (var i = 0; i < meshCount; i++)
                    a.Add(key == "m_refMeshGroupMasks" ? new KVObject(ulong.MaxValue) : key == "m_refLODGroupMasks" ? new KVObject(255u) : new KVObject(0u));
                root.Add(key, a);
            }
            else
                root.Add(key, value);
        }
        return root;
    }

    /// <param name="phys">The PHYS tree, or null for a model whose physics is empty: the model compile
    /// (rc 18032f06f) writes PHYS, and CTRL's embedded_physics (+0x420, 18032b3e0), only when the physics
    /// data is not empty (180c23d50 / 180c23ca0: no parts, joints or other lists); Mako's physicsType
    /// none brushes (func_rotating Fan1, the func_door Wall_Engine_*, func_brush Finale_Tp_Zm_Arrow).</param>
    public static byte[] Container(IReadOnlyList<Mesh> meshes, KVObject? phys, KVObject physicsRed2)
    {
        var generic = Kv3Formats.Get("generic");
        var file = new ResourceFile { Version = WorldNodeModelAuthor.ResourceVersion };
        foreach (var m in meshes)
        {
            foreach (var (type, bytes) in m.Buffers)
                file.Blocks.Add(new ResourceBlock(type, bytes));
            file.Blocks.Add(new ResourceBlock("MDAT", Kv3Binary.Write(WorldNodeModelTrees.Mdat(m.Model), generic)));
        }
        int? physBlock = phys is null ? null : file.Blocks.Count;
        if (phys is not null)
            file.Blocks.Add(new ResourceBlock("PHYS", Kv3Binary.Write(phys, generic)));
        file.Blocks.Add(new ResourceBlock("CTRL", Kv3Binary.Write(Ctrl(meshes, physBlock), generic)));
        if (ResourceRefs.Block(meshes.SelectMany(m => m.Materials)) is { } rerl)
            file.Blocks.Add(rerl);
        file.Blocks.Add(new ResourceBlock("RED2", Kv3Binary.Write(Red2(meshes, physicsRed2), generic)));
        file.Blocks.Add(new ResourceBlock("DATA", Kv3Binary.Write(Data(meshes[0].Model, meshes.Count), generic)));
        return file.Write();
    }
}
