namespace Source2.Compiler.Maps;

/// <summary>A node mesh entry (0x238 bytes): its mesh and the facts the compile reads.</summary>
public sealed record WorldNodeEntry(string Material, int Stride, float[] Vertices, int[] Indices, WrbMeshEntry Facts)
{
    public int TriangleCount => Indices.Length / 3;
}
