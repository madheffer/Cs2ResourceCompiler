using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Source2.Compiler;

/// <summary>
/// Writes a binary DMX file (encoding binary 9), the layout <see cref="DmxBinary"/> reads.
///
/// <para>Every attribute is written with the type code in <see cref="DmxBinary.Element.Types"/>;
/// one without a code is refused rather than guessed, since a value alone does not say
/// whether it is a vector3 or a qangle.</para>
///
/// <para>The string table is in first-use order: each element's type and name, in element
/// order, then each element's attribute names and scalar string values. Hammer reads any
/// order. Its own writer orders the table differently (a saved probe_classes.vmap starts
/// with the root's attribute names reversed); that order is not ported, so a file written
/// here equals a Hammer save as a graph, not byte for byte.</para>
/// </summary>
public static class DmxBinaryWriter
{
    /// <summary>
    /// Write a document: the root and every element it reaches, depth first in attribute
    /// order, then any element of <paramref name="extra"/> not reached. The root is element 0.
    /// </summary>
    public static byte[] Write(string format, int formatVersion, DmxBinary.Element root,
                               IReadOnlyList<DmxBinary.Element> prefix, IEnumerable<DmxBinary.Element>? extra = null)
    {
        var order = new List<DmxBinary.Element>();
        var seen = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DmxBinary.Element>();
        var pending = new List<DmxBinary.Element>();
        void Visit(DmxBinary.Element start)
        {
            if (!seen.Add(start))
                return;
            stack.Push(start);
            // Depth first, children in attribute order: pushed in reverse so the first pops first.
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                order.Add(e);
                pending.Clear();
                foreach (var value in e.Attributes.Values)
                {
                    if (value is DmxBinary.Element child)
                        pending.Add(child);
                    else if (value is object?[] array)
                        pending.AddRange(array.OfType<DmxBinary.Element>());
                }
                for (var i = pending.Count - 1; i >= 0; i--)
                    if (seen.Add(pending[i]))
                        stack.Push(pending[i]);
            }
        }
        Visit(root);
        foreach (var e in extra ?? [])
            Visit(e);
        return Write(format, formatVersion, order, prefix);
    }

    /// <summary>Write elements in the order given (element 0 is the root). An element
    /// reference to one outside the list is written as null.</summary>
    public static byte[] Write(string format, int formatVersion, IReadOnlyList<DmxBinary.Element> elements,
                               IReadOnlyList<DmxBinary.Element> prefix)
    {
        var index = new Dictionary<DmxBinary.Element, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < elements.Count; i++)
            index.TryAdd(elements[i], i);

        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Str(string s)
        {
            if (!stringIndex.TryGetValue(s, out var i))
            {
                i = strings.Count;
                strings.Add(s);
                stringIndex[s] = i;
            }
            return i;
        }
        foreach (var e in elements)
        {
            Str(e.Type);
            Str(e.Name);
        }
        foreach (var e in elements)
            foreach (var (name, value) in e.Attributes)
            {
                Str(name);
                if (TypeOf(e, name) == DmxBinary.AttrType.String)
                    Str(value as string ?? "");
            }

        var w = new Writer();
        w.Ascii($"<!-- dmx encoding binary 9 format {format} {formatVersion} -->\n");
        w.Byte(0);

        w.Count(prefix.Count);
        foreach (var p in prefix)
        {
            w.Count(p.Attributes.Count);
            foreach (var (name, value) in p.Attributes)
            {
                w.InlineString(name);
                var type = TypeOf(p, name);
                w.Byte(type);
                WriteValue(w, type, value, inlineStrings: true, null, null);
            }
        }

        w.Count(strings.Count);
        foreach (var s in strings)
            w.InlineString(s);

        w.Count(elements.Count);
        foreach (var e in elements)
        {
            w.Int(Str(e.Type));
            w.Int(Str(e.Name));
            w.Bytes(e.Id.ToByteArray());
        }
        foreach (var e in elements)
        {
            w.Count(e.Attributes.Count);
            foreach (var (name, value) in e.Attributes)
            {
                w.Int(Str(name));
                var type = TypeOf(e, name);
                w.Byte(type);
                WriteValue(w, type, value, inlineStrings: false, index, Str);
            }
        }
        return w.ToArray();
    }

    private static byte TypeOf(DmxBinary.Element e, string name)
        => e.Types.TryGetValue(name, out var t) && t != 0
            ? t
            : throw new InvalidOperationException($"{e.Type} '{e.Name}': attribute {name} has no DMX type.");

    private static void WriteValue(Writer w, byte type, object? value, bool inlineStrings,
                                   Dictionary<DmxBinary.Element, int>? index, Func<string, int>? str)
    {
        if (type > DmxBinary.ArrayTypeBase)
        {
            var scalar = (byte)(type - DmxBinary.ArrayTypeBase);
            var items = value as System.Collections.IList ?? Array.Empty<object?>();
            w.Count(items.Count);
            foreach (var item in items)
                // A string inside an array is inline in every section (DmxBinary).
                WriteScalar(w, scalar, item, inlineStrings: scalar == DmxBinary.AttrType.String, index, str);
            return;
        }
        WriteScalar(w, type, value, inlineStrings, index, str);
    }

    private static void WriteScalar(Writer w, byte type, object? value, bool inlineStrings,
                                    Dictionary<DmxBinary.Element, int>? index, Func<string, int>? str)
    {
        switch (type)
        {
            case DmxBinary.AttrType.Element:
                w.Int(value is DmxBinary.Element e && index is not null && index.TryGetValue(e, out var i) ? i : -1);
                break;
            case DmxBinary.AttrType.Int: w.Int(Convert.ToInt32(value ?? 0)); break;
            case DmxBinary.AttrType.Float: w.Float(Convert.ToSingle(value ?? 0f)); break;
            case DmxBinary.AttrType.Bool: w.Byte(value is true ? (byte)1 : (byte)0); break;
            case DmxBinary.AttrType.String:
                var s = value as string ?? "";
                if (inlineStrings || str is null)
                    w.InlineString(s);
                else
                    w.Int(str(s));
                break;
            case DmxBinary.AttrType.Binary:
                var blob = value as byte[] ?? [];
                w.Count(blob.Length);
                w.Bytes(blob);
                break;
            case DmxBinary.AttrType.Time:
                w.Int((int)Math.Round(((TimeSpan)(value ?? TimeSpan.Zero)).TotalSeconds * 10000.0));
                break;
            case DmxBinary.AttrType.Color:
                var c = value as byte[] ?? [255, 255, 255, 255];
                w.Bytes([c[0], c[1], c[2], c[3]]);
                break;
            case DmxBinary.AttrType.Vector2:
                var v2 = (Vector2)(value ?? Vector2.Zero);
                w.Float(v2.X); w.Float(v2.Y);
                break;
            case DmxBinary.AttrType.Vector3 or DmxBinary.AttrType.QAngle:
                var v3 = (Vector3)(value ?? Vector3.Zero);
                w.Float(v3.X); w.Float(v3.Y); w.Float(v3.Z);
                break;
            case DmxBinary.AttrType.Vector4:
                var v4 = (Vector4)(value ?? Vector4.Zero);
                w.Float(v4.X); w.Float(v4.Y); w.Float(v4.Z); w.Float(v4.W);
                break;
            case DmxBinary.AttrType.Quaternion:
                var q = (Quaternion)(value ?? Quaternion.Identity);
                w.Float(q.X); w.Float(q.Y); w.Float(q.Z); w.Float(q.W);
                break;
            case DmxBinary.AttrType.Matrix:
                var m = (Matrix4x4)(value ?? Matrix4x4.Identity);
                foreach (var f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                                          m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
                    w.Float(f);
                break;
            case DmxBinary.AttrType.UInt64: w.ULong(Convert.ToUInt64(value ?? 0UL)); break;
            case DmxBinary.AttrType.UInt8: w.Byte(Convert.ToByte(value ?? (byte)0)); break;
            default: throw new InvalidOperationException($"DMX attribute type {type} cannot be written.");
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _out = new();
        private readonly byte[] _scratch = new byte[8];

        public void Byte(byte b) => _out.WriteByte(b);
        public void Bytes(ReadOnlySpan<byte> b) => _out.Write(b);
        public void Ascii(string s) => Bytes(Encoding.ASCII.GetBytes(s));
        public void InlineString(string s) { Bytes(Encoding.UTF8.GetBytes(s)); Byte(0); }
        public void Int(int v) { BinaryPrimitives.WriteInt32LittleEndian(_scratch, v); Bytes(_scratch.AsSpan(0, 4)); }
        public void Count(int v) { BinaryPrimitives.WriteUInt32LittleEndian(_scratch, (uint)v); Bytes(_scratch.AsSpan(0, 4)); }
        public void Float(float v) { BinaryPrimitives.WriteSingleLittleEndian(_scratch, v); Bytes(_scratch.AsSpan(0, 4)); }
        public void ULong(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(_scratch, v); Bytes(_scratch.AsSpan(0, 8)); }
        public byte[] ToArray() => _out.ToArray();
    }
}
