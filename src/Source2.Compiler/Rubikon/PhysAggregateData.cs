// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/PhysAggregateData.cs and RubikonPhysics/, onto this library's KV3 stack.
using System.Numerics;
using Source2.Compiler.KeyValues;
using ValveKeyValue;

namespace Source2.Compiler.Rubikon;

/// <summary>
/// A model's physics (<c>VPhysXAggregateData_t</c>): the PHYS block a CTRL's
/// <c>embedded_physics.phys_data_block</c> points at, or a <c>.vphys_c</c>'s DATA. A view over the
/// block's KV3 tree; nothing is copied until a property is read.
/// </summary>
public sealed class PhysAggregateData
{
    /// <summary>The tree this reads.</summary>
    public KVObject Data { get; }

    /// <summary>Read <paramref name="data"/> (the block's root).</summary>
    public PhysAggregateData(KVObject data) => Data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary><c>m_nFlags</c>.</summary>
    public int Flags => Data.GetInt32Property("m_nFlags");

    /// <summary><c>m_bindPose</c>: each part's bind pose, a 3x4 matrix as <see cref="KvExtensions.ToMatrix4x4(KVObject)"/> reads it.</summary>
    public Matrix4x4[] BindPose => _bindPose ??= [.. Data.GetArray("m_bindPose").Select(v => v.ToMatrix4x4())];

    /// <summary><c>m_parts</c>.</summary>
    public Part[] Parts => _parts ??= [.. Data.GetArray("m_parts").Select(p => new Part(p))];

    /// <summary><c>m_boneNames</c>.</summary>
    public string[] BoneNames => Data.GetArray<string>("m_boneNames");

    /// <summary><c>m_boneParents</c>.</summary>
    public uint[] BoneParents => Data.GetArray<uint>("m_boneParents");

    /// <summary><c>m_surfacePropertyHashes</c>.</summary>
    public uint[] SurfacePropertyHashes => _surfaceHashes ??= [.. Data.GetArray<object>("m_surfacePropertyHashes").Select(Convert.ToUInt32)];

    /// <summary><c>m_collisionAttributes</c>.</summary>
    public IReadOnlyList<KVObject> CollisionAttributes => _collisionAttributes ??= Data.GetArray("m_collisionAttributes");

    private Matrix4x4[]? _bindPose;
    private Part[]? _parts;
    private uint[]? _surfaceHashes;
    private IReadOnlyList<KVObject>? _collisionAttributes;

    /// <summary>The name of part <paramref name="partIndex"/>'s parent bone, or empty.</summary>
    public string GetParentBoneName(int partIndex)
    {
        var parents = BoneParents;
        var names = BoneNames;
        if (parents == null || names == null || partIndex < 0 || partIndex >= parents.Length)
            return string.Empty;
        var parent = parents[partIndex];
        return parent < names.Length ? names[parent] : string.Empty;
    }
}

/// <summary>One part of an aggregate (<c>VPhysXBodyPart_t</c>).</summary>
public struct Part
{
    /// <summary><c>m_nFlags</c>.</summary>
    public int Flags { get; set; }

    /// <summary><c>m_flMass</c>.</summary>
    public float Mass { get; set; }

    /// <summary><c>m_rnShape</c>.</summary>
    public Shape Shape { get; set; }

    /// <summary><c>m_nCollisionAttributeIndex</c>.</summary>
    public int CollisionAttributeIndex { get; set; }

    /// <summary>Read a part.</summary>
    public Part(KVObject data)
    {
        Flags = data.GetInt32Property("m_nFlags");
        Mass = data.GetFloatProperty("m_flMass");
        Shape = new Shape(data.GetSubCollection("m_rnShape"));
        CollisionAttributeIndex = data.GetInt32Property("m_nCollisionAttributeIndex");
    }
}

/// <summary>A part's shapes (<c>RnShapeDesc</c> lists).</summary>
public struct Shape
{
    /// <summary><c>m_spheres</c>.</summary>
    public SphereDescriptor[] Spheres { get; set; }

    /// <summary><c>m_capsules</c>.</summary>
    public CapsuleDescriptor[] Capsules { get; set; }

    /// <summary><c>m_hulls</c>.</summary>
    public HullDescriptor[] Hulls { get; set; }

    /// <summary><c>m_meshes</c>.</summary>
    public MeshDescriptor[] Meshes { get; set; }

    /// <summary><c>m_CollisionAttributeIndices</c>.</summary>
    public int[] CollisionAttributeIndices { get; set; }

    /// <summary>Read a shape list.</summary>
    public Shape(KVObject data)
    {
        Spheres = Load<SphereDescriptor, Shapes.Sphere>(data, "m_spheres");
        Capsules = Load<CapsuleDescriptor, Shapes.Capsule>(data, "m_capsules");
        Hulls = Load<HullDescriptor, Shapes.Hull>(data, "m_hulls");
        Meshes = Load<MeshDescriptor, Shapes.Mesh>(data, "m_meshes");
        CollisionAttributeIndices = [.. data.GetArray<object>("m_CollisionAttributeIndices")!.Select(Convert.ToInt32)];
    }

    private static TDescriptor[] Load<TDescriptor, TShape>(KVObject data, string name)
        where TDescriptor : ShapeDescriptor<TShape>, new()
        where TShape : struct
    {
        var items = data.GetArray(name);
        var array = new TDescriptor[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            array[i] = new TDescriptor();
            array[i].Read(items[i]);
        }
        return array;
    }
}

/// <summary>A shape with its collision attribute and surface property (<c>RnShapeDesc_t</c>).</summary>
public abstract class ShapeDescriptor<T> where T : struct
{
    /// <summary><c>m_nCollisionAttributeIndex</c>.</summary>
    public int CollisionAttributeIndex { get; set; }

    /// <summary><c>m_nSurfacePropertyIndex</c>.</summary>
    public int SurfacePropertyIndex { get; set; }

    /// <summary><c>m_UserFriendlyName</c>.</summary>
    public string? UserFriendlyName { get; set; }

    /// <summary>The shape (<c>m_Sphere</c>, <c>m_Capsule</c>, <c>m_Hull</c> or <c>m_Mesh</c>).</summary>
    public T Shape { get; set; }

    /// <summary>Read the descriptor and its shape, which sits under <c>m_</c> plus the shape type's name.</summary>
    public void Read(KVObject data)
    {
        CollisionAttributeIndex = data.GetInt32Property("m_nCollisionAttributeIndex");
        SurfacePropertyIndex = data.GetInt32Property("m_nSurfacePropertyIndex");
        UserFriendlyName = data.GetStringProperty("m_UserFriendlyName");
        var member = typeof(T).Name;
        var shape = data.GetSubCollection("m_" + member) ?? throw new InvalidDataException("No m_" + member + " in a " + member + " descriptor.");
        Shape = ReadShape(shape);
    }

    /// <summary>The shape from its tree.</summary>
    protected abstract T ReadShape(KVObject data);
}

/// <summary>A sphere's descriptor.</summary>
public sealed class SphereDescriptor : ShapeDescriptor<Shapes.Sphere>
{
    /// <inheritdoc/>
    protected override Shapes.Sphere ReadShape(KVObject data) => new(data);
}

/// <summary>A capsule's descriptor.</summary>
public sealed class CapsuleDescriptor : ShapeDescriptor<Shapes.Capsule>
{
    /// <inheritdoc/>
    protected override Shapes.Capsule ReadShape(KVObject data) => new(data);
}

/// <summary>A hull's descriptor.</summary>
public sealed class HullDescriptor : ShapeDescriptor<Shapes.Hull>
{
    /// <inheritdoc/>
    protected override Shapes.Hull ReadShape(KVObject data) => new(data);
}

/// <summary>A mesh's descriptor.</summary>
public sealed class MeshDescriptor : ShapeDescriptor<Shapes.Mesh>
{
    /// <inheritdoc/>
    protected override Shapes.Mesh ReadShape(KVObject data) => new(data);
}
