// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/RubikonPhysics/Shapes/, onto this library's KV3 stack.
using System.Numerics;
using System.Runtime.InteropServices;
using Source2.Compiler.KeyValues;
using ValveKeyValue;

namespace Source2.Compiler.Rubikon.Shapes;

/// <summary>A sphere (<c>RnSphere_t</c>).</summary>
public struct Sphere
{
    /// <summary><c>m_vCenter</c>.</summary>
    public Vector3 Center { get; set; }

    /// <summary><c>m_flRadius</c>.</summary>
    public float Radius { get; set; }

    /// <summary>Read a sphere.</summary>
    public Sphere(KVObject data)
    {
        Center = data.GetSubCollection("m_vCenter").ToVector3();
        Radius = data.GetFloatProperty("m_flRadius");
    }
}

/// <summary>A capsule (<c>RnCapsule_t</c>): two centres and a radius.</summary>
public struct Capsule
{
    /// <summary><c>m_vCenter</c>, both ends.</summary>
    public Vector3[] Center { get; set; }

    /// <summary><c>m_flRadius</c>.</summary>
    public float Radius { get; set; }

    /// <summary>Read a capsule.</summary>
    public Capsule(KVObject data)
    {
        Center = [.. data.GetArray("m_vCenter").Select(v => v.ToVector3())];
        Radius = data.GetFloatProperty("m_flRadius");
    }
}

/// <summary>A convex hull (<c>RnHull_t</c>): vertices, half-edges, faces and planes, each either a KV3
/// array or the packed binary blob newer files carry.</summary>
public readonly struct Hull
{
    /// <summary>A face plane, as packed in <c>m_Planes</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Plane
    {
        /// <summary><c>m_vNormal</c>.</summary>
        public readonly Vector3 Normal;

        /// <summary><c>m_flOffset</c>.</summary>
        public readonly float Offset;

        /// <summary>Read a plane from its tree.</summary>
        public Plane(KVObject data)
        {
            Normal = data.GetSubCollection("m_vNormal").ToVector3();
            Offset = data.GetFloatProperty("m_flOffset");
        }
    }

    /// <summary>A half-edge, as packed in <c>m_Edges</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct HalfEdge
    {
        /// <summary><c>m_nNext</c>.</summary>
        public readonly byte Next;

        /// <summary><c>m_nTwin</c>.</summary>
        public readonly byte Twin;

        /// <summary><c>m_nOrigin</c>.</summary>
        public readonly byte Origin;

        /// <summary><c>m_nFace</c>.</summary>
        public readonly byte Face;

        /// <summary>Read a half-edge from its tree.</summary>
        public HalfEdge(KVObject data)
        {
            Next = data.GetByteProperty("m_nNext");
            Twin = data.GetByteProperty("m_nTwin");
            Origin = data.GetByteProperty("m_nOrigin");
            Face = data.GetByteProperty("m_nFace");
        }
    }

    /// <summary>A face (its first half-edge), as packed in <c>m_Faces</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Face
    {
        /// <summary><c>m_nEdge</c>.</summary>
        public readonly byte Edge;

        /// <summary>Read a face from its tree.</summary>
        public Face(KVObject data) => Edge = data.GetByteProperty("m_nEdge");
    }

    /// <summary>The hull's SVM region (<c>m_pRegionSVM</c>).</summary>
    public sealed class Region(KVObject data)
    {
        /// <summary>The region's tree.</summary>
        public KVObject Data { get; } = data;

        /// <summary>The region's planes.</summary>
        public ReadOnlySpan<Plane> GetPlanes() => Planes(Data);
    }

    /// <summary><c>m_vCentroid</c>.</summary>
    public Vector3 Centroid { get; }

    /// <summary><c>m_flMaxAngularRadius</c>.</summary>
    public float MaxAngularRadius { get; }

    /// <summary><c>m_pRegionSVM</c>, or null.</summary>
    public Region? RegionSVM { get; }

    /// <summary><c>m_vOrthographicAreas</c>.</summary>
    public Vector3 OrthographicAreas { get; }

    /// <summary><c>m_flVolume</c>.</summary>
    public float Volume { get; }

    /// <summary><c>m_Bounds.m_vMinBounds</c>.</summary>
    public Vector3 Min { get; }

    /// <summary><c>m_Bounds.m_vMaxBounds</c>.</summary>
    public Vector3 Max { get; }

    /// <summary>The hull's tree.</summary>
    public KVObject Data { get; }

    /// <summary>Read a hull.</summary>
    public Hull(KVObject data)
    {
        Centroid = data.GetSubCollection("m_vCentroid").ToVector3();
        MaxAngularRadius = data.GetFloatProperty("m_flMaxAngularRadius");
        OrthographicAreas = data.GetSubCollection("m_vOrthographicAreas").ToVector3();
        Volume = data.GetFloatProperty("m_flVolume");
        var bounds = data.GetSubCollection("m_Bounds");
        Min = bounds.GetSubCollection("m_vMinBounds").ToVector3();
        Max = bounds.GetSubCollection("m_vMaxBounds").ToVector3();
        var svm = data.GetSubCollection("m_pRegionSVM");
        RegionSVM = svm == null ? null : new Region(svm);
        Data = data;
    }

    // Files since 2023-11 keep positions in m_VertexPositions and per-vertex edge indices in m_Vertices.
    private static bool HasExplicitVertexIndices(KVObject data) => data.ContainsKey("m_VertexPositions");

    /// <summary>The per-vertex first edge (<c>m_Vertices</c> beside <c>m_VertexPositions</c>), or empty in older files.</summary>
    public Span<byte> GetVertices() => HasExplicitVertexIndices(Data) ? Data.GetArray<byte>("m_Vertices") : [];

    /// <summary>The vertex positions.</summary>
    public ReadOnlySpan<Vector3> GetVertexPositions() => ParseVertices(Data);

    /// <summary>The half-edges.</summary>
    public ReadOnlySpan<HalfEdge> GetEdges() => Data.IsNotBlobType("m_Edges")
        ? Data.GetArray("m_Edges").Select(e => new HalfEdge(e)).ToArray()
        : MemoryMarshal.Cast<byte, HalfEdge>(Data.GetArray<byte>("m_Edges"));

    /// <summary>The faces.</summary>
    public ReadOnlySpan<Face> GetFaces() => Data.IsNotBlobType("m_Faces")
        ? Data.GetArray("m_Faces").Select(e => new Face(e)).ToArray()
        : MemoryMarshal.Cast<byte, Face>(Data.GetArray<byte>("m_Faces"));

    /// <summary>The face planes.</summary>
    public ReadOnlySpan<Plane> GetPlanes() => Planes(Data);

    private static ReadOnlySpan<Plane> Planes(KVObject data) => data.IsNotBlobType("m_Planes")
        ? data.GetArray("m_Planes").Select(p => new Plane(p)).ToArray()
        : MemoryMarshal.Cast<byte, Plane>(data.GetArray<byte>("m_Planes"));

    internal static ReadOnlySpan<Vector3> ParseVertices(KVObject data)
    {
        if (data.IsNotBlobType("m_Vertices"))
            return data.GetArray("m_Vertices").Select(v => v.ToVector3()).ToArray();
        return MemoryMarshal.Cast<byte, Vector3>(data.GetArray<byte>(HasExplicitVertexIndices(data) ? "m_VertexPositions" : "m_Vertices"));
    }
}

/// <summary>A triangle mesh with its AABB tree (<c>RnMesh_t</c>).</summary>
public readonly struct Mesh
{
    /// <summary>A tree node's kind: a split on one axis, or a leaf.</summary>
    public enum NodeType
    {
        /// <summary>Split on x.</summary>
        SplitX = 0,

        /// <summary>Split on y.</summary>
        SplitY = 1,

        /// <summary>Split on z.</summary>
        SplitZ = 2,

        /// <summary>A leaf.</summary>
        Leaf = 3,
    }

    /// <summary>A tree node, as packed in <c>m_Nodes</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Node
    {
        /// <summary><c>m_vMin</c>.</summary>
        public readonly Vector3 Min;

        private readonly uint PackedTypeChildOffset;

        /// <summary>The top two bits of <c>m_nChildren</c>.</summary>
        public NodeType Type => (NodeType)(PackedTypeChildOffset >> 30);

        /// <summary>The low 30 bits of <c>m_nChildren</c>.</summary>
        public uint ChildOffset => PackedTypeChildOffset & 0x3FFFFFFF;

        /// <summary><c>m_vMax</c>.</summary>
        public readonly Vector3 Max;

        /// <summary><c>m_nTriangleOffset</c>.</summary>
        public readonly uint TriangleOffset;

        /// <summary>Read a node from its tree.</summary>
        public Node(KVObject data)
        {
            Min = data.GetSubCollection("m_vMin").ToVector3();
            Max = data.GetSubCollection("m_vMax").ToVector3();
            PackedTypeChildOffset = data.GetUInt32Property("m_nChildren");
            TriangleOffset = data.GetUInt32Property("m_nTriangleOffset");
        }

        /// <summary>A node from its fields.</summary>
        public Node(Vector3 min, Vector3 max, NodeType type, uint childOffset, uint triangleOffset)
        {
            Min = min;
            Max = max;
            PackedTypeChildOffset = ((uint)type << 30) | childOffset;
            TriangleOffset = triangleOffset;
        }
    }

    /// <summary>A triangle's three vertex indices, as packed in <c>m_Triangles</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Triangle
    {
        /// <summary>The first index.</summary>
        public readonly int X;

        /// <summary>The second index.</summary>
        public readonly int Y;

        /// <summary>The third index.</summary>
        public readonly int Z;

        /// <summary>Read a triangle from its tree (<c>m_nIndex</c>).</summary>
        public Triangle(KVObject data)
        {
            var indices = data.GetArray<object>("m_nIndex")!.Select(Convert.ToInt32).ToArray();
            if (indices.Length != 3)
                throw new InvalidDataException("A triangle has 3 indices, not " + indices.Length + ".");
            (X, Y, Z) = (indices[0], indices[1], indices[2]);
        }
    }

    /// <summary><c>m_vMin</c>.</summary>
    public Vector3 Min { get; }

    /// <summary><c>m_vMax</c>.</summary>
    public Vector3 Max { get; }

    /// <summary><c>m_Materials</c>: each triangle's surface index (a blob or an array; empty when absent).</summary>
    public int[] Materials { get; }

    /// <summary><c>m_vOrthographicAreas</c>.</summary>
    public Vector3 OrthographicAreas { get; }

    /// <summary>The mesh's tree.</summary>
    public KVObject Data { get; }

    /// <summary>Read a mesh.</summary>
    public Mesh(KVObject data)
    {
        Data = data;
        Min = data.GetSubCollection("m_vMin").ToVector3();
        Max = data.GetSubCollection("m_vMax").ToVector3();
        Materials = data.GetArray<object>("m_Materials") is { } materials ? [.. materials.Select(Convert.ToInt32)] : [];
        OrthographicAreas = data.GetSubCollection("m_vOrthographicAreas").ToVector3();
    }

    /// <summary>The vertex positions.</summary>
    public ReadOnlySpan<Vector3> GetVertices() => Hull.ParseVertices(Data);

    /// <summary>The AABB tree.</summary>
    public ReadOnlySpan<Node> ParseNodes() => Data.IsNotBlobType("m_Nodes")
        ? Data.GetArray("m_Nodes").Select(n => new Node(n)).ToArray()
        : MemoryMarshal.Cast<byte, Node>(Data.GetArray<byte>("m_Nodes"));

    /// <summary>The triangles.</summary>
    public ReadOnlySpan<Triangle> GetTriangles() => Data.IsNotBlobType("m_Triangles")
        ? Data.GetArray("m_Triangles").Select(t => new Triangle(t)).ToArray()
        : MemoryMarshal.Cast<byte, Triangle>(Data.GetArray<byte>("m_Triangles"));
}
