using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The vector and name helpers the model builder shares with the map compiler. Moved here unchanged from
/// NodeMeshEntries, MapMeshes, TJunctionFix and SettleWorld, whose methods of the same names now forward to
/// these, so both compiles run the same code.
/// </summary>
public static class MeshVectors
{
    private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    /// <summary>Matrix3x4_Rotate (18125d1b0): (x r0 + y r1) + z r2, the translation left out.</summary>
    public static Vector3 Rotate(float[] m, Vector3 d)
        => new(m[0] * d.X + m[1] * d.Y + m[2] * d.Z, m[4] * d.X + m[5] * d.Y + m[6] * d.Z, m[8] * d.X + m[9] * d.Y + m[10] * d.Z);

    /// <summary>180d46590 on its own with the identity: a direction as DmeMeshToCMesh copies it into a CMesh.</summary>
    public static Vector3 Copy(Vector3 d) => Normalise(Rotate(Identity, d));

    /// <summary>
    /// 180d46590's renormalisation: squares summed z, y, x; between 1e-17 and
    /// 1e17 each component times the reciprocal of the root; a zero length
    /// gives +0 components, smaller or larger lengths go through
    /// VectorNormalize_Slow.
    /// </summary>
    public static Vector3 Normalise(Vector3 d)
    {
        var len = MathF.Sqrt(d.Z * d.Z + d.Y * d.Y + d.X * d.X);
        if (len == 0f)
            return Vector3.Zero;
        if (len < 1e-17f || 1e17f < len)
            return VectorNormalizeSlow.Normalise(d);
        var inv = 1f / len;
        return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
    }

    /// <summary>FUN_18013e340: squares summed y, z, x; outside 1e-17 to 1e17 the double path, zero stays zero.</summary>
    public static Vector3 NormaliseYzx(Vector3 d)
    {
        var len = MathF.Sqrt(d.Y * d.Y + d.Z * d.Z + d.X * d.X);
        if (1e-17f <= len && len <= 1e17f)
        {
            var inv = 1f / len;
            return new Vector3(inv * d.X, inv * d.Y, inv * d.Z);
        }
        if (len == 0f)
            return Vector3.Zero;
        double x = d.X, y = d.Y, z = d.Z;
        var l = Math.Sqrt(y * y + x * x + z * z);
        return new Vector3((float)(x / l), (float)(y / l), (float)(z / l));
    }

    // A point through a matrix: rc's Matrix3x4_TransformPointXmm 1812651a0
    // packs (x, y, z), 18125d1f0 blends in w = 1.0 (183012c70) and, per row,
    // adds the products pairwise: (t + y m1) + (x m0 + z m2) (rc 0923 disasm;
    // HammerMesh_TransformToWorld 1810c4e50 and HalfEdge_FacePositionsTransformed
    // 18139b130 place mesh positions through it). Each add here is the same
    // pair, so the bits are Valve's (ledger 276).
    /// <summary>A point through a 3x4 matrix, adds paired as Matrix3x4_TransformPointXmm (1812651a0) pairs them.</summary>
    public static Vector3 TransformPoint(float[] m, Vector3 v)
        => new(((m[0] * v.X) + (m[2] * v.Z)) + ((m[1] * v.Y) + m[3]),
               ((m[4] * v.X) + (m[6] * v.Z)) + ((m[5] * v.Y) + m[7]),
               ((m[8] * v.X) + (m[10] * v.Z)) + ((m[9] * v.Y) + m[11]));

    /// <summary>
    /// The string token hash: MurmurHash2 with seed 0x31415926 after ASCII
    /// lowering, as resourcecompiler's 1800c7c10 inlines it (see
    /// <see cref="Io.ResourceNames.Hash"/>).
    /// </summary>
    public static uint NameHash(string name)
    {
        var bytes = Io.ResourceNames.Terminated(name);
        return Io.ResourceNames.Hash(bytes, bytes.Length - 1, 0x31415926);
    }

    /// <summary>FUN_180fdeda0: any angle under 0.001 becomes 0, and a negative yaw gains 360 until it is not.</summary>
    public static Vector3 SetAngles(Vector3 a)
    {
        float pitch = MathF.Abs(a.X) < 0.001f ? 0f : a.X, yaw = MathF.Abs(a.Y) < 0.001f ? 0f : a.Y;
        var roll = MathF.Abs(a.Z) < 0.001f ? 0f : a.Z;
        while (yaw < 0f)
            yaw += 360f;
        return new Vector3(pitch, yaw, roll);
    }
}
