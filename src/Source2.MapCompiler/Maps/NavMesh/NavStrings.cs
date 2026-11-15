using System.Text;

namespace Source2.Compiler.Maps.NavMesh;

internal static class NavStrings
{
    /// <summary>A NUL-terminated string at the reader's position, the reader left past the NUL.</summary>
    public static string ReadNullTerm(BinaryReader reader, Encoding encoding)
    {
        var bytes = new List<byte>();
        for (var b = reader.ReadByte(); b != 0; b = reader.ReadByte())
            bytes.Add(b);
        return encoding.GetString([.. bytes]);
    }
}
